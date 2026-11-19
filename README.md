# WinGLSLScrSaver

> **Agents: read `AGENT_HANDOFF.md` (repo root) first** - pacing investigation,
> present-serialization root cause, AMD keyed-mutex limit, crash history, test workflow.

A multi-monitor GLSL screensaver for Windows. D3D11 via Pie, C# / WinForms.

## Modes

`/s` run, `/c` config, `/p <hwnd>` preview, `/e [path]` live shader editor, `/t [path]` shader smoke test (100x100 window, ~2 s; exit 0 = pass, 2 = fail; also warms the shader cache), `--diag:N` diagnostics (auto-exits after 6 s), `-h` / `/?` help. Windows style (`/s`, `/p12345`) and unix style (`-s`, `--editor file.fs`) mix freely; a bare existing `.fs`/`.glsl`/`.frag` path opens the editor.

**ShellExecute drops `.scr` arguments**: launching a `.scr` via double-click, `Start-Process` or PowerShell goes through the `HKCR\scrfile\shell\open\command` template `"%1" /S`, which has no placeholder for extra args - flags are silently dropped and it runs fullscreen. Workarounds:

- Launch from **cmd**, which runs it via `CreateProcess` and forwards argv verbatim: `WinGLSLScrSaver.scr -e file.fs`.
- **Registry fix** (admin): `reg add "HKCR\scrfile\shell\open\command" /ve /d "\"%1\" /S %*" /f` (`fix_screensaver_args.reg` is a ready merge file). Machine-wide; a feature update can reset it.
- **Exe-named copy**: `copy WinGLSLScrSaver.scr WinGLSLScrSaver.exe`, then `.\WinGLSLScrSaver.exe -e [file]`.

The `.scr` itself is for installing: double-click = run fullscreen, right-click = Install.

## Technologies

- **C# / .NET 8**, published self-contained as a single `.scr` (nothing to install)
- **WinForms** for the fullscreen windows and config dialog
- **Scintilla.NET** 5: the editor's code control (native styling, never scrolls on recolor)
- **Pie** 0.10.1: D3D11 wrapper; compiles GLSL with shaderc, cross-compiles SPIR-V to HLSL
- **Vortice.Direct3D11 / D3DCompiler**: raw D3D11 for the extra swapchains and the blit (Pie can only create one swapchain per device)
- **Win32 interop** (`Gl\Native.cs`): DPI awareness, refresh rates, cursor hiding, keep-awake, high-res waitable timer

## Architecture

Screens that tile a rectangle nearly perfectly (1% tolerance absorbs driver rounding and bezel compensation) merge into one group; one borderless top-most `RenderWindow` per group. Window 0 hosts the single Pie D3D11 device and the shared canvas: a render target at master resolution (max width/height over all groups) times `res_mult`. Every other window is a raw flip-model DXGI swapchain on that same device (separate devices cannot share textures).

## Present pacing

The loop never blocks on a vblank. Each tick: **draw** - the owner renders the shader into the shared canvas once, every due window blits its slice, nobody presents yet (GPU work queued behind a flip would stall up to a whole vblank); then **present** - every window that drew queues `Present(0, DO_NOT_WAIT | ALLOW_TEARING)`. Each window paces itself to its own monitor's refresh, so a 60 Hz portrait never throttles a 170 Hz landscape. The loop runs on the high-res waitable timer (immune to the 15.6 ms unfocused-process sleep degradation) at the fastest refresh, or `fps_cap`.

Known limit: presents from one device serialize at vblank granularity (~one primary-vblank per extra `Present`), so multi-monitor runs stay below the fastest refresh. Fixing that needs per-monitor present threads (present-lanes exist as an experiment, see `present_lanes`).

## Shader editor (`/e`)

Dark Scintilla editor with GLSL highlighting, line-number gutter and an embedded live preview (resizable splitter - the preview re-renders at the new size, never stretched). Status bar under the viewer: compile state + resolved textures, file path, Ln/Col. Window position/size and the splitter ratio persist across sessions (per-monitor DPI aware). Toolbar: Open Ctrl+O, Save Ctrl+S, Save As, Reload, Apply F5, Insert texture example. Edits recompile ~800 ms after the last keystroke; the preview keeps running the previous shader until the new one compiles; errors show in the status bar (double-click jumps to the line). Ctrl+S saves (Ctrl+S on an unsaved default opens Save As), redirects texture lookup to the file's folder and invalidates the shader cache.

External changes are watched: the file reloads automatically with scroll, selection and caret preserved; the editor's own saves never trigger a reload; unsaved edits are kept and reported. A save that does not change the source or path skips the recompile. Compiles run off the UI thread; the vsync flip runs on a present thread; resizes are applied on the UI thread only when the present thread is idle. UI-thread exceptions are logged (WinForms would otherwise exit silently).

## Shader handling

`Gl\ShaderAdapter.cs` strips `#version`, `iResolution` / `iTime`, `sampler2D` and output declarations, then wraps the code in a header declaring them as a single UBO (`vec4 uResolutionTime`: xyz = resolution, w = seconds) plus vertex input / fragment output. Shadertoy-style shaders (`mainImage`, no `main`) get a `main` generated; `iChannel0..3` are auto-declared, so pasted Shadertoy code works unchanged. No file found -> embedded default swirl shader.

**Shader cache** (on by default): the adapted fragment is compiled to SPIR-V once and cached in `%localappdata%\WinGLSLScrSaver\cache\`, keyed on the file's mtime + length; an untouched file never re-runs shaderc (Pie's SPIRV-Cross -> HLSL step still runs - not cacheable through its API). Any edit invalidates the entry; the editor never touches the cache; `shader_cache=0` opts out. A corrupt entry falls back to a full compile.

**Textures:** `uniform sampler2D name;` + `name.png` next to the shader (then shader folder, then app data dir), decoded flipped, bound at slots 1..N (0 = the UBO). Missing PNGs sample as black. See `shaders\matrix_atlas.fs` for a full example.

## Render pipeline

1. **Pass A, per tick**: shader renders into the shared canvas (`iResolution` = canvas size).
2. **Pass B, per due window**: crop-to-fit blit of its slice - canvas scaled uniformly to cover the group (centered, cropped, never stretched). The owner blits through Pie; raw windows use the raw HLSL blit in `CanvasBlit.cs` (bilinear + nearest samples mixed by `scale_blend`, baked into the shader source).
3. **Present phase, per due window**: non-blocking, per-monitor cadence.

The shader runs once per tick regardless of monitor count; `res_mult` is paid once. Diag modes 1-6 bypass this pipeline; diag 7 only turns on verbose logging.

Pie quirks worth knowing: `UpdateBuffer` on dynamic vertex buffers silently fails in 0.10.1 (all VBs are static); flip-model backbuffers have no stable RTV (`GetBuffer(0)` is always the *current* back buffer - RTV re-created per frame); `Debug=true` throws `SEHException` on `SetDepthStencilState`.

## Shaders

One shader picked at random at start: a random `.fs`/`.glsl`/`.frag` from the configured folder, else from `%localappdata%\WinGLSLScrSaver\`, else the embedded default.

## Settings (`/c`)

`%localappdata%\WinGLSLScrSaver\config.txt`, `key=value` lines (a bare folder path - the old format - still works): `folder`, `fps_cap` (0 = auto = fastest refresh), `res_mult` (0.05-1.0, scales the canvas), `scale_blend` (0 = nearest, 1 = bilinear), `show_fps`, `shader_cache`, plus tuning keys documented in the header comment of `src\Core\Settings.cs`.

## Logs

Silent in Release by default; Debug builds log verbosely; `--diag:N` forces verbose in any build. Log: `%localappdata%\WinGLSLScrSaver\winglslscrsaver.log`. Fullscreen canvas runs add an `FPS:` line every ~2 s (loop rate, per-window present rates, frame-budget timings) - that is how the present-serialization limit was measured.

## Source (`src\`)

| File | What it does |
|---|---|
| `Program.cs` | entry point, modes, window creation, shader pick |
| `Program.RunLoop.cs` | partial: render loop, pacing engine (grid/rel/pi), FPS stats |
| `Core\Log.cs` | debug logging |
| `Core\Settings.cs` | config file (every key documented in the header comment) |
| `Core\ShaderCache.cs` | compiled-SPIR-V cache keyed on shader file mtimes |
| `Core\NativeDeps.cs` | extracts the embedded Scintilla native DLLs to `x64\` next to the exe at startup |
| `Gl\Native.cs` | Win32 interop incl. the high-res waitable timer |
| `Gl\ScreenGroup.cs` | monitor grouping with the 1% merge tolerance |
| `Gl\ShaderAdapter.cs` | GLSL adaptation, texture declarations, embedded default shader |
| `Rendering\RenderWindow.cs` | partial: fields, lifecycle, InitGraphics, Render() dispatch, live recompile |
| `Rendering\RenderWindow.Canvas.cs` | partial: shared-canvas draw/present path, pacing gating, fps overlay |
| `Rendering\RenderWindow.Diag.cs` | partial: diag 4/5/6 readback helpers |
| `Rendering\CanvasBlit.cs` | partial: single-device blit pipeline, swapchains, QI helpers |
| `Rendering\CanvasBlit.Lane.cs` | partial: present-lane machinery (present_lanes=2) with probe + fail-safe fallback |
| `Rendering\ShaderTextures.cs` | PNG texture lookup, decode (flipped) and binding |
| `UI\ConfigDialog.cs` | the `/c` dialog |
| `UI\ShaderEditor.cs` | the `/e` live shader editor with embedded preview |

## Build and install

`build.cmd` (needs .NET 8 SDK) outputs `publish\WinGLSLScrSaver.scr` + `shader.fs`. Scintilla's native DLLs are embedded in the `.scr` and extracted to `x64\` next to it at first launch; deleting the folder just gets it recreated. For a PowerShell CLI workflow make an exe-named copy on demand or launch the `.scr` from cmd. Self-contained - runs on any Windows 10/11 x64 machine. Install: right-click -> Install, or copy to `%localappdata%\WinGLSLScrSaver` (best location - writable, so `x64\` can be extracted; System32 needs admin and is read-only for users).

