#!/usr/bin/env node
// Shared layout and links; translations contain only reader-facing text.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../..');
const shared = JSON.parse(fs.readFileSync(path.join(__dirname, 'shared.json'), 'utf8'));
const reference = JSON.parse(fs.readFileSync(path.join(__dirname, 'locales/en-US.json'), 'utf8'));
const args = process.argv.slice(2);
if (args.length > 1 || (args.length && args[0] !== '--check')) throw new Error('Usage: node docs/readme/generate.cjs [--check]');
const check = args[0] === '--check';
const keys = Object.keys(reference).sort();
const placeholders = text => [...text.matchAll(/\{([a-zA-Z][a-zA-Z0-9]*)\}/g)].map(match => match[1]).sort();
const wiki = name => shared.repository + '/wiki/' + encodeURIComponent(name);
const pages = shared.wikiPages;
const link = (label, url) => `[${label}](${url})`;
const attr = text => text.replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
const tableCell = text => text.replaceAll('|', '\\|');
if (!/^v\d+\.\d+(?:\.\d+)?$/.test(shared.release) || !/^\d{4}-\d{2}-\d{2}$/.test(shared.releaseDate)) throw new Error('Invalid shared release metadata');
if (!/^v\d+\.\d+(?:\.\d+)?$/.test(shared.nextRelease) || !Number.isInteger(shared.triggerCount) || !Number.isInteger(shared.actionCount) || shared.triggerCount < 1 || shared.actionCount < 1) throw new Error('Invalid shared roadmap or feature counts');
const interfaceLocales = fs.readdirSync(path.join(root, 'SonicRoute/Resources/Lang')).filter(name => name.endsWith('.json')).map(name => name.slice(0, -5)).sort();
if (JSON.stringify(interfaceLocales) !== JSON.stringify(shared.languages.map(language => language.locale).sort())) throw new Error('README languages differ from built-in interface languages');
if (new Set(shared.languages.map(language => language.file)).size !== shared.languages.length) throw new Error('Duplicate README output');
function validate(locale, text) {
  if (JSON.stringify(Object.keys(text).sort()) !== JSON.stringify(keys)) throw new Error(locale + ': translation keys differ');
  for (const key of keys) {
    const values = Array.isArray(reference[key]) ? text[key] : [text[key]];
    const expected = Array.isArray(reference[key]) ? reference[key] : [reference[key]];
    if (!Array.isArray(values) || values.length !== expected.length) throw new Error(locale + ': invalid item count for ' + key);
    values.forEach((value, index) => {
      if (typeof value !== 'string' || (!value.trim() && !(locale === 'zh-CN' && key === 'wikiNote')) || /[\r\n]/.test(value) || /https?:\/\/|<\/?[a-z]/i.test(value)) throw new Error(locale + ': invalid text for ' + key);
      if (JSON.stringify(placeholders(value)) !== JSON.stringify(placeholders(expected[index]))) throw new Error(locale + ': placeholders differ for ' + key);
    });
  }
}
function render(language, text) {
  const cjk = ['zh-CN', 'zh-TW', 'ja-JP'].includes(language.locale);
  const colon = cjk ? '：' : language.locale === 'fr-FR' ? ' : ' : ': ';
  const period = cjk ? '。' : '.';
  const experimentalSuffix = cjk ? '（' + text.experimental + '）' : ' (' + text.experimental + ')';
  const releaseUrl = shared.repository + '/releases';
  const downloadAsset = suffix => releaseUrl + '/download/' + shared.release + '/SonicRoute-' + shared.release + '-' + suffix;
  const variables = { release: shared.release, version: shared.release.slice(1), nextRelease: shared.nextRelease,
    triggerCount: shared.triggerCount, actionCount: shared.actionCount, ruleCommand: 'sr "' + text.ruleExample + '"',
    faq: link(text.faq, wiki(pages.faq)), history: link(text.history, wiki(pages.history)),
    runtime: link('.NET 10 Desktop Runtime', shared.runtime), runtime8: link('.NET 8 Desktop Runtime', shared.runtime8) };
  const fill = value => value.replace(/\{([a-zA-Z][a-zA-Z0-9]*)\}/g, (_, key) => {
    if (!(key in variables)) throw new Error('Unknown shared placeholder: ' + key);
    return variables[key];
  });
  const scenarioIcons = ['🎮 ', '🎵 ', '⚡ ', '🎙️ ', '📹 '];
  const editionIcons = ['⭐ ', '⚡ ', '🛡️ ', '🔄 '];
  const rows = [
    [editionIcons[0] + text.editionLiteX64, link(text.singleExe, downloadAsset('Lite-x64.exe')), text.liteRequirement],
    [editionIcons[1] + text.editionLiteArm64 + experimentalSuffix, link(text.singleExe, downloadAsset('Lite-arm64.exe')), text.arm64Requirement],
    ['☕ ' + text.editionLiteNet8X64, link(text.singleExe, downloadAsset('Lite-NET8-x64.exe')), text.net8RequirementX64],
    ['☕ ' + text.editionLiteNet8Arm64 + experimentalSuffix, link(text.singleExe, downloadAsset('Lite-NET8-arm64.exe')), text.net8RequirementArm64],
    [editionIcons[2] + text.editionLegacyX64, link(text.fullZip, downloadAsset('Legacy-x64.zip')), text.legacyRequirement],
    [editionIcons[3] + text.editionStore, link(text.storeInstall, shared.store), text.storeRequirement]
  ];
  const navigation = shared.languages.map(item => item.locale === language.locale ? `**${item.name}**` : link(item.name, item.file)).join(' | ') + ' | ' + link('🤖 AI', 'README.ai.md');
  const sponsorBadge = language.locale === 'zh-CN' ? '赞助-爱发电' : 'Support-Afdian';
  const sponsor = `[![${text.support}](https://img.shields.io/badge/${sponsorBadge}-946CE6?logo=afdian&logoColor=white)](${shared.author.afdian})`;
  const automationImage = `<a href="${wiki(pages.automationGuide)}" title="${attr(text.automationGuide)}"><img src="docs/images/${attr(language.automationMascot)}" width="48" align="middle" alt="${attr(text.automationAlt)}"></a>`;
  const docGroups = [ ['guide', 'featuresGuide'], ['hotkeysGuide'], ['automationGuide', 'commandGuide'], ['faq'], ['technical', 'releaseInfo', 'history'] ];
  const documentLink = key => link(text[key], wiki(key === 'releaseInfo' ? pages.roadmap : pages[key]));
  return [
    '<!-- Generated from docs/readme/shared.json and docs/readme/locales/' + language.locale + '.json. Edit those sources, then run node docs/readme/generate.cjs. -->',
    '<!-- AI / LLM Context: ' + wiki(pages.ai) + ' -->',
    '# 🎧 ' + text.title, '', navigation, '',
    `[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=34A853&logo=github)](${releaseUrl}) ` +
      '[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=4285F4)](LICENSE) ' +
      `[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue)](${shared.repository}) ` +
      `[![Arch](https://img.shields.io/badge/Arch-x64%20%7C%20ARM64-orange)](${releaseUrl}) ` +
      `[![Downloads](https://img.shields.io/github/downloads/kunkunkunQoQ/SonicRoute/total?color=blue)](${releaseUrl}) ` + sponsor, '',
    '**' + text.tagline + '**', '', text.intro, '',
    '📥 ' + [link(text.download, releaseUrl + '/latest'), link('Microsoft Store', shared.store), link(text.guide, wiki(pages.guide)), link(text.wiki, shared.repository + '/wiki')].join(' · '), '',
    shared.minimumWindows + ' · x64 / ARM64' + experimentalSuffix + ' · ' + text.languages + ' · ' + text.license, '',
    '<div>', `<a href="${wiki(pages.guide)}" title="${attr(text.guideTitle)}"><img src="docs/images/mascot-panel-peek.png" width="104" align="middle" alt="${attr(text.mascotAlt)}"></a>`,
    '<br>', `<a href="docs/images/quick-panel.png"><img src="docs/images/quick-panel.png" width="275" align="top" alt="${attr(text.panelAlt)}"></a>`,
    `<a href="docs/images/automation-editor.png"><img src="docs/images/automation-editor.png" width="395" align="top" alt="${attr(text.automationPreviewAlt)}"></a>`, '</div>', '',
    '<sub>' + attr(text.preview) + '</sub>', '', '## ✨ ' + text.featuresTitle, '',
    `<a href="${wiki(pages.featuresGuide)}" title="${attr(text.featuresGuide)}"><img src="docs/images/mascot-music-hoodie.png" width="68" align="right" alt="${attr(text.featuresAlt)}"></a>`, '',
    ...text.features.map(feature => '- ' + fill(feature)), '',
    '## 🎮 ' + text.useCasesTitle, '',
    automationImage + ' ' + text.automationExample + ' ' + link(text.configureAutomation, wiki(pages.automationGuide)), '',
    '| ' + [text.scenario, text.whatYouCanDo].join(' | ') + ' |', '|---|---|',
    ...text.useCaseNames.map((scenario, index) => '| ' + tableCell((scenarioIcons[index] || '') + scenario) + ' | ' + tableCell(fill(text.useCaseActions[index])) + ' |'), '',
    '## 🚀 ' + text.downloadTitle, '',
    '**' + text.currentRelease + colon + link(shared.release, releaseUrl + '/tag/' + shared.release) + '** · ' + shared.releaseDate + ' · ' + link(text.releaseNotes, shared.releaseNotes), '',
    fill(text.downloadIntro), '',
    '| ' + [text.edition, text.downloadColumn, text.requirements].join(' | ') + ' |', '|---|---|---|',
    ...rows.map(row => '| ' + row.map(tableCell).join(' | ') + ' |'), '',
    fill(text.runtimeNote), '',
    ...text.steps.map((step, index) => (index + 1) + '. ' + step), '',
    fill(text.commandNote), '',
    '```bash',
    '# ' + text.commandSnippetComment,
    'sr "' + text.ruleExample + '"',
    '```', '',
    fill(text.releaseSummary), '',
    '### ❓ ' + text.faqQuickTitle, '',
    ...text.faqQuestions.flatMap((question, index) => [
      '**Q' + colon + question + '**',
      '',
      'A' + colon + text.faqAnswers[index],
      ''
    ]),
    '## 📚 ' + text.docsTitle, '', ...(text.wikiNote ? [text.wikiNote, ''] : []),
    '| ' + [text.docTopic, text.docGuides].join(' | ') + ' |', '|---|---|',
    ...text.docTopics.map((topic, index) => '| ' + tableCell(topic) + ' | ' + docGroups[index].map(documentLink).join(' · ') + ' |'), '',
    '## 📌 ' + text.roadmapTitle, '', ...text.roadmapItems.map(item => '- ' + fill(item)), '',
    link(text.roadmapFeedback, shared.repository + '/issues/new/choose') + period, '',
    '## 🧩 ' + text.otherTitle, '',
    '- **' + link('SilentPlayer', shared.projects.silentPlayer) + '**' + colon + text.silentPlayer + ' ' + link(text.combinationGuide, wiki(pages.guide) + '#' + encodeURIComponent(shared.combinationAnchor)),
    '',
    `<a href="${shared.projects.xiaoYueComics}" title="${attr(text.comicTitle)}"><img src="docs/images/mascot-comics.png" width="84" align="left" alt="${attr(text.comicAlt)}"></a>`, '',
    '**' + link(text.comicTitle, shared.projects.xiaoYueComics) + '**', '',
    text.comicDescription, '', '<br clear="all">', '', '---', '',
    `<a href="EASTER_EGGS.md" title="${attr(text.footerTitle)}"><img src="docs/images/mascot-cozy-knit.png" width="56" align="right" alt="${attr(text.easterEgg)}"></a>`, '',
    text.thanks + ' ' + link(text.footerDocs, shared.repository + '/wiki') + ' · ' + link(text.footerIssues, shared.repository + '/issues') + period, '',
    '**' + text.author + colon + [link(text.bilibili, shared.author.bilibili), link(text.afdian, shared.author.afdian), link('GitHub', shared.author.github)].join(' | ') + '**', '',
    `<div align="right"><sub><a href="${wiki(pages.ai)}" title="AI / LLM Context">🤖 AI Context</a></sub></div>`, ''
  ].join('\n');
}
function renderAI(shared) {
  const navigation = shared.languages.map(item => link(item.name, item.file)).join(' | ') + ' | **🤖 AI**';
  return [
    '<!-- Generated via docs/readme/generate.cjs. Metadata from docs/readme/shared.json; body template maintained in renderAI() of docs/readme/generate.cjs. Run node docs/readme/generate.cjs to update. -->',
    '# 🎧 SonicRoute (AI / LLM Machine Specification)', '',
    navigation, '',
    "SCHEMA_SPEC:",
    "  system: \"SonicRoute\"",
    "  platform: \"Windows 10 2004+ (Build 19041+) | Windows 11 (x64 / win-arm64)\"",
    "  version: \"v1.25 (1.25.0.0)\"",
    "  repository: \"https://github.com/kunkunkunQoQ/SonicRoute\"",
    "  wiki_root: \"https://github.com/kunkunkunQoQ/SonicRoute/wiki\"",
    "  download_latest: \"https://github.com/kunkunkunQoQ/SonicRoute/releases/latest\"",
    "  store_url: \"https://apps.microsoft.com/detail/9NQZGRTPM1NT\"",
    "",
    "SYSTEM_ARCHITECTURE_&_FACTUAL_BOUNDS:",
    "  driver_isolation: \"NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_TARGET_PROCESS_DLL_INJECTION\"",
    "  routing_backend: \"Undocumented COM Windows.Media.Internal.AudioPolicyConfig -> Windows PropertyStore persistence\"",
    "  persistence_scope: \"OS-level native persistence (survives app exit & OS reboot without SonicRoute running)\"",
    "  mixer_isolation: \"Per-app endpoint persistence; never corrupts Windows volume mixer\"",
    "  hooks_in_use: \"User-mode only: SetWinEventHook (foreground focus tracking), SetWindowsHookEx WH_MOUSE_LL (tray wheel & mouse hotkey input)\"",
    "  execution_resource_model: \"Event-driven first (WinEventHook + CoreAudio IMMNotificationClient). Conditional timers (1s-2s) activate only during fallback polling (BackgroundPollingFallbackEnabled), rule process probing, or mic mute sync.\"",
    "  cli_ipc_transport: \"NamedPipe IPC (sr.cmd targets running instance; SonicRoute.exe --run-rule cold-boots temporary CLI host if instance absent)\"",
    "  cross_platform: \"FALSE (strictly Win32/WPF/.NET CoreAudio; never macOS/Linux)\"",
    "",
    "CLI_AND_SHORT_COMMAND_SPEC:",
    "  command_invocation: \"sr <RuleName|RuleId> (invokes %LocalAppData%\\\\SonicRoute\\\\Command\\\\sr.cmd wrapping sr-client.ps1) OR SonicRoute.exe --run-rule <selector>\"",
    "  pipe_endpoint: \"\\\\\\\\.\\\\pipe\\\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}\"",
    "  stream_deck_ahk: \"Call sr <RuleName|RuleId> or sr.cmd; waits for completion and returns rule execution result and corresponding exit code\"",
    "  exit_codes:",
    "    0: { code: ok, desc: \"Execution succeeded without step failures\" }",
    "    1: { code: step_failed, desc: \"One or more rule steps failed\" }",
    "    2: { code: invalid_arguments, desc: \"Empty selector or invalid command arguments\" }",
    "    3: { code: rule_not_found, desc: \"Rule ID or name not found in rules store\" }",
    "    4: { code: ambiguous_rule, desc: \"Multiple rules share the same name\" }",
    "    5: { code: rule_busy, desc: \"Target rule is currently executing\" }",
    "    6: { code: server_unavailable, desc: \"Named pipe connection failed (SonicRoute not running)\" }",
    "    7: { code: canceled, desc: \"Execution canceled or app shutdown in progress\" }",
    "    8: { code: internal_error, desc: \"Unhandled exception during execution\" }",
    "",
    "KNOWLEDGE_RETRIEVAL_ROUTER:",
    "  ROUTE_CORE_AUDIO:",
    "    intent: \"Per-app audio routing, default audio devices, sound splitting\"",
    "    keywords: [\"route\", \"split\", \"headset\", \"speaker\", \"device\", \"endpoint\", \"render\", \"capture\", \"分流\", \"设备\", \"耳机\", \"音箱\"]",
    "    wiki: \"02-功能特性.md\"",
    "    source: [\"SonicRoute.Core/AudioService.cs\", \"SonicRoute.Core/Interop/AudioPolicyConfig.cs\", \"SonicRoute.Core/Interop/PolicyConfigClient.cs\"]",
    "    symbols: [\"AudioService.ApplyEndpoint\", \"AudioPolicyConfig.SetDefaultEndPoint\", \"SystemDefaultDeviceService.SetDefault\"]",
    "    facts:",
    "      - \"Routing is driverless via undocumented Windows COM Windows.Media.Internal.AudioPolicyConfig.\"",
    "      - \"Writes per-app device routing to Windows PropertyStore; persists across reboots without SonicRoute running.\"",
    "      - \"Resolution order: Default Device -> App PropertyStore -> Active Audio Session.\"",
    "",
    "  AUTOMATION_ENGINE:",
    "    intent: \"Automation triggers, action execution, recipe creation, rule chaining\"",
    "    keywords: [\"automation\", \"rule\", \"trigger\", \"action\", \"chain\", \"recipe\", \"schedule\", \"自动化\", \"规则\", \"触发器\", \"动作\", \"配方\"]",
    "    wiki: \"04-自动化规则.md\"",
    "    source: [\"SonicRoute/AutoRuleService.cs\", \"SonicRoute/AutoRuleScheduler.cs\", \"SonicRoute.Core/Models/AutoRule.cs\", \"SonicRoute.Core/AutoRuleStore.cs\"]",
    "    symbols: [\"AutoRuleService.ExecuteAsync\", \"AutoRuleTrigger\", \"AutoRuleAction\", \"AutoRuleStore.LoadAll\"]",
    "    facts:",
    "      - \"6 Triggers (0..5) × 37 Actions (0..36).\"",
    "      - \"Action 17 ExecuteRule enables nested rule chaining.\"",
    "      - \"Supports StopOnFailure flag and millisecond StepDelay.\"",
    "      - \"Rules stored as separate JSON files in %LocalAppData%\\\\SonicRoute\\\\Automation\\\\.\"",
    "",
    "  CLI_AND_IPC:",
    "    intent: \"Command-line execution, Stream Deck, AHK, named pipe IPC\"",
    "    keywords: [\"cli\", \"sr\", \"command\", \"stream deck\", \"ahk\", \"pipe\", \"terminal\", \"命令行\", \"终端\", \"管道\"]",
    "    wiki: \"05-命令行执行规则.md\"",
    "    source: [\"SonicRoute/RuleCommandLine.cs\", \"SonicRoute/Program.cs\"]",
    "    symbols: [\"RuleCommandLine.PipeName\", \"RuleCommandLine.SendAsync\", \"RuleCommandLine.ExecuteAsync\", \"RuleCommandServer\"]",
    "    facts:",
    "      - \"Short command sr.cmd invokes sr-client.ps1 (requires running instance in same session/user; exits 6 if unstarted).\"",
    "      - \"Direct CLI SonicRoute.exe --run-rule cold-boots a headless temporary execution host if no instance is running.\"",
    "      - \"IPC via Named Pipe SonicRoute.RuleCommand.{Edition}.{SessionId}.{UserSid}.\"",
    "      - \"Exit codes 0..8 (0=ok, 1=step_failed, 2=invalid_arguments, 3=rule_not_found, etc.).\"",
    "      - \"Waits for rule completion; no fixed execution timeout (10s write timeout).\"",
    "",
    "  PANEL_AND_TRAY:",
    "    intent: \"QuickPanel UI, tray volume scroll wheel, custom icons, blind control\"",
    "    keywords: [\"panel\", \"tray\", \"wheel\", \"icon\", \"quickpanel\", \"height\", \"blind\", \"面板\", \"托盘\", \"滚轮\", \"盲操\", \"图标\"]",
    "    wiki: \"01-使用指南.md\"",
    "    source: [\"SonicRoute/TrayWheelService.cs\", \"SonicRoute/QuickPanelWindow.xaml.cs\", \"SonicRoute/QuickPanelModernWindow.xaml.cs\", \"SonicRoute/CustomAppIconStore.cs\"]",
    "    symbols: [\"TrayWheelService.Start\", \"QuickPanelModernWindow\", \"CustomAppIconStore.Prepare\", \"CustomAppIconStore.Load\"]",
    "    facts:",
    "      - \"Tray mouse wheel blinds-scrolls active app, foreground app, or master volume via WH_MOUSE_LL hook.\"",
    "      - \"QuickPanel heights: 350px to 850px adjustable (v1.25+).\"",
    "      - \"Drag-and-drop PNG/JPEG/ICO onto app button to set custom icon in %LocalAppData%\\\\SonicRoute\\\\Icons\\\\.\"",
    "",
    "  GLOBAL_HOTKEYS:",
    "    intent: \"Global keyboard/mouse shortcuts, mic mute toggle, volume hotkeys\"",
    "    keywords: [\"hotkey\", \"shortcut\", \"bind\", \"key\", \"mouse hook\", \"esc\", \"快捷键\", \"热键\", \"按键\", \"静音快捷键\"]",
    "    wiki: \"03-快捷键.md\"",
    "    source: [\"SonicRoute/HotkeyService.cs\", \"SonicRoute/MouseHotkeyHook.cs\", \"SonicRoute/HotkeyActions.cs\"]",
    "    symbols: [\"HotkeyService.Reload\", \"MouseHotkeyHook\", \"HotkeyActions.All\", \"HotkeyActions.DisplayName\"]",
    "    facts:",
    "      - \"Registered via Win32 RegisterHotKey API with WH_MOUSE_LL hook for mouse button/wheel binds.\"",
    "      - \"Scopes: Global, Foreground App, Last Active App.\"",
    "      - \"Esc cancels recording; conflict detector prevents duplicate binding.\"",
    "",
    "  TROUBLESHOOT_ROUTING_FAILURES:",
    "    intent: \"App route not working, sound still in default device, admin process, EAC/Vanguard\"",
    "    keywords: [\"fail\", \"not working\", \"no sound\", \"admin\", \"vanguard\", \"eac\", \"anticheat\", \"失效\", \"不生效\", \"没声音\", \"管理员\", \"反作弊\"]",
    "    wiki: \"06-常见问题.md\"",
    "    source: [\"SonicRoute.Core/AudioService.cs\", \"SonicRoute.Core/ProcessSnapshotService.cs\"]",
    "    symbols: [\"AudioService.GetApps\", \"AudioService.InvalidateAppsCache\", \"ProcessSnapshotService.Capture\"]",
    "    facts:",
    "      - \"Admin processes: Non-admin SonicRoute cannot alter elevated app COM session. Fix: Run SonicRoute as Administrator.\"",
    "      - \"Anti-cheat (Valorant/Vanguard, EasyAntiCheat): Kernel drivers block runtime COM modification. Fix: Set route before launching game.\"",
    "      - \"Inactive audio session: Windows only registers WASAPI session when audio is actively outputting. Play sound first.\"",
    "",
    "  TROUBLESHOOT_RUNTIMES_AND_CRASHES:",
    "    intent: \"App crash on launch, missing runtime, .NET 10 / .NET 8, Store MSIX vs Portable\"",
    "    keywords: [\"crash\", \"runtime\", \"net10\", \"net8\", \"net48\", \"store\", \"portable\", \"msix\", \"闪退\", \"运行库\", \"打不开\", \"报错\"]",
    "    wiki: \"08-版本相关.md\"",
    "    source: [\"SonicRoute/AppRuntimeIdentity.cs\", \"SonicRoute.Core/Compat/WindowsVersion.cs\"]",
    "    symbols: [\"AppRuntimeIdentity.Current\", \"AppRuntimeIdentity.CommandEdition\", \"WindowsVersion.Build\"]",
    "    facts:",
    "      - \"Lite editions require Windows Desktop Runtime (x64/ARM64), NOT standard console runtime.\"",
    "      - \"Legacy x64 requires .NET Framework 4.8 (preinstalled on Windows 10/11).\"",
    "      - \"OS requirement: Windows 10 Build 19041+ (v2004) or Windows 11.\"",
    "",
    "  RESOURCE_AND_PERFORMANCE:",
    "    intent: \"CPU usage, memory footprint, background timers, polling frequency\"",
    "    keywords: [\"cpu\", \"memory\", \"resource\", \"poll\", \"leak\", \"lag\", \"占用\", \"性能\", \"卡顿\", \"内存\", \"后台\"]",
    "    wiki: \"07-技术实现.md\"",
    "    source: [\"SonicRoute.Core/AudioMeterService.cs\", \"SonicRoute/ForegroundChangeSource.cs\", \"SonicRoute/App.BackgroundMonitoring.cs\"]",
    "    symbols: [\"AudioMeterService.Start\", \"AudioMeterService.Stop\", \"ForegroundChangeSource\"]",
    "    facts:",
    "      - \"Event-driven first architecture using WinEventHook and CoreAudio events.\"",
    "      - \"Background polling fallback (1s-2s) is disabled by default; runs only if explicitly configured or WMI trace fails.\"",
    "      - \"AudioMeterService peak monitoring runs exclusively while UI panels are open; stops when closed.\"",
    "",
    "  SOURCE_ARCHITECTURE_AND_BUILD:",
    "    intent: \"Codebase architecture, COM interop, multi-target compile\"",
    "    keywords: [\"source\", \"build\", \"architecture\", \"compile\", \"csproj\", \"com interop\", \"架构\", \"编译\", \"源码\", \"工程\"]",
    "    wiki: \"07-技术实现.md\"",
    "    source: [\"SonicRoute.sln\", \"SonicRoute/SonicRoute.csproj\", \"SonicRoute.Core/SonicRoute.Core.csproj\"]",
    "    symbols: [\"SonicRoute\", \"SonicRoute.Core\"]",
    "    facts:",
    "      - \"SonicRoute (WPF presentation & shell) -> SonicRoute.Core (netstandard2.0 / net8 / net10 multi-targeted domain).\"",
    "      - \"SonicRoute.Legacy: .NET Framework 4.8 compatibility shell.\"",
    "",
    "  CONFIG_AND_STORAGE:",
    "    intent: \"Configuration file locations, rules backup, custom icon cache, history\"",
    "    keywords: [\"config\", \"path\", \"storage\", \"rules.json\", \"backup\", \"location\", \"配置\", \"路径\", \"备份\", \"目录\"]",
    "    wiki: \"01-使用指南.md\"",
    "    source: [\"SonicRoute.Core/AppDataRoot.cs\", \"SonicRoute.Core/ConfigService.cs\", \"SonicRoute.Core/AutoRuleStore.cs\"]",
    "    symbols: [\"AppDataRoot.DirectoryPath\", \"ConfigService.ConfigPath\", \"AutoRuleStore.RulesDir\"]",
    "    facts:",
    "      - \"Root dir: %LocalAppData%\\\\SonicRoute\\\\\"",
    "      - \"Config: %LocalAppData%\\\\SonicRoute\\\\config.json (atomic write via temp file)\"",
    "      - \"Rules dir: %LocalAppData%\\\\SonicRoute\\\\Automation\\\\*.json\"",
    "      - \"Command dir: %LocalAppData%\\\\SonicRoute\\\\Command\\\\ (sr.cmd, sr-client.ps1)\"",
    "      - \"Icons dir: %LocalAppData%\\\\SonicRoute\\\\Icons\\\\\"",
    "",
    "  VERSION_ARCHIVE_AND_CHANGELOG:",
    "    intent: \"Release history, changelog, v1.25 upgrade notes\"",
    "    keywords: [\"changelog\", \"history\", \"version\", \"update\", \"v1.25\", \"v1.24\", \"更新日志\", \"版本历史\"]",
    "    wiki: \"09-版本历史.md\"",
    "    source: [\"docs/release-v1.25.md\"]",
    "    symbols: [\"v1.25\"]",
    "    facts:",
    "      - \"v1.25: .NET 10 default, native updater, 37 actions, drag icon, 850px panel.\"",
    "      - \"v1.24: Card-style release notes, audio meter optimizations.\"",
    "",
    "DOMAIN_ENUM_REGISTRY:",
    "  AutoRuleTrigger:",
    "    0: { name: Hotkey, scope: GLOBAL_INPUT, desc: \"Key combination or mouse button\" }",
    "    1: { name: AppStart, scope: PROCESS_EVENT, desc: \"Process creation detected via Trace/Snapshot\" }",
    "    2: { name: AppSwitch, scope: FOREGROUND_EVENT, desc: \"Active window audio session focus change\" }",
    "    3: { name: AppExit, scope: PROCESS_EVENT, desc: \"Process termination detected\" }",
    "    4: { name: Schedule, scope: TIME_EVENT, desc: \"Single/Daily/Weekly schedule\" }",
    "    5: { name: Startup, scope: LIFECYCLE_EVENT, desc: \"SonicRoute app boot completion (once per run)\" }",
    "",
    "  AutoRuleAction:",
    "    0: { name: SetSystemOutput, target: DEFAULT_RENDER_DEVICE, param: DeviceID }",
    "    1: { name: SetSystemInput, target: DEFAULT_CAPTURE_DEVICE, param: DeviceID }",
    "    2: { name: SetSystemVolume, target: SYSTEM_MASTER_VOLUME, param: 0..100 }",
    "    3: { name: ToggleSystemMute, target: SYSTEM_MASTER_VOLUME, param: TOGGLE }",
    "    4: { name: SetAppVolume, target: APP_PLAYBACK_SESSION, param: 0..100 }",
    "    5: { name: ToggleAppMute, target: APP_PLAYBACK_SESSION, param: TOGGLE }",
    "    6: { name: SetAppOutput, target: APP_PLAYBACK_ROUTING, param: DeviceID }",
    "    7: { name: SetAppInput, target: APP_CAPTURE_ROUTING, param: DeviceID }",
    "    8: { name: LaunchProgram, target: OS_EXEC, param: \"FilePath, Args, WorkingDir\" }",
    "    9: { name: RunPowerShell, target: OS_EXEC, param: ScriptText }",
    "    10: { name: ShowOsd, target: UI_HUD, param: \"Title, Subtitle, Timeout\" }",
    "    11: { name: SetSystemMute, target: SYSTEM_MASTER_VOLUME, param: BOOL_EXPLICIT }",
    "    12: { name: SetAppMute, target: APP_PLAYBACK_SESSION, param: BOOL_EXPLICIT }",
    "    13: { name: AdjustSystemVolume, target: SYSTEM_MASTER_VOLUME, param: \"DeltaPercent (+/-)\" }",
    "    14: { name: AdjustAppVolume, target: APP_PLAYBACK_SESSION, param: \"DeltaPercent (+/-)\" }",
    "    15: { name: SetGlobalMicMute, target: ALL_CAPTURE_ENDPOINTS, param: BOOL_EXPLICIT }",
    "    16: { name: ToggleGlobalMicMute, target: ALL_CAPTURE_ENDPOINTS, param: TOGGLE }",
    "    17: { name: ExecuteRule, target: AUTOMATION_ENGINE, param: \"RuleID (Chained Execution)\" }",
    "    18: { name: SetMicrophoneMute, target: SPECIFIC_CAPTURE_ENDPOINT, param: \"DeviceID, BOOL_EXPLICIT\" }",
    "    19: { name: ToggleMicrophoneMute, target: SPECIFIC_CAPTURE_ENDPOINT, param: \"DeviceID, TOGGLE\" }",
    "    20: { name: SetPlaybackDeviceVolume, target: SPECIFIC_RENDER_ENDPOINT, param: \"DeviceID, 0..100\" }",
    "    21: { name: AdjustPlaybackDeviceVolume, target: SPECIFIC_RENDER_ENDPOINT, param: \"DeviceID, DeltaPercent\" }",
    "    22: { name: SetPlaybackDeviceMute, target: SPECIFIC_RENDER_ENDPOINT, param: \"DeviceID, BOOL_EXPLICIT\" }",
    "    23: { name: TogglePlaybackDeviceMute, target: SPECIFIC_RENDER_ENDPOINT, param: \"DeviceID, TOGGLE\" }",
    "    24: { name: SetDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: 0..100 }",
    "    25: { name: AdjustDefaultInputVolume, target: RUNTIME_DEFAULT_CAPTURE, param: DeltaPercent }",
    "    26: { name: SetDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: BOOL_EXPLICIT }",
    "    27: { name: ToggleDefaultInputMute, target: RUNTIME_DEFAULT_CAPTURE, param: TOGGLE }",
    "    28: { name: SetAppInputMute, target: APP_CAPTURE_SESSION, param: \"ProcessName, BOOL_EXPLICIT\" }",
    "    29: { name: ToggleAppInputMute, target: APP_CAPTURE_SESSION, param: \"ProcessName, TOGGLE\" }",
    "    30: { name: SetAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: 0..100 }",
    "    31: { name: AdjustAllAppsVolume, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: DeltaPercent }",
    "    32: { name: SetAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: BOOL_EXPLICIT }",
    "    33: { name: ToggleAllAppsMute, target: ALL_ACTIVE_PLAYBACK_SESSIONS, param: TOGGLE }",
    "    34: { name: ResetAllAppRouting, target: ALL_PERSISTED_APP_ROUTES, param: RESET_TO_DEFAULT }",
    "    35: { name: ShowQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_SHOW }",
    "    36: { name: HideQuickPanel, target: UI_WINDOW, param: IDEMPOTENT_HIDE }",
    "",
    "DATA_PATHS_&_CONFIG_SCHEMA:",
    "  root_dir: \"%LocalAppData%\\\\SonicRoute\\\\\"",
    "  config_file: \"%LocalAppData%\\\\SonicRoute\\\\config.json\"",
    "  rules_dir: \"%LocalAppData%\\\\SonicRoute\\\\Automation\\\\*.json\"",
    "  command_dir: \"%LocalAppData%\\\\SonicRoute\\\\Command\\\\ (sr.cmd, sr-client.ps1)\"",
    "  icons_dir: \"%LocalAppData%\\\\SonicRoute\\\\Icons\\\\\"",
    "  history_dir: \"%LocalAppData%\\\\SonicRoute\\\\History\\\\\"",
    "  pipe_pattern: \"\\\\\\\\.\\\\pipe\\\\SonicRoute.RuleCommand.{CommandEdition}.{SessionId}.{UserSid}\"",
    "",
    "QUERY_DECISION_MATRIX_FOR_AGENTS:",
    "  WHEN user_says_routing_fails:",
    "    CHECK_1: \"Is target app elevated? (Admin process blocks non-admin COM manipulation -> run SonicRoute as Admin)\"",
    "    CHECK_2: \"Is app an anti-cheat game (Valorant/Vanguard, EAC)? (Kernel hooks block policy config -> set routing BEFORE anti-cheat launches)\"",
    "    CHECK_3: \"Is audio stream actively playing? (Windows creates WASAPI session only on active sound stream; start sound first)\"",
    "  WHEN user_asks_cli_or_scripting:",
    "    SYNTAX: \"sr <RuleName|RuleID> OR sr.cmd <RuleName|RuleID>\"",
    "    IPC_PROTOCOL: \"Named pipe SonicRoute.RuleCommand.{Edition}.{SessionId}.{UserSid} (same session/user). Connect timeout 250ms (sr) / 5000ms (--run-rule); write timeout 10s; no fixed execution timeout.\"",
    "    EXIT_CODE_MAP: \"0=ok, 1=step_failed, 2=invalid_arguments, 3=rule_not_found, 4=ambiguous_rule, 5=rule_busy, 6=server_unavailable, 7=canceled, 8=internal_error.\"",
    "  WHEN user_asks_runtime_error:",
    "    ERROR_MISSING_NET_DESKTOP:",
    "      CAUSE: \"Installed base .NET Runtime instead of Windows Desktop Runtime.\"",
    "      FIX: \"Download .NET Desktop Runtime (x64/ARM64) from official Microsoft link.\"",
    "    ALTERNATIVE: \"Advise Legacy x64 build (uses system .NET Framework 4.8; no separate runtime required).\"",
    "  WHEN user_asks_cpu_consumption:",
    "    RESPONSE_FACT: \"Idle CPU is minimized via WinEventHook and CoreAudio event callbacks. Conditional fallback timers (1s-2s) only run if fallback polling mode is explicitly enabled or when actively probing rules.\"",
    "",
    '',
    '<div align="right"><sub><a href="' + shared.repository + '/wiki/AI" title="AI Context">🤖 Wiki AI Knowledge Base</a></sub></div>', ''
  ].join('\n');
}
// Validate and render every locale before writing any output.
const outputs = shared.languages.map(language => {
  if (!/^README(?:\.[a-zA-Z-]+)?\.md$/.test(language.file) || !/^[a-z]{2}-[A-Z]{2}$/.test(language.locale)) throw new Error('Invalid README filename or locale');
  if (!/^mascot-automation(?:-[a-z]{2})?\.png$/.test(language.automationMascot)) throw new Error('Invalid automation mascot filename for ' + language.locale);
  const text = JSON.parse(fs.readFileSync(path.join(__dirname, 'locales', language.locale + '.json'), 'utf8'));
  validate(language.locale, text);
  return { file: language.file, body: render(language, text) };
});
outputs.push({ file: 'README.ai.md', body: renderAI(shared) });
const stale = [];
for (const output of outputs) {
  const file = path.join(root, output.file);
  if (check) {
    if (!fs.existsSync(file) || fs.readFileSync(file, 'utf8').replaceAll('\r\n', '\n') !== output.body) stale.push(output.file);
  } else fs.writeFileSync(file, output.body);
}
if (stale.length) { console.error('README output is out of date: ' + stale.join(', ') + '. Run node docs/readme/generate.cjs.'); process.exitCode = 1; }
else console.log((check ? 'Checked ' : 'Generated ') + outputs.length + ' README files.');
