<!-- Generated from docs/readme/shared.json and docs/readme/locales/en-US.json. Edit those sources, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute

[简体中文](README.md) | **English** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Support on Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://www.ifdian.net/a/koukou021)

**Send each app's audio to the device you choose.**

Keep music on your speakers and games or calls on your headset. SonicRoute is a Windows audio utility that lives in the system tray: choose output and input devices for individual apps, control audio with hotkeys, the tray wheel or a panel, and combine routine actions into automation rules.

📥 [Download the latest release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nine languages · MIT licensed

<div>
<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97" title="User guide (Chinese)"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="Click the mascot to open the user guide"></a>
<br>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="SonicRoute Quick panel: output device and per-app volume controls"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="SonicRoute automation rule editor: select app triggers and audio action steps"></a>
</div>

<sub>Interface preview: Quick panel (with adaptive height and acrylic style) for instant app volume and routing; automation editor for custom triggers and action chains.</sub>

## ✨ What SonicRoute offers

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7" title="Features"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="Click the mascot to explore SonicRoute features"></a>

- **Independent app audio routing (playback + recording).** Assign output and input devices to individual apps—send games and voice chat to headphones and headsets, and music to speakers. Settings persist natively in Windows even after closing SonicRoute, without altering system mixer settings.
- **Smart foreground focus and tray wheel blind adjustments.** System events lock onto the active audio app in milliseconds. Scroll directly over the system tray to adjust app volume, paired with on-screen OSD feedback and global mic mute, without leaving your active window.
- **Dual panel layouts and live audio activity indicators.** Choose between the classic row-based management panel and the Modern card panel, featuring 350–850px adaptive height and position saving; control per-app volume sliders with real-time sound activity meters.
- **Automation combo engine and blazing CLI (6 triggers × 37 actions).** Trigger rules by app launch, focus, exit, schedule, hotkeys, or startup. Chain device switching, volume, muting, app launch with file associations, and PowerShell scripts; use the built-in sr named-pipe command for instant script and hardware deck control without extra background processes.
- **Lightweight, pure event-driven architecture (near-zero idle CPU).** Fully driven by system events with zero persistent polling timers, near-zero idle CPU usage, and on-demand memory cleanup. Available as dependency-free Legacy (.NET 4.8), single-file Lite (x64 / experimental ARM64), and Microsoft Store builds, with nine built-in languages.

## 🎮 Everyday uses

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automation"><img src="docs/images/mascot-automation-gb.png" width="48" align="middle" alt="Click the little robot to learn about automation rules"></a> Turn tedious manual sound adjustments into seamless, one-time automations. [Set up automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Everyday scenario | How SonicRoute helps |
|---|---|
| 🎮 **Voice chat too loud, or game sound drowning out teammates** | Adjust voice chat (Discord) volume independently without altering game audio; keep game in headset and chat in earpiece |
| 🎵 **Listening to music or watching videos while gaming** | Route music to speakers and games to headphones; adjust music volume with the tray scroll wheel without leaving full screen |
| ⚡ **Constantly swapping headsets and changing Windows settings for games** | Create a rule: launch game → switch to headset + set volume to 40% + start voice chat; restore speakers on exit |
| 🎙️ **Stepping away from the PC or taking a private call** | Mute all microphones with Ctrl+Shift+N and confirm mute state instantly via the on-screen OSD banner |
| 📹 **Streaming on OBS or recording tutorials** | Route applications to dedicated physical or virtual audio tracks so private chats and alerts never end up in recordings |

## 🚀 Download and get started

**Current release: [v1.24](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.24)** · 2026-10-20 · [Release notes (Chinese)](docs/release-v1.24.md)

For most Windows x64 users, the Microsoft Store build offers convenient installation and automatic updates. Choose Lite x64 for a single executable, or Legacy x64 if installing .NET 8 is not convenient. The download link above always points to the latest release; Lite and Legacy links below are for v1.24, while the Store page shows the version currently available there.

| Version | Download | Requirements |
|---|---|---|
| ⭐ Lite x64 (Recommended) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-x64.exe) | Standalone single-file executable; requires matching x64 .NET 8 Desktop Runtime |
| ⚡ Lite ARM64 (experimental) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Lite-arm64.exe) | Native performance on Surface Pro and Snapdragon ARM64 devices; requires matching ARM64 .NET 8 Desktop Runtime |
| 🛡️ Legacy x64 (No .NET 8 required) | [Complete ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.24/SonicRoute-v1.24-Legacy-x64.zip) | No .NET 8 needed; runs directly via built-in .NET Framework 4.8; extract ZIP to use |
| 🔄 Microsoft Store (Auto-updates) | [Install from the Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | One-click install with silent automatic updates; for 64-bit Windows 10/11 |

Lite requires the matching [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). Legacy does not require .NET 8.

1. **Launch & Keep in Tray**: Double-click to start; it stays in your system tray (drag it out of the overflow menu to keep it visible). Double-click the tray icon to open the main window.
2. **Select & Route Devices**: Check your headphones and speakers under Settings → Preserved devices. Left-click the tray icon to pop up the quick panel, and route each app with instant volume sliders.
3. **Blind Volume Control**: Hover over the tray icon and scroll the wheel to adjust volume blindly, or use hotkeys and automation rules without leaving full-screen games.

`sr` connects only to an already-running SonicRoute instance; it does not start the app. Before first use, start SonicRoute normally to register the command, then close and reopen your terminal. See the [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) if device switching does not work as expected.

```bash
# Trigger an automation rule (SonicRoute must be running)
sr "Rule name or ID"
```

Version 1.24 adds starter templates for automation, optional recent execution details (off by default, with a storage limit), and per-app icon imports. Missing or generic app icons fall back to the first character of the app’s name. Configuration backups include imported icons. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for earlier changes.

### ❓ Quick FAQ

**Q: Will app routing and volume settings persist after closing SonicRoute?**

A: Yes! App audio routing is natively preserved by the Windows audio engine and stays active even after SonicRoute is closed. Only hotkeys, tray controls, and automation rules require SonicRoute running in the background.

**Q: What should I do if Windows prompts that .NET 8 Desktop Runtime is missing?**

A: Click the runtime link in the table above to install the official Microsoft .NET 8 Desktop Runtime, or download the Legacy edition which requires no additional runtime.

**Q: Does running SonicRoute in the background consume CPU or battery?**

A: Practically zero. SonicRoute is built entirely on Windows event-driven listeners; it idles at 0% CPU with zero background polling timers and on-demand memory cleanup.

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

- **v1.25 · Major Update (In Development)**: Automation orchestration expands to 6 triggers and 37 advanced actions (including recording device details, per-app mic mute, batch app volume ducking, and one-click routing reset).
- **Upcoming Stages**: Continuously refine low-latency event listeners and zero-polling telemetry based on community feedback.

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
