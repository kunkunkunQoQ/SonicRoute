param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = Split-Path -Parent $repoRoot
$videoTarget = Join-Path $PackageDirectory 'AndroidRuntime\VirtualDisplay'
New-Item -ItemType Directory -Path $videoTarget -Force | Out-Null
$videoInputs = Get-Content -LiteralPath (Join-Path $repoRoot 'SonicRoute.AndroidAudio\VirtualDisplayRuntime.files.json') -Raw | ConvertFrom-Json
foreach ($videoInput in $videoInputs) {
    if ([IO.Path]::GetFileName($videoInput.Name) -cne $videoInput.Name) { throw 'Invalid video runtime manifest path.' }
    # Standalone source handoffs carry the pinned runtime beside the other Android tools.
    $source = Join-Path $repoRoot ('AndroidRuntime\VirtualDisplay\' + $videoInput.Name)
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
        $source = Join-Path $labRoot ('android-audio-test\dist\scrcpy-runtime\' + $videoInput.Name)
        if ($videoInput.Name -in @('scrcpy.exe','scrcpy-server-video')) { $source = Join-Path $labRoot ('outputs\video-client\' + $videoInput.Name) }
        elseif ($videoInput.Name -match '^(avcodec|avformat|avutil|swresample)-\d+\.dll$') { $source = Join-Path $labRoot ('outputs\video-runtime\' + $videoInput.Name) }
    }
    if ((Get-Item -LiteralPath $source).Length -ne $videoInput.Bytes -or (Get-FileHash -LiteralPath $source).Hash -cne $videoInput.SHA256) { throw ('Video runtime differs: ' + $videoInput.Name) }
    Copy-Item -LiteralPath $source -Destination (Join-Path $videoTarget $videoInput.Name) -Force
}
foreach ($license in @('scrcpy-LICENSE.txt','SDL2-LICENSE.txt','FFmpeg-LICENSE.txt','zlib-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('video-licenses\' + $license)) -Destination $videoTarget -Force
}
