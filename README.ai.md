<!-- Generated via docs/readme/generate.cjs. Metadata from docs/readme/shared.json; body template maintained in renderAI() of docs/readme/generate.cjs. Run node docs/readme/generate.cjs to update. -->
# 🎧 SonicRoute (AI / LLM Machine Specification)

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | **🤖 AI**

SCHEMA_SPEC:
  system: "SonicRoute"
  platform: "Windows 10 2004+ (Build 19041+) | Windows 11 (x64 / win-arm64)"
  version: "v1.27 (1.27.0.0)"
  repository: "https://github.com/kunkunkunQoQ/SonicRoute"
  wiki_root: "https://github.com/kunkunkunQoQ/SonicRoute/wiki"
  download_latest: "https://github.com/kunkunkunQoQ/SonicRoute/releases/latest"
  store_url: "https://apps.microsoft.com/detail/9NQZGRTPM1NT"

SYSTEM_ARCHITECTURE_&_FACTUAL_BOUNDS:
  driver_isolation: "NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_TARGET_PROCESS_DLL_INJECTION"
  routing_backend: "Undocumented COM Windows.Media.Internal.AudioPolicyConfig -> Windows PropertyStore persistence"
  persistence_scope: "OS-level native persistence (survives app exit & OS reboot without SonicRoute running)"
  mixer_isolation: "Per-app endpoint persistence; never corrupts Windows volume mixer"
  hooks_in_use: "User-mode only: SetWinEventHook (foreground focus tracking), SetWindowsHookEx WH_MOUSE_LL (tray wheel & mouse hotkey input)"
  execution_resource_model: "Event-driven first (WinEventHook + CoreAudio IMMNotificationClient). A necessary 1-second automation process snapshot fallback and conditional recovery checks are retained; visible active-session meters sample at 33ms. No relevant rules or closed meter UI retain their dedicated polling."
  cli_ipc_transport: "NamedPipe IPC (sr.cmd targets running instance; SonicRoute.exe --run-rule cold-boots temporary CLI host if instance absent)"
  cross_platform: "FALSE (strictly Win32/WPF/.NET CoreAudio; never macOS/Linux)"

CLI_AND_SHORT_COMMAND_SPEC:
  command_invocation: "sr <RuleName|RuleId> (invokes %LocalAppData%\\SonicRoute\\Command\\sr.cmd wrapping sr-client.ps1) OR SonicRoute.exe --run-rule <selector>"
  pipe_endpoint: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"
  stream_deck_ahk: "Call sr <RuleName|RuleId> or sr.cmd; waits for completion and returns rule execution result and corresponding exit code"
  exit_codes:
    0: { code: ok, desc: "Execution succeeded without step failures" }
    1: { code: step_failed, desc: "One or more rule steps failed" }
    2: { code: invalid_arguments, desc: "Empty selector or invalid command arguments" }
    3: { code: rule_not_found, desc: "Rule ID or name not found in rules store" }
    4: { code: ambiguous_rule, desc: "Multiple rules share the same name" }
    5: { code: rule_busy, desc: "Target rule is currently executing" }
    6: { code: server_unavailable, desc: "Named pipe connection failed (SonicRoute not running)" }
    7: { code: canceled, desc: "Execution canceled or app shutdown in progress" }
    8: { code: internal_error, desc: "Unhandled exception during execution" }

KNOWLEDGE_RETRIEVAL_ROUTER:
  ROUTE_CORE_AUDIO:
    intent: "Per-app audio routing, default audio devices, sound splitting"
    keywords: ["route", "split", "headset", "speaker", "device", "endpoint", "render", "capture", "分流", "设备", "耳机", "音箱"]
    wiki: "02-功能特性.md"
    source: ["SonicRoute.Core/AudioService.cs", "SonicRoute.Core/Interop/AudioPolicyConfig.cs", "SonicRoute.Core/Interop/PolicyConfigClient.cs"]
    symbols: ["AudioService.ApplyEndpoint", "AudioPolicyConfig.SetDefaultEndPoint", "SystemDefaultDeviceService.SetDefault"]
    facts:
      - "Routing is driverless via undocumented Windows COM Windows.Media.Internal.AudioPolicyConfig."
      - "Writes per-app device routing to Windows PropertyStore; persists across reboots without SonicRoute running."
      - "Resolution order: Default Device -> App PropertyStore -> Active Audio Session."

  AUTOMATION_ENGINE:
    intent: "Automation triggers, action execution, recipe creation, rule chaining"
    keywords: ["automation", "rule", "trigger", "action", "chain", "recipe", "schedule", "自动化", "规则", "触发器", "动作", "配方"]
    wiki: "04-自动化规则.md"
    source: ["SonicRoute/AutoRuleService.cs", "SonicRoute/AutoRuleScheduler.cs", "SonicRoute.Core/Models/AutoRule.cs", "SonicRoute.Core/AutoRuleStore.cs"]
    symbols: ["AutoRuleService.ExecuteAsync", "AutoRuleTrigger", "AutoRuleAction", "AutoRuleStore.LoadAll"]
    facts:
      - "6 Triggers (0..5) × 37 Actions (0..36)."
      - "Action 17 ExecuteRule enables nested rule chaining."
      - "Supports StopOnFailure flag and millisecond StepDelay."
      - "Rules stored as separate JSON files in %LocalAppData%\\SonicRoute\\Automation\\."

  CLI_AND_IPC:
    intent: "Command-line execution, Stream Deck, AHK, named pipe IPC"
    keywords: ["cli", "sr", "command", "stream deck", "ahk", "pipe", "terminal", "命令行", "终端", "管道"]
    wiki: "05-命令行执行规则.md"
    source: ["SonicRoute/RuleCommandLine.cs", "SonicRoute/Program.cs"]
    symbols: ["RuleCommandLine.PipeName", "RuleCommandLine.SendAsync", "RuleCommandLine.ExecuteAsync", "RuleCommandServer"]
    facts:
      - "Short command sr.cmd invokes sr-client.ps1 (requires running instance in same session/user; exits 6 if unstarted)."
      - "Direct CLI SonicRoute.exe --run-rule cold-boots a headless temporary execution host if no instance is running."
      - "IPC via Named Pipe SonicRoute.RuleCommand.{Edition}.{SessionId}.{UserSid}."
      - "Exit codes 0..8 (0=ok, 1=step_failed, 2=invalid_arguments, 3=rule_not_found, etc.)."
      - "Waits for rule completion; no fixed execution timeout (10s write timeout)."

  PANEL_AND_TRAY:
    intent: "QuickPanel UI, tray volume scroll wheel, custom icons, blind control"
    keywords: ["panel", "tray", "wheel", "icon", "quickpanel", "height", "blind", "面板", "托盘", "滚轮", "盲操", "图标"]
    wiki: "01-使用指南.md"
    source: ["SonicRoute/TrayWheelService.cs", "SonicRoute/QuickPanelModernWindow.xaml.cs", "SonicRoute/CustomAppIconStore.cs"]
    symbols: ["TrayWheelService.Start", "QuickPanelModernWindow", "CustomAppIconStore.Prepare", "CustomAppIconStore.Load"]
    facts:
      - "Tray mouse wheel blinds-scrolls active app, foreground app, or master volume via WH_MOUSE_LL hook."
      - "QuickPanel heights: 350px to 850px adjustable (v1.25+)."
      - "Drag-and-drop PNG/JPEG/ICO onto app button to set custom icon in %LocalAppData%\\SonicRoute\\Icons\\."

  GLOBAL_HOTKEYS:
    intent: "Global keyboard/mouse shortcuts, mic mute toggle, volume hotkeys"
    keywords: ["hotkey", "shortcut", "bind", "key", "mouse hook", "esc", "快捷键", "热键", "按键", "静音快捷键"]
    wiki: "03-快捷键.md"
    source: ["SonicRoute/HotkeyService.cs", "SonicRoute/MouseHotkeyHook.cs", "SonicRoute/HotkeyActions.cs"]
    symbols: ["HotkeyService.Reload", "MouseHotkeyHook", "HotkeyActions.All", "HotkeyActions.DisplayName"]
    facts:
      - "Registered via Win32 RegisterHotKey API with WH_MOUSE_LL hook for mouse button/wheel binds."
      - "Scopes: Global, Foreground App, Last Active App."
      - "Esc cancels recording; conflict detector prevents duplicate binding."

  TROUBLESHOOT_ROUTING_FAILURES:
    intent: "App route not working, sound still in default device, admin process, EAC/Vanguard"
    keywords: ["fail", "not working", "no sound", "admin", "vanguard", "eac", "anticheat", "失效", "不生效", "没声音", "管理员", "反作弊"]
    wiki: "06-常见问题.md"
    source: ["SonicRoute.Core/AudioService.cs", "SonicRoute.Core/ProcessSnapshotService.cs"]
    symbols: ["AudioService.GetApps", "AudioService.InvalidateAppsCache", "ProcessSnapshotService.Capture"]
    facts:
      - "Admin processes: Non-admin SonicRoute cannot alter elevated app COM session. Fix: Run SonicRoute as Administrator."
      - "Anti-cheat (Valorant/Vanguard, EasyAntiCheat): Kernel drivers block runtime COM modification. Fix: Set route before launching game."
      - "Inactive audio session: Windows only registers WASAPI session when audio is actively outputting. Play sound first."

  TROUBLESHOOT_RUNTIMES_AND_CRASHES:
    intent: "App crash on launch, missing runtime, .NET 10 / .NET 8, Store MSIX vs Portable"
    keywords: ["crash", "runtime", "net10", "net8", "net48", "store", "portable", "msix", "闪退", "运行库", "打不开", "报错"]
    wiki: "08-版本相关.md"
    source: ["SonicRoute/AppRuntimeIdentity.cs", "SonicRoute.Core/Compat/WindowsVersion.cs"]
    symbols: ["AppRuntimeIdentity.Current", "AppRuntimeIdentity.CommandEdition", "WindowsVersion.Build"]
    facts:
      - "Lite editions require Windows Desktop Runtime (x64/ARM64), NOT standard console runtime."
      - "Legacy x64 requires .NET Framework 4.8 (preinstalled on Windows 10/11)."
      - "OS requirement: Windows 10 Build 19041+ (v2004) or Windows 11."

  RESOURCE_AND_PERFORMANCE:
    intent: "CPU usage, memory footprint, background timers, polling frequency"
    keywords: ["cpu", "memory", "resource", "poll", "leak", "lag", "占用", "性能", "卡顿", "内存", "后台"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.Core/AudioMeterService.cs", "SonicRoute/ForegroundChangeSource.cs", "SonicRoute/App.BackgroundMonitoring.cs"]
    symbols: ["AudioMeterService.Start", "AudioMeterService.Stop", "ForegroundChangeSource"]
    facts:
      - "Event-driven first architecture using WinEventHook and CoreAudio events."
      - "Automation process fallback remains 1 second when required. WMI subscription recovery backs off from 2 to 30 seconds without slowing fallback detection; no process rules retain no dedicated listener or timer."
      - "AudioMeterService peak monitoring runs exclusively while UI panels are open; stops when closed."

  SOURCE_ARCHITECTURE_AND_BUILD:
    intent: "Codebase architecture, COM interop, multi-target compile"
    keywords: ["source", "build", "architecture", "compile", "csproj", "com interop", "架构", "编译", "源码", "工程"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.sln", "SonicRoute/SonicRoute.csproj", "SonicRoute.Core/SonicRoute.Core.csproj"]
    symbols: ["SonicRoute", "SonicRoute.Core"]
    facts:
      - "SonicRoute (WPF presentation & shell) -> SonicRoute.Core (netstandard2.0 / net8 / net10 multi-targeted domain)."
      - "SonicRoute.Legacy: .NET Framework 4.8 compatibility shell."

  CONFIG_AND_STORAGE:
    intent: "Configuration file locations, rules backup, custom icon cache, history"
    keywords: ["config", "path", "storage", "rules.json", "backup", "location", "配置", "路径", "备份", "目录"]
    wiki: "01-使用指南.md"
    source: ["SonicRoute.Core/AppDataRoot.cs", "SonicRoute.Core/ConfigService.cs", "SonicRoute.Core/AutoRuleStore.cs"]
    symbols: ["AppDataRoot.DirectoryPath", "ConfigService.ConfigPath", "AutoRuleStore.RulesDir"]
    facts:
      - "Root dir: %LocalAppData%\\SonicRoute\\"
      - "Config: %LocalAppData%\\SonicRoute\\config.json (atomic write via temp file)"
      - "Rules dir: %LocalAppData%\\SonicRoute\\Automation\\*.json"
      - "Command dir: %LocalAppData%\\SonicRoute\\Command\\ (sr.cmd, sr-client.ps1)"
      - "Icons dir: %LocalAppData%\\SonicRoute\\Icons\\"

  VERSION_ARCHIVE_AND_CHANGELOG:
    intent: "Release history, changelog, v1.27 upgrade notes"
    keywords: ["changelog", "history", "version", "update", "v1.27", "v1.25", "v1.24", "更新日志", "版本历史"]
    wiki: "09-版本历史.md"
    source: ["docs/release-v1.27.md"]
    symbols: ["v1.27"]
    facts:
      - "v1.27: One quick panel, session notification lifecycle, reliable mute hotkey targets, custom device titles, shared scrollbars and reusable settings rows. v1.26: Protected-app icon fallback and runtime-specific persistence."
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
  rules_dir: "%LocalAppData%\\SonicRoute\\Automation\\*.json"
  command_dir: "%LocalAppData%\\SonicRoute\\Command\\ (sr.cmd, sr-client.ps1)"
  icons_dir: "%LocalAppData%\\SonicRoute\\Icons\\"
  history_dir: "%LocalAppData%\\SonicRoute\\History\\"
  pipe_pattern: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"

QUERY_DECISION_MATRIX_FOR_AGENTS:
  WHEN user_says_routing_fails:
    CHECK_1: "Is target app elevated? (Admin process blocks non-admin COM manipulation -> run SonicRoute as Admin)"
    CHECK_2: "Is app an anti-cheat game (Valorant/Vanguard, EAC)? (Kernel hooks block policy config -> set routing BEFORE anti-cheat launches)"
    CHECK_3: "Is audio stream actively playing? (Windows creates WASAPI session only on active sound stream; start sound first)"
  WHEN user_asks_cli_or_scripting:
    SYNTAX: "sr <RuleName|RuleID> OR sr.cmd <RuleName|RuleID>"
    IPC_PROTOCOL: "Named pipe SonicRoute.RuleCommand.{Edition}.{SessionId}.{UserSid} (same session/user). Connect timeout 250ms (sr) / 5000ms (--run-rule); write timeout 10s; no fixed execution timeout."
    EXIT_CODE_MAP: "0=ok, 1=step_failed, 2=invalid_arguments, 3=rule_not_found, 4=ambiguous_rule, 5=rule_busy, 6=server_unavailable, 7=canceled, 8=internal_error."
  WHEN user_asks_runtime_error:
    ERROR_MISSING_NET_DESKTOP:
      CAUSE: "Installed base .NET Runtime instead of Windows Desktop Runtime."
      FIX: "Download .NET Desktop Runtime (x64/ARM64) from official Microsoft link."
    ALTERNATIVE: "Advise Legacy x64 build (uses system .NET Framework 4.8; no separate runtime required)."
  WHEN user_asks_cpu_consumption:
    RESPONSE_FACT: "System events drive detection, with necessary 1-second process fallback and conditional failure recovery. Active visible meters still sample at 33ms; inactive sessions wait for notifications. No whole-app CPU or memory improvement is claimed from local call-count checks."


<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>
