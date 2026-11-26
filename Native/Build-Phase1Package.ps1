param([Parameter(Mandatory)][string]$HostExe,[Parameter(Mandatory)][string]$AssetsDirectory,
      [Parameter(Mandatory)][string]$OutDirectory,[Parameter(Mandatory)][string]$MakeAppx,
      [switch]$TestIdentity)
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath($OutDirectory)
if(Test-Path -LiteralPath $out){throw 'Use a new directory for each candidate.'}
$layout=[IO.Directory]::CreateDirectory((Join-Path $out 'layout')).FullName
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$manifest=[IO.File]::ReadAllText((Join-Path $root 'packaging/Store/AppxManifest.xml'))
$hostVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Get-Item -LiteralPath $HostExe).FullName).FileVersion
if ($hostVersion -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Expected a four-part SonicRoute file version ending in zero.' }
$manifest.Package.Identity.SetAttribute('Version', $hostVersion)
$manifest.Package.Properties.Description='SonicRoute Native AOT phase 1 verification'
if($TestIdentity){
    $manifest.Package.Identity.SetAttribute('Name','SonicRoute.PhaseOne.Validation')
    $manifest.Package.Identity.SetAttribute('Publisher','CN=SonicRoute Phase One Test')
    $manifest.Package.Properties.DisplayName='SonicRoute Phase 1 (isolated test)'
    # Keep the real task declaration but never register networking rules for the test identity.
    $manifest.Package.RemoveChild($manifest.Package.Extensions) | Out-Null
}
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))
Copy-Item -LiteralPath $HostExe -Destination (Join-Path $layout 'SonicRoute.exe')
Copy-Item -LiteralPath $AssetsDirectory -Destination (Join-Path $layout 'Assets') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $layout
$msix=Join-Path $out 'Full-x64-phase1-unsigned.msix'
& $MakeAppx pack /d $layout /p $msix
if($LASTEXITCODE){throw 'MakeAppx pack failed'}
& $MakeAppx unpack /p $msix /d (Join-Path $out 'readback')
if($LASTEXITCODE){throw 'MakeAppx unpack failed'}
if((Get-FileHash (Join-Path $out 'readback/SonicRoute.exe')).Hash -ne (Get-FileHash $HostExe).Hash){throw 'PE readback mismatch'}
