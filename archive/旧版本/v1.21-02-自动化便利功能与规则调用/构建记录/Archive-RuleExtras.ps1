$ErrorActionPreference = 'Stop'
$taskWorkspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [System.Text.UTF8Encoding]::new($false)
$taskArchiveName = 'v1.21-02-自动化便利功能与规则调用'
$taskArchive = Join-Path $taskWorkspace ('SonicRoute源码/' + $taskArchiveName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) {
    throw '本阶段已有完整备份，保留原备份。'
}

$taskArtifacts = foreach ($taskPath in @(
    'dist/SonicRoute-v1.21-Lite-x64-rule-extras/SonicRoute.exe',
    'dist/SonicRoute-v1.21-Legacy-x64-rule-extras/SonicRoute.exe',
    'dist/SonicRoute-v1.21-Legacy-x64-rule-extras/SonicRoute.Core.dll')) {
    $taskFile = Get-Item -LiteralPath (Join-Path $taskWorkspace $taskPath)
    [pscustomobject]@{
        Path = $taskPath
        Bytes = $taskFile.Length
        FileVersion = $taskFile.VersionInfo.FileVersion
        SHA256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    }
}
[System.IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifact-metadata.json'),
    ($taskArtifacts | ConvertTo-Json -Depth 4), $taskEncoding)

$taskProjectPaths = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出项目源码。' }
$taskSourcePaths = @($taskProjectPaths) + @(
    '.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md', 'README.en.md',
    'docs/automation-command-line.md', 'docs/automation-convenience.md')
$taskSourcePaths = @($taskSourcePaths | Sort-Object -Unique)

$taskNote = @"

### 2026-10-07 v1.21 自动化便利功能与规则调用（阶段37，v1.21-02）

- 用户选择实施建议1/2/3/5/8：规则名称搜索及启用/触发筛选、复制按Id执行短命令、音量25/50/75/100%右键档位、托盘常用规则、可选失败停止；额外要求选择执行另一条规则。
- 用户明确失败停止不是必选项，默认关闭：模型StopOnFailure=false、编辑器新建/重置未勾选，旧规则缺字段也false；编辑/复制保留用户选择。关闭时继续执行，开启时跳过本规则剩余步骤，已完成操作不撤销。
- ExecuteRule=17追加到原0–16枚举，目标TargetRuleId经Clone/ToPersisted/首步镜像保存，改名不影响调用；手动调用允许禁用目标，等待完成再继续。各规则独立决定失败策略；循环、超过16层、缺失及已运行目标记调用步骤失败，不强制停止默认继续的调用方。
- 收藏存FavoriteRuleIds，按收藏顺序；托盘打开按需读取，运行项禁用，缺失项略过。搜索仅更新已加载行可见性；音量档位首次右键建项，复用原Slider.ValueChanged/防抖/反馈及共用主题菜单。无新增常驻轮询或音频采样；执行状态事件合并刷新，窗口关闭取消订阅。
- 共用结果描述与命令行JSON扩展Stopped/Skipped，保留旧退出码语义。程序/PowerShell仍只判断启动成功，不等待退出码。
- 九语言各419键，提示明确可选默认关闭。双语README增加1.21开发说明；新增docs/automation-convenience.md并解除单文件忽略，命令行文档补开发说明；当前公开正式版仍1.20。
- Lite x64单文件开发发布、Legacy x64开发构建、Lite ARM64编译均0错误；保留既有可空性警告，ARM64仅编译。未运行功能/性能测试，未启动用户程序、未修改用户配置或音量；手工验收步骤见公开开发说明。不宣称CPU/内存下降。
- 备份SonicRoute源码/$taskArchiveName：$($taskProjectPaths.Count)项目文件及$($taskSourcePaths.Count)份完整源码/文档快照、构建日志、开发产物、归档脚本和SHA256清单。原v1.21-01基线保留，本次未提交GitHub或发布，版本仍1.21。
- 开发目录：dist/SonicRoute-v1.21-Lite-x64-rule-extras/与dist/SonicRoute-v1.21-Legacy-x64-rule-extras/；Legacy需保留整目录，文件版本1.21.0.0。原有.workbuddy-ai/与动画/保持。
"@

foreach ($taskDoc in @('AI_CONTEXT.md', 'SonicRoute源码/README.md',
    'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskDocPath = Join-Path $taskWorkspace $taskDoc
    # 只在内存检查阶段标记；不输出或复制包含本地签名信息的主规范。
    if (-not [System.IO.File]::ReadAllText($taskDocPath).Contains('2026-10-07 v1.21 自动化便利功能与规则调用')) {
        [System.IO.File]::AppendAllText($taskDocPath, "`r`n" + $taskNote + "`r`n", $taskEncoding)
    }
}

New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskPath in $taskSourcePaths) {
    $taskDestination = Join-Path $taskArchive ('源码快照/' + $taskPath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskWorkspace $taskPath) -Destination $taskDestination
}

foreach ($taskBuildFolder in @('SonicRoute-v1.21-Lite-x64-rule-extras',
    'SonicRoute-v1.21-Legacy-x64-rule-extras')) {
    $taskFrom = Join-Path $taskWorkspace ('dist/' + $taskBuildFolder)
    $taskTo = Join-Path $taskArchive ('本地构建/' + $taskBuildFolder)
    New-Item -ItemType Directory -Path $taskTo -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFrom -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTo $taskFile.Name)
    }
}

$taskLogDirectory = Join-Path $taskArchive '构建记录'
New-Item -ItemType Directory -Path $taskLogDirectory -Force | Out-Null
foreach ($taskFileName in @('publish-lite.log', 'build-legacy.log', 'build-arm64.log',
    'artifact-metadata.json', 'Archive-RuleExtras.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFileName) -Destination (Join-Path $taskLogDirectory $taskFileName)
}

$taskReadme = @"
# $taskArchiveName

本地阶段37最终源码与开发产物，工作区版本1.21，日期2026-10-07。
修改前基线为v1.21-01-修复与性能优化，Git基线e61e14766a4b4d3dd3b63de601fae8637d2d32e4。

$taskNote

## 手工验收

参见源码快照/docs/automation-convenience.md。新建规则应默认关闭失败停止；用失败步骤加后续OSD对照开/关，再检查A调用B的独立失败策略。
只完成源码审查、语言JSON格式检查与三目标编译，没有运行功能或性能测试；构建成功不代表实际音频及UI流程已验收。

## 目录

- 源码快照：$($taskSourcePaths.Count)文件，含$($taskProjectPaths.Count)项目文件；保留项目结构，无bin/obj及用户配置。
- 本地构建：未签名Lite x64单文件和Legacy x64整目录开发产物；ARM64仅编译日志。
- 构建记录：三目标日志、产物哈希及本次归档脚本。
- 留档清单.json：逐文件SHA256，不包含自身。

本归档不含本地签名规范、用户规则或配置，不作为正式Release；后续上传GitHub应沿用archive/旧版本/结构，本次未上传。
"@
[System.IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), $taskReadme, $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')

$taskEntries = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{
        Path = [System.IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\', '/')
        Bytes = $taskFile.Length
        SHA256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
    }
}
$taskManifest = [ordered]@{
    Stage = 37
    Version = '1.21'
    Date = '2026-10-07'
    BaselineCommit = 'e61e14766a4b4d3dd3b63de601fae8637d2d32e4'
    ProjectFiles = $taskProjectPaths.Count
    SourceFiles = $taskSourcePaths.Count
    RuntimeTests = 'not run'
    Files = @($taskEntries | Sort-Object Path)
}
[System.IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'),
    ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive; ProjectFiles=$taskProjectPaths.Count;
    SourceFiles=$taskSourcePaths.Count; ArchivedFiles=$taskEntries.Count + 1} | ConvertTo-Json
