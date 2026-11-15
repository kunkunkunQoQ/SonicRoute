param(
    [ValidateSet('Full', 'Lite')]
    [string]$Edition='Full',
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$PackageVersion,
    [string]$SdkDirectory,
    [string]$ManifestPath,
    [string]$IdentityName,
    [string]$Publisher,
    [string]$DisplayName
)
$ErrorActionPreference='Stop'
if(-not $PublishDirectory){
    $PublishDirectory=Join-Path $PSScriptRoot ("../../dist/publish/$Edition/win-x64")
}
if(-not $ManifestPath){
    $manifestFile=if($Edition -eq 'Lite'){'AppxManifest.Lite.xml'}else{'AppxManifest.xml'}
    $ManifestPath=Join-Path $PSScriptRoot $manifestFile
}
if(-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)){throw "Missing Store manifest: $ManifestPath"}
if(-not $PackageVersion){
    [xml]$versionProperties=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../build/Version.props'))
    $sourceVersion=[string]$versionProperties.Project.PropertyGroup.SonicRouteVersion
    if($sourceVersion -notmatch '^\d+\.\d+(\.\d+)?$'){throw 'Invalid version in build/Version.props.'}
    $sourceNumber=[Version]::Parse($sourceVersion)
    $PackageVersion='{0}.{1}.{2}.0' -f $sourceNumber.Major,$sourceNumber.Minor,[Math]::Max(0,$sourceNumber.Build)
}
if($PackageVersion -notmatch '^\d+\.\d+\.\d+\.0$'){throw 'Use a four-part Store version ending in .0.'}
$version=[Version]::Parse($PackageVersion)
if(-not $OutputDirectory){
    $directoryVersion=$version.ToString($(if($version.Build -gt 0){3}else{2}))
    $editionDirSuffix=if($Edition -eq 'Lite'){'-Lite'}else{''}
    $OutputDirectory=Join-Path $PSScriptRoot ('../../dist/msix/v'+$directoryVersion+$editionDirSuffix)
}
if($version.Major -gt 65535 -or $version.Minor -gt 65535 -or $version.Build -gt 65535){throw 'Package version component exceeds 65535.'}
$publish=[IO.Path]::GetFullPath($PublishDirectory)
$output=[IO.Path]::GetFullPath($OutputDirectory)
$exe=Join-Path $publish 'SonicRoute.exe'
if(-not (Test-Path -LiteralPath $exe)){throw "Publish the $Edition x64 single-file application first."}
if(Test-Path -LiteralPath $output){throw 'Use a new output directory; existing packages are retained.'}
# Validate the matching NET10 x64 edition (Full or Lite) and reject NET8/ARM64/version mismatches
# before creating any package files; use the same read-only identity validator as portable releases.
. "$PSScriptRoot/../Updates/ReleaseLayout.ps1"
Assert-ReleaseExecutable $exe 'x64' $Edition 'net10' $PackageVersion
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
[xml]$manifest=[IO.File]::ReadAllText([IO.Path]::GetFullPath($ManifestPath))
$manifest.Package.Identity.SetAttribute('Version',$PackageVersion)
if($IdentityName){$manifest.Package.Identity.SetAttribute('Name',$IdentityName)}
if($Publisher){$manifest.Package.Identity.SetAttribute('Publisher',$Publisher)}
if($DisplayName){
    $manifest.Package.Properties.DisplayName=$DisplayName
    $ns=New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace('uap','http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $ns.AddNamespace('desktop','http://schemas.microsoft.com/appx/manifest/desktop/windows10')
    $visual=$manifest.SelectSingleNode('//uap:VisualElements',$ns)
    if($visual){$visual.SetAttribute('DisplayName',$DisplayName)}
    $startup=$manifest.SelectSingleNode('//desktop:StartupTask',$ns)
    if($startup){$startup.SetAttribute('DisplayName',"$DisplayName (Microsoft Store)")}
}
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
$msixEditionSuffix=if($Edition -eq 'Lite'){'-Lite'}else{''}
$msix=Join-Path $output ('SonicRoute-v'+$PackageVersion+$msixEditionSuffix+'-x64-unsigned.msix')
& $makeappx pack /d $package /p $msix > (Join-Path $output 'makeappx.log') 2>&1
if($LASTEXITCODE -ne 0){throw 'MSIX packing or manifest validation failed.'}
Write-Output $msix
