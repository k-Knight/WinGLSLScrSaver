@echo off
setlocal
cd /d "%~dp0"
echo === WinGLSLScrSaver build ===

rem Everything the build produces lives in publish\: the .scr, the default
rem shader and (when output is redirected) build_log.txt - the repo root
rem stays clean (publish/ is gitignored).

if not exist publish mkdir publish

rem Require the .NET SDK (only needed on the BUILD machine - the produced
rem .scr is fully self-contained and runs on any Windows 10/11 x64 PC).
where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet SDK not found. Install from https://dotnet.microsoft.com/download/dotnet/8.0
  exit /b 1
)

rem Fully self-contained single-file publish:
rem   --self-contained true                  bundles the .NET runtime itself
rem   -p:PublishSingleFile=true              one exe, no DLL sprawl
rem   -p:IncludeNativeLibrariesForSelfExtract=true  native libs inside the exe
rem   -p:EnableCompressionInSingleFile=true  compressed (~smaller file)
rem   -p:PublishTrimmed=false                WinForms does not support trimming
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

del publish\WinGLSLScrSaver.scr >nul 2>nul
ren publish\WinGLSLScrSaver.exe WinGLSLScrSaver.scr >nul
del publish\WinGLSLScrSaver.pdb >nul 2>nul
if exist shaders\shader_techno.fs copy /y shaders\shader_techno.fs publish\shader.fs >nul

echo.
echo Done. Portable pair in publish\:
dir publish\WinGLSLScrSaver.scr publish\shader.fs | findstr /i "WinGLSLScrSaver shader.fs"
echo Copy publish\WinGLSLScrSaver.scr to C:\Windows\System32 (or right-click it and
echo choose Install) - it runs on any Windows 10/11 x64 PC with nothing else installed.
endlocal
