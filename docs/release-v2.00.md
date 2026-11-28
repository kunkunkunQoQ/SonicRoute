# SonicRoute v2.00

## Full 与 Lite

- 桌面程序改用 .NET 10 Native AOT。x64 和 ARM64 的便携包均为单文件 EXE，自带 .NET 运行时。ARM64 为实验版。
- 「应用」画布集中显示播放、录音和转发连接。拖动实线可切换 Windows 主输出，点击「+」可添加转发，每路转发可单独调节音量和静音。画布支持缩放、自动整理和按应用保存布局。
- 左侧应用 Dock 随窗口高度和应用数量调整，搜索浮层可筛选、刷新与选择应用。设备改名或隐藏时，画布同步更新。[使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas)

## Full 的设备互联

- **SonicRoute Android App**。让电脑与手机接入同一局域网，在 Windows 的「设备 → 连接设备 → Android」选择附近手机，核对两端显示的八位数字。配对后可双向传输声音，并查看经手机系统授权共享的画面。该投屏用于查看。
- **ADB 调试连接**。通过已授权的 USB 或无线调试，可操作独立的应用虚拟屏、发送鼠标键盘等输入、传输文件和同步文字剪贴板。已保存无线调试连接的后台发现使用 Windows DNS 服务。
- **AirPlay**。电脑可接收兼容 Apple 设备的音频和屏幕镜像。[设备互联操作](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94)

## 便携包与升级

| 版型 | x64 | ARM64（实验版） |
| --- | --- | --- |
| Full | `SonicRoute-v2.00-x64.exe` | `SonicRoute-v2.00-arm64.exe` |
| Lite | `SonicRoute-v2.00-Lite-x64.exe` | `SonicRoute-v2.00-Lite-arm64.exe` |

按电脑架构选择 EXE。桌面版支持 Windows 10 2004（内部版本 19041）及更高版本和 Windows 11，逐进程音频捕获转发要求内部版本 20348 或更高。下载后可用 `SHA256SUMS.txt` 核对文件。便携更新使用 Protocol 2 的 `SonicRoute-update.json`，按版型、运行时和架构选包。[GitHub 发行页](https://github.com/kunkunkunQoQ/SonicRoute/releases)

手动升级同版型便携程序前，请先从托盘退出旧版，再替换对应 EXE。设置和规则使用原用户数据目录。切换版型、运行时或处理器架构时，请手动选择新包。Microsoft Store 的 Full x64 候选单独打包，商店用户通过 Microsoft Store 安装和更新。

<details>
<summary>English release notes</summary>

## Full and Lite

- Both desktop editions move to .NET 10 Native AOT. The x64 and ARM64 portable executables include the .NET runtime. ARM64 is experimental.
- The Apps canvas shows playback, recording and forwarding routes. Drag the solid line to change the primary Windows output or click **+** to add a forwarded output. Each forward has its own volume and mute controls. Zoom, arrange nodes and save the layout per app.
- The app dock adapts to the window height and app count. Use the search panel to filter, refresh and select apps. The canvas refreshes when you rename or hide a device. [User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas)

## Device connections in Full

- **SonicRoute Android app.** Connect the PC and phone to the same local network, select the nearby phone in the Windows Devices page, and compare the eight-digit codes on both devices. Once paired, send audio in either direction and view the screen after granting system permission on the phone. The shared screen is view-only.
- **ADB debugging.** An authorized USB or wireless debugging connection supports interactive app virtual displays, mouse and keyboard input, file transfers and text clipboard sync. Background discovery of saved wireless debugging connections uses the Windows DNS service.
- **AirPlay.** Receive audio and screen mirroring from compatible Apple devices. [Device connection guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94)

## Portable packages and upgrades

| Edition | x64 | ARM64 (experimental) |
| --- | --- | --- |
| Full | `SonicRoute-v2.00-x64.exe` | `SonicRoute-v2.00-arm64.exe` |
| Lite | `SonicRoute-v2.00-Lite-x64.exe` | `SonicRoute-v2.00-Lite-arm64.exe` |

Choose the EXE for your PC’s architecture. The desktop app requires Windows 10 version 2004 (build 19041) or later, including Windows 11. Per-process audio capture for forwarding requires build 20348 or later. Check downloaded files against `SHA256SUMS.txt`. The Protocol 2 `SonicRoute-update.json` matches portable updates by edition, runtime and architecture. [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases)

To upgrade the same portable edition manually, exit the old copy from the system tray, then replace its EXE. Settings and rules remain in the user data directory. Choose the new package manually when switching editions, runtimes or architectures. The Full x64 Microsoft Store candidate is packaged separately. Store users install and update through Microsoft Store.

</details>
