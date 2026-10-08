$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-10-界面内存优化与规则自动命名'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/'+$taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
$taskNote = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'stage-note.txt'))
foreach ($taskDocument in @('AI_CONTEXT.md','SonicRoute源码/README.md','SonicRoute源码/改动记录.md','SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有规范只在内存核对阶段标记并追加，不输出或复制原文。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 界面内存优化与规则自动命名')) {
        [IO.File]::AppendAllText($taskPath,"`r`n`r`n"+$taskNote+"`r`n",$taskEncoding)
    }
}
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
$taskSources = @($taskProjects) + @('.gitignore','SonicRoute.sln','LICENSE','README.md','README.en.md','docs/automation-command-line.md','docs/automation-convenience.md','docs/theme-settings.md')
$taskSources = @($taskSources | Sort-Object -Unique)
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/'+$taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskRelative) -Destination $taskDestination
}
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-ui-memory','SonicRoute-v1.21-Legacy-x64-ui-memory')) {
    $taskTarget = Join-Path $taskArchive ('本地构建/'+$taskBuild)
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot ('dist/'+$taskBuild)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
    }
}
$taskLogs = Join-Path $taskArchive '检查记录'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
foreach ($taskFileName in @('publish-lite.log','build-legacy.log','build-arm64.log','review-build-lite.log','review-build-legacy.log','stage-note.txt','language-audit.json','Audit-Stage.ps1','source-audit.json','user-data-before.json','user-data-after.json','user-data-audit.json','REPORT.md','Archive-UiMemory.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFileName) -Destination (Join-Path $taskLogs $taskFileName)
}
foreach ($taskTarget in @('lite','legacy')) {
    $taskResultPath = Join-Path $taskLogs ('results-'+$taskTarget)
    New-Item -ItemType Directory -Path $taskResultPath -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot ('results-'+$taskTarget)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskResultPath $taskFile.Name)
    }
}
$taskTest = Join-Path $taskArchive '测试'
New-Item -ItemType Directory -Path (Join-Path $taskTest 'runner') -Force | Out-Null
foreach ($taskFileName in @('Program.cs','InputPickerCheck.cs','UiMemoryCheck.cs','PerfReview.csproj')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('review/runner/'+$taskFileName)) -Destination (Join-Path $taskTest ('runner/'+$taskFileName))
}
foreach ($taskRelative in @('SonicRoute/App.xaml.cs','SonicRoute.Core/ConfigService.cs','SonicRoute.Core/AutoRuleStore.cs','SonicRoute/AutoRuleScheduler.cs','SonicRoute/L10n.cs','SonicRoute/RuleCommandLine.cs')) {
    $taskDestination = Join-Path $taskTest ('isolation-source/'+$taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('review/source/'+$taskRelative)) -Destination $taskDestination
}
foreach ($taskFileName in @('Reproduce-Checks.ps1','Capture-UserDataHashes.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFileName) -Destination (Join-Path $taskTest $taskFileName)
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'),('# '+$taskName+"`r`n`r`n"+$taskNote+"`r`n`r`n源码不含bin/obj及本地敏感规范；清单不包含自身。报告见检查记录/REPORT.md。测试/Reproduce-Checks.ps1可从源码快照复现隔离UI检查，仅运行ui-memory-check。"),$taskEncoding)
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive,$taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=46;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-09-颜色选择与统一输入框';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;Validation='Lite and Legacy each passed 34 isolated UI checks; three builds passed; no resource benchmark';Files=@($taskFiles|Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'),($taskManifest|ConvertTo-Json -Depth 6),$taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1}|ConvertTo-Json
