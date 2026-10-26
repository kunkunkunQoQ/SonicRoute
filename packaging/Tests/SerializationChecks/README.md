# History and language serialization checks

Build `Checks.csproj -c Release -f <TFM>` using SDK 10 for `net10.0-windows`,
`net8.0-windows`, or `net48`. Run with the matching runtime host or Framework EXE.
Each functional invocation creates a fresh `isolated-serialization-*` directory
beside the harness and sets the product data root before first use. Never substitute
the real user data directory. No audio, hotkeys, timers or application startup run.

The harness references the actual Core assembly and links the actual localization
sources/resources. It checks old/reflection JSONL byte compatibility, Unicode,
dates, partial tails, malformed records, rotation, limits, cancellation and disposal;
all nine builtin languages, stream parsing, external overrides, null/duplicate keys,
imports, exports, rename, deletion and corrupt-file fallback.

NET10 opt-in performance commands:

```text
Checks --fixture <history.json>
Checks --perf language reflection|generated <builtin-language.json>
Checks --perf history reflection|generated <history.json>
```

Generate fixtures in a separate process. Publish the harness as x64 FDD with
ReadyToRun for comparisons; alternate fresh processes in AB/BA order and retain all
samples. Language cold time includes the first dictionary deserialization; export
timing compares the prior per-call writer options with the product cached options.
History measurements warm both paths and use the actual Core generated writer
contract; the reader intentionally remains the original reader in both modes.
Contract reflection used to access the internal writer in the harness occurs before
the timing interval. Allocation counts cover the current thread. These are serializer
microbenchmarks, not application startup, resident memory, file-IO or idle CPU results.
