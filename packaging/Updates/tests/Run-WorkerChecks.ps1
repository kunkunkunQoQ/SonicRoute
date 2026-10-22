[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$AssetsDirectory,
      [Parameter(Mandatory=$true)][string]$OutputDirectory,
      [string]$DotnetPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../ReleaseLayout.ps1"
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh worker-check directory; previous results are retained.'}
$dotnet=Resolve-ReleaseDotnet $DotnetPath
$version=([xml][IO.File]::ReadAllText("$PSScriptRoot/../../../build/Version.props")).Project.PropertyGroup.SonicRouteVersion
$project=Join-Path $PSScriptRoot 'WorkerChecks/Checks.csproj'
New-Item -ItemType Directory -Path $output|Out-Null
foreach($tfm in @('net10.0-windows','net8.0-windows')){
    $fixture=Join-Path $output $tfm
    foreach($item in @(@{Directory='old';Version='1.24'},@{Directory='new';Version=$version})){
        $destination=Join-Path $fixture $item.Directory
        & $dotnet publish $project -c Release -f $tfm "-p:Version=$($item.Version)" -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -p:UseAppHost=true -o $destination --nologo
        if($LASTEXITCODE -ne 0){throw "Worker fixture build failed: $tfm / $($item.Directory)"}
    }
    $asset=if($tfm -eq 'net8.0-windows'){"SonicRoute-v$version-Lite-NET8-x64.exe"}else{"SonicRoute-v$version-Lite-x64.exe"}
    & (Join-Path $fixture 'new/SonicRoute.exe') (Join-Path $fixture 'results') (Join-Path $AssetsDirectory $asset) (Join-Path $fixture 'old/SonicRoute.exe') (Join-Path $fixture 'new/SonicRoute.exe')
    if($LASTEXITCODE -ne 0){throw "Worker checks failed: $tfm"}
}
