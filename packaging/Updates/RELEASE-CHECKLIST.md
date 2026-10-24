# SonicRoute 发布操作清单

本文用于便携版正式发布。Store 版本有独立流程，须按 [`packaging/Store/README.md`](../Store/README.md) 操作；便携版通过不代表 Store 包已验证。

## 固定发布约定

- `build/Version.props` 中的 `SonicRouteVersion` 是版本号唯一来源。发布前将它更新为目标稳定版本（如 `1.26`），不要在命令或资产名中另设版本号。
- Lite 全矩阵使用固定的稳定 .NET 10 SDK，由仓库 `global.json` 约束。目标仍包括 .NET 10 与 .NET 8 Lite；.NET 8 是目标运行时兼容版，不改变构建 SDK 选择。Legacy 仍为 net48 x64。
- 五个包、七个 Release 资产及现有文件名保持不变：四个 Lite EXE、一个 Legacy ZIP、`SonicRoute-update.json`、`SHA256SUMS.txt`。
- 日常开发 `dotnet build` 与正式候选发布分开。普通编译成功不等于候选包已验证。
- 每个候选使用新的、绝对路径输出目录。脚本不会清理或覆盖旧目录；失败后保留目录供检查，修正后换新目录重建。
- 失败时保留候选和远端 Draft。重试必须使用同一完整 commit 和同一候选资产；不要覆盖已发布 Release。已发布版本不能复用或改写。

## 生成正式候选

**输入**：目标版本已写入 `Version.props`；工作树干净；已检出的完整 commit；可用的稳定 .NET 10 SDK；未被占用的新绝对输出目录。

```powershell
$candidate = 'E:\releases\SonicRoute-v1.26-candidate-<commit>'
.\packaging\Updates\Build-PortableRelease.ps1 -OutputDirectory $candidate
```

可用 `-DotnetPath` 显式指定稳定 .NET 10 SDK 的 `dotnet.exe`。构建脚本进行一次完整 solution Release build，然后构建便携矩阵并生成五个包及两份元数据，共七项资产。它还会在候选根目录（`assets` 之外）写入 `build-receipt.json`，记录 SHA、完整 tree、工作树 clean 状态、solution build 通过状态和七项资产 hash。保留该收据和资产目录作为一组。

**成功检查**：命令成功退出；收据对应当前完整 commit 且工作树 clean、solution build 通过；候选目录含七项预期资产及现有文件名。任一条件不满足即停止，不进入发布。

常见失败：SDK 版本与 `global.json` 不符、编译失败、工作树非 clean、输出目录已存在、资产缺失或收据与当前 commit 不匹配。修复原因后使用新的输出目录重新生成，勿手工改包或收据。

## 本地校验与 Dry Run

先只检查候选资产：

```powershell
.\packaging\Updates\Invoke-ReleasePipeline.ps1 -Stage VerifyLocal -OutputDirectory $candidate
```

该阶段只在本机验证候选中的文件、清单及 SHA，不发布到 GitHub。校验失败时按错误定位候选问题，修复后重新构建并重新校验。

正式候选完整 Dry Run：

```powershell
.\packaging\Updates\Invoke-ReleasePipeline.ps1 -Stage FullPipeline -SkipBuild -DryRun -OutputDirectory $candidate
```

上述带 `-SkipBuild` 的本地命令要求工作树 clean，且候选收据绑定当前完整 commit；它复用已经构建和本地验证的候选，不重新构建或上传。Actions 的 Dry Run 则会先实际构建五包，再运行该命令。当前版本已经发布时，DryRun 允许在更新的源码上只读复验，汇总明确标记 `DRY_RUN_REVALIDATED_PUBLISHED_VERSION`；这不授权替换已经发布的标签、文件或版本，低于最新版本的请求仍拒绝。检查输出中的预检、本地资产验证、变更说明生成和汇总状态均通过。

发布说明必须是有效 UTF-8，并与当前源码中同版本的人工维护 Markdown 一致；晋级前还会核对 GitHub 草稿的实际正文。不可只比较上传正文与生成文件、遗漏生成阶段已经发生的乱码。失败时保留候选，先解决具体原因；改变源码或版本后须用新候选目录重新走全流程。

新版本正文以 [v1.27](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.27) 为排版参考：标题/日期/核心主题 → 核心更新亮点 → 按功能分组的详细演进 → 升级与验证说明 → 资产下载矩阵 → 数据保留指引 → 相关链接。维护 `docs/release-v<version>.md`，逐项核对本版事实、下载版本和运行时要求；性能收益须有对应证据，未测场景如实说明。人工正文优先且原样发布；缺少正文时仅从本版提交生成同结构摘要，不继承旧版功能或测试结论。

## 正式发布

正式操作前确认目标版本和 commit 正确、工作树 clean、候选与收据匹配、Dry Run 通过，并具备 GitHub CLI 对目标仓库 Contents 的写权限。正式发布仅允许 `master`。

```powershell
.\packaging\Updates\Invoke-ReleasePipeline.ps1 -Stage Publish -OutputDirectory $candidate
```

`Publish` 使用已验证候选，不构建新包；先校验本地候选，再检查或恢复指向同一 commit 的 Draft，上传资产并远端复验，之后才正式发布。也可在已满足相同前置条件时使用默认 `FullPipeline` 阶段执行全流程。

若发布中断，保留远端 Draft 和本地候选，读取失败信息后用相同 commit、相同资产重试。不得删除未知 Draft 资产、替换不同 commit 的 Draft、覆盖已发布 Release，或在候选内容变化后继续沿用原收据。远端资产或最新版本指针校验失败时，暂停后续操作并先查明原因。

## CI 与权限边界

Actions 默认勾选 Dry Run。正式发布必须明确取消勾选并选择 master；策略预检在构建前执行，错误分支明确失败。CI 将只读验证与正式发布拆分为不同 job：只读验证负责一致性、Harness、构建、三运行时检查及 NET10/NET8 更新助手进程/回滚检查，不持有发布写权限；只有正式发布 job 在 master 且未选择 dry_run 时取得 Contents write 权限，并复验同一候选及收据。Checkout 不持久保存凭据。尚未实际运行的托管 CI 必须另记 NOT TESTED，不把本地 Harness 或 DryRun 通过视为托管 CI 已通过。

发布失败后使用 **Re-run failed jobs**，保留成功验证 job 的输出，发布 job 按该 job 输出的 artifact 名下载原候选。候选按验证 attempt 分名保存 14 天。**Re-run all jobs** 或重新 dispatch 会重建；不要把它们称为复用同一候选。验证中途失败时，能取得的资产、收据和检查结果进入单独的 failure artifact，仅用于诊断，不能拿它当正式候选；尚未生成有效收据的文件不得发布。

## 1.26 / 1.27 操作顺序

每个版本单独执行以下步骤，不共用或改写候选目录：

1. 更新 `Version.props` 为 `1.26`（下一次发布再更新为 `1.27`），确认准备发布的完整 commit 和干净工作树。
2. 为该版本和 commit 选一个从未存在过的绝对候选路径，例如 `E:\releases\SonicRoute-v1.26-candidate-<commit>`；执行 Build，核对七资产与目录外 receipt。
3. 对该目录执行 `VerifyLocal`，再执行完整 `FullPipeline -SkipBuild -DryRun`；通过后冻结候选内容。
4. 仅在确认 `master`、commit、候选、receipt 和 GitHub CLI 写权限无误后，对同一候选执行 `-Stage Publish`。
5. 发布完成后确认 Release 页面为正式发布状态且资产齐全。失败则保留 Draft 和候选，针对同一 commit/资产排查及重试。
6. 1.27 从新的 commit、新的版本号和全新的候选目录重新开始；不得把 1.26 候选或结果当作 1.27 验证。

上述步骤用于后续新版本；同版本已公开时，正式发布必须被拒绝。只读复验不创建新 Release，也不提审 Microsoft Store。
