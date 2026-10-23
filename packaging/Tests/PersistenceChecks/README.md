# Persistence checks

This harness references the real Core project. It compares the current config/rule
serializer with the previous reflection contract, then runs actual atomic config
save, export/import validation, rule reload, scheduled claims and legacy migration.
`shutdown` independently verifies that both pending watcher callbacks can acquire
the store lock while disposal waits; this reproduced the original lock order failure.
It does not execute audio actions. Set `SONICROUTE_DATA_DIRECTORY` **in the parent
process** to a new disposable directory before each invocation (`checks` and
`legacy` must use separate directories). Never point it at production user data.

Build with SDK 10 for `net10.0-windows`, `net8.0-windows`, and `net48`. Run NET8 with
the installed NET8 host, NET10 with the installed NET10 host, and Framework with
`Checks.exe`. The installed SDK host and runtime host can live in different places.

NET10 also accepts:

```text
Checks fixtures <directory>
Checks perf reflection <directory>
Checks perf generated <directory>
```

Generate fixtures in a separate process. Publish the harness with the same
ReadyToRun setting as Lite, run each sample in a fresh process, and alternate modes.
Otherwise fixture serialization prewarms the reflection path and invalidates cold
comparisons. `configColdMs` covers serializer/options initialization and the first
config read; `coldMs` additionally reads 100 single-step rules. Warm reads/writes
each perform 2,000 operations. Allocations are current-thread managed bytes, not
process private memory; these timings are not whole-app startup or idle CPU results.

The NET10 persistence context is intentionally limited to config/rule contracts.
Update/network JSON, command responses and optional execution history keep their
existing behavior. Unknown fields in imported config backups still pass through
the existing `JsonDocument` transfer path.
