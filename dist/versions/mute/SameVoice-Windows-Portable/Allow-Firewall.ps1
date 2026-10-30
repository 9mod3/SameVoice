#Requires -RunAsAdministrator
# Allow SameVoice through Windows Defender Firewall on the Private profile.
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "SameVoice.exe"

Get-NetFirewallRule -DisplayName "SameVoice*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue

$program = @{}
if (Test-Path $exe) {
    $program["Program"] = $exe
    Write-Host "Restricting rules to $exe"
}

New-NetFirewallRule -DisplayName "SameVoice TCP control" -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 47820-47829 -Profile Private @program | Out-Null
New-NetFirewallRule -DisplayName "SameVoice UDP discovery/audio" -Direction Inbound -Action Allow `
    -Protocol UDP -LocalPort 47821-47822 -Profile Private @program | Out-Null
Write-Host "Firewall rules added for the Private network profile." -ForegroundColor Green
