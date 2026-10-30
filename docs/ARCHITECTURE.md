# 架构与实现说明

## 1. 角色

- **输出端（Host）**：启动房间、发现应答、接收音频、对齐时间线、混音、播放。
- **音源端（Source）**：采集本机系统声音、时钟同步、发送音频。
- 同一台设备同一时刻只能选择一种角色（避免回环和重复捕获）。任意 Windows / Android 设备都可以担任任一角色。

没有中心服务器，也没有“主控端”。输出端即房间。

## 2. 线程与数据流（Windows）

```
WASAPI loopback 线程
  └─ IAudioCapture.DataAvailable(48k stereo s16, 本地单调时间)
       └─ SourceClient：按 5 ms 切包 → 加时间戳/序号/session/token
            └─ 有界发送队列 → UDP :47822

输出端 UDP 接收线程
  └─ 校验 token/session → 按采集时间排入该音源 SourceTimeline
       └─ waveOut 线程（10 ms 块）→ MixEngine.Render
            └─ 5 ms 源队列取样、求和、软限幅 → 默认扬声器
```

控制连接每个音源一个独立线程，负责握手、心跳、`start/stop` 和模式广播。UI 定时读取不可变 `HostSnapshot`，网络线程不直接触碰 WPF 控件。

Android 结构相同：`AudioTrack.write(WRITE_BLOCKING)` 驱动混音时钟，`AudioRecord` 在独立音频优先级线程读取内部音频；网络线程使用普通后台线程。前台服务持有 WakeLock 和 high-perf Wi-Fi lock。

## 3. 时钟同步

时钟校准使用四个时间戳：

- t1：音源发出 PING 的本地单调时间
- t2：输出端收到 PING 的主机单调时间
- t3：输出端发出 PONG 的主机单调时间
- t4：音源收到 PONG 的本地单调时间

```
RTT    = (t4 - t1) - (t3 - t2)
offset = ((t2 - t1) + (t3 - t4)) / 2   // host - source
```

音源最近保留 8 个样本，取 RTT 最小的样本作为基准，但每秒最多只修正 2 ms，避免时钟补偿造成音频时间戳跳变。每 5 秒重新采样一次。

音频包中的 `captureUs = localCaptureUs + offset`，即采集时刻的输出端时钟表示。

## 4. 对齐与混音

输出端从第一个输出块开始维护绝对输出帧号 `f` 和起始时间 `t0`。每收到一个音频包：

```
startFrame = round((captureUs + targetBufferUs - t0) * 48000 / 1e6)
```

所有音源使用同一 `targetBufferUs`（同步优先 240 ms / 最低延迟 120 ms），所以同一采集时刻到达的音频被安排到同一个输出帧区间。输出块长度 10 ms（480 帧），每次从各路队列取与 `[f, f+480)` 重叠的样本求和。

- 迟到的包：落在播放窗口之前则丢弃，绝不通过“追赶”改变其相对延迟。
- 丢包：队列自然形成静音，不影响其他音源。
- 队列越界：源队列最长保留约 800 个 5 ms 包；防止内存增长。
- 软限幅：|x| ≤ 0.75 保持线性，超过后使用 tanh 软拐点，避免多路相加数字削波。
- 主音量：混音后缩放；Windows 由 MixEngine 统一应用，Android 同样，保证两端行为一致。
- 切换模式：清空各路未播放队列并改变目标缓冲，短暂静音后重新对齐。

这种“时间戳排程”比“每路独立固定延迟”更适合多源对齐：它对齐的是采集时刻，而不是接收顺序。

## 5. 发现与控制

- 音源向 `255.255.255.255` 和每个网卡的子网广播地址发送发现查询；输出端从 `47821` 单播应答。这样不要求监听广播，也避免多网卡选错地址。
- TCP 控制使用 UTF-8 JSON Lines，便于调试。`hello -> welcome` 分配会话；之后心跳 3 秒；15 秒无消息清理。
- UDP 音频头 36 字节：magic、版本、类型、session、token、sequence、captureUs、payloadLength。payload 固定 960 字节。
- PING/PONG 也在同一 UDP 音频端口上，避免额外端口和 NAT/防火墙规则。

## 6. Windows 实现

### 采集

`src/windows/SameVoice.App/Audio/WasapiLoopbackCapture.cs` 直接 P/Invoke WASAPI：

- `IMMDeviceEnumerator` 取默认 `eRender` / `eMultimedia`
- `IAudioClient.Initialize` 使用 `AUDCLNT_STREAMFLAGS_LOOPBACK` 共享模式
- 轮询 `IAudioCaptureClient`，读取设备混音格式（float32/PCM16/24/32）
- `StereoPcmConverter48k` 负责声道选择、格式转换和 44.1→48 线性重采样
- 静音包依然发送，保持源时间线连续

不依赖 NAudio，也没有虚拟声卡/VB-Cable 要求。

### 播放

`WaveOutSink` 使用 winmm `waveOutOpen` 共享模式（WAVE_MAPPER，48 kHz/16-bit/stereo），准备 5 个 10 ms WAVEHDR，通过 callback event 回收并重新填数据。waveOut 由系统混音器与其他应用共享，不会独占设备。

### UI / 后台

WPF 无边框深色界面；关闭窗口时若正在输出/共享则隐藏到托盘，播放线程继续；托盘菜单可以停止或退出。采集/发送线程通过有界队列与 UI 解耦。程序使用单实例 Mutex。

## 7. Android 实现

- 输出端：AudioTrack（`USAGE_MEDIA` / `CONTENT_TYPE_MUSIC`）共享混音，不调用 `requestAudioFocus`；`HostService` 是 `mediaPlayback` 前台服务，持有 PARTIAL_WAKE_LOCK 与 high-perf Wi-Fi lock。
- 音源端：`ShareService` 是 `mediaProjection` 前台服务，先 `startForeground` 再获取 `MediaProjection`（Android 14 要求），用 `AudioPlaybackCaptureConfiguration` + `AudioRecord` 捕获内部音频；仅匹配 `USAGE_MEDIA`、`USAGE_GAME`、`USAGE_UNKNOWN`，不采集通话/麦克风。
- 系统录屏授权由 Activity 的 `onActivityResult` 取得，Intent 数据传入服务；MediaProjection 被系统停止时服务自动停止。
- UI 使用平台 View 编写（无 AndroidX/Compose 依赖），降低构建复杂度和 APK 体积。
- 服务与 Activity 同进程，通过 `ServiceHub` 暴露只读状态；Activity 前台时 550 ms 轮询刷新。

## 8. 已知工程取舍

1. **PCM 而非 Opus/AAC**：保证零第三方编解码器、最低算法延迟、全平台一致；代价是每路 1.54 Mbps。后续可把 payload 换成 Opus，协议版本号已预留。
2. **不处理设备扬声器/蓝牙链路延迟**：多设备输出场景并不存在，当前只有一个物理输出端，因此无需为每台接收设备做延迟补偿；需要对齐的是各音源采集时间与输出端时钟。
3. **UDP 不重传**：实时音频重传只会增加延迟；通过 240 ms 缓冲和静音容错。
4. **没有 PIN/加密**：局域网可信假设下换取零配置体验。如要商用，需增加 TLS/DTLS、房间 PIN、设备配对与审计。
5. **Android 内部音频捕获依赖系统授权**：部分 OEM、DRM、通话音频不可采集，这是 Android 平台限制，不是应用可绕过的。
6. **输出端自身音频不采集**：避免必然的回环；需要输出端本地声音时，请把输出端视为纯扬声器，用其他设备提供音源。

## 9. 测试

- `SameVoice.Core.Tests`：协议编解码、发现应答、控制 JSON、时钟偏移、时间线排程/混音、PCM 转换、重采样，共 54 项断言。
- `SameVoice.LoopbackProbe`：真实 WASAPI loopback 捕获 440 Hz 测试音 → SourceClient → UDP → HostEngine 混音，验证采集、时钟校准、网络和混音端到端链路。输出端峰值和输入一致（按音量缩放）。
- `SameVoice.AudioProbe`：单独验证 waveOut 共享播放的 buffer 回收。
- Android：`assembleDebug` / `assembleRelease` 编译通过；由于交付环境没有真机或模拟器，MediaProjection 授权路径未实测，详见 README 的开发状态。
