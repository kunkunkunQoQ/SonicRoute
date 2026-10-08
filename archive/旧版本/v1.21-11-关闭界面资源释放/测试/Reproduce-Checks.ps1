param(
    [string]$SourceSnapshot=(Join-Path $PSScriptRoot '../源码快照'),
    [string]$WorkDirectory=(Join-Path ([IO.Path]::GetTempPath()) ('SonicRoute-close-'+[Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference='Stop'
$taskSource=[IO.Path]::GetFullPath($SourceSnapshot)
$taskWork=[IO.Path]::GetFullPath($WorkDirectory)
if(Test-Path -LiteralPath $taskWork){throw 'Use a new work directory to avoid stale isolation files'}
foreach($file in @(& rg --files -uu (Join-Path $taskSource 'SonicRoute') (Join-Path $taskSource 'SonicRoute.Core') (Join-Path $taskSource 'SonicRoute.Legacy') -g '!**/bin/**' -g '!**/obj/**')){
    $relative=[IO.Path]::GetRelativePath($taskSource,$file)
    $target=Join-Path $taskWork ('source/'+$relative)
    New-Item -ItemType Directory -Path (Split-Path $target) -Force|Out-Null
    Copy-Item -LiteralPath $file -Destination $target
}
if($LASTEXITCODE -ne 0){throw 'Cannot enumerate source snapshot'}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'isolation-source') -File -Recurse){
    $relative=[IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'isolation-source'),$file.FullName)
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $taskWork ('source/'+$relative)) -Force
}
New-Item -ItemType Directory -Path (Join-Path $taskWork 'runner')|Out-Null
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'runner') -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $taskWork ('runner/'+$file.Name))}
# The driver enables its own isolation flag; never run resident startup in this functional check.
$priorResident=$env:SONICROUTE_CLOSE_RESIDENT
$priorDelay=$env:SONICROUTE_CLOSE_DELAY
try{
    $env:SONICROUTE_CLOSE_RESIDENT=$null
    $env:SONICROUTE_CLOSE_DELAY=$null
    $project=Join-Path $taskWork 'runner/PerfReview.csproj'
    foreach($edition in @('lite','legacy')){
        $framework=if($edition -eq 'lite'){'net8.0-windows10.0.19041.0'}else{'net48'}
        $output=Join-Path $taskWork ('bin-'+$edition)
        $buildArgs=@('build',$project,'-c','Release','-f',$framework,'-o',$output)
        if($edition -eq 'lite'){$buildArgs+=@('-r','win-x64','--self-contained','false')}
        & dotnet @buildArgs > (Join-Path $taskWork ('build-'+$edition+'.log')) 2>&1
        if($LASTEXITCODE -ne 0){throw "Build failed; inspect $taskWork/build-$edition.log"}
        foreach($mode in @('close-check','ui-memory-check')){
            $results=Join-Path $taskWork ('results-'+$edition+'-'+$mode)
            if($edition -eq 'lite'){
                & dotnet exec --depsfile (Join-Path $output 'SonicRoute.deps.json') --runtimeconfig (Join-Path $output 'SonicRoute.runtimeconfig.json') (Join-Path $output 'PerfReview.dll') $results $mode > (Join-Path $taskWork ("run-$edition-$mode.log")) 2>&1
            }else{
                & (Join-Path $output 'PerfReview.exe') $results $mode > (Join-Path $taskWork ("run-$edition-$mode.log")) 2>&1
            }
            if($LASTEXITCODE -ne 0){throw "Check failed: $edition $mode"}
            $rows=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $results 'results.json')))
            $expected=if($mode -eq 'close-check'){30}else{34}
            $checks=@($rows|Where-Object {$_.Case -eq $mode -and $_.Passed -eq $true})
            if(@($rows|Where-Object {$_.Passed -eq $false}).Count -or $checks.Count -ne $expected){throw "Unexpected assertion results: $edition $mode"}
            Write-Output "$edition $mode : $expected checks passed"
        }
    }
}finally{
    $env:SONICROUTE_CLOSE_RESIDENT=$priorResident
    $env:SONICROUTE_CLOSE_DELAY=$priorDelay
}
Write-Output "Fresh results: $taskWork"
