$ErrorActionPreference = 'Stop'
$lab = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$work = Join-Path $lab ('outputs\update-check-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $work | Out-Null
$script:checks = 0
$script:unicodeName = [regex]::Unescape('\u542f\u52a8\u624b\u673a\u97f3\u9891.cmd')
function Check($value,$name) { if (!$value) {throw ('FAIL ' + $name)}; $script:checks++; Write-Output ('PASS ' + $name) }
function Fixture($name) {
    $root = Join-Path $work $name
    New-Item -ItemType Directory -Path (Join-Path $root '.pending\AndroidRuntime') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'LabData') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'package\Start-Lab.ps1') -Destination (Join-Path $root 'Start-Lab.ps1')
    [IO.File]::WriteAllText((Join-Path $root 'Start-Lab.ps1'),[IO.File]::ReadAllText((Join-Path $root 'Start-Lab.ps1')),[Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText((Join-Path $root 'SonicRoute.dll'),'old')
    [IO.File]::WriteAllText((Join-Path $root 'LabData\config.json'),'{"keep":true}')
    [IO.File]::WriteAllText((Join-Path $root 'user-note.txt'),'preserve')
    [IO.File]::WriteAllText((Join-Path $root '.pending\SonicRoute.dll'),'new')
    [IO.File]::WriteAllText((Join-Path $root '.pending\AndroidRuntime\helper.jar'),'new helper')
    [IO.File]::WriteAllText((Join-Path (Join-Path $root '.pending') $script:unicodeName),'@echo off')
    $files = @('SonicRoute.dll','AndroidRuntime\helper.jar',$script:unicodeName) | ForEach-Object {
        $file = Get-Item -LiteralPath (Join-Path $root ('.pending\' + $_))
        [pscustomobject]@{Path=$_;Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    }
    return [pscustomobject]@{Root=$root;Files=@($files)}
}
function SaveManifest($fixture) {
    $json = @{Version=1;Files=$fixture.Files}|ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $fixture.Root '.pending\update-manifest.json'),$json,[Text.UTF8Encoding]::new($false))
}
function Apply($fixture) {
    $log = Join-Path $fixture.Root 'apply.log'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture.Root 'Start-Lab.ps1') -ApplyOnly *> $log
    return $LASTEXITCODE
}
$good = Fixture 'good'; SaveManifest $good
Check ((Apply $good) -eq 0) 'pending package applies under Windows PowerShell without launching a UI'
Check ([IO.File]::ReadAllText((Join-Path $good.Root 'SonicRoute.dll')) -ceq 'new') 'program file is replaced with the checked content'
Check ([IO.File]::ReadAllText((Join-Path $good.Root $script:unicodeName)) -ceq '@echo off') 'Chinese filenames in a UTF-8 manifest without BOM apply under Windows PowerShell'
Check ([IO.File]::ReadAllText((Join-Path $good.Root 'LabData\config.json')) -ceq '{"keep":true}' -and [IO.File]::ReadAllText((Join-Path $good.Root 'user-note.txt')) -ceq 'preserve') 'application configuration and unknown user files remain untouched'
$written = (Get-Item -LiteralPath (Join-Path $good.Root 'SonicRoute.dll')).LastWriteTimeUtc
Check ((Apply $good) -eq 0 -and (Get-Item -LiteralPath (Join-Path $good.Root 'SonicRoute.dll')).LastWriteTimeUtc -eq $written) 'already-applied manifest skips program copying on the next launch'
$locked = Fixture 'locked-identical'; SaveManifest $locked
New-Item -ItemType Directory -Path (Join-Path $locked.Root 'AndroidRuntime') | Out-Null
$unchanged = Join-Path $locked.Root 'AndroidRuntime\helper.jar'
[IO.File]::WriteAllText($unchanged,'new helper')
$handle=[IO.File]::Open($unchanged,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try { Check ((Apply $locked) -eq 0) 'identical locked runtime files are reused without stopping a shared helper process' }
finally { $handle.Dispose() }
foreach ($case in @('hash','outside','config','duplicate')) {
    $fixture = Fixture $case
    switch ($case) {
        'hash' { $fixture.Files[1].SHA256 = 'BAD' }
        'outside' { $fixture.Files[0].Path = '..\outside.txt' }
        'config' { $fixture.Files[0].Path = 'LabData\config.json' }
        'duplicate' { $fixture.Files += $fixture.Files[0] }
    }
    SaveManifest $fixture
    Check ((Apply $fixture) -ne 0 -and [IO.File]::ReadAllText((Join-Path $fixture.Root 'SonicRoute.dll')) -ceq 'old') ($case + ' is rejected before any program file is overwritten')
}
foreach ($location in @('SonicRoute.exe','.pending\SonicRoute.exe','.next\SonicRoute.exe')) {
    $active = Fixture ('active-' + $location.Replace('\','-')); SaveManifest $active
    $path = Join-Path $active.Root 'Start-Lab.ps1'
    $scriptText = [IO.File]::ReadAllText($path)
    $fakePath = (Join-Path $active.Root $location).Replace("'","''")
    $prefix = "function Get-Process { [CmdletBinding()]param([string]`$Name) [pscustomobject]@{Path='$fakePath'} }`r`n"
    $scriptText = $scriptText.Insert($scriptText.IndexOf("`n") + 1, $prefix)
    [IO.File]::WriteAllText($path,$scriptText,[Text.UTF8Encoding]::new($true))
    Check ((Apply $active) -ne 0 -and [IO.File]::ReadAllText((Join-Path $active.Root 'SonicRoute.dll')) -ceq 'old') ('running instance path ' + $location + ' blocks update before copying; process fixture only')
}
$preparing = Fixture 'preparing'; SaveManifest $preparing
[IO.File]::WriteAllText((Join-Path $preparing.Root '.pending\staging-state.json'),'{"Ready":false}')
Check ((Apply $preparing) -ne 0) 'an incomplete build cannot be applied even when an old manifest exists'
foreach ($broken in @($false,$true)) {
    $dual = Fixture ('dual-' + $broken); SaveManifest $dual
    $next = Join-Path $dual.Root '.next'
    New-Item -ItemType Directory -Path (Join-Path $next 'AndroidRuntime') -Force | Out-Null
    foreach ($entry in $dual.Files) { Copy-Item -LiteralPath (Join-Path $dual.Root ('.pending\' + $entry.Path)) -Destination (Join-Path $next $entry.Path) }
    [IO.File]::WriteAllText((Join-Path $next 'SonicRoute.dll'),'newest')
    $nextFiles = foreach ($entry in $dual.Files) {
        $file = Get-Item -LiteralPath (Join-Path $next $entry.Path)
        [pscustomobject]@{Path=$entry.Path;Bytes=$file.Length;SHA256=(Get-FileHash -LiteralPath $file.FullName).Hash}
    }
    [IO.File]::WriteAllText((Join-Path $next 'update-manifest.json'),(@{Version=1;Created='2100-01-01T00:00:00Z';Files=@($nextFiles)} | ConvertTo-Json -Depth 6))
    if ($broken) {
        [IO.File]::WriteAllText((Join-Path $next 'SonicRoute.dll'),'damaged')
        Check ((Apply $dual) -ne 0 -and [IO.File]::ReadAllText((Join-Path $dual.Root 'SonicRoute.dll')) -ceq 'old') 'damaged latest slot fails closed without falling back to the older package'
    } else {
        Check ((Apply $dual) -eq 0 -and [IO.File]::ReadAllText((Join-Path $dual.Root 'SonicRoute.dll')) -ceq 'newest') 'the newer fixed staging slot is selected and applied'
        Check ((Apply $dual) -eq 0 -and [IO.File]::ReadAllText((Join-Path $dual.Root 'LabData\config.json')) -ceq '{"keep":true}') 'new slot apply remains idempotent and preserves configuration'
    }
}
@{Created=(Get-Date -Format o);Assertions=$checks;Work=$work;ApplicationsLaunched=0;ProcessesTerminated=0;RealPackageModified=$false} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'result.json') -Encoding utf8
Write-Output ('RESULT passed=' + $checks + ' FIXTURES_ONLY=true APPLICATIONS_LAUNCHED=0 ' + $work)
$global:LASTEXITCODE = 0
