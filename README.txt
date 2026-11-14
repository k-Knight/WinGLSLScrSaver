WinGLSLScrSaver
===============

A multi-monitor GLSL (rendered via D3D11 through Pie) screensaver for Windows,
written in C# / WinForms. Modes: /s (run), /c (config), /p (preview), plus
optional --diag modes for troubleshooting. All code is in src/Program.cs;
shaders are plain .fs files (shadertoy-style, adapted automatically).

Config and shaders live in %localappdata%\WinGLSLScrSaver\ :
    config.txt          the configured shader path (written by the /c dialog)
    <anything>.fs       shader used when config.txt is empty or the path is gone
If nothing is found there, an embedded default swirl shader is used.
The /c Browse dialog opens in the folder the .scr itself lives in.

Build (needs .NET 8 SDK on the build machine only):
    build.cmd

Output (all in publish\):
    WinGLSLScrSaver.scr  +  shader.fs  +  build_log.txt

The .scr is fully self-contained: the .NET runtime, all managed and native
libraries bundled into one compressed file, so it runs on any Windows 10/11
x64 PC with nothing installed. Release builds are silent - no log file and
no debug dump; logging is only enabled in DEBUG builds or with --diag=N.

Install for testing: right-click WinGLSLScrSaver.scr -> Install,
or copy it to C:\Windows\System32.
