$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskArchiveName = 'v1.21-03-步骤失败策略与筛选布局'
$taskArchive = Join-Path $taskWorkspace ('SonicRoute源码/' + $taskArchiveName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整阶段备份。' }
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
$taskSources = @($taskProjects) + @('.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md',
    'README.en.md', 'docs/automation-command-line.md', 'docs/automation-convenience.md')
$taskSources = @($taskSources | Sort-Object -Unique)

$taskNote = @"

### 2026-10-07 v1.21 步骤失败策略与筛选布局（阶段38，v1.21-03）

- 按用户澄清，把失败停止从规则级改为每个操作参数区的独立复选框，默认不勾选。勾选与否均正常执行该步骤；只有失败且该步骤勾选时，才跳过本规则后续步骤。前面未勾选步骤的失败不会提前停止，勾选步骤成功也继续执行。
- StopOnFailure移到AutoRuleStep，保存于Actions[i].StopOnFailure；Clone/ToPersisted显式复制，复制规则/步骤、排序、切换操作类型保留每步选择。AutoRule全局字段与主编辑器全局控件移除，先前开发构建顶层字段不再生效，旧步骤缺字段默认false，未自动改写用户规则。
- ExecuteRule调用步骤同样独立选择：目标失败只使当前调用步骤失败，由该步骤复选框决定是否停止调用方。目标内部各步的选择只影响目标内部后续步骤；循环/深度/单飞及延时沿用。
- 状态筛选和触发方式放到规则名搜索框右侧，用一个三列Grid排列，搜索框使用剩余宽度；沿用原过滤事件，无新增轮询或监听。
- 九语言提示改为只在本步失败时停止、默认未勾选；双语README、开发功能说明、命令行文档和AI_CONTEXT同步。阶段37历史记录与v1.21-02备份保留，当前规则级设计已由本阶段替代。
- Lite x64单文件开发发布、Legacy x64构建、ARM64编译均0错误，保留现有可空性警告；版本1.21.0.0。仅源码审查/构建，未运行功能或性能测试，未启动用户程序或修改实际音量/配置，未上传GitHub及发布。
- 留档SonicRoute源码/$taskArchiveName，含$($taskProjects.Count)项目文件/$($taskSources.Count)完整源码文档、日志、开发产物与SHA256；维护AI_CONTEXT与本地主规范/改动记录/留档。原.workbuddy-ai/与动画/保持。
- 新开发目录dist/SonicRoute-v1.21-Lite-x64-step-options/和dist/SonicRoute-v1.21-Legacy-x64-step-options/，Legacy需保留整目录。手工验收详见docs/automation-convenience.md。
"@
foreach ($taskDocument in @('AI_CONTEXT.md', 'SonicRoute源码/README.md', 'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskWorkspace $taskDocument
    # 本地主规范仅检查阶段标记并追加，不输出或归档其签名信息。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-07 v1.21 步骤失败策略与筛选布局')) {
        [IO.File]::AppendAllText($taskPath, "`r`n" + $taskNote + "`r`n", $taskEncoding)
    }
}
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/' + $taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskWorkspace $taskRelative) -Destination $taskDestination
}
$taskArtifacts = @()
foreach ($taskBuildName in @('SonicRoute-v1.21-Lite-x64-step-options', 'SonicRoute-v1.21-Legacy-x64-step-options')) {
    $taskTarget = Join-Path $taskArchive ('本地构建/' + $taskBuildName)
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskWorkspace ('dist/' + $taskBuildName)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
        $taskArtifacts += [pscustomobject]@{Path='dist/' + $taskBuildName + '/' + $taskFile.Name;
            Bytes=$taskFile.Length;FileVersion=$taskFile.VersionInfo.FileVersion;
            SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
    }
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifact-metadata.json'), ($taskArtifacts | ConvertTo-Json -Depth 4), $taskEncoding)
$taskLogs = Join-Path $taskArchive '构建记录'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
foreach ($taskName in @('publish-lite.log', 'build-legacy.log', 'build-arm64.log', 'artifact-metadata.json', 'Archive-StepOptions.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskLogs $taskName)
}
$taskReadme = "# $taskArchiveName`r`n`r`n修改前完整基线：v1.21-02-自动化便利功能与规则调用；Git HEAD：e61e14766a4b4d3dd3b63de601fae8637d2d32e4。`r`n$taskNote`r`n`r`n源码快照保留项目结构，未包含bin/obj及用户配置。本地构建是未签名开发产物；ARM64仅编译。本归档不含本地签名规范。留档清单逐文件SHA256不含清单自身。`r`n"
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), $taskReadme, $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\', '/');
        Bytes=$taskFile.Length;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=38;Version='1.21';Date='2026-10-07';BaselineArchive='v1.21-02-自动化便利功能与规则调用';
    ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;RuntimeTests='not run';Files=@($taskFiles | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count + 1} | ConvertTo-Json
