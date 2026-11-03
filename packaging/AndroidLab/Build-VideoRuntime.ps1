$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$lab = Split-Path -Parent $repo
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $lab 'outputs'))
$work = Join-Path $outputRoot ('runtime-build-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (!$work.StartsWith($outputRoot + '\', [System.StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $work)) {
    throw 'Runtime build requires a fresh Lab output directory.'
}
New-Item -ItemType Directory -Path $work | Out-Null
$letter = @('Z','Y','X','W','V','U','T') | Where-Object { !(Test-Path -LiteralPath ($_ + ':\')) -and !(Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
if (!$letter) { throw 'No free temporary drive mapping for native tools.' }
$drive = $letter + ':'
& subst.exe $drive $work
if ($LASTEXITCODE -ne 0) { throw 'Cannot map the verified Lab build directory.' }
try {
    $python = Join-Path $env:LOCALAPPDATA 'AndroidAudioTest\python\python.exe'
    & $python -B (Join-Path $PSScriptRoot 'native\build_runtime.py') ($drive + '\') $repo *> (Join-Path $work 'build.log')
    $result = $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $work 'build.log') -Tail 12
    if ($result -ne 0) { throw ('Video runtime build failed: ' + (Join-Path $work 'build.log')) }
} finally {
    & subst.exe $drive /D
}
