<!-- Generated from docs/readme/shared.json and docs/readme/locales/en-US.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | **English** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) [![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Support on Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**Send each app's audio to the device you choose.**

Keep music on your speakers and games or calls on your headset. SonicRoute is a Windows audio utility that lives in the system tray: choose output and input devices for individual apps, control audio with hotkeys, the tray wheel or a panel, and combine routine actions into automation rules.

📥 [Download the latest release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nine languages · MIT licensed

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="User guide (Chinese)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Click the mascot to open the user guide"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute Quick panel: output device and per-app volume controls"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute v1.23 automation rule editor: app trigger and audio action selection"></a>
</div>

<sub>Interface examples: Quick panel for app volume and devices; automation editor for combining triggers and action steps.</sub>

## ✨ What SonicRoute offers

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Features"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Click the mascot to explore SonicRoute features"></a>

- **Automatic foreground-app detection.** By default, controls follow the audio app you’re using. With the panel closed, use the same hotkeys to change its volume, mute it, or switch devices, or scroll over the system tray to adjust volume—no need to find the app first.
- **Quick panel.** Click the tray icon to manage app volumes in one place. Click an app’s icon to mute it or expand its device section to choose headphones or speakers. Control music players, games, and voice chat apps separately.
- **Automation rules.** On the Automation page, arrange device switches, volume changes, and program launches in order, with delays if needed. Save and enable the rule, then run it with a hotkey, when an app starts or gains focus, or on a schedule instead of repeating the steps yourself.

## 🎮 Everyday uses

Teammates too loud? Lower just the voice chat app’s slider and leave the game’s volume alone.

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automation"><img src="docs/images/mascot-automation-gb.png" width="48" align="middle" alt="Click the little robot to learn about automation rules"></a> For example, set a game-start rule to send the game’s audio to headphones → set the game’s volume to 50% → open voice chat, then enable the rule. [Set up automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

## 🚀 Download and get started

**Current release: [v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [Release notes (Chinese)](docs/release-v1.24.md)

For most Windows x64 users, the Microsoft Store build offers convenient installation and automatic updates. Choose Lite x64 for a single executable, or Legacy x64 if installing .NET 8 is not convenient. The download link above always points to the latest release; Lite and Legacy links below are for v1.24, while the Store page shows the version currently available there.

| Version | Download | Requirements |
|---|---|---|
| Lite x64 | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | Matching x64 .NET 8 Desktop Runtime |
| Lite ARM64 (experimental) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | Matching ARM64 .NET 8 Desktop Runtime |
| Legacy x64 | [Complete ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | .NET Framework 4.8; extract the entire archive |
| Microsoft Store | [Install from the Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | x64; automatic updates; the available version is shown on the Store |

Lite requires the matching [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Legacy does not require .NET 8.

1. Start SonicRoute, find its icon in the Windows notification area (it may be under hidden icons), and double-click it to open the full interface.
2. Select your preferred output and input devices in Settings.
3. Click the tray icon to open a panel, or set up your hotkeys. Create automation rules when you want to combine actions.

`sr` connects only to an already-running SonicRoute instance; it does not start the app. Before first use, start SonicRoute normally to register the command, then close and reopen your terminal. See the [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) if device switching does not work as expected.

Version 1.24 adds starter templates for automation, optional recent execution details (off by default, with a storage limit), and per-app icon imports. Missing or generic app icons fall back to the first character of the app’s name. Configuration backups include imported icons. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for earlier changes.

## 📚 Documentation

The Wiki is currently in Chinese.

| Topic | Guides |
|---|---|
| Installation, panels and everyday controls | [User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Features](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Default keys, bindings and control targets | [Hotkeys](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Triggers, actions and script integration | [Automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Command line](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation and troubleshooting | [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Source, builds and releases | [Technical implementation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Release information](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

## 📌 Roadmap

- **v1.25 · Major update:** Details will be shared in the upcoming release notes.
- **Later stages:** Keep refining existing features based on feedback. Plans may change as feedback comes in.

[Share an idea or report an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 🧩 Other projects and combinations

- **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)**: A Windows audio player. Combine it with the Launch program action to play sound effects from hotkeys. [Setup guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad)

<a href="https://github.com/kunkunkunQoQ/XiaoYue-Comics" title="XiaoYue Comics"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="Click XiaoYue peeking out of an eggshell with a comic book to read the series"></a>

**[XiaoYue Comics](https://github.com/kunkunkunQoQ/XiaoYue-Comics)**

An absurd magical-girl comedy about a girl who hatches from an egg and makes a mess whenever she tries to help.

<br clear="all">

---

<a href="EASTER_EGGS.md" title="An Easter egg: yesterday and tomorrow"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Click the resting mascot to discover an Easter egg"></a>

Thanks for using SonicRoute. [Read the guides (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Report an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Author: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**
