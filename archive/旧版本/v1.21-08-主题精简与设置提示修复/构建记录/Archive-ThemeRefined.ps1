$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-08-主题精简与设置提示修复'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/'+$taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
$taskNote = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'stage-note.txt'))
foreach ($taskDocument in @('AI_CONTEXT.md','SonicRoute源码/README.md','SonicRoute源码/改动记录.md','SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有规范含敏感信息，只在内存核对标记并追加本轮说明，不输出或复制原文。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 主题精简与设置提示修复')) {
        [IO.File]::AppendAllText($taskPath,"`r`n`r`n"+$taskNote+"`r`n",$taskEncoding)
    }
}
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw 'Cannot list source files.' }
$taskSources = @($taskProjects) + @('.gitignore','SonicRoute.sln','LICENSE','README.md','README.en.md','docs/automation-command-line.md','docs/automation-convenience.md','docs/theme-settings.md')
$taskSources = @($taskSources | Sort-Object -Unique)
New-Item -ItemType Directory -Path $taskArchive -Force | Out-Null
foreach ($taskRelative in $taskSources) {
    $taskDestination = Join-Path $taskArchive ('源码快照/'+$taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskDestination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot $taskRelative) -Destination $taskDestination
}
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-theme-refined','SonicRoute-v1.21-Legacy-x64-theme-refined')) {
    $taskTarget = Join-Path $taskArchive ('本地构建/'+$taskBuild)
    New-Item -ItemType Directory -Path $taskTarget -Force | Out-Null
    foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $taskRoot ('dist/'+$taskBuild)) -File) {
        Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskTarget $taskFile.Name)
    }
}
$taskLogs = Join-Path $taskArchive '构建记录'
New-Item -ItemType Directory -Path $taskLogs -Force | Out-Null
foreach ($taskName in @('publish-lite.log','build-legacy.log','build-arm64.log','stage-note.txt','language-audit.json','Archive-ThemeRefined.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskName) -Destination (Join-Path $taskLogs $taskName)
}
$taskReferenceRoot = 'C:/Users/20905/AppData/Local/Temp'
$taskReferences = @{
    'codex-clipboard-38540b7e-d7fb-41f8-8ef8-1319f54d3ab4.png'='用户参考-设备名称.png'
    'codex-clipboard-9d0c43b1-4dfe-40f0-b78a-88b8a16a9390.png'='用户参考-移除高级颜色.png'
    'codex-clipboard-3a942a0a-1811-4105-9eb4-ec21f496ea07.png'='用户参考-主题下半布局.png'
}
foreach ($taskKey in $taskReferences.Keys) {
    $taskPath = Join-Path $taskReferenceRoot $taskKey
    if (Test-Path -LiteralPath $taskPath) { Copy-Item -LiteralPath $taskPath -Destination (Join-Path $taskArchive $taskReferences[$taskKey]) }
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'),('# v1.21-08-主题精简与设置提示修复'+"`r`n`r`n"+$taskNote+"`r`n`r`n源码不含bin/obj；清单不包含自身。本阶段只编译，未重跑界面或性能测试。"),$taskEncoding)
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive,$taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=44;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-07-主题设置卡片与延时对齐';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;Validation='Compilation and static audit only; no new runtime/resource tests';Files=@($taskFiles|Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'),($taskManifest|ConvertTo-Json -Depth 6),$taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1}|ConvertTo-Json
