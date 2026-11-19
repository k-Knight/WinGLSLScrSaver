@echo off
setlocal
cd /d "%~dp0"
echo === WinGLSLScrSaver build ===

rem Everything the build produces lives in publish\ (gitignored).

if not exist publish mkdir publish

rem Require the .NET SDK (only needed on the BUILD machine - the produced
rem .scr is fully self-contained and runs on any Windows 10/11 x64 PC).
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0
  exit /b 1
)

rem Fully self-contained single-file publish (no DLL sprawl; WinForms does
rem not support trimming).
dotnet publish WinGLSLScrSaver.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:PublishTrimmed=false ^
  -o publish
if errorlevel 1 (
  echo ERROR: publish failed
  exit /b 1
)

rem R2 spec ?7: publish\ contains a single binary plus shader.fs - the exe is
rem RENAMED to .scr (no duplicated exe wasting space). For command-line
rem launches from PowerShell either launch from cmd (CreateProcess forwards
rem argv verbatim to .scr files) or make an exe-named copy on demand:
rem   copy WinGLSLScrSaver.scr WinGLSLScrSaver.exe
rem (The scrfile registry template fix - "%1" /S %* - also lets ShellExecute
rem pass .scr arguments through; see README.)
del publish\WinGLSLScrSaver.scr >nul 2>nul
ren publish\WinGLSLScrSaver.exe WinGLSLScrSaver.scr >nul
del publish\WinGLSLScrSaver.pdb >nul 2>nul
if exist shaders\shader_techno.fs copy /y shaders\shader_techno.fs publish\shader.fs >nul

rem Scintilla's native DLLs (Scintilla.dll / Lexilla.dll) can NOT be loaded
rem from inside the single-file bundle - the OS loader needs real files on
rem disk. They are therefore EMBEDDED in the .scr as resources and written to
rem an x64\ folder next to the executable at startup (NativeDeps.Ensure in
rem Program.Main). No deploy step needed here - the .scr is portable alone;
rem delete x64\ and it is recreated on the next launch.

echo.
echo Done. In publish\: WinGLSLScrSaver.scr (install) + shader.fs
rem (the .scr carries Scintilla's native DLLs embedded; it self-extracts x64\ on first run)
echo For CLI use from PowerShell: copy WinGLSLScrSaver.scr WinGLSLScrSaver.exe
echo or launch the .scr from cmd (argv passes through), e.g. -t file.fs
endlocal
