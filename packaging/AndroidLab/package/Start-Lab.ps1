param([switch]$ApplyOnly)
$ErrorActionPreference = 'Stop'
function Apply-PendingUpdate {
    $candidates = foreach ($slot in @('.pending','.next')) {
        $root = Join-Path $PSScriptRoot $slot
        if (!(Test-Path -LiteralPath $root)) { continue }
        if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '暂存更新目录包含链接，已停止更新。' }
        $state = Join-Path $root 'staging-state.json'
        if ((Test-Path -LiteralPath $state) -and !(Get-Content -LiteralPath $state -Raw -Encoding UTF8 | ConvertFrom-Json).Ready) { throw '新版正在准备，请构建完成后再打开。' }
        $path = Join-Path $root 'update-manifest.json'
        if (!(Test-Path -LiteralPath $path)) { continue }
        $candidate = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
        $created = if ($candidate.Created) { [DateTimeOffset]::Parse($candidate.Created).UtcDateTime } else { (Get-Item -LiteralPath $path).LastWriteTimeUtc }
        [pscustomobject]@{Root=$root;Created=$created}
    }
    $latest = $candidates | Sort-Object Created -Descending | Select-Object -First 1
    if (!$latest) { return }
    $pendingRoot = $latest.Root
    $manifestPath = Join-Path $pendingRoot 'update-manifest.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { return }
    if ((Get-Item -LiteralPath $pendingRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '暂存更新目录包含链接，已停止更新。' }
    $receiptPath = Join-Path $pendingRoot 'applied.json'
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $receiptPath) {
        $previous = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($previous.ManifestHash -ceq $manifestHash) { return }
    }
    $running = @(Get-Process -Name SonicRoute -ErrorAction SilentlyContinue | Where-Object {
        try { !$_.Path -or $_.Path.StartsWith($PSScriptRoot + '\',[StringComparison]::OrdinalIgnoreCase) } catch { $true }
    })
    if ($running.Count) { throw '新版已准备好。请先从托盘退出当前 SonicRoute，再从这个入口打开，配置会保留。' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or !$manifest.Files -or $manifest.Files.Count -gt 2000) { throw '更新清单无效，请重新准备更新。' }
    $entries = @(); $names = @{}
    foreach ($file in $manifest.Files) {
        $relative = [string]$file.Path
        if (!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?([\\/]|$)' -or $relative -match ':' -or
            $relative -match '^(LabData|\.pending|\.next)([\\/]|$)' -or $names.ContainsKey($relative)) { throw '更新文件路径无效。' }
        $names[$relative] = $true
        $source = [IO.Path]::GetFullPath((Join-Path $pendingRoot $relative))
        $target = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
        if (!$source.StartsWith($pendingRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($PSScriptRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw '更新文件超出程序目录。' }
        $sourceItem = Get-Item -LiteralPath $source
        if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $sourceItem.Length -ne $file.Bytes -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $file.SHA256) { throw '更新文件校验失败，请重新准备更新。' }
        $sourceParent = Split-Path -Parent $source
        while ($sourceParent.Length -gt $pendingRoot.Length) {
            if ((Get-Item -LiteralPath $sourceParent).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '暂存文件路径包含链接，已停止更新。' }
            $sourceParent = Split-Path -Parent $sourceParent
        }
        # Existing links in a destination path must never redirect writes out of this package.
        $parent = Split-Path -Parent $target
        while ($parent.Length -ge $PSScriptRoot.Length) {
            if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '程序目录包含链接，已停止更新。' }
            if ($parent -ieq $PSScriptRoot) { break }; $parent = Split-Path -Parent $parent
        }
        if ((Test-Path -LiteralPath $target) -and ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '目标文件包含链接，已停止更新。' }
        $entries += [pscustomobject]@{Source=$source;Target=$target;SHA256=$file.SHA256}
    }
    foreach ($entry in $entries) {
        if ((Test-Path -LiteralPath $entry.Target) -and (Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ceq $entry.SHA256) { continue }
        New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Target) -Force | Out-Null
        Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
        if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -cne $entry.SHA256) { throw '程序更新未完成，请退出后重试。' }
    }
    [ordered]@{Applied=(Get-Date -Format o);ManifestHash=$manifestHash;Files=$entries.Count;Preserved='LabData'} |
        ConvertTo-Json | Set-Content -LiteralPath $receiptPath -Encoding utf8
}
try { Apply-PendingUpdate }
catch {
    if ($ApplyOnly) { throw }
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show($_.Exception.Message, 'SonicRoute 手机音频') | Out-Null
    exit 1
}
if ($ApplyOnly) { return }
$candidates = @(
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
$runtimeHost = $null
foreach ($candidate in $candidates) {
    $runtimes = & $candidate --list-runtimes
    if ($LASTEXITCODE -eq 0 -and ($runtimes -match '^Microsoft.WindowsDesktop.App 10\.0\.')) { $runtimeHost = $candidate; break }
}
if (!$runtimeHost) {
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('此实验版需要 x64 的 .NET 10 Desktop Runtime。当前未找到该运行环境。', 'SonicRoute 手机音频') | Out-Null
    exit 1
}
$env:DOTNET_ROOT_X64 = Split-Path -Parent $runtimeHost
$env:DOTNET_ROOT = $env:DOTNET_ROOT_X64
$env:SONICROUTE_DATA_DIRECTORY = Join-Path $PSScriptRoot 'LabData'
$env:SONICROUTE_INSTANCE_ID = 'AndroidLabMultiApp'
Start-Process -FilePath (Join-Path $PSScriptRoot 'SonicRoute.exe') -ArgumentList '--android-lab' -WorkingDirectory $PSScriptRoot
