param([Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'SonicRoute'
$config = Join-Path $root 'config.json'
$rules = Join-Path $root 'Automation'
$configHash = $null
if (Test-Path -LiteralPath $config -PathType Leaf) { $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash }
$folderHash = $null
$fileCount = 0
if (Test-Path -LiteralPath $rules -PathType Container) {
    $files = @(Get-ChildItem -LiteralPath $rules -File -Recurse -Force | Sort-Object FullName)
    $fileCount = $files.Count
    $parts = foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($rules, $file.FullName).Replace('\','/')
        $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $relative + "`t" + $digest
    }
    $canonical = [string]::Join("`n", [string[]]@($parts))
    $bytes = [Text.Encoding]::UTF8.GetBytes($canonical)
    $folderHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
$result = [pscustomobject]@{
    CapturedAt = (Get-Date).ToString('o')
    ConfigExists = [bool]$configHash
    ConfigSha256 = $configHash
    AutomationDirectoryExists = [bool](Test-Path -LiteralPath $rules -PathType Container)
    AutomationFileCount = $fileCount
    AutomationAggregateSha256 = $folderHash
}
$result | ConvertTo-Json | Set-Content -LiteralPath $OutputFile -Encoding utf8
$result | ConvertTo-Json