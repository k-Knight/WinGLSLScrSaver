WinGLSLScrSaver
===============

A multi-monitor OpenGL/GLSL (rendered via D3D11) screensaver for Windows,
written in C# / WinForms / Pie. Screens: /s (run), /c (config), /p (preview),
plus --diag modes for troubleshooting. All code is in src/Program.cs;
shaders are plain .fs files (shadertoy-style, adapted automatically).

Build (needs .NET 8 SDK on the build machine only):
    build.cmd

Output:
    WinGLSLScrSaver.scr  +  shader.fs

Both files are fully portable: the .scr embeds the .NET runtime, all managed
and native libraries (single file, compressed), so it runs on any Windows
10/11 x64 PC with nothing installed. The shader.fs next to it is the default
look (techno tunnel); users can pick any other .fs via the /c dialog.

Install for testing: right-click WinGLSLScrSaver.scr -> Install,
or copy it to C:\Windows\System32.
