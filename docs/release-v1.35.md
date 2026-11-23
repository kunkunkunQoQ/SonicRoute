# 🎧 SonicRoute v1.35 正式版

> **发布日期** 2026-11-21 · **主题** 快捷面板统一即时切换、防闪烁设备切换 OSD、显式多输出保留与剪贴板并发修复 · [📥 发布页](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.35) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 更新亮点

- 🎛️ **快捷面板统一即时切换**。Full、Lite 和 Legacy 的电脑应用麦克风输入与外部音频行均支持点击后即时高亮并排队生效，不再禁用按钮；应用行标题即时同步所选输出设备名称，失败时自动回退并提示。
- ⚡ **设备切换快捷键与防闪烁 OSD**。全局设备切换与路由还原快捷键改为无阻塞排队执行。45 ms 内完成的快速切换直接显示结果，耗时更久时再展示本地化“切换中”提示并至少保留 500 ms 可见。
- 🔊 **显式多输出镜像稳定保留**。移除按系统默认端点或原生活动会话自动剔除镜像的逻辑，原生音频会话在已选输出之间移动时继续保持用户的多路播放组合。
- 📋 **Android 音频与剪贴板并发修复（仅 Full）**。在模式切换期间保持输出切换能力并维持虚拟屏响应；修复停用手机来源后在途桌面剪贴板写入与清空剪贴板前的标记内存管理。

---

## 🛠️ 详细更新

### 1. 全版本（Full / Lite / Legacy）· 快捷面板交互、快捷键 OSD 与多输出

- **快捷面板统一即时切换**。电脑应用的麦克风输入行支持独立单选即时切换（与多选播放输出互不干扰），AirPlay 与单输出外部音源行同样改为点击后即时高亮并按顺序排队应用，切换期间保持按钮可点击。快速面板中电脑、Android 与 AirPlay 应用行标题均即时同步显示目标输出名称（电脑系统默认输出保留默认设备后缀样式）。顶部系统设备下拉框增强默认设备回退与并发保护；目标不可用或切换失败时回退到实际状态并弹出提示。
- **设备切换快捷键与 OSD 阈值**。切换当前或全局应用输出/输入、切换系统默认输出/输入及还原全部应用路由改为无阻塞排队处理。45 ms 内完成的快速切换直接展示最终 OSD，避免瞬时闪烁；超过该阈值后展示本地化的“正在切换输出设备”或“{应用} · 切换中”提示并至少保持 500 ms 可见，失败时立即提示，不额外等待。九种界面语言同步新增对应切换提示文案。
- **显式多输出镜像保留**。移除按系统默认端点或原生活动会话自动剔除已选本机镜像的逻辑。当原生音频会话在多个已选输出之间移动时，继续保留用户显式选择的多路镜像，并防止同一端点被重复添加。

### 2. Full 版 · Android 音频、虚拟屏响应与剪贴板同步

- **Android 输出切换与虚拟屏响应**。在模式切换经过重新启动阶段时保持活动输出切换能力；在音频输出打开与连接阶段记录分段耗时，保持虚拟屏点击与操作响应，并补充异常清理。
- **剪贴板同步并发与资源安全**。多手机启用双向剪贴板同步时，停用其中一台来源设备会立即取消其尚未写入电脑的在途桌面剪贴板写入，同时不影响其余已启用设备；清空系统剪贴板前先预分配并托管本地标记格式（`Remote` / `Cloud`）内存句柄，避免写入失败时泄露或产生未标记状态；释放剪贴板同步器时先取消在途任务再关闭桌面监听。

<details>
<summary>English release notes</summary>

## What’s new

### Full, Lite, and Legacy

- **Unified optimistic switching in Quick Panel**. Microphone input selections for PC apps (independent of multi-output playback) and single-output external sources (including AirPlay) now highlight immediately and apply through a serialized queue without disabling buttons. PC, Android, and AirPlay row titles update optimistically to reflect the selected output name (keeping the default-device suffix on PC system-default rows). The top system device selector hardens default rollback and concurrency handling, and failed or unavailable switches roll back cleanly with an on-screen notice.
- **Non-blocking device hotkeys and flicker-free OSD**. Global device-switching and route-reset hotkeys execute via a non-blocking queue with two-stage OSD feedback. Switches finishing within 45 ms jump straight to the final result without flashing a pending banner; slower switches show localized switching status and keep it visible for at least 500 ms before updating (failures display immediately without waiting). All nine interface languages include the new switching and failure strings.
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
