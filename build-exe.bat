@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ==============================================
echo   ClipVoice Release Builder
echo ==============================================
echo.

if not exist "%~dp0.dotnet\dotnet.exe" (
    echo Portable .NET SDK is not ready. Preparing it now...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\ensure-dotnet.ps1"
    if errorlevel 1 goto :fail
)

set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "VERSION=1.0.0"
set "OUT=%~dp0dist\ClipVoice-%VERSION%-win-x64"

echo Building ClipVoice %VERSION% for Windows x64...
echo Output: %OUT%
echo.

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%" >nul 2>&1

"%~dp0.dotnet\dotnet.exe" publish "%~dp0ClipVoice.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -p:GenerateDocumentationFile=false ^
  -p:Version=%VERSION% ^
  -p:FileVersion=%VERSION%.0 ^
  -p:AssemblyVersion=%VERSION%.0 ^
  -o "%OUT%"

if errorlevel 1 goto :fail

if not exist "%OUT%\ClipVoice.exe" (
    echo.
    echo ERROR: publish finished but ClipVoice.exe was not created.
    goto :fail
)

for /r "%OUT%" %%F in (*.pdb) do del /q "%%F" >nul 2>&1

echo.
echo ==============================================
echo   READY: ClipVoice %VERSION%
echo ==============================================
echo.
echo EXE:
echo %OUT%\ClipVoice.exe
echo.
echo This build is self-contained: .NET does not need to be installed on the target PC.
echo.
explorer.exe "%OUT%"
exit /b 0

:fail
echo.
echo Build failed. Fix the error above and run build-exe.bat again.
pause
exit /b 1
