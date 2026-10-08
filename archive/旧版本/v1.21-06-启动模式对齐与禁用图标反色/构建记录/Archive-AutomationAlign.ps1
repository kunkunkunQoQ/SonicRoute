$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-06-启动模式对齐与禁用图标反色'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/' + $taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
$taskSources = @($taskProjects) + @('.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md', 'README.en.md',
    'docs/automation-command-line.md', 'docs/automation-convenience.md')
$taskSources = @($taskSources | Sort-Object -Unique)
$taskNote = @"
### 2026-10-08 v1.21 启动模式对齐与禁用图标反色（阶段41，v1.21-06）

- 按用户两张标注图调整：启动程序步骤的“启动模式”下拉框从居中改为左对齐，宽260，和启动列表内容左缘对齐；随机/全部模式字段与事件写回保持。
- 规则卡片左侧动作图标：启用时Theme.Accent，禁用时Theme.AccentInverse，反色按RGB各通道255减原值计算，不是透明度变化或深浅模式切换。禁用仍只影响自动触发，手动执行不变。
- ThemeService.Apply在强调色更新后复用GetInvertedAccentBrush、冻结画刷缓存更新Theme.AccentInverse，Path沿用AutomationIcons.Create动态资源，所以改强调色不必重建规则行。App.xaml提供初始反色资源；原规则状态点的InvertAccentBrush也委托同一个反色方法，去掉重复计算和逐次画刷分配。
- 仅UI对齐及颜色调整，没有新增配置、语言键、线程、计时器或执行引擎改动；九语言仍428键。AI_CONTEXT、开发指南、本地规范/改动记录/留档同步。公开README已有功能概述仍有效，本阶段不增加首页细节。
- Lite x64单文件开发发布、Legacy x64构建、ARM64编译均成功0错误；保留既有可空性警告。没有运行界面/功能/性能测试，没有启动或退出用户程序、未读写真实规则/配置/音量。实际对齐与反色效果待用户打开新版检查。
- 阶段40备份v1.21-05-导航图标与自动化编辑布局保留；阶段41备份v1.21-06-启动模式对齐与禁用图标反色包含完整源码、开发程序、构建记录、两张用户标注图及SHA256。工作区仍1.21，Git基线077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc；未上传GitHub或发布，用户目录保持。
- 当前开发程序dist/SonicRoute-v1.21-Lite-x64-automation-align/、dist/SonicRoute-v1.21-Legacy-x64-automation-align/；用户打开新版前需退出旧实例。
"@
foreach ($taskDocument in @('AI_CONTEXT.md', 'SonicRoute源码/README.md', 'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有主规范只检查标记后追加，不输出、不复制原内容。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 启动模式对齐与禁用图标反色')) {
        [IO.File]::AppendAllText($taskPath, "`r`n`r`n" + $taskNote + "`r`n", $taskEncoding)
    }
}
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/' + $taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskRelative) -Destination $taskDestination
}
$taskArtifacts = @()
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-automation-align', 'SonicRoute-v1.21-Legacy-x64-automation-align')) {
    $taskTarget = Join-Path $taskArchive ('本地构建/' + $taskBuild)
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot ('dist/' + $taskBuild)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
        $taskArtifacts += [pscustomobject]@{Path='dist/' + $taskBuild + '/' + $taskFile.Name;Bytes=$taskFile.Length;
            Version=$taskFile.VersionInfo.FileVersion;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
    }
}
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifact-metadata.json'), ($taskArtifacts | ConvertTo-Json -Depth 4), $taskEncoding)
$taskLogs = Join-Path $taskArchive '构建记录'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
foreach ($taskFile in @('publish-lite.log','build-legacy.log','build-arm64.log','artifact-metadata.json','Archive-AutomationAlign.ps1','stage-note.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskLogs $taskFile)
}
$taskReference = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-d16c22fe-ee72-4789-a6d7-5ef9c059648a.png'
if (Test-Path -LiteralPath $taskReference) { Copy-Item -LiteralPath $taskReference -Destination (Join-Path $taskArchive '图1-启动模式对齐.png') }
$taskReference2 = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-20f01f9f-ece8-4a1d-b8e5-3c34e8dcd39b.png'
if (Test-Path -LiteralPath $taskReference2) { Copy-Item -LiteralPath $taskReference2 -Destination (Join-Path $taskArchive '图2-禁用图标反色.png') }
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), ("# $taskName`r`n`r`n" + $taskNote + "`r`n`r`nSHA256清单不包含自身；源码不含bin/obj，程序为本地开发产物，尚未运行界面验证。`r`n"), $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;
        SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=41;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-05-导航图标与自动化编辑布局';
    BaselineCommit='077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;
    RuntimeTests='not run';Files=@($taskFiles | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1} | ConvertTo-Json
