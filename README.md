<!-- Generated from docs/readme/shared.json, docs/readme/locales/zh-CN.json, and docs/readme/releases/v1.35.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 音跃 SonicRoute v1.35 Beta

**简体中文** | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Beta：v1.35](https://img.shields.io/static/v1?label=Beta&message=v1.35&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.35...Beta) [![许可证：GPL-3.0](https://img.shields.io/static/v1?label=%E8%AE%B8%E5%8F%AF%E8%AF%81&message=GPL-3.0&color=blue)](LICENSE) [![系统：Windows 10 | 11](https://img.shields.io/static/v1?label=%E7%B3%BB%E7%BB%9F&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![架构：x64 | ARM64](https://img.shields.io/static/v1?label=%E6%9E%B6%E6%9E%84&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![下载量](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=%E4%B8%8B%E8%BD%BD%E9%87%8F)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![在爱发电赞助](https://img.shields.io/static/v1?label=&message=%E5%9C%A8%E7%88%B1%E5%8F%91%E7%94%B5%E8%B5%9E%E5%8A%A9&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

## Beta 版有哪些变化（与正式版 v1.35 相比）？

- **配对两台 Windows 电脑。** 在两台电脑的 SonicRoute Full 中打开“应用 → 添加设备 → Windows Link”，搜索同一局域网内的电脑，或手动输入对方的地址。核对两台电脑显示的 8 位配对码，然后分别确认。
- **跨电脑播放声音。** 把应用拖到另一台电脑，选择只在对方电脑播放，或让两台电脑同时播放。还可以发送整台电脑的声音，或为同一音源选择多个输出。
- **共享画面、文件和剪贴板。** 共享窗口或显示器时可以同时传送声音，允许对方使用键盘和鼠标后，对方即可操作共享画面。两台电脑可以互传文件和文件夹。双方都开启剪贴板同步后，还可共享新复制的纯文本。

[Windows Link 使用指南](docs/windows-link.md) · [查看完整改动](https://github.com/kunkunkunQoQ/SonicRoute/compare/v1.35...Beta)

**让每个应用的声音从你选的设备播放。**

让音乐从音箱播放，让游戏和通话走耳机。SonicRoute 常驻 Windows 任务栏的通知区域，可为每个应用分别选择播放和录音设备，也能让同一个应用通过多个设备同时播放。按快捷键、在托盘图标上滚动鼠标滚轮，或打开快速面板，都能调节音量。常用操作可以保存为自动化规则。

📥 [下载正式版](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64（实验版） · 九种语言 · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="将手机应用拖到电脑的快速面板，展开应用行切换输出，再拖回手机"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="自动化规则编辑器：触发条件为按下快捷键，操作菜单中选中了“设置系统默认播放设备”"></a>
</div>

<sub>左图演示如何把手机应用的声音拖到电脑上播放，再切换输出设备。右图展示如何为快捷键规则选择要执行的音频操作。</sub>

<details>
<summary><sub>🖼️ 更多截图：快速面板和手机管理</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="快速面板顶部显示输出设备，下方可分别调节各应用的音量"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="手机管理页面中的 Android 连接、固定应用和虚拟屏幕音频设置"></a>
</div>

</details>

## ✨ 主要功能

- **按应用选择播放和录音设备。** 游戏用耳机，音乐用音箱，通话应用还可以单独选择麦克风。在快速面板中，按住 Ctrl 单击可选中或取消选中一个播放设备，按住 Shift 单击可选中一段连续的设备。所有版本都能让同一个应用同时使用多个电脑输出，Full 还可加入可用的手机输出。Windows 会记住应用的主播放设备和录音设备。要在其他输出上同时播放，需要保持 SonicRoute 运行。
- **用滚轮控制当前应用。** 默认优先选择最近在前台使用且有音频会话的应用。在托盘图标上滚动即可调节音量，按住 Alt 滚动可切换该应用的输出。屏幕提示（OSD）会显示操作结果，也可以用全局快捷键切换麦克风静音。
- **在快速面板查看各应用的音量。** 滑块可单独调节应用音量，实时电平表能看出哪个应用正在发声。面板位置可保存，高度可在 350 至 850 个逻辑像素之间调整，实际高度受屏幕可用空间限制。
- **Android 手机互联与 AirPlay 接收（Full）。** 可以在电脑上播放 Android 应用的声音，也能将电脑声音传到手机。当前开发版支持按手机设置传到电脑的音频格式（默认 Opus，也可选未压缩的 PCM）和延迟模式（超低延迟、平衡或稳定，默认为平衡）。这些设置在下次开始传输音频时生效。如果编码器支持，开发版还会根据连接状况自动调整虚拟屏幕的视频码率。Android 应用可在独立虚拟屏幕中运行，支持 H.265 视频。视频窗口获得焦点时可用手柄操作。内置 C# AirPlay 接收器可播放音频并按原比例显示屏幕镜像。在快速面板中可将声音送到电脑、Android 手机扬声器或与手机连接的蓝牙设备。智能音量（SmartVolume）可减小响度波动。还可以从电脑浏览 Android 手机文件夹，并双向传输多个文件。
- **自动化规则和命令行。** 支持 6 种触发条件：按下快捷键、应用启动、切换到应用、应用退出、到达预定时间、SonicRoute 启动。可从 39 种操作中组合切换设备、调节音量、静音、启动程序或运行 PowerShell 脚本等步骤。通过 `sr` 可从脚本或控制台执行已保存的规则。
- **按需检测。** 优先使用系统事件取得状态变化，必要时每秒检查一次快照，并按需清理数据。Full 提供可选的手机与 AirPlay 功能，Lite 和 Legacy 专用于电脑。三个版本都支持九种界面语言。

## 🎮 使用场景

例如，为游戏启动和退出分别设置规则，让 Windows 随游戏切换播放设备。 [配置自动化](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| 场景 | 可以这样做 |
|---|---|
| 🎮 **游戏声音盖过队友的语音** | 单独调高 Discord 或 YY 的音量，或调低游戏音量。也可以让游戏和语音分别使用不同的播放设备。 |
| 🎵 **玩游戏时听音乐或看视频** | 让音乐从音箱播放、游戏从耳机播放。需要调整某个应用时，可在快速面板中直接选择它。 |
| ⚡ **启动游戏时要切换好几项设置** | 为游戏启动设置一条规则：切换到耳机、将音量设为 40%、打开语音软件。再为游戏退出设置一条切回音箱的规则。 |
| 🎙️ **通话中需要暂时关闭麦克风** | 默认按 Ctrl+Shift+N 可将所有麦克风静音，再按一次恢复。开启常驻屏幕提示后，静音期间可随时查看状态。 |
| 📹 **用 OBS 直播游戏或录制教程** | 将游戏和聊天分配给不同的物理或虚拟输出设备，再在 OBS 中选择要录制的音频源。 |
| 📱 **在电脑上听手机音频或操作手机应用** | Full 可播放 Android 音频、在独立虚拟屏幕中操作手机应用，也可接收 Apple 设备的 AirPlay 音频和屏幕镜像。在快速面板中选择电脑输出、手机扬声器或与手机配对并已连接的蓝牙设备。 |

## 🚀 下载与开始使用

**当前正式版：[v1.35](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.35)** · 2026年11月21日 · [更新说明](docs/release-v1.35.md)

想在电脑上播放 Android 手机的声音、使用虚拟屏幕或接收 AirPlay 投屏，请选 **Full**。只需电脑音频功能，可选 **Lite**。已有 .NET Framework 4.8 的 x64 电脑也可使用 **Legacy**。ARM64 构建为实验版。下表列出 v1.35 的下载链接，Microsoft Store 的 Full 和 Lite 版本以各自的商店页面为准。

| 版本 | 下载 | 运行要求 |
|---|---|---|
| ⭐ Full（.NET 10，含可选手机互联） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-x64.exe) / [ARM64（实验版）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-arm64.exe) | 单个便携 EXE。需要与程序架构匹配的 .NET 10 Desktop Runtime，可选用 Android 手机互联、虚拟屏幕和 AirPlay。 |
| ⭐ Lite（.NET 10，仅电脑音频） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-x64.exe) / [ARM64（实验版）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Lite-arm64.exe) | 单个便携 EXE。需要与程序架构匹配的 .NET 10 Desktop Runtime。 |
| 🛡️ Legacy x64（仅电脑功能，.NET Framework 4.8） | [完整 ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.35/SonicRoute-v1.35-Legacy-x64.zip) | 解压后运行。需要 .NET Framework 4.8，系统已安装时无需额外安装运行库。 |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full 和 Lite 分别上架，均需要 x64 版 .NET 10 Desktop Runtime。 |

<details>
<summary><sub>📦 旧版 .NET 8 下载（已停止更新）</sub></summary>

| 版本 | 下载 | 运行要求 |
|---|---|---|
| Lite NET8（已停更） | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64（实验版）](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | 仅保留历史下载。如需继续获取更新，可改用 .NET 10 版或 Legacy。 |
</details>

> [!NOTE]
> Full 和 Lite 需要与程序架构匹配的 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。Legacy 使用系统中的 .NET Framework 4.8。使用 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) 的旧版已停止更新，仍可下载。

### 🏁 快速上手

1. **启动 SonicRoute。** 运行下载的 EXE。使用 Legacy 时，先将 ZIP 完整解压。双击通知区域的 SonicRoute 图标可打开主窗口。
2. **选择设备。** 在设置中勾选要在快速面板显示的耳机和音箱。单击托盘图标打开快速面板，展开应用行选择播放设备，拖动滑块调节该应用的音量。
3. **使用滚轮。** 把指针移到 SonicRoute 托盘图标上，滚动调节当前应用音量，按住 Alt 滚动切换该应用的输出，按住 Shift 滚动调节系统默认播放设备的音量。也可以在设置中将滚轮操作扩展到整个通知区域。

### ⚡ 从命令行执行规则

首次启动 SonicRoute 后，关闭并重新打开终端，即可使用 `sr` 按名称或 ID 执行规则。它通过命名管道连接正在运行的 SonicRoute，可用于脚本、AutoHotkey 或 Stream Deck。如果 SonicRoute 尚未运行，命令会返回错误。详见[常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98)。

```powershell
# 触发指定自动化规则（需 SonicRoute 正在运行）
sr "规则名称或 ID"
```

### 🆕 v1.35 更新要点

在快速面板中为电脑应用选择麦克风时，所选设备会立即高亮，切换期间仍可继续点选，SonicRoute 会按顺序应用更改。Full 版中只支持单个输出的外部音源也有相同反馈。切换输出时，应用行中的设备名称会立即更新。设备切换快捷键可连续使用。用快捷键切换当前应用的输出时，若在 45 毫秒内完成，屏幕只显示结果。如果先出现切换进度提示，成功结果会等提示显示至少 500 毫秒后再出现，失败则立即提示。当 Windows 将应用的音频会话移到另一个已选输出时，其余已选输出也会继续播放。

Full 版在 Android 音频采集模式切换期间仍可更改输出设备，虚拟屏幕也能继续响应操作。关闭某台手机的剪贴板同步后，该手机尚未完成的电脑剪贴板写入会被取消，其他手机可继续同步。同步失败时，SonicRoute 也会释放为写入剪贴板分配的内存。早期版本的改动见[版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

<details>
<summary>v1.34</summary>

各版本现在都可以为同一个应用选择多个电脑播放设备。重启 SonicRoute 后，应用再次播放时会恢复所选设备。托盘音量调节和鼠标快捷键共用一个系统鼠标监听。

Full 的两个 Android 面板同步显示 USB 和 Wi-Fi 连接状态。快捷手机面板的「刷新」也能重新连接选中的设备。Android 应用的输出菜单现在会显示手机当前输出的自定义名称。早期版本详见[版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2)。

</details>

### ❓ 常见问题

**退出 SonicRoute 后，设备选择和应用音量还会保留吗？**

Windows 会保留应用的主播放设备、录音设备以及现有音频会话的音量。要同时在多个设备上播放，或使用快捷键、托盘控制、自动化、手机音频、屏幕镜像和剪贴板同步，需要保持 SonicRoute 运行。

**各版本需要安装哪种运行库？**

Full 和 Lite 需要与程序架构匹配的 .NET 10 Desktop Runtime，Microsoft Store 版使用 x64 运行库。Legacy 使用 .NET Framework 4.8。旧的 .NET 8 版仍可下载，现已停止维护。

**SonicRoute 在后台运行时会持续占用 CPU 吗？**

SonicRoute 主要依靠系统事件检测变化，必要时才每秒检查一次快照，并按需清理数据。实际 CPU 占用取决于系统和正在使用的功能。

## 📚 使用文档

| 想了解什么 | 文档 |
|---|---|
| 安装、面板与常见操作 | [使用指南](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [功能特性](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| 默认按键、录制与作用范围 | [快捷键](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| 触发条件、多步操作与脚本调用 | [自动化规则](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [命令行](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| 安装与功能排障 | [常见问题](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| 源码、构建与版本记录 | [技术实现](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [版本相关](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [版本历史](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **供 AI 助手查阅的项目索引**：使用 Cursor、Copilot、ChatGPT 或 Claude 排查问题或编写脚本时，可先提供 [README.ai.md](README.ai.md)，其中整理了主要功能的实现位置、命令参数和排查步骤。

## 📌 后续计划

- **应用音频路由。** 计划改进设备插拔或状态变化后的输出切换与恢复，并提高特殊应用和多音频会话场景下的路由稳定性。

[提交建议或问题](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose)。

## 💖 支持 SonicRoute

SonicRoute 是一个持续维护的免费开源项目。如果它让你在电脑上听音乐、玩游戏或通话更方便，欢迎通过爱发电赞助，支持新功能开发、兼容性改进和长期维护。

☕ [在爱发电赞助](https://afdian.com/a/koukou021)

赞助完全自愿，所有功能都可以免费使用。反馈问题、向朋友推荐 SonicRoute，或在 GitHub 上给项目点个 Star，也是在支持项目。谢谢你！

## 📜 许可证

- **GPL-3.0 开源协议**：SonicRoute 采用 GNU 通用公共许可证第 3 版，仅限该版本（GPL-3.0-only），详见 [LICENSE](LICENSE)。允许商用、销售和收费分发，须遵守 GPL 条款。
- **再分发要求**：保留必要的版权和许可声明，标明修改，并以 GPL 第 3 版分发所涵盖的修改作品；分发所涵盖的二进制文件时，须按 GPL 第 6 条提供完整对应源码，详见 [LICENSE](LICENSE)。
- **适用范围与历史许可**：自 2026-11-17 本次 GPL 转换起，在版权所有者有权授权的范围内，当前版本所涵盖的完整项目（含此前代码）采用 GPL-3.0-only。此前已授予的 MIT 和旧自定义许可权限继续有效，旧标签和已发布二进制文件不因此改标；第三方组件保留各自许可（见 [第三方许可说明](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)）。

---

<a href="EASTER_EGGS.md" title="关于昨天是否经过明天的讨论"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="点击看板娘发现一个彩蛋"></a>

感谢使用 SonicRoute。 [查看文档](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [反馈问题](https://github.com/kunkunkunQoQ/SonicRoute/issues)。

**作者主页：[哔哩哔哩](https://b23.tv/TDqSAKM) | [爱发电](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 AI 助手参考资料</a></sub></div>
