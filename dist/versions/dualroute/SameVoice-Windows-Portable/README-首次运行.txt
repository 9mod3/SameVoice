同声 SameVoice Windows 便携版 — 首次运行说明
================================================

为什么以前会提示“Windows 保护了你的电脑”？
------------------------------------------------
旧包直接启动 SameVoice.exe。这个 exe 没有商业代码签名证书，Windows
SmartScreen 会把它当作“未知发布者”并拦截。

本版的处理：
- 不再直接启动未签名的 SameVoice.exe。
- 改为通过微软签名的 dotnet.exe 加载 SameVoice.dll。
- dotnet.exe 位于 dotnet-runtime\dotnet.exe，带微软有效签名。
- 启动器会自动对 SameVoice.dll / dotnet.exe 执行 Unblock-File。

启动方法
------------------------------------------------
1. 运行本目录的 Start-SameVoice.cmd。
2. 不要去找 SameVoice.exe（本版已移除）。
3. 如果 Windows 对 .cmd 弹出“打开文件 - 安全警告”，点“运行”即可。

更稳妥的做法
------------------------------------------------
下载 ZIP 后，先右键 ZIP → 属性：
- 如果底部有“解除锁定 / Unblock”，勾选后再解压。
这样解压出来的文件不会带“来自 Internet”的标记，通常不会再弹安全警告。

仍然弹出 SmartScreen 怎么办
------------------------------------------------
点“更多信息”→“仍要运行”。这是因为同声目前没有购买受信任的代码签名证书，
不是病毒。正式消除该提示需要给程序做 Authenticode 代码签名，并积累
SmartScreen 信誉；仅靠程序本身无法绕过 Windows 的信誉机制。

文件说明
------------------------------------------------
Start-SameVoice.cmd     启动入口
SameVoice.dll           SameVoice 主程序
dotnet-runtime\         内置 .NET 8 运行时
dotnet-runtime\dotnet.exe  微软签名的 .NET 主机
