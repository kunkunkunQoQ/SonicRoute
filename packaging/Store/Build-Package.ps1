param(
    [string]$PublishDirectory=(Join-Path $PSScriptRoot '../../dist/SonicRoute-v1.22-Lite-x64-event-only'),
    [string]$OutputDirectory=(Join-Path $PSScriptRoot '../../dist/msix/v1.22'),
    [string]$PackageVersion='1.22.0.0',
    [string]$SdkDirectory
)
$ErrorActionPreference='Stop'
if($PackageVersion -notmatch '^\d+\.\d+\.\d+\.0$'){throw 'Use a four-part Store version ending in .0.'}
$version=[Version]::Parse($PackageVersion)
if($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535){throw 'Package version component exceeds 65535.'}
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$exe=Join-Path $publish 'SonicRoute.exe'
if(-not (Test-Path -LiteralPath $exe)){throw 'Publish the Lite x64 single-file application first.'}
if(Test-Path -LiteralPath $output){throw 'Use a new output directory; existing packages are retained.'}
if(-not $SdkDirectory){
    $sdkRoot=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $sdk=Get-ChildItem -LiteralPath $sdkRoot -Directory|Where-Object {
        $_.Name -match '^10\.0\.\d+\.0$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64/makepri.exe')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64/makeappx.exe'))
    }|Sort-Object {[Version]$_.Name} -Descending|Select-Object -First 1
    if(-not $sdk){throw 'Windows SDK packaging tools are required.'}
    $SdkDirectory=Join-Path $sdk.FullName 'x64'
}
$makepri=Join-Path $SdkDirectory 'makepri.exe'; $makeappx=Join-Path $SdkDirectory 'makeappx.exe'
if(-not (Test-Path -LiteralPath $makepri) -or -not (Test-Path -LiteralPath $makeappx)){throw 'SDK packaging tools are missing.'}
$package=Join-Path $output 'package'
New-Item -ItemType Directory -Path $package -Force|Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $package 'SonicRoute.exe')
[xml]$manifest=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'AppxManifest.xml'))
$manifest.Package.Identity.SetAttribute('Version',$PackageVersion)
$manifestPath=Join-Path $package 'AppxManifest.xml'
$manifest.Save($manifestPath)
& (Join-Path $PSScriptRoot 'Generate-Assets.ps1') -AssetsDirectory (Join-Path $package 'Assets')
$config=Join-Path $output 'priconfig.xml'
& $makepri createconfig /cf $config /dq zh-CN > (Join-Path $output 'makepri-config.log') 2>&1
if($LASTEXITCODE -ne 0){throw 'PRI configuration failed.'}
& $makepri new /pr $package /cf $config /mn $manifestPath /of (Join-Path $package 'resources.pri') > (Join-Path $output 'makepri-build.log') 2>&1
if($LASTEXITCODE -ne 0){throw 'PRI generation failed.'}
& $makepri dump /if (Join-Path $package 'resources.pri') /of (Join-Path $output 'resources-dump.xml') > (Join-Path $output 'makepri-dump.log') 2>&1
if($LASTEXITCODE -ne 0){throw 'PRI validation dump failed.'}
$msix=Join-Path $output ('SonicRoute-v'+$PackageVersion+'-Lite-x64-unsigned.msix')
& $makeappx pack /d $package /p $msix > (Join-Path $output 'makeappx.log') 2>&1
if($LASTEXITCODE -ne 0){throw 'MSIX packing or manifest validation failed.'}
Write-Output $msix
