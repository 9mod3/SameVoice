#Requires -Version 7.0
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$env:DOTNET_CLI_HOME = Join-Path $root ".build\dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_NOLOGO = "1"
$env:NUGET_PACKAGES = Join-Path $root ".build\nuget"
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

Write-Host "Building SameVoice.Core and tests..." -ForegroundColor Cyan
dotnet run --project "tests/SameVoice.Core.Tests/SameVoice.Core.Tests.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "Core tests failed." }

Write-Host "Publishing Windows app..." -ForegroundColor Cyan
dotnet publish "src/windows/SameVoice.App/SameVoice.App.csproj" `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false `
    -p:DebugType=None -p:DebugSymbols=false `
    -o "dist/windows"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "Done: dist/windows/SameVoice.exe" -ForegroundColor Green
