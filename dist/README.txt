同声 SameVoice（双路由 1.4.1 / 本机静音 1.2.0）
================================================

这次提供两个 Windows 版，Android 输出端共用 1.1.7。

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

二、双路由版（DualRoute 1.4.1）
--------------------------------
文件：
  versions/dualroute/SameVoice-Windows-Portable.zip

主音量：
  - 输出端主音量范围改为 0–200%。
  - Windows 用 +/- 调整；Android 用 +/- 或「全满」。
  - 200% 会进入软限幅，更响但动态会压缩。

手动缓冲：
  - 输出端新增 40–1000 ms「手动缓冲延迟」滑杆。
  - 保留 120 ms / 240 ms 预设；拖动滑杆后按手动值生效。
  - 调低延迟更跟手，但 Wi-Fi 抖动时更容易断音；调整时会短暂静音。

Android 输出稳定性：
  - AudioTrack 保持约 20 ms 缓冲；1.1.5 的 100 ms 尝试已回退。
  - 1.1.6 增加缓冲漂移微调：缓冲偏低/偏高时按 ±1/±2 帧滑动时间线游标。
  - 1.1.7 把混音时间轴锚定到 AudioTrack 实际播放位置，显示缓冲不再比设定值少一个声道缓冲。

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

三、Android 1.1.7
--------------------------------
  android/SameVoice-1.1.7.apk（release，推荐）
  android/SameVoice-1.1.7-debug.apk
  SameVoice-Android-1.1.7.zip

新增：
  - 混音时间轴锚定 AudioTrack 实际播放位置，修复显示缓冲低于设定值。
  - 缓冲漂移微调，避免网络缓冲持续变小导致周期性卡顿。
  - 保持原 AudioTrack 缓冲和线程优先级，不引入额外延迟。

两个 Windows 包内都已附带：
  Android/SameVoice-1.1.6.apk
  Android/SameVoice-1.1.6-debug.apk

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
  2F64705FD22A983E79A2ACE1C7AA75463563BD9EDBFBD5AAA6CF8D3593A20002  versions/dualroute/SameVoice-Windows-Portable.zip
  9E4A26D9726FAC3F47D04149B0DAAC877D8AFE22905CA168855AAADA61F718C7  SameVoice-Android-1.1.7.zip
  29A02F727415FD755D4EF7677D8CDD9A5C80232CDB0F8889B1C546DD59E3B2A5  android/SameVoice-1.1.7.apk
  3E7B5508A9007254D0C4F1EB938D02B47838CD6BF607B1FD657E3461C80ABEA7  android/SameVoice-1.1.7-debug.apk
