<!-- Generated from docs/readme/shared.json and docs/readme/locales/zh-CN.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音跃 SonicRoute v1.34 Beta

**简体中文** | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![v1.34 Beta](https://img.shields.io/badge/Beta-v1.34-orange)](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.33...Beta) [![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![爱发电](https://img.shields.io/badge/赞助-爱发电-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

## 相较正式版 v1.33，这次更新了什么？

- **两台电脑配对**。在两台 Windows 电脑的 Full 版中打开「应用 → 添加设备 → Windows Link」，搜索同一局域网内的电脑或手动输入地址。两边核对同一个 8 位码即可配对。
- **跨电脑播放声音**。把音乐应用拖到另一台电脑，可选仅在对方播放，或两台一起播放。也可以转发整台电脑的声音，或为同一音源选择多个输出。
- **共享画面、文件和剪贴板**。共享应用窗口或整台显示器时可以一起传声音，获得授权后可用键鼠操作。两台电脑能互传文件和文件夹，双方开启剪贴板同步后，还能同步新复制的纯文本。
- **Android 文件与无线连接**。在电脑上浏览手机目录，可多选文件，在手机和电脑之间双向传输。此次还修复了无线连接空闲或重连后，音频或剪贴板同步容易中断的问题。

[Windows Link 使用指南](docs/windows-link.md) · [查看完整改动](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.33...Beta)

**把每个应用的声音，送到你想要的设备。**

SonicRoute 是常驻托盘的 Windows 音频工具：为应用分别选择输出与输入设备，让音乐走音箱，游戏和通话走耳机。用快捷键、托盘区域滚轮或快速面板控制声音，再把常用操作组合成自动化规则。

📥 [下载正式版](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（实验） · 九种语言 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="SonicRoute 实时演示：将手机应用音频拖入电脑快速面板并切换输出设备"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="SonicRoute 自动化规则编辑器：选择应用触发器与音频操作步骤"></a>
</div>

<sub>界面预览：左侧演示手机与电脑音频拖拽分流及快速切换输出；右侧展示自动化规则编辑器自由编排触发器与执行动作。</sub>

<details>
<summary><sub>🖼️ 展开查看更多界面截图（快速面板 &amp; 手机互联管理）</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute 快速面板：顶部输出设备与各应用音量"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="SonicRoute 手机互联管理界面：Android 设备连接、常用应用固定与虚拟屏音频设置"></a>
</div>

</details>

## ✨ SonicRoute 的特点

- **应用独立声音分流与双向路由（播放 + 录音）**：为每个应用分别指定输出与输入设备，让游戏与语音走耳机和麦克风，音乐走音箱。配置由 Windows 原生持久化，退出软件依然生效，不破坏系统混音器配置。
- **前台焦点智能跟随与托盘滚轮盲操**：系统事件驱动秒级锁定当前前台有声应用；支持在任务栏托盘滚动滚轮盲调音量，按住 Alt 滚动即时换轨设备，配合屏幕浮动 OSD 提示与全局麦克风静音，无需切出窗口。
- **快速面板与实时出声电平指示**：卡片式快速面板支持 350~850px 高度调节与位置保存；直观掌控各应用独立音量，内置实时声音电平指示。
- **Android 互联与自研 AirPlay 音画接收（Full 版）**：支持 Android 音频接入电脑分流调音、独立虚拟屏幕窗口操作（含 H.265 与前台手柄支持），以及电脑声音回传手机；新增自研 C# AirPlay 音频与等比屏幕镜像接收，支持在快速面板切换电脑输出或已连接的 Android 扬声器/蓝牙设备，搭配智能音量（SmartVolume）平衡响度。
- **自动化连招引擎与极速 CLI（6 种触发 × 39 种操作）**：支持应用启停与切换、定时、快捷键等 6 种触发，自由组合设备切换、音量、静音、程序关联启动与 PowerShell；内置 sr 命名管道短命令，无额外常驻开销，轻松联动脚本与硬件控制台。
- **事件优先检测**：使用系统通知，并在需要时以每秒一次的快照检查作为兜底；不需要的数据按需清理。Full 提供可选 Android 手机音频、虚拟屏幕与 AirPlay 集成；Lite 与 Legacy 仅含电脑功能。支持九种语言。

## 🎮 这样使用

把繁琐的手动改设置，变成一次配置、随开随用的自动化体验。 [配置自动化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 你的日常场景 | SonicRoute 怎么帮你解决 |
|---|---|
| 🎮 **游戏开黑，队友太吵或游戏声盖过语音** | 单独调节语音软件（Discord/YY）音量，游戏音效不受影响；游戏走耳机，语音走耳麦，互不串音 |
| 🎵 **边玩游戏边在后台听歌追剧** | 音乐送去音箱，游戏留在耳机；无需切出游戏全屏，鼠标悬停在任务栏托盘直接滚轮调音量或 Alt+滚轮换轨设备 |
| ⚡ **每次开游戏都要插拔耳机、改 Windows 设置** | 设定规则：游戏打开时自动「切换耳机 + 音量 40% + 启动语音软件」，退出游戏自动切回音箱 |
| 🎙️ **临时离开电脑、拿外卖或私密通话** | 一键全局麦克风静音（快捷键 Ctrl+Shift+N），配合屏幕常驻 OSD 浮窗，一眼确认是否闭麦 |
| 📹 **OBS 游戏直播 / 录屏教程** | 为各应用指定独立物理或虚拟轨道，避免录屏时把私人聊天或系统提示音混入录像 |
| 📱 **手机声音接入电脑播放，或在电脑上操作手机应用** | 把 Android 手机声音接入电脑独立调音并在虚拟窗口操作手机应用，或通过 AirPlay 接收 Apple 设备音画投屏；快速面板可直接切换电脑输出或源 Android 扬声器/蓝牙设备，无需频繁看手机 |

## 🚀 下载与开始使用

**当前正式版：[v1.33](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.33)** · 2026-11-18 · [更新说明](docs/release-v1.33.md)

需要可选 Android 手机互联、虚拟屏幕与 AirPlay 功能时选择 **Full x64**；只使用电脑音频功能可选 **Lite x64**；受限环境可选依赖系统 .NET Framework 4.8 的 **Legacy x64**。ARM64 版本为实验性。Full 包含可选手机与 AirPlay 集成，Lite 与 Legacy 不含手机功能；Microsoft Store 提供 Full 与 Lite，当前在架版本以 Store 页面为准。下方直链对应 v1.33。

| 版本 | 下载 | 运行要求 |
|---|---|---|
| ⭐ Full（.NET 10，含可选手机互联） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-arm64.exe) | 单 EXE 便携版；需要匹配架构的 .NET 10 Desktop Runtime，包含可选 Android 手机互联、虚拟屏幕与 AirPlay 集成。 |
| ⭐ Lite（.NET 10，仅电脑音频） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Lite-arm64.exe) | 单文件便携版；需要匹配架构的 .NET 10 Desktop Runtime，仅包含电脑音频功能。 |
| 🛡️ Legacy x64（仅电脑功能，.NET Framework 4.8） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.33/SonicRoute-v1.33-Legacy-x64.zip) | 免装额外运行库，适合受限办公机；依赖系统自带 .NET Framework 4.8，解压即用。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | 提供 Full / Lite，需 x64 .NET 10 Desktop Runtime，在架版本以 Store 为准。 |

<details>
<summary><sub>📦 历史 NET8 兼容版（已停更，保留历史下载，点击展开）</sub></summary>

| 版本 | 下载 | 运行要求 |
|---|---|---|
| Lite NET8（已停更） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（实验）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 已停更，保留历史下载（后续版本不再提供）；建议迁移至 .NET 10 或 Legacy。 |
</details>

> [!NOTE]
> 运行库说明：Full 与 Lite 都需要安装 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；Legacy 依赖系统自带 .NET Framework 4.8，无需额外安装运行库（历史 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 兼容版已停止维护，历史下载保留）。

### 🏁 快速上手三步走

1. **双击启动常驻**：下载解压或双击运行后软件常驻任务栏托盘（推荐从溢出区拖出固定以便随时查看）；双击托盘图标即可打开主窗口。
2. **勾选设备与分流**：首次在「设置 → 保留的设备」勾选常用耳机与音箱；左键单击托盘图标呼出快速面板，滑动应用滑块即时分流设备与调节音量。
3. **托盘盲调与快捷掌控**：鼠标悬停在任务栏托盘区直接滚动滚轮调节应用音量，按住 Alt 滚动即时换轨设备，Shift 滚轮调系统总音量；配合全局静音快捷键或自动化联动，无需切屏即可从容掌控。

### ⚡ 命令行与自动化联动（极客推荐）

`sr` 命令基于命名管道与已运行的 SonicRoute 实例毫秒级通信（软件未运行时不会启动）。首次使用请先启动软件自动注册环境变量，重启终端后即可在脚本、AutoHotkey 或 Stream Deck 旋钮中调用。遇到疑问可查阅 [常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```bash
# 触发指定自动化规则（需 SonicRoute 正在运行）
sr "规则名称或ID"
```

### 🆕 v1.33 版本亮点速览

v1.33 为 Full 新增自研 C# AirPlay 音频与等比屏幕镜像（支持 PCM/ALAC/AAC-LC/AAC-ELD、可选投屏码、置顶/F11 全屏及拖拽转发至电脑或 Android 扬声器/蓝牙）、H.265 (HEVC) 与声画解码器选择、可见设备页按需编码器查询、前台手柄门控、合并二维码（2 分钟自动换码）与配对码卡片、已保存无线设备局域网搜索与统一重连刷新（设备设置重连、快捷设备面板与侧栏刷新统一核对连接、筛掉过期端口并按身份找回新 IP/端口，无候选或失败时保留手动输入草稿）与设备配置恢复；全版本（Full/Lite/Legacy）新增两项独立麦克风常驻 OSD 开关、单行「配置文件管理」卡片、>20 项应用列表虚拟化、OSD 合并优化、PNG 原始字节释放与退役纹理清理、Full 取消关窗显式主动 GC 请求（交由运行时自然回收）、配置 .bak 保护与按需错误诊断、独立 Store Lite 支持及 GPL-3.0-only 离线查看。详见 [版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

### ❓ 常见问题速览

**Q：关闭软件后，应用的分流与音量设置还会保留吗？**

A：Windows 会保留应用路由和现有音频会话。快捷键、托盘控制、自动化以及手机音频、镜像、剪贴板同步需要 SonicRoute 继续运行。

**Q：各版本需要什么运行库？**

A：Full 与 Lite 需要匹配架构的 .NET 10 Desktop Runtime；Legacy 使用 .NET Framework 4.8。Store 包需要 x64 .NET 10 Desktop Runtime。历史 NET8 已停更，旧资产仍可下载。

**Q：软件在后台常驻会占用 CPU 或耗电吗？**

A：检测优先使用系统事件，必要时以每秒一次的快照检查作为兜底；不需要的数据按需清理。实际 CPU 占用取决于系统和使用情况。

## 📚 使用文档

| 想了解什么 | 文档 |
|---|---|
| 安装、面板与常见操作 | [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 默认按键、录制与作用范围 | [快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 触发条件、多步操作与脚本调用 | [自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [命令行](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 安装与功能排障 | [常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 源码、构建与版本记录 | [技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **AI 快速总结与省 Token 排错**：使用 Cursor、Copilot、ChatGPT 或 Claude 等 AI 助手排查问题、理解架构或编写脚本时，推荐直接参考专用机器规范 [README.ai.md](README.ai.md)。该文档以高密度结构化 DSL 整理核心机制与排障决策树，避免大模型扫描冗长图文，大幅节省上下文 Token 开销并秒级定位根因。

## 📌 后续计划

- **音频分流与路由能力进阶（Route Enhancement）**：持续打磨核心音频路由引擎，探索更灵活的应用流向接力、设备热插拔与状态切换时的智能重定向及动态恢复，进一步增强特殊应用与多音频会话场景下的分流稳定性。

[提交建议或问题](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 📜 许可证与授权说明

- **GPL-3.0 开源协议**：SonicRoute 采用 GNU 通用公共许可证第 3 版，仅限该版本（GPL-3.0-only），详见 [LICENSE](LICENSE)。允许商用、销售和收费分发，须遵守 GPL 条款。
- **再分发要求**：保留必要的版权和许可声明，标明修改，并以 GPL 第 3 版分发所涵盖的修改作品；分发所涵盖的二进制文件时，须按 GPL 第 6 条提供完整对应源码，详见 [LICENSE](LICENSE)。
- **适用范围与历史许可**：自 2026-11-17 本次 GPL 转换起，在版权所有者有权授权的范围内，当前版本所涵盖的完整项目（含此前代码）采用 GPL-3.0-only。此前已授予的 MIT 和旧自定义许可权限继续有效，旧标签和已发布二进制文件不因此改标；第三方组件保留各自许可（见 [第三方许可说明](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="关于昨天是否经过明天的讨论"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="点击看板娘发现一个彩蛋"></a>

感谢使用 SonicRoute。 [查看文档](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [反馈问题](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者主页：[哔哩哔哩](https://b23.tv/TDqSAKM) | [爱发电](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
