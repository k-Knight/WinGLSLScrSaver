WinGLSLScrSaver
===============

A multi-monitor GLSL (rendered via D3D11 through Pie) screensaver for Windows,
written in C# / WinForms. Modes: /s (run), /c (config), /p (preview), plus
optional --diag modes for troubleshooting.

How shaders are chosen (a fresh random pick on every start):
    1. A random .fs / .glsl / .frag file from the folder configured in /c
       (stored in %localappdata%\WinGLSLScrSaver\config.txt)
    2. If not configured or the folder is gone/empty: a random shader from
       %localappdata%\WinGLSLScrSaver\
    3. Otherwise an embedded default swirl shader.
The /c dialog edits the shader FOLDER (folder browser, defaults to
%localappdata%\WinGLSLScrSaver).

Logs and debug dumps (DEBUG builds and --diag runs only - Release is silent:
no log file, no dump, zero per-call file I/O):
    %localappdata%\WinGLSLScrSaver\winglslscrsaver.log
    %localappdata%\WinGLSLScrSaver\ss_fragment_dump.fs

Source layout (src\):
    Program.cs                  entry point, /s /p /c modes, multi-monitor
                                grouping, render loop
    Core\Log.cs                 debug logging (silent in Release)
    Core\Settings.cs            config file + local app data folder
    Gl\Native.cs                Win32 interop
    Gl\ScreenGroup.cs           monitor grouping + shared vertex layout
    Gl\ShaderAdapter.cs         GLSL -> HLSL adaptation, embedded default
    Rendering\RenderWindow.cs   per-window D3D11 device, swapchain, rendering
    UI\ConfigDialog.cs          /c settings dialog (shader folder)

Build (needs .NET 8 SDK on the build machine only):
    build.cmd

Output (all in publish\):
    WinGLSLScrSaver.scr  +  shader.fs  +  build_log.txt

The .scr is fully self-contained: the .NET runtime, all managed and native
libraries bundled into one compressed file, so it runs on any Windows 10/11
x64 PC with nothing installed.

Install for testing: right-click WinGLSLScrSaver.scr -> Install,
or copy it to C:\Windows\System32.
