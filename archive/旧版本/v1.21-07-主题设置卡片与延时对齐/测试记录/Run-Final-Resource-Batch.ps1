param([string]$BatchId = ('repro-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$batchRoot = Join-Path $root "raw-results/$BatchId"
$buildRoot = Join-Path $root "bin/$BatchId"
$framework = 'net8.0-windows10.0.19041.0'
$variants = @(
    @{ Name = 'baseline'; Dir = (Join-Path $root 'baseline-harness') },
    @{ Name = 'candidate'; Dir = (Join-Path $root 'candidate-harness') }
)
$scenarios = @(
    @{ Name = 'theme'; Page = 'theme'; Rules = 0; Warmup = 10; Sample = 30; Hz = 0 },
    @{ Name = 'settings'; Page = 'settings'; Rules = 0; Warmup = 10; Sample = 30; Hz = 0 },
    @{ Name = 'automation-50-disabled'; Page = 'automation'; Rules = 50; Warmup = 10; Sample = 30; Hz = 0 },
    @{ Name = 'active-osd-15hz'; Page = 'active-osd'; Rules = 0; Warmup = 15; Sample = 60; Hz = 15 }
)

if (Test-Path -LiteralPath $batchRoot) { throw "Batch output already exists; choose a new BatchId: $batchRoot" }
if (Test-Path -LiteralPath $buildRoot) { throw "Build output already exists; choose a new BatchId: $buildRoot" }
New-Item -ItemType Directory -Path $batchRoot -Force | Out-Null
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$runnerHashes = foreach ($variant in $variants) {
    $runner = Join-Path $variant.Dir 'runner/Program.cs'
    $project = Join-Path $variant.Dir 'runner/PerfReview.csproj'
    [pscustomobject]@{
        Variant = $variant.Name
        ProgramSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $runner).Hash
        ProjectSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $project).Hash
    }
}
if (($runnerHashes.ProgramSha256 | Select-Object -Unique).Count -ne 1 -or
    ($runnerHashes.ProjectSha256 | Select-Object -Unique).Count -ne 1) {
    throw 'The baseline and candidate runner source/project hashes differ.'
}
$runnerHashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $batchRoot 'runner-hashes.json') -Encoding utf8

foreach ($variant in $variants) {
    $project = Join-Path $variant.Dir 'runner/PerfReview.csproj'
    $output = Join-Path $buildRoot $variant.Name
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $buildLog = Join-Path $batchRoot ("build-" + $variant.Name + '.log')
    Write-Output "BUILD $($variant.Name)"
    & dotnet build $project -t:Rebuild -c Release -f $framework -o $output --nologo -v minimal *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $buildLog -Tail 80
        throw "Build failed for $($variant.Name); see $buildLog"
    }
    $buildTail = Get-Content -LiteralPath $buildLog | Select-Object -Last 3
    Write-Output ("BUILT {0}: {1}" -f $variant.Name, ($buildTail -join ' | '))
}

$roundIndex = 0
foreach ($scenario in $scenarios) {
    for ($round = 1; $round -le 3; $round++) {
        $roundIndex++
        $order = if (($round % 2) -eq 1) { @('baseline', 'candidate') } else { @('candidate', 'baseline') }
        foreach ($variantName in $order) {
            $variant = $variants | Where-Object Name -eq $variantName | Select-Object -First 1
            $output = Join-Path $buildRoot $variant.Name
            $resultDir = Join-Path $batchRoot ("{0}/round-{1}/{2}" -f $scenario.Name, $round, $variant.Name)
            New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
            $runner = Join-Path $output 'PerfReview.dll'
            $deps = Join-Path $output 'SonicRoute.deps.json'
            $runtime = Join-Path $output 'SonicRoute.runtimeconfig.json'
            $runLog = Join-Path $resultDir 'console.log'
            Write-Output ("START {0} round {1}/3 {2}; warmup={3}s sample={4}s" -f $scenario.Name, $round, $variant.Name, $scenario.Warmup, $scenario.Sample)
            & dotnet exec --depsfile $deps --runtimeconfig $runtime $runner $resultDir 'resource' $scenario.Page $scenario.Rules $scenario.Warmup $scenario.Sample $scenario.Hz 'none' *> $runLog
            if ($LASTEXITCODE -ne 0) {
                Get-Content -LiteralPath $runLog -Tail 80
                throw "Runner failed: $($scenario.Name) round $round $($variant.Name); see $runLog"
            }
            $summaryPath = Join-Path $resultDir 'results.json'
            if (!(Test-Path -LiteralPath $summaryPath -PathType Leaf)) { throw "Missing result summary: $summaryPath" }
            $records = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            $summary = @($records | Where-Object Case -eq 'resource-sample' | Select-Object -Last 1)[0]
            if ($null -eq $summary -or $summary.Samples -ne $scenario.Sample) { throw "Incomplete sample: $summaryPath" }
            $loadFlag = ($summary.SystemCpuMeanPercent -gt 20) -or ($summary.SystemCpuP95Percent -gt 40)
            $status = [pscustomobject]@{
                Scenario = $scenario.Name
                Round = $round
                Variant = $variant.Name
                SampleSeconds = $summary.Samples
                MachineCpuMeanPercent = [math]::Round($summary.MachineCpuMeanPercent, 5)
                SystemCpuMeanPercent = [math]::Round($summary.SystemCpuMeanPercent, 2)
                SystemCpuP95Percent = [math]::Round($summary.SystemCpuP95Percent, 2)
                PrivateRangeMiB = ('{0:F2}..{1:F2}' -f $summary.PrivateMinMiB, $summary.PrivateMaxMiB)
                WorkingSetRangeMiB = ('{0:F2}..{1:F2}' -f $summary.WorkingSetMinMiB, $summary.WorkingSetMaxMiB)
                ManagedHeapRangeMiB = ('{0:F2}..{1:F2}' -f $summary.ManagedHeapMinMiB, $summary.ManagedHeapMaxMiB)
                ActivityActualHz = [math]::Round($summary.ActivityActualHzDuringSample, 3)
                HighExternalLoadFlag = $loadFlag
                Results = $summaryPath
            }
            $status | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $batchRoot 'progress.jsonl') -Encoding utf8
            Write-Output ("DONE {0} round {1}/3 {2}: CPU={3:F5}% machine; private={4}; workingSet={5}; managed={6}; actualHz={7:F3}; externalLoadFlag={8}" -f `
                $scenario.Name, $round, $variant.Name, $summary.MachineCpuMeanPercent, $status.PrivateRangeMiB, $status.WorkingSetRangeMiB, `
                $status.ManagedHeapRangeMiB, $summary.ActivityActualHzDuringSample, $loadFlag)
        }
        Write-Output ("PAIR COMPLETE {0} round {1}/3; pair {2}/12" -f $scenario.Name, $round, $roundIndex)
    }
}

Write-Output "FINAL BATCH COMPLETE: $batchRoot"
