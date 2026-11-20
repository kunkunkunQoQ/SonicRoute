$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Updates/ReleaseLayout.ps1')
$worktree = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-LocalPathKey([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Path.ToUpperInvariant()))).Replace('-','') }
    finally { $sha.Dispose() }
}

function Get-BindingPath([string]$Edition, [string]$Architecture, [string]$BindingDirectory) {
    if (!$BindingDirectory) {
        $BindingDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SonicRoute/LocalBuilds'
    }
    $directory = Join-Path $BindingDirectory (Get-LocalPathKey $worktree)
    return Join-Path $directory "$Edition-$Architecture.json"
}

function New-LocalEditionBinding([string]$Target, [string]$Edition, [string]$Architecture, [string]$InstanceId) {
    $targetFile = Get-Item -LiteralPath $Target -ErrorAction Stop
    if ($targetFile.PSIsContainer -or $targetFile.Extension -ine '.exe') { throw 'Select an existing portable SonicRoute executable.' }
    for ($path = $targetFile; $path; $path = Get-Item -LiteralPath $parent) {
        if ($path.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Local update target contains a link: $($path.FullName)" }
        $parent = Split-Path -Parent $path.FullName
        if (!$parent) { break }
    }
    $targetVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($targetFile.FullName).FileVersion
    Assert-ReleaseExecutable $targetFile.FullName $Architecture $Edition 'net10' $targetVersion
    return [pscustomobject]@{ Machine = [Environment]::MachineName; Worktree = $worktree; Edition = $Edition
        Architecture = $Architecture; Target = $targetFile.FullName; InstanceId = $InstanceId }
}

function Get-LocalEditionBinding([string]$Edition, [string]$Architecture, [string]$BindingDirectory) {
    $file = Get-BindingPath $Edition $Architecture $BindingDirectory
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { return }
    $saved = Get-Content -Raw -Encoding utf8 -LiteralPath $file | ConvertFrom-Json
    if ($saved.Machine -ine [Environment]::MachineName -or $saved.Worktree -ine $worktree -or
        $saved.Edition -cne $Edition -or $saved.Architecture -cne $Architecture) { throw "Mismatched local build binding: $file" }
    return New-LocalEditionBinding $saved.Target $Edition $Architecture $saved.InstanceId
}

function Save-LocalEditionBinding($Binding, [string]$BindingDirectory) {
    $file = Get-BindingPath $Binding.Edition $Binding.Architecture $BindingDirectory
    [IO.Directory]::CreateDirectory((Split-Path -Parent $file)) | Out-Null
    $temporary = $file + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, ($Binding | ConvertTo-Json))
        if ([IO.File]::Exists($file)) { [IO.File]::Replace($temporary, $file, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $file) }
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    Write-Output "Automatic updates from this worktree are bound to $($Binding.Target)"
}

function Find-TargetProcesses([string]$Target) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Target)
    foreach ($process in [Diagnostics.Process]::GetProcessesByName($name)) {
        try {
            if (!$process.HasExited -and $process.MainModule.FileName -ieq $Target) { $process; continue }
        } catch [InvalidOperationException] { } catch [ComponentModel.Win32Exception] { }
        $process.Dispose()
    }
}

function Get-ArgumentTail($Process, [string]$Target) {
    $row = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $Process.Id)
    if (!$row -or $Process.HasExited -or $row.ExecutablePath -ine $Target -or !$row.CommandLine) {
        throw 'Could not verify how the bound instance was launched.'
    }
    $command = $row.CommandLine.TrimStart()
    if ($command.StartsWith($Target, [StringComparison]::OrdinalIgnoreCase) -and
        ($command.Length -eq $Target.Length -or [char]::IsWhiteSpace($command[$Target.Length]))) {
        return $command.Substring($Target.Length).TrimStart()
    }
    $match = [regex]::Match($command, '^(?:"[^"]+"|\S+)\s*(.*)$', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (!$match.Success) { throw 'Could not preserve the launch arguments.' }
    return $match.Groups[1].Value
}

function Start-BoundProcess($Binding, [string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Binding.Target
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Binding.Target)
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    if ($Binding.InstanceId) { $start.EnvironmentVariables['SONICROUTE_INSTANCE_ID'] = $Binding.InstanceId }
    else { $start.EnvironmentVariables.Remove('SONICROUTE_INSTANCE_ID') }
    return [Diagnostics.Process]::Start($start)
}

function Wait-ForBoundProcess($Process, [string]$SinkTitle) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!$Process.HasExited -and $timer.Elapsed.TotalSeconds -lt 25) {
        if ([SonicRoute.Packaging.LocalEditionNative]::IsReady($Process, $SinkTitle)) { return }
        Start-Sleep -Milliseconds 150
    }
    throw 'The restarted copy did not create a responsive activation window.'
}

function Install-LocalEdition($Binding, [string]$Source, [string]$ExpectedHash, [string]$ExpectedVersion) {
    $sourceFile = Get-Item -LiteralPath $Source
    if ($ExpectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'Missing or invalid SHA-256 in the build receipt.' }
    $target = $Binding.Target
    $mutex = [Threading.Mutex]::new($false, ('Local\SonicRoute.Build.' + (Get-LocalPathKey $target)))
    $locked = $false
    $staged = $target + '.' + [guid]::NewGuid().ToString('N') + '.new'
    $backup = $target + '.previous'
    $recovery = $staged + '.rollback'
    $old = $null
    $started = $null
    $swapped = $false
    try {
        try { $locked = $mutex.WaitOne(120000) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (!$locked) { throw 'Another installation is still using this target.' }
        # Copy first and verify the exact bytes that will be installed, even when the source is on another volume.
        [IO.File]::Copy($sourceFile.FullName, $staged)
        if ((Get-FileHash -LiteralPath $staged).Hash -ine $ExpectedHash) { throw 'The build changed or failed SHA-256 verification.' }
        if (!$ExpectedVersion) { $ExpectedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($staged).FileVersion }
        Assert-ReleaseExecutable $staged $Binding.Architecture $Binding.Edition 'net10' $ExpectedVersion
        if ((Get-FileHash -LiteralPath $target).Hash -ieq $ExpectedHash) {
            Write-Output "Already current: $target"
            return
        }
        if (!('SonicRoute.Packaging.LocalEditionNative' -as [type])) {
            Add-Type -Path (Join-Path $PSScriptRoot 'LocalEditionNative.cs')
        }
        $suffix = if ($Binding.InstanceId) { '_' + $Binding.InstanceId } else { '' }
        if ($Binding.Edition -ceq 'Full') { $suffix += '_Full' }
        $title = "SonicRoute${suffix}_ActivateSink"
        $processes = @(Find-TargetProcesses $target)
        if ($processes.Count -gt 1) { throw 'Several instances are running from the bound path.' }
        $arguments = ''
        if ($processes.Count -eq 1) {
            $old = $processes[0]
            if (![SonicRoute.Packaging.LocalEditionNative]::IsReady($old, $title)) {
                throw 'The running file is not the responsive instance named in this binding.'
            }
            $arguments = Get-ArgumentTail $old $target
            if ($old.HasExited) { throw 'The bound instance changed before shutdown.' }
        }
        try {
            if ($old) { [SonicRoute.Packaging.LocalEditionNative]::RequestExit($old) }
            if (@(Find-TargetProcesses $target).Count) { throw 'Another process started from the bound path during installation.' }
            [IO.File]::Replace($staged, $target, $recovery, $true)
            $swapped = $true
            if ($old) {
                $started = Start-BoundProcess $Binding $arguments
                Wait-ForBoundProcess $started $title
            }
            if ([IO.File]::Exists($backup)) { [IO.File]::Replace($recovery, $backup, [NullString]::Value, $true) }
            else { [IO.File]::Move($recovery, $backup) }
        } catch {
            $cause = $_.Exception.Message
            try {
                if ($started -and !$started.HasExited) {
                    try { [SonicRoute.Packaging.LocalEditionNative]::RequestExit($started) }
                    catch {
                        # Only the newly spawned, unhealthy child can be terminated during rollback.
                        if (!$started.HasExited) { $started.Kill(); $started.WaitForExit() }
                    }
                }
                if ($swapped) { [IO.File]::Replace($recovery, $target, $staged, $true) }
                if ($old -and $old.HasExited -and !@(Find-TargetProcesses $target).Count) {
                    $previous = Start-BoundProcess $Binding $arguments
                    try { Wait-ForBoundProcess $previous $title } finally { $previous.Dispose() }
                }
            } catch { throw "Local update failed ($cause); recovery failed: $($_.Exception.Message). Target: $target; rollback file: $recovery" }
            throw "Local replacement failed: $cause. The previous copy is at $target."
        }
        if ($started) { Write-Output "Updated and restarted PID $($started.Id): $target" }
        else { Write-Output "Updated while closed: $target" }
    } finally {
        if ($started) { $started.Dispose() }
        if ($old) { $old.Dispose() }
        if ([IO.File]::Exists($staged)) { [IO.File]::Delete($staged) }
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

Export-ModuleMember -Function Get-LocalEditionBinding, New-LocalEditionBinding, Save-LocalEditionBinding, Install-LocalEdition
