# v1.21 validation result index

All run data is under `results/`. Each case writes a machine-readable `results.json` in its run directory.

## Final candidate regressions

- Lite storage regression: `results/formal4/lite-candidate-storage-full/results.json` — rule save failure retention, rename/same-name, legacy-ID migration, delete/cache, deep copy, external revision/watcher directory recreation, empty AllMuted, combined system read. All candidate checks pass; the empty AllMuted test uses a test-only no-audio-refresh seam.
- Legacy storage regression: `results/formal4/legacy-candidate-storage-full/results.json` — same checks. All candidate checks pass.
- Lite UI regression after final AtomicFile: `results/formal4/lite-candidate-ui-v121-final-atomic/results.json` — 100-row refresh reuse, running state, one-rule delta, name debounce/focus/close flush, device ID retention, 1000 unchanged watcher-cache reads, QuickPanel meter targeting and cleanup. All checks pass.
- Legacy UI regression after final AtomicFile: `results/formal4/legacy-candidate-ui-v121-final-atomic/results.json` — same contracts pass.
- Atomic share-lock regressions: `results/formal4/lite-candidate-atomic-conflict/results.json` and `results/formal4/legacy-candidate-atomic-conflict/results.json`. Persistent `FileShare.Read` lock yields `HRESULT 0x80070020`/Win32 32; old file/cache are retained and no temporary file remains. A 10 ms lock release succeeds on retry; new contents persist.
- Baseline negative storage check: `results/formal4/legacy-baseline-storage-mini/results.json` — forced rename-save failure loses the prior file/rule (`Pass=false`), demonstrating the baseline behavior addressed by candidate storage code. `ReadSnapshot` is unsupported in baseline.

## Indexed rule save A/B

Each case saves and reloads 100 actual rule files, then performs 50 saves against the indexed target. Lite ABBA samples:

- Baseline A/B: `results/formal4/lite-baseline-indexed-save-a/results.json`, `results/formal4/lite-baseline-indexed-save-b/results.json`
- Candidate A/B: `results/formal4/lite-candidate-indexed-save-a/results.json`, `results/formal4/lite-candidate-indexed-save-b/results.json`

Legacy ABBA samples:

- Baseline A/B: `results/formal4/legacy-baseline-indexed-save-a/results.json`, `results/formal4/legacy-baseline-indexed-save-b/results.json`
- Candidate A/B: `results/formal4/legacy-candidate-indexed-save-a/results.json`, `results/formal4/legacy-candidate-indexed-save-b/results.json`

Allocation fell from about 2.63 MB/op to 29–82 KB/op in Lite (about 96.9–98.9% lower), and from 2.70 MB/op to about 34.7 KB/op in Legacy (about 98.7% lower). Wall-time tails varied substantially: candidate P95 reached 232 ms in Lite and 191 ms in Legacy, so the samples do not support a claim that saves are faster.

## Screen-visible OSD A/B

The test OSD was visible at Left=24, Top=24, Opacity=1 and intersected the primary work area in every run. Each run fed about 149 volume visual updates over 7 seconds. The requested DispatcherTimer interval was 30 Hz, while actual callback frequency was about 21.3 Hz in both sides. `ScaleXChangedFramesPerSecond` is the logical property update rate, not compositor-presented FPS. CPU-only phases have no Rendering monitor; the separate diagnostic phase adds a test-only Rendering subscriber.

Lite ABBA, final interpolation path:

- Baseline A/B: `results/formal4/lite-baseline-osd-onscreen-a/results.json`, `results/formal4/lite-baseline-osd-onscreen-b/results.json`
- Candidate A/B: `results/formal4/lite-candidate-osd-onscreen-a/results.json`, `results/formal4/lite-candidate-osd-onscreen-b/results.json`

Both sides maintained about 30 logical ScaleX changes/sec. In the two CPU-only bar-on phases, baseline used 1453/1172 ms CPU and 2.69–2.70 MB UI-thread allocation; candidate used 641/734 ms CPU and 1.99–2.00 MB allocation. This is evidence for the fixed, visible Lite scenario only. Final 77% text, `ScaleX=0.77`, animation stop, hidden state, and cleanup pass.

The final Legacy code restores the v1.20-35 WPF `DoubleAnimation` path for the complete NET48 bar block; Lite keeps the new interpolation block. Final Legacy verification: `results/formal6/legacy-candidate-restored-baseline-osd/results.json`.

That run placed the window at (24,24), visible over the primary work area. `ScaleX` changed 210 times in 6,996 ms (30.02 logical updates/sec), the input timer delivered 149 calls (21.30 Hz), and the 7-second bar-on phase used 296.9 ms CPU, within the previously measured baseline range. Final text `77%`, `ScaleX=0.77`, no pending animation, bar-off, and hidden cleanup passed. `RenderingSubscribed=null` is expected because the restored Legacy block has no such tracking field.

`formal5` holds a superseded intermediate Legacy implementation and is not the final candidate: `results/formal5/legacy-candidate-wpf-osd-c1/results.json` and `results/formal5/legacy-candidate-wpf-osd-c2/results.json` are the two intermediate candidate runs; `results/formal5/legacy-baseline-wpf-osd-b1/results.json` and `results/formal5/legacy-baseline-wpf-osd-b2/results.json` are the contemporaneous baseline runs. The intermediate candidate's mean CPU was 500 ms vs 367 ms baseline, prompting restoration of the original Legacy block. Earlier interpolation-only Legacy measurements are also diagnostic, not final: `results/formal4/legacy-candidate-osd-onscreen/results.json` and `results/formal4/legacy-candidate-osd-onscreen-b/results.json`.

## Build status and scope limits

Validation copies built with .NET 8 Lite and .NET Framework 4.8 Legacy; the final Legacy runner build succeeded with zero errors and 40 nullable warnings. The final test also rebuilt the candidate Lite/Legacy copies before the Legacy-only source update. The parent reports the final product Lite, Legacy and ARM64 builds completed with zero errors. These results use a fixed test harness and isolated storage; they do not establish full-machine application CPU behavior or compositor-presented FPS.

The root-level runner/app output discovered during cleanup was moved to `accidental-root-output/PROVENANCE.txt` with its exact path, timestamps, hashes and test-data inventory. `.workbuddy-ai/`, `动画/`, and production source files were left untouched.
