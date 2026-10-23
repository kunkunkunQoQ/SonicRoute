# Application icon regression checks

Windows only; requires the .NET 10 SDK and the corresponding Desktop Runtimes
(.NET 10, .NET 8, and .NET Framework 4.8). No administrator access is required.

```powershell
dotnet build packaging/Tests/AppIconChecks/Checks.csproj -c Release
foreach ($framework in 'net10.0-windows', 'net8.0-windows', 'net48') {
    & "packaging/Tests/AppIconChecks/bin/Release/$framework/Checks.exe"
    if ($LASTEXITCODE -ne 0) { throw "Icon checks failed: $framework" }
}
```

The checks compile the actual `AppIconService.cs`. Only configuration and the
custom icon store are substituted; no user configuration or audio settings are
read or written. Disposable child processes create hidden native windows, then
restrict **their own** process DACL to deny `PROCESS_QUERY_LIMITED_INFORMATION`.
The parent verifies Windows actually returns access denied and tests class icons,
`WM_GETICON`, alpha, a hung window, missing icons and native handle cleanup.
The children are terminated in the test's cleanup path.

Also checks successful/concurrent cache reuse, recovery of a failed cache entry
after five seconds, session-supplied file paths, custom icon precedence, and paths
containing Unicode, spaces and commas. The five seconds are a cache cooldown
checked on demand, not a production timer or an automatic retry loop.

These fixtures test real Windows access denial; they do not simulate a UAC token,
launch an elevated application or prove every protected application is readable.
They do not launch SonicRoute's complete UI or replace native ARM64/Windows 10
acceptance testing.

Optional read-only probes for a running application (the output PNG path is explicit):

```powershell
Checks.exe --probe-pid <pid> <output.png>
Checks.exe --probe-session <pid> <session-icon-file> <output.png>
```

The second probe uses the supplied metadata path; it does not itself query WASAPI.
An end-to-end diagnosis must also confirm that `AudioService.GetApps()` supplies
that path and that the actual production `AppItem` consumes it.
