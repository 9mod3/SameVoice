#requires -Version 5.1
<#
.SYNOPSIS
Checks whether the Scream virtual audio driver/device is installed on Windows.

This script is read-only. It does not install drivers and does not change audio
settings. Run it in a normal PowerShell window.
#>

$ErrorActionPreference = 'SilentlyContinue'
$found = New-Object System.Collections.Generic.List[string]

Write-Host 'SameVoice / Scream detection' -ForegroundColor Cyan
Write-Host '----------------------------'

$service = Get-Service -Name 'Scream' -ErrorAction SilentlyContinue
if ($service) {
    $found.Add("服务: $($service.Name) / $($service.Status)")
}

$devices = @()
try {
    $devices = @(Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -match 'Scream' -or $_.InstanceId -match 'Scream' })
} catch { }
foreach ($d in $devices) {
    $found.Add("音频端点: $($d.FriendlyName) [$($d.Status)]")
}

try {
    $soundDevices = @(Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match 'Scream' })
    foreach ($d in $soundDevices) {
        $found.Add("声音设备: $($d.Name)")
    }
} catch { }

$regPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\Scream'
if (Test-Path $regPath) {
    $found.Add("驱动服务注册表: $regPath")
    $options = Join-Path $regPath 'Options'
    if (Test-Path $options) {
        $props = Get-ItemProperty $options
        if ($props.UnicastIPv4) {
            $found.Add("Scream 单播目标: $($props.UnicastIPv4):$($props.UnicastPort)")
        }
    }
}

if ($found.Count -eq 0) {
    Write-Host ''
    Write-Host '未找到 Scream 驱动/音频设备。' -ForegroundColor Yellow
    Write-Host '请先从 https://github.com/duncanthrax/scream/releases 安装。'
    Write-Host 'Windows 11 可能需要关闭 Secure Boot 并按官方说明临时开启测试签名。'
    Write-Host '安装后运行本脚本，或在 Windows 声音设置中确认存在 Scream 输出设备。'
    exit 1
}

Write-Host ''
Write-Host 'Scream 驱动/设备已找到：' -ForegroundColor Green
$found | ForEach-Object { Write-Host "  - $_" }
Write-Host ''
Write-Host '下一步：'
Write-Host '  1. Windows 输出设备设为 Scream（或让播放器单独输出到 Scream）。'
Write-Host '  2. Scream 高级设置建议 48 kHz / 16-bit / 立体声。'
Write-Host '  3. Android 同声 → 设置 → Scream 虚拟声卡接收 → 开始接收。'
Write-Host '  4. 确认手机和电脑同一 Wi-Fi，UDP 4010 未被防火墙/AP 隔离阻挡。'
exit 0
