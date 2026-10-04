$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskStage = Join-Path $taskRoot 'SonicRoute源码/v1.21-01-修复与性能优化'
$taskUtf8 = [Text.UTF8Encoding]::new($false)
Set-Location -LiteralPath $taskRoot

function Write-TaskJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12) + "`n", $taskUtf8)
}

$taskFiles = @(& git ls-files -- SonicRoute SonicRoute.Core SonicRoute.Legacy) + @('SonicRoute.Core/AtomicFile.cs')
$taskFiles = @($taskFiles | Sort-Object -Unique)
if ($taskFiles.Count -ne 75) { throw "Unexpected production snapshot count: $($taskFiles.Count)" }
$taskSourceChecks = foreach ($taskRelative in $taskFiles) {
    $taskOriginal = Join-Path $taskRoot $taskRelative
    $taskCopy = Join-Path (Join-Path $taskStage '源码快照') $taskRelative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskCopy)) -Force | Out-Null
    Copy-Item -LiteralPath $taskOriginal -Destination $taskCopy -Force
    $taskHash = (Get-FileHash -LiteralPath $taskOriginal -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $taskCopy -Algorithm SHA256).Hash -ne $taskHash) { throw "Snapshot mismatch: $taskRelative" }
    [pscustomobject]@{ Path=$taskRelative; SHA256=$taskHash; Matches=$true }
}

$taskProjects = foreach ($taskRelative in @('SonicRoute/SonicRoute.csproj','SonicRoute.Core/SonicRoute.Core.csproj','SonicRoute.Legacy/SonicRoute.Legacy.csproj')) {
    [xml]$taskProject = Get-Content -LiteralPath $taskRelative -Raw
    $taskVersion = @($taskProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if ($taskVersion -ne '1.21') { throw "Unexpected version: $taskRelative $taskVersion" }
    [pscustomobject]@{ Project=$taskRelative; Version=$taskVersion }
}
[xml]$taskManifest = Get-Content -LiteralPath 'SonicRoute.Legacy/app.manifest' -Raw
if ($taskManifest.assembly.assemblyIdentity.version -ne '1.21.0.0') { throw 'Legacy manifest version mismatch' }
$taskLanguages = foreach ($taskLanguage in Get-ChildItem -LiteralPath 'SonicRoute/Resources/Lang' -Filter '*.json' -File) {
    $taskDictionary = Get-Content -LiteralPath $taskLanguage.FullName -Raw | ConvertFrom-Json -AsHashtable
    if (-not $taskDictionary['Auto.SaveFailed'] -or -not $taskDictionary['Auto.DeleteFailed']) { throw "Language keys missing: $($taskLanguage.Name)" }
    [pscustomobject]@{ Language=$taskLanguage.Name; Parsed=$true; SaveFailure=$true; DeleteFailure=$true }
}
if ($taskLanguages.Count -ne 9) { throw 'Unexpected language count' }
$taskSeamMatches = foreach ($taskRelative in $taskFiles) {
    if ($taskRelative -match '\.(cs|csproj|xaml|json)$') {
        Select-String -LiteralPath $taskRelative -Pattern 'SONICROUTE_V121|V121TestSaveCount|v121-validation-app' | ForEach-Object { "$taskRelative`:$($_.LineNumber)" }
    }
}
if (@($taskSeamMatches).Count -gt 0) { throw 'Test instrumentation leaked to production' }
$taskBaselineOsd = [IO.File]::ReadAllText((Join-Path $taskRoot 'SonicRoute源码/v1.20-35-代码审查与性能测试/源码快照/SonicRoute/OsdService.cs'))
$taskCurrentOsd = [IO.File]::ReadAllText((Join-Path $taskRoot 'SonicRoute/OsdService.cs'))
$taskOldStart = $taskBaselineOsd.IndexOf('        private void UpdateBar(double target, bool animate)')
$taskOldEnd = $taskBaselineOsd.IndexOf('        private static void SetText', $taskOldStart)
$taskNewStart = $taskCurrentOsd.IndexOf('        private void UpdateBar(double target, bool animate)')
$taskNewEnd = $taskCurrentOsd.IndexOf('#else', $taskNewStart)
if ($taskOldStart -lt 0 -or $taskOldEnd -lt 0 -or $taskNewStart -lt 0 -or $taskNewEnd -lt 0) { throw 'OSD block boundaries not found' }
$taskOldBlock = $taskBaselineOsd.Substring($taskOldStart, $taskOldEnd-$taskOldStart).Replace("`r`n","`n").Trim()
$taskLegacyBlock = $taskCurrentOsd.Substring($taskNewStart, $taskNewEnd-$taskNewStart).Replace("`r`n","`n").Trim()
if ($taskOldBlock -cne $taskLegacyBlock) { throw 'Final Legacy animation differs from baseline' }
$taskDiffOutput = @(& git diff --check 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }
$taskStatic = [pscustomobject]@{
    Date='2026-10-03'; Projects=@($taskProjects); LegacyManifest='1.21.0.0'; Languages=@($taskLanguages)
    ProductionTestSeamMatches=@($taskSeamMatches); LegacyAnimationBlockMatchesBaseline=$true
    GitDiffCheck=$true; SnapshotFileCount=$taskFiles.Count; SnapshotFiles=@($taskSourceChecks)
}
Write-TaskJson (Join-Path $PSScriptRoot 'static-validation.json') $taskStatic

$taskBuildDir = Join-Path $taskStage '本地构建'
New-Item -ItemType Directory -Path $taskBuildDir -Force | Out-Null
foreach ($taskRelative in @('Build-Development.ps1','build-lite.log','build-legacy.log','build-arm64.log','static-validation.json','Finalize-Archive.ps1','final-review.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskRelative) -Destination (Join-Path $taskBuildDir $taskRelative) -Force
}
$taskArtifacts = foreach ($taskFlavor in @('Lite-x64','Legacy-x64')) {
    $taskFolder = Join-Path $taskRoot "dist/SonicRoute-v1.21-$taskFlavor"
    $taskExe = Get-Item -LiteralPath (Join-Path $taskFolder 'SonicRoute.exe')
    if ($taskExe.VersionInfo.FileVersion -ne '1.21.0.0') { throw "Artifact version mismatch: $taskFlavor" }
    $taskArtifactFiles = foreach ($taskItem in Get-ChildItem -LiteralPath $taskFolder -File -Recurse) {
        [pscustomobject]@{ Path=[IO.Path]::GetRelativePath($taskFolder,$taskItem.FullName).Replace('\','/'); Bytes=$taskItem.Length; SHA256=(Get-FileHash -LiteralPath $taskItem.FullName -Algorithm SHA256).Hash }
    }
    [pscustomobject]@{ Flavor=$taskFlavor; Path=$taskExe.FullName; FileVersion=$taskExe.VersionInfo.FileVersion; ProductVersion=$taskExe.VersionInfo.ProductVersion; Bytes=$taskExe.Length; SHA256=(Get-FileHash -LiteralPath $taskExe.FullName -Algorithm SHA256).Hash; Files=@($taskArtifactFiles) }
}
Write-TaskJson (Join-Path $taskBuildDir '产物清单.json') @($taskArtifacts)

$taskValidation = Join-Path $taskRoot 'dist/v121-validation-agent'
$taskArchivedValidation = Join-Path $taskStage '独立验证'
New-Item -ItemType Directory -Path $taskArchivedValidation -Force | Out-Null
foreach ($taskItem in Get-ChildItem -LiteralPath $taskValidation -File -Recurse) {
    $taskRelative = [IO.Path]::GetRelativePath($taskValidation,$taskItem.FullName)
    if ($taskRelative -match '(^|[\\/])(bin|obj|accidental-root-output)([\\/]|$)') { continue }
    $taskDestination = Join-Path $taskArchivedValidation $taskRelative
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskDestination)) -Force | Out-Null
    Copy-Item -LiteralPath $taskItem.FullName -Destination $taskDestination -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $taskArchivedValidation 'results/formal6/legacy-candidate-restored-baseline-osd/results.json'))) { throw 'Final Legacy result missing from archive' }
$taskProvenance = Join-Path $taskValidation 'accidental-root-output/PROVENANCE.txt'
if (Test-Path -LiteralPath $taskProvenance) { Copy-Item -LiteralPath $taskProvenance -Destination (Join-Path $taskArchivedValidation 'ROOT-OUTPUT-PROVENANCE.txt') -Force }
$taskReceipt = foreach ($taskItem in Get-ChildItem -LiteralPath $taskStage -File -Recurse | Sort-Object FullName) {
    $taskRelative = [IO.Path]::GetRelativePath($taskStage,$taskItem.FullName).Replace('\','/')
    if ($taskRelative -eq '留档清单.json') { continue }
    [pscustomobject]@{ Path=$taskRelative; Bytes=$taskItem.Length; SHA256=(Get-FileHash -LiteralPath $taskItem.FullName -Algorithm SHA256).Hash }
}
Write-TaskJson (Join-Path $taskStage '留档清单.json') ([pscustomobject]@{ Date='2026-10-03'; Version='1.21'; BaselineCommit='a89ca5c348a3b18abeccd663f944bad2e35c7f34'; SourceFileCount=$taskFiles.Count; FileCount=@($taskReceipt).Count; Files=@($taskReceipt) })
[pscustomobject]@{ Stage=$taskStage; SnapshotFiles=$taskFiles.Count; ArchivedFiles=@($taskReceipt).Count; LegacyOriginalAnimation=$true; ProductionTestSeams=0; Versions=@($taskArtifacts | Select-Object Flavor,FileVersion,SHA256) } | ConvertTo-Json -Depth 5
