同声 SameVoice — Windows 便携版

双击「Start-SameVoice.cmd」启动。
这个目录已经内置 .NET 8 桌面运行时（dotnet-runtime），不需要另外安装 .NET，也不需要联网。

首次启动时：
1. Windows 防火墙可能询问是否允许同声访问网络，请勾选「专用网络」并允许。
2. 如果没有弹窗或被误点「取消」，右键以管理员身份运行本目录的 Allow-Firewall.ps1。
3. 如果使用过其他安全软件，请放行 TCP 47820–47829、UDP 47821、UDP 47822。

直接双击 SameVoice.exe 也可以，但需要系统已安装 .NET 8 Desktop Runtime。
建议始终使用 Start-SameVoice.cmd，便携运行时才能生效。

输出端使用系统默认扬声器；关闭窗口会缩到托盘继续播放。
音源端捕获系统默认播放设备上所有应用的声音。

更多说明见项目根目录 README.md。
