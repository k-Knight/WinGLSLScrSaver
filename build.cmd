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

rem Scintilla.NET does NOT load its native DLLs (Scintilla.dll / Lexilla.dll)
rem through the .NET single-file host - its type initializer probes the disk
rem for an x64\ subfolder next to the app. In a single-file bundle those
rem files never exist on disk, so we must deploy them beside the .scr or the
rem editor crashes with TypeInitializationException on startup.
if not exist publish\x64 mkdir publish\x64
copy /y bin\Release\net8.0-windows\win-x64\x64\Scintilla.dll publish\x64\ >nul
copy /y bin\Release\net8.0-windows\win-x64\x64\Lexilla.dll publish\x64\ >nul

echo.
echo Done. In publish\: WinGLSLScrSaver.scr (install) + shader.fs + x64\ (Scintilla native)
echo For CLI use from PowerShell: copy WinGLSLScrSaver.scr WinGLSLScrSaver.exe
echo or launch the .scr from cmd (argv passes through), e.g. -t file.fs
endlocal
