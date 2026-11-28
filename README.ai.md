<!-- Generated via docs/readme/generate.cjs. Release metadata: docs/readme/shared.json. Source version: build/Version.props. Body: renderAI(). Run node docs/readme/generate.cjs to update. -->
# 🎧 SonicRoute 2.00 (AI / LLM Machine Specification)

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | **🤖 AI**

This guide describes the SonicRoute 2.00 desktop source on master. Full and Lite use self-contained .NET 10 Native AOT. Full adds Android connections and AirPlay to Windows audio routing. [Version notes](docs/release-v2.00.md) · [Latest published desktop release](https://github.com/kunkunkunQoQ/SonicRoute/releases/latest)

## 1. Schema, Licensing & Version Scope

```yaml
SCHEMA_SPEC:
  system: "SonicRoute"
  platform: "Windows 10 2004+ (Build 19041+) | Windows 11 (x64 / win-arm64)"
  process_loopback_min_os: "Windows build 20348 or later for VAD\\Process_Loopback"
  android_min_os_capture: "The ADB capture helper requires Android 11 (API 30+). The companion app supports Android 10 (API 29+) and uses Android projection consent and the source app capture policy."
  version: "v2.00 (2.00.0.0)"
  source_version: "v2.00 (2.00.0.0)"
  wpf_baseline_tag: "v1.36 (3d9f9e1083b228f666e30d97e3c8edfce5e6654a)"
  repository: "https://github.com/kunkunkunQoQ/SonicRoute"
  android_repository: "https://github.com/kunkunkunQoQ/SonicRoute-App"
  android_releases: "https://github.com/kunkunkunQoQ/SonicRoute-App/releases"
  android_installation: "https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/README.md#install"
  license: "GNU General Public License v3.0 only (SPDX: GPL-3.0-only; commercial use, sale, and paid distribution permitted subject to GPL Section 6 source obligations; see LICENSE and docs/LICENSING.md)"
  license_transition: "From 2026-11-17, the covered project in this revision (including prior authored code) is offered under GPL-3.0-only to the extent licensed by the copyright holder. Prior MIT and custom-license grants remain valid; old tags/binaries are not relabeled. Third-party notices remain under their original licenses."
  third_party_notices: ["packaging/AndroidLab/THIRD-PARTY-NOTICES.md", "packaging/Licenses/Windows-runtime.txt", "packaging/AndroidLab/QRCoder-LICENSE.txt", "packaging/Licenses/Windows-drawing.txt", "SonicRoute.AndroidAudio/AirPlay/AacEld.NOTICE.md", "SonicRoute.AndroidAudio/AirPlay/Compatibility/LICENSE-MIT.txt"]
  wiki_root: "https://github.com/kunkunkunQoQ/SonicRoute/wiki"
  downloads: "https://github.com/kunkunkunQoQ/SonicRoute/releases"
  store_url: "https://apps.microsoft.com/detail/9NQZGRTPM1NT"
  store_lite_url: "https://apps.microsoft.com/detail/9N37LQ8SMLF6"
  localization: "9 built-in locales (de-DE, en-US, es-ES, fr-FR, ja-JP, ko-KR, ru-RU, zh-CN, zh-TW). Missing entries fall back to zh-CN. Custom JSON languages can be imported at runtime."
```

## 2. Feature & Branch Status Matrix

```yaml
FEATURE_STATUS_MATRIX:
  SONICROUTE_v200:
    - "Native AOT (.NET 10 PublishAot=true, reflection JSON disabled) single-EXE desktop host for Full & Lite; removes WPF, WinForms, XAML, NAudio, and System.Management from Full/Lite."
    - "Custom Win32 LayeredWindow + Direct2D/DirectWrite UI matching v1.36 theme/metrics, per-monitor DPI, and event-driven device notifications."
    - "Redesigned Apps workspace (AppsPageView.Canvas + AppsCanvasEngine + OutputConnectionPopupWindow): per-app node/wire routing canvas, partitioned collision-free layout, solid native / dashed forward / emerald mic wires, saved per-output volume, mute and volume ceiling (1%–300%), and bounded AppCanvasLayouts persistence."
    - "Adaptive UiDockRail for Apps and Devices with floating search/filter/refresh tools."
    - "Unified Devices tab (MainNavTab.Devices / PhoneDevicePageView) combining Android Configure/Connect and AirPlay receiver settings."
    - "Native Full pairs with a nearby Android companion using an eight-digit comparison code. A trusted phone or PC can start and stop sound sharing in either direction and request a view of the phone screen. The quick panel displays icons for active phone media sessions."
    - "The Android device page offers one SmartVolume switch for sound received from all companion apps. It defaults off. USB and wireless ADB offer per-app SmartVolume, draggable pinned apps, file transfer, clipboard sync and audio/video encoder settings."
    - "Windows UI Automation exposes the quick panel controls for pinning, devices, mute and opening the main window. The Full connection page supports keyboard focus and protects pairing codes from accessibility readback. Settings displays third-party notices for the installed edition offline."
    - "Device notifications refresh the Windows audio cache on a background thread. Closing the connection page cancels its pending discovery requests. Pairing, permission and connection prompts cover all nine interface languages."
    - "Windows DNS-SD (DnsServiceBrowse) background detection for saved Android devices without triggering firewall prompts when AirPlay is off."
    - "Source-generated COM ([GeneratedComInterface]/[GeneratedComClass]) for Core Audio, VAD\\Process_Loopback, and MTA WMI ProcessTraceSubscription."
    - "Isolated diagnostic/test IPC (--control over SonicRoute.NativeControl.*) gated by CandidateEnvironment."
    - "Microsoft Store has separate listings for SonicRoute (Full) and SonicRoute Lite. Build-Package.ps1 supports both editions. The release workflow builds an unsigned Full x64 MSIX."
  EXPERIMENTAL:
    - "ExperimentalMode page (unlock by clicking author name 5x in Settings, restart required): BackgroundPollingFallbackEnabled (1s fallback), ExecutionHistoryEnabled (bounded JSONL), MicInPanel, and Config export/import/clear."
    - "ExperimentalMic (default true; per-app capture routing & mic canvas partition; unsupported on external Android/AirPlay sources)."
    - "ARM64 portable Native AOT builds (win-arm64 marked EXPERIMENTAL in release matrix)."
  HISTORICAL_WPF_v136:
    - "WPF/.NET 10 Desktop Runtime (Full/Lite x64; ARM64 experimental) and .NET Framework 4.8 (Legacy x64)."
    - "Per-app primary playback/capture routing, multi-device playback mirrors & restart restore, global hotkeys, tray wheel, 6x39 automation, CLI sr/--run-rule, Protocol 2 updater."
    - "Full: pure C# in-process ADB, Android audio (Opus/Raw PCM, 3 latency modes), SmartVolume, virtual display & H.265/adaptive bitrate, focus-gated gamepad, 64 KiB clipboard sync, file transfer, AirPlay audio/mirror + multi-output."
  HARDWARE_VERIFICATION_SCOPE:
    - "Android hardware coverage still needed for listening on two physical phone outputs at once, per-app gain on third-party players, BLE/SCO/USB playback, other vendor ROMs and acoustic end-to-end latency. Whole-PC mix playback still needs a physical test with an identifiable source."
    - "Native AOT v2.00 ADB virtual display and routing canvas integration still need full device coverage. See ANDROID_APP_SRAP for the separately verified companion screen viewer."
    - "Native AOT v2.00 physical Apple AirPlay streaming/mirroring, physical ARM64 execution, and signed Store install/upgrade."
  SEPARATE_BRANCH_UNMERGED:
    - "Windows Link (LAN PC-to-PC audio and window sharing) is available in the Beta branch. The v2.00 master tree excludes it."
  DISCONTINUED_OR_REMOVED:
    - "NET8 builds (discontinued after v1.29)."
    - "External adb.exe, scrcpy.exe, FFmpeg, and SDL2 binaries (replaced in v1.32 by SonicRoute.AndroidAudio pure C# + sonicroute-catalog.jar)."
    - "Legacy x64 (.NET Framework 4.8, WPF) ended with v1.36. Its updater requires a matching Legacy package."
```

## 3. Architecture, Tech Stack & Editions

The Android 1.0 companion (API 29+, target 35) is maintained in its own repository. The desktop repository retains the original Android source snapshot under `SonicRoute app/`. [Android source and setup](https://github.com/kunkunkunQoQ/SonicRoute-App) · [Windows connection guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/10-设备互联) · [Device verification record](https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/docs/verified-handoff-20261128.md)

### 3.1 Solution & Project Topology

| Solution / Project | Target & Mode | Role | Notes |
|---|---|---|---|
| `SonicRoute.Native.slnx` | `.NET 10` Native AOT | Active `v2.00` Full & Lite solution | `Native/Product`, `Native/Audio`, `SonicRoute.AndroidAudio`, `SonicRoute.Core`, `StoreInterop` |
| `Native/Product/Product.csproj` | `net10.0-windows`, `PublishAot=true` | Native AOT WinExe (`Full` / `Lite`) | Reflection JSON off; links shared C# from `SonicRoute/` via `Protocol/Routing/Automation.targets` |
| `Native/Audio/Audio.csproj` | `net10.0-windows`, `IsAotCompatible` | AOT COM Core Audio, Policy & Loopback | Raw vtable calls + `[GeneratedComInterface]` (`Endpoints`, `Policy`, `ProcessCapture`, `DeviceNotificationHub`) |
| `SonicRoute.Core/SonicRoute.Core.csproj` | `net10.0-windows;net48` | Shared config, rules, stores, Core Audio models | `PersistenceJsonContext` source-gen on NET10; `System.Text.Json 8.0.5` on `net48` |
| `SonicRoute.AndroidAudio/SonicRoute.AndroidAudio.csproj` | `net10.0-windows` (`Full` only) | Pure C# ADB, Android A/V, Virtual Display, AirPlay | `Concentus 2.2.2`, `SONICROUTE_NATIVE_LOOPBACK`, embeds `sonicroute-catalog.jar` (`99,072` B) |
| `StoreInterop/StoreInterop.csproj` | `net10.0-windows` | Packaged MSIX identity, `StartupTask`, AUMID | Embedded `Microsoft.Windows.CsWinRT 2.3.1` (`WinRT.Runtime.dll` stripped for NET10) |
| `SonicRoute.Legacy/SonicRoute.Legacy.csproj` | `net48`, x64 only | Archived v1.36 Legacy compatibility shell (`WPF` + `NAudio 2.2.1`) | Compiles `..\SonicRoute\**\*.cs` WPF UI; no phone/AirPlay; no Native AOT v2.00 canvas |
| `SonicRoute/SonicRoute.csproj` | `net10.0-windows` (WPF) | Frozen `v1.36` baseline + shared source pool | Shared logic files (`AutoRule*`, `Hotkey*`, `AppPlayback*`, `LocalAppOutputMirrors`, `Updates/*`) compiled into Native via `.targets` |
| `android-helper/` | Java (`AndroidRuntime/sonicroute-catalog.jar`) | On-device helper launched via `app_process` | `SonicRouteServer`, `Catalog`, `ClipboardBridge`, `AudioOutputBridge`, `MediaSessionBridge`, `EncoderCatalog` |

### 3.2 Editions & Distribution Matrix

```yaml
RELEASE_EDITIONS:
  - { edition: Full, runtime: net10_native_aot, architecture: x64, asset: "SonicRoute-v2.00-x64.exe", deployment: SELF_CONTAINED_NATIVE_EXE, defines: [SONICROUTE_PHONE], nuget: [Concentus 2.2.2, QRCoder 1.7.0, CsWinRT 2.3.1(build)], features: "PC Audio + Android Audio/Files/Clipboard/VirtualDisplay + AirPlay" }
  - { edition: Full, runtime: net10_native_aot, architecture: arm64, asset: "SonicRoute-v2.00-arm64.exe", deployment: SELF_CONTAINED_NATIVE_EXE, status: EXPERIMENTAL, defines: [SONICROUTE_PHONE], features: "PC Audio + Android + AirPlay" }
  - { edition: Lite, runtime: net10_native_aot, architecture: x64, asset: "SonicRoute-v2.00-Lite-x64.exe", deployment: SELF_CONTAINED_NATIVE_EXE, features: "PC Audio only (no AndroidAudio/QRCoder)" }
  - { edition: Lite, runtime: net10_native_aot, architecture: arm64, asset: "SonicRoute-v2.00-Lite-arm64.exe", deployment: SELF_CONTAINED_NATIVE_EXE, status: EXPERIMENTAL, features: "PC Audio only" }
RELEASE_METADATA: ["SonicRoute-update.json", "SHA256SUMS.txt"]
MICROSOFT_STORE:
  - { edition: Store, runtime: net10_native_aot, architecture: x64, manifest: "packaging/Store/AppxManifest.xml", package_id: "8B44F47E.SonicRoute", deployment: MSIX_FULL_NATIVE_EXE, features: "Full + StartupTask + AUMID + AirPlay Inbound Firewall Rules", updates: MICROSOFT_STORE }
  - { edition: StoreLite, runtime: net10_native_aot, architecture: x64, manifest: "packaging/Store/AppxManifest.Lite.xml", package_id: "8B44F47E.SonicRouteLite", deployment: MSIX_LITE_NATIVE_EXE, features: "PC Audio + StartupTask + AUMID", updates: MICROSOFT_STORE }
HISTORICAL_PORTABLE_EDITIONS:
  - { edition: Legacy, runtime: net48, architecture: x64, last_release: "v1.36", format: ZIP }
```

### 3.3 Native AOT Host & UI Architecture (`Native/Product`)

```yaml
NATIVE_HOST_&_UI_PIPELINE:
  entry_and_host:
    - "Program.Main ([STAThread]) opens CandidateEnvironment before any Core singleton, installs ExceptionDiagnostics, initializes STA COM, checks UpdateBootstrap/RestartCoordinator, parses CLI (--version, --help, --run-rule, --control), acquires single-instance mutex, and enters NativeApplicationWindow.Run() (blocking GetMessageW/DispatchMessageW)."
    - "NativeApplicationWindow owns one invisible top-level Win32 HWND (to receive WM_DEVICECHANGE, WM_POWERBROADCAST, WM_WTSSESSION_CHANGE, WM_SETTINGCHANGE, TaskbarCreated, RegisterHotKey, clipboard listener, and WinEventHook). Never uses message-only HWND."
    - "ApplicationHost owns lifecycle, ProductAudio, ProductCurrentApp, ProductAutomation, CoalescedVolumeWriter (app, endpoint(Flow,Id), external), PhonePreview, AirPlayPreview, UpdateService, FeedbackState, and UI shells."
  rendering_and_windows:
    - "LayeredWindow (WS_EX_LAYERED + UpdateLayeredWindow premultiplied BGRA DIB + D2dTarget / D2dSharedContext lazy Direct2D/DirectWrite factories) handles per-monitor DPI (WM_DPICHANGED), keyboard/UIA/mouse/wheel/OLE drop, and Win32 InlineEditHost."
    - "MainWindowShell (920x620 default, 780x540 min, collapsible nav rail 196<->64 DIP 180ms CubicOut): hosts Apps, Devices (Full only), Hotkeys, Theme, Settings, Automation, and Experimental (when unlocked & enabled) pages."
    - "Transient layered windows: QuickPanelWindow (372 DIP wide, 350..850 DIP height), PhoneAudioWindowView (235 DIP companion, 6 DIP gap), OutputVolumesPopupWindow, OutputConnectionPopupWindow (304x<=368 DIP), OsdWindow (NoActivate + ClickThrough, 90ms eased bar), TrayMenuController, ColorPickerWindow, DragIconWindow, ReadOnlyWindow, VirtualDisplayStatusWindow."
    - "Cross-thread audio/phone operations run off the UI thread (MTA or ThreadPool) and marshal results back via TryPost / PostContinuation; closing MainWindowShell hides it and releases D2D/DIB buffers in OnHidden()."
```

## 4. Subsystems: Mechanisms, Permissions, Limits & Source Map

```yaml
SYSTEM_ARCHITECTURE_&_FACTUAL_BOUNDS:
  driver_isolation: "NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_TARGET_PROCESS_DLL_INJECTION (PC) | NO_ELEVATION_BY_DEFAULT (asInvoker / runFullTrust)"
  routing_backend: "Undocumented WinRT/COM Windows.Media.Internal.AudioPolicyConfig (Win10 IID 2a59116d-...; Win11 22000+ IID ab3d4648-...) -> Windows PropertyStore persistence"
  persistence_scope: "Windows retains primary PC playback/capture endpoint preferences and active session volumes. Additional simultaneous outputs, hotkeys, tray wheel, automation, Android audio/mirror/clipboard, and AirPlay require SonicRoute running."
  routing_scope: "Per-app endpoint preferences. ResetAllAppRouting invokes ClearAllPersistedApplicationDefaultEndpoints first with per-running-process fallback; preserves volume/mute and system defaults."
  hooks_in_use: "User-mode only: SetWinEventHook (foreground focus tracking, active only when needed), SetWindowsHookEx WH_MOUSE_LL (single shared hook for tray wheel & mouse hotkeys)"
  cross_platform: "The desktop host runs on Windows 10 2004+ and Windows 11. SonicRoute-App is the separately maintained Android companion."

KNOWLEDGE_RETRIEVAL_ROUTER:
  APPS_ROUTING_CANVAS:
    intent: "Visual per-app audio routing canvas, node graph, dock rail, continuous output popup (v2.00)"
    keywords: ["canvas", "wire", "dock", "node", "zoom", "layout", "apps", "画布", "连线", "节点", "应用页"]
    source: ["Native/Product/Views/AppsPageView.cs", "Native/Product/Views/AppsPageView.Canvas.cs", "Native/Product/Views/AppsCanvasEngine.cs", "Native/Product/Views/AppsPageView.Dock.cs", "Native/Product/Views/OutputConnectionPopupWindow.cs"]
    symbols: ["AppsPageView", "AppsCanvasLayoutEngine.Reconcile", "CanvasWireGeometryCache", "OutputConnectionPopupWindow", "AppConfig.AppCanvasLayouts"]
    facts:
      - "Left UiDockRail lists filtered apps (All, Active, Saved, Hidden) with floating search/refresh/filter panel (AppsPageView.Dock.cs)."
      - "Right canvas maintains one reusable node-and-wire state per active app: Application node (primary volume/mute, forward rows, right-edge + port, mic input, switches) wired to Playback nodes (upper-right) and Capture nodes (lower-right when ExperimentalMic=true)."
      - "3 wire types: solid primary/native output (AudioPolicyConfig), dashed capture-forwarding output (LocalAppOutputMirrors/PcmOutputMirrors), emerald-green reverse microphone input (Flow.Capture; disabled for external Android/AirPlay apps)."
      - "AppsCanvasLayoutEngine enforces collision-free playback/capture partitioning (>4 devices per partition switches to 2 columns), preserves user-positioned nodes (Auto=false), and skips layout solves when BuildLayoutSignature is unchanged."
      - "Zoom 0.35..1.80, 6 DIP drag-vs-click threshold, asymmetric low-zoom port hit reach, continuous non-closing OutputConnectionPopupWindow on + click, and 450ms debounced per-app layout save (<=256 apps, <=64 devices/app)."

  ROUTE_CORE_AUDIO:
    intent: "Per-app primary audio routing, default audio devices, endpoint enumeration"
    keywords: ["route", "headset", "speaker", "device", "endpoint", "render", "capture", "分流", "设备", "耳机", "音箱"]
    wiki: "02-功能特性.md"
    source: ["Native/Audio/Endpoints.cs", "Native/Audio/Policy.cs", "Native/Product/Audio/ProductAudio.cs", "Native/Product/Routing/PcRoutingApi.cs", "SonicRoute.Core/AudioService.cs", "SonicRoute.Core/Interop/AudioPolicyConfig.cs"]
    symbols: ["ProductAudio", "PcRoutingApi", "Policy", "Endpoints", "AudioService.ApplyEndpoint", "AudioPolicyConfig.SetDefaultEndPoint"]
    facts:
      - "Native/Audio uses source-generated COM and raw vtable function pointers (no NAudio/RCW) on MTA threads."
      - "Native/Core read routes via AudioPolicyConfig.GetPersistedDefaultAudioEndpoint; missing preference follows Windows system default."
      - "On the Apps canvas, connect a primary device or use + to add a forwarded output with its own volume and mute. In the quick panel, Ctrl-click toggles additional outputs, Shift-click adds a range, and Ctrl+Shift-click chooses a primary PC output while retaining the additional outputs."
      - "SystemDefault device node syncs bidirectionally with actual Windows default render/capture device."

  MULTI_OUTPUT_AND_CAPTURE_FORWARDING:
    intent: "Simultaneous multi-device playback, process loopback capture, independent per-forward gain, PC-to-phone isolation"
    keywords: ["mirror", "split", "simultaneous", "loopback", "gain", "forward", "多设备", "同时播放", "转发", "环回"]
    source: ["SonicRoute/AppPlaybackOutputs.cs", "SonicRoute/AppPlaybackRestore.cs", "SonicRoute/LocalAppOutputMirrors.cs", "Native/Product/Routing/LocalAppOutputMirrors.Native.cs", "Native/Product/Routing/DesktopMirrorPlayer.cs", "Native/Product/Routing/CaptureForwardGainPreferences.cs", "Native/Product/Audio/ExternalAudioBridge.MirrorGains.cs", "SonicRoute.Core/Models/MirrorGainState.cs", "Native/Audio/Streams.cs", "shared/WindowsAudio/PcProcessLoopbackCapture.cs", "Native/Product/Routing/PhoneMirrorIsolation.cs"]
    symbols: ["AppPlaybackOutputs.SetAsync", "AppPlaybackRestore", "LocalAppOutputMirrors", "ProcessCapture", "LoopbackCompletion", "DesktopMirrorPlayer", "PhoneMirrorIsolation"]
    limits_and_permissions:
      - "Requires Windows 10 Build 20348+ for VAD\\Process_Loopback capture; cannot capture SonicRoute itself."
      - "Cross-instance process lock Local\\SonicRoute.AudioMirror.{pid} prevents Full & Lite side-by-side from double-capturing the same PID."
    facts:
      - "One event-driven ProcessCapture per PID feeds multiple DesktopMirrorPlayer (WASAPI render) or AndroidPhonePcmPlayer sinks."
      - "Right-click an active forwarded output’s volume slider on the Apps canvas to set its ceiling (1%–300%, default 100%). Its slider and mute affect only that copy. DesktopMirrorPlayer applies Q15 PCM gain with saturation and passes unmuted audio at 100% through unchanged."
      - "CaptureForwardGains in config.json restores gain, mute and ceiling for a stable app and output pair. During a slider drag, changes apply live. Releasing the slider saves the final value. Sources without a stable identity keep session-local settings."
      - "AppPlaybackRestore persists multi-output selections in SavedPlaybackStore and restores them via PlaybackSessionWatch (IAudioSessionNotification) when apps restart or devices return."
      - "Routing a PC app exclusively to a phone (PhoneMirrorIsolation + LocalSilenceLeasePool + SourceSpeakerLeases) reserves an unoccupied non-default muted endpoint or speaker mute lease via AudioPolicyConfig and rolls back automatically on unplug or external route migration."

  ANDROID_CONNECTION_AND_DISCOVERY:
    intent: "Pure C# ADB transport, USB/Wi-Fi pairing, DNS-SD background discovery, device profiles (Full only)"
    keywords: ["android", "adb", "winusb", "qr", "spake2", "tls", "mdns", "reconnect", "安卓", "连接", "配对", "无线调试"]
    source: ["SonicRoute.AndroidAudio/AdbClient.cs", "SonicRoute.AndroidAudio/DirectAdbTransport.cs", "SonicRoute.AndroidAudio/WinUsbAdbTransport.cs", "SonicRoute.AndroidAudio/AdbSocketClient.cs", "SonicRoute.AndroidAudio/Spake25519.cs", "SonicRoute.AndroidAudio/SspiPairingTls.cs", "SonicRoute.AndroidAudio/AndroidMdnsDiscovery.cs", "SonicRoute.AndroidAudio/AndroidConnectionManager.cs", "SonicRoute.AndroidAudio/AndroidPhoneStore.cs", "Native/Product/Views/PhonePreview.cs", "Native/Product/Views/PhoneDevicePageView.cs"]
    symbols: ["DirectAdbTransport", "ArbitratedAdbTransport", "WinUsbAdbLink", "Spake25519", "SspiPairingTls", "AndroidMdnsDiscovery", "AndroidConnectionManager", "AndroidPhoneStore"]
    limits_and_permissions:
      - "Requires USB debugging or Android 11+ Wireless debugging; WinUSB interface claimed exclusively if another ADB daemon holds the USB interface (reports UsbInterfaceInUse)."
      - "Up to 32 saved phone profiles stored in android-phone.json (migrated from legacy android-phones.json if present)."
    facts:
      - "Pure C# in-process ADB v41 over WinUSB (SetupAPI/WinUSB DMA POH buffers) and TCP/TLS 1.3 (Windows SSPI SChannel); ArbitratedAdbTransport delegates to external 127.0.0.1:5037 if already listening and falls back to DirectAdbTransport when absent."
      - "Pairing card combines ephemeral QR (QRCoder 1.7.0, 2-min cycle) and manual IP:port + 6-digit SPAKE2/Edwards25519 + AES-128-GCM pairing."
      - "Background detection of saved wireless endpoints uses Windows DnsServiceBrowse (AndroidMdnsDiscovery.WatchConnectionsAsync) to avoid triggering Windows firewall prompts on startup when AirPlay is off; explicit connect/search uses short-lived UDP mDNS + identity verification."
      - "AndroidConnectionManager enforces identity fingerprint / serial / transport disambiguation so USB and Wi-Fi aliases never collide or silently merge unverified endpoints."

  ANDROID_APP_SRAP:
    intent: "Native Full connection to the companion Android app on the same LAN"
    source: ["docs/sonicroute-windows-android.md", "Native/Product/Views/PhoneDevicePageView.App.cs", "Native/Product/Views/PhonePreview.SharingRequests.cs", "SonicRoute.AndroidAudio/SrapClient.Nearby.cs", "SonicRoute.AndroidAudio/SrapOperationAdapter.cs", "SonicRoute.AndroidAudio/SrapPcListening.cs", "SonicRoute.AndroidAudio/SrapPcmCapture.cs", "SonicRoute.AndroidAudio/SrapPcmOutput.cs", "SonicRoute.AndroidAudio/SrapScreenViewer.cs", "SonicRoute.AndroidAudio/SrapSession.Alias.cs"]
    pairing: "Open pairing on the phone Computer tab, select the nearby phone in Windows Devices, then compare and confirm the eight digits on both devices. Discovery uses _sonicroute._tcp. Mutual TLS authenticates saved identities after a network change. Revoking trust ends automatic reconnect."
    operations: "From the device row or the phone notification, listen to PC audio, play phone audio on the PC, or share the phone screen. Android requests projection consent and requires an explicit audio source when its scope is unknown. Screen sharing opens a view-only window. An active ADB connection provides independent virtual display and input."
    adb_profile_linking: "With both connections active, the user confirms a signed identity challenge tied to the selected ADB attachment. Merging preserves the original ADB profile ID and lets the user choose which settings to keep and which transport to prefer."
    device_evidence_2026_11_28: "Windows x64 and Xiaomi 13 (Android 16) completed nearby pairing, ADB/app profile linking and a real Wi-Fi leave/return. System media volume changed from 38 to 37 and back to 38 out of 150. The phone confirmed pause and resume of a test WAV. Windows decoded and presented the consented phone projection."
    audio_evidence_2026_11_28: "A selected phone player sent 48 packets containing 2,874 nonzero samples to Windows. In the other direction a dedicated Windows player produced 21,120 nonzero bytes and the phone confirmed 11 packets. These tests identify the source on each end."
    android_local_routing: "On Xiaomi 13, Shizuku Root moved an active third-party player from Bluetooth A2DP to the earpiece or speaker and restored its original output. Android 10 emulator checks covered permission denial, same-output rejection and cleanup after source removal. Physical playback on two phone outputs still needs verification."
    android_home_source: "The Android Sound tab pins installed apps by Android user and signing identity. Expand a pin to switch its output or start simultaneous playback. Capture volume controls the second local output, send volume controls audio shared with the PC, and media volume controls the phone. The home player shows track and artist with available transport controls. Settings offers four persistent theme colors."
    android_background_playback: "During verified native routing or simultaneous playback, direct Root rechecks authorization every 4 min 30 s. Success keeps the track and gain. Failure or expiry releases the route. The expanded playback notification shows the confirmed gain and adjusts the second output in steps of 10 percentage points (0–100%). It also offers mute, restore and stop. A native route can be returned to system output."
    android_permission_preference: "The phone remembers the selected permission mode, including standard mode. Reopening the local UI checks the saved backend before activating it. A background start for PC reception uses standard permissions. After a failed direct Root check, the user can restore the grant and select the mode again."
    android_emulator_evidence_2026_11_28: "On Android 15 with an independent A2DP receiver, a 440 Hz test app switched to Bluetooth while a 960 Hz control stayed on the speaker. The receiver verified the second output at 100%, 37%, mute and restored 37%. In a separate test, direct Root playback continued past five minutes. Listening on two physical outputs still needs verification."
    android_routing_reference: "https://github.com/kunkunkunQoQ/SonicRoute-App/blob/main/docs/native-app-routing.md"
    test_source: ["checks/AndroidAudioChecks/fixtures/README.md", "checks/AndroidAudioChecks/SrapStage2B.cs", "checks/AndroidAudioChecks/SrapCaptureChecks.cs", "checks/AndroidAudioChecks/SrapPcListenChecks.cs", "checks/AndroidAudioChecks/SrapScreenChecks.cs"]

  ANDROID_BIDIRECTIONAL_AUDIO:
    intent: "Phone app/device audio capture to PC, PC audio to phone, SmartVolume, companion media control (Full only)"
    keywords: ["phone audio", "opus", "pcm", "smartvolume", "latency", "companion", "media", "手机声音", "手机音频", "智能音量"]
    source: ["SonicRoute.AndroidAudio/AndroidAudioManager.cs", "SonicRoute.AndroidAudio/AndroidAudioSession.cs", "SonicRoute.AndroidAudio/PcToPhoneAudioManager.cs", "SonicRoute.AndroidAudio/ScrcpyOpusReceiver.cs", "SonicRoute.AndroidAudio/ScrcpyRawPcmReceiver.cs", "SonicRoute.AndroidAudio/AndroidPcmSource.cs", "SonicRoute.AndroidAudio/AudioLatencyProfile.cs", "SonicRoute.AndroidAudio/AndroidMediaSessionClient.cs", "Native/Product/Audio/ExternalAudioBridge.cs", "Native/Product/Views/PhoneAudioWindowView.cs", "Native/Product/Views/CrossDeviceDragHost.cs", "android-helper/src/org/sonicroute/android/audio/PlaybackAudioCapture.java", "android-helper/AudioOutputBridge.java"]
    symbols: ["AndroidAudioManager", "AndroidAudioSession", "PcToPhoneAudioManager", "AndroidPcmSource", "AudioLatencyProfile", "ExternalAudioBridge", "PhoneAudioWindowView", "CrossDeviceDragHost"]
    limits_and_permissions:
      - "The ADB capture helper requires Android 11 (API 30+). The companion uses system projection consent. Protected content and app capture policies can prevent playback capture."
      - "Audio codec/latency settings (Opus via Concentus 2.2.2 vs Raw PCM; Ultra-low / Balanced / Stable) apply per phone on next stream start."
    facts:
      - "Embedded sonicroute-catalog.jar (99,072 B, SHA-256 verified) runs on Android via app_process for app enumeration, icons, media controls, and audio capture/playback."
      - "AndroidAudioSession supports simultaneous multi-output routing (PC endpoints and/or source phone playback) via IMultiOutputAudioSession + PcmOutputMirrors, plus SIMD PCM16 conversion and optional SmartVolume leveling."
      - "PhoneAudioWindowView (235 DIP companion beside QuickPanel) supports cross-device drag-and-drop (PC apps <-> phone speaker/Bluetooth, Android apps <-> PC shared/exclusive/virtual display) and MediaSession seek/play/skip + album art."

  VIRTUAL_DISPLAY_AND_MIRRORING:
    intent: "Per-app Android virtual display, display0 mirroring, H.264/HEVC decoding, IME & gamepad input (Full only)"
    keywords: ["virtual display", "mirror", "hevc", "h265", "h264", "d3d11", "gamepad", "ime", "虚拟屏", "投屏", "手柄"]
    source: ["SonicRoute.AndroidAudio/AndroidVirtualDisplayManager.cs", "SonicRoute.AndroidAudio/InProcessVirtualDisplayClient.cs", "SonicRoute.AndroidAudio/MediaFoundationH264Decoder.cs", "SonicRoute.AndroidAudio/D3D11VideoSurfaceRenderer.cs", "SonicRoute.AndroidAudio/NativeVideoWindow.cs", "SonicRoute.AndroidAudio/NativeGamepadReader.cs", "android-helper/src/org/sonicroute/android/SonicRouteServer.java", "android-helper/src/org/sonicroute/android/video/AdaptiveBitrateController.java", "android-helper/src/org/sonicroute/android/control/UnicodeBroker.java", "android-helper/src/org/sonicroute/android/control/GamepadController.java"]
    symbols: ["AndroidVirtualDisplayManager", "InProcessVirtualDisplayClient", "MediaFoundationH264Decoder", "D3D11VideoSurfaceRenderer", "NativeVideoWindow", "NativeGamepadReader"]
    limits_and_permissions:
      - "H.265 (HEVC) requires both Android device HEVC encoder and Windows Media Foundation HEVC decoder (MediaFoundationH264Decoder.SupportsHevc); falls back to H.264 on unsupported devices or encoder startup failure."
      - "Gamepad input is strictly focus-gated to the foreground virtual display HWND (no RIDEV_INPUTSINK); supports up to 4 XInput/RawInput HID slots."
    facts:
      - "Dedicated Win32 NativeVideoWindow per session runs on its own GetMessageW thread (zero idle CPU), renders D3D11 DXGI_FORMAT_NV12 slices via IMFDXGIDeviceManager (or software NV12 fallback), and supports 8px-aligned flex resize (100ms coalesce) or aspect-locked display0 mirror."
      - "MediaFoundationH264Decoder pools up to 4 input samples (<=2 MiB total) and requests sync frames on stream/corrupt recovery; AdaptiveBitrateController applies separate USB and Wi-Fi policies."

  CLIPBOARD_AND_FILE_TRANSFER:
    intent: "Bidirectional text clipboard sync and shared-storage file transfer through an active USB or wireless ADB attachment (Full only)"
    keywords: ["clipboard", "file", "transfer", "sdcard", "download", "剪贴板", "文件传输", "传文件"]
    source: ["Native/Product/Views/PhoneClipboard.cs", "Native/Product/Views/NativeTextClipboard.cs", "SonicRoute.AndroidAudio/AndroidClipboardClient.cs", "android-helper/ClipboardBridge.java", "SonicRoute.AndroidAudio/AndroidFileTransfer.cs", "SonicRoute.AndroidAudio/AndroidFileTransfers.cs"]
    symbols: ["PhoneClipboard", "AndroidClipboardClient", "NativeTextClipboard", "AndroidFileTransfer", "AndroidFileTransfers"]
    limits_and_permissions:
      - "Clipboard sync is opt-in per device, Unicode text only, <= 64 KiB (AndroidClipboardClient.MaxTextBytes), and subject to Android 10+ background clipboard restrictions unless permitted by shell/ADB context."
      - "File transfer targets Android shared storage (/sdcard, default /sdcard/Download) via ADB sync; rejects unsafe path/control/symlink segments."
    facts:
      - "PhoneClipboard suppresses loops via sequence tracking + SonicRoute.RemoteClipboard.v1, ExcludeClipboardContentFromMonitorProcessing, Clipboard Viewer Ignore, CanUploadToCloudClipboard=0, CanIncludeInClipboardHistory=0, KDE password-manager hint, and password-manager process filtering; disabling a phone cancels its in-flight desktop write."
      - "AndroidFileTransfer uses staged .part temporary files with atomic rename and automatic numeric deduplication, and supports multi-file drop upload on PhoneDevicePageView."

  AIRPLAY_RECEIVER:
    intent: "Self-built C# AirPlay audio and screen-mirroring receiver with multi-output routing (Full only)"
    keywords: ["airplay", "apple", "aac-eld", "alac", "mirror", "pin", "firewall", "投屏", "苹果"]
    source: ["Native/Product/Views/AirPlayPreview.cs", "Native/Product/Views/AirPlayReceiverViewControl.cs", "SonicRoute.AndroidAudio/AirPlay/AirPlayReceiver.cs", "SonicRoute.AndroidAudio/AirPlay/AirPlayDiscovery.cs", "SonicRoute.AndroidAudio/AirPlay/AirPlayAudioOutput.cs", "SonicRoute.AndroidAudio/AirPlay/AirPlayMirrorStream.cs", "SonicRoute.AndroidAudio/AirPlay/AirPlayListenerPorts.cs", "SonicRoute.AndroidAudio/AirPlay/AacEldDecoder.cs"]
    symbols: ["AirPlayPreview", "AirPlayReceiver", "AirPlayAudioOutput", "AirPlayDiscovery", "AirPlayListenerPorts", "AacEldDecoder"]
    limits_and_permissions:
      - "Requires active IPv4 LAN interface (else AirPlayNoNetworkException) and ChaCha20Poly1305 crypto support."
      - "Store Full MSIX declares inbound domainAndPrivate firewall rules for UDP 5353, UDP 319-320, and TCP/UDP 47120-47183 (bound by AirPlayListenerPorts when packaged). Portable builds bind ephemeral ports and rely on user firewall acceptance."
    facts:
      - "Supports PCM, ALAC, AAC-LC, and 44.1/48 kHz stereo AAC-ELD (without SBR; C# filterbank adapted from FFmpeg n8.0 under GPL-3.0-only), plus H.264/HEVC aspect-preserving mirror window (topmost, F11 fullscreen, track metadata/cover)."
      - "Optional 4-digit pairing PIN (max 6 attempts, excludes 3939). AirPlayAudioOutput implements IMultiOutputAudioSession for simultaneous playback on multiple PC or connected Android outputs."

  AUTOMATION_ENGINE:
    intent: "Rule triggers, 39 actions, rule chaining, WMI process monitoring, scheduling, execution history"
    keywords: ["automation", "rule", "trigger", "action", "chain", "schedule", "wmi", "自动化", "规则", "触发器", "动作", "定时"]
    wiki: "04-自动化规则.md"
    source: ["Native/Product/Automation/ProductAutomation.cs", "Native/Monitoring/ProcessTraceSubscription.cs", "SonicRoute/AutoRuleService.cs", "SonicRoute/AutoRuleScheduler.cs", "SonicRoute.Core/Models/AutoRule.cs", "SonicRoute.Core/AutoRuleStore.cs", "SonicRoute.Core/ExecutionHistoryStore.cs", "Native/Product/Views/AutomationPageView.cs"]
    symbols: ["ProductAutomation", "ProcessTraceSubscription", "AutoRuleService.ExecuteAsync", "AutoRuleScheduler", "AutoRuleTrigger", "AutoRuleAction", "ExecutionHistoryStore"]
    facts:
      - "6 Triggers (0..5) x 39 Actions (0..38); Action 17 ExecuteRule enables nested rule chaining; Actions 37/38 mute/toggle mic for runtime current app (capture session required)."
      - "Process rules use MTA native COM WMI (Win32_ProcessStartTrace / Win32_ProcessStopTrace) with 2s..30s backoff recovery; optional 1s ProcessSnapshotService fallback activates only when WMI is unavailable or BackgroundPollingFallbackEnabled=true. Zero process rules = zero WMI/snapshot overhead."
      - "Schedules use a single nearest-due one-shot Timer and skip missed times across sleep/shutdown."
      - "In Full, phone and AirPlay audio sessions can trigger app start and exit rules. An app-switch rule can target an open Android virtual display or AirPlay mirror, including a video-only session. An existing offline target is preserved when editing its rule."
      - "Ordinary settings import leaves automation rules under the Automation page. In-place upgrades still migrate rules from older configurations. Queued settings edits survive a failed import or reset."
      - "Older rules with a missing name, hotkey or app target can open in the editor. Saving applies the normal naming, hotkey and target validation for the selected trigger."
      - "Rules persist as separate JSON files in %LocalAppData%\\SonicRoute\\Automation\\. ExecutionHistory writes rotated JSONL (<=ExecutionHistoryLimitMb MiB) only when ExperimentalMode and ExecutionHistoryEnabled are both true."

  PANEL_TRAY_AND_HOTKEYS:
    intent: "QuickPanel UI, tray wheel, OSD, global keyboard/mouse hotkeys, custom icons"
    keywords: ["panel", "tray", "wheel", "icon", "quickpanel", "hotkey", "osd", "面板", "托盘", "滚轮", "快捷键", "图标"]
    wiki: ["01-使用指南.md", "03-快捷键.md"]
    source: ["Native/Product/Views/QuickPanelWindow.cs", "Native/Product/Views/OutputVolumesPopupWindow.cs", "Native/Product/Views/OsdWindow.cs", "Native/Product/Views/TrayMenuController.cs", "Native/Product/Shell/Tray.cs", "Native/Product/Shell/Mouse.cs", "Native/Product/Audio/PanelPeakMeter.cs", "Native/Product/Ui/NativeAppIconService.cs", "SonicRoute/HotkeyActions.cs", "SonicRoute/HotkeyService.cs", "Native/Product/Automation/NativeMouseBindings.cs"]
    symbols: ["QuickPanelWindow", "OutputVolumesPopupWindow", "OsdWindow", "TrayMenuController", "PanelPeakMeter", "HotkeyActions.All", "NativeAppIconService"]
    facts:
      - "14 built-in global hotkey actions (HotkeyActions.All) + arbitrary Rule:{Id} bindings via RegisterHotKey and shared WH_MOUSE_LL hook (MButton, XButton1/2, WheelUp/Down; Esc clears the recorded binding). Device hotkeys keep original single-direction actions; tray modifier-wheel directions are separate."
      - "Tray wheel (defaults: TrayWheelEverywhere=false, PanelChangeSystemDefault=true): normal wheel -> current app volume; Shift+wheel -> system default output volume; Alt+wheel -> cycle current app output; Alt+Shift+wheel -> cycle system default output. Hotkeys queue non-blocking device switches with 45ms fast-complete / 500ms min-pending OSD thresholds."
      - "QuickPanel height: 350..850 DIP (constrained by work area). Deferred OutputVolumesPopupWindow (QuickPanelShowAllOutputVolumes=false default; PanelOutputVolumesRetainedOnly=false filters with HiddenOutputDevices) subscribes only while expanded; quick-panel endpoint volume/mute avoids redundant OSD."
      - "Peak meter runs at 50ms only while visible and ShowAppPeakMeter=true."
      - "Expand a local app in the quick panel and Ctrl-click a second playback device to capture and forward that app’s audio while keeping its primary output. With ExperimentalMic and MicInPanel enabled, the same row also lets the user choose a recording device (Flow.Capture)."
      - "Custom PNG/JPEG/ICO drop import stores managed PNGs in %LocalAppData%\\SonicRoute\\Icons\\ via NativeAppIconService."

  CLI_AND_CONTROL_IPC:
    intent: "Rule CLI (sr / --run-rule) and isolated Native AOT control IPC (--control)"
    keywords: ["cli", "sr", "command", "control", "stream deck", "ahk", "pipe", "命令行", "管道"]
    wiki: "05-命令行执行规则.md"
    source: ["Native/Product/Program.cs", "Native/Product/Protocol/ProductInstance.cs", "Native/Product/Protocol/CandidateEnvironment.cs", "Native/Product/HostCommands.cs", "SonicRoute/RuleCommandLine.Parse.cs", "SonicRoute/RuleCommandLine.Selection.cs", "SonicRoute/RuleCommandLine.Wire.cs", "SonicRoute/Resources/RuleCommand.ps1"]
    symbols: ["Program.Start", "ProductInstance", "RuleCommandLine", "CandidateEnvironment"]
    facts:
      - "Rule execution: sr <RuleName|RuleId> (invokes %LocalAppData%\\SonicRoute\\Command\\sr.cmd -> sr-client.ps1; targets running instance, exits 6 if absent; supports --full/--lite) OR SonicRoute.exe --run-rule <selector> (cold-boots temporary host if absent)."
      - "Rule pipe: \\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}; 250ms (sr) / 5000ms (--run-rule) connect timeout, 10s write timeout, waits for completion; exit codes 0..8."
      - "Isolated diagnostic control CLI (--control <verb> [args]): active only when CandidateEnvironment.IsIsolated=true (exits 2 if unisolated) over \\\\.\\pipe\\SonicRoute.NativeControl.{CommandEdition}.{SessionId}.{UserSid}; supports status, activate, panel, osd, language, current, devices, apps, volume, volume-queue, volume-flush, route, outputs-get, outputs-set, outputs-clear-saved, mirror-status, cycle-output, output-state, automation-status, exit, restart, install."

  UPDATE_AND_RELEASE:
    intent: "Portable self-update Protocol 2/1, SHA-256 verification, Store isolation, release & packaging pipelines"
    keywords: ["update", "download", "rollback", "manifest", "protocol", "sha256", "store", "msix", "更新", "回滚", "商店包"]
    source: ["SonicRoute/Updates/UpdateService.cs", "SonicRoute/Updates/UpdateTransport.cs", "SonicRoute/Updates/UpdateProtocol.cs", "SonicRoute/Updates/UpdateInstaller.cs", "SonicRoute/Updates/UpdateBootstrap.cs", "Native/Product/Protocol/ProductProtocol.cs", "packaging/Updates/Build-PortableRelease.ps1", "packaging/Updates/ReleaseLayout.ps1", "packaging/Store/Build-Package.ps1", "packaging/Updates/README.md", "packaging/Store/README.md"]
    symbols: ["UpdateService", "UpdateProtocol.ValidatePackage", "UpdateBootstrap.BeforeStartup", "ProductProtocol.InstallCachedAsync"]
    facts:
      - "Portable auto-check defaults off; if enabled and >=24h since LastUpdateCheckUtc, startup warmup + single 15s delay performs one async check against GitHub stable releases/latest (no recurring timer; drafts/prereleases rejected; download and install require user action). Store editions disable the updater completely."
      - "Protocol 2 selects an exact edition, runtime and architecture. The 2.00 manifest lists Full and Lite on net10 for x64 and arm64. Installers verify the declared length and SHA-256 against official GitHub release metadata."
      - "Build-PortableRelease.ps1 compares source state before and after packaging. For a modified checkout, the build receipt includes workingSetSha256 over the staged entries and the changed or untracked file inventory."
      - "Legacy/net48 and retired Lite/net8 clients have no matching 2.00 update. To switch editions, manually choose a Full or Lite EXE for the PC architecture. v1.24 has no native updater."
      - "Trusted local EXE spawns worker mode to backup, swap, restart, and roll back on failure; fails cleanly on protected paths without silent elevation; user data is never touched."

  VERSION_ARCHIVE_AND_CHANGELOG:
    intent: "Version history from v1.24 through v2.00"
    keywords: ["changelog", "history", "version", "v2.00", "v1.36", "v1.32", "v1.24", "更新日志", "版本历史"]
    wiki: "09-版本历史.md"
    source: ["docs/release-v2.00.md", "docs/release-v1.36.md"]
    symbols: ["v2.00"]
    facts:
      - "v2.00 — 全新开始 (A New Beginning). .NET 10 Native AOT + Win32/Direct2D/DirectWrite UI, redesigned Apps routing canvas with volume, mute and ceiling saved per app/output pair, adaptive UiDockRail, unified Devices tab, Windows DNS-SD Android background browse, --control isolated IPC."
      - "v2.00 Full and the Android 1.0 companion support nearby pairing, two-way audio, screen viewing and a shared ADB/app device profile. See ANDROID_APP_SRAP for the device evidence and transport-specific tools."
      - "v1.36 (published 2026-11-24): Shared Apps/QuickPanel multi-output selections (Ctrl/Shift/Ctrl+Shift), persistent sidebar collapse, AudioDeviceNotificationHub, AirPlay multi-output, Android 11+ Opus/PCM + 3 latency modes, AdaptiveBitrateController, Store AUMID & AirPlay firewall rules."
      - "v1.34-v1.35: Multi-PC playback selection & restart restore, shared WH_MOUSE_LL hook, optimistic QuickPanel/external switching, 45ms/500ms OSD thresholds, Android file browser/transfer, clipboard cancellation on phone disable."
      - "v1.32-v1.33: Pure C# ADB/TLS1.3/SPAKE2/MediaFoundation/D3D11 backend (removing adb.exe/scrcpy/FFmpeg/SDL2), self-built C# AirPlay audio/mirror receiver (PCM/ALAC/AAC-LC/AAC-ELD), H.265 + H.264 fallback, focus-gated 4-slot gamepad, unified QR+PIN pairing & mDNS recovery, StoreLite MSIX, GPL-3.0-only transition (2026-11-17)."
      - "v1.24-v1.31: .NET 10 & Protocol 2 updater (v1.25; NET8 retired v1.29), single QuickPanel + output volume popup (v1.27-v1.29), phone audio + virtual displays + 39 automation actions (v1.30), display0 mirror + 64 KiB text clipboard sync + 9 locales (v1.31)."
```

## 5. Resource, Idle-CPU & Memory Policy

```yaml
RESOURCE_AND_PERFORMANCE:
  event_driven_first: "WinEventHook (only while foreground tracking needed), CoreAudio IMMNotificationClient/IAudioSessionNotification (AudioDeviceNotificationHub / DeviceNotificationHub), Win32 device/power/session/theme/time broadcasts, MTA WMI process trace, Windows DnsServiceBrowse."
  coalescing_and_caching:
    - "CoalescedVolumeWriter batches rapid slider/drag volume writes (90ms coalesce) on app, (Flow,Id) endpoint, and external source streams."
    - "AppsCanvasLayoutSave debounces at 450ms; CanvasWireGeometryCache reuses ID2D1PathGeometry when endpoints move <0.25f and prunes unused wires every frame; AudioService device enumeration uses 3s generation-invalidated cache."
  active_samplers_and_animations:
    - "MainWindowShell & QuickPanelWindow run 16ms frame timers ONLY during active transitions (sidebar 180ms, quick-panel entrance 210ms, drag edge scroll) and stop immediately on completion or hide."
    - "QuickPanel peak meter samples only while visible & ShowAppPeakMeter=true: 50ms (PanelPeakMeter); fades out then pauses until audio activity notifications."
  window_close_and_reclaim:
    - "Closing MainWindowShell hides it and calls OnHidden() -> ReleaseRenderResources() (releases D2D target & GDI DIB) and page OnDeactivated() (unsubscribes UI events & releases geometry/icon transients)."
    - "Native Full preserves the working set of active phone, audio and display sessions when windows close."
```

## 6. Build, Verification & Isolated Testing

```powershell
# Build Native AOT Full & Lite (default output: ../outputs/native-product/candidate/<Edition>-<Arch>/)
# Fails if any IL2xxx/IL3xxx trimming/AOT diagnostics appear.
./Native/Build-Product.ps1 -Edition Full -Architecture x64
./Native/Build-Product.ps1 -Edition Lite -Architecture x64

# Build the four Native AOT EXEs (Full/Lite, x64/arm64), Protocol 2 manifest and SHA-256 file
./packaging/Updates/Build-PortableRelease.ps1 -OutputDirectory ../outputs/release-candidate

# Package unsigned Store MSIX (Full x64 Native AOT)
./packaging/Store/Build-Package.ps1 -Edition Full

# Verify 10 generated READMEs, Core dual-target build, and 9-locale JSON contracts
node docs/readme/generate.cjs --check
dotnet build SonicRoute.Core/SonicRoute.Core.csproj -c Release
dotnet run --project packaging/Tests/SerializationChecks/Checks.csproj -c Release -f net10.0-windows

# Check the Windows SRAP client with local fixtures
$checks = "checks/AndroidAudioChecks"
$run = Join-Path $env:TEMP ("sonicroute-srap-" + [guid]::NewGuid())
dotnet run --project $checks -c Release -- --srap-stage2a "$run/pairing"
dotnet run --project $checks -c Release -- --gemini-android-contract "$run/protocol" "$checks/fixtures/srap-v1.txt"
dotnet run --project $checks -c Release -- --srap-stage2b "$run/linking" "$checks/fixtures/srap-alias-v1.txt"
dotnet run --project $checks -c Release -- --srap-nearby-vectors "$checks/fixtures/srap-nearby-v1.txt"
dotnet run --project $checks -c Release -- --srap-business "$run/business" "$checks/fixtures/srap-business-v1.txt"

# Isolated candidate run (never touches user %LocalAppData%\SonicRoute\ or grabs running hotkeys)
$exe = "../outputs/native-product/candidate/Full-x64/SonicRoute.exe"
$env:SONICROUTE_CANDIDATE_ROOT = (Resolve-Path "../outputs/native-product").Path
$env:SONICROUTE_CANDIDATE_ID = "dev-isolated"
$env:SONICROUTE_DISABLE_DEFAULT_HOTKEYS = "1"
$env:SONICROUTE_CANDIDATE_METRICS = "1"   # Optional: writes last-exit.json COM/MTA/GC receipt on exit
Start-Process -FilePath $exe -WindowStyle Hidden
& $exe --control status
& $exe --control exit
```

- **Additional test suites**: `Native/ProductChecks/Test-ProductMirrors.ps1`, `Native/ProductChecks/Test-ProductUpdate.ps1`, `Native/Measurements/Measure-Product.ps1`, `checks/AndroidAudioChecks`, `checks/AndroidUiChecks`, `checks/EditionChecks`, `checks/ReliabilityChecks`, `packaging/Tests/*`, `tools/SonicRoute.BenchmarkLab`.

## 7. Domain Enums, CLI Exit Codes & Data Paths

```yaml
CLI_AND_SHORT_COMMAND_SPEC:
  command_invocation: "sr <RuleName|RuleId> (invokes %LocalAppData%\\SonicRoute\\Command\\sr.cmd -> sr-client.ps1) OR SonicRoute.exe --run-rule <selector>"
  pipe_endpoint: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"
  edition_flags: "Use sr --full or sr --lite to target a running 2.00 portable instance."
  exit_codes:
    0: { code: ok, desc: "Execution succeeded without step failures" }
    1: { code: step_failed, desc: "One or more rule steps failed" }
    2: { code: invalid_arguments, desc: "Empty selector, invalid arguments, or unisolated --control" }
    3: { code: rule_not_found, desc: "Rule ID or name not found in rules store" }
    4: { code: ambiguous_rule, desc: "Selector matches multiple rules" }
    5: { code: rule_busy, desc: "Target rule is currently executing" }
    6: { code: server_unavailable, desc: "Named pipe server unavailable (instance not running)" }
    7: { code: canceled, desc: "Execution canceled or app shutdown in progress" }
    8: { code: internal_error, desc: "Unhandled exception during execution" }

DOMAIN_ENUM_REGISTRY:
  AutoRuleTrigger:
    0: { name: Hotkey, scope: GLOBAL_INPUT, desc: "Key combination or mouse button" }
    1: { name: AppStart, scope: PROCESS_EVENT, desc: "Process creation via WMI Trace / fallback snapshot" }
    2: { name: AppSwitch, scope: FOREGROUND_EVENT, desc: "Foreground window process change" }
    3: { name: AppExit, scope: PROCESS_EVENT, desc: "Process termination via WMI Trace / fallback snapshot" }
    4: { name: Schedule, scope: TIME_EVENT, desc: "Once / Daily / Weekly one-shot schedule" }
    5: { name: Startup, scope: LIFECYCLE_EVENT, desc: "SonicRoute boot completion (once per run)" }

  AutoRuleAction:
    0: { name: SetSystemOutput, target: DEFAULT_RENDER_DEVICE, param: DeviceID }
    1: { name: SetSystemInput, target: DEFAULT_CAPTURE_DEVICE, param: DeviceID }
    2: { name: SetSystemVolume, target: SYSTEM_MASTER_VOLUME, param: "0..100" }
    3: { name: ToggleSystemMute, target: SYSTEM_MASTER_VOLUME, param: TOGGLE }
    4: { name: SetAppVolume, target: APP_PLAYBACK_SESSION, param: "0..100" }
    5: { name: ToggleAppMute, target: APP_PLAYBACK_SESSION, param: TOGGLE }
    6: { name: SetAppOutput, target: APP_PLAYBACK_ROUTING, param: DeviceID }
    7: { name: SetAppInput, target: APP_CAPTURE_ROUTING, param: DeviceID }
    8: { name: LaunchProgram, target: OS_EXEC, param: "LaunchItems (Path, OpenWith), LaunchMode" }
    9: { name: RunPowerShell, target: OS_EXEC, param: "ProgramPaths (script path or command)" }
    10: { name: ShowOsd, target: UI_HUD, param: "OsdTitle, OsdText" }
    11: { name: SetSystemMute, target: SYSTEM_MASTER_VOLUME, param: BOOL_EXPLICIT }
    12: { name: SetAppMute, target: APP_PLAYBACK_SESSION, param: BOOL_EXPLICIT }
    13: { name: AdjustSystemVolume, target: SYSTEM_MASTER_VOLUME, param: "DeltaPercent (+/-)" }
    14: { name: AdjustAppVolume, target: APP_PLAYBACK_SESSION, param: "DeltaPercent (+/-)" }
    15: { name: SetGlobalMicMute, target: ALL_CAPTURE_ENDPOINTS, param: BOOL_EXPLICIT }
    16: { name: ToggleGlobalMicMute, target: ALL_CAPTURE_ENDPOINTS, param: TOGGLE }
    17: { name: ExecuteRule, target: AUTOMATION_ENGINE, param: "RuleID (Chained Execution)" }
    18: { name: SetMicrophoneMute, target: SPECIFIC_CAPTURE_ENDPOINT, param: "DeviceID, BOOL_EXPLICIT" }
    19: { name: ToggleMicrophoneMute, target: SPECIFIC_CAPTURE_ENDPOINT, param: "DeviceID, TOGGLE" }
    20: { name: SetPlaybackDeviceVolume, target: SPECIFIC_RENDER_ENDPOINT, param: "DeviceID, 0..100" }
    21: { name: AdjustPlaybackDeviceVolume, target: SPECIFIC_RENDER_ENDPOINT, param: "DeviceID, DeltaPercent" }
    22: { name: SetPlaybackDeviceMute, target: SPECIFIC_RENDER_ENDPOINT, param: "DeviceID, BOOL_EXPLICIT" }
    23: { name: TogglePlaybackDeviceMute, target: SPECIFIC_RENDER_ENDPOINT, param: "DeviceID, TOGGLE" }
    24: { name: SetDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: "0..100" }
    25: { name: AdjustDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: DeltaPercent }
    26: { name: SetDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: BOOL_EXPLICIT }
    27: { name: ToggleDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: TOGGLE }
    28: { name: SetAppInputMute, target: APP_CAPTURE_SESSION, param: "ProcessName, BOOL_EXPLICIT" }
    29: { name: ToggleAppInputMute, target: APP_CAPTURE_SESSION, param: "ProcessName, TOGGLE" }
    30: { name: SetAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: "0..100" }
    31: { name: AdjustAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: DeltaPercent }
    32: { name: SetAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: BOOL_EXPLICIT }
    33: { name: ToggleAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: TOGGLE }
    34: { name: ResetAllAppRouting, target: ALL_PERSISTED_APP_ROUTES, param: RESET_TO_DEFAULT }
    35: { name: ShowQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_SHOW }
    36: { name: HideQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_HIDE }
    37: { name: SetCurrentAppInputMute, target: RUNTIME_CURRENT_APP_CAPTURE_SESSION, param: BOOL_EXPLICIT }
    38: { name: ToggleCurrentAppInputMute, target: RUNTIME_CURRENT_APP_CAPTURE_SESSION, param: TOGGLE }

DATA_PATHS_&_CONFIG_SCHEMA:
  root_dir: "%LocalAppData%\\SonicRoute\\"
  config_file: "%LocalAppData%\\SonicRoute\\config.json (+ config.json.bak; atomic temp write)"
  saved_playback: "%LocalAppData%\\SonicRoute\\playback-outputs.json (SavedPlaybackStore)"
  rules_dir: "%LocalAppData%\\SonicRoute\\Automation\\*.json"
  command_dir: "%LocalAppData%\\SonicRoute\\Command\\ (sr.cmd, sr-client.ps1)"
  icons_dir: "%LocalAppData%\\SonicRoute\\Icons\\"
  history_dir: "%LocalAppData%\\SonicRoute\\ExecutionHistory\\{CommandEdition}\\ (optional; recent.jsonl + previous.jsonl)"
  updates_dir: "%LocalAppData%\\SonicRoute\\Updates\\"
  phone_data_full: "android-phone.json, android-audio.json, phone-clipboard.json, android-file-receive-folder.txt, PhoneRuntime\\ (extracted jar)"
  root_override: "SONICROUTE_DATA_DIRECTORY (absolute path) or SONICROUTE_CANDIDATE_ROOT + SONICROUTE_CANDIDATE_ID (isolated candidate root)"
  pipe_pattern: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"
```

## 8. Agent Troubleshooting Matrix & Development Constraints

```yaml
QUERY_DECISION_MATRIX_FOR_AGENTS:
  WHEN user_says_routing_or_mirror_fails:
    CHECK_1: "Verify app has an active WASAPI session (AudioService / ProductAudio excludes expired sessions). Distinguish icon/process metadata failure from actual AudioPolicyConfig HRESULT failure."
    CHECK_2: "For multi-output mirrors / capture forwarding, verify OS is Windows 10 Build 20348+ (VAD\\Process_Loopback); check if another SonicRoute edition holds Local\\SonicRoute.AudioMirror.{pid}."
    CHECK_3: "For protected/anti-cheat games, record game title, privilege level, and audio mode; pre-launch routing is only a possible workaround, not a guaranteed fix."
  WHEN user_says_phone_or_airplay_fails:
    CHECK_1: "Use Full for Android or AirPlay connections. Lite contains the Windows routing tools."
    CHECK_2: "For ADB, check USB debugging or wireless debugging on the same LAN. UsbInterfaceInUse means another host holds the USB interface. The ADB capture helper requires Android 11 or newer."
    CHECK_3: "AirPlay requires active IPv4 LAN interface and inbound LAN firewall allowance (UDP 5353, 319-320, TCP/UDP 47120-47183)."
    CHECK_4: "For the companion, open nearby pairing on the phone, compare the code on both devices and confirm saved trust. For audio or screen sharing, check the negotiated capability, Android projection consent and selected source. File transfer, clipboard and independent virtual display require active ADB."
  WHEN user_asks_runtime_error:
    NATIVE_AOT_v200: "Native AOT Full and Lite bundles include the runtime; no .NET Desktop Runtime install is needed."
    WPF_v136_RELEASE: "Published WPF v1.36 Full and Lite require .NET 10 Desktop Runtime (matching x64 or ARM64); base .NET Runtime alone fails."
    HISTORICAL_LEGACY_v136: "Legacy v1.36 x64 requires .NET Framework 4.8 and full ZIP extraction."
  WHEN user_asks_cpu_or_memory:
    RESPONSE_FACT: "Event-driven notifications drive updates. Process rules use native WMI with optional 1s fallback. Peak meters run only while QuickPanel is visible and enabled (50ms). Hiding MainWindowShell releases D2D/DIB targets; Native AOT/Full never forces SetProcessWorkingSetSize."

DEVELOPMENT_CONSTRAINTS:
  - "AOT & Trimming: Product.csproj enforces PublishAot=true and JsonSerializerIsReflectionEnabledByDefault=false. Register all new persisted/IPC JSON types in PersistenceJsonContext / HostJson / AndroidJsonContext / UpdateManifestContext; never introduce WPF, WinForms, XAML, NAudio, or System.Management into Native/."
  - "Single Version & README Source: Set the application version in build/Version.props; update the documentation version in docs/readme/shared.json, the localized summaries in docs/readme/releases/*, docs/readme/locales/* + renderAI() in docs/readme/generate.cjs and pass node docs/readme/generate.cjs --check (enforced by CI)."
  - "Reuse & Idle-CPU Rules: Reuse LayeredWindow, D2dRenderer, UiPrimitives, UiDockRail, ProductAudio, ProductRouting, and CoalescedVolumeWriter; avoid new background polling timers and release UI/D2D/subscription resources on window close/deactivation."
  - "Isolated Testing: Never overwrite user %LocalAppData%\\SonicRoute\\ or kill running user SonicRoute processes; always test candidates with SONICROUTE_CANDIDATE_ROOT + SONICROUTE_CANDIDATE_ID + SONICROUTE_DISABLE_DEFAULT_HOTKEYS=1."
```

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>
