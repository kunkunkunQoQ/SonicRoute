# README 维护 / Maintaining the READMEs

九份 README 是同一版式的静态 Markdown，GitHub 可直接显示。简体中文保留根 README.md，英文保留 README.en.md，另有繁体中文、日语、韩语、德语、法语、西班牙语和俄语入口。

| 需要更新的内容 | 唯一维护位置 |
|---|---|
| 版本、发布日期、下载及作者链接、语言导航 | [shared.json](shared.json) |
| 各语言简介、功能体验、使用示例与替代文本 | [locales](locales/) 下对应语言 JSON；`features` 保持四个以内的体验重点，`useCaseExample` 与 `automationExample` 提供简短场景 |
| 排版、徽章、图片尺寸与共同结构 | [generate.cjs](generate.cjs) |
| AI / LLM 事实索引 README.ai.md | generate.cjs 的 renderAI()；正式版读取 shared.json，源码版本读取 build/Version.props，发布与开发状态分别说明 |
| 详细操作与排障 | [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki) 或现有 docs 正文 |
| 某版本的变化及升级说明 | docs/release-vX.Y.md，shared.json 链接该文件 |
| 计划版本及触发/操作数量 | shared.json；相应说明保留在各语言文案 |

不要直接编辑生成的 README。使用 Node.js 24，无需安装依赖，从仓库根目录运行：

```powershell
node docs/readme/generate.cjs
node docs/readme/generate.cjs --check
```

第一条生成九份语言 README 和一份 README.ai.md，共十份；第二条只检查，不写文件。检查缺少语言、字段、列表项数、占位符和生成结果不同步时返回非零退出码。GitHub Actions 会运行同一检查（包括开发版本文件修改），不自动翻译或提交。

发布新版时统一更新 shared.json 中的 release、releaseDate、releaseNotes；由 release 自动拼出五个程序包直链，不逐份手改。最新下载入口始终指向 Releases/latest，商店独立使用其在架版本。releaseSummary维护各语言的版本摘要；详细变化仍由现有发布说明维护。后续计划的版本号由 nextRelease 统一维护。修改 build/Version.props 后也要重新生成 README.ai.md，不能把开发版本冒充正式版；AI 索引内的源码、参数、路径及事实说明仍需人工对照实现，生成一致性检查不等于事实自动验收。

修改功能说明时，以实现和中文文案核对语义，再更新九种文案、生成并审读。`features` 不再区分核心/次要功能，用四个以内的重点说明用户可以少做哪些操作；使用示例只保留具体场景，避免重复功能介绍。详细步骤放在 Wiki。各字段里的占位符必须保留；`{release}`、`{version}`、`{nextRelease}`统一版本，`{triggerCount}`、`{actionCount}`统一数量，`{arch}`、`{runtime}`、`{faq}`、`{history}`统一架构与链接，`{ruleCommand}`统一命令示例。`sr` 是命令名，只有示例规则名称可以本地化。翻译要符合当地表达，模式和控件名称参考 SonicRoute/Resources/Lang；JSON 结构检查不代表母语审校。

提交本目录的源文件、十份生成 README 以及检查流程。生成器不会读本地维护档案、用户配置或签名材料。功能介绍与场景保持简短，下载、入门、版本摘要、文档表、后续计划、其他作品和看板娘继续由同一版式维护。

---

The nine READMEs share one layout and link manifest. Edit shared.json for versions, links and common facts, locales/<language>.json for text, and generate.cjs for layout. Keep features to four practical benefits and use short, concrete examples. Downloads, getting-started steps, release summary, documentation table, roadmap, other projects and artwork share the same layout.

Run the commands above with Node.js 24; no dependencies are needed. They cover the nine language READMEs plus README.ai.md. Maintain AI implementation facts in renderAI(); its stable version comes from shared.json and its source version from build/Version.props. Regenerate after changing either version source. Commit both the sources and generated Markdown. `--check` is read-only and fails when a translation is incomplete or the generated files are stale. The workflow checks consistency, not factual accuracy; it does not translate, publish or commit changes.
