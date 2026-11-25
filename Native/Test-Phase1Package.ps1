param([Parameter(Mandatory)][string]$TestLayout, [Parameter(Mandatory)][string]$PortableHost, [Parameter(Mandatory)][string]$ResultFile)
$ErrorActionPreference='Stop'
$testName='SonicRoute.PhaseOne.Validation'
$manifest=(Resolve-Path (Join-Path $TestLayout 'AppxManifest.xml')).Path
[xml]$doc=[IO.File]::ReadAllText($manifest)
if($doc.Package.Identity.Name -cne $testName -or $doc.Package.Identity.Publisher -cne 'CN=SonicRoute Phase One Test'){throw 'Refusing a production identity.'}
if(Get-AppxPackage -Name $testName){throw 'A test registration already exists; it will not be modified.'}
$before=[DateTime]::UtcNow.AddSeconds(-1)
try{
    Add-AppxPackage -Register $manifest
    $pkg=Get-AppxPackage -Name $testName
    if(!$pkg -or [IO.Path]::GetFullPath($pkg.InstallLocation) -ine [IO.Path]::GetFullPath($TestLayout)){throw 'Unexpected test registration location.'}
    $aumid=$pkg.PackageFamilyName+'!App'
    $appProcessId=& $PortableHost --activate-test $aumid
    if($LASTEXITCODE){throw 'Native activation manager failed.'}
    $p=Get-Process -Id ([int]$appProcessId) -ErrorAction SilentlyContinue
    if($p -and !$p.WaitForExit(25000)){throw 'Packaged test exceeded time limit.'}
    $candidates=@((Join-Path $env:LOCALAPPDATA 'SonicRoute.Phase1.TestIdentity/package-result.json'),
      (Join-Path $env:LOCALAPPDATA ('Packages/'+$pkg.PackageFamilyName+'/LocalCache/Local/SonicRoute.Phase1.TestIdentity/package-result.json')))
    $written=$candidates|Where-Object {(Test-Path -LiteralPath $_) -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -ge $before}|Select-Object -First 1
    if(!$written){throw 'No package result was written.'}
    [IO.File]::WriteAllText($ResultFile,[IO.File]::ReadAllText($written),[Text.UTF8Encoding]::new($false))
    $r=Get-Content -Raw $ResultFile|ConvertFrom-Json
    if($r.packaged -cne $aumid -or $r.failure -or $r.commandEdition -ne 'Store'){throw 'Packaged activation / startup contract failed.'}
}
finally{
    $owned=Get-AppxPackage -Name $testName
    if($owned -and [IO.Path]::GetFullPath($owned.InstallLocation) -ieq [IO.Path]::GetFullPath($TestLayout)){
        Remove-AppxPackage -Package $owned.PackageFullName
    }
}
