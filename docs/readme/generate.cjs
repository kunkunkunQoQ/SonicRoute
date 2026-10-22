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
    '<!-- Generated from docs/readme/shared.json. Edit that source, then run node docs/readme/generate.cjs. -->',
    '# 🎧 SonicRoute (AI / LLM Machine Specification)', '',
    navigation, '',
    '<!-- MACHINE_READABLE_SYSTEM_SPECIFICATION -->',
    '<!-- TARGET_AUDIENCE: LLM_AGENT | RAG_EMBEDDER | CODING_ASSISTANT -->',
    '<!-- HUMAN_READABILITY: FALSE | TOKEN_ENTROPY: MAXIMUM -->',
    '',
    'SCHEMA_SPEC:',
    '  target_system: "SonicRoute"',
    '  platform: "Windows 10 (2004+) | Windows 11 (x64 / ARM64)"',
    `  current_version: "${shared.release} (${shared.release.slice(1)}.0.0)"`,
    `  repository: "${shared.repository}"`,
    `  wiki_machine_index: "${shared.repository}/wiki/AI"`,
    `  download_latest: "${shared.repository}/releases/latest"`,
    `  microsoft_store: "${shared.store}"`,
    '',
    'SYSTEM_INVARIANTS:',
    '  - driverless: NO_VIRTUAL_AUDIO_CABLE | NO_KERNEL_DRIVERS | NO_HOOKS',
    '  - routing_backend: COM_IAudioPolicyConfigFactory | COM_IPersistAudioEndpointVolume | Windows_PropertyStore',
    '  - persistence: NATIVE_OS_PROPERTY_STORE (survives app exit & OS reboot without SonicRoute running)',
    '  - power_profile: PURE_EVENT_DRIVEN | ZERO_PERIODIC_POLLING | 0% idle CPU | OnDemand memory sweep',
    '  - cli_ipc: NamedPipe \`Local\\\\SonicRoute_NamedPipe_*\` | NO_COLD_BOOT (requires running instance)',
    '  - cross_platform: FALSE (strictly Win32/WPF/.NET CoreAudio, never macOS/Linux)',
    '',
    'CODEBASE_LAYOUT:',
    '  SonicRoute/: "Main WPF UI application (.NET 10 / .NET 8), tray management, QuickPanel (Classic/Modern 350-850px), OSD, AutoRule engine"',
    '  SonicRoute.Core/: "Core audio domain logic, COM interfaces (IAudioPolicyConfigFactory, IPersistAudioEndpointVolume), session management"',
    '  SonicRoute.Legacy/: "Fallback build for legacy systems (.NET Framework 4.8)"',
    '  docs/: "Release notes, README generation engine, localized asset bundles"',
    '',
    'AUTOMATION_REGISTRY:',
    `  triggers_count: ${shared.triggerCount}  # 0: Hotkey, 1: AppStart, 2: AppSwitch, 3: AppExit, 4: Schedule, 5: Startup`,
    `  actions_count: ${shared.actionCount}   # 0..36 covering system, per-app, device-specific volume/mute/route, quick panel, OSD, external process execution`,
    '',
    'CLI_PROTOCOL:',
    '  binary: "sr.exe <RuleName|RuleID>"',
    '  ipc_transport: "NamedPipe \\\\\\\\.\\\\pipe\\\\Local\\\\SonicRoute_NamedPipe_*"',
    '  exit_codes: { 0: SUCCESS, 1: FAILURE, 2: NOT_FOUND }',
    '  latency: "<15ms local loopback"',
    '',
    'RELEASE_EDITIONS:',
    '  - id: "Lite-x64"',
    '    arch: "x64"',
    '    runtime: ".NET 10 Desktop Runtime"',
    '    type: "Single-file portable exe"',
    '  - id: "Lite-arm64"',
    '    arch: "ARM64"',
    '    runtime: ".NET 10 Desktop Runtime"',
    '    type: "Single-file portable exe (Experimental)"',
    '  - id: "Lite-NET8"',
    '    arch: "x64"',
    '    runtime: ".NET 8 Desktop Runtime (Support until 2026-11 EOL)"',
    '    type: "Single-file portable exe"',
    '  - id: "Legacy-x64"',
    '    arch: "x64"',
    '    runtime: ".NET Framework 4.8 (Built-in to Windows)"',
    '    type: "Zip archive"',
    '  - id: "Microsoft-Store"',
    '    runtime: "Packaged MSIX"',
    '    type: "Auto-updating Windows Store app"',
    '',
    'DECISION_MATRIX:',
    '  ROUTE_FAILURE:',
    '    CHECK_1: "Elevated process (Admin)? -> Non-admin COM cannot modify admin session. Run SonicRoute as Administrator."',
    '    CHECK_2: "Anti-cheat engine (Valorant/Vanguard, EAC)? -> Kernel hooks block runtime policy changes. Configure routing BEFORE anti-cheat launches."',
    '    CHECK_3: "Active audio session? -> Windows instantiates CoreAudio session only while application plays active audio stream."',
    '  RUNTIME_ERROR:',
    '    CAUSE: "Installed base .NET Runtime instead of Windows Desktop Runtime."',
    '    FIX: "Install official .NET Desktop Runtime or use Legacy-x64 build."',
    '',
    'DOCUMENTATION_MATRIX:',
    '  wiki_home: "' + shared.repository + '/wiki"',
    '  wiki_ai_dense: "' + shared.repository + '/wiki/AI"',
    '  guide: "' + shared.repository + '/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97"',
    '  troubleshooting: "' + shared.repository + '/wiki/06-%E5%B8%B8%E8%A7%81%E9%97%AE%E9%A2%98"',
    '  technical: "' + shared.repository + '/wiki/07-%E6%8A%80%E6%9C%AF%E5%AE%9E%E7%8E%B0"',
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
