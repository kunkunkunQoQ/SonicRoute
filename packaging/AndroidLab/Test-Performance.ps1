param([Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Label,
    [string]$BaselineAppDirectory, [string]$MetricFilter)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:DOTNET_CLI_HOME = Join-Path $labRoot 'outputs\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $labRoot 'outputs\nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
. (Join-Path $PSScriptRoot '../Updates/ReleaseLayout.ps1')
$dotnetExe = Resolve-ReleaseDotnet
$project = Join-Path $repositoryRoot 'checks\AndroidPerformanceChecks\AndroidPerformanceChecks.csproj'
$output = Join-Path $labRoot ('outputs\performance-' + $Label)
if (Test-Path -LiteralPath $output) { throw 'Performance evidence already exists; choose another label.' }
New-Item -ItemType Directory -Path $output | Out-Null
$build = Join-Path $output 'build.log'
& $dotnetExe publish $project -c Release --self-contained false -p:PublishReadyToRun=false -p:UseSharedCompilation=false -o (Join-Path $output 'app') *> $build
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $build -Tail 25; throw 'Performance harness build failed.' }
if ($BaselineAppDirectory) {
    $baseline = [IO.Path]::GetFullPath($BaselineAppDirectory)
    if (!$baseline.StartsWith($labRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Baseline must be inside the isolated Lab.' }
    # The same freshly compiled harness can run the preserved baseline assemblies. Public APIs
    # are unchanged; artwork-delta support is detected through reflection for this comparison.
    foreach ($assembly in @('SonicRoute','SonicRoute.AndroidAudio','SonicRoute.Core')) {
        foreach ($extension in @('.dll','.pdb')) {
            $source = Join-Path $baseline ($assembly + $extension)
            if ($extension -eq '.dll' -and !(Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing baseline assembly: ' + $source) }
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $output ('app\' + $assembly + $extension)) -Force }
        }
    }
}
$log = Join-Path $output 'run.log'
$runArguments = @((Join-Path $output 'app\AndroidPerformanceChecks.dll'), $output, $Label)
if ($MetricFilter) { $runArguments += $MetricFilter }
& $dotnetExe @runArguments *> $log
$runExit = $LASTEXITCODE
Get-Content -LiteralPath $log
if ($runExit -ne 0) { throw 'Performance harness failed.' }
