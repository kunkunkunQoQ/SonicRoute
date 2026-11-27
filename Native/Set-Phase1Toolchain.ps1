param([ValidateSet('x64','arm64')][string]$Architecture='x64', [string]$VsRoot, [string]$SdkRoot, [string]$ToolCache)
$ErrorActionPreference='Stop'
if(!$VsRoot){
    $vswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
    $VsRoot=& $vswhere -latest -all -products '*' -property installationPath
}
if(!$SdkRoot){$SdkRoot=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10'}
if(!$ToolCache){$ToolCache=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../toolchain'))}
$toolsets=Get-ChildItem "$VsRoot/VC/Tools/MSVC" -Directory | Sort-Object {[version]$_.Name} -Descending
$msvc=$toolsets | Where-Object {
    (Test-Path "$($_.FullName)/bin/Hostx64/$Architecture/link.exe") -and (Test-Path "$($_.FullName)/lib/$Architecture/libcmt.lib")
} | Select-Object -First 1
if(!$msvc){$msvc=$toolsets | Select-Object -First 1}
$kit=Get-ChildItem "$SdkRoot/Lib" -Directory | Where-Object {$_.Name -match '^\d+\.' -and (Test-Path "$($_.FullName)/um/$Architecture/kernel32.lib")} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$supportLib=$null
$compilerBin=Join-Path $msvc.FullName "bin/Hostx64/$Architecture"
$runtimeLib=Join-Path $msvc.FullName "lib/$Architecture"
if($Architecture -eq 'arm64' -and (!(Test-Path "$compilerBin/link.exe") -or !(Test-Path "$runtimeLib/libcmt.lib"))){
    if(!(Test-Path -LiteralPath $ToolCache -PathType Container)){throw 'Install the Visual Studio MSVC ARM64 tools or supply -ToolCache.'}
    $link=Get-ChildItem $ToolCache -Filter link.exe -Recurse -File | Where-Object {$_.Directory.Name -eq 'arm64' -and $_.FullName -match ([regex]::Escape($msvc.Name))} | Select-Object -First 1
    $crt=Get-ChildItem $ToolCache -Filter libcmt.lib -Recurse -File | Where-Object {$_.Directory.Name -eq 'arm64' -and $_.FullName -match ([regex]::Escape($msvc.Name))} | Select-Object -First 1
    if(!$link -or !$crt){throw 'Install MSVC ARM64 tools, or supply the matching extracted Microsoft VS packages in -ToolCache.'}
    $compilerBin=$link.DirectoryName
    $crtSupport=Get-ChildItem $ToolCache -Filter oldnames.lib -Recurse -File | Where-Object {$_.Directory.Name -eq 'arm64' -and $_.FullName -match ([regex]::Escape($msvc.Name))} | Select-Object -First 1
    if(!$crtSupport){throw 'ARM64 CRT common import libraries (oldnames.lib) are missing.'}
    $runtimeLib=$crt.DirectoryName
    $supportLib=$crtSupport.DirectoryName
}
if(!$kit -or !(Test-Path "$compilerBin/link.exe") -or !(Test-Path "$runtimeLib/libcmt.lib")){throw "Missing MSVC/SDK tools for $Architecture."}
$env:PATH="$compilerBin;$($msvc.FullName)/bin/Hostx64/x64;$SdkRoot/bin/$($kit.Name)/x64;$env:PATH"
$env:LIB="$runtimeLib;$($kit.FullName)/um/$Architecture;$($kit.FullName)/ucrt/$Architecture"
if($supportLib){$env:LIB+=";"+$supportLib}
$env:IlcUseEnvironmentalTools='true'
