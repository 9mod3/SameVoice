#Requires -RunAsAdministrator
# Adds Windows Defender Firewall rules for SameVoice on the Private network profile.
param(
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidate = Join-Path (Split-Path $PSScriptRoot -Parent) "dist/SameVoice-Windows-Portable/SameVoice.exe"
    if (Test-Path $candidate) { $ExePath = (Resolve-Path $candidate).Path }
}

Get-NetFirewallRule -DisplayName "SameVoice*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

$program = @{}
if ($ExePath -and (Test-Path $ExePath)) {
    $program["Program"] = $ExePath
    Write-Host "Restricting rules to $ExePath"
} else {
    Write-Host "SameVoice.exe not found; creating port-only rules for any app." -ForegroundColor Yellow
}

New-NetFirewallRule -DisplayName "SameVoice TCP control" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 47820-47829 -Profile Private @program | Out-Null
New-NetFirewallRule -DisplayName "SameVoice UDP discovery/audio" -Direction Inbound -Action Allow `
    -Protocol UDP -LocalPort 47821-47822 -Profile Private @program | Out-Null
Write-Host "Firewall rules added for the Private network profile." -ForegroundColor Green
Write-Host "If SameVoice is already running, restart it so new sockets use the rules."
