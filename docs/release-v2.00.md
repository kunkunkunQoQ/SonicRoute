# SonicRoute v2.00

## Full 与 Lite

- Windows 桌面版迁移到 .NET 10 Native AOT。x64 与 ARM64 均提供便携单文件 EXE，运行时内置。
- 「应用」页以画布展示应用、播放设备、录音设备及连接。可拖动连线切换原生输出或添加转发，并独立调节各路转发的音量和静音。画布支持缩放、自动整理和按应用保存布局。
- 左侧应用 Dock 随窗口高度及应用数量调整尺寸。搜索浮层可筛选、刷新并选择应用，结果放不下时显示滚动条。设备改名和隐藏后，画布即时刷新。

## Full

- 「设备」页集中提供连接和配置入口。Android 已保存连接的后台检测使用 Windows 的 DNS 服务，避免在程序首次打开且未使用 AirPlay 时，由 SonicRoute 的后台发现触发防火墙许可提示。
- Full 继续提供 Android 音频、虚拟屏与 AirPlay。Lite 提供 Windows 本机音频功能。

## 下载与校验

| 版本 | 资产 | 运行要求 |
| --- | --- | --- |
| Full | `SonicRoute-v2.00-x64.exe`、`SonicRoute-v2.00-arm64.exe` | 匹配架构的 Windows |
| Lite | `SonicRoute-v2.00-Lite-x64.exe`、`SonicRoute-v2.00-Lite-arm64.exe` | 匹配架构的 Windows |
| Legacy | `SonicRoute-v2.00-Legacy-x64.zip` | .NET Framework 4.8，完整解压后运行 |

`SHA256SUMS.txt` 用于校验下载文件。`SonicRoute-update.json` 供支持 Protocol 2 的便携客户端按版型、运行时和架构匹配更新。Microsoft Store 版通过商店单独分发。

<details>
<summary>English release notes</summary>

- Full and Lite move to .NET 10 Native AOT. The x64 and ARM64 portable executables include the runtime.
- The Apps workspace visualizes playback, recording and forwarding routes. Use the canvas to connect devices and control each forwarding output's volume and mute. Layouts are saved per app.
- The App Dock adapts to the window and app count. Search and filtering open in a floating panel. Device renaming and visibility changes now refresh the workspace immediately.
- Full groups device connection and configuration in one page. Background discovery for saved Android devices uses Windows DNS. Legacy remains the x64 .NET Framework 4.8 ZIP.

</details>
