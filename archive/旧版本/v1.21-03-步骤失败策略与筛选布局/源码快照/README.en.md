# 🎧 SonicRoute

[简体中文](README.md) | **English**

[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE)
[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases)
[![Support on Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

Faster control of audio for every Windows app.

SonicRoute is a Windows audio control tool that lives in the system tray. Quickly change an app's output or input device, volume, and mute state. Hotkeys, tray wheel controls, onscreen notifications (OSD), and automation make everyday audio changes easier.

Audio routing for each app · Tray wheel controls · Global hotkeys · Quick panel · OSD · Automation

![Preview](docs/images/preview.gif)

**v1.20 stable is available** | Windows 10 2004+ / Windows 11 · x64 · Experimental ARM64 · C# / WPF

📥 [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases) | [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

> 🎉 **[v1.20 stable](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.20)** is available, with live audio activity for each app, more flexible automation and command line access, plus improvements to the quick panel, OSD, and background processing.

---

## Why SonicRoute?

When gaming, watching videos, or using several audio apps at once, you may need to switch between headphones and speakers, adjust one app's volume, or choose an input device. Windows provides these controls, but using them often means opening the system audio settings.

SonicRoute puts these frequent actions within reach of a hotkey or a tray panel.

## How does it compare with the Windows Volume Mixer?

Windows can already control each app's volume and output device. SonicRoute offers quicker ways to access those controls:

- Control the current app with hotkeys while keeping your game or window active.
- Scroll over the tray area to adjust the current app's volume.
- View and control apps with active audio sessions in the tray quick panel.
- Quickly switch output and input devices.
- See immediate OSD feedback.
- Run automation rules when their triggers occur.

## ✨ Core features

- 🎯 **App audio routing**: Switch output/input devices for one app, all running apps, or Windows defaults.
- 🖱 **Quick panel**: Compact and Classic layouts for volume, mute, and device controls.
- 📊 **Live audio activity**: Optional app level meters in the Compact panel, with colors for light and dark themes.
- ⌨️ **Hotkeys and tray wheel**: Customize controls for volume, device switching, and microphones.
- 🖥 **OSD**: Customize appearance and position, with an optional volume bar and persistent microphone mute notice.
- 🤖 **Automation**: Run steps on app events, hotkeys, schedules, or startup, with manual execution, copying, and reordering.
- 💻 **Command line**: Execute rules in a running instance with `sr "Rule name or ID"`.
- 🎨 **Appearance and languages**: Light/dark themes, nine languages, device filtering, and custom names.
- 🧹 **Resource management**: UI loading as needed, caching, and idle cleanup.

See the [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki) for detailed settings and instructions. The Wiki is currently in Chinese.

**1.21 workspace (in development)**: Adds rule search and filters, command copying, tray favorites, volume presets, an optional stop on failure for each step (off by default), and rule calling. See the [development feature guide (Chinese)](docs/automation-convenience.md). The stable download is still 1.20.

Before using `sr` for the first time, start the new version normally once, then reopen your terminal. See the [command line guide (Chinese)](docs/automation-command-line.md) for full usage details.

## 🚀 Quick start

🛍 **Microsoft Store**: Get SonicRoute from the [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) for x64 with automatic updates. The available version depends on the Store publication status.

Download **v1.20 stable** from [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.20):

| Version | Requirements |
|---|---|
| ⚡ Lite x64 | Main portable version; a single EXE requiring the x64 .NET 8 Desktop Runtime |
| 🧪 Lite ARM64 (experimental) | A single EXE requiring the ARM64 .NET 8 Desktop Runtime; not yet fully validated on ARM64 hardware |
| 🪟 Legacy x64 | Requires .NET Framework 4.8. Extract the complete package before running; .NET 8 is not required |

> Older packages that include the runtime remain in the v1.19 release assets. This package format is no longer provided starting with v1.20.
>
> The **v1.20 x64 Lite package has been submitted to the Microsoft Store for review**. Check the Store listing for the version currently available.

## 📚 Full documentation → [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

The following Wiki pages are in Chinese.

| Guide | Contents |
|---|---|
| [01 · User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) | Installation, panels, the main window, and usage examples |
| [02 · Features](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) | Features and what each control affects |
| [03 · Hotkeys](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) | Default keys, custom bindings, and usage examples |
| [04 · Automation rules](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) | Six triggers, seventeen actions, copying, and step ordering |
| [05 · Execute rules from the command line](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) | sr usage, execution results, and exit codes |
| [06 · FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) | Installation, devices, volume, automation, and performance troubleshooting |
| [07 · Technical implementation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) | Source structure, audio interfaces, and build instructions |
| [08 · Release information](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) | Current downloads, v1.20 changes, and release guidelines |
| [09 · Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) | Earlier changelogs and the version index |

## 📌 Roadmap

- **Current stable release: v1.20**, promoted to stable on October 2, 2026.
- **A major release is planned for the end of the year**. Its version number and scope will be announced later.

See [Release information](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) for current downloads, v1.20 changes, and release guidelines. Earlier changelogs are in [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

💡 Have an idea or feedback? [Open an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

---

## 🧩 Other projects

- 🎧 **[SilentPlayer](https://github.com/kunkunkunQoQ/SilentPlayer)** — A minimal Windows audio player with low resource use.
- 💡 Pair it with SonicRoute to create a soundboard controlled by hotkeys, similar to Soundpad. See the [combined setup guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#52-%E7%BB%84%E5%90%88%E5%9C%BA%E6%99%AFsilentplayer-%E9%9F%B3%E6%95%88%E6%9D%BF%E7%B1%BB%E4%BC%BC-soundpad).
- 📦 **[WinAudioRoute](https://github.com/kunkunkunQoQ/WinAudioRoute)** — SonicRoute's .NET library for Windows audio control: devices, sessions, audio routing for each app, events, and a CLI.

---

**Author: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://www.ifdian.net/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ/SonicRoute)**
