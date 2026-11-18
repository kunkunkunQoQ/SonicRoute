# AirPlay 设备面板和投屏检查

在 Full 版打开设备面板，从设备下拉栏选择 AirPlay。添加设备页也提供同一个接收界面。点「开启接收」后，卡片显示接收名。将 iPhone、iPad 和电脑连到同一局域网，从 Apple 控制中心的「屏幕镜像」选择该接收名，首帧到达后会打开等比窗口。最小化后可点「显示投屏窗口」。只接收声音时，从控制中心「正在播放」的 AirPlay 列表选择同一接收名。播放开始后，在快速面板找到投屏图标的 AirPlay 音量行，展开「播放」可选 Windows 输出、已连接的安卓扬声器和手机蓝牙音频设备。音量、静音和退出仍由该音量行控制。当前安卓输出断开或设备被移除时，AirPlay 切回系统输出。

接收范围为 IPv4 局域网中的一个发送端。无连接等待两分钟后自动停止。接收中切换设备或关闭面板会保留当前接收，关闭投屏窗口结束该投屏连接。视频自动模式使用兼容的 H.264，Windows 能打开 HEVC 解码器时可以在开启接收前选「H.265 优先」，实际格式由发送端协商。视频使用 Windows Media Foundation 和原有 D3D11 等比窗口。支持立体声 PCM、ALAC、AAC-LC，发送端在镜像中使用 AAC-ELD 时显示「仅画面」。在 Apple 设备上操作被投屏的应用。苹果设备发现、版本与格式协商仍需实机确认。

在 Windows、.NET 10 上执行以下命令。Python 包只用于模拟发送端，用专用环境安装即可。

```powershell
dotnet build checks/AndroidAudioChecks/AndroidAudioChecks.csproj -c Release -p:SonicRouteEdition=Full
python -m pip install cryptography==46.0.5 av==16.1.0
python checks/AndroidAudioChecks/airplay_simulator.py (Get-Command dotnet).Source '<AndroidAudioChecks.dll 的绝对路径>' '<输出目录>/simulation-summary.json'
```

在本机有可用 HEVC 解码器时，追加 `--require-hevc` 覆盖 H.265 投屏和 AAC-ELD 仅画面回退。模拟端在 `127.0.0.1` 检查 DNS-SD、密钥交换、加密控制、NTP、UDP/TCP 音频、AVC 和 HEVC 视频、输出切换和断线退役。合成音频由模拟输出读取，合成画面经真实 Windows 解码和渲染进入隐藏的窗口。无需 Apple 设备或操作系统音量。

运行 `dotnet <AndroidAudioChecks.dll> --airplay-phone-output <输出目录>` 可用本机模拟的 ADB 和安卓输出助手单独验证扬声器切换。检查覆盖无声时的路由确认、失败后原输出保留、实际 PCM 转发、静音、返回 Windows、设备移除和断线回退。

界面回归使用 `AndroidUiChecks` 的 `--device-panel-airplay` 检查下拉切换、重新打开、九语显示及设备读取边界。追加 `--airplay-lan-start` 会实际开启及停止当前 Windows 局域网接收。`--pairing-combined` 检查设置页切换和二维码，`--device-audio` 检查 Android 整机音量行。
