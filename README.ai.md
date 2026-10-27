<!-- Generated via docs/readme/generate.cjs. Release metadata: docs/readme/shared.json. Source version: build/Version.props. Body: renderAI(). Run node docs/readme/generate.cjs to update. -->
# 🎧 SonicRoute (AI / LLM Machine Specification)

[简体中文](README.md) | [English](README.en.md) | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [Deutsch](README.de.md) | [Français](README.fr.md) | [Español](README.es.md) | [Русский](README.ru.md) | **🤖 AI**

```yaml
SCHEMA_SPEC:
  system: "SonicRoute"
  platform: "Windows 10 2004+ (Build 19041+) | Windows 11 (x64 / win-arm64)"
  version: "v1.29 (1.29.0.0)"
  source_version: "v1.29"
  source_status: "Same version number as the published release; source may contain later commits."
  version_scope: "version identifies the published release from shared.json; source_version comes from Version.props. Implementation facts below describe repository source and do not establish that a development feature is in the stable download."
  repository: "https://github.com/kunkunkunQoQ/SonicRoute"
  wiki_root: "https://github.com/kunkunkunQoQ/SonicRoute/wiki"
  download_latest: "https://github.com/kunkunkunQoQ/SonicRoute/releases/latest"
  store_url: "https://apps.microsoft.com/detail/9NQZGRTPM1NT"

SYSTEM_ARCHITECTURE_&_FACTUAL_BOUNDS:
  driver_isolation: "NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_TARGET_PROCESS_DLL_INJECTION"
  routing_backend: "Undocumented COM Windows.Media.Internal.AudioPolicyConfig -> Windows PropertyStore persistence"
  persistence_scope: "OS-level native persistence (survives app exit & OS reboot without SonicRoute running)"
  routing_scope: "Per-app endpoint preferences. ResetAllAppRouting clears routes, preserving volume/mute preferences and system default devices; it is not the Windows volume mixer full reset."
  hooks_in_use: "User-mode only: SetWinEventHook (foreground focus tracking), SetWindowsHookEx WH_MOUSE_LL (tray wheel & mouse hotkey input)"
  execution_resource_model: "Event-driven first (WinEventHook, CoreAudio device/session notifications, WMI process events). Process rules retain a 1-second snapshot fallback when enabled or event monitoring is unavailable. Visible enabled meters sample active/fading sessions at 33ms; idle, closed and failed states have different wait/recovery paths. This is not zero polling or a zero-CPU guarantee."
  cli_ipc_transport: "NamedPipe IPC (sr.cmd targets running instance; SonicRoute.exe --run-rule cold-boots temporary CLI host if instance absent)"
  cross_platform: "FALSE (strictly Win32/WPF/.NET CoreAudio; never macOS/Linux)"

RELEASE_EDITIONS:
  - { edition: Lite, runtime: net10, architecture: x64, asset: "SonicRoute-v1.29-Lite-x64.exe", deployment: FRAMEWORK_DEPENDENT_SINGLE_EXE }
  - { edition: Lite, runtime: net10, architecture: arm64, asset: "SonicRoute-v1.29-Lite-arm64.exe", deployment: FRAMEWORK_DEPENDENT_SINGLE_EXE, status: EXPERIMENTAL }
  - { edition: Lite, runtime: net8, architecture: x64, asset: "SonicRoute-v1.29-Lite-NET8-x64.exe", deployment: FRAMEWORK_DEPENDENT_SINGLE_EXE }
  - { edition: Lite, runtime: net8, architecture: arm64, asset: "SonicRoute-v1.29-Lite-NET8-arm64.exe", deployment: FRAMEWORK_DEPENDENT_SINGLE_EXE, status: EXPERIMENTAL }
  - { edition: Legacy, runtime: net48, architecture: x64, asset: "SonicRoute-v1.29-Legacy-x64.zip", deployment: MULTIFILE_ZIP }
  - { edition: Store, runtime: net10, architecture: x64, deployment: MSIX_WITH_FRAMEWORK_DEPENDENT_LITE_EXE, updates: MICROSOFT_STORE }

CLI_AND_SHORT_COMMAND_SPEC:
  command_invocation: "sr <RuleName|RuleId> (invokes %LocalAppData%\\SonicRoute\\Command\\sr.cmd wrapping sr-client.ps1) OR SonicRoute.exe --run-rule <selector>"
  pipe_endpoint: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"
  stream_deck_ahk: "Call sr <RuleName|RuleId> or sr.cmd; waits for completion and returns rule execution result and corresponding exit code"
  exit_codes:
    0: { code: ok, desc: "Execution succeeded without step failures" }
    1: { code: step_failed, desc: "One or more rule steps failed" }
    2: { code: invalid_arguments, desc: "Empty selector or invalid command arguments" }
    3: { code: rule_not_found, desc: "Rule ID or name not found in rules store" }
    4: { code: ambiguous_rule, desc: "Selector matches multiple rules (duplicate name or ID)" }
    5: { code: rule_busy, desc: "Target rule is currently executing" }
    6: { code: server_unavailable, desc: "Named pipe server unavailable, including an absent running instance" }
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
      - "GetApps enumerates non-expired WASAPI sessions. GetPersistedEndpoint separately reads per-app routing; a missing preference follows the system default. There is no universal default/property-store/session resolution chain."
      - "ResetAllAppRouting first uses the native global route-clear call, with a per-running-process/session compatibility fallback. That fallback does not prove that exited apps were reset."

  AUTOMATION_ENGINE:
    intent: "Automation triggers, action execution, recipe creation, rule chaining"
    keywords: ["automation", "rule", "trigger", "action", "chain", "recipe", "schedule", "自动化", "规则", "触发器", "动作", "配方"]
    wiki: "04-自动化规则.md"
    source: ["SonicRoute/AutoRuleService.cs", "SonicRoute/AutoRuleScheduler.cs", "SonicRoute.Core/Models/AutoRule.cs", "SonicRoute.Core/AutoRuleStore.cs"]
    symbols: ["AutoRuleService.ExecuteAsync", "AutoRuleTrigger", "AutoRuleAction", "AutoRuleStore.LoadAll"]
    facts:
      - "6 Triggers (0..5) × 37 Actions (0..36)."
      - "Action 17 ExecuteRule enables nested rule chaining."
      - "Steps execute in order with DelayMs before each step and optional StopOnFailure. Detection is independent of the serialized process/foreground execution queue; single-rule reentry is rejected and obsolete queued requests are revalidated."
      - "Schedules use one-shot timers and validate current plan identity; missed times are not replayed."
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
      - "sr --legacy selects the Legacy instance. There is no separate sr.exe; use the registered sr.cmd or the application executable."

  PANEL_AND_TRAY:
    intent: "QuickPanel UI, tray volume scroll wheel, custom icons, blind control"
    keywords: ["panel", "tray", "wheel", "icon", "quickpanel", "height", "blind", "面板", "托盘", "滚轮", "盲操", "图标"]
    wiki: "01-使用指南.md"
    source: ["SonicRoute/TrayWheelService.cs", "SonicRoute/QuickPanelModernWindow.xaml.cs", "SonicRoute/QuickPanelModernWindow.OutputVolumes.cs", "SonicRoute.Core/OutputVolumeMonitor.cs", "SonicRoute/MainWindow.AppIcons.cs", "SonicRoute/CustomAppIconStore.cs"]
    symbols: ["TrayWheelService.Start", "QuickPanelModernWindow", "CustomAppIconStore.Prepare", "CustomAppIconStore.Load"]
    facts:
      - "One quick panel (QuickPanelModernWindow); the old application/classic panel is removed. Quick panel styles do not imply a second legacy panel implementation."
      - "QuickPanel height setting: 350 to 850 WPF device-independent units, additionally constrained by available work area."
      - "v1.28 defaults: TrayWheelEverywhere=false (own tray icon only); PanelChangeSystemDefault=true. Saved explicit preferences are retained. Normal tray wheel adjusts current-app volume."
      - "v1.28 adds Alt+wheel for current-app output and Alt+Shift+wheel for system default playback; up selects previous, down selects next. Existing registered wheel shortcuts take priority."
      - "v1.29 adds Shift+wheel to adjust system default playback volume in the same configured tray area. It shares the system-volume hotkey handler, configured step, endpoint readback and OSD; no new polling or setting."
      - "v1.29 adds a deferred output-volume popup: expand beside/above the unchanged owner to control real output endpoint master volumes and mute. Height follows displayed rows, capped by the owner and work area. QuickPanelShowAllOutputVolumes defaults false and remembers explicit clicks."
      - "PanelOutputVolumesRetainedOnly defaults false and independently filters the extra popup using HiddenOutputDevices. The popup loads only while expanded; collapse cancels its own reads/rendering and releases controls/subscriptions. Healthy notifications read only changed endpoints; visible notification/read failures retain bounded recovery."
      - "Quick panel device volume/mute changes do not show redundant OSD; hotkey and tray feedback remain."
      - "Drop one PNG/JPEG/ICO onto the Import icon button for the selected app in the full interface; it reuses the file-picker import path and stores managed PNGs in the Icons directory."

  GLOBAL_HOTKEYS:
    intent: "Global keyboard/mouse shortcuts, mic mute toggle, volume hotkeys"
    keywords: ["hotkey", "shortcut", "bind", "key", "mouse hook", "esc", "快捷键", "热键", "按键", "静音快捷键"]
    wiki: "03-快捷键.md"
    source: ["SonicRoute/HotkeyService.cs", "SonicRoute/MouseHotkeyHook.cs", "SonicRoute/HotkeyActions.cs"]
    symbols: ["HotkeyService.Reload", "MouseHotkeyHook", "HotkeyActions.All", "HotkeyActions.DisplayName"]
    facts:
      - "Registered via Win32 RegisterHotKey API with WH_MOUSE_LL hook for mouse button/wheel binds."
      - "Global registration invokes action-specific targets. Current-app actions use the configured shared current-app selection; system-default and all-app actions have separate targets. These are not three generic per-binding scope modes."
      - "Device hotkeys retain the original single-direction actions and stored IDs; the experimental previous/next hotkey split was reverted. Tray modifier-wheel directions are separate."
      - "Esc cancels recording; conflict detector prevents duplicate binding."

  TROUBLESHOOT_ROUTING_FAILURES:
    intent: "App route not working, sound still in default device, admin process, EAC/Vanguard"
    keywords: ["fail", "not working", "no sound", "admin", "vanguard", "eac", "anticheat", "失效", "不生效", "没声音", "管理员", "反作弊"]
    wiki: "06-常见问题.md"
    source: ["SonicRoute.Core/AudioService.cs", "SonicRoute.Core/ProcessSnapshotService.cs"]
    symbols: ["AudioService.GetApps", "AudioService.InvalidateAppsCache", "ProcessSnapshotService.Capture"]
    facts:
      - "Record the affected application, privilege levels, audio mode and returned HRESULT. Permission restrictions can affect process metadata or audio operations, but an icon fallback does not prove routing is blocked or that elevation will fix it."
      - "Protected games require per-application evidence. Trying a route before launch is a possible workaround, not a guaranteed fix; do not assert that all anti-cheat drivers block this COM API."
      - "Start the target and play audio to establish a session. Inactive WASAPI sessions can still exist; GetApps excludes expired sessions and is not a list of every installed or running application."

  TROUBLESHOOT_RUNTIMES_AND_CRASHES:
    intent: "App crash on launch, missing runtime, .NET 10 / .NET 8, Store MSIX vs Portable"
    keywords: ["crash", "runtime", "net10", "net8", "net48", "store", "portable", "msix", "闪退", "运行库", "打不开", "报错"]
    wiki: "08-版本相关.md"
    source: ["SonicRoute/AppRuntimeIdentity.cs", "SonicRoute.Core/Compat/WindowsVersion.cs"]
    symbols: ["AppRuntimeIdentity.Current", "AppRuntimeIdentity.CommandEdition", "WindowsVersion.Build"]
    facts:
      - "Lite editions require Windows Desktop Runtime (x64/ARM64), NOT standard console runtime."
      - "Match the runtime major version and architecture: default Lite uses .NET 10; NET8-suffixed Lite uses .NET 8. Single EXE does not mean self-contained. Legacy x64 uses .NET Framework 4.8."
      - "Store packaging reuses the NET10 Lite x64 framework-dependent EXE; it does not bundle .NET 10 Desktop Runtime. Do not assume Store installs that runtime automatically or infer the live Store version from the repository version."
      - "OS requirement: Windows 10 Build 19041+ (v2004) or Windows 11."

  RESOURCE_AND_PERFORMANCE:
    intent: "CPU usage, memory footprint, background timers, polling frequency"
    keywords: ["cpu", "memory", "resource", "poll", "leak", "lag", "占用", "性能", "卡顿", "内存", "后台"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.Core/AudioMeterService.cs", "SonicRoute/ForegroundChangeSource.cs", "SonicRoute/App.BackgroundMonitoring.cs"]
    symbols: ["AudioMeterService.Start", "AudioMeterService.Stop", "ForegroundChangeSource"]
    facts:
      - "Event-driven first architecture using WinEventHook and CoreAudio events."
      - "Enabled process-start/exit rules use WMI events, with a 1-second snapshot fallback when enabled or events are unavailable. WMI recovery backs off from 2 to 30 seconds; no process rules means no dedicated process listener/snapshot timer."
      - "Enabled visible quick-panel meters sample active sessions and fade-out at 33ms. With no active session after fade-out, they wait for notifications; an active silent stream still samples. Conditional failure recovery is retained; close stops and disposes the meter."
      - "Closing windows releases UI work. Existing deferred idle reclaim can trim the process working set after 1.5 seconds; a smaller working set is not proof of a smaller managed heap or lower CPU."

  SOURCE_ARCHITECTURE_AND_BUILD:
    intent: "Codebase architecture, COM interop, multi-target compile"
    keywords: ["source", "build", "architecture", "compile", "csproj", "com interop", "架构", "编译", "源码", "工程"]
    wiki: "07-技术实现.md"
    source: ["SonicRoute.sln", "SonicRoute/SonicRoute.csproj", "SonicRoute.Core/SonicRoute.Core.csproj", "build/Version.props", "global.json"]
    symbols: ["SonicRoute", "SonicRoute.Core"]
    facts:
      - "SonicRoute WPF targets net10.0-windows and net8.0-windows. SonicRoute.Core targets net10.0-windows, net8.0-windows and net48; it does not target netstandard2.0."
      - "SonicRoute.Legacy uses the shared application source with a net48/x64 shell. SDK selection is maintained in global.json; development versions come only from build/Version.props."

  UPDATE_AND_RELEASE:
    intent: "Update checks, downloads, installation/rollback, runtime isolation and release workflow"
    keywords: ["update", "download", "rollback", "manifest", "protocol", "sha256", "更新", "回滚", "校验"]
    source: ["SonicRoute/Updates/UpdateService.cs", "SonicRoute/Updates/UpdateTransport.cs", "SonicRoute/Updates/UpdateProtocol.cs", "SonicRoute/Updates/UpdateInstaller.cs", "SonicRoute/Updates/UpdateBootstrap.cs", "packaging/Updates/README.md", "packaging/Store/README.md"]
    symbols: ["UpdateService.StartAsync", "UpdateService.CheckAsync", "UpdateProtocol.ValidatePackage"]
    facts:
      - "Portable auto-check is off by default. If enabled and due (at least 24h since the last attempt), startup warmup and one 15s delay precede a single async check; there is no recurring update poll. Manual checks are in Settings."
      - "Only the fixed official GitHub stable latest release is accepted; draft/prerelease and nonnumeric tags are rejected. Download and installation each require user action; there is no forced unattended update."
      - "Protocol 2 explicitly matches edition, runtime and architecture. NET10, NET8, Legacy and ARM64/x64 never silently cross channels. Store identity disables the portable updater, including worker entry points."
      - "NET8 maintenance ends after November 2026. Migration is manual through the official download page: x64 may choose NET10 or Legacy; ARM64 is directed to NET10 ARM64. Missing assets do not authorize another runtime or architecture."
      - "The original protocol-1 Lite contract means NET8 only; ambiguous dual-runtime protocol-1 metadata is refused. Old protocol-1 clients reject protocol 2. Version 1.24 has no native updater and first upgrades manually or through Store."
      - "GitHub asset digests, SHA256SUMS, manifest hashes/sizes, PE architecture, version and edition/runtime must agree. SHA-256 is integrity checking, not independent publisher authentication; portable packages/manifests are unsigned and trust official GitHub over system-validated HTTPS."
      - "The local trusted executable runs worker mode to wait, back up, replace, restart and roll back failures; downloaded scripts are never executed. Protected paths fail before exit, without silent elevation. Existing user config, rules, icons and other data are not installation targets."
      - "Interrupted or hung-process recovery may retain a journal and backups requiring a later retry/manual recovery. Startup acknowledgement is not a guarantee that every feature works; consult the updater README for recovery instructions."
      - "Release-note text belongs to the open settings view and is released through existing window cleanup, not a persistent notes cache. Verified package/installation metadata can be reused."

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
      - "Optional execution history: ExecutionHistory/{CommandEdition}, only when experimental mode and execution history are enabled. SONICROUTE_DATA_DIRECTORY can override the root with an absolute path for isolated tooling."

  VERSION_ARCHIVE_AND_CHANGELOG:
    intent: "Release history, changelog, v1.29 upgrade notes"
    keywords: ["changelog", "history", "version", "update", "v1.29", "v1.25", "v1.24", "更新日志", "版本历史"]
    wiki: "09-版本历史.md"
    source: ["docs/release-v1.29.md"]
    symbols: ["v1.29"]
    facts:
      - "v1.29: Expandable output endpoint volume panel, independent retained-device filter, deferred loading and event-driven incremental refresh; Shift wheel controls system volume. JSON/language and audio-meter work was reduced without a whole-app CPU claim."
      - "v1.28: Inline application renaming, shared routing card, independent mute/activity and hidden-panel markers; Alt wheel changes app output, Alt+Shift wheel changes system output. Existing device hotkeys remain."
      - "v1.27: One quick panel, session notification lifecycle, reliable mute hotkey targets, custom device titles, shared scrollbars and reusable settings rows. v1.26: Protected-app icon fallback and runtime-specific persistence."
      - "v1.25: .NET 10 default, native updater, 37 actions, drag icon, 850px panel."
      - "v1.24: Automation starter templates, optional recent execution details and per-app custom icons; later protected-app icon fixes are documented in v1.26."

DOMAIN_ENUM_REGISTRY:
  AutoRuleTrigger:
    0: { name: Hotkey, scope: GLOBAL_INPUT, desc: "Key combination or mouse button" }
    1: { name: AppStart, scope: PROCESS_EVENT, desc: "Process creation detected via Trace/Snapshot" }
    2: { name: AppSwitch, scope: FOREGROUND_EVENT, desc: "Foreground process changes and matches the configured application" }
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
  history_dir: "%LocalAppData%\\SonicRoute\\ExecutionHistory\\{CommandEdition}\\ (optional)"
  updates_dir: "%LocalAppData%\\SonicRoute\\Updates\\"
  root_override: "SONICROUTE_DATA_DIRECTORY (absolute path; otherwise the default root above)"
  pipe_pattern: "\\\\.\\pipe\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}"

QUERY_DECISION_MATRIX_FOR_AGENTS:
  WHEN user_says_routing_fails:
    CHECK_1: "Identify target, selected device, app privilege level and HRESULT; distinguish missing icon/process metadata from an actual route-write failure."
    CHECK_2: "For protected games record the title/audio mode and test independently; pre-launch routing is only a possible workaround, not a confirmed universal fix."
    CHECK_3: "Establish a WASAPI session by starting the app and playing audio; inactive, expired and absent sessions are different states."
  WHEN user_asks_cli_or_scripting:
    SYNTAX: "sr <RuleName|RuleID> OR sr.cmd <RuleName|RuleID>"
    IPC_PROTOCOL: "Named pipe SonicRoute.RuleCommand.{Edition}.{SessionId}.{UserSid} (same session/user). Connect timeout 250ms (sr) / 5000ms (--run-rule); write timeout 10s; no fixed execution timeout."
    EXIT_CODE_MAP: "0=ok, 1=step_failed, 2=invalid_arguments, 3=rule_not_found, 4=ambiguous_rule, 5=rule_busy, 6=server_unavailable, 7=canceled, 8=internal_error."
  WHEN user_asks_runtime_error:
    ERROR_MISSING_NET_DESKTOP:
      CAUSE: "Installed base .NET Runtime instead of Windows Desktop Runtime."
      FIX: "Download .NET Desktop Runtime (x64/ARM64) from official Microsoft link."
    ALTERNATIVE: "On x64, Legacy uses .NET Framework 4.8 instead of .NET Desktop Runtime. ARM64 users should use matching ARM64 Lite/runtime; do not recommend a nonexistent Legacy ARM64 build."
  WHEN user_asks_cpu_consumption:
    RESPONSE_FACT: "System events drive detection, with necessary 1-second process fallback and conditional failure recovery. Active visible meters still sample at 33ms; inactive sessions wait for notifications. No whole-app CPU or memory improvement is claimed from local call-count checks."
```

<div align="right"><sub><a href="https://github.com/kunkunkunQoQ/SonicRoute/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>
