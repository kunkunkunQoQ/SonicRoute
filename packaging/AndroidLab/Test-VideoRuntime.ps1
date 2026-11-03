$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$lab = Split-Path -Parent $repo
$stage = Join-Path $lab ('outputs\runtime-check-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $stage | Out-Null
$cache = Join-Path $env:LOCALAPPDATA 'AndroidAudioTest'
$tc = Join-Path $cache 'toolchain\llvm-mingw-20261020-ucrt-x86_64\bin'
$prefix = Join-Path $cache 'prefix'
foreach ($name in @('avcodec-62.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll')) {
    Copy-Item -LiteralPath (Join-Path $lab ('outputs\video-runtime\' + $name)) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $lab 'outputs\video-client\scrcpy.exe') -Destination $stage
Copy-Item -LiteralPath (Join-Path $prefix 'bin\SDL2.dll') -Destination $stage
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native\runtime_check.c') -Destination $stage
$catalog = Get-Content -LiteralPath (Join-Path $lab 'outputs\catalog.json') -Raw | ConvertFrom-Json
$icon = $catalog.Apps | Where-Object Package -EQ 'cn.wenyu.bodian' | Select-Object -First 1
[System.IO.File]::WriteAllBytes((Join-Path $stage 'icon.png'), [Convert]::FromBase64String($icon.IconPng))
$ffmpeg = Get-ChildItem -LiteralPath (Join-Path $cache 'downloads\ffmpeg') -Directory | ForEach-Object { Join-Path $_.FullName 'bin\ffmpeg.exe' } | Select-Object -First 1
$log = Join-Path $stage 'check.log'
& $ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=cyan:s=64x128:r=1' -frames:v 1 -c:v libx264 -preset ultrafast -threads 1 -f h264 (Join-Path $stage 'sample.h264') *> $log
if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare H264 fixture.' }
& $ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=cyan:s=64x128:r=1' -frames:v 1 -c:v libx265 -preset ultrafast -threads 1 -x265-params 'pools=1:frame-threads=1:log-level=error' -f hevc (Join-Path $stage 'sample.hevc') *>> $log
if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare HEVC fixture.' }
$letter = @('Z','Y','X','W','V','U','T') | Where-Object { !(Test-Path -LiteralPath ($_ + ':\')) -and !(Get-PSDrive -Name $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1
if (!$letter) { throw 'No free temporary drive mapping.' }
$drive = $letter + ':'
& subst.exe $drive $stage
if ($LASTEXITCODE -ne 0) { throw 'Cannot map the Lab check stage.' }
try {
    & (Join-Path $tc 'x86_64-w64-mingw32-clang.exe') -std=c17 -Wall -Wextra ('-I' + (Join-Path $prefix 'include')) ($drive + '\runtime_check.c') ('-L' + (Join-Path $prefix 'lib')) -lavformat -lavcodec -lavutil -o ($drive + '\runtime-check.exe') *>> $log
    if ($LASTEXITCODE -ne 0) { throw 'Runtime check compilation failed.' }
    Push-Location -LiteralPath $stage
    try {
        & ($drive + '\runtime-check.exe') *>> $log
        if ($LASTEXITCODE -ne 0) { throw 'Slim runtime did not decode its required video/icon formats.' }
        $help = & ($drive + '\scrcpy.exe') --help 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or !$help.Contains('--new-display') -or !$help.Contains('--start-app')) { throw 'Slim client DLL or option loading failed.' }
        Add-Content -LiteralPath $log 'PASS exact video client loads with the six isolated runtime files and retains display/app options'
    } finally { Pop-Location }
} finally { & subst.exe $drive /D }
Get-Content -LiteralPath $log
Write-Output ('Runtime check: ' + $log)
