# Audio path checks

Build and run with .NET 10 SDK on Windows:

```powershell
dotnet build packaging/Tests/AudioPathChecks/Checks.csproj -c Release -f net10.0-windows
& packaging/Tests/AudioPathChecks/bin/Release/net10.0-windows/AudioPathChecks.exe
```

Repeat with `net8.0-windows` and `net48` for compatibility. `-p:BaselineRevision=<commit>` compiles the corresponding Git sources and runs the common before/after cases, with the old public APIs on that side.

The preparation script compiles product service bodies with only native object construction/release replaced by managed fakes; the panel's actual row-read lambda is extracted verbatim. The routing factory boundary is a fake as well. No real audio volume, mute, route, default-device or user data writes occur. This checks control flow, call counts, failure handling and reference protection; it does **not** prove native COM, physical audio, CPU savings, or installed Store behavior. Product code has no test hooks or new runtime dependency.

`--input-state` limits current-source execution to combined capture availability
and mute reads: first-session/PID selection, cache reuse and invalidation,
missing sessions, one read-failure retry, consistent state after disappearance,
reference protection during refresh, and unchanged checked-write verification.
These are fake sessions; PID-targeted calls do not prove that native capture
mute is isolated from other applications using the same recording device.

The baseline assertions document previous behavior, including the mixed-session and role-parameter defects, rather than treating those defects as the desired result.

Current-source checks also cover concurrent volume adjustments and a blocked refresh
with a waiting caller, verifying that generation sharing and Monitor wakeup finish.
The optional NET10 `--perf-session` (`concurrent` for four workers) measures the actual
ReadState method against managed fake sessions. It is a controlled local probe, not
a real COM, whole-process idle CPU or UI performance measurement. The reported
allocation count covers only the calling thread; it excludes worker allocations.

`--panel-snapshot` checks the actual panel-reader body and shared service snapshot
against fake native boundaries. It covers hidden and multiple same-PID playback
sessions, recording separation, missing PIDs, one failure/generation retry,
disabled route names and empty-list deferral. A cold healthy comparison uses the
previous refresh/row/global-mute sequence and the combined snapshot sequence;
the enumeration counts are local operation evidence, not startup latency or
whole-process CPU measurements. The reader extractor supports both historical
dictionary returns and the current snapshot return without changing its logic.

`Native/Checks.csproj` references the real Core assembly. It reads actual device/application state and creates a temporary silent waveOut session. It only writes volume/mute for **its own PID**, restores those values, and closes that stream; it never writes global volume, microphones, routes or default devices. Run separately on each x64 runtime. It does not validate system-default switching or physical-device write failures.
