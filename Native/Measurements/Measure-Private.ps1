param([Parameter(Mandatory)][ValidateSet('Wpf','Native')][string]$Mode,
      [Parameter(Mandatory)][ValidateSet('Full','Lite')][string]$Edition,
      [Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$OutputDirectory,[string]$WpfHook)
$ErrorActionPreference='Stop'
$harness=Join-Path $PSScriptRoot '../../packaging/Tests/PanelChecks/Run-OutputPopupChecks.ps1'
$source=Get-Content $harness -Raw
$begin=$source.IndexOf('using System;')
$end=$source.IndexOf([char]39+'@',$begin)
if($begin -lt 0 -or $end -le $begin){throw 'Private desktop launcher source unavailable.'}
if(!('OutputPopupLauncher' -as [type])){Add-Type -TypeDefinition $source.Substring($begin,$end-$begin)}
$folder=[IO.Path]::GetFullPath($OutputDirectory)
$values=@('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'Measure-Product.ps1'),'-Mode',$Mode,'-Edition',$Edition,'-Executable',(Get-Item $Executable).FullName,'-OutputDirectory',$folder,'-Desktop','Private','-ProfilePath',(Join-Path $PSScriptRoot 'Idle-UnboundProfile.json'))
if($WpfHook){$values+=@('-WpfHook',(Get-Item $WpfHook).FullName)}
$arguments=($values|ForEach-Object{if($_ -match '["\r\n]' -or $_.EndsWith('\')){throw 'Invalid launcher argument.'};'"'+$_+'"'}) -join ' '
$status=[OutputPopupLauncher]::Run([Environment]::ProcessPath,$arguments,$PSScriptRoot,60000)
if($status -ne 0 -or !(Test-Path (Join-Path $folder 'metrics.json'))){throw "Isolated desktop measurement failed ($status)."}
