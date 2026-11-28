# 🎧 SonicRoute v2.00

> **主题** 全新开始 · [📥 发布页](https://github.com/kunkunkunQoQ/SonicRoute/releases) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 更新亮点

- 🪟 **原生桌面与便携单文件**。Full 和 Lite 改用 .NET 10 Native AOT，x64 和 ARM64 EXE 自带 .NET 运行时。ARM64 为实验版。
- 🎛️ **应用路由画布与快速面板**。拖动实线切换应用主输出，点「+」添加转发，再分别调节各路音量和静音。快速面板可在应用所在行按需选择录音输入，按住 `Ctrl` 单击可追加播放输出。
- 📱 **SonicRoute Android App 互联（仅 Full）**。手机与电脑在同一局域网核对八位数字完成配对，可双向传输声音，并查看手机授权共享的画面。
- 🎬 **ADB 与 AirPlay（仅 Full）**。ADB 提供可操作的独立应用虚拟屏、文件传输和文字剪贴板同步。AirPlay 可接收兼容 Apple 设备的音频和屏幕镜像。

---

## 🛠️ 详细更新

### 1. Full 与 Lite · 原生桌面和应用音频路由

- **「应用」画布**。实线显示 Windows 主输出，虚线显示附加的捕获转发。可按应用选择采集声音并转发到其他播放设备，各路转发分别设定音量与静音。画布支持缩放、自动整理和按应用保存布局。左侧应用栏随窗口高度和应用数量调整，搜索浮层可筛选、刷新与选择应用。设备改名或隐藏时，画布同步更新。[画布操作](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas)
- **快速面板选区**。展开正在发声的应用，按住 `Ctrl` 单击可添加或取消一个播放设备，按住 `Ctrl+Shift` 单击电脑播放设备可切换原生主输出，保留其他已选设备。追加的本机输出会捕获并转发该应用的声音。开启「设置 → 杂项」中的「麦克风选项」和「在快速面板显示麦克风」后，可在该应用展开行选择录音设备。[快速面板操作](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#multi-output)

### 2. Full · Android App、ADB 与 AirPlay

- **SonicRoute Android App**。电脑与手机接入同一局域网，在 Windows 的「设备 → 连接设备 → Android」选择附近手机，核对两端显示的八位数字。配对后可在电脑播放手机音频、将电脑声音送到手机，以及查看经手机系统授权共享的画面。App 投屏供查看。
- **ADB 调试连接**。通过已授权的 USB 或无线调试，可在电脑上使用鼠标键盘操作独立的应用虚拟屏，也可传输文件和同步文字剪贴板。已保存无线调试连接的后台发现使用 Windows DNS 服务。
- **AirPlay 接收**。接收兼容 Apple 设备的音频和屏幕镜像。[设备互联操作](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94)

## 可靠性与无障碍

- Windows 报告设备变化时，音频缓存会在后台刷新。导入或重置设置遇到文件占用时，已排队的普通设置仍会保存。
- “导入设置”只替换普通设置，自动化规则继续由自动化页管理。原地升级仍会迁移旧配置中的规则。
- 快捷面板的固定、设备、静音和主窗入口支持屏幕阅读器。设置中的 GPL-3.0 窗口可离线查看当前版型附带的第三方声明。
- Full 恢复了手机音频和 AirPlay 的应用开始、结束触发。独立手机连接的协议读取错误进入既有断线处理，AirPlay 接收器启动期间也会处理网络接口变化。
- Full 的“切换到应用”规则可选取已打开的 Android 应用虚拟屏和 AirPlay 镜像窗口。仅投画面时也可以新建规则。重新编辑规则时可以保留已离线的原目标。
- 旧规则的应用目标为空时仍能打开编辑器，选好应用后即可保存。
- 手机配对、权限与连接提示补齐七种语言。窄幅手机副面板会换行显示媒体说明。关闭连接页时会取消并收回进行中的设备发现请求。

<details>
<summary>English release notes</summary>

## What’s new · A New Beginning

### Full and Lite

- **Native desktop and portable executables.** Both editions move to .NET 10 Native AOT. Each x64 or experimental ARM64 executable includes the .NET runtime.
- **Per-app routing canvas.** Drag the solid line to change the primary Windows playback device. Click **+** to capture the app’s audio and forward it to another output, with independent volume and mute controls. Zoom, arrange nodes and save layouts per app. The app dock and search panel help you find the app you want.
- **Quick Panel.** Expand an app that’s playing audio. Ctrl-click to add or remove an output and use Ctrl+Shift-click to make a PC output the primary route while keeping other selected devices. Additional PC outputs use capture and forwarding for that app. To choose the app’s recording input, enable the microphone options and microphone controls in Quick Panel.

### Full

- **SonicRoute Android app.** Pair on the same local network by comparing the eight-digit code shown on both devices. Send audio in either direction and view the phone screen once the user grants Android’s system permission. The shared screen is view-only. [Android releases](https://github.com/kunkunkunQoQ/SonicRoute-App/releases) · [Get and install the app](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install)
- **ADB debugging.** Use authorized USB or wireless debugging for interactive app virtual displays, input controls, file transfers and text clipboard sync. Background discovery of saved wireless debugging connections uses Windows DNS.
- **AirPlay.** Receive audio and screen mirroring from compatible Apple devices. [Device connection guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-%E8%AE%BE%E5%A4%87%E4%BA%92%E8%81%94)

## Reliability and accessibility

- Windows device notifications now refresh the audio cache on a background thread. A failed settings import or reset preserves edits already queued for saving. Settings imports are separate from automation rules. In-place upgrades still migrate rules from older configurations.
- Screen readers can access the quick panel's pin, device, mute, and main-window controls. The GPL-3.0 window in Settings includes the third-party notices for the installed edition.
- Full restores app start and exit triggers for phone audio and AirPlay. Phone protocol read errors enter the existing disconnect recovery path. The AirPlay receiver also handles network changes during startup.
- Full can select an open Android app virtual display or AirPlay mirror window for an app-switch rule, including video-only sessions. Editing a saved rule preserves its original target when the target is offline.
- Older rules with an empty app target now open in the editor. Select an app to save the rule.
- Phone pairing, permission, and connection messages now cover all nine languages. Media instructions wrap in the narrow phone panel. Closing the connection page cancels and completes pending device discovery requests.

</details>

---

## 📦 资产下载与校验

在 Windows 发行页选择对应版本，按下表文件名从 **Assets（附件）** 下载。手机端使用单独的 Android App 下载页。[Windows 发行页](https://github.com/kunkunkunQoQ/SonicRoute/releases)

| 版本 | 下载 | 说明 |
| --- | --- | --- |
| ⭐ **Full**（全功能版） | x64 `SonicRoute-v2.00-x64.exe` / ARM64（实验）`SonicRoute-v2.00-arm64.exe` | Windows 本机音频、Android 互联与 AirPlay。单文件 EXE，自带 .NET 10 Native AOT 运行时 |
| 🪶 **Lite**（纯 PC 轻量版） | x64 `SonicRoute-v2.00-Lite-x64.exe` / ARM64（实验）`SonicRoute-v2.00-Lite-arm64.exe` | Windows 本机音频。单文件 EXE，自带 .NET 10 Native AOT 运行时 |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | Full x64 商店候选单独打包。在架版本以商店页面为准 |
| 📋 **更新清单** | `SonicRoute-update.json` | Protocol 2 便携更新按版型、运行时和架构选包 |
| 🔒 **校验散列** | `SHA256SUMS.txt` | 便携包与更新清单的 SHA-256 校验值 |
| 📱 **SonicRoute Android App** | [Android App 下载页](https://github.com/kunkunkunQoQ/SonicRoute-App/releases) / [安装说明](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install) | Android 10 及以上。APK 将列在安卓发行页，当前可按安装说明从源码构建 |

桌面版需要 Windows 10 2004（内部版本 19041）或更高版本，含 Windows 11。将选中应用的音频捕获并转发到其他本机输出需要 Windows 内部版本 20348 或更高。

手动升级同版型便携程序时，先从托盘退出旧版，再替换对应 EXE，设置和规则沿用原用户数据目录。切换版型、运行时或处理器架构时，请手动选择新包。商店版通过 Microsoft Store 安装和更新。

To upgrade the same portable edition, exit it from the system tray and replace its executable. Your settings and rules stay in the user data directory. Choose a package manually when switching editions, runtimes or architectures. Microsoft Store installation and updates are handled separately.

---

## 📜 许可证说明（GPL-3.0-only）

本版采用 GPL-3.0-only。第三方组件沿用各自的许可证。[许可证全文](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/LICENSE) · [第三方声明](https://github.com/kunkunkunQoQ/SonicRoute/blob/master/packaging/AndroidLab/THIRD-PARTY-NOTICES.md) · [与 v1.36 比较](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.36...master)
