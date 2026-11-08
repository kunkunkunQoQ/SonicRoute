param([switch]$ClientOnly)
$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$lab = Split-Path -Parent $repo
$iconShell = Join-Path $PSHOME 'pwsh.exe'
if (!(Test-Path -LiteralPath $iconShell)) { $iconShell = Join-Path $PSHOME 'powershell.exe' }
& $iconShell -NoProfile -STA -File (Join-Path $PSScriptRoot 'Build-PhoneIcon.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Shared phone icon export failed.' }
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $lab 'outputs'))
$work = Join-Path $outputRoot ('video-build-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (!$work.StartsWith($outputRoot + '\', [System.StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $work)) {
    throw 'Native build requires a fresh Lab output directory.'
}
New-Item -ItemType Directory -Path $work | Out-Null
$letter = @('Z','Y','X','W','V','U','T') | Where-Object { !(Test-Path -LiteralPath ($_ + ':\')) -and !(Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
if (!$letter) { throw 'No free temporary drive mapping for native tools.' }
$drive = $letter + ':'
& subst.exe $drive $work
if ($LASTEXITCODE -ne 0) { throw 'Cannot map the verified Lab build directory.' }
$oldByteCode = $env:PYTHONDONTWRITEBYTECODE
try {
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $python = Join-Path $env:LOCALAPPDATA 'AndroidAudioTest\python\python.exe'
    $buildArguments=@('-B',(Join-Path $PSScriptRoot 'native\build_video.py'),($drive + '\'),$repo)
    if ($ClientOnly) { $buildArguments += '--client-only' }
    & $python @buildArguments *> (Join-Path $work 'build.log')
    $result = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $work 'build.log') -Tail 12
    if ($result -ne 0) { throw ('Native client build failed: ' + (Join-Path $work 'build.log')) }
} finally {
    $env:PYTHONDONTWRITEBYTECODE = $oldByteCode
    & subst.exe $drive /D
    # Only the mapping is removed; all physical build inputs and logs remain in the Lab.
}
