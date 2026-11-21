# SonicRoute v1.34

2026-11-21 · [发布页 / Release page](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.34)

## 更新内容

### Full、Lite 和 Legacy

- **应用多设备播放与恢复**。在快捷面板中按住 Ctrl 可增减电脑播放设备，按住 Shift 可选择连续范围。成功的多输出组合会保存。重启 SonicRoute 后，应用再次播放时会尝试恢复该组合。切回单输出会清除保存的多输出组合。
- **鼠标和托盘操作**。托盘滚轮调节音量和鼠标快捷键共用一个系统鼠标监听。关闭托盘滚轮后，已启用的鼠标快捷键仍可使用。
- **界面语言**。在保留已打开的自动化编辑器时切换语言，编辑器也会更新。随版本提供更新的多语言文档与自动化编辑器预览。

### Full

- **Android 连接管理**。主界面与快捷手机面板同步显示 USB 和 Wi-Fi 连接状态。同一手机在两种连接方式之间切换时可保留原设备设置。快捷手机面板右下角的「刷新」现在也能按设备详情页的「重新连接」流程恢复所选手机。
- **手机音频输出**。「手机当前输出」跟随手机的媒体路由，可作为 Windows 应用、Android 来源及 AirPlay 的播放目标。Android 应用的输出选择菜单会使用为「手机当前输出」设置的自定义名称。
- **Android 文件浏览和传输**。设备详情可以浏览受支持的手机目录、发送和接收文件，并设置收件目录。改进了连接变化时的剪贴板同步处理。

## What’s new

### Full, Lite, and Legacy

- **Multi-device playback per app**. In Quick Panel, hold Ctrl to add or remove PC playback devices, or hold Shift to select a range. SonicRoute saves successful multi-device selections and tries to restore them when the app plays audio after a restart. Switching back to a single output clears the saved group.
- **Mouse and tray controls**. Tray-wheel volume control and mouse shortcuts share a system mouse hook. Enabled mouse shortcuts keep working when tray-wheel volume control is turned off.
- **Interface languages**. The automation editor now updates when you change the language while the editor is open. The release includes updated multilingual documentation and automation editor previews.

### Full

- **Android connection management**. The main window and the quick phone panel now show the same USB or Wi-Fi connection status. SonicRoute can retain the saved settings for a phone when switching between the two connection types. Refresh in the bottom-right corner of the quick phone panel now uses the same reconnection flow as Reconnect in device settings.
- **Phone audio output**. The phone’s current output follows its active media route and can be selected for Windows apps, Android audio sources, and AirPlay. The Android app output menu now shows the custom name assigned to the phone’s current output.
- **Android file browsing and transfers**. From the device page, you can browse supported folders on the phone, send and receive files, and set the destination folders. Clipboard synchronization now handles connection changes more reliably.

## 下载 / Downloads

| 版本 / Edition | 下载 / Download | 运行环境 / Runtime |
| --- | --- | --- |
| **Full** · Windows 音频、Android、AirPlay | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-x64.exe) · [ARM64（实验 / experimental）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-arm64.exe) | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，架构须匹配 / Match the app’s architecture |
| **Lite** · Windows 音频 / Windows audio | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-x64.exe) · [ARM64（实验 / experimental）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-arm64.exe) | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)，架构须匹配 / Match the app’s architecture |
| **Legacy** · Windows 音频 / Windows audio | [x64 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Legacy-x64.zip) | .NET Framework 4.8，完整解压后运行 / Extract the entire ZIP before running |

更新清单 / Update manifest [SonicRoute-update.json](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-update.json) · 文件校验 / Checksums [SHA256SUMS.txt](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SHA256SUMS.txt)

便携版升级时先退出对应版本，再替换程序。Legacy 请完整解压新版 ZIP。原有设置保存在用户数据目录。Microsoft Store 版本以商店页面显示为准。

To upgrade a portable edition, close that edition before replacing its executable. For Legacy, extract the entire new ZIP. Your settings remain in the user data directory. See the Microsoft Store listings for the versions available there.

ARM64 下载为实验版。构建和自动检查与真机听音、设备兼容及文件往返测试分开。

ARM64 downloads are experimental. Build and automated checks are separate from audio listening, device compatibility, and file round-trip testing on physical hardware.

许可证 / License [GPL-3.0-only](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.34/LICENSE) · [第三方声明 / Third-party notices](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.34/packaging/AndroidLab/THIRD-PARTY-NOTICES.md)
