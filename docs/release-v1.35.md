# 🎧 SonicRoute v1.35 正式版

> **发布日期** 2026-11-21 · **主题** 快速面板即时反馈、当前应用输出切换提示、多输出播放与剪贴板同步 · [📥 发布页](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.35) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 更新亮点

- 🎛️ **快速面板即时反馈**。所有版本都会立即高亮为电脑应用选择的麦克风。Full 版中只支持单个输出的外部音源也会立即显示选择结果。切换期间可以继续点选，更改会依次生效。应用行的输出名称随选择更新，失败时恢复实际状态并提示。
- ⚡ **设备切换快捷键与屏幕提示**。可连续使用设备切换或路由还原快捷键，操作会排队处理。用快捷键切换当前应用的输出时，45 毫秒内完成便直接显示结果。如果先出现切换进度提示，成功结果会等提示显示至少 500 毫秒后再出现，失败则立即提示。
- 🔊 **显式多输出镜像稳定保留**。移除按系统默认端点或原生活动会话自动剔除镜像的逻辑，原生音频会话在已选输出之间移动时继续保持用户的多路播放组合。
- 📋 **Android 音频与剪贴板并发修复（仅 Full）**。在模式切换期间保持输出切换能力并维持虚拟屏响应；修复停用手机来源后在途桌面剪贴板写入与清空剪贴板前的标记内存管理。

---

## 🛠️ 详细更新

### 1. 全版本（Full / Lite / Legacy）· 快速面板交互、快捷键 OSD 与多输出

- **快速面板即时反馈**。电脑应用可单独选择一个麦克风，不影响已选的多个播放输出。Full 版中，AirPlay 等只支持单个输出的外部音源也会在点击后立即高亮所选设备，按顺序应用更改，期间仍可继续点选。应用行会立即显示所选输出名称，Full 版的 Android 和 AirPlay 音源同样如此。选择电脑的系统默认输出时，名称仍带有默认设备标记。顶部系统设备下拉框增强默认设备回退与并发保护；目标不可用或切换失败时回退到实际状态并弹出提示。
- **设备切换快捷键与 OSD 阈值**。切换当前或全局应用输出/输入、切换系统默认输出/输入及还原全部应用路由改为无阻塞排队处理。用快捷键切换当前应用的输出时，如果在 45 毫秒内完成，屏幕只显示最终结果。耗时较长时会出现“正在切换输出设备”或“{应用} · 切换中”的提示。一旦显示，至少 500 毫秒后才会用成功结果替换。失败时立即提示。九种界面语言同步新增对应切换提示文案。
- **显式多输出镜像保留**。移除按系统默认端点或原生活动会话自动剔除已选本机镜像的逻辑。当原生音频会话在多个已选输出之间移动时，继续保留用户显式选择的多路镜像，并防止同一端点被重复添加。

### 2. Full 版 · Android 音频、虚拟屏响应与剪贴板同步

- **Android 输出切换与虚拟屏响应**。在模式切换经过重新启动阶段时保持活动输出切换能力；在音频输出打开与连接阶段记录分段耗时，保持虚拟屏点击与操作响应，并补充异常清理。
- **剪贴板同步并发与资源安全**。多手机启用双向剪贴板同步时，停用其中一台来源设备会立即取消其尚未写入电脑的在途桌面剪贴板写入，同时不影响其余已启用设备；清空系统剪贴板前先预分配并托管本地标记格式（`Remote` / `Cloud`）内存句柄，避免写入失败时泄露或产生未标记状态；释放剪贴板同步器时先取消在途任务再关闭桌面监听。

<details>
<summary>English release notes</summary>

## What’s new

### Full, Lite, and Legacy

- **Immediate feedback in the quick panel.** Select a microphone for a PC app and it is highlighted right away, independently of any playback outputs you selected. In Full, the same applies when choosing an output for a single-output external source, including AirPlay. You can keep clicking while changes take effect in order. Each app row immediately shows the selected output name, including Android and AirPlay rows in Full. A PC app using the system default output keeps the default-device label. If a device switch fails or the device becomes unavailable, the system device selector at the top returns to the actual selection and an on-screen notice explains the problem.
- **Device-switching hotkeys and on-screen feedback.** You can queue device switches and route resets with their hotkeys. When you use the hotkey to switch the current app’s output and it finishes within 45 ms, only the result appears. If a progress notice appears first, it stays visible for at least 500 ms before a successful result replaces it. Failures appear immediately. The new progress and failure messages are available in all nine interface languages.
- **Explicit multi-output mirror retention**. Removed logic that dropped mirrors based on the system default endpoint or native session moves. When a native audio session moves between selected outputs, explicitly chosen mirrors remain active and duplicate endpoints are prevented.

### Full

- **Android output switching and virtual display responsiveness**. Keeps active output switching enabled across capture mode transitions, instruments output startup phases while preserving virtual display input responsiveness, and strengthens error cleanup.
- **Clipboard sync concurrency and handle safety**. When multiple phones are enabled, disabling a source phone now cancels its in-flight desktop clipboard write while keeping other devices active. Local clipboard marker flags (`Remote` and `Cloud`) are pre-allocated before `EmptyClipboard()` with unified ownership transfer and cleanup on failure, and disposal cancels pending clipboard tasks before tearing down the desktop listener.

</details>

---

## 📦 资产下载与校验

| 版本 | 下载 | 说明 |
| --- | --- | --- |
| ⭐ **Full**（全功能版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-arm64.exe) | Windows 音频、Android 互联与 AirPlay。单文件 EXE，需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 🪶 **Lite**（纯 PC 轻量版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-arm64.exe) | Windows 本机音频。单文件 EXE，需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 🛡️ **Legacy x64**（兼容版） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Legacy-x64.zip) | Windows 本机音频，需 .NET Framework 4.8，完整解压后运行 |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | 在架版本以各自的商店页面为准 |
| 📋 **更新清单** | [`SonicRoute-update.json`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-update.json) | 客户端自动更新检测清单 |
| 🔒 **校验散列** | [`SHA256SUMS.txt`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SHA256SUMS.txt) | SHA-256 完整性校验文件 |

便携版升级时先退出对应版本，再替换程序。Legacy 请完整解压新版 ZIP。原有设置保存在用户数据目录。Microsoft Store 版本以商店页面显示为准。

To upgrade a portable edition, close that edition before replacing its executable. For Legacy, extract the entire new ZIP. Your settings remain in the user data directory. See the Microsoft Store listings for the versions available there.

---

## 📜 许可证说明（GPL-3.0-only）

本版采用 GPL-3.0-only。第三方组件沿用各自的许可证。 [许可证全文](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.35/LICENSE) · [第三方声明](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.35/packaging/AndroidLab/THIRD-PARTY-NOTICES.md) · [完整源码比较](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.34...v1.35)
