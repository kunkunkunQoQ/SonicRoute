# SonicRoute v1.34

2026-11-21 · [发布页 / Release page](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.34) · [完整源码比较 / Source comparison](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.33...v1.34)

## 更新内容

### Full、Lite 和 Legacy

- **应用多设备播放与恢复**。在快捷面板中按住 Ctrl 可增减播放设备，按住 Shift 可选择连续范围。各版可组合多个电脑输出，Full 还可加入可用的手机输出。成功的多输出组合按应用保存，应用再次播放或输出设备恢复可用时会尝试恢复。切回单输出会清除保存的组合。
- **鼠标和托盘操作**。托盘滚轮调节音量和鼠标快捷键共用一个系统鼠标监听。关闭托盘滚轮后，已启用的鼠标快捷键仍可使用。匹配的快捷键只执行一次，普通滚动继续传给原窗口。
- **触屏操作**。短距离滑动仍可滚动列表。长按后移动可拖动应用图标，也可用拖动手柄排列自动化规则和步骤。
- **界面语言和文档**。切换语言时，已经打开的自动化编辑器也会刷新，并保留未保存的规则内容和正在输入的延迟值。设备选择器、窗口按钮、设置提示和无障碍标签补充了本地化。九种语言的 README 同步更新，并提供日文自动化编辑器预览。

### Lite 和 Legacy

- **按需启动**。仅在托盘运行时推迟加载窗口资源和触控处理。首次显示界面时再加载。
- **本机多输出**。为同一个应用选择多个本机设备时，多个输出共用同一路应用音频采集，输出设备暂时来不及接收时按需缓冲。
- **Legacy 打包**。本机播放使用的 NAudio 程序集内嵌到 EXE，继续通过完整 ZIP 分发。

### Full

- **Android 连接管理**。打开的主界面与快捷手机面板会随 USB 插拔和 Wi-Fi 断线更新状态。同一手机在两种连接方式之间切换时可保留原设备 ID、名称和设置。快捷手机面板右下角的「刷新」和设备详情页的「重新连接」共用恢复流程。USB 无法打开时，设备详情会显示原因，并继续显示仍有效的 Wi-Fi 连接。
- **手机音频输出**。「手机当前输出」跟随手机的媒体路由，可作为 Windows 应用、Android 来源及 AirPlay 的播放目标。Android 应用的输出选单会使用为该输出设置的自定义名称。设备音频与单个 Android 应用可选择多个播放目标，保存的路由可在来源和目标重新可用后尝试恢复。
- **仅手机播放**。把电脑应用声音只送到手机时，先尝试可用的备用电脑输出进行隔离。没有可用备用输出时，在满足条件的情况下才暂时静音来源扬声器，并在退出该路由后恢复。
- **Android 文件浏览和传输**。可从设备详情拖入或选择多个文件发送，浏览受支持的手机目录并选择文件接收到电脑。手机和电脑的接收目录可分别设置，传输可取消。
- **剪贴板和串流**。改进双向文字剪贴板在 USB 或 Wi-Fi 连接变化后的恢复，并补充权限问题提示。编码器查询绑定当前连接。从后台或托盘启动后，视频窗口会正确显示首帧。
- **AirPlay 界面与连接状态**。主界面和快捷面板同步显示接收的启动、等待、连接和停止状态，接收状态也显示在快捷面板底部。切换接收状态期间防止重复操作。

## What’s new

### Full, Lite, and Legacy

- **Multi-device playback per app**. In Quick Panel, hold Ctrl to add or remove playback devices, or hold Shift to select a range. Every edition can combine multiple PC outputs. Full can also include an available phone output. SonicRoute saves successful selections per app and tries to restore them when the app plays again or an output becomes available. Switching to a single output clears the saved group.
- **Mouse and tray controls**. Tray-wheel volume control and mouse shortcuts share a system mouse hook. Enabled mouse shortcuts keep working when tray-wheel volume control is turned off. A matching shortcut fires once, and ordinary scrolling still reaches the original window.
- **Touch controls**. Short swipes still scroll lists. Press and hold, then move to drag an app icon or use a drag handle to reorder automation rules and steps.
- **Interface languages and documentation**. Changing the language refreshes an open automation editor while preserving unsaved rule changes and partially entered delay values. Device pickers, window controls, settings hints, and accessibility labels have broader localization coverage. The READMEs were updated in all nine languages, and a Japanese automation editor preview is available.

### Lite and Legacy

- **On-demand startup**. Tray-only startup defers loading window resources and touch handling until the first interface opens.
- **Local multi-output playback**. When an app plays through multiple local devices, the outputs share one capture of the app’s audio. An output buffers data only when the device cannot accept it immediately.
- **Legacy packaging**. The NAudio assemblies used for local playback are embedded in the executable. Legacy is still distributed as a complete ZIP.

### Full

- **Android connection management**. The main window and quick phone panel update when a USB cable is connected or removed or when Wi-Fi disconnects. A phone can keep its saved device ID, name, and settings when switching between USB and Wi-Fi. Refresh in the quick phone panel and Reconnect in device settings use the same recovery flow. If the USB interface cannot be opened, the device page explains the problem and still shows a working Wi-Fi connection.
- **Phone audio output**. The phone’s current output follows its active media route and can be selected for Windows apps, Android audio sources, and AirPlay. The Android app output menu displays the custom name assigned to that output. Device-wide Android audio and individual apps can use multiple playback targets. SonicRoute can try to restore saved routes when the source and targets become available again.
- **Phone-only playback**. When sending a PC app’s audio only to the phone, SonicRoute first tries to isolate playback with an available spare PC output. If none can be used, it temporarily mutes the source speaker only when the conditions allow it and restores it when the route ends.
- **Android file browsing and transfers**. From the device page, drag in or select multiple files to send. Browse supported folders on the phone and select files to receive on the PC. You can set separate receive folders on the phone and PC and cancel a transfer.
- **Clipboard and streaming**. Bidirectional text clipboard sync can recover after USB or Wi-Fi changes and now indicates permission problems. Encoder queries are tied to the current connection. The video window now appears on the first frame when SonicRoute was started in the background or from the tray.
- **AirPlay interface and status**. Starting, waiting, connected, and stopping states are synchronized across the main window and quick panel, with receiving status in the quick panel footer. The receive switch is disabled during transitions to prevent duplicate actions.

## 下载 / Downloads

| 版本 / Edition | 下载 / Download | 运行环境 / Runtime |
| --- | --- | --- |
| **Full** · Windows 音频、Android、AirPlay | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-x64.exe) · [ARM64（实验 / experimental）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-arm64.exe) | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，架构须匹配 / Match the app’s architecture |
| **Lite** · Windows 音频 / Windows audio | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-x64.exe) · [ARM64（实验 / experimental）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-arm64.exe) | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，架构须匹配 / Match the app’s architecture |
| **Legacy** · Windows 音频 / Windows audio | [x64 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Legacy-x64.zip) | .NET Framework 4.8，完整解压后运行 / Extract the entire ZIP before running |

更新清单 / Update manifest [SonicRoute-update.json](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-update.json) · 文件校验 / Checksums [SHA256SUMS.txt](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SHA256SUMS.txt)

便携版升级时先退出对应版本，再替换程序。Legacy 请完整解压新版 ZIP。原有设置保存在用户数据目录。Microsoft Store 版本以商店页面显示为准。

To upgrade a portable edition, close that edition before replacing its executable. For Legacy, extract the entire new ZIP. Your settings remain in the user data directory. See the Microsoft Store listings for the versions available there.

## 验证范围 / Validation scope

发布前的 Full x64 测试包在小米 2211133C 上验证了 USB 插拔、无线掉线后的重连，以及拔出 USB 后从原设备重新连接 Wi-Fi。Android 文件往返和 ARM64 真机兼容性仍待验证。ARM64 构建为实验版。

Pre-release Full x64 builds were tested on a Xiaomi 2211133C for USB connect and disconnect, reconnection after Wi-Fi loss, and reconnection over Wi-Fi from the existing device entry after removing the USB cable. Android file round-trip and ARM64 hardware compatibility testing remain pending. ARM64 builds are experimental.

许可证 / License [GPL-3.0-only](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.34/LICENSE) · [第三方声明 / Third-party notices](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.34/packaging/AndroidLab/THIRD-PARTY-NOTICES.md)
