# Monitoring checks

Build `Checks.csproj` for the desired target framework. The prepare script copies
the product monitoring sources and replaces only native object construction and
COM release boundaries with managed fixtures. No user audio state is accessed.

- `--panel-performance`: stable target reuse, dirty/explicit device selection,
  filter replacement, notification arrival during an in-flight read, registration
  recovery, asynchronous meter disposal during a blocked native read, skipped
  remaining sampling, and rapid new-instance startup while old cleanup finishes.
- `--endpoint-volumes-only`: existing output and microphone notification,
  incremental read, recovery and disposal checks.
- `--meter-only`: existing meter sampling, notifications, idle waits, recovery,
  target replacement and resource cleanup checks.
- No arguments: the original combined checks.

Operation counters and blocking fixtures verify execution behavior. They do not
measure native driver latency, physical input, idle CPU or whole-app performance.
