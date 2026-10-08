# Short command client: sends only to an existing SonicRoute instance, never launches the app.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

function Write-ErrorResult([int] $ExitCode, [string] $Code) {
    [Console]::WriteLine((@{ ExitCode = $ExitCode; Code = $Code; RuleId = ''; RuleName = ''; Succeeded = 0; Failed = 0 } | ConvertTo-Json -Compress))
}

function Read-Full([System.IO.Stream] $Stream, [byte[]] $Buffer) {
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($read -eq 0) { throw 'Disconnected' }
        $offset += $read
    }
}

if ($args.Count -eq 1 -and $args[0] -in @('--help', '-h')) {
    [Console]::WriteLine([System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'sr-help.txt')))
    exit 0
}

$editions = @('Lite', 'Legacy')
$selector = ''
if ($args.Count -eq 1) {
    $selector = [string] $args[0]
} elseif ($args.Count -eq 2 -and $args[0] -eq '--legacy') {
    $editions = @('Legacy')
    $selector = [string] $args[1]
}
if ([string]::IsNullOrWhiteSpace($selector)) { Write-ErrorResult 2 'invalid_arguments'; exit 2 }
$selector = $selector.Trim()
$body = [System.Text.Encoding]::UTF8.GetBytes($selector)
if ($body.Length -gt 16384) { Write-ErrorResult 2 'invalid_arguments'; exit 2 }

$pipe = $null
$exitCode = 6
try {
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($edition in $editions) {
        $candidate = [System.IO.Pipes.NamedPipeClientStream]::new('.', "SonicRoute.RuleCommand.$edition.$sessionId.$sid", [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
        try { $candidate.Connect(250); $pipe = $candidate; break }
        catch { $candidate.Dispose() }
    }
    if ($null -eq $pipe) { throw 'Server unavailable' }

    $request = [byte[]]::new(4 + $body.Length)
    [System.BitConverter]::GetBytes($body.Length).CopyTo($request, 0)
    $body.CopyTo($request, 4)
    $write = $pipe.WriteAsync($request, 0, $request.Length)
    if (-not $write.Wait(10000)) { throw 'Write timeout' }
    $write.GetAwaiter().GetResult()

    # Completion has no fixed timeout because rules can contain long delays.
    $header = [byte[]]::new(4)
    Read-Full $pipe $header
    $length = [System.BitConverter]::ToInt32($header, 0)
    if ($length -le 0 -or $length -gt 16384) { throw 'Invalid frame' }
    $response = [byte[]]::new($length)
    Read-Full $pipe $response
    $json = [System.Text.UTF8Encoding]::new($false, $true).GetString($response)
    $result = $json | ConvertFrom-Json
    if ($null -eq $result.ExitCode -or $result.ExitCode -lt 0 -or $result.ExitCode -gt 8) { throw 'Invalid result' }
    [Console]::WriteLine($json)
    $exitCode = [int] $result.ExitCode
} catch {
    Write-ErrorResult 6 'server_unavailable'
} finally {
    if ($null -ne $pipe) { $pipe.Dispose() }
}
exit $exitCode
