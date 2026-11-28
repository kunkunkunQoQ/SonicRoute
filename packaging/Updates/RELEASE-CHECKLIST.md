# SonicRoute 发布操作清单

本文用于 SonicRoute 2.00 及后续便携版的发布。发布工作流单独生成 Full x64 的未签名 Microsoft Store 候选。商店签名、安装验证和提交按 [Store 打包说明](../Store/README.md) 执行。

## 固定发布约定

- `build/Version.props` 中的 `SonicRouteVersion` 是版本号唯一来源。发布前将它更新为目标稳定版本（如 `2.00`），不要在命令或资产名中另设版本号。
- Full 与 Lite 通过仓库 `global.json` 选择稳定 .NET 10 SDK，使用对应架构的 MSVC 生成内置 .NET 10 Native AOT 运行时的单文件 EXE。
- 四个便携包为 Full x64、Full ARM64、Lite x64 和 Lite ARM64 EXE。再加上 `SonicRoute-update.json` 和 `SHA256SUMS.txt`，共六项公开资产。Legacy 仅匹配相应的历史发布包。构建区分见 [EDITIONS.md](../EDITIONS.md)。
- 日常开发 `dotnet build` 与正式候选发布分开。普通编译成功不等于候选包已验证。
- 仓库不跟踪 `.gitignore`。新的发行检出先执行 `Initialize-ReleaseCheckout.ps1`，只在该检出的 Git 本地排除文件中加入 `bin/`、`obj/` 和根 `build-artifacts/`；源码和其他未跟踪文件仍参与 clean 检查。Actions 两个 job 均自动执行该步骤，不上传本机排除文件。
- 每个候选使用新的、绝对路径输出目录。脚本不会清理或覆盖旧目录；失败后保留目录供检查，修正后换新目录重建。
- 失败时保留候选和远端 Draft。重试必须使用同一完整 commit 和同一候选资产；不要覆盖已发布 Release。已发布版本不能复用或改写。

## 生成正式候选

**输入**：目标版本已写入 `Version.props`；工作树干净；已检出的完整 commit；可用的稳定 .NET 10 SDK；未被占用的新绝对输出目录。

```powershell
$candidate = 'E:\releases\SonicRoute-candidate-<commit>'
.\packaging\Updates\Build-PortableRelease.ps1 -OutputDirectory $candidate
```

可用 `-DotnetPath` 显式指定稳定 .NET 10 SDK 的 `dotnet.exe`。构建脚本进行一次完整 solution Release build，然后构建便携矩阵并生成四个包及两份元数据，共六项资产。它还会在候选根目录（`assets` 之外）写入 `build-receipt.json`，记录 SHA、完整 tree、工作树 clean 状态、solution build 通过状态和六项资产的 SHA-256。保留该收据和资产目录作为一组。

**成功检查**：命令成功退出；收据对应当前完整 commit 且工作树 clean、solution build 通过；候选目录含六项预期资产及现有文件名。任一条件不满足即停止，不进入发布。

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

上述带 `-SkipBuild` 的本地命令要求工作树 clean，且候选收据绑定当前完整 commit；它复用已经构建和本地验证的候选，不重新构建或上传。Actions 的 Dry Run 则会先实际构建四个便携 EXE，再运行该命令。当前版本已经发布时，DryRun 允许在更新的源码上只读复验，汇总明确标记 `DRY_RUN_REVALIDATED_PUBLISHED_VERSION`；这不授权替换已经发布的标签、文件或版本，低于最新版本的请求仍拒绝。检查输出中的预检、本地资产验证、变更说明生成和汇总状态均通过。

发布说明必须是有效 UTF-8，并与当前源码中同版本的人工维护 Markdown 一致；晋级前还会核对 GitHub 草稿的实际正文。不可只比较上传正文与生成文件、遗漏生成阶段已经发生的乱码。自动生成时复用完整分页历史，选择低于目标版本的最高正式版本，排除草稿和预发布；git 历史读取失败直接停止，不冒充空更新。离线回退本地数字标签会明确标记远端未验证。失败时保留候选，先解决具体原因；改变源码或版本后须用新候选目录重新走全流程。

新版本正文沿用 [v1.36 发布说明](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.36) 的结构，依次提供标题与导航、更新亮点、分组详细更新、可折叠的英文说明、下载与校验表、中英文升级指引和 GPL-3.0-only 许可证。维护 `docs/release-v<version>.md`，逐项核对本版事实、资产与运行时要求。确认后才填写发布日期，性能与验证结论应附对应记录。已审阅的正文优先并原样保留，缺少正文时由本版实际提交生成，英文折叠链接到源码变更。支持安卓版配对的版本在下载表中附上安卓版发行页和官方安装说明。

## 正式发布

正式操作前确认目标版本和 commit 正确、工作树 clean、候选与收据匹配、Dry Run 通过，并具备 GitHub CLI 对目标仓库 Contents 的写权限。正式发布仅允许 `master`。

```powershell
.\packaging\Updates\Invoke-ReleasePipeline.ps1 -Stage Publish -OutputDirectory $candidate
```

`Publish` 使用已验证候选，不构建新包；先校验本地候选，再检查或恢复指向同一 commit 的 Draft，上传资产并远端复验，之后才正式发布。也可在已满足相同前置条件时使用默认 `FullPipeline` 阶段执行全流程。

若发布中断，保留远端 Draft 和本地候选，读取失败信息后用相同 commit、相同资产重试。不得删除未知 Draft 资产、替换不同 commit 的 Draft、覆盖已发布 Release，或在候选内容变化后继续沿用原收据。远端资产或最新版本指针校验失败时，暂停后续操作并先查明原因。

## CI 与权限边界

普通推送只执行只读验证。Actions 手动运行默认勾选 Dry Run，正式发布必须明确取消勾选并选择 `master`。策略预检在构建前执行，错误分支会失败。只读验证涵盖一致性、Harness、构建、客户端运行时隔离、.NET 10 包暂存、单 EXE 更新、更新助手进程和回滚，以及独立的 Full x64 未签名 Store 候选。旧客户端的协议夹具仍可用于验证兼容性。只有满足手动发布条件的发布 job 取得 Contents write 权限，并复验同一候选及收据。Checkout 不持久保存凭据。托管 CI 的结果以对应 Actions 运行记录为准。

发布失败后使用 **Re-run failed jobs**，保留成功验证 job 的输出，发布 job 按该 job 输出的 artifact ID 下载原候选，并核对归档 SHA-256。候选按验证 attempt 分名保存 14 天。**Re-run all jobs** 或重新 dispatch 会重建；不要把它们称为复用同一候选。验证中途失败时，能取得的资产、收据和检查结果进入单独的 failure artifact，仅用于诊断，不能拿它当正式候选；尚未生成有效收据的文件不得发布。

## 新版本操作顺序

每个版本单独执行以下步骤，不共用或改写候选目录：

1. 更新 `Version.props` 为拟发布的下一数值版本，确认准备发布的完整 commit 和干净工作树；不得改写已经发布的版本。
2. 为该版本和 commit 选一个从未存在过的绝对候选路径，例如 `E:\releases\SonicRoute-candidate-<commit>`；执行 Build，按当前完整矩阵核对六项资产与资产目录外的 receipt。
3. 对该目录执行 `VerifyLocal`，再执行完整 `FullPipeline -SkipBuild -DryRun`；通过后冻结候选内容。
4. 仅在确认 `master`、commit、候选、receipt 和 GitHub CLI 写权限无误后，对同一候选执行 `-Stage Publish`。
5. 发布完成后确认 Release 页面为正式发布状态且资产齐全。失败则保留 Draft 和候选，针对同一 commit/资产排查及重试。
6. 下一版本从新的 commit、新的版本号和全新的候选目录重新开始；不得沿用上一版本的候选或验证结论。

上述步骤用于后续新版本；同版本已公开时，正式发布必须被拒绝。只读复验不创建新 Release，也不提审 Microsoft Store。
