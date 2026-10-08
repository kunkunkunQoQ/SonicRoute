param([Parameter(Mandatory=$true)][ValidateRange(1,3)][int]$Round)
$ErrorActionPreference = 'Stop'
$raw = $PSScriptRoot
$perfRoot = Split-Path -Parent $raw
$variants = @{
    v120 = Join-Path $perfRoot 'baseline-harness'
    v121 = Join-Path $perfRoot 'candidate-harness'
}
$scenarioPlans = @{
    theme = @{ Page = 'theme'; Rules = 0; Warmup = 10; Sample = 30; ActivityHz = 30 }
    automation_empty = @{ Page = 'automation'; Rules = 0; Warmup = 10; Sample = 30; ActivityHz = 30 }
    automation_50_disabled = @{ Page = 'automation'; Rules = 50; Warmup = 10; Sample = 30; ActivityHz = 30 }
    active_osd_volume_30hz = @{ Page = 'active-osd'; Rules = 0; Warmup = 15; Sample = 60; ActivityHz = 30 }
}
$orders = @{
    1 = @{ Scenarios = @('theme','automation_empty','automation_50_disabled','active_osd_volume_30hz'); Versions = @('v120','v121') }
    2 = @{ Scenarios = @('active_osd_volume_30hz','automation_50_disabled','automation_empty','theme'); Versions = @('v121','v120') }
    3 = @{ Scenarios = @('automation_empty','active_osd_volume_30hz','theme','automation_50_disabled'); Versions = @('v120','v121') }
}
$plan = $orders[$Round]
foreach ($scenarioName in $plan.Scenarios) {
    $scenario = $scenarioPlans[$scenarioName]
    foreach ($variantName in $plan.Versions) {
        $variantRoot = $variants[$variantName]
        $bin = Join-Path $variantRoot 'bin\resource-final'
        $output = Join-Path $raw ("round-{0}\{1}\{2}" -f $Round,$variantName,$scenarioName)
        if (Test-Path -LiteralPath (Join-Path $output 'results.json')) { throw "Refusing to overwrite completed run: $output" }
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        $meta = [pscustomobject]@{
            Round = $Round
            Variant = $variantName
            Scenario = $scenarioName
            Page = $scenario.Page
            DisabledFixtureRules = $scenario.Rules
            WarmupSeconds = $scenario.Warmup
            SampleSeconds = $scenario.Sample
            RequestedActivityHz = if ($scenario.Page -eq 'active-osd') { $scenario.ActivityHz } else { 0 }
            RunnerSha256 = (Get-FileHash (Join-Path $variantRoot 'runner\Program.cs') -Algorithm SHA256).Hash
            StartedAt = (Get-Date).ToString('o')
        }
        $meta | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'run-metadata.json') -Encoding utf8
        Write-Output ("START round={0} variant={1} scenario={2} warmup={3}s sample={4}s" -f $Round,$variantName,$scenarioName,$scenario.Warmup,$scenario.Sample)
        $arguments = @(
            '--depsfile', (Join-Path $bin 'SonicRoute.deps.json'),
            '--runtimeconfig', (Join-Path $bin 'SonicRoute.runtimeconfig.json'),
            (Join-Path $bin 'PerfReview.dll'),
            $output, 'resource', $scenario.Page, [string]$scenario.Rules,
            [string]$scenario.Warmup, [string]$scenario.Sample, [string]$scenario.ActivityHz
        )
        & dotnet exec @arguments 2>&1 | Tee-Object -FilePath (Join-Path $output 'console.log')
        if ($LASTEXITCODE -ne 0) { throw "Runner failed ($LASTEXITCODE): $output" }
        $meta | Add-Member -NotePropertyName FinishedAt -NotePropertyValue (Get-Date).ToString('o')
        $meta | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'run-metadata.json') -Encoding utf8
        Write-Output ("DONE round={0} variant={1} scenario={2}" -f $Round,$variantName,$scenarioName)
    }
}
Write-Output "ROUND $Round COMPLETE"