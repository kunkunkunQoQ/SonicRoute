param([ValidatePattern('^[A-Za-z0-9_-]+$')][string]$OutputName = 'SonicRoute-Android-Perf-x64', [switch]$Direct,
    [ValidateSet('.pending','.next')][string]$StagingSlot = '.pending')
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = Split-Path -Parent $repoRoot
$installed = Join-Path $labRoot ('outputs\' + $OutputName)
$package = if ($Direct) { $installed } else { Join-Path $installed $StagingSlot }
if ((Get-Item -LiteralPath $package).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging directory cannot be a link.' }
$running = @(Get-Process -Name SonicRoute -ErrorAction SilentlyContinue | Where-Object {
    try { !$_.Path -or $_.Path.StartsWith($package + '\',[StringComparison]::OrdinalIgnoreCase) } catch { $true }
})
if ($running.Count) { throw 'Cannot package over the running staging slot.' }
if (!(Test-Path -LiteralPath (Join-Path $package 'SonicRoute.exe'))) { throw 'Run Build-Lab.ps1 -Publish first.' }
foreach ($name in @('Start-Lab.ps1','启动手机音频.cmd','使用说明.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('package\' + $name)) -Destination (Join-Path $package $name) -Force
}
$startScript = Join-Path $package 'Start-Lab.ps1'
[System.IO.File]::WriteAllText($startScript, [System.IO.File]::ReadAllText($startScript), [System.Text.UTF8Encoding]::new($true))
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $package 'SONICROUTE-LICENSE.txt') -Force
$required = @('AndroidRuntime\sonicroute-catalog.jar')
foreach ($name in $required) { if (!(Test-Path -LiteralPath (Join-Path $package $name))) { throw ('Missing package input: ' + $name) } }
$videoTarget = Join-Path $package 'AndroidRuntime\VirtualDisplay'
& (Join-Path $PSScriptRoot 'Copy-VideoRuntime.ps1') -PackageDirectory $package
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.FullName -notlike (Join-Path $package 'LabData\*') -and $_.Name -notin @('update-manifest.json','update-manifest.json.tmp','applied.json','staging-state.json') })
foreach ($file in $files) {
    if (($file.Name -match '^(scrcpy\.exe|avcodec.*\.dll|avformat.*\.dll|avutil.*\.dll|swresample.*\.dll|SDL.*\.dll)$') -and ($file.DirectoryName -cne $videoTarget)) {
        throw 'Video-only dependency escaped its isolated directory.'
    }
}
$manifest = foreach ($file in $files) {
    [ordered]@{Path=$file.FullName.Substring($package.Length + 1);Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$receipt = [ordered]@{Created=(Get-Date -Format o);Build='net10.0-windows/win-x64 framework-dependent';Package=$package;Files=$manifest}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $labRoot 'outputs\package-manifest.json') -Encoding utf8
if (!$Direct) {
    $manifestTemp = Join-Path $package 'update-manifest.json.tmp'
    [ordered]@{Version=1;Created=(Get-Date -Format o);Files=$manifest} | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $manifestTemp -Encoding utf8
    Move-Item -LiteralPath $manifestTemp -Destination (Join-Path $package 'update-manifest.json') -Force
    '{"Ready":true}' | Set-Content -LiteralPath (Join-Path $package 'staging-state.json') -Encoding utf8
    # Only the launcher is replaced while the previous program is running. Program/config files stay untouched.
    foreach ($name in @('Start-Lab.ps1','启动手机音频.cmd')) {
        Copy-Item -LiteralPath (Join-Path $package $name) -Destination (Join-Path $installed $name) -Force
    }
}
Write-Output ('Ready: ' + $package)
Write-Output ('Files: ' + $files.Count + '; bytes: ' + ($files | Measure-Object -Property Length -Sum).Sum)
