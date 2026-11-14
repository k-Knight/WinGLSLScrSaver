# WinGLSLScrSaver

A multi-monitor GLSL screensaver for Windows. Renders shaders with D3D11, written in C# / WinForms.

Modes: `/s` (run), `/c` (config), `/p` (preview), plus `--diag` modes for troubleshooting.

## Technologies

- **C# / .NET 8**: published self-contained as a single `.scr`, so nothing has to be installed on the target machine
- **WinForms**: the borderless fullscreen windows and the config dialog
- **Pie** (0.10.1): thin graphics wrapper over D3D11. Shaders are fed in as GLSL source; Pie compiles them with shaderc (GLSL to SPIR-V), cross compiles the SPIR-V to HLSL (SPIRV-Cross) and creates the D3D11 shaders from that
- **Win32 interop** (`Gl\Native.cs`): DPI awareness, monitor refresh rates, cursor hiding, display keep-awake

## Architecture

One process, several windows. `/s` computes groups from the connected screens, then creates one `RenderWindow` (a borderless top-most `Form`) per group. Each window owns its own D3D11 device and swapchain, so every monitor renders at its own native resolution and refresh rate. Windows share nothing on the GPU; only settings and the picked shader are the same.

`Program.cs` runs the loop: for each window it calls `RenderFrame(time)`, then paces. With `fps_cap = 0` the loop relies on vsync (`Present(1)`, throttled by each monitor's refresh). With a cap set, the loop paces itself with a high-resolution waitable timer and presents without vsync.

## Shader handling

`Gl\ShaderAdapter.cs` adapts user GLSL before Pie sees it. It strips `#version`, `iResolution` / `iTime` uniforms and output declarations, then wraps the code in a header that declares those uniforms as a single UBO (`vec4 uResolutionTime`: xyz = resolution, w = time in seconds) plus the vertex input and fragment output. Shaders in Shadertoy style (a `mainImage` function, no `main`) get a `main` generated around them. If no shader file is found, an embedded default swirl shader is used.

## Render pipeline (per frame, per window)

A fullscreen quad is drawn with the user shader; the UBO carries resolution and time.

- **Auto mode** (`res_mult = 1.0`): one pass, straight to the backbuffer. The shader sees the monitor's real resolution, so it fills the screen edge to edge with no post-processing.
- **Scaled mode** (`res_mult < 1.0`): two passes. Pass A renders the shader into a per-monitor render target at client size x multiplier, so each monitor keeps its own aspect ratio and nothing is stretched. Pass B upscales that target to the screen with a blit shader that samples it twice, once through a bilinear sampler and once through a nearest sampler, and mixes the two samples by the `scale_blend` value. That factor is baked into the blit shader source at startup as a GLSL float literal (which must always carry a decimal point).

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
- `res_mult`: 0.05 to 1.0. Below 1.0 the shader renders into a smaller target that gets upscaled. Each monitor keeps its own aspect ratio
- `scale_blend`: 0 = nearest, 1 = bilinear, values in between mix both. Only used when `res_mult` is below 1.0

## Logs

Only DEBUG builds and `--diag` runs write anything; Release is fully silent.

- `%localappdata%\WinGLSLScrSaver\winglslscrsaver.log`
- `%localappdata%\WinGLSLScrSaver\ss_fragment_dump.fs`

## Source (`src\`)

| File | What it does |
|---|---|
| `Program.cs` | entry point, modes, monitor grouping, render loop |
| `Core\Log.cs` | debug logging |
| `Core\Settings.cs` | config file |
| `Gl\Native.cs` | Win32 interop |
| `Gl\ScreenGroup.cs` | monitor grouping, vertex layout |
| `Gl\ShaderAdapter.cs` | GLSL adaptation, embedded default shader |
| `Rendering\RenderWindow.cs` | per-window device, swapchain, rendering |
| `UI\ConfigDialog.cs` | the `/c` dialog |

## Build and install

`build.cmd` needs the .NET 8 SDK. Output lands in `publish\`: `WinGLSLScrSaver.scr`, `shader.fs`, `build_log.txt`.

The `.scr` is self-contained (runtime and libraries bundled), so it runs on any Windows 10/11 x64 machine with nothing installed. To test, right-click it and choose Install, or copy it to `C:\Windows\System32`.
