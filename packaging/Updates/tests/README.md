# Update checks: local reproduction

Run these checks on Windows with the SDKs/runtimes required by the target frameworks. Use a real release `assets` directory and fresh, separate output directories for every run. These checks exercise update validation and isolated temporary files/processes; they do not change system audio settings.

Run the commands below from the repository root and replace the example release/output paths with your own.

## RuntimeChecks

`RuntimeChecks` currently contains 23 check groups for each target: `net10.0-windows`, `net8.0-windows`, and `net48`. It reads the real manifest and package assets, stages the package for its own runtime, and rejects packages for the other runtimes. ARM64 assets are staging-validated only; the test executables and launched scenarios are x64 and do not run ARM64 binaries.

```powershell
$repo = (Get-Location).Path
$assets = 'E:\releases\SonicRoute-v1.26\assets' # real release assets
$runRoot = 'E:\release-checks\runtime-v1.26-run-01' # new, unused path
$project = Join-Path $repo 'packaging\Updates\tests\RuntimeChecks\Checks.csproj'

dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'RuntimeChecks build failed.' }

$checks = Join-Path $repo 'packaging\Updates\tests\RuntimeChecks\bin\Release'
foreach ($tfm in @('net10.0-windows', 'net8.0-windows', 'net48')) {
    $out = Join-Path $runRoot $tfm
    if (Test-Path -LiteralPath $out) { throw "Choose a new output directory: $out" }
    & (Join-Path $checks "$tfm\Checks.exe") $assets $out
    if ($LASTEXITCODE -ne 0) { throw "RuntimeChecks failed for $tfm." }
}
```

Each target writes `result.json` on success or `failure.txt` on failure under its own output directory. The framework-dependent executables require the corresponding runtime to be installed.

## WorkerChecks

CI 和本地可共用入口，自动按当前版本构建 NET10/NET8 的旧、新单文件测试替身并运行既有进程/回滚用例：

```powershell
.\packaging\Updates\tests\Run-WorkerChecks.ps1 -AssetsDirectory E:\releases\SonicRoute-v1.26\assets -OutputDirectory E:\release-checks\worker-new-run
```

输出目录必须尚不存在，可用 `-DotnetPath` 指定稳定 .NET 10 SDK。旧替身版本为 1.24、新替身版本取自 `Version.props`；它们均不是历史正式客户端。下方命令用于逐运行时手动复现。

WorkerChecks has 33 scenario groups using real temporary files and child processes plus HTTP fault injection. Publish the same fixture source twice as framework-dependent single-file executables: an old 1.24 fixture and a new 1.26 fixture. Both must have the `SonicRoute Lite NET8` assembly title. The 1.24 fixture is a test fixture; it is not an official historical client. The harness runs the new fixture as its driver and uses the real NET8 x64 candidate EXE as the replacement payload. It makes one real GitHub latest-release metadata request; the injected HTTP failure cases use the in-memory test handler.

```powershell
$repo = (Get-Location).Path
$project = Join-Path $repo 'packaging\Updates\tests\WorkerChecks\Checks.csproj'
$fixtures = 'E:\release-checks\worker-fixtures-v1.24-v1.26' # new, unused path
$oldDir = Join-Path $fixtures 'old-1.24'
$newDir = Join-Path $fixtures 'new-1.26'
$result = 'E:\release-checks\worker-v1.26-run-01' # new, unused path
$candidate = 'E:\releases\SonicRoute-v1.26\assets\SonicRoute-v1.26-Lite-NET8-x64.exe'

foreach ($path in @($fixtures, $result)) {
    if (Test-Path -LiteralPath $path) { throw "Choose a new path: $path" }
}

dotnet publish $project -c Release -f net8.0-windows -p:Version=1.24 -p:AssemblyTitle='SonicRoute Lite NET8' `
    -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -p:UseAppHost=true -o $oldDir
if ($LASTEXITCODE -ne 0) { throw 'Publishing the old fixture failed.' }

dotnet publish $project -c Release -f net8.0-windows -p:Version=1.26 -p:AssemblyTitle='SonicRoute Lite NET8' `
    -r win-x64 -p:PublishSingleFile=true -p:SelfContained=false -p:UseAppHost=true -o $newDir
if ($LASTEXITCODE -ne 0) { throw 'Publishing the new fixture failed.' }

& (Join-Path $newDir 'SonicRoute.exe') $result $candidate `
    (Join-Path $oldDir 'SonicRoute.exe') (Join-Path $newDir 'SonicRoute.exe')
if ($LASTEXITCODE -ne 0) { throw 'WorkerChecks failed.' }
```

The driver writes `results.json` into the result directory. It uses isolated test data and child processes; it does not install or launch the candidate as a normal application or alter system audio settings.

Repeat the worker check for NET10 by supplying `-f net10.0-windows -p:AssemblyTitle='SonicRoute Lite'` to both publishes, choosing different fixture/result directories and using the real NET10 x64 candidate. The linked implementation selects `CurrentRuntime` at compile time; HTTP faults exercise Protocol 2. Original Protocol 1 compatibility is covered separately by RuntimeChecks. Fixture version numbers model an upgrade and do not represent historical .NET 10 releases.

## What these checks do not establish

- They do not validate behavior on a NET8-only operating system or on a machine with the Store edition installed.
- ARM64 is not executed on native ARM64 hardware; RuntimeChecks only stages and inspects the ARM64 package.
- A passing local check is not evidence that Store packaging or Store installation was tested.
