# 🎧 SonicRoute

[简体中文](README.md) | **English**

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE)
[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Support on Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

Faster control of audio for every Windows app.

SonicRoute is a Windows audio utility that lives in the system tray. Route audio for individual apps, adjust volume, or mute audio with hotkeys, taskbar scrolling, a quick panel, OSD feedback, and automation.

![Preview](docs/images/preview.gif)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · C# / WPF

📥 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) | [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

---

## Why SonicRoute?

Windows already has a volume mixer, but frequently switching devices or controlling a particular app can mean opening system settings. SonicRoute puts common audio controls in hotkeys, the taskbar, and the tray — particularly useful when gaming or working in fullscreen without switching windows.

## ✨ Core features

- 🎯 **Per-app audio routing**: Switch output/input devices for the current app, all running apps, or Windows defaults.
- 🖱 **Quick controls**: Use the tray panel for volume, mute, device switching, and live audio activity, with Quick and App panel layouts.
- ⌨️ **Hotkeys and taskbar wheel**: Adjust volume, switch devices, mute audio, or control the microphone without leaving the current window.
- 🖥 **OSD feedback**: Show instant operation feedback with customizable appearance, position, volume bar, and microphone mute notice.
- 🤖 **Automation and command line**: Run multi-step rules on app events, hotkeys, schedules, or startup, or trigger them with `sr "Rule name or ID"`.
- 🎨 **Appearance and personalization**: Light/dark themes, nine languages, device filtering, and custom names.

See the [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki) for settings and detailed instructions (currently in Chinese).

## 🚀 Quick start

- **Microsoft Store**: [Install from the Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) with automatic updates (x64).
- **GitHub**: Download the latest version from [Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases).

| Version | Requirements |
|---|---|
| ⚡ Lite x64 | Single EXE; requires the x64 .NET 8 Desktop Runtime |
| 🧪 Lite ARM64 (experimental) | Single EXE; requires the ARM64 .NET 8 Desktop Runtime; not yet fully validated on ARM64 hardware |
| 🪟 Legacy x64 | .NET Framework 4.8; extract the entire folder; .NET 8 is not required |

### v1.21 · 2026-10-09

[View the v1.21 release](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.21) · [Release notes](docs/release-v1.21.md)

- Refines the UI, theme settings, and automation workflow. Automation supports six triggers and 18 actions, including the new “Execute another rule” action.
- Foreground app and microphone state updates are event-driven by default. The experimental background polling fallback is off by default and takes effect immediately when toggled. The app does not force garbage collection.
- Adds a new transparent headphone icon and uses `SonicRoute` in the main window and both quick panels.

| v1.21 asset | Download |
|---|---|
| Lite x64 | [SonicRoute-v1.21-Lite-x64.exe](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Lite-x64.exe) |
| Lite ARM64 (experimental) | [SonicRoute-v1.21-Lite-arm64.exe](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Lite-arm64.exe) |
| Legacy x64 | [SonicRoute-v1.21-Legacy-x64.zip](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.21/SonicRoute-v1.21-Legacy-x64.zip) |

Lite builds require the matching .NET 8 Desktop Runtime. ARM64 has not yet been fully validated on physical hardware. Extract the complete Legacy archive before running it. You can also install and update through the [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT).

## 📚 Full documentation → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

The following Wiki pages are in Chinese.

| Guide | Contents |
|---|---|
| [01 · User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) | Installation, panels, the main window, and usage examples |
| [02 · Features](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) | Features and what each control affects |
| [03 · Hotkeys](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) | Default keys, custom bindings, and usage examples |
| [04 · Automation rules](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) | Six triggers, 18 actions, copying, and step ordering |
| [05 · Execute rules from the command line](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) | sr usage, execution results, and exit codes |
| [06 · FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) | Installation, devices, volume, automation, and performance troubleshooting |
| [07 · Technical implementation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) | Source structure, audio interfaces, and build instructions |
| [08 · Release information](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) | Downloads, release notes, and guidelines |
| [09 · Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) | Earlier changelogs and the version index |

## 📌 Roadmap

Future improvements will be delivered in stages rather than held for one major release.

- **v1.22 · Experience refinements**: Improve the quick panel, OSD, and fullscreen interactions.
- **v1.23 · Performance and stability**: Improve background resource usage, responsiveness, and compatibility.
- **Later stages**: Continue refining existing features based on feedback.

See the [theme and settings guide (Chinese)](docs/theme-settings.md) and [automation guide (Chinese)](docs/automation-convenience.md) for v1.21 details. Later plans may change based on feedback.

💡 Have an idea or feedback? [Open an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

---

## 🧩 Other projects

- 🎧 **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** — A minimal Windows audio player with low resource use.
- 💡 Pair it with SonicRoute to create a soundboard controlled by hotkeys, similar to Soundpad. See the [combined setup guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad).
- 📦 **[WinAudioRoute](https://github.com/kunkunkunQoQ/WinAudioRoute)** — SonicRoute's .NET library for Windows audio control: devices, sessions, audio routing for each app, events, and a CLI.

---

**Author: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
