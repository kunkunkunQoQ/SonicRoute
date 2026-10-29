# AI 测试执行规范

读者是执行任务的 AI。本文件定义选择、执行、判定、留证和停止条件；不是终端用户教程。轮数、预热、基线日期、复测规则和决策条件见 [BENCHMARK_POLICY.md](BENCHMARK_POLICY.md)。详细测量约定见 TESTING.md，目录 / 清理约定见 ARTIFACTS.md 与 CLEANUP.md。用户当轮明确约束优先。

## A. 必须先做的决策

1. 读工作区 AGENTS 和 AI_CONTEXT 顶部。确认实际公开业务树与当前修改，保留旧根 WIP。
2. 写出：改动功能、可失败行为、需要的证据和本次不覆盖的真实环境。没有产品变化时，不构建或运行产品全套。
3. 在以下两类测试中选择所需的一类或两类：**正确性检查**验证工具 / 既有产品行为；**性能测量**验证资源变化。前者通过不能替代后者，后者通过不能替代功能正确性。
4. 选择专项 / 轻量 / 全量。常规默认专项；轻量用于快速连通 / 冒烟；全量只在用户明确要求或改动跨越多数链路时使用。通过必要检查后停止，不按数量堆叠验证。

## B. 三种正确性检查模式（已实现）

统一入口 `Invoke-IsolatedChecks.ps1` 负责隔离构建、检查、必要报告和 finally 清理。

| 模式 | 命令 | 实际范围 / 判定 |
| --- | --- | --- |
| 功能专项 | `./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Targeted -Feature sampling,protocol` | 只跑对应工具 Case；涉及网站时选择 viewer-data 或 viewer-ui；不自动扩全套 |
| 轻量 | `./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Light` | 独立工具构建、自检、纯数据和脚本语法；不默认启动浏览器或产品 |
| 全量 | `./tools/SonicRoute.BenchmarkLab/Invoke-IsolatedChecks.ps1 -Mode Full` | 轻量全部 + 桌面 / 手机无头浏览器 + 六次自建进程 A/A 流程冒烟；不自动寻找产品或写真实音频 |

专项名称：`cpu`、`statistics`、`sampling`、`protocol`、`reports`、`sandbox`、`cleanup`、`lifecycle`、`viewer-data`、`viewer-ui`。多个名称用 PowerShell 数组，例如 `-Feature reports,viewer-ui`。未知项必须失败，不得悄悄跑默认套件。

报告 finalize 协议或报告页面变化选择 `reports,viewer-ui`；覆盖自动生成模板、只读上下文、指纹校验、场景校验、必填字段校验、未完成时 finalize 失败、已完成时生成 HTML/JSON 视图、重复 finalize 不重复记录、修订总结保留 ReviewHistory、恶意文本安全转义，以及失败时保留原判定。`viewer-data` 包含报告数据归一化与场景筛选的纯数据检查。QA 假体不得声称真实模型分析；工具 `ai-summary.json` 仅为规则摘要，不能冒充 AI 总结。

只有确需验证真实产品就绪 / 报告入口时追加 `-Target <已知测试EXE>`；脚本执行独立 SonicRoute 空闲两秒冒烟，不产生性能改善结论。全量中的 A/A 流程冒烟允许 0 或 4，但必须有六轮次记录、来源未变、无负载失败；4 保留原“不确定”含义，不能说性能通过。

`-Scope Tool|Viewer|All` 保留为兼容组件选择；不是第四种测试模式。专项从 Feature 自动确定 Scope；全量不能缩成单组件。浏览器未能完成则标未测 / 失败，不伪造截图验收。

## C. 三种性能测量模式（已实现）

先用同样参数调用 `plan`。性能入口是已构建的 `SonicRoute.BenchmarkLab.exe`，使用 `run` 或 `compare`。

| 模式 | 选择 | 默认规模 / 用途 |
| --- | --- | --- |
| `--mode targeted` | 必须指定 `--feature <名称列表>` 或 `--scenario <ID列表>` | 每侧五轮、30 秒测量、5 秒预热；只覆盖选定功能 |
| `--mode light` | 固定 idle_empty、rules_scaling_10、target_absent | 每侧一轮、3 秒测量、2 秒预热；只验证运行链路，比较不得当最终门禁 |
| `--mode full` | 12 个已实现隔离性能场景，排除 generic_process | 默认每侧七轮、60 秒测量、5 秒预热；覆盖现有自动化场景，含桌面 / 热键项目，执行需显式 allow-desktop-interaction |

`--mode` 不与旧 `--suite/--module` 混用；light/full 不再叠加临时 --scenario，避免“全量”名义下缩范围。定制场景使用 targeted。用户可以延长时长；低 CPU 正式结论先参考 A/A，并按需用 30 秒或更长窗口。

性能专项映射：

| Feature | 实际场景 | 不能声称 |
| --- | --- | --- |
| idle | idle_empty | 默认受控配置不代表任意用户配置 |
| rules | rules_scaling_1/10/100、target_absent | 不是规则正确性全覆盖 |
| process | app_lifecycle_churn | 不是所有真实应用启动性能 |
| foreground | fast_foreground_switch | 切换被系统阻止时不能判负载完成 |
| delay | long_delay_step | 必须有真实 JSONL 执行记录 |
| hotkeys | rule_save_hotkeys | 只验证保存与重载，不代表物理按键和冲突 |
| schedule | schedule_rule_churn | 更新未来计划，不代表定时到期实际触发 |
| window | window_tray_transitions | 主窗口原生可见，不代表快速面板首帧、数据就绪或关窗回收 |
| endurance | endurance_leak_trend | 短时斜率不是证明内存泄漏 |

示例仅替换目标路径和新的报告目录：

```powershell
SonicRoute.BenchmarkLab.exe plan --aa --mode targeted --feature rules,process --duration 30 --json
SonicRoute.BenchmarkLab.exe compare --aa --candidate <测试EXE> --mode light --output <新证据目录> --json
SonicRoute.BenchmarkLab.exe compare --baseline <旧EXE> --candidate <新EXE> --mode targeted --feature idle,rules --rounds 3 --duration 30 --output <新证据目录> --json
SonicRoute.BenchmarkLab.exe plan --mode full --aa --duration 30 --json
```

完整性能模式覆盖的是现有 12 个场景；当前没有真实快速面板拖拽 / 首帧、实际全局音量写入、物理输入、混合 DPI 或原生 ARM64 性能专项。不得把主窗口或假体结果代替这些功能。新功能需要新场景时先实现负载完成证据，再纳入映射。

## D. 产品各功能专项路由（复用既有检查）

以下是 AI 选检查的目录索引，不自动执行所有项目。执行前读取相应 README、Program 和 csproj；按本次改动只选需要的参数与运行时。现有项目使用产品 SDK / 运行时，不能因为工具可用 NET8 就假定产品检查也只需 NET8。先将生成输出与临时数据重定向到本次专属目录，并检查 prepare 脚本输出位置，禁止默认在旧根或冻结树中构建。

| 功能 | 现有入口 | 默认边界 / 专项参数 |
| --- | --- | --- |
| 音频路径 / 会话快照 | packaging/Tests/AudioPathChecks | 默认、--input-state、--panel-snapshot；托管假原生边界 |
| 设备通知 / 电平 / 额外面板 | packaging/Tests/MonitoringChecks | --panel-performance、--endpoint-volumes-only、--meter-only；假体 |
| 自动化 / 托盘 / 热键 | packaging/Tests/AutomationMonitoringChecks | --app-input-only、--hotkeys-only、--mute-removal-only；默认更广且会启动自建子进程，音频写入是假体 |
| WPF 面板 / 名称 / 应用行 | packaging/Tests/PanelChecks | 实际产品 BAML，默认不 Show / 不启动产品 App；PanelTestFramework 控制目标；选具体 UI 参数 |
| 图标与受限进程 | packaging/Tests/AppIconChecks | 自建短命进程和 DACL，不等于 UAC / 提权验收 |
| 配置 / 原子保存 / 迁移 | packaging/Tests/PersistenceChecks | 先设全新 SONICROUTE_DATA_DIRECTORY；真实隔离文件写入 |
| JSON 与本地化序列化 | packaging/Tests/SerializationChecks | 自建隔离数据，不启动产品或音频动作 |
| Legacy 启动兼容 | packaging/Tests/LegacyCompatChecks | 完整输出复制至专属目录，独立数据，再运行 checks |
| 更新安装 / 回滚 | packaging/Updates/tests | 需真实 assets；RuntimeChecks 不安装候选，WorkerChecks 有真实临时进程与默认联网，单独选择 |

“产品专项”不是新增一套重复测试。上述入口静态索引不能当作当轮已执行证据；真正运行什么，收据写什么。

## E. 不自动纳入常规模式的项目

- AudioPath Native：真实读设备并创建自己 PID 的静音 waveOut 会话，确实改该会话音量 / 静音再恢复；须明确选择，不能称零写入。
- Monitoring Native：真实音频通知 / 电平和静音 helper，读麦克风状态；受设备可用性影响。
- Panel 私有桌面 OutputPopup：显示真实面板并读真实音频，不等于物理输入 / 混合 DPI。
- Updates Worker / official-asset：含网络和真实资产依赖；未满足条件则明确未执行。
- 真实用户声音跟随、物理鼠标 / 键盘、OSD、Explorer 重启、Win10、原生 ARM64、商店安装等：没有实际操作证据就列未测。

## F. 结果、留证、清理、停止

1. 读取退出码与逐轮 Success，核对来源哈希 / 协议 / 就绪与负载完成证据。不得只看均值或网站绿色。
2. 0、1、2、3、4、130 含义遵守 TESTING.md。报告失败优先显式记录；不确定不等于零开销或通过。
3. 完成性能测量后，compare/run 自动生成 `ai-review.json` 待填写模板和 `review-context.json`。后者是只读来源文件清单，`SourceFiles` 每项为 `{Name, SHA256}`，并包含 `ReportSha256`。执行 AI 读取原始报告，并直接写入模板，不能要求用户复制材料或粘贴 / 导入总结。Schema 必须为 `BenchmarkLab.AIReview.v1`，保留模板的 `ReportSha256`，并填写 `Status: complete`、`Author: {Kind: ai, Model: <实际模型名>}`、必需且为 UTC 的 `GeneratedUtc`、中文 `Summary`、`Findings: [{Title, Detail, ScenarioId?}]`、`Limitations` 与 `Recommendations` 字符串数组。不得猜测模型名或时间；未实际完成分析时保留未完成状态。
   `SonicRoute.BenchmarkLab report --input <报告目录> --json` 可为历史报告目录准备报告视图；随后调用同一命令并加 `--finalize`。finalize 校验指纹、场景 ID 与必填字段；不完整或无效时必须失败，保留原网页与日志，不将无效的新总结作为完成结果。成功时生成包含原始数据和 AI 总结的 `report.html`、`report-view.json`，并将总结加入 `ReviewHistory`，每项保存 `Number`、`RecordedUtc`、`ReviewSha256`、`Review`；相同内容不重复记录。修改总结后再次 finalize，保留既有历史。原始测量文件不得修改，不能把规则 `ai-summary.json` 改名冒充模型总结。最终只把成品 `report.html` 的 `file` URL 或已有静态网站 URL 交给用户；不新增托管服务或轮询。
   报告专项还要验证 HTML 安全转义、finalize 失败时原判定和原始测量完整性。仅 UI 正确性测试无需重跑性能测量。
4. 按 ARTIFACTS.md 留持久证据与交付哈希；按 CLEANUP.md 在 finally 清理。收据须列模式、功能、命令 / 退出码、残留路径和 TemporaryDirectoryRemoved。
5. 必要检查通过且无新风险则停止。仅出现新失败、未解决疑点或用户明确要求基准时扩测。
6. 最终汇报：完成内容、实际检查与结果、未覆盖范围、报告 / 网站 / 包位置和清理状态。不要自动提交、推送、发布或创建后台测试任务。
