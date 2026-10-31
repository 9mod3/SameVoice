#Requires -Version 7.0
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

& (Join-Path $PSScriptRoot "build-windows.ps1")
& (Join-Path $PSScriptRoot "build-android.ps1")

$bundle = Join-Path $root "dist/SameVoice-Windows-Portable"
Write-Host "Creating portable Windows bundle (built-in .NET 8 runtime)..." -ForegroundColor Cyan
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null
Copy-Item "dist/windows/*" $bundle -Recurse -Force
# Do not ship the unsigned apphost; launch through the Microsoft-signed
# dotnet host to avoid the "Windows protected your PC" SmartScreen prompt.
Remove-Item (Join-Path $bundle "SameVoice.exe") -Force -ErrorAction SilentlyContinue

$dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
$coreRoot = Join-Path $dotnetRoot "shared/Microsoft.NETCore.App"
$desktopRoot = Join-Path $dotnetRoot "shared/Microsoft.WindowsDesktop.App"
$fxrRoot = Join-Path $dotnetRoot "host/fxr"

$common = Get-ChildItem $coreRoot -Directory |
    Where-Object { $_.Name -like "8.*" -and (Test-Path (Join-Path $desktopRoot $_.Name)) } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if ($null -eq $common) {
    throw "No matching .NET 8 desktop runtime found. Install the .NET 8 Desktop Runtime, or use the framework-dependent dist/windows folder and require .NET 8."
}
$version = $common.Name
$fxr = Get-ChildItem $fxrRoot -Directory | Where-Object Name -eq $version | Select-Object -First 1
if ($null -eq $fxr) {
    $fxr = Get-ChildItem $fxrRoot -Directory | Where-Object { $_.Name -like "8.*" } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
}

$runtime = Join-Path $bundle "dotnet-runtime"
New-Item -ItemType Directory -Force `
    (Join-Path $runtime "shared/Microsoft.NETCore.App"), `
    (Join-Path $runtime "shared/Microsoft.WindowsDesktop.App"), `
    (Join-Path $runtime "host/fxr") | Out-Null
Copy-Item $common.FullName (Join-Path $runtime "shared/Microsoft.NETCore.App") -Recurse
Copy-Item (Join-Path $desktopRoot $version) (Join-Path $runtime "shared/Microsoft.WindowsDesktop.App") -Recurse
Copy-Item $fxr.FullName (Join-Path $runtime "host/fxr") -Recurse
Copy-Item (Join-Path $dotnetRoot "LICENSE.txt") (Join-Path $runtime "LICENSE.txt") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $dotnetRoot "ThirdPartyNotices.txt") (Join-Path $runtime "ThirdPartyNotices.txt") -ErrorAction SilentlyContinue
Copy-Item (Join-Path $dotnetRoot "dotnet.exe") (Join-Path $runtime "dotnet.exe") -Force

@"
@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath '%~dp0SameVoice.dll','%~dp0SameVoice.deps.json','%~dp0SameVoice.runtimeconfig.json','%~dp0dotnet-runtime\dotnet.exe' -ErrorAction SilentlyContinue" >nul 2>&1
set "DOTNET_ROOT=%~dp0dotnet-runtime"
set "DOTNET_ROOT_X64=%~dp0dotnet-runtime"
start "" "%~dp0dotnet-runtime\dotnet.exe" "%~dp0SameVoice.dll" %*
"@ | Set-Content (Join-Path $bundle "Start-SameVoice.cmd") -Encoding ASCII

Copy-Item (Join-Path $PSScriptRoot "portable-readme.txt") (Join-Path $bundle "README-Portable.txt") -Force
Copy-Item (Join-Path $PSScriptRoot "allow-firewall-portable.ps1") (Join-Path $bundle "Allow-Firewall.ps1") -Force

Write-Host "Creating ZIP archive..." -ForegroundColor Cyan
$zip = Join-Path $root "dist/SameVoice-Windows-Portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $bundle -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Done." -ForegroundColor Green
Get-ChildItem dist | Select-Object Name, Length
