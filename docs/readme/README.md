# README 维护 / Maintaining the READMEs

九份 README 共用静态 Markdown 版式，GitHub 可直接显示。简体中文使用根目录的 `README.md`，英文为 `README.en.md`，另有繁体中文、日语、韩语、德语、法语、西班牙语和俄语。

| 需要更新的内容 | 维护位置 |
| --- | --- |
| 手册版本、已发布记录、下载与作者链接、语言和 Wiki 导航 | [shared.json](shared.json) |
| 各语言简介、功能、场景、图注和替代文字 | [locales](locales/) 下对应语言的 JSON |
| 排版、徽章、图片尺寸与共同结构 | [generate.cjs](generate.cjs) |
| 各版本的多语言摘要 | [releases](releases/) 下对应版本的 JSON |
| 各版本的详细功能与升级说明 | `docs/release-v<版本>.md` |
| AI 事实索引 `README.ai.md` | [generate.cjs](generate.cjs) 的 `renderAI()`。实现、参数与路径应对照源码 |
| 操作步骤、排障和截图 | [Wiki](https://github.com/kunkunkunQoQ/SonicRoute/wiki) 和现有 docs 正文 |

`documentationVersion` 选择 README 展示的版本、`docs/release-<documentationVersion>.md` 版本说明，以及 `releases/<documentationVersion>.json` 的九语摘要。上一版摘要由较早的版本记录提供，v1.33 摘要仍保存在各语言的 `releaseSummary`。功能文案、示例和常见问题同步到手册讲解的版本。

`release`、`releaseDate` 和 `releaseNotes` 记录已发布的版本、实际发布日期及该版说明。只有核实发行页和对应附件后才更新这些字段。`arm64LastRelease` 记录确实附带 ARM64 便携包的最近版本。手册版本的对应资产未发布时，表中显示文件名，下载入口统一指向 [GitHub 发行页](https://github.com/kunkunkunQoQ/SonicRoute/releases)。ARM64 资产链接也必须与实际已发布的架构版本一致。Microsoft Store 通过各自商店页面展示在架版本。

2.00 README 的首图取自 Wiki。替代文字使用各语言的 `canvasAlt`，图注使用 `preview`，入口指向使用指南的 `routing-canvas` 锚点。折叠区保留并标明 v1.36 界面示例。[2.00 画布截图](https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png) · [画布操作](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas)

用 `features` 的四项功能概括用户能做什么。保持 `useCaseNames` 和 `useCaseActions` 的顺序一致。修改文案时保留原字段所需的占位符，`{release}` 对应手册版本，`{triggerCount}` 和 `{actionCount}` 对应数量，`{faq}` 和 `{history}` 对应文档链接。命令名 `sr` 保持原样，示例规则名称可本地化。界面名称按 `SonicRoute/Resources/Lang` 核对。

从仓库根目录使用 Node.js 24 运行，无需安装依赖。

```powershell
node docs/readme/generate.cjs
node docs/readme/generate.cjs --check
```

第一条生成九份语言 README 和 `README.ai.md`。第二条只读检查语种、字段、列表长度、占位符和生成结果。修改版本参数、功能文案或 `build/Version.props` 后，重新生成并提交源文件与生成结果。GitHub Actions 运行同样的一致性检查。事实和翻译质量仍需人工审阅。

---

## English maintenance guide

The nine language READMEs share a static Markdown layout. Edit [shared.json](shared.json) for version records, shared facts and links, [locales](locales/) for translated copy, and [generate.cjs](generate.cjs) for layout. Maintain AI implementation facts in `renderAI()` and verify its source version, paths and commands against the repository.

`documentationVersion` selects the version shown in the READMEs, the notes at `docs/release-<documentationVersion>.md`, and the localized summaries in `releases/<documentationVersion>.json`. The previous summary comes from the preceding version record. The v1.33 summary remains in each locale’s `releaseSummary`.

`release`, `releaseDate` and `releaseNotes` record the published version, its actual publication date and its notes. Set `arm64LastRelease` to the most recent version that actually shipped the ARM64 portable assets. Update published records after verifying the release and its attachments. When an asset for the documentation version is unavailable, display its filename and direct readers to [GitHub Releases](https://github.com/kunkunkunQoQ/SonicRoute/releases). Microsoft Store listings show their available versions separately.

The main 2.00 screenshot comes from the Wiki. Supply localized `canvasAlt` and `preview` strings and link to the `routing-canvas` section of the user guide. The collapsible section contains labeled 1.36 examples. [Routing canvas image](https://raw.githubusercontent.com/wiki/kunkunkunQoQ/SonicRoute/images/routing-canvas-v2.00.png) · [Routing canvas guide](https://github.com/kunkunkunQoQ/SonicRoute/wiki/01-%E4%BD%BF%E7%94%A8%E6%8C%87%E5%8D%97#routing-canvas)

Keep the four feature descriptions focused on user actions. Preserve placeholders and the pairing between `useCaseNames` and `useCaseActions`. Run the generation and `--check` commands above with Node.js 24. Commit both source files and generated Markdown. The read-only check ensures consistency for all ten outputs. Review facts and translation quality separately.
