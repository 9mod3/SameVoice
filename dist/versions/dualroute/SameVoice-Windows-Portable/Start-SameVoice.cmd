@echo off
setlocal
set "DOTNET_ROOT=%~dp0dotnet-runtime"
set "DOTNET_ROOT_X64=%~dp0dotnet-runtime"
if not exist "%DOTNET_ROOT%\host\fxr" (
  echo SameVoice portable runtime is missing.
  pause
  exit /b 1
)
start "" "%~dp0SameVoice.exe" %*
