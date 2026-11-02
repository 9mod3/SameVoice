同声 SameVoice（双路由 1.4.0 / 本机静音 1.2.0）
================================================

这次提供两个 Windows 版，Android 输出端共用 1.1.3。

一、本机静音版（Mute）
--------------------------------
文件：
  versions/mute/SameVoice-Windows-Portable.zip

适用：
  只要“Windows 本机静音，Android/输出端继续最大声”。

功能：
  - Windows 共享页多一个「本机静音」按钮。
  - 静音的是 Windows 播放端点，不会停掉 WASAPI loopback 采集。
  - Android 端「全满」按钮可把主音量和每路音源都设到 100%。
  - 不做自动音量补偿，不包含双路由采集设备选择。

二、双路由版（DualRoute 1.4.0）
--------------------------------
文件：
  versions/dualroute/SameVoice-Windows-Portable.zip

手动缓冲：
  - 输出端新增 40–1000 ms「手动缓冲延迟」滑杆。
  - 保留 120 ms / 240 ms 预设；拖动滑杆后按手动值生效。
  - 调低延迟更跟手，但 Wi-Fi 抖动时更容易断音；调整时会短暂静音。

本机静音：
  - 静音后调节「本机音量」不会解除静音；音量变化完成后自动恢复静音。
  - 恢复声音请点「恢复本机声音」。

音质选择：
  - 标准 48 kHz / 16-bit，或高音质 48 kHz / 24-bit。
  - Windows 音源和 Android 输出端都选高音质时生效；否则自动回退标准。
  - 高音质使用 120 帧/包（2.5 ms，720 字节），避免 UDP 分片。
  - B1 不做固定放大，发送增益默认 100%，范围 0–600%。
  - 软限幅拐点 0.98。

适用：
  两端音量都要任意独立：Windows 本机 0–100%，Android 输出 0–100%。

功能：
  - 共享页新增「采集设备」选择：可选 Scream、Voicemeeter、CABLE Input 等虚拟端点。
  - 新增「本机输出」选择：选择 A1 实际连接的物理扬声器。
  - 「本机音量」只改选中的物理输出。
  - 「发送增益」只改送往 Android 的采集流。
  - 新增「本机静音」：静音选中的 A1 物理输出，采集和 Android 输出继续。
  - Android 每路音量仍然独立。
  - 需要安装 Voicemeeter（或等效双路由调音台），把 A1 接物理输出、B1 接
    Scream/VB-CABLE，然后把 Windows 应用路由到 Voicemeeter Input。
  - 完整步骤见包内 DUAL-ROUTE.md 或 docs/DUAL-ROUTE.md。

三、Android 1.1.3
--------------------------------
  android/SameVoice-1.1.3.apk（release，推荐）
  android/SameVoice-1.1.3-debug.apk
  SameVoice-Android-1.1.3.zip

新增：
  - 输出端新增 40–1000 ms 手动缓冲延迟滑杆。
  - 输出端传输音质选择：标准 16-bit / 高音质 24-bit。
  - AudioTrack 使用浮点输出，支持 24-bit 混音。
  - 保留每路音源独立音量和「全满」。
  - 保留 Scream 直接接收模式。

两个 Windows 包内都已附带：
  Android/SameVoice-1.1.1.apk
  Android/SameVoice-1.1.1-debug.apk

四、Scream 说明
--------------------------------
Scream 是第三方 Windows 内核虚拟声卡，本项目不打包驱动。
安装、检测、组播/单播与防火墙说明见：
  Scream-Guide.md（Windows 包内）
  docs/SCREAM.md（源码仓库）
  Detect-Scream.ps1（双路由包内）

五、限制
--------------------------------
- 本机静音版只能保证“静音 + 输出最大”；输入端音量任意调时仍会影响采集。
- 双路由版要真正独立，采集设备必须选 B1 虚拟设备，本机输出必须选 A1 物理设备。
- 没有 Voicemeeter 时，单靠 Scream/VB-CABLE 不能同时保留本机扬声器。
- 所有版本都不申请独占音频，不静音其他应用（本机静音版除外，它只静音本机端点）。

SHA-256:
  027367DD0872FDF51DEFF791B25539CFE0486F67F6A58B5D302699D8409689D0  versions/mute/SameVoice-Windows-Portable.zip
  B990D81621B92FB32200ABF0B3A69CC9C738C66576CECB1440ADED69CB877534  versions/dualroute/SameVoice-Windows-Portable.zip
  D4E791791EBC310815D574EE28B6330D06708CD65F97864A00387EEAD31BBF10  SameVoice-Android-1.1.3.zip
  843D1657F487B4334160DB437E2D5B77A301F46C2055742A29FFDBFD662E1577  android/SameVoice-1.1.3.apk
  4517C2F33F445C784E503E8B5A4CDAE2C95B52789F28EED9776241003C7346E5  android/SameVoice-1.1.3-debug.apk
