# WinGLSLScrSaver

> **Agents: read `AGENT_HANDOFF.md` (repo root) first** - it documents the
> FPS/pacing investigation, the present-serialization root cause, the AMD
> keyed-mutex limitation, past crashes and the test workflow.

A multi-monitor GLSL screensaver for Windows. Renders shaders with D3D11, written in C# / WinForms.

Modes: `/s` (run), `/c` (config), `/p` (preview), plus `--diag` modes for troubleshooting.

## Technologies

- **C# / .NET 8**: published self-contained as a single `.scr`, so nothing has to be installed on the target machine
- **WinForms**: the borderless fullscreen windows and the config dialog
- **Pie** (0.10.1): thin graphics wrapper over D3D11. Shaders are fed in as GLSL source; Pie compiles them with shaderc (GLSL to SPIR-V), cross compiles the SPIR-V to HLSL (SPIRV-Cross) and creates the D3D11 shaders from that
- **Vortice.Direct3D11 / D3DCompiler**: raw D3D11 used directly for the extra swapchains and the HLSL slice blit of the shared canvas, because Pie 0.10.1 can only create one swapchain per device
- **Win32 interop** (`Gl\Native.cs`): DPI awareness, monitor refresh rates, cursor hiding, display keep-awake, and the high resolution waitable timer used for FPS capping

## Architecture

One process, one shared canvas. `/s` first computes groups from the connected screens: screens that tile a rectangle nearly perfectly (within 1% of the smaller screen's size, absorbing driver coordinate rounding and bezel compensation) merge into one group and are treated as a single monitor. One borderless top-most `RenderWindow` is created per group.

The first window hosts the one Pie D3D11 device for the whole process and owns the shared canvas: a render target sized to the master resolution (max width and max height over all groups) times `res_mult`. Every other window is a raw window: a plain flip-model DXGI swapchain (2+ buffers, `AllowTearing`) created directly on that same device. Separate devices cannot share textures, which is why everything runs on one.

## Per-monitor present pacing

The loop never blocks on any monitor's vblank. Each frame is split into a draw phase and a present phase:

1. **Draw phase**: the owner renders the shader into the shared canvas once, then every window whose monitor is *due* blits its slice. No present happens yet - a flip cannot retire before a vblank, and any GPU work queued behind a flip would stall for up to a whole vblank (measured at ~5.9 ms per frame).
2. **Present phase**: every window that drew now queues its flip with `Present(0, DO_NOT_WAIT | ALLOW_TEARING)` - never blocking the loop.

Each window paces itself to its own monitor's refresh rate (`EnumDisplaySettings`): a monitor that is not due yet simply keeps showing its previous frame, exactly like a native per-monitor vsync loop - so a 60 Hz portrait can never throttle a 170 Hz landscape and vice versa. The loop itself is paced by the high resolution waitable timer (immune to the 15.6 ms sleep degradation of unfocused processes) at the fastest monitor's refresh, or at `fps_cap` when set.

Known limit: presents from one D3D11 device serialize at vblank granularity - the second (and further) `Present` of a frame costs about one primary-vblank period, so with several monitors the loop runs below the fastest refresh even though no single monitor is the bottleneck. Removing that limit needs per-monitor present threads (draws stay on the main thread; only the flip queueing moves).

## Shader handling

`Gl\ShaderAdapter.cs` adapts user GLSL before Pie sees it. It strips `#version`, `iResolution` / `iTime` uniforms and output declarations, then wraps the code in a header that declares those uniforms as a single UBO (`vec4 uResolutionTime`: xyz = resolution, w = time in seconds) plus the vertex input and fragment output. Shadertoy style shaders (a `mainImage` function, no `main`) get a `main` generated around them. If no shader file is found, an embedded default swirl shader is used.

## Render pipeline (normal runs)

1. **Pass A, once per loop tick**: the shader renders into the shared canvas. The shader sees the canvas resolution (master x `res_mult`) as `iResolution`.
2. **Pass B, per due window**: a crop-to-fit blit of that window's slice. The canvas is scaled uniformly to cover the group's area (centered, overflow cropped, never stretched or letterboxed) and the window samples the part of the canvas it shows. The owner blits through Pie itself (framebuffer null, so Pie handles backbuffer rotation and resize); raw windows use the raw HLSL blit in `CanvasBlit.cs`, which samples the canvas twice (bilinear + nearest) and mixes the two samples by `scale_blend`. That factor is baked into the blit shader source at startup as a GLSL float literal (which must always carry a decimal point). Raw flip-model backbuffers have no stable RTV: `GetBuffer(0)` always returns the *current* back buffer in D3D11, so the RTV is re-created per frame.
3. **Present phase, per due window**: `Present(0, DO_NOT_WAIT | ALLOW_TEARING)` - non-blocking, per-monitor cadence.

So the shader runs once per loop tick no matter how many monitors there are, and `res_mult` is paid once instead of once per monitor. Diag modes 1 to 6 bypass this pipeline and use their own per-window paths; diag 7 runs the same pipeline and only turns verbose logging on.

One Pie quirk worth knowing: in 0.10.1, `UpdateBuffer` on a dynamic vertex buffer silently fails, so all vertex buffers here are created static.

## Shaders

On every start one shader is picked at random:

1. a random `.fs`, `.glsl` or `.frag` file from the folder set in `/c`
2. otherwise a random shader from `%localappdata%\WinGLSLScrSaver\`
3. otherwise an embedded default swirl shader

## Settings (`/c`)

All settings are stored in `%localappdata%\WinGLSLScrSaver\config.txt` as `key=value` lines. A config file that only holds a folder path (the old format) still works.

- `folder`: where shaders are picked from
- `fps_cap`: `0` = auto (pace at the fastest monitor's refresh). When set, the loop runs at that rate (clamped to 1..max screen refresh, 120)
- `res_mult`: 0.05 to 1.0. Scales the shared canvas; the crop-to-fit blits stay at full screen resolution
- `scale_blend`: 0 = nearest, 1 = bilinear, values in between mix both
- `show_fps`: `1` shows a small counter on each monitor. It counts frames actually handed to the display (post pacing) - not render ticks

## Logs

Only DEBUG builds and `--diag` runs write anything; Release is fully silent.

- `%localappdata%\WinGLSLScrSaver\winglslscrsaver.log`

In fullscreen canvas runs (`--diag=0`/`--diag=7`) the log also contains an `FPS:` line every ~2 s: the loop rate, per-window present rates, and where the frame budget went (pre/render/wait/present timings with min/max) - that is how the present-serialization limit above was measured.

## Source (`src\`)

| File | What it does |
|---|---|
| `Program.cs` | entry point, modes, window creation, shader pick |
| `Program.RunLoop.cs` | partial: render loop, pacing engine (grid/rel/pi), FPS stats |
| `Core\Log.cs` | debug logging |
| `Core\Settings.cs` | config file (every key documented in the header comment) |
| `Gl\Native.cs` | Win32 interop incl. the high-res waitable timer |
| `Gl\ScreenGroup.cs` | monitor grouping with the 1% merge tolerance |
| `Gl\ShaderAdapter.cs` | GLSL adaptation, embedded default shader |
| `Rendering\RenderWindow.cs` | partial: fields, lifecycle, InitGraphics, Render() dispatch |
| `Rendering\RenderWindow.Canvas.cs` | partial: shared-canvas draw/present path, pacing gating, fps overlay |
| `Rendering\RenderWindow.Diag.cs` | partial: diag 4/5/6 readback helpers |
| `Rendering\CanvasBlit.cs` | partial: single-device blit pipeline, swapchains, QI helpers |
| `Rendering\CanvasBlit.Lane.cs` | partial: present-lane machinery (present_lanes=2) with probe + fail-safe fallback |
| `UI\ConfigDialog.cs` | the `/c` dialog |

## Build and install

`build.cmd` needs the .NET 8 SDK. Output lands in `publish\`: `WinGLSLScrSaver.scr`, `shader.fs`, `build_log.txt`.

The `.scr` is self-contained (runtime and libraries bundled), so it runs on any Windows 10/11 x64 machine with nothing installed. To test, right-click it and choose Install, or copy it to `C:\Windows\System32`.
