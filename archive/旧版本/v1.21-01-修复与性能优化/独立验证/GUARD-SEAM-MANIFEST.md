# v1.21 test isolation and seam manifest

## Source and output scope

- Validation-only files and test outputs are confined to `E:\kunkun\SonarSwitch\dist\v121-validation-agent\`.
- `source\baseline` is the copied v1.20-35 source snapshot; `source\candidate` is the copied current v1.21 source used for the isolated builds. The validation work did not edit production source files.
- Test binaries are copied under `bin\lite\{baseline,candidate}` and `bin\legacy\{baseline,candidate}`. Results are under `results\formal4`, `results\formal5`, and `results\formal6`.
- No file under `SonicRoute源码\` was read or changed by this validation agent.
- The final test candidate has the Legacy NET48 `UpdateBar`/`BarRendering`/`ApplyBar`/`StopBarAnimation`/`CancelBarUpdate`/`CreateBarEase` block restored to the v1.20-35 baseline text. The Lite NET8 interpolation block remains unchanged. The isolated `(24,24)` OSD placement seam is present only in the copied candidate.

## Startup and storage isolation

- The runner sets `SONICROUTE_V121_PERF_ISOLATED=1` and `SONICROUTE_V121_READ_ONLY=1`, suppresses normal `App.OnStartup` work in the test copy, and installs `DispatcherSynchronizationContext` before `App.InitializeComponent`.
- `ConfigService.ConfigPath` and `AutoRuleStore.RulesDir` are redirected to each binary's own `test-data` subtree. The harness canonicalizes and validates paths against that subtree before deletion/reset operations.
- The default app self-test is not run. Normal user-app processes are not started, stopped, or modified.

## Audio and routing write barriers

The read-only environment guard is present in both test-copy source sets and compiled in both runtime variants:

- `SystemVolumeService`: `SetVolumePercent`, `AdjustVolumePercent`, `SetMute`.
- `SessionVolumeService`: `SetVolumePercent`, `AdjustVolumePercent`, `SetMute`, `SetAllMute`, `SetInputMute`.
- `GlobalMicMuteService.SetMute`.
- `AudioService.ApplyEndpoint` and `ResetAllPersistedEndpoints`.
- `PolicyConfigClient.SetDefault`.

The UI tests do not select a real audio app row. The QuickPanel fixture uses eight synthetic inactive PIDs. Audio APIs are guarded at the test-copy setter/service entry points. No volume or routing write was performed by these runs.

## Test-only instrumentation and behavioral seams

- `ConfigService.V121TestSaveCount` counts saves for debounce/flush assertions.
- `AudioService.GetApps` returns eight synthetic inactive apps only when `SONICROUTE_V121_TEST_APPS_FIXTURE=1`.
- `SessionVolumeService.Refresh` is a no-op only during the isolated empty-set `AllMuted` test when `SONICROUTE_V121_TEST_NO_AUDIO_REFRESH=1`; the harness removes the variable after that case. This verifies the empty collection branch without depending on live audio sessions.
- In the isolated OSD runs, the copied `OsdService.PlaceNow` positions the visible window at `(24,24)` so both sides are measured in the primary work area. This test-only placement path is not in production.
- MainWindow is exercised as logical UI without showing it. QuickPanel is briefly shown to validate rows/meter cleanup. The OSD is visibly shown at `(24,24)` during each onscreen measurement and is closed by the test process.
- The OSD diagnostic-only Rendering subscription counts logical `ScaleX` changes. CPU measurements use separate phases without the monitor; `ScaleX` change rate is not a presented-frame rate.

## Cleanup verification

After final Lite and Legacy candidate storage/UI runs, recursive scans of each candidate `test-data` tree found zero `*.tmp` and zero `*.rollback` files. Atomic success and persistent-lock failure cases also recorded no remaining temporary file. The Legacy candidate test data contained only generated test JSON/config files.

Root-level test/build artifacts found during final cleanup (the `PerfReview.exe` runner, matching app/dependency binaries, and generated `test-data`) were moved into `accidental-root-output/`. `accidental-root-output/PROVENANCE.txt` preserves exact source paths, timestamps, hashes, and the data-file inventory. The move was an explicit file/directory whitelist into a verified path under the validation directory. `.workbuddy-ai/`, `动画/`, and production source files, including `SonicRoute.Core/AtomicFile.cs`, were not moved or changed by cleanup.

## Known limitations

- OSD DispatcherTimer inputs requested 30 Hz but delivered about 21.3 Hz under this dispatcher; both sides matched at that actual rate. ScaleX logical update rates were measured separately at about 30 Hz.
- .NET Framework CPU timing was quantized and noisy; the Legacy OSD comparison is a narrow test scenario. No broad product CPU claim is supported by it.
- The Legacy baseline full storage run once stalled in an earlier exploratory attempt; the final baseline mini failure case and final candidate full storage cases completed. This does not affect the final candidate storage pass result.
- Legacy baseline `storage-mini` intentionally fails the preservation assertion after an injected rename-save error; this is a baseline regression observation, not a candidate failure.
- `results/formal5` is an intermediate Legacy OSD implementation and is not the final candidate. The final restored Legacy block was rebuilt and checked by `results/formal6/legacy-candidate-restored-baseline-osd/results.json`; its final value, ~30 Hz logical update rate, animation stop, disabled state, and hidden cleanup pass. Do not use formal5 CPU results to describe the final Legacy implementation.
