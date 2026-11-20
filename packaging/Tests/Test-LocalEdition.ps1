param([Parameter(Mandatory)][string]$ReceiptPath, [Parameter(Mandatory)][string]$PreviousExe,
      [Parameter(Mandatory)][string]$OutputDirectory, [switch]$CheckBuildHook, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$packaging = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $packaging 'LocalEdition.psm1')
if (!('SonicRoute.Packaging.LocalEditionNative' -as [type])) { Add-Type -Path (Join-Path $packaging 'LocalEditionNative.cs') }
$receipt = Get-Content -Raw -Encoding utf8 -LiteralPath $ReceiptPath | ConvertFrom-Json
if ($receipt.Edition -cne 'Full') { throw 'This isolated exercise uses the Full edition lab switch.' }
$oldHash = (Get-FileHash -LiteralPath $PreviousExe).Hash
if ($oldHash -ieq $receipt.SHA256) { throw 'Supply two distinct builds.' }

$run = [guid]::NewGuid().ToString('N')
$fixture = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $run
$bindingDir = Join-Path $fixture 'bindings'
$identities = @("SetupTest_$run","OtherTest_$run")
$targets = @()
$processes = @()
$log = Join-Path $OutputDirectory 'results.log'
[IO.Directory]::CreateDirectory($fixture) | Out-Null
function Assert-Test($Pass,[string]$Label) {
    if (!$Pass) { throw "FAIL $Label" }
    "PASS $Label" | Tee-Object -FilePath $log -Append
}
function Find-Exact([string]$File) {
    return [Diagnostics.Process]::GetProcessesByName([IO.Path]::GetFileNameWithoutExtension($File)) |
        Where-Object { !$_.HasExited -and $_.Path -ieq $File } | Select-Object -First 1
}
function Start-Fixture($File,$Id) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File; $start.Arguments = '--android-lab --panel'
    $start.WorkingDirectory = Split-Path -Parent $File; $start.UseShellExecute = $false
    $start.EnvironmentVariables['SONICROUTE_INSTANCE_ID'] = $Id
    $start.EnvironmentVariables.Remove('SONICROUTE_DATA_DIRECTORY')
    $process = [Diagnostics.Process]::Start($start)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!$process.HasExited -and $watch.Elapsed.TotalSeconds -lt 25) {
        if ([SonicRoute.Packaging.LocalEditionNative]::IsReady($process,"SonicRoute_${Id}_Full_ActivateSink")) { return $process }
        Start-Sleep -Milliseconds 150
    }
    throw "Fixture did not start: $File"
}
try {
    foreach ($name in @('installed copy','same name elsewhere')) {
        $dir = Join-Path $fixture $name
        [IO.Directory]::CreateDirectory($dir) | Out-Null
        $target = Join-Path $dir 'SonicRoute.exe'
        [IO.File]::Copy($PreviousExe, $target)
        $targets += $target
        $processes += Start-Fixture $target $identities[$processes.Count]
    }
    $binding = New-LocalEditionBinding $targets[0] $receipt.Edition $receipt.Architecture $identities[0]
    $rejected = $false
    try { Install-LocalEdition $binding $receipt.Asset ('0' * 64) } catch { $rejected = $true }
    Assert-Test ($rejected -and !$processes[0].HasExited -and (Get-FileHash $targets[0]).Hash -ieq $oldHash) 'hash mismatch preserves the live old copy'
    & (Join-Path $packaging 'Install-LocalEdition.ps1') -ReceiptPath $ReceiptPath -TargetPath $targets[0] -Remember -InstanceId $identities[0] -BindingDirectory $bindingDir
    $updated = Find-Exact $targets[0]
    Assert-Test ($processes[0].HasExited -and $updated -and $updated.Id -ne $processes[0].Id -and
        [SonicRoute.Packaging.LocalEditionNative]::IsReady($updated,"SonicRoute_$($identities[0])_Full_ActivateSink")) 'running copy exits and the new PID has a responsive activation window'
    Assert-Test ((Get-FileHash $targets[0]).Hash -ieq $receipt.SHA256 -and (Get-FileHash ($targets[0]+'.previous')).Hash -ieq $oldHash) 'verified executable installed with a byte-identical backup'
    Assert-Test (!$processes[1].HasExited -and (Get-FileHash $targets[1]).Hash -ieq $oldHash) 'same-named instance in another folder stays live and unmodified'
    Assert-Test ((Get-CimInstance Win32_Process -Filter ("ProcessId = " + $updated.Id)).CommandLine -match '--android-lab --panel') 'the restart preserves the original arguments'
    $saved = Get-LocalEditionBinding $receipt.Edition $receipt.Architecture $bindingDir
    Assert-Test ($saved.Target -ieq $targets[0] -and !(Get-LocalEditionBinding 'Lite' $receipt.Architecture $bindingDir)) 'the saved binding applies only to the selected edition and worktree'
    Install-LocalEdition $saved $receipt.Asset $receipt.SHA256
    Assert-Test (!$updated.HasExited) 'reinstalling identical bytes leaves the healthy process alone'
    # Fail only the first health probe, after the candidate really started. All shutdown,
    # replacement, rollback and restart operations use the same production implementation.
    $module = Get-Module LocalEdition
    & $module {
        $script:realProbe = (Get-Item Function:Wait-ForBoundProcess).ScriptBlock
        $script:failProbe = $true
        function script:Wait-ForBoundProcess($Process, [string]$SinkTitle) {
            & $script:realProbe $Process $SinkTitle
            if ($script:failProbe) { $script:failProbe = $false; throw 'Fixture rejects candidate startup.' }
        }
    }
    $rejected = $false
    try { Install-LocalEdition $saved $PreviousExe $oldHash }
    catch { if ($_.Exception.Message -notmatch 'Fixture rejects candidate startup') { throw }; $rejected = $true }
    finally { & $module { Set-Item Function:script:Wait-ForBoundProcess $script:realProbe } }
    $restored = Find-Exact $targets[0]
    Assert-Test ($rejected -and $restored -and $restored.Id -ne $updated.Id -and (Get-FileHash $targets[0]).Hash -ieq $receipt.SHA256 -and
        (Get-FileHash ($targets[0]+'.previous')).Hash -ieq $oldHash -and !$processes[1].HasExited) 'rejected startup rolls back bytes, keeps the last good backup and restarts only the original target'
    [SonicRoute.Packaging.LocalEditionNative]::RequestExit($restored)
    Install-LocalEdition $saved $PreviousExe $oldHash
    Assert-Test (!(Find-Exact $targets[0]) -and (Get-FileHash $targets[0]).Hash -ieq $oldHash) 'updating a closed copy keeps it closed'
    if ($CheckBuildHook) {
        $build = Join-Path $packaging 'Build-Edition.ps1'
        foreach ($layout in @('asset','publish')) {
            $output = Join-Path $fixture "build-$layout"
            $path = if ($layout -eq 'asset') { Join-Path $output ([IO.Path]::GetFileName($receipt.Asset)) }
                else { Join-Path $output 'Full-x64/SonicRoute.exe' }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            [IO.File]::Copy($PreviousExe, $path)
            $targets += $path
            $testId = "Hook_${layout}_$run"
            $original = Start-Fixture $path $testId
            $hookBinding = New-LocalEditionBinding $path $receipt.Edition $receipt.Architecture $testId
            Save-LocalEditionBinding $hookBinding $bindingDir
            & $build -Edition Full -Architecture x64 -OutputRoot $output -DotnetPath $DotnetPath -NoReadyToRun -LocalBindingDirectory $bindingDir
            $built = Get-Content -Raw -LiteralPath (Join-Path $output 'Full-x64-receipt.json') | ConvertFrom-Json
            $fresh = Find-Exact $path
            Assert-Test ($original.HasExited -and $fresh -and [SonicRoute.Packaging.LocalEditionNative]::IsReady($fresh, "SonicRoute_${testId}_Full_ActivateSink") -and
                (Get-FileHash $path).Hash -ieq $built.SHA256 -and (Get-FileHash ($path+'.previous')).Hash -ieq $oldHash -and !$processes[1].HasExited) "ordinary build automatically installs and restarts the running $layout output, keeping its backup"
            [SonicRoute.Packaging.LocalEditionNative]::RequestExit($fresh)
        }
    }
} catch {
    $_ | Out-String | Add-Content -LiteralPath $log
    throw
} finally {
    foreach ($target in $targets) {
        $owned = Find-Exact $target
        if ($owned -and $owned.Path -ieq $target) {
            try { [SonicRoute.Packaging.LocalEditionNative]::RequestExit($owned) }
            catch { if (!$owned.HasExited) { $owned.Kill(); $owned.WaitForExit() } }
        }
    }
}
