<!-- Generated from docs/readme/shared.json, docs/readme/locales/en-US.json, and docs/readme/releases/v1.34.json. Run node docs/readme/generate.cjs after editing. -->
<!-- AI / LLM Context: https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI -->
# 🎧 SonicRoute

[简体中文](README.md) | **English** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | [🤖 AI](README.ai.md)

[![Current stable release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github&label=Current%20stable%20release)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![License: GPL-3.0](https://img.shields.io/static/v1?label=License&message=GPL-3.0&color=blue)](LICENSE) [![Platform: Windows 10 | 11](https://img.shields.io/static/v1?label=Platform&message=Windows+10+%7C+11&color=blue)](https://github.com/kunkunkunQoQ/SonicRoute) [![Architecture: x64 | ARM64](https://img.shields.io/static/v1?label=Architecture&message=x64+%7C+ARM64&color=orange)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue&label=Downloads)](https://github.com/kunkunkunQoQ/SonicRoute/releases) [![Support on Afdian](https://img.shields.io/static/v1?label=&message=Support+on+Afdian&color=946CE6&logo=afdian&logoColor=white)](https://afdian.com/a/koukou021)

**Send each app's audio to the device you choose.**

Play music through your speakers and keep games and calls in your headset. SonicRoute runs in the Windows system tray. Choose a playback device and microphone for each app, adjust the volume with hotkeys or the mouse wheel, and use automation rules for changes you make regularly.

📥 [Download the latest release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest) · Microsoft Store ([Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6)) · [User guide (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97) · [Wiki (Chinese)](https://github.com/kunkunkunQoQ/SonicRoute/wiki)

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

## ✨ What SonicRoute offers

- **Choose where each app plays and records.** Send music to your speakers and game audio to your headset, then select the headset microphone for voice chat. Windows remembers the device choices when you exit SonicRoute.
- **Adjust the current app from the tray.** SonicRoute can follow the foreground app. Scroll over the SonicRoute tray icon to change the target app’s volume, or hold Alt and scroll to switch its output. On-screen indicators show changes, and a global hotkey lets you mute your microphones.
- **See which apps are playing audio.** The quick panel has a volume slider and a live level meter for each app. It remembers its position, and you can set its height from 350 to 850 device-independent pixels.
- **Connect an Android device or use AirPlay (Full).** Play Android app audio on the PC, open apps in separate virtual display windows, or send PC audio back to the phone. Android supports H.265 video and gamepad input in the active video window. The built-in C# AirPlay receiver plays audio and mirrors the screen at its original aspect ratio. In the quick panel, send that audio to a PC output, an Android phone’s speaker or a Bluetooth device connected to the phone. Smart Volume evens out loud passages on supported streams.
- **Run rules from an event or the command line.** Choose from 6 triggers and 39 actions. Trigger a rule with a hotkey, on a schedule, when an app opens, closes or gains focus, or when SonicRoute starts. Chain actions to switch devices, set volume, mute audio, open a program or file, or run PowerShell. Scripts and hardware controllers can run a rule with `sr` through the existing SonicRoute process.
- **Respond to Windows events.** SonicRoute uses system notifications, checks process state once a second when needed, and releases data it no longer needs. All three editions support nine interface languages. Lite and Legacy focus on PC audio.

## 🎮 Everyday uses

For example, one rule can switch to your headset and open voice chat when you launch a game. [Set up automation](https://github.com/kunkunkunQoQ/SonicRoute/wiki/04-%E8%87%AA%E5%8A%A8%E5%8C%96%E8%A7%84%E5%88%99)

| Everyday scenario | How SonicRoute helps |
|---|---|
| 🎮 **Game audio drowns out voice chat** | Adjust voice chat (such as Discord) without changing the game’s volume. You can also send the game and the chat to different outputs. |
| 🎵 **Listen to music or watch a video while gaming** | Send music to your speakers and keep the game in your headset. Scroll over the SonicRoute icon to adjust the target app’s volume, or hold Alt and scroll to switch its output. |
| ⚡ **Switch to a headset when a game starts** | Create a rule that switches to the headset, sets the volume to 40% and opens voice chat when the game starts. Use an exit rule to switch back to the speakers. |
| 🎙️ **Step away from a call or speak privately** | The default shortcut, Ctrl+Shift+N, toggles mute for all microphones. Enable the persistent on-screen indicator to see when they’re muted. |
| 📹 **Stream with OBS or record a tutorial** | Send the apps you want to record to a separate physical or virtual output, then select that output in your recording software. Leave chats and notifications on another output. |
| 📱 **Listen to a phone or use its apps on your PC** | In Full, route Android audio to the PC and use phone apps in a virtual display. You can also receive AirPlay audio and screen mirroring. Use the quick panel to play the audio on a PC output, an Android phone’s speaker or a Bluetooth device connected to the phone. |

## 🚀 Download and get started

**Current stable release: [v1.34](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.34)** · November 21, 2026 · [Release notes (Chinese)](docs/release-v1.34.md)

Choose **Full** for Android audio, virtual displays and AirPlay, or **Lite** for PC audio only. Both have x64 and experimental ARM64 builds. **Legacy** is the PC-only x64 edition for systems using .NET Framework 4.8. The direct downloads below are for v1.34. See the Microsoft Store listings for the versions of Full and Lite available there.

| Edition | Download | Requirements |
|---|---|---|
| ⭐ Full (.NET 10, includes Android and AirPlay) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-arm64.exe) | One portable executable with Android and AirPlay support. Install the .NET 10 Desktop Runtime for the build’s architecture (x64 or ARM64). |
| ⭐ Lite (.NET 10, PC audio only) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Lite-arm64.exe) | One portable executable. Install the .NET 10 Desktop Runtime for the build’s architecture (x64 or ARM64). |
| 🛡️ Legacy x64 (PC audio only, .NET Framework 4.8) | [Download ZIP](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.34/SonicRoute-v1.34-Legacy-x64.zip) | Extract the ZIP and run SonicRoute. It uses Windows’ .NET Framework 4.8. |
| 🔄 Microsoft Store | [Full](https://apps.microsoft.com/detail/9NQZGRTPM1NT) / [Lite](https://apps.microsoft.com/detail/9N37LQ8SMLF6) | The Store offers Full and Lite. Both require the x64 .NET 10 Desktop Runtime. |

<details>
<summary><sub>📦 Archived .NET 8 downloads (no longer maintained)</sub></summary>

| Edition | Download | Requirements |
|---|---|---|
| Lite for .NET 8 (discontinued) | [x64](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-x64.exe) / [ARM64 (experimental)](https://github.com/kunkunkunQoQ/SonicRoute/releases/download/v1.29/SonicRoute-v1.29-Lite-NET8-arm64.exe) | Archived build. For updates, switch to a .NET 10 edition or Legacy. |
</details>

> [!NOTE]
> For Full or Lite, install [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the architecture you downloaded. Legacy uses .NET Framework 4.8. Older builds that use [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) are available in the archive and no longer receive updates.

### 🏁 Quick start

1. **Open SonicRoute.** Start the executable, then double-click the SonicRoute icon in the system tray to open the main window. If the icon is hidden, you can drag it out of the hidden icons menu.
2. **Choose your devices.** In Settings, select the headphones and speakers you want to see in the quick panel. Click the tray icon once to open the panel. Expand an app’s row to select its output device, or drag its slider to adjust the volume.
3. **Use the mouse wheel.** Scroll over the SonicRoute icon to adjust the target app’s volume. Hold Alt while scrolling to change its output, or Shift to adjust the default output device’s volume. You can extend these controls to the entire system tray in Settings.

### ⚡ Run an automation rule from the command line

The `sr` command sends a rule name or ID to a running copy of SonicRoute over a named pipe. Start SonicRoute once to register the command, then close and reopen your terminal. You can now call `sr` from a script, AutoHotkey or Stream Deck. If SonicRoute is closed, `sr` returns an error. See [FAQ](https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98).

```powershell
# Trigger an automation rule (SonicRoute must be running)
sr "Rule name or ID"
```

### 🆕 What’s new in v1.34

All editions can now select multiple PC playback devices per app and restore the selection when the app plays audio again after SonicRoute restarts. Tray volume controls and mouse shortcuts now share a single system mouse hook.

Full keeps USB and Wi-Fi connection status in sync across both Android panels. Refresh in the quick phone panel can also reconnect the selected device. Android app output menus now show the custom name set for the phone’s current output. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for earlier releases.

<details>
<summary>v1.33</summary>

Full can now receive AirPlay audio (PCM, ALAC, AAC-LC and AAC-ELD) and mirror a screen at its original aspect ratio using a receiver written in C#. Set an optional pairing code, keep the mirror on top or press F11 for full screen. Drag audio to a PC output, an Android phone’s speaker or a Bluetooth device connected to the phone. Android and AirPlay support H.265 (HEVC). Android falls back to H.264 if the encoder cannot start. You can choose Android audio and video decoders. SonicRoute checks the available encoders when you open the device settings and sends gamepad input only to the focused video window.

Android pairing now puts the QR code (refreshed every two minutes) beside the manual pairing code. Reconnecting or refreshing a device checks its identity and looks for its current address and port on the local network. SonicRoute preserves a manually entered address if the search fails and can restore settings for a previously paired device.

All editions now have separate options to keep the mic indicator on screen when all microphones are muted or when the default recording device is muted. Configuration tools share one row. Long app lists render only the visible rows once the list exceeds 20 entries. SonicRoute combines on-screen updates and frees decoded PNG source data, backs up settings to `config.json.bak` and records diagnostics when errors occur. Full also releases unused video textures and leaves garbage collection to the runtime when a window closes. Lite is available separately in the Microsoft Store, and all editions include an offline copy of the GPL-3.0-only license. See [Version history](https://github.com/kunkunkunQoQ/SonicRoute/wiki/09-%E7%89%88%E6%9C%AC%E5%8E%86%E5%8F%B2) for details.

</details>

### ❓ Common questions

**What happens to app routing and volume when I exit SonicRoute?**

Windows keeps each app’s device choices and the volume of existing audio sessions. Keep SonicRoute running to use hotkeys, tray controls, automation, phone audio, screen mirroring or clipboard sync.

**Which runtime do I need?**

Full and Lite require the .NET 10 Desktop Runtime for the build’s architecture. The Store editions require the x64 runtime. Legacy uses .NET Framework 4.8. Archived .NET 8 builds are still available but no longer receive updates.

**How much CPU or battery does SonicRoute use in the background?**

SonicRoute relies on Windows notifications and checks process state once a second when needed. It releases data it no longer uses. CPU use and battery impact depend on your system and what you’re doing.

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
