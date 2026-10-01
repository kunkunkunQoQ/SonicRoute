# 🎧 音跃 SonicRoute

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE)
[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![爱发电](https://img.shields.io/badge/赞助-爱发电-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

更快捷地控制 Windows 每个应用的声音。

SonicRoute 是一个常驻托盘的 Windows 音频控制工具，可以快速控制当前应用的输出 / 输入设备、音量和静音，并通过快捷键、任务栏滚轮、OSD 和自动化减少反复进入系统设置的操作。

按应用音频路由 · 任务栏滚轮 · 全局快捷键 · 托盘面板 · OSD · 自动化

![预览](docs/images/preview.gif)

**v1.20 预览版已发布** ｜ 稳定版 v1.19 ｜ Windows 10 2004+ / Windows 11 · x64 · ARM64 实验 · C# / WPF

📥 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) ｜ [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) ｜ [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

> 🧪 **v1.20 Preview** 已在 GitHub Releases 提供。它重点改进快速面板、OSD、自动化和后台性能；稳定版目前仍为 v1.19。

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
- 🖥 **OSD**：音量、设备切换和麦克风状态通过轻量 OSD 显示，支持位置、尺寸、音量进度条等自定义
- 🤖 **自动化**：支持应用启动 / 退出、切换应用、快捷键、定时等触发方式，可切换设备、调整音量、静音、控制麦克风或启动程序
- 📊 **实时音量电平**：v1.20 预览版可在快速面板显示每个应用的实时声音电平，并可在主题设置中关闭
- 🧹 **轻量常驻**：界面按需加载，并持续优化空闲轮询、缓存、OSD 更新和后台资源占用
- 🎨 **个性化与设备管理**：设备保留 / 改名、主题、透明度、多语言以及语言文件导入导出

具体操作方式与完整设置项见 [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)。

v1.20 预览版还支持在任意目录使用 `sr "规则名称或ID"` 调用正在运行的 SonicRoute 执行自动化规则，详见 [命令行调用说明](docs/automation-command-line.md)。

## 🧪 v1.20 预览版

v1.20 是一次以**体验重构和性能优化**为主的更新。目前 GitHub 预览版已发布，正式稳定版会在预览测试与问题收敛后推出，不再以此前预告的“国庆中旬”作为固定上线日期。

### 快速面板

- 新增**每应用实时音量电平**，声音变化可直接显示在应用音量条中，并可在主题设置中关闭
- 改进快速面板加载、更多选项展开、设备栏复用与入场动画，减少展开 / 切换时的闪动和重复创建
- 简洁面板默认高度调整为 **450px**
- 优化深色 / 浅色模式下的可见度和细节表现

### OSD

- 重做音量 OSD 的更新与动画逻辑，连续滚轮 / 快捷键调音量时更稳定
- 新增可关闭的**音量进度条**，开启时音量数字移动到左侧
- 优化多屏定位、布局稳定性、动画衔接和重复动画开销
- 调整强调色、标题样式与 OSD 外观设置排版

### 自动化

- 新增**手动执行规则**
- 支持复制规则与步骤
- 新增**软件启动**触发方式
- 新增明确静音 / 取消静音、相对音量调整、全局麦克风控制等操作
- 操作菜单重新分组，并改进步骤拖动、边缘滚动和换位手感
- 新增命令行调用：`sr "规则名称或ID"`，可从脚本、快捷方式或其他程序触发规则

### 性能与响应

- 优化自动化进程查询、麦克风静音兜底轮询、空闲内存回收与应用图标缓存
- 优化实时电平刷新，减少没有必要的后台更新
- 优化 Legacy 设置 / 自动化切页，以及滚轮、快捷键和 OSD 的响应路径
- 合并和去重 OSD 高频更新，减少连续调音量时的重复动画与 UI 开销

> 当前预览版的三种发布资产均已完成构建检查。ARM64 仍为实验版本，尚未完成 ARM64 真机完整验证；本轮性能优化也尚未进行完整运行时基准测量。

## 🚀 快速开始

🛍 **稳定使用推荐**：从 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT)（x64，自动更新）安装当前稳定版。

想体验最新功能，可以从 [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.20) 下载 **v1.20 预览版**：

| 版本 | 说明 |
|---|---|
| ⚡ Lite x64 | 主要便携版本；单 EXE，需 x64 .NET 8 Desktop Runtime |
| 🧪 Lite ARM64（实验） | 单 EXE，需 ARM64 .NET 8 Desktop Runtime；尚未完成 ARM64 真机完整验证 |
| 🪟 Legacy x64 | .NET Framework 4.8 版本；解压完整目录后运行，无需安装 .NET 8 |

> v1.19 Release Assets 中仍保留旧的自包含版本；自 v1.20 起不再提供这种发布形式。
>
> Microsoft Store 的 v1.20 x64 包已经完成本地打包与基础校验，但当前尚未提交 Partner Center；商店版本以上架状态为准。

## 📚 完整文档 → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

[📖 使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)（安装 / 面板 / 完整界面 / 场景教程）｜ [✨ 功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) ｜ [⌨️ 快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) ｜ [❓ 常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) ｜ [🛠 技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) ｜ [📌 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)（版本规则 + 更新日志）

## 📌 版本计划

- **当前稳定版：v1.19**
- **当前预览版：v1.20 Preview**（2026-10-01 已发布至 GitHub Releases）
- **v1.20 正式版**：将在预览测试与问题收敛后发布，不再固定承诺“10 月中旬上线”
- **年底大版本**：仍按计划保留，具体版本号与内容以后续公告为准

完整版本历史（版本规则 + 更新日志）见 [Wiki 版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3)。

💡 有建议或反馈？→ [提交建议](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)

---

## 🧩 其他作品

- 🎧 **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** — 极简、静默、低资源占用的 Windows 音频播放器
- 💡 与音跃配合可拼出按键音效板（类似 Soundpad）→ [组合场景](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)
- 📦 **[WinAudioRoute](https://github.com/kunkunkunQoQ/WinAudioRoute)** — 音跃的 Windows 音频控制库（.NET）：设备 / 会话 / 按应用路由 / 事件 / CLI

---

**作者主页 ‖ [哔哩哔哩](https://b23.tv/TDqSAKM) ‖ [爱发电](https://www.ifdian.net/a/koukou021) ‖ [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
