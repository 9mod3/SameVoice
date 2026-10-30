同声 SameVoice 1.2.0 · 双版本
================================

这次提供两个 Windows 版，Android 输出端共用 1.1.1。

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

二、双路由版（DualRoute）
--------------------------------
文件：
  versions/dualroute/SameVoice-Windows-Portable.zip

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

三、Android 1.1.1
--------------------------------
  android/SameVoice-1.1.1.apk（release，推荐）
  android/SameVoice-1.1.1-debug.apk
  SameVoice-Android-1.1.1.zip

新增：
  - 输出端「全满」按钮：主音量 100%，所有音源 100%。
  - 保留每路音源独立音量。
  - 保留 Scream 直接接收模式（设置 → Scream 虚拟声卡接收）。

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
  C35172D4017530A932DC00652AAC7A4878034971E3D3F6FC6A2390D33F446E39  versions/dualroute/SameVoice-Windows-Portable.zip
  4310B9855572B2FD80649C1C9E2C91C12ECAB153F3DDBE1B9F343762DDDC1DE6  SameVoice-Android-1.1.1.zip
  763DE11F72656D6657D8F531D248C5D801FE09785AA34B6EC944A8AC76B604CE  android/SameVoice-1.1.1.apk
  314EEA1AAE3B9D76F280BDB950BAB4AD2D0F99DAA33D0C1B920AB391C7DFCB05  android/SameVoice-1.1.1-debug.apk
