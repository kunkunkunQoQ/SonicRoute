# Legacy compatibility checks

Build with SDK 10 in Release. This harness references the real Legacy project and
loads its compiled update bootstrap; it does not construct the WPF application or
execute audio actions.

Copy the complete output directory to a disposable directory containing `中文 空格`.
Set `SONICROUTE_DATA_DIRECTORY` to an absolute disposable path in the parent process,
then run `LegacyCompatChecks.exe checks`. The checks cover Unicode/space paths,
working-directory independence, concurrent reads, fallback after a failed cached
lookup, ordinary bootstrap, Store exclusion and malformed update arguments.
The fallback check temporarily changes a private field in this isolated test
process only, then restores it. Do not run this tool against production data.

Optional local performance modes:

```text
LegacyCompatChecks.exe path
LegacyCompatChecks.exe json <fixture-directory>
LegacyCompatChecks.exe json-read <fixture-directory>
```

`path` warms the existing identity cache, then reads the executable path 1,000 times.
`json` reads/writes a `config.json` and 100 single-step `rule.json` fixtures, then
measures 1,000 warm rule read/write pairs; `json-read` measures the corresponding
read-only workload. Generate fixtures in a separate process. Compare fresh test
processes against frozen before/after product assemblies with alternating order.
Managed allocations are current-thread counts, not resident memory; these are
component measurements, not whole-application startup or background CPU benchmarks.

The existing `PersistenceChecks` harness separately validates config/rule JSON
compatibility, real atomic save/export/import validation, legacy data migration,
scheduled claims and watcher shutdown behavior.
