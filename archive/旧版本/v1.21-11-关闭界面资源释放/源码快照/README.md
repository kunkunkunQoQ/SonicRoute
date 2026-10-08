# 🎧 音跃 SonicRoute

**简体中文** | [English](README.en.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE)
[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

更快捷地控制 Windows 每个应用的声音。

SonicRoute 是一款常驻托盘的 Windows 音频工具，支持按应用切换输入 / 输出设备、调整音量和静音。通过快捷键、任务栏滚轮、快速面板、OSD 与自动化，让常用操作更简单。

![预览](docs/images/preview.gif)

Windows 10 2004+ / Windows 11 · x64 / ARM64（实验） · C# / WPF

📥 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) ｜ [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) ｜ [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

---

## 为什么选择 SonicRoute？

Windows 已提供音量合成器，但切换设备、调整单个应用音量等操作仍可能需要打开设置。SonicRoute 将这些高频操作集中到快捷键、任务栏与托盘中，尤其适合希望在游戏或全屏应用中快速操作、不切换窗口的场景。

## ✨ 核心功能

- 🎯 **应用音频路由**：独立切换当前应用、全部运行中应用或系统默认的输出 / 输入设备
- 🖱 **快速控制**：托盘快速面板集中控制音量、静音和设备，并显示应用实时声音活动，支持快捷面板 / 应用面板
- ⌨️ **快捷键与任务栏滚轮**：无需切出当前窗口即可调音量、切设备、静音或控制麦克风
- 🖥 **OSD 提示**：即时显示操作结果，外观、位置、音量条和麦克风静音提示均可自定义
- 🤖 **自动化与命令行**：按应用事件、快捷键、定时或启动执行多步规则，也可用 `sr "规则名称或ID"` 直接调用
- 🎨 **外观与个性化**：深浅主题、九种语言、设备筛选与自定义名称

详细设置与使用方法参见 [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)。

## 🚀 快速开始

- **Microsoft Store**：[商店安装](https://apps.microsoft.com/detail/9NQZGRTPM1NT)，支持自动更新（x64）。
- **GitHub**：从 [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载最新版本。

| 版本 | 说明 |
|---|---|
| ⚡ Lite x64 | 单 EXE，需安装 x64 .NET 8 Desktop Runtime |
| 🧪 Lite ARM64（实验） | 单 EXE，需安装 ARM64 .NET 8 Desktop Runtime；尚未完成 ARM64 真机完整验证 |
| 🪟 Legacy x64 | .NET Framework 4.8；解压完整目录后运行，无需安装 .NET 8 |

## 📚 完整文档 → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

| 文档 | 内容 |
|---|---|
| [01 · 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) | 安装、面板、完整界面与场景教程 |
| [02 · 功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) | 功能介绍与控制范围 |
| [03 · 快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) | 默认按键、绑定和使用场景 |
| [04 · 自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) | 六种触发、十七种操作、复制与排序 |
| [05 · 命令行执行规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) | sr 用法、执行结果与退出码 |
| [06 · 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) | 安装、设备、音量、自动化与性能排查 |
| [07 · 技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) | 源码结构、音频接口与构建方法 |
| [08 · 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) | 下载方式、版本说明与发布规则 |
| [09 · 版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) | 旧版更新日志与版本索引 |

## 📌 更新计划

后续改进将分阶段持续推出，不再集中到单次大版本发布。

- **v1.21 · UI 优化**：优化布局、视觉细节、动画流畅度和操作路径。
- **v1.22 · 体验打磨**：改善快速面板、OSD 与全屏操作体验。
- **v1.23 · 性能与稳定性**：优化后台资源占用、响应速度及兼容性。
- **后续阶段**：根据反馈持续完善现有功能。

v1.21 工作区进展见 [主题与设置界面](docs/theme-settings.md) 和 [自动化功能说明](docs/automation-convenience.md)（含规则自动命名）。具体更新内容与时间以实际发布为准。

💡 有建议或反馈？→ [提交建议](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)

---

## 🧩 其他作品

- 🎧 **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** — 极简、静默、低资源占用的 Windows 音频播放器
- 💡 与音跃配合可拼出按键音效板（类似 Soundpad）→ [组合场景](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)
- 📦 **[WinAudioRoute](https://github.com/kunkunkunQoQ/WinAudioRoute)** — 音跃的 Windows 音频控制库（.NET）：设备 / 会话 / 按应用路由 / 事件 / CLI

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
