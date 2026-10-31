@echo off
setlocal
cd /d "%~dp0"
rem Remove the "downloaded from Internet" mark from the files we launch.
rem Only touch specific files; scanning the whole runtime is slow.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0SameVoice.dll','%~dp0SameVoice.deps.json','%~dp0SameVoice.runtimeconfig.json','%~dp0dotnet-runtime\dotnet.exe' -ErrorAction SilentlyContinue" >nul 2>&1
set "DOTNET_ROOT=%~dp0dotnet-runtime"
set "DOTNET_ROOT_X64=%~dp0dotnet-runtime"
if not exist "%~dp0dotnet-runtime\dotnet.exe" (
  echo SameVoice portable runtime is missing dotnet.exe.
  pause
  exit /b 1
)
if not exist "%~dp0SameVoice.dll" (
  echo SameVoice.dll is missing.
  pause
  exit /b 1
)
start "" "%~dp0dotnet-runtime\dotnet.exe" "%~dp0SameVoice.dll" %*
