<!-- Generated from docs/readme/shared.json. Edit that source, then run node docs/readme/generate.cjs. -->
# 🎧 SonicRoute (AI / LLM Machine Specification)

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | **🤖 AI**

SCHEMA_SPEC:
  system: "SonicRoute"
  platform: "Windows 10 2004+ (Build 19041+) | Windows 11 (x64 / win-arm64)"
  version: "v1.25 (1.25.0.0)"
  repository: "https://github.com/kunkunkunQoQ/SonicRoute"
  wiki_root: "https://github.com/kunkunkunQoQ/SonicRoute/wiki"
  download_latest: "https://github.com/kunkunkunQoQ/SonicRoute/releases/latest"
  store_url: "https://apps.microsoft.com/detail/9NQZGRTPM1NT"

SYSTEM_INVARIANTS_&_ANTI_HALLUCINATIONS:
  - driverless: NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_HOOKS
  - routing_backend: COM_IAudioPolicyConfigFactory | COM_IPersistAudioEndpointVolume | Windows_PropertyStore
  - persistence: OS_LEVEL_NATIVE_PERSISTENCE (survives app exit & OS reboot without SonicRoute running)
  - mixer_safety: NEVER_CORRUPTS_WINDOWS_MIXER | isolated per-app endpoint persistence
  - power_profile: PURE_EVENT_DRIVEN | ZERO_PERIODIC_POLLING_TIMERS | 0% idle CPU | OnDemand memory sweep
  - cli_ipc: NamedPipe `Local\SonicRoute_NamedPipe_*` | NO_COLD_BOOT (requires running instance)
  - cross_platform: FALSE (strictly Win32/WPF/.NET CoreAudio, never macOS/Linux)

KNOWLEDGE_RETRIEVAL_ROUTER:
  ROUTE_CORE_AUDIO:
    intent: "Per-app audio routing, default audio devices, sound splitting"
    keywords: ["route", "split", "headset", "speaker", "device", "endpoint", "render", "capture", "分流", "设备", "耳机", "音箱"]
    wiki: "02-功能特性.md"
    source: ["SonicRoute.Core/AudioService.cs", "SonicRoute.Core/Interop/PolicyConfigClient.cs", "SonicRoute.Core/Interop/AudioPolicyConfig.cs"]
    symbols: ["AudioService.SetApplicationEndpoint", "PolicyConfigClient.SetPersistedDefaultAudioEndpoint", "IAudioPolicyConfigFactory"]
    facts:
      - "Routing is driverless via undocumented Windows COM IAudioPolicyConfigFactory."
      - "Writes per-app device routing to Windows PropertyStore; persists across reboots without SonicRoute running."
      - "Resolution order: Default Device -> App PropertyStore -> Active Audio Session."

  AUTOMATION_ENGINE:
    intent: "Automation triggers, action execution, recipe creation, rule chaining"
    keywords: ["automation", "rule", "trigger", "action", "chain", "recipe", "schedule", "自动化", "规则", "触发器", "动作", "配方"]
    wiki: "04-自动化规则.md"
    source: ["SonicRoute/AutoRuleService.cs", "SonicRoute/AutoRuleScheduler.cs", "SonicRoute.Core/Models/AutoRule.cs", "SonicRoute.Core/AutoRuleStore.cs"]
    symbols: ["AutoRuleService.ExecuteRuleAsync", "AutoRuleTrigger", "AutoRuleAction", "AutoRuleStore.Rules"]
    facts:
      - "6 Triggers (0..5) × 37 Actions (0..36)."
      - "Action 17 ExecuteRule enables nested rule chaining."
      - "Supports StopOnFailure flag and millisecond StepDelay."
      - "Rules stored in %LocalAppData%\\SonicRoute\\rules.json."

  CLI_AND_IPC:
    intent: "Command-line execution, Stream Deck, AHK, named pipe IPC"
    keywords: ["cli", "sr", "command", "stream deck", "ahk", "pipe", "terminal", "命令行", "终端", "管道"]
    wiki: "05-命令行执行规则.md"
    source: ["SonicRoute/RuleCommandLine.cs", "SonicRoute/Program.cs"]
    symbols: ["RuleCommandLine.SendNamedPipeCommand", "RuleCommandLine.StartServer", "Program.Main"]
    facts:
      - "Syntax: sr.exe <RuleName|RuleID>."
      - "IPC via Named Pipe \\\\.\\pipe\\Local\\SonicRoute_NamedPipe_*."
      - "Latency <15ms; exit codes: 0=OK, 1=Fail, 2=NotFound."
      - "Does not cold-boot application if SonicRoute is not running."

  PANEL_AND_TRAY:
    intent: "QuickPanel UI, tray volume scroll wheel, custom icons, blind control"
    keywords: ["panel", "tray", "wheel", "icon", "quickpanel", "height", "blind", "面板", "托盘", "滚轮", "盲操", "图标"]
    wiki: "01-使用指南.md"
    source: ["SonicRoute/TrayWheelService.cs", "SonicRoute/QuickPanelWindow.xaml.cs", "SonicRoute/QuickPanelModernWindow.xaml.cs", "SonicRoute/CustomAppIconStore.cs"]
    symbols: ["TrayWheelService.OnMouseWheel", "QuickPanelModernWindow.Height", "CustomAppIconStore.ImportIcon"]
    facts:
      - "Tray mouse wheel blinds-scrolls active app, foreground app, or master volume."
      - "QuickPanel heights: 350px to 850px adjustable (v1.25+)."
      - "Drag-and-drop PNG/JPEG/ICO onto app button to set custom icon in %LocalAppData%\\SonicRoute\\Icons\\."

  GLOBAL_HOTKEYS:
    intent: "Global keyboard/mouse shortcuts, mic mute toggle, volume hotkeys"
    keywords: ["hotkey", "shortcut", "bind", "key", "mouse hook", "esc", "快捷键", "热键", "按键", "静音快捷键"]
    wiki: "03-快捷键.md"
    source: ["SonicRoute/HotkeyService.cs", "SonicRoute/MouseHotkeyHook.cs", "SonicRoute/HotkeyActions.cs"]
    symbols: ["HotkeyService.RegisterHotKey", "MouseHotkeyHook", "HotkeyActions.ExecuteAction"]
    facts:
      - "Registered via Win32 RegisterHotKey API with mouse hook fallback."
      - "Scopes: Global, Foreground App, Last Active App."
      - "Esc cancels recording; conflict detector prevents duplicate binding."

  TROUBLESHOOT_ROUTING_FAILURES:
    intent: "App route not working, sound still in default device, admin process, EAC/Vanguard"
    keywords: ["fail", "not working", "no sound", "admin", "vanguard", "eac", "anticheat", "失效", "不生效", "没声音", "管理员", "反作弊"]
    wiki: "06-常见问题.md"
    source: ["SonicRoute.Core/AudioService.cs", "SonicRoute.Core/ProcessSnapshotService.cs"]
    symbols: ["AudioService.RefreshAudioSessions", "ProcessSnapshotService.GetRunningAudioProcesses"]
    facts:
      - "Admin processes: Non-admin SonicRoute cannot alter elevated app COM session. Fix: Run SonicRoute as Administrator."
      - "Anti-cheat (Valorant/Vanguard, EasyAntiCheat): Kernel drivers block runtime COM modification. Fix: Set route before launching game."
      - "Inactive audio session: Windows only registers WASAPI session when audio is actively outputting. Play sound first."

  TROUBLESHOOT_RUNTIMES_AND_CRASHES:
    intent: "App crash on launch, missing runtime, .NET 10 / .NET 8, Store MSIX vs Portable"
    keywords: ["crash", "runtime", "net10", "net8", "net48", "store", "portable", "msix", "闪退", "运行库", "打不开", "报错"]
    wiki: "08-版本相关.md"
    source: ["SonicRoute/AppRuntimeIdentity.cs", "SonicRoute.Core/Compat/CompatEnv.cs"]
    symbols: ["AppRuntimeIdentity.GetRuntimeFlavor", "CompatEnv.IsSupportedWindowsVersion"]
    facts:
      - "Lite editions require Windows Desktop Runtime (x64/ARM64), NOT standard console runtime."
      - "Legacy x64 requires .NET Framework 4.8 (preinstalled on Windows 10/11)."
      - "OS requirement: Windows 10 Build 19041+ (v2004) or Windows 11."

  RESOURCE_AND_PERFORMANCE:
    intent: "CPU usage, memory footprint, background timers, polling frequency"
    keywords: ["cpu", "memory", "resource", "poll", "leak", "lag", "占用", "性能", "卡顿", "内存", "后台"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.Core/AudioMeterService.cs", "SonicRoute/App.BackgroundMonitoring.cs"]
    symbols: ["AudioMeterService", "ProcessSnapshotService"]
    facts:
      - "Idle CPU is mathematically ~0%."
      - "Pure event-driven: WinEventHook for window switch, IMMNotificationClient for audio changes."
      - "Peak meters completely halt computation when UI panel is closed."
      - "Zero periodic polling timers active during idle."

  SOURCE_ARCHITECTURE_AND_BUILD:
    intent: "Codebase architecture, COM interop, multi-target compile"
    keywords: ["source", "build", "architecture", "compile", "csproj", "com interop", "架构", "编译", "源码", "工程"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.sln", "SonicRoute/SonicRoute.csproj", "SonicRoute.Core/SonicRoute.Core.csproj"]
    symbols: ["SonicRoute.Core.Interop.PolicyConfigClient", "SonicRoute.Core.AudioService"]
    facts:
      - "SonicRoute (WPF presentation & shell) -> SonicRoute.Core (netstandard2.0 / net8 / net10 multi-targeted domain)."
      - "SonicRoute.Legacy: .NET Framework 4.8 compatibility shell."

  CONFIG_AND_STORAGE:
    intent: "Configuration file locations, rules backup, custom icon cache, history"
    keywords: ["config", "path", "storage", "rules.json", "backup", "location", "配置", "路径", "备份", "目录"]
    wiki: "01-使用指南.md"
    source: ["SonicRoute.Core/AppDataRoot.cs", "SonicRoute.Core/ConfigService.cs", "SonicRoute.Core/AutoRuleStore.cs"]
    symbols: ["AppDataRoot.Path", "ConfigService", "AutoRuleStore"]
    facts:
      - "Root dir: %LocalAppData%\\SonicRoute\\"
      - "Config: %LocalAppData%\\SonicRoute\\config.json (atomic write via temp file)"
      - "Rules: %LocalAppData%\\SonicRoute\\rules.json"
      - "Icons: %LocalAppData%\\SonicRoute\\Icons\\"

  VERSION_ARCHIVE_AND_CHANGELOG:
    intent: "Release history, changelog, v1.25 upgrade notes"
    keywords: ["changelog", "history", "version", "update", "v1.25", "v1.24", "更新日志", "版本历史"]
    wiki: "09-版本历史.md"
    source: ["docs/release-v1.25.md"]
    symbols: ["CHANGELOG"]
    facts:
      - "v1.25: .NET 10 default, native updater, 37 actions, drag icon, 850px panel."
      - "v1.24: Card-style release notes, audio meter optimizations."

DOMAIN_ENUM_REGISTRY:
  AutoRuleTrigger:
    0: { name: Hotkey, scope: GLOBAL_INPUT, desc: "Key combination or mouse button" }
    1: { name: AppStart, scope: PROCESS_EVENT, desc: "Process creation detected via Trace/Snapshot" }
    2: { name: AppSwitch, scope: FOREGROUND_EVENT, desc: "Active window audio session focus change" }
    3: { name: AppExit, scope: PROCESS_EVENT, desc: "Process termination detected" }
    4: { name: Schedule, scope: TIME_EVENT, desc: "Single/Daily/Weekly schedule" }
    5: { name: Startup, scope: LIFECYCLE_EVENT, desc: "SonicRoute app boot completion (once per run)" }

  AutoRuleAction:
    0: { name: SetSystemOutput, target: DEFAULT_RENDER_DEVICE, param: DeviceID }
    1: { name: SetSystemInput, target: DEFAULT_CAPTURE_DEVICE, param: DeviceID }
    2: { name: SetSystemVolume, target: SYSTEM_MASTER_VOLUME, param: 0..100 }
    3: { name: ToggleSystemMute, target: SYSTEM_MASTER_VOLUME, param: TOGGLE }
    4: { name: SetAppVolume, target: APP_PLAYBACK_SESSION, param: 0..100 }
    5: { name: ToggleAppMute, target: APP_PLAYBACK_SESSION, param: TOGGLE }
    6: { name: SetAppOutput, target: APP_PLAYBACK_ROUTING, param: DeviceID }
    7: { name: SetAppInput, target: APP_CAPTURE_ROUTING, param: DeviceID }
    8: { name: LaunchProgram, target: OS_EXEC, param: "FilePath, Args, WorkingDir" }
    9: { name: RunPowerShell, target: OS_EXEC, param: ScriptText }
    10: { name: ShowOsd, target: UI_HUD, param: "Title, Subtitle, Timeout" }
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
    24: { name: SetDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: 0..100 }
    25: { name: AdjustDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: DeltaPercent }
    26: { name: SetDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: BOOL_EXPLICIT }
    27: { name: ToggleDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: TOGGLE }
    28: { name: SetAppInputMute, target: APP_CAPTURE_SESSION, param: "ProcessName, BOOL_EXPLICIT" }
    29: { name: ToggleAppInputMute, target: APP_CAPTURE_SESSION, param: "ProcessName, TOGGLE" }
    30: { name: SetAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: 0..100 }
    31: { name: AdjustAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: DeltaPercent }
    32: { name: SetAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: BOOL_EXPLICIT }
    33: { name: ToggleAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: TOGGLE }
    34: { name: ResetAllAppRouting, target: ALL_PERSISTED_APP_ROUTES, param: RESET_TO_DEFAULT }
    35: { name: ShowQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_SHOW }
    36: { name: HideQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_HIDE }

DATA_PATHS_&_CONFIG_SCHEMA:
  root_dir: "%LocalAppData%\\SonicRoute\\"
  config_file: "%LocalAppData%\\SonicRoute\\config.json"
  rules_file: "%LocalAppData%\\SonicRoute\\rules.json"
  icons_dir: "%LocalAppData%\\SonicRoute\\Icons\\"
  history_dir: "%LocalAppData%\\SonicRoute\\History\\" # opt-in in experimental mode, max 1-64MB
  mutex_pattern: "Local\\SonicRoute_{Suffix}_9NQZGRTPM1NT"

QUERY_DECISION_MATRIX_FOR_AGENTS:
  WHEN user_says_routing_fails:
    CHECK_1: "Is target app elevated? (Admin process blocks non-admin COM manipulation -> run as Admin)"
    CHECK_2: "Is app an anti-cheat game (Valorant/Vanguard, EAC)? (Kernel hooks block policy config -> set routing BEFORE anti-cheat launches)"
    CHECK_3: "Is audio stream actively playing? (Windows creates audio session only on active sound stream)"
  WHEN user_asks_stream_deck_or_ahk:
    SYNTAX: "sr.exe \"Rule Name\" OR sr.exe \"<GUID>\""
    RETURN: "sr returns 0 on success. Never launches app if closed. Latency < 15ms via local named pipe."
  WHEN user_asks_runtime_error:
    ERROR_MISSING_NET_DESKTOP:
      CAUSE: "Installed base .NET Runtime instead of Windows Desktop Runtime."
      FIX: "Download .NET Desktop Runtime (x64/ARM64) from official Microsoft link."
    ALTERNATIVE: "Advise Legacy x64 build (no .NET 8/10 needed, uses system .NET Framework 4.8)."
  WHEN user_asks_cpu_consumption:
    RESPONSE_FACT: "Idle CPU is mathematically ~0% due to WinEventHook / native WMI trace event subscription. No periodic polling unless fallback compatibility mode is manually turned on."

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>
