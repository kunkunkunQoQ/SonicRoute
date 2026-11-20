param(
    [Parameter(Mandatory)][string]$DotnetHost,
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$hostPath = (Resolve-Path -LiteralPath $DotnetHost).Path
$assembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a new output directory for this test.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$launcherScript = Get-Content (Join-Path $repository 'packaging/Tests/PanelChecks/Run-OutputPopupChecks.ps1') -Raw -Encoding UTF8
$launcher = [regex]::Match($launcherScript, '(?s)Add-Type -TypeDefinition @''\r?\n(.*?)\r?\n''@').Groups[1].Value
if (-not ('OutputPopupLauncher' -as [type])) { Add-Type -TypeDefinition $launcher }
$previous = $env:SONICROUTE_PRIVATE_INPUT_CHECK
$env:SONICROUTE_PRIVATE_INPUT_CHECK = '1'
$source = $null
try {
    # This process only publishes the two synthetic app windows below the foreground window through a local listener.
    $sourceArgs = '"' + $assembly + '" --windows-link-window-audio-source "' + $root + '"'
    $source = Start-Process -FilePath $hostPath -ArgumentList $sourceArgs -WindowStyle Hidden -PassThru -WorkingDirectory $repository `
        -RedirectStandardOutput (Join-Path $root 'source.log') -RedirectStandardError (Join-Path $root 'source-error.log')
    $descriptor = Join-Path $root 'source.json'
    $startup = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $descriptor) -and -not $source.HasExited -and $startup.Elapsed.TotalSeconds -lt 10) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $descriptor)) { throw (Get-Content (Join-Path $root 'source-error.log') -Raw) }
    $receiver = '"' + $assembly + '" --windows-link-window-audio-native "' + $root + '" --file-log'
    $code = [OutputPopupLauncher]::Run($hostPath, $receiver, $repository)
    Get-Content (Join-Path $root 'console.log') -Encoding UTF8
    if ($code) { throw "Private window/audio test exited with $code." }
    if (-not $source.WaitForExit(5000) -or $source.ExitCode -ne 0) { throw 'Window source cleanup failed.' }
    Get-Content (Join-Path $root 'source.log') -Encoding UTF8
}
finally {
    if ($source -and -not $source.HasExited) {
        if (Test-Path -LiteralPath $descriptor) {
            $name = (Get-Content $descriptor -Raw | ConvertFrom-Json).Signal + '-stop'
            try {
                $event = [Threading.EventWaitHandle]::OpenExisting($name)
                try { [void]$event.Set() } finally { $event.Dispose() }
            } catch [Threading.WaitHandleCannotBeOpenedException] { }
        }
        [void]$source.WaitForExit(5000)
    }
    if ($source) { $source.Dispose() }
    $env:SONICROUTE_PRIVATE_INPUT_CHECK = $previous
}
