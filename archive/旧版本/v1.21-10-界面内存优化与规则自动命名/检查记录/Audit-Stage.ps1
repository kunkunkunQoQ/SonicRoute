$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskEncoding=[Text.UTF8Encoding]::new($false)
function WriteAudit($name,$value){[IO.File]::WriteAllText((Join-Path $PSScriptRoot $name),($value|ConvertTo-Json -Depth 6),$taskEncoding)}
$taskLanguages=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'SonicRoute/Resources/Lang') -Filter '*.json' -File)
$taskReferenceKeys=$null
$taskLanguageResults=foreach($taskFile in $taskLanguages){
    $taskBytes=[IO.File]::ReadAllBytes($taskFile.FullName)
    $taskLanguage=[IO.File]::ReadAllText($taskFile.FullName)|ConvertFrom-Json -AsHashtable
    $taskKeys=@($taskLanguage.Keys|Sort-Object)
    if($null -eq $taskReferenceKeys){$taskReferenceKeys=$taskKeys}
    $taskDifference=@(Compare-Object $taskReferenceKeys $taskKeys)
    $taskBom=$taskBytes[0] -eq 239 -and $taskBytes[1] -eq 187 -and $taskBytes[2] -eq 191
    if($taskDifference.Count -or $taskKeys.Count -ne 453 -or -not $taskBom -or -not $taskLanguage['Auto.DefaultNamePrefix'] -or $taskLanguage.ContainsKey('Auto.NameRequired')){throw ('语言校验失败: '+$taskFile.Name)}
    [pscustomobject]@{Language=$taskFile.BaseName;Keys=$taskKeys.Count;UTF8BOM=$taskBom;SameKeys=$true;DefaultNamePrefix=$taskLanguage['Auto.DefaultNamePrefix']}
}
WriteAudit 'language-audit.json' @($taskLanguageResults)
$taskIsolated=@('SonicRoute/App.xaml.cs','SonicRoute.Core/ConfigService.cs','SonicRoute.Core/AutoRuleStore.cs','SonicRoute/AutoRuleScheduler.cs','SonicRoute/L10n.cs','SonicRoute/RuleCommandLine.cs','SonicRoute/MainWindow.xaml.cs')
$taskProjectFiles=@(& rg --files -- SonicRoute SonicRoute.Core SonicRoute.Legacy)
if($LASTEXITCODE){throw '无法枚举源码'}
$taskCompared=0
foreach($taskFile in $taskProjectFiles){
    $taskRelative=$taskFile.Replace('\','/')
    if($taskRelative -in $taskIsolated){continue}
    if((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskRelative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot ('review/source/'+$taskRelative))).Hash){throw ('隔离副本不一致: '+$taskRelative)}
    $taskCompared++
}
$taskPreserved=@('SonicRoute/App.xaml','SonicRoute/ThemeService.cs','SonicRoute/ThemeColorPickerWindow.cs','SonicRoute/AppItem.cs','SonicRoute/QuickPanelWindow.xaml','SonicRoute/QuickPanelWindow.xaml.cs','SonicRoute/QuickPanelModernWindow.xaml','SonicRoute/QuickPanelModernWindow.xaml.cs')
foreach($taskRelative in $taskPreserved){
    $taskPrevious=Join-Path $taskRoot ('SonicRoute源码/v1.21-09-颜色选择与统一输入框/源码快照/'+$taskRelative)
    if((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskRelative)).Hash -ne (Get-FileHash -LiteralPath $taskPrevious).Hash){throw ('本次意外改动原有功能: '+$taskRelative)}
}
$taskResults=foreach($taskTarget in @('lite','legacy')){
    $taskCases=Get-Content -LiteralPath (Join-Path $PSScriptRoot ('results-'+$taskTarget+'/results.json')) -Raw|ConvertFrom-Json
    $taskChecks=@($taskCases|Where-Object Case -eq 'ui-memory-check')
    if($taskChecks.Count -ne 34 -or @($taskChecks|Where-Object Passed -ne $true).Count){throw ('检查失败: '+$taskTarget)}
    [pscustomobject]@{Target=$taskTarget;Passed=$taskChecks.Count;Failed=0}
}
$taskVersions=foreach($taskTarget in @('Lite','Legacy')){
    $taskExe=Join-Path $taskRoot ('dist/SonicRoute-v1.21-'+$taskTarget+'-x64-ui-memory/SonicRoute.exe')
    $taskVersion=(Get-Item -LiteralPath $taskExe).VersionInfo.ProductVersion
    if(-not $taskVersion.StartsWith('1.21')){throw ('程序版本不符: '+$taskTarget)}
    [pscustomobject]@{Target=$taskTarget;ProductVersion=$taskVersion}
}
WriteAudit 'source-audit.json' ([ordered]@{ProjectFiles=$taskProjectFiles.Count;MatchingReviewFiles=$taskCompared;IsolationOverrides=$taskIsolated;UnchangedFromStage45=$taskPreserved;Checks=$taskResults;BuildVersions=$taskVersions})
[pscustomobject]@{Languages=$taskLanguages.Count;Keys=453;ProjectFiles=$taskProjectFiles.Count;MatchingReviewFiles=$taskCompared;LiteChecks=34;LegacyChecks=34;UnchangedFeatures=$taskPreserved.Count}|ConvertTo-Json
