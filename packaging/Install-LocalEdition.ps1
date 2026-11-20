[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReceiptPath, [string]$TargetPath,
      [switch]$Remember, [string]$InstanceId, [string]$BindingDirectory)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LocalEdition.psm1')
$receipt = Get-Content -Raw -Encoding utf8 -LiteralPath $ReceiptPath | ConvertFrom-Json
if ($receipt.Edition -cnotin @('Full','Lite') -or $receipt.Architecture -cnotin @('x64','arm64') -or
    $receipt.Framework -cne 'net10.0-windows') { throw 'Expected a portable edition build receipt.' }
if ($TargetPath) {
    $binding = New-LocalEditionBinding $TargetPath $receipt.Edition $receipt.Architecture $InstanceId
} else {
    $binding = Get-LocalEditionBinding $receipt.Edition $receipt.Architecture $BindingDirectory
    if (!$binding) { throw 'Provide the target executable for the first installation.' }
}
Install-LocalEdition $binding $receipt.Asset $receipt.SHA256 $receipt.Version
if ($Remember) { Save-LocalEditionBinding $binding $BindingDirectory }
