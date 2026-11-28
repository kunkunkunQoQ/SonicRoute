# Update checks: local reproduction

Run these checks on Windows from the repository root. Use a verified candidate's `assets` directory and fresh output directories. The tests use isolated temporary files and processes.

## Client runtime isolation and package staging

A 2.00 candidate contains four `net10` Full/Lite EXEs. The .NET 10 client checks verify staging for both editions and rejection of packages that do not match each client's edition or runtime. The historical `net48` check uses the same candidate to verify refusal of incompatible packages. Protocol fixtures cover the older matching behavior.

```powershell
$assets = 'E:\releases\SonicRoute-v2.00-candidate\assets'
$runRoot = 'E:\release-checks\runtime-v2.00-run-01'
$project = 'packaging/Updates/tests/RuntimeChecks/Checks.csproj'

$clients = @(
    @{ Framework = 'net10.0-windows'; Edition = 'Full'; Name = 'Full' }
    @{ Framework = 'net10.0-windows'; Edition = 'Lite'; Name = 'Lite' }
    @{ Framework = 'net48'; Edition = 'Lite'; Name = 'Legacy-client' }
)
foreach ($client in $clients) {
    $out = Join-Path $runRoot $client.Name
    if (Test-Path -LiteralPath $out) { throw "Choose a new output directory: $out" }
    dotnet run --project $project -c Release -f $client.Framework "-p:SonicRouteEdition=$($client.Edition)" "-p:ArtifactsPath=$runRoot/build/$($client.Name)" -- $assets $out
    if ($LASTEXITCODE -ne 0) { throw "RuntimeChecks failed for $($client.Name)." }
}
```

Each run writes `result.json` on success or `failure.txt` on failure. The test executables require their corresponding runtimes. A positive Legacy ZIP staging or file-transaction check needs matching historical release assets. For actual NET8 release fixtures, use the matching historical checkout and assets. [v1.26 test instructions](https://github.com/kunkunkunQoQ/SonicRoute/blob/v1.26/packaging/Updates/tests/README.md)

Append `--subset` after the output directory when testing a partial candidate assembled for local staging checks. To also exercise actual Legacy ZIP parsing, installation and rollback under net48, supply historical release assets and an older baseline ZIP:

```powershell
dotnet run --project $project -c Release -f net48 -p:SonicRouteEdition=Lite -- $assets "$runRoot/Legacy-transaction" --legacy-transaction 'E:/releases/v1.36/assets' 'E:/releases/v1.35/assets/SonicRoute-v1.35-Legacy-x64.zip'
```

## WorkerChecks

The shared CI and local entry point publishes two .NET 10 x64 single-file test fixtures and uses the corresponding Lite x64 candidate as the replacement payload.

```powershell
./packaging/Updates/tests/Run-WorkerChecks.ps1 -AssetsDirectory E:/releases/SonicRoute-v2.00-candidate/assets -OutputDirectory E:/release-checks/worker-v2.00-run-01
```

Use a fresh output directory. To select the stable .NET 10 SDK, pass `-DotnetPath`. The fixture builds use version 1.24 and the current version from `build/Version.props` to model an upgrade. They are test executables. The driver checks real child processes, update handoff and recovery in temporary directories, and makes a read-only check of GitHub's latest release. It writes `results.json` under the runtime's results directory. The workflow also runs `Native/ProductChecks/Test-ProductUpdate.ps1` against the actual Full and Lite x64 Native AOT candidates.

These checks stage ARM64 packages without running them on ARM64 hardware. Microsoft Store signing, installation, updating and packaged behavior have [separate checks](../../Store/README.md).
