param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$LogPath)
$ErrorActionPreference='Stop'
$harness=Join-Path $PSScriptRoot '../../../packaging/Tests/PanelChecks/Run-OutputPopupChecks.ps1'
$text=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $harness))
$begin=$text.IndexOf('using System;')
$end=$text.IndexOf([char]39+'@',$begin)
if($begin -lt 0 -or $end -le $begin){throw 'Missing private desktop launcher in existing test harness.'}
if(-not ('OutputPopupLauncher' -as [type])){Add-Type -TypeDefinition $text.Substring($begin,$end-$begin)}
$exe=(Resolve-Path -LiteralPath $Executable).Path
$log=[IO.Path]::GetFullPath($LogPath)
$result=[OutputPopupLauncher]::Run($exe,('"{0}"' -f $log),[IO.Path]::GetDirectoryName($exe),15000)
Get-Content -Encoding utf8 -LiteralPath $log
exit $result
