$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding = [Text.UTF8Encoding]::new($false)
$taskName = 'v1.21-07-主题设置卡片与延时对齐'
$taskArchive = Join-Path $taskRoot ('SonicRoute源码/' + $taskName)
if (Test-Path -LiteralPath (Join-Path $taskArchive '留档清单.json')) { throw '保留已有完整备份。' }
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'dist/v121-theme-layout-perf/REPORT.md'))) { throw 'Final test report required before archiving.' }
$taskProjects = @(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
$taskSources = @($taskProjects) + @('.gitignore', 'SonicRoute.sln', 'LICENSE', 'README.md', 'README.en.md',
    'docs/automation-command-line.md', 'docs/automation-convenience.md', 'docs/theme-settings.md')
$taskSources = @($taskSources | Sort-Object -Unique)
$taskNote = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'stage-note.txt'))
if ($taskNote -match '__RESOURCE_SUMMARY__|__DATA_HASH_RESULT__') { throw 'Fill final resource and data-integrity results before archiving.' }
foreach ($taskDocument in @('AI_CONTEXT.md', 'SonicRoute源码/README.md', 'SonicRoute源码/改动记录.md', 'SonicRoute源码/留档.txt')) {
    $taskPath = Join-Path $taskRoot $taskDocument
    # 私有主规范只检查标记后追加，不输出、不复制原内容。
    if (-not [IO.File]::ReadAllText($taskPath).Contains('2026-10-08 v1.21 主题设置卡片与延时对齐')) {
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
foreach ($taskBuild in @('SonicRoute-v1.21-Lite-x64-settings-layout', 'SonicRoute-v1.21-Legacy-x64-settings-layout')) {
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
foreach ($taskFile in @('publish-lite.log','build-legacy.log','build-arm64.log','artifact-metadata.json','language-validation.json','UI-CLEANUP.md','Archive-SettingsLayout.ps1','stage-note.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination (Join-Path $taskLogs $taskFile)
}
$taskReference = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-20d9437d-caa3-4d3a-b568-e38769082c6e.png'
if (Test-Path -LiteralPath $taskReference) { Copy-Item -LiteralPath $taskReference -Destination (Join-Path $taskArchive '用户参考-主题卡片.png') }
[IO.File]::WriteAllText((Join-Path $taskArchive 'README.md'), ("# $taskName`r`n`r`n" + $taskNote + "`r`n`r`nSHA256清单不包含自身；源码不含bin/obj，程序为本地开发产物；隔离验收与资源测量范围见测试记录。`r`n"), $taskEncoding)
Copy-Item -LiteralPath (Join-Path $taskRoot 'AI_CONTEXT.md') -Destination (Join-Path $taskArchive 'AI_CONTEXT.md')
$taskTestSource = Join-Path $taskRoot 'dist/v121-theme-layout-perf'
$taskTestTarget = Join-Path $taskArchive '测试记录'
New-Item -ItemType Directory -Path $taskTestTarget -Force | Out-Null
foreach ($taskTestName in @('REPORT.md', 'Capture-UserDataHashes.ps1', 'user-data-hashes-before.json', 'user-data-hashes-after.json', 'isolation-patches.md', 'Run-Comparison.ps1', 'Run-Final-Resource-Batch.ps1')) {
    $taskTestPath = Join-Path $taskTestSource $taskTestName
    if (Test-Path -LiteralPath $taskTestPath) { Copy-Item -LiteralPath $taskTestPath -Destination (Join-Path $taskTestTarget $taskTestName) }
}
foreach ($taskTestDirectory in @('raw-results', 'reproduction')) {
    $taskTestPath = Join-Path $taskTestSource $taskTestDirectory
    if (Test-Path -LiteralPath $taskTestPath) { Copy-Item -LiteralPath $taskTestPath -Destination $taskTestTarget -Recurse }
}
$taskSettingsReference = 'C:/Users/20905/AppData/Local/Temp/codex-clipboard-5dd94d5a-4e09-425f-a585-453aa8f3b5fc.png'
if (Test-Path -LiteralPath $taskSettingsReference) { Copy-Item -LiteralPath $taskSettingsReference -Destination (Join-Path $taskArchive '用户参考-设置卡片.png') }
$taskFiles = foreach ($taskFile in Get-ChildItem -LiteralPath $taskArchive -Recurse -File) {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($taskArchive, $taskFile.FullName).Replace('\','/');Bytes=$taskFile.Length;
        SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
}
$taskManifest = [ordered]@{Stage=43;Version='1.21';Date='2026-10-08';BaselineArchive='v1.21-06-启动模式对齐与禁用图标反色';
    BaselineCommit='077b6e2261fa85c3fa1b69c2fcbfe4161b997ecc';ProjectFiles=$taskProjects.Count;SourceFiles=$taskSources.Count;
    RuntimeTests='isolated source UI; see 测试记录/REPORT.md';Files=@($taskFiles | Sort-Object Path)}
[IO.File]::WriteAllText((Join-Path $taskArchive '留档清单.json'), ($taskManifest | ConvertTo-Json -Depth 6), $taskEncoding)
[pscustomobject]@{Archive=$taskArchive;SourceFiles=$taskSources.Count;ArchivedFiles=$taskFiles.Count+1} | ConvertTo-Json
