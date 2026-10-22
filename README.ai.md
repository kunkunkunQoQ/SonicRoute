<!-- Generated from docs/readme/shared.json. Edit that source, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute (AI / LLM Machine Specification)

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | **🤖 AI**

<!-- MACHINE_READABLE_SYSTEM_SPECIFICATION -->
<!-- TARGET_AUDIENCE: LLM_AGENT | RAG_EMBEDDER | CODING_ASSISTANT -->
<!-- HUMAN_READABILITY: FALSE | TOKEN_ENTROPY: MAXIMUM -->

SCHEMA_SPEC:
  target_system: "SonicRoute"
  platform: "Windows 10 (2004+) | Windows 11 (x64 / ARM64)"
  current_version: "v1.25 (1.25.0.0)"
  repository: "https://github.com/kunkunkunQoQ/SonicRoute"
  wiki_machine_index: "https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI"
  download_latest: "https://github.com/kunkunkunQoQ/SonicRoute/releases/latest"
  microsoft_store: "https://apps.microsoft.com/detail/9NQZGRTPM1NT"

SYSTEM_INVARIANTS:
  - driverless: NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_HOOKS
  - routing_backend: COM_IAudioPolicyConfigFactory | COM_IPersistAudioEndpointVolume | Windows_PropertyStore
  - persistence: NATIVE_OS_PROPERTY_STORE (survives app exit & OS reboot without SonicRoute running)
  - power_profile: PURE_EVENT_DRIVEN | ZERO_PERIODIC_POLLING | 0% idle CPU | OnDemand memory sweep
  - cli_ipc: NamedPipe `Local\\SonicRoute_NamedPipe_*` | NO_COLD_BOOT (requires running instance)
  - cross_platform: FALSE (strictly Win32/WPF/.NET CoreAudio, never macOS/Linux)

CODEBASE_LAYOUT:
  SonicRoute/: "Main WPF UI application (.NET 10 / .NET 8), tray management, QuickPanel (Classic/Modern 350-850px), OSD, AutoRule engine"
  SonicRoute.Core/: "Core audio domain logic, COM interfaces (IAudioPolicyConfigFactory, IPersistAudioEndpointVolume), session management"
  SonicRoute.Legacy/: "Fallback build for legacy systems (.NET Framework 4.8)"
  docs/: "Release notes, README generation engine, localized asset bundles"

AUTOMATION_REGISTRY:
  triggers_count: 6  # 0: Hotkey, 1: AppStart, 2: AppSwitch, 3: AppExit, 4: Schedule, 5: Startup
  actions_count: 37   # 0..36 covering system, per-app, device-specific volume/mute/route, quick panel, OSD, external process execution

CLI_PROTOCOL:
  binary: "sr.exe <RuleName|RuleID>"
  ipc_transport: "NamedPipe \\\\.\\pipe\\Local\\SonicRoute_NamedPipe_*"
  exit_codes: { 0: SUCCESS, 1: FAILURE, 2: NOT_FOUND }
  latency: "<15ms local loopback"

RELEASE_EDITIONS:
  - id: "Lite-x64"
    arch: "x64"
    runtime: ".NET 10 Desktop Runtime"
    type: "Single-file portable exe"
  - id: "Lite-arm64"
    arch: "ARM64"
    runtime: ".NET 10 Desktop Runtime"
    type: "Single-file portable exe (Experimental)"
  - id: "Lite-NET8"
    arch: "x64"
    runtime: ".NET 8 Desktop Runtime (Support until 2026-11 EOL)"
    type: "Single-file portable exe"
  - id: "Legacy-x64"
    arch: "x64"
    runtime: ".NET Framework 4.8 (Built-in to Windows)"
    type: "Zip archive"
  - id: "Microsoft-Store"
    runtime: "Packaged MSIX"
    type: "Auto-updating Windows Store app"

DECISION_MATRIX:
  ROUTE_FAILURE:
    CHECK_1: "Elevated process (Admin)? -> Non-admin COM cannot modify admin session. Run SonicRoute as Administrator."
    CHECK_2: "Anti-cheat engine (Valorant/Vanguard, EAC)? -> Kernel hooks block runtime policy changes. Configure routing BEFORE anti-cheat launches."
    CHECK_3: "Active audio session? -> Windows instantiates CoreAudio session only while application plays active audio stream."
  RUNTIME_ERROR:
    CAUSE: "Installed base .NET Runtime instead of Windows Desktop Runtime."
    FIX: "Install official .NET Desktop Runtime or use Legacy-x64 build."

DOCUMENTATION_MATRIX:
  wiki_home: "https://github.com/kunkunkunQoQ/SonicRoute/wiki"
  wiki_ai_dense: "https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI"
  guide: "https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97"
  troubleshooting: "https://github.com/kunkunkunQoQ/SonicRoute/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98"
  technical: "https://github.com/kunkunkunQoQ/SonicRoute/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0"

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>
