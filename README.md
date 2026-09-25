# 🎧 音跃 SonicRoute

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE)
[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

更快捷地控制 Windows 每个应用的声音。

SonicRoute 是一个常驻托盘的 Windows 音频控制工具，可以快速控制当前应用的输出/输入设备、音量和静音，并通过快捷键、任务栏滚轮、OSD 和自动化减少反复进入系统设置的操作。

按应用音频路由 · 任务栏滚轮 · 全局快捷键 · 托盘面板 · OSD · 自动化

![预览](docs/images/preview.gif)

**v1.19** ｜ Windows 10 2004+ / Windows 11 · x64 · ARM64 实验 · C# / WPF

📥 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) ｜ [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) ｜ [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

---

## 为什么做 SonicRoute？

玩游戏、看视频或同时使用多个音频应用时，经常需要切换耳机 / 音箱、调整某个应用的音量、切换输入设备。Windows 本身具备这些能力，但这些操作通常需要进入系统音频界面才能完成。

SonicRoute 的出发点是把这些高频操作从系统设置里解放出来——让控制声音像按快捷键一样快，而不是打开一层层菜单。

## 和 Windows 音量合成器有什么不同？

Windows 本身已经可以调整每个应用的音量和输出设备，SonicRoute 提供的是更快捷的交互方式：

- 用快捷键直接操作当前应用，无需切出游戏或窗口
- 鼠标停在任务栏上滚轮即可调整当前应用音量
- 托盘快速面板查看和控制正在出声的应用
- 输出 / 输入设备快速切换
- OSD 即时反馈
- 自动化按条件执行操作

不需要频繁打开系统设置页面。

## ✨ 核心功能

- 🎯 **应用音频路由**：为游戏、浏览器、播放器、语音软件等应用分别选择输出和输入设备；既可以只控制当前正在使用的应用，也可以统一管理所有应用或系统默认设备
- 🖱 **快速控制**：通过托盘快捷面板管理正在运行或出声的应用；鼠标停在任务栏上滚轮即可调整当前应用音量
- ⌨️ **全局快捷键**：不用切出游戏即可调整当前应用音量、静音、切换设备、控制麦克风等
- 🖥 **OSD**：音量、设备切换和麦克风状态通过轻量 OSD 显示，支持位置、尺寸等自定义
- 🤖 **自动化**：支持应用启动、退出、快捷键、定时等触发方式，自动切换设备、调整音量、静音或启动程序
- 🧹 **轻量常驻**：按需加载界面，并尽量减少后台资源占用
- 🎨 **个性化与设备管理**：设备保留 / 改名、主题、透明度、多语言以及语言文件导入导出

具体操作方式与完整设置项见 [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)。

## 🚀 快速开始

🛍 **推荐**：从 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)（x64，自动更新）安装。

也可以从 [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) 下载：

| 版本 | 说明 |
|---|---|
| ⚡ Lite x64 | 主要便携版本，需 .NET 8 Desktop Runtime |
| ⚡ Lite ARM64（实验） | 需 .NET 8 Desktop Runtime；实验版本，尚未完成 ARM64 真机完整验证 |
| 🪟 Legacy x64 | 使用 .NET Framework 4.8，无需安装 .NET 8 |

> v1.19 Release Assets 中仍保留旧的自包含版本；自 v1.20 起不再提供这种发布形式。

## 📚 完整文档 → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

[📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)（安装 / 面板 / 完整界面 / 场景教程）｜ [✨ 功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) ｜ [⌨️ 快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) ｜ [❓ 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) ｜ [🛠 技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) ｜ [📌 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)（版本规则 + 更新日志）

## 📌 版本相关

完整版本历史（版本规则 + 更新日志）见 [Wiki 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)。

**下一版本**：持续优化使用体验与维护 bug；当前重心为 ARM64 测试与 Legacy 优化，大版本更新暂无明确时间表。

💡 有建议或反馈？→ [提交建议](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)

---

## 🧩 其他作品

- 🎧 **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** — 极简、静默、低资源占用的 Windows 音频播放器｜[与音跃配合使用](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)（拼出按键音效板，类似 Soundpad）

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
