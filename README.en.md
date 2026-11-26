<!-- Generated from docs/readme/shared.json, docs/readme/locales/en-US.json, and docs/readme/releases/v1.36.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | **English** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Current stable release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=Current%20stable%20release)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: GPL-3.0-only](https://img.shields.io/static/v1?label=License&message=GPL-3.0-only&color=blue)](LICENSE) [![Platform: Windows 10 | 11](https://img.shields.io/static/v1?label=Platform&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architecture: x64 | ARM64](https://img.shields.io/static/v1?label=Architecture&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Downloads)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Support on Afdian](https://img.shields.io/static/v1?label=&message=Support+on+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Send each app's audio to the device you choose.**

Play music through your speakers while games and calls use your headset. From the Windows system tray, SonicRoute lets you choose a playback device and microphone for each app and adjust its volume.

📥 [Download the latest release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · [Compare editions](#downloads) · [Quick start](#quick-start) · [User guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97)

Windows 10 2004+ / Windows 11 · x64 / ARM64 (experimental) · Nine languages · [GPL-3.0](LICENSE)

<div>
<a href="docs/images/phone-audio-drag-demo.gif"><img src="docs/images/phone-audio-drag-demo.gif" width="380" align="top" alt="A phone app’s audio is dragged into the PC’s quick panel, switched to a different output, then dragged back to the phone"></a>
<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="380" align="top" alt="Automation rule editor with a hotkey trigger and the action menu open to “Set system default playback device”"></a>
</div>

<sub>On the left, a phone app’s audio is dragged to the PC and switched between outputs. On the right, the action menu is open for a rule triggered by a hotkey.</sub>

<details>
<summary><sub>🖼️ More screenshots of the quick panel and Android device settings</sub></summary>

<div>
<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="Quick panel with an output selector and a volume slider for each app"></a>
<a href="docs/images/phone-management.png"><img src="docs/images/phone-management.png" width="465" align="top" alt="Android device settings for pairing, pinned apps and virtual display audio"></a>
</div>

</details>

<a name="downloads"></a>

## 🚀 Download and get started

**Current stable release: [v1.36](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36)** · November 24, 2026 · [Release notes (English and Chinese)](docs/release-v1.36.md)

Choose **Lite** for Windows audio, **Full** to add Android and AirPlay, or **Legacy** for PC audio on an x64 system with .NET Framework 4.8. The table lists downloads for v1.36 and links to the Microsoft Store editions.

| Edition | Download | Requirements |
|---|---|---|
| ⭐ Full (PC, Android and AirPlay) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-arm64.exe) | Portable EXE. Requires the .NET 10 Desktop Runtime for the selected architecture. |
| ⚡ Lite (PC audio) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Lite-arm64.exe) | Portable EXE. Requires the .NET 10 Desktop Runtime for the selected architecture. |
| 🛡️ Legacy (PC audio, x64) | [Download ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.36/SonicRoute-v1.36-Legacy-x64.zip) | Extract the entire ZIP before running SonicRoute. Requires .NET Framework 4.8. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | Full and Lite both require the x64 .NET 10 Desktop Runtime. See each Store listing for its current version. |

<details>
<summary><sub>📦 Archived .NET 8 downloads (no longer maintained)</sub></summary>

| Edition | Download | Requirements |
|---|---|---|
| Lite for .NET 8 (discontinued) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Archived build. For updates, switch to a .NET 10 edition or Legacy. |

</details>

> [!NOTE]
> For Full or Lite, install [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the architecture you downloaded. Legacy uses .NET Framework 4.8. Older builds that use [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) are available in the archive and no longer receive updates.

<a name="quick-start"></a>

### 🏁 Quick start

1. **Open SonicRoute.** Run the EXE (extract the ZIP first for Legacy). Double-click the SonicRoute icon in the system tray to open the main window.
2. **Route an app.** In Settings, choose which devices to show. Play audio in an app, then single-click the tray icon. Expand the app’s row in the quick panel to choose an output. Drag its slider to adjust its volume.
3. **Try the mouse wheel.** Scroll over the tray icon to adjust the current app’s volume. By default, SonicRoute looks for an audio session in the foreground app first, then in the last foreground app that had one. Hold Alt and scroll to switch the app’s output, or hold Shift and scroll to adjust the volume of the system’s default playback device.

## ✨ What SonicRoute offers

- **Choose playback and recording devices for each app.** In the quick panel or on the Apps page, Ctrl-click an output to add or remove it. Shift-click to add a range without clearing your selection. For a Windows app, Ctrl+Shift-click a PC output to make it the app’s primary playback device. SonicRoute sends the same audio to the other selected outputs. Full can also use outputs on a connected Android phone.
- **See what’s playing and adjust its volume.** Live level meters show which apps are playing, each with its own volume slider. Scroll over the tray icon to adjust the current app’s volume, or use a hotkey to mute and unmute your microphones.
- **Automate routine changes.** Choose one of 6 triggers and build a sequence from 39 available actions. When a game starts, a rule can switch to your headset and open voice chat. You can also run a saved rule from a terminal or script with `sr`.
- **Connect an Android phone or receive AirPlay (Full).** Play Android app audio on your PC, send PC audio to your phone, or open an Android app in its own virtual display. Browse the phone’s shared storage and transfer files in either direction, including several at once. From an Apple device, you can mirror the screen or send AirPlay audio to SonicRoute and play it through several PC or Android outputs at once.

## 🎮 Everyday uses

For example, one rule can switch to your headset and open voice chat when you launch a game. [Set up automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Everyday scenario | How SonicRoute helps |
|---|---|
| 🎮 **Game audio drowns out voice chat** | Adjust voice chat (such as Discord) without changing the game’s volume. You can also send the game and the chat to different outputs. |
| 🎵 **Listen to music or watch a video while gaming** | Send music to your speakers and keep the game in your headset. Scroll over the SonicRoute icon to adjust the target app’s volume, or hold Alt and scroll to switch its output. |
| ⚡ **Switch to a headset when a game starts** | Create a rule that switches to the headset, sets the volume to 40% and opens voice chat when the game starts. Use an exit rule to switch back to the speakers. |
| 🎙️ **Step away from a call or speak privately** | The default shortcut, Ctrl+Shift+N, toggles mute for all microphones. Enable the persistent on-screen indicator to see when they’re muted. |
| 📹 **Stream with OBS or record a tutorial** | Send the apps you want to record to a separate physical or virtual output, then select that output in your recording software. Leave chats and notifications on another output. |
| 📱 **Listen to a phone or use its apps on your PC** | Play an Android game in a virtual display on your PC, with H.265 video on supported devices. Gamepad input goes to the game only while its virtual display window has focus. Choose your PC speakers in the quick panel. Smart Volume helps even out sudden changes in loudness. |

### ⚡ Run an automation rule from the command line

The `sr` command sends a rule name or ID to a running copy of SonicRoute over a named pipe. Start SonicRoute once to register the command, then close and reopen your terminal. You can now call `sr` from a script, AutoHotkey or Stream Deck. If SonicRoute is closed, `sr` returns an error. See [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Trigger an automation rule (SonicRoute must be running)
sr "Rule name or ID"
```

## ❓ Common questions

**What happens to app routing and volume when I exit SonicRoute?**

Windows keeps each app’s primary PC playback device, recording device and the volume of existing audio sessions. Playback on additional outputs stops when you exit SonicRoute. Keep SonicRoute running to use hotkeys, tray controls, automation, phone audio, screen mirroring or clipboard sync.

**Which runtime do I need?**

Full and Lite require the .NET 10 Desktop Runtime for the build’s architecture. The Store editions require the x64 runtime. Legacy uses .NET Framework 4.8. Archived .NET 8 builds are still available but no longer receive updates.

**How much CPU or battery does SonicRoute use in the background?**

SonicRoute relies on Windows notifications and checks process state once a second when needed. It releases data it no longer uses. CPU use and battery impact depend on your system and what you’re doing.

**How do I adjust the latency when playing Android audio on my PC?**

In Full, open the phone’s device page. Under Audio, the default latency mode is Balanced. Try Ultra-low latency for a shorter delay, or Stable if you hear dropouts. The phone sends audio using Opus by default. For uncompressed audio, choose Raw PCM in the decoder list. Changes apply the next time you start an audio stream from that phone.

## 🆕 What’s new in v1.36

In the quick panel and on the Apps page, Shift-click adds a range of outputs to your current selection. Ctrl+Shift-click a PC output to make it the primary playback device for a Windows app. The buttons show which output Windows uses for the app and which ones receive a copy. SonicRoute also remembers whether you left the main window’s sidebar open or collapsed. Both Microsoft Store editions keep the running app grouped with its pinned taskbar icon.

Full can play incoming AirPlay audio through several outputs at once. It can capture audio from compatible apps on Android 11 or later, with a choice of Opus or uncompressed PCM and three latency modes. If the phone’s encoder supports it, the virtual display adapts its video bitrate to the USB or Wi-Fi connection. The Full package for the Microsoft Store includes rules for incoming AirPlay connections on private and domain networks. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for earlier releases.

<details>
<summary>v1.35</summary>

When you select a microphone for a PC app in the quick panel, the device is highlighted immediately. You can keep clicking while SonicRoute applies your changes in order. Full gives the same feedback for external audio sources that use a single output. The output name in the app row updates as soon as you select a device. Device-switching hotkeys now let you queue changes. When you use the hotkey to switch the current app’s output, a switch that finishes within 45 ms shows only the result. If a progress notice appears, it stays on screen for at least 500 ms before showing success. Errors appear immediately. When Windows moves an app’s audio session to another selected output, playback continues on the other outputs you chose.

In Full, you can still change outputs while Android switches audio capture modes, and the virtual display remains responsive. Turning off clipboard sync for one phone cancels its pending writes to the PC clipboard. Other phones continue syncing. SonicRoute also releases clipboard memory if a write fails. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for earlier releases.

</details>

## 📚 Documentation

The Wiki is currently in Chinese.

| Topic | Guides |
|---|---|
| Installation, panels and everyday controls | [User guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Features](https://github.com/kunkunkunQoQ/SonicRoute/wiki/02-%E5%8A%9F%E8%83%BD%E7%89%B9%E6%80%A7) |
| Default keys, bindings and control targets | [Hotkeys](https://github.com/kunkunkunQoQ/SonicRoute/wiki/03-%E5%BF%AB%E6%8D%B7%E9%94%AE) |
| Triggers, actions and script integration | [Automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99) · [Command line](https://github.com/kunkunkunQoQ/SonicRoute/wiki/05-%E5%91%BD%E4%BB%A4%E8%A1%8C%E6%89%A7%E8%A1%8C%E8%A7%84%E5%88%99) |
| Installation and troubleshooting | [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98) |
| Source, builds and releases | [Technical implementation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0) · [Release information](https://github.com/kunkunkunQoQ/SonicRoute/wiki/08-%E7%89%88%E6%9C%AC%E7%9B%B8%E5%85%B3) · [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) |

> [!TIP]
> **Using an AI assistant**: For help with a script or a technical issue, give your assistant [README.ai.md](README.ai.md). It summarizes the architecture, commands and troubleshooting steps in a compact format for tools such as Cursor, Copilot, ChatGPT and Claude.

## 📌 Roadmap

- **Audio routing.** Planned work includes moving audio between devices as they connect or disconnect, recovering interrupted routes, and making routing more reliable for apps with unusual audio behavior or multiple audio sessions.

[Share an idea or report an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues/new/choose).

## 💖 Support SonicRoute

SonicRoute is a free, actively maintained open-source project. If it makes managing audio on your PC easier, a donation through Afdian helps fund new features, compatibility improvements, and long-term maintenance.

☕ [Support on Afdian](https://afdian.com/a/koukou021)

Donations are optional. You can use every feature without donating. Reporting an issue, recommending SonicRoute to a friend, or giving the project a star on GitHub also helps. Thank you!

## 📜 License

- **License.** SonicRoute is licensed under the GNU General Public License version 3 only (GPL-3.0-only). Commercial use, sale and paid distribution are allowed under its terms. See [LICENSE](LICENSE).
- **Redistribution.** Keep the required copyright and license notices, identify your changes, and distribute covered modified works under GPL version 3. If you distribute covered binaries, provide the complete Corresponding Source as required by section 6. See [LICENSE](LICENSE).
- **Scope and earlier licenses.** As of November 17, 2026, GPL-3.0-only covers this revision of the project, including earlier code, to the extent the copyright holder can license it. Permissions already granted under the MIT license and the former custom license remain valid. Existing tags and previously released binaries retain their original licensing. Third-party components retain their own licenses (see [third-party notices](packaging/AndroidLab/THIRD-PARTY-NOTICES.md)).

---

<a href="EASTER_EGGS.md" title="An Easter egg: yesterday and tomorrow"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="Click the resting mascot to discover an Easter egg"></a>

Thanks for using SonicRoute. [Read the guides (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki) · [Report an issue](https://github.com/kunkunkunQoQ/SonicRoute/issues).

**Author: [Bilibili](https://b23.tv/TDqSAKM) | [Afdian](https://afdian.com/a/koukou021) | [GitHub](https://github.com/kunkunkunQoQ)**

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI">🤖 Reference for AI assistants</a></sub></div>
