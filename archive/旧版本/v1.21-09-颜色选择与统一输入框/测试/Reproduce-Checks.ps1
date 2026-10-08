param([string]$SourceSnapshot = (Join-Path $PSScriptRoot '../源码快照'))
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath($SourceSnapshot)
$taskReview = Join-Path $PSScriptRoot 'review'
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'SonicRoute/SonicRoute.csproj'))) { throw '请指定含三个工程的源码快照目录。' }
$taskSources = @(& rg --files -- (Join-Path $taskSource 'SonicRoute') (Join-Path $taskSource 'SonicRoute.Core') (Join-Path $taskSource 'SonicRoute.Legacy'))
if ($LASTEXITCODE -ne 0) { throw '无法列出源码。' }
foreach ($taskFile in $taskSources) {
    $taskRelative = [IO.Path]::GetRelativePath($taskSource, $taskFile)
    $taskTarget = Join-Path $taskReview ('source/'+$taskRelative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile -Destination $taskTarget -Force
}
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'isolation-source') -Recurse -File) {
    $taskRelative = [IO.Path]::GetRelativePath((Join-Path $PSScriptRoot 'isolation-source'), $taskFile.FullName)
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskReview ('source/'+$taskRelative)) -Force
}
$taskMainPath = Join-Path $taskReview 'source/SonicRoute/MainWindow.xaml.cs'
$taskText = [IO.File]::ReadAllText($taskMainPath)
$taskProbe = @'
private void ShowToast(string text)
        {
            if (Environment.GetEnvironmentVariable("SONICROUTE_PERF_ISOLATED") == "1")
            { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data/toasts.log"), text + Environment.NewLine); return; }
'@
$taskText = [regex]::Replace($taskText, 'private void ShowToast\(string text\)\s*\{', $taskProbe)
[IO.File]::WriteAllText($taskMainPath, $taskText, [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path (Join-Path $taskReview 'runner') -Force | Out-Null
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'runner') -File) {
    Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskReview ('runner/'+$taskFile.Name)) -Force
}
$taskProject = Join-Path $taskReview 'runner/PerfReview.csproj'
$taskLite = Join-Path $taskReview 'bin-lite'
dotnet build $taskProject -c Release -f net8.0-windows10.0.19041.0 -r win-x64 --self-contained false -o $taskLite
if ($LASTEXITCODE -ne 0) { throw 'Lite检查构建失败。' }
dotnet exec --depsfile (Join-Path $taskLite 'SonicRoute.deps.json') --runtimeconfig (Join-Path $taskLite 'SonicRoute.runtimeconfig.json') (Join-Path $taskLite 'PerfReview.dll') (Join-Path $PSScriptRoot 'results-lite') input-picker-check
if ($LASTEXITCODE -ne 0) { throw 'Lite检查失败。' }
$taskLegacy = Join-Path $taskReview 'bin-legacy'
dotnet build $taskProject -c Release -f net48 -o $taskLegacy
if ($LASTEXITCODE -ne 0) { throw 'Legacy检查构建失败。' }
& (Join-Path $taskLegacy 'PerfReview.exe') (Join-Path $PSScriptRoot 'results-legacy') input-picker-check
if ($LASTEXITCODE -ne 0) { throw 'Legacy检查失败。' }
