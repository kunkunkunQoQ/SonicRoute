# AirPlay 设备面板和投屏检查

在 Full 版打开设备面板，从底部下拉栏选择 AirPlay。添加设备页也使用同一接收界面。开启前可修改接收器名称、画面格式和投屏码开关；关闭面板再打开会保留设置。卡片顶部显示连接状态，底栏负责切换设备。

1. 点击「开启接收」。Apple 设备和电脑连接同一局域网后，在 iPhone 或 iPad 的控制中心选择「屏幕镜像」，或从「正在播放」的 AirPlay 列表选择接收器名称来传声音。启用投屏码时，在 Apple 设备上输入卡片显示的四位码；结束后码会更新。
2. 投屏首帧会打开等比窗口。可从卡片显示窗口、切换置顶或全屏；F11 切换全屏，Esc 退出全屏并还原原窗口。若发送端提供媒体信息，设备面板会显示歌曲、歌手和封面。
3. 快速面板的 AirPlay 音量行可选 Windows 输出、已连接的安卓扬声器或手机蓝牙设备。从 AirPlay 图标拖到安卓设备页会切到那台手机；从设备选择栏的 AirPlay 图标拖回快速面板，会回到 Windows 系统输出。拖到 AirPlay 页会结束当前串流，接收器继续等待。当前安卓输出断开或设备被移除时也会回到系统输出。

接收范围为 IPv4 局域网中的一个发送端。等待两分钟无连接后会停止接收；切换设备或关面板会保留已经开启的接收。关闭投屏窗口结束该投屏连接。画面通过 Windows Media Foundation 和现有等比窗口播放；默认用 H.264，Windows 支持时可在开启接收前选「H.265 优先」，实际格式由发送端协商。声音支持立体声 PCM、ALAC、AAC-LC，以及 44.1/48 kHz、480/512 样本、未启用 SBR 的双声道 AAC-ELD。遇到不支持的镜像音频格式时会保留画面并提示「仅画面」。应用控制仍由 Apple 设备处理。

在 Windows、.NET 10 上运行模拟发送端。Python 包只供测试，在专用环境安装即可：

```powershell
dotnet build checks/AndroidAudioChecks/AndroidAudioChecks.csproj -c Release -p:SonicRouteEdition=Full
python -m pip install cryptography==46.0.5 av==16.1.0
python checks/AndroidAudioChecks/airplay_simulator.py (Get-Command dotnet).Source '<AndroidAudioChecks.dll 的绝对路径>' '<输出目录>/simulation-summary.json'
```

本机支持 HEVC 时追加 `--require-hevc`。模拟覆盖 DNS-SD、PIN 验证、加密控制、NTP、UDP/TCP 音频、媒体信息、H.264/HEVC 画面、支持与不支持的低延迟音频、输出切换和断线退役。合成音频通过模拟输出判定，合成画面由 Windows 解码并渲染到测试窗口。模拟脚本还用 PyAV 自带的 FFmpeg 解码器独立比对 AAC-ELD 音频；合成文件及来源见 [fixtures](fixtures/README.md)。

- `AndroidUiChecks <输出目录> --device-panel-airplay --airplay-lan-start` 检查设备切换、九语和配置恢复、共享开关、拖放，并启动与停止实际 Windows LAN 监听。界面验证需要独立 Windows 桌面环境。`--airplay-window-modes` 属于 AndroidAudioChecks，检查原生窗口从普通/最大化进入并退出全屏。
- `AndroidAudioChecks --airplay-phone-output <输出目录>` 模拟安卓扬声器转发、目标失败、静音、回到系统和断线回退。`--output-startup` 与 `--explicit-output` 覆盖安卓原生应用切换，包括捕获释放时准备目标扬声器、原路由保留和重复选择。

真机上把正在播放的安卓应用接管进快速面板，保持播放，从「系统输出」切到手机扬声器，再切手机已连接的蓝牙音频设备，并切回系统输出。确认选择和实际出声一致，重复选中当前设备不会打断播放。Apple 设备发现、实际格式协商、真人听音与真机切换耗时均需设备验证。
