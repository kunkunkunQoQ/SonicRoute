# 🎧 SonicRoute v1.36 正式版

> **发布日期** 2026-11-24 · **主题** AirPlay 多设备同时输出、Android 未压缩 PCM / 3 档延迟与自适应码率、多输出键位重映射与主窗口侧栏折叠 · [📥 发布页](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36) · [📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

---

## ✨ 更新亮点

- 🎛️ **多输出选区重映射与主窗口同步**。Full、Lite 和 Legacy 的快速面板将 `Shift + 单击` 改为保留现有选择并追加连续范围，`Ctrl + Shift + 单击` 切换 Windows 原生路由主设备，并以不同主题样式区分原生路由与镜像分发；「应用」页补齐多输出操作，主窗口左侧主导航栏支持折叠与状态记忆。
- 🍎 **AirPlay 多设备同时输出（仅 Full）**。AirPlay 音频会话支持在快速面板和「应用」页中同时选择多个输出目标（电脑或 Android 设备），统一遵循多输出选区与样式规则。
- 🔊 **Android 未压缩 PCM、3 档延迟与更高兼容性（仅 Full）**。每台手机新增未压缩 `Raw PCM` 音频传输及「超低延迟 / 平衡 / 稳定」三档延迟模式；扩展多 ROM 音频捕获兼容，应用捕获最低要求下探至 Android 11。
- 🎬 **Android 视频自适应码率与全端性能/商店优化**。无线连接支持事件驱动自适应视频码率与样本池解码优化；全版本合并设备通知中心、批量音量写入与空闲资源回收；Store 版保持任务栏分组标识并声明 AirPlay 防火墙规则。

---

## 🛠️ 详细更新

### 1. 全版本（Full / Lite / Legacy）· 多输出交互、主窗口与底层重构

- **多输出键位重映射与样式区分**。`Shift + 单击` 现保留当前已有选择（含已选隐藏设备）并追加连续范围，`Ctrl + Shift + 单击` 用于选择或切换 Windows 原生路由设备（支持系统默认镜像并在默认输出变化时按事件重新绑定）。主设备（`DevButtonActive`）与采集分发镜像（`DevButtonMirror`）采用区分样式并自适应浅色/深色主题；同步更新九种语言的 `Qp.PlaybackMultiSelect` 提示。
- **主窗口侧栏折叠与「应用」页对齐**。主窗口左侧主导航栏支持平滑折叠/展开并持久化状态，收窄「应用」页二级设备栏宽度并收紧间距。「应用」页补齐与快速面板一致的多输出选区、即点即亮（Optimistic UI）与原生/镜像样式，并与快速面板保持跨窗口同步。
- **设备通知、空闲回收与配置安全**。引入 `AudioDeviceNotificationHub` 合并 CoreAudio 设备通知，保护内部镜像会话并复用轻量镜像播放；统一设备筛选、音量批量写入（`CoalescedVolumeWriter`）与异步启动项编辑器；改进主窗口空闲 UI 回收与 `AtomicFile` 原子配置写入，清理 13 项经审计的未用符号与资源。
- **Microsoft Store 任务栏标识**。Store 打包版启动时显式绑定应用用户模型 ID（AUMID），保持任务栏图标与固定标识一致。

### 2. Full 版 · AirPlay 多输出、Android 音视频与防火墙规则

- **AirPlay 多设备同时输出**。AirPlay 音频会话接入 `IMultiOutputAudioSession`，可在快速面板和「应用」页同时选择多个电脑或已连接 Android 输出设备，并同步头部增益、音量与静音状态。
- **Android Raw PCM 与 3 档音频延迟**。设备流媒体设置新增未压缩 `Raw PCM` 传输选项与「超低延迟 / 平衡（默认）/ 稳定」三档延迟模式，配合 Concentus Opus 延迟配置与自适应 PCM 缓冲；九种界面语言同步本地化设置项与生效说明。
- **Android 音频捕获兼容性扩展**。改进跨 ROM 播放音频捕获兼容，将应用音频捕获门槛下探至 Android 11 及以上，新增不支持音频格式与无效音频流错误提示。
- **Android 视频自适应码率与解码优化**。无线连接增加事件驱动的视频自适应码率控制器（`AdaptiveBitrateController`，平滑下调码率并支持关键帧同步请求）；桌面端引入引用计数安全的 MediaFoundation 输入样本池、`ComLease`、双 span 解码与同步帧恢复，限制编码器 FPS/能力边界并优化低延迟回退；跨 ADB 服务缓存 helper JAR、共享 USB 监听并保护活动 USB 流，改进虚拟屏触控/输入处理。
- **Store Full 版 AirPlay 防火墙规则**。Store Full 清单声明 AirPlay mDNS、PTP 时钟及传输端口范围（UDP `5353`、`319–320`，TCP/UDP `47120–47183`）的入站防火墙规则，并在商店运行时绑定该端口范围。

<details>
<summary>English release notes</summary>

## What’s new

### Full, Lite, and Legacy

- **Remapped multi-output modifiers and route styling**. In Quick Panel, `Shift-click` now preserves existing selections (including hidden selected outputs) while adding a contiguous range, and `Ctrl+Shift-click` selects or switches the Windows native routing endpoint (including System Default mirrors with event-driven rebinding). Native routes (`DevButtonActive`) and capture-distributed mirrors (`DevButtonMirror`) use distinct adaptive light/dark button styles, with updated `Qp.PlaybackMultiSelect` guidance in all nine languages.
- **Collapsible main sidebar and Apps page parity**. The main window’s primary sidebar can now collapse and expand with persisted state, and the Apps page secondary sidebar is narrower with tighter spacing. The Apps page now matches Quick Panel’s multi-output selection, optimistic UI, native vs. mirror styling, and cross-window state synchronization.
- **Consolidated audio notifications, idle reclaim, and cleanup**. Added `AudioDeviceNotificationHub` to consolidate CoreAudio device notifications, protect internal mirror sessions, and reuse lightweight mirror playback. Unified device filtering, batched volume writes (`CoalescedVolumeWriter`), async launch-item editing, atomic file backups, and idle main-window UI reclamation, while removing 13 audited unused symbols and resources.
- **Microsoft Store taskbar identity**. Packaged Store builds explicitly apply their AppUserModelID on startup so taskbar icons and pinned entries stay grouped consistently.

### Full

- **AirPlay simultaneous multi-device output**. AirPlay audio sessions now implement `IMultiOutputAudioSession`, letting you play AirPlay audio across multiple PC or connected Android outputs at the same time from Quick Panel or the Apps page with synchronized volume, mute, and gain.
- **Android Raw PCM and three audio latency modes**. Per-phone streaming settings add uncompressed `Raw PCM` audio transport alongside Concentus Opus and introduce three audio latency modes — Ultra-low latency, Balanced (default), and Stable — backed by adaptive PCM buffering and localized across all nine languages.
- **Broader Android audio capture compatibility**. Improved app playback capture compatibility across Android ROMs, lowering the minimum requirement notice to Android 11 and adding clear error messages for unsupported audio formats or invalid audio streams.
- **Adaptive video bitrate and decoding pipeline improvements**. Wireless virtual displays add an event-driven `AdaptiveBitrateController` with smoothed downshifts and keyframe sync requests. Desktop decoding adds a refcount-safe MediaFoundation input sample pool, `ComLease`, two-span decoding, and sync frame recovery with encoder FPS/capability clamping. Helper JAR transfers are cached across ADB services with shared USB hook handling and live USB stream protection, alongside refined virtual display input handling.
- **AirPlay firewall rules in Store Full**. `AppxManifest.xml` declares inbound domain/private firewall rules for AirPlay discovery, PTP timing, and listener ports (UDP `5353`, `319–320`, TCP/UDP `47120–47183`), matched by `AirPlayListenerPorts` when packaged.

</details>

---

## 📦 资产下载与校验

| 版本 | 下载 | 说明 |
| --- | --- | --- |
| ⭐ **Full**（全功能版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-arm64.exe) | Windows 音频、Android 互联与 AirPlay。单文件 EXE，需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 🪶 **Lite**（纯 PC 轻量版） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-arm64.exe) | Windows 本机音频。单文件 EXE，需匹配架构的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 🛡️ **Legacy x64**（兼容版） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Legacy-x64.zip) | Windows 本机音频，需 .NET Framework 4.8，完整解压后运行 |
| 🔄 **Microsoft Store** | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | 在架版本以各自的商店页面为准 |
| 📋 **更新清单** | [`SonicRoute-update.json`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-update.json) | 客户端自动更新检测清单 |
| 🔒 **校验散列** | [`SHA256SUMS.txt`](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SHA256SUMS.txt) | SHA-256 完整性校验文件 |

便携版升级时先退出对应版本，再替换程序。Legacy 请完整解压新版 ZIP。原有设置保存在用户数据目录。Microsoft Store 版本以商店页面显示为准。

To upgrade a portable edition, close that edition before replacing its executable. For Legacy, extract the entire new ZIP. Your settings remain in the user data directory. See the Microsoft Store listings for the versions available there.

---

## 📜 许可证说明（GPL-3.0-only）

本版采用 GPL-3.0-only。第三方组件沿用各自的许可证。 [许可证全文](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.36/LICENSE) · [第三方声明](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.36/packaging/AndroidLab/THIRD-PARTY-NOTICES.md) · [完整源码比较](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.35...v1.36)
