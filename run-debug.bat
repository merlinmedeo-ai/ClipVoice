@echo off
setlocal
cd /d "%~dp0"

echo ClipVoice portable debug launcher
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\ensure-dotnet.ps1"
if errorlevel 1 (
  echo.
  echo Failed to prepare portable .NET SDK.
  pause
  exit /b 1
)

set "DOTNET_ROOT=%~dp0.dotnet"
set "DOTNET_MULTILEVEL_LOOKUP=0"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
"%~dp0.dotnet\dotnet.exe" run --project "%~dp0ClipVoice.csproj" --configuration Debug

if errorlevel 1 pause
