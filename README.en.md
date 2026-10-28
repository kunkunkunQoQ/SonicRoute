<!-- Generated from docs/readme/shared.json and docs/readme/locales/en-US.json. Edit those sources, then run node docs/readme/generate.cjs. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | **English** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Support on Afdian](https://img.shields.io/badge/Support-Afdian-946CE6?logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Send each app's audio to the device you choose.**

Keep music on your speakers and games or calls on your headset. SonicRoute is a Windows audio utility that lives in the system tray: choose output and input devices for individual apps, control audio with hotkeys, the tray wheel or a panel, and combine routine actions into automation rules.

📥 [Download the latest release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Microsoft Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) · [User guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nine languages · MIT licensed

> [!TIP]
> **AI Summaries & Low-Token Troubleshooting**: When using AI assistants (such as Cursor, Copilot, ChatGPT, or Claude) to troubleshoot issues, explore architecture, or write scripts, refer directly to [README.ai.md](README.ai.md). Structured in a high-density DSL specifically for LLMs, it eliminates verbose prose, drastically reduces token consumption, and helps AI pinpoint root causes in seconds.

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
- **Smart foreground focus and tray wheel blind adjustments.** System events lock onto the active audio app in milliseconds. Scroll directly over the system tray to adjust volume or Alt+scroll to switch devices, paired with on-screen OSD feedback and global mic mute, without leaving your active window.
- **Quick panel and live audio activity indicators.** The card panel features 350–850px adjustable height and position saving; control per-app volume sliders with real-time sound activity meters.
- **Automation combo engine and blazing CLI (6 triggers × 37 actions).** Trigger rules by app launch, focus, exit, schedule, hotkeys, or startup. Chain device switching, volume, muting, app launch with file associations, and PowerShell scripts; use the built-in sr named-pipe command for instant script and hardware deck control without extra background processes.
- **Event-first detection.** Uses system notifications with a necessary one-second snapshot fallback when needed, and cleans up unneeded data on demand. Available as Legacy (.NET 4.8), Lite for .NET 10 and NET8, and a Store edition in nine languages.

## 🎮 Everyday uses

<a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99" title="Automation"><img src="docs/images/mascot-automation-gb.png" width="48" align="middle" alt="Click the little robot to learn about automation rules"></a> Turn tedious manual sound adjustments into seamless, one-time automations. [Set up automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Everyday scenario | How SonicRoute helps |
|---|---|
| 🎮 **Voice chat too loud, or game sound drowning out teammates** | Adjust voice chat (Discord) volume independently without altering game audio; keep game in headset and chat in earpiece |
| 🎵 **Listening to music or watching videos while gaming** | Send music to speakers and keep games in your headset. Hover your mouse over the tray icon and scroll to adjust music volume or Alt+scroll to switch devices without Alt+Tabbing. |
| ⚡ **Constantly swapping headsets and changing Windows settings for games** | Create a rule: launch game → switch to headset + set volume to 40% + start voice chat; restore speakers on exit |
| 🎙️ **Stepping away from the PC or taking a private call** | Mute all microphones with Ctrl+Shift+N and confirm mute state instantly via the on-screen OSD banner |
| 📹 **Streaming on OBS or recording tutorials** | Route applications to dedicated physical or virtual audio tracks so private chats and alerts never end up in recordings |

## 🚀 Download and get started

**Current release: [v1.29](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.29)** · 2026-10-27 · [Release notes (Chinese)](docs/release-v1.29.md)

For Windows x64, choose Lite with .NET 10, the NET8-compatible Lite edition, or Legacy with .NET Framework 4.8. The Store page shows the version currently available there and uses Store updates. Direct download links below are for v1.29.

| Version | Download | Requirements |
|---|---|---|
| ⭐ Lite x64 (.NET 10, Standard) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-x64.exe) | Portable single-file executable; requires the matching x64 .NET 10 Desktop Runtime |
| ⚡ Lite ARM64 (.NET 10) (experimental) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-arm64.exe) | Portable ARM64 executable; requires the matching ARM64 .NET 10 Desktop Runtime |
| ☕ Lite NET8 x64 | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) | NET8-compatible Lite edition; requires the x64 .NET 8 Desktop Runtime |
| ☕ Lite NET8 ARM64 (experimental) | [Single EXE](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | NET8-compatible Lite edition; requires the ARM64 .NET 8 Desktop Runtime |
| 🛡️ Legacy x64 (No .NET 8 required) | [Complete ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Legacy-x64.zip) | No .NET 8 needed; runs directly via built-in .NET Framework 4.8; extract ZIP to use |
| 🔄 Microsoft Store (Store updates) | [Install from the Store](https://apps.microsoft.com/detail/9NQZGRTPM1NT) | The .NET 10 Store package requires the x64 .NET 10 Desktop Runtime. See the Store page for the version currently available there; for 64-bit Windows 10/11. |

Standard Lite requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); NET8-compatible Lite requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). The NET8 edition will no longer be offered or maintained after November 2026. After that, x64 users can choose .NET 10 or Legacy (.NET Framework 4.8); ARM64 users should choose .NET 10.

1. **Launch & Keep in Tray**: Double-click to start; it stays in your system tray (drag it out of the overflow menu to keep it visible). Double-click the tray icon to open the main window.
2. **Select & Route Devices**: Check your headphones and speakers under Settings → Preserved devices. Left-click the tray icon to pop up the quick panel, and route each app with instant volume sliders.
3. **Effortless blind control.** Move your mouse over the tray icon and scroll to adjust volume or Alt+scroll to switch devices; combined with hotkeys and automation, control your audio without leaving full-screen games.

`sr` connects only to an already-running SonicRoute instance; it does not start the app. Before first use, start SonicRoute normally to register the command, then close and reopen your terminal. See the [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) if device switching does not work as expected.

```bash
# Trigger an automation rule (SonicRoute must be running)
sr "Rule name or ID"
```

1.29 adds an on-demand, event-refreshed panel with main volume controls for all output devices, optionally filtered to retained devices. Shift+wheel adjusts system volume; the existing wheel, Alt+wheel, and Alt+Shift+wheel actions remain. Earlier changes: [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2).

### ❓ Quick FAQ

**Q: Will app routing and volume settings persist after closing SonicRoute?**

A: Yes! App audio routing is natively preserved by the Windows audio engine and stays active even after SonicRoute is closed. Only hotkeys, tray controls, and automation rules require SonicRoute running in the background.

**Q: Which runtime does each edition require?**

A: Standard Lite requires the matching .NET 10 Desktop Runtime; Lite NET8 requires .NET 8. Legacy needs no separate .NET Desktop Runtime package and uses .NET Framework 4.8. The current Store package requires the x64 .NET 10 Desktop Runtime.

**Q: Does running SonicRoute in the background consume CPU or battery?**

A: Detection prioritizes system events and uses a necessary one-second snapshot fallback when needed. Unneeded data is cleaned up on demand; actual CPU use depends on the system and workload.

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

- **Audio routing enhancement**: Plan to further advance the core routing engine with dynamic stream handover, automatic recovery upon device plug/unplug, and improved routing stability for complex multi-session and protected apps.

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

**Author: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI / LLM Context">🤖 AI Context</a></sub></div>
