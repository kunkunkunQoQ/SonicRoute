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
    runtime: link('.NET 8 Desktop Runtime', shared.runtime) };
  const fill = value => value.replace(/\{([a-zA-Z][a-zA-Z0-9]*)\}/g, (_, key) => {
    if (!(key in variables)) throw new Error('Unknown shared placeholder: ' + key);
    return variables[key];
  });
  const rows = [
    ['Lite x64', link(text.singleExe, downloadAsset('Lite-x64.exe')), text.liteRequirement.replace('{arch}', 'x64')],
    ['Lite ARM64' + experimentalSuffix, link(text.singleExe, downloadAsset('Lite-arm64.exe')), text.liteRequirement.replace('{arch}', 'ARM64')],
    ['Legacy x64', link(text.fullZip, downloadAsset('Legacy-x64.zip')), text.legacyRequirement],
    ['Microsoft Store', link(text.storeInstall, shared.store), text.storeRequirement]
  ];
  const navigation = shared.languages.map(item => item.locale === language.locale ? `**${item.name}**` : link(item.name, item.file)).join(' | ');
  const sponsorBadge = language.locale === 'zh-CN' ? '赞助-爱发电' : 'Support-Afdian';
  const sponsor = `[![${text.support}](https://img.shields.io/badge/${sponsorBadge}-946CE6?logo=afdian&logoColor=white)](${shared.author.afdian})`;
  const automationImage = `<a href="${wiki(pages.automationGuide)}" title="${attr(text.automationGuide)}"><img src="docs/images/${attr(language.automationMascot)}" width="48" align="middle" alt="${attr(text.automationAlt)}"></a>`;
  const docGroups = [ ['guide', 'featuresGuide'], ['hotkeysGuide'], ['automationGuide', 'commandGuide'], ['faq'], ['technical', 'releaseInfo', 'history'] ];
  const documentLink = key => link(text[key], wiki(key === 'releaseInfo' ? pages.roadmap : pages[key]));
  return [
    '<!-- Generated from docs/readme/shared.json and docs/readme/locales/' + language.locale + '.json. Edit those sources, then run node docs/readme/generate.cjs. -->',
    '# 🎧 ' + text.title, '', navigation, '',
    '[![License: MIT](https://img.shields.io/github/license/kunkunkunQoQ/SonicRoute?color=green)](LICENSE) ' +
      `[![Release](https://img.shields.io/github/v/release/kunkunkunQoQ/SonicRoute?color=blue)](${releaseUrl}) ` +
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
    text.useCaseExample, '',
    automationImage + ' ' + text.automationExample + ' ' + link(text.configureAutomation, wiki(pages.automationGuide)), '',
    '## 🚀 ' + text.downloadTitle, '',
    '**' + text.currentRelease + colon + link(shared.release, releaseUrl + '/tag/' + shared.release) + '** · ' + shared.releaseDate + ' · ' + link(text.releaseNotes, shared.releaseNotes), '',
    fill(text.downloadIntro), '',
    '| ' + [text.edition, text.downloadColumn, text.requirements].join(' | ') + ' |', '|---|---|---|',
    ...rows.map(row => '| ' + row.map(tableCell).join(' | ') + ' |'), '',
    fill(text.runtimeNote), '',
    ...text.steps.map((step, index) => (index + 1) + '. ' + step), '',
    fill(text.commandNote), '', fill(text.releaseSummary), '',
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
    '**' + text.author + colon + [link(text.bilibili, shared.author.bilibili), link(text.afdian, shared.author.afdian), link('GitHub', shared.author.github)].join(' | ') + '**', ''
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
const stale = [];
for (const output of outputs) {
  const file = path.join(root, output.file);
  if (check) {
    if (!fs.existsSync(file) || fs.readFileSync(file, 'utf8').replaceAll('\r\n', '\n') !== output.body) stale.push(output.file);
  } else fs.writeFileSync(file, output.body);
}
if (stale.length) { console.error('README output is out of date: ' + stale.join(', ') + '. Run node docs/readme/generate.cjs.'); process.exitCode = 1; }
else console.log((check ? 'Checked ' : 'Generated ') + outputs.length + ' README files.');
