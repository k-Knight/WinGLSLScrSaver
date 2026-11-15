# WinGLSLScrSaver

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

The first window hosts the one Pie D3D11 device for the whole process and owns the shared canvas: a render target sized to the master resolution (max width and max height over all groups) times `res_mult`. Every other window is a raw window: a plain DXGI swapchain created directly on that same device. Separate devices cannot share textures, which is why everything runs on one.

`Program.cs` runs the loop: each frame the owner renders the shader into the canvas once, then every window blits its slice and presents. With `fps_cap = 0` the loop relies on vsync (`Present(1)`). With a cap set, the loop paces itself with a high resolution waitable timer (immune to the 15.6 ms sleep degradation of unfocused processes) and all presents use interval 0, since per-monitor presents are decoupled from the render rate.

## Shader handling

`Gl\ShaderAdapter.cs` adapts user GLSL before Pie sees it. It strips `#version`, `iResolution` / `iTime` uniforms and output declarations, then wraps the code in a header that declares those uniforms as a single UBO (`vec4 uResolutionTime`: xyz = resolution, w = time in seconds) plus the vertex input and fragment output. Shadertoy style shaders (a `mainImage` function, no `main`) get a `main` generated around them. If no shader file is found, an embedded default swirl shader is used.

## Render pipeline (normal runs)

Per frame:

1. **Pass A, once**: the shader renders into the shared canvas. The shader sees the canvas resolution (master x `res_mult`) as `iResolution`.
2. **Pass B, per window**: a crop-to-fit blit of that window's slice. The canvas is scaled uniformly to cover the group's area (centered, overflow cropped, never stretched or letterboxed) and the window samples the part of the canvas it shows. The owner blits through Pie itself (framebuffer null, so Pie handles backbuffer rotation and resize); raw windows use the raw HLSL blit in `CanvasBlit.cs`, which samples the canvas twice (bilinear + nearest) and mixes the two samples by `scale_blend`. That factor is baked into the blit shader source at startup as a GLSL float literal (which must always carry a decimal point).

So the shader runs once per frame no matter how many monitors there are, and `res_mult` is paid once instead of once per monitor. Diag modes 1 to 6 bypass this pipeline and use their own per-window paths; diag 7 runs it and additionally dumps each window's real backbuffer as a base64 JPEG into the app data folder, so output can be checked without screen capture.

One Pie quirk worth knowing: in 0.10.1, `UpdateBuffer` on a dynamic vertex buffer silently fails, so all vertex buffers here are created static.

## Shaders

On every start one shader is picked at random:

1. a random `.fs`, `.glsl` or `.frag` file from the folder set in `/c`
2. otherwise a random shader from `%localappdata%\WinGLSLScrSaver\`
3. otherwise an embedded default swirl shader

## Settings (`/c`)

All settings are stored in `%localappdata%\WinGLSLScrSaver\config.txt` as `key=value` lines. A config file that only holds a folder path (the old format) still works.

- `folder`: where shaders are picked from
- `fps_cap`: `0` = auto (vsync). When set, the loop runs at that rate (clamped to 1..max screen refresh, 120)
- `res_mult`: 0.05 to 1.0. Scales the shared canvas; the crop-to-fit blits stay at full screen resolution
- `scale_blend`: 0 = nearest, 1 = bilinear, values in between mix both

## Logs

Only DEBUG builds and `--diag` runs write anything; Release is fully silent.

- `%localappdata%\WinGLSLScrSaver\winglslscrsaver.log`
- `%localappdata%\WinGLSLScrSaver\ss_fragment_dump.fs`
- `%localappdata%\WinGLSLScrSaver\diag7_*.b64` (only in `--diag=7` runs)

## Source (`src\`)

| File | What it does |
|---|---|
| `Program.cs` | entry point, modes, render loop |
| `Core\Log.cs` | debug logging |
| `Core\Settings.cs` | config file |
| `Gl\Native.cs` | Win32 interop |
| `Gl\ScreenGroup.cs` | monitor grouping with the 1% merge tolerance |
| `Gl\ShaderAdapter.cs` | GLSL adaptation, embedded default shader |
| `Rendering\RenderWindow.cs` | canvas owner rendering, slice blits, diag paths |
| `Rendering\CanvasBlit.cs` | raw D3D11 helpers: extra swapchains, backbuffer RTVs, the HLSL slice blit |
| `UI\ConfigDialog.cs` | the `/c` dialog |

## Build and install

`build.cmd` needs the .NET 8 SDK. Output lands in `publish\`: `WinGLSLScrSaver.scr`, `shader.fs`, `build_log.txt`.

The `.scr` is self-contained (runtime and libraries bundled), so it runs on any Windows 10/11 x64 machine with nothing installed. To test, right-click it and choose Install, or copy it to `C:\Windows\System32`.
