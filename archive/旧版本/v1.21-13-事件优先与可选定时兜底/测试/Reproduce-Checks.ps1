param(
    [string]$SourceSnapshot=(Join-Path $PSScriptRoot '../源码快照'),
    [string]$WorkDirectory=(Join-Path $env:TEMP ('SonicRoute-EventChecks-'+[Guid]::NewGuid().ToString('N'))),
    [switch]$IncludeForeground
)
$ErrorActionPreference='Stop'
$taskWork=[IO.Path]::GetFullPath($WorkDirectory)
if(Test-Path -LiteralPath $taskWork){throw 'Use a new work directory.'}
New-Item -ItemType Directory -Path $taskWork -Force|Out-Null
foreach($project in @('SonicRoute','SonicRoute.Core','SonicRoute.Legacy')){
    $target=Join-Path $taskWork ('source/'+$project)
    New-Item -ItemType Directory -Path $target -Force|Out-Null
    Copy-Item -Path (Join-Path $SourceSnapshot ($project+'/*')) -Destination $target -Recurse
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'isolation-source') -Recurse -File){
    $relative=[IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'isolation-source'),$file.FullName)
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $taskWork ('source/'+$relative)) -Force
}
New-Item -ItemType Directory -Path (Join-Path $taskWork 'runner') -Force|Out-Null
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'runner') -File|ForEach-Object{
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $taskWork ('runner/'+$_.Name))
}
foreach($name in @('process-target','target')){
    $target=Join-Path $taskWork ('dist/v121-background-events/'+$name)
    New-Item -ItemType Directory -Path $target -Force|Out-Null
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $name) -File|ForEach-Object{
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $target $_.Name)
    }
}
function Retire-Fixture([string]$framework){
    $from=[IO.Path]::GetFullPath((Join-Path $taskWork ('runner/bin/Release/'+$framework+'/test-data')))
    $to=[IO.Path]::GetFullPath((Join-Path $taskWork ('retired-fixtures/'+[Guid]::NewGuid().ToString('N'))))
    if(-not $from.StartsWith($taskWork+[IO.Path]::DirectorySeparatorChar) -or -not $to.StartsWith($taskWork+[IO.Path]::DirectorySeparatorChar)){throw 'Unsafe fixture path.'}
    if(Test-Path -LiteralPath $from){
        New-Item -ItemType Directory -Path (Split-Path $to) -Force|Out-Null
        Move-Item -LiteralPath $from -Destination $to
    }
}
$oldLocation=Get-Location
try{
    Set-Location -LiteralPath $taskWork
    foreach($name in @('SONICROUTE_CLOSE_RESIDENT','SONICROUTE_CLOSE_DELAY','SONICROUTE_REAL_UI','SONICROUTE_FAST_ONLY','SONICROUTE_PERIODIC_FALLBACK','SONICROUTE_EVENT_FOREGROUND_ONLY')){
        [Environment]::SetEnvironmentVariable($name,$null)
    }
    dotnet build dist/v121-background-events/process-target/ProcessTarget.csproj -c Release -v quiet > helper-build.log 2>&1
    if($LASTEXITCODE -ne 0){throw 'Process helper build failed.'}
    if($IncludeForeground){
        dotnet build dist/v121-background-events/target/Target.csproj -c Release -v quiet > foreground-helper-build.log 2>&1
        if($LASTEXITCODE -ne 0){throw 'Foreground helper build failed.'}
    }
    foreach($framework in @('net8.0-windows10.0.19041.0','net48')){
        dotnet build runner/PerfReview.csproj -c Release ('-p:TargetFrameworks='+$framework) -v quiet > ('build-'+$framework+'.log') 2>&1
        if($LASTEXITCODE -ne 0){throw ('Driver build failed: '+$framework)}
        $exe=Join-Path $taskWork ('runner/bin/Release/'+$framework+'/PerfReview.exe')
        foreach($mode in @('close-check','ui-memory-check','mic-check','background-unit')){
            Retire-Fixture $framework
            $output=Join-Path $taskWork ('results/'+$framework+'/'+$mode)
            New-Item -ItemType Directory -Path (Split-Path $output) -Force|Out-Null
            & $exe $output $mode > ($output+'.log') 2>&1
            if($LASTEXITCODE -ne 0){throw ('Check failed: '+$framework+' / '+$mode)}
            $rows=@(Get-Content -LiteralPath (Join-Path $output 'results.json') -Raw|ConvertFrom-Json)
            if(@($rows|Where-Object {$_.Passed -eq $false -or $_.Case -eq 'error'}).Count){throw ('Failed assertion: '+$mode)}
        }
        if($IncludeForeground){
            # 显式选择后临时切换自己的无声测试窗口；完成后恢复原前台。
            $env:SONICROUTE_REAL_UI='1'
            foreach($state in @('off','on')){
                Retire-Fixture $framework
                $env:SONICROUTE_PERIODIC_FALLBACK=if($state -eq 'on'){'1'}else{$null}
                $output=Join-Path $taskWork ('results/'+$framework+'/real-ui-'+$state)
                & $exe $output background-check > ($output+'.log') 2>&1
                if($LASTEXITCODE -ne 0){throw 'Real foreground UI check failed.'}
            }
            $env:SONICROUTE_REAL_UI=$null; $env:SONICROUTE_PERIODIC_FALLBACK=$null
        }
    }
} finally { Set-Location -LiteralPath $oldLocation }
Write-Output ('Passed; isolated evidence: '+$taskWork)
