# Scream 虚拟声卡接收模式

同声 Android 1.1.0 新增一个独立模式：直接接收 **Scream** Windows 虚拟声卡的
局域网 PCM 流。它不使用同声自己的控制协议，因此非常适合“Windows 播放，手机当
无线扬声器”的场景。

> Scream 是第三方 Windows 内核驱动，不包含在本项目中。驱动安装需要管理员权限；
> Windows 11 可能还需要关闭 Secure Boot 并临时开启测试签名。请先阅读官方说明。

- 项目主页：<https://github.com/duncanthrax/scream>
- 下载页：<https://github.com/duncanthrax/scream/releases>

## 1. 安装 Scream 驱动

### Windows 10

1. 从 Releases 下载包含 `Install` 目录的 ZIP。
2. 解压后，以**管理员身份**打开命令提示符。
3. 进入 `Install\driver\x64`（ARM64 机器用 `arm64`）。
4. 运行安装脚本/`pnputil`，按官方 `README` 操作。
5. 安装完成后，在“声音设置”里应能看到名为 **Scream** 的播放设备。

### Windows 11

Windows 11 不能直接使用旧版 `devcon` 安装。官方建议：

```bat
bcdedit /set testsigning on
:: 重启；确认桌面右下角显示 Test Mode
pnputil /add-driver .\Scream.inf /install
bcdedit /set testsigning off
:: 再次重启
```

同时需要在 BIOS 中关闭 Secure Boot。若你的机器有更严格的驱动签名策略，Scream
可能无法安装；这属于驱动签名限制，不是同声应用能绕过的。

### 检查是否安装成功

在本项目根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File tools\detect-scream.ps1
```

如果看到 `Scream 驱动/设备已找到`，就可以继续。

## 2. 把 Windows 声音送到 Scream

1. 右键任务栏音量图标 → 声音设置。
2. 将“输出”设为 **Scream**。
3. 打开 Scream 设备的“属性 → 高级”，建议设置为：
   - **48 kHz**
   - **16 位**
   - **2 声道 / 立体声**
4. 打开“配置”，选择立体声。
5. 播放音乐/视频，Windows 本机扬声器会安静（Scream 是虚拟输出）；声音会进入
   Scream 的 UDP 流。

如果只想让某个播放器走 Scream，可以在 Windows 11 的“音量合成器”里单独把该
应用的输出设为 Scream。

## 3. Android 接收

1. 安装同声 Android 1.1.0 或更高版本。
2. 打开同声 → 右上角设置 → **Scream 虚拟声卡接收**。
3. 点击 **开始接收**。首次启动会建立前台服务。
4. 手机和 Windows 必须在**同一个 Wi-Fi/子网**。
5. 接收成功后界面会显示格式（如 `48000 Hz · 16-bit · 2 ch`）、包数和电平。

Scream 默认使用：

```text
组播地址：239.255.77.77
UDP 端口：4010
```

Android 端会加入该组播组，同时也会接收直接发到手机 IP:4010 的单播包。

## 4. 网络与防火墙

- Windows 防火墙：允许 Scream 驱动/相关程序发送 UDP 4010（出站）。
- 路由器：关闭 **AP 隔离 / 客户端隔离**；需要允许组播/IGMP。
- 不建议使用访客 Wi-Fi、企业 802.1X 或跨 VLAN。
- 如果组播被路由器拦截，可在 Windows 注册表中把 Scream 改为单播：

```text
HKLM\SYSTEM\CurrentControlSet\Services\Scream\Options
  UnicastIPv4 = 手机当前 Wi-Fi IPv4 地址（REG_SZ）
  UnicastPort = 4010（REG_DWORD）
```

修改后重启 Windows。手机设置里可以看到本机 IP；Android 接收端已经绑定 4010，
无需额外配置。

## 5. 限制

- 这是**单路**接收：只接收一台 Windows 的 Scream 流，不参与同声的多音源混音、
  时钟对齐和每路音源音量。
- Android 的媒体音量控制播放音量；Scream 流本身没有单独的软件音量滑杆。
- Scream 驱动默认最多 8 声道；同声接收端会取前两个声道作为立体声。
- 支持 16/24/32-bit PCM；如果 AudioTrack 不支持某个高采样率，请把 Scream
  高级设置改为 48 kHz。
- 驱动安装、签名、蓝屏风险和许可由 Scream 项目负责；本项目不打包驱动程序。

## 6. 故障排查

| 现象 | 处理 |
| --- | --- |
| Windows 看不到 Scream 设备 | 检查驱动安装、Secure Boot/测试签名；运行检测脚本 |
| Android 一直等待数据 | 确认 Windows 输出已设为 Scream、同一 Wi-Fi、UDP 4010、AP 隔离关闭 |
| 组播不工作 | 按第 4 节改为 Scream 单播到手机 IP |
| 有数据但无声 | 检查 Android 媒体音量；确认 Scream 格式为 48 kHz/16-bit/立体声 |
| 声音断断续续 | 靠近 5 GHz 路由；关闭其他大流量；Scream 组播对 Wi-Fi 质量敏感 |
