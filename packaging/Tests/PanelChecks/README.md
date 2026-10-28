# Remaining quick panel and removed-panel compatibility checks

This harness loads the actual product MainWindow/QuickPanelModernWindow BAML and
the product App resource declarations. It suppresses product Application startup
and exit, does not show windows or register global input, and uses a unique data
directory. Audio calls in row construction are reads for deliberately missing PIDs.

It checks removed types/BAML, old config compatibility, deferred settings/theme
creation and release, light/dark resources, case-insensitive/null hidden lists,
existing row reuse, batch/single-app mute visuals, shared device name projection,
default/missing device selection, selection-event suppression, and absence of
queued volume writes.
It also checks custom device headings with unchanged lower descriptions, native
vertical/horizontal scrollbar templates, routed drag/wheel/page handling, native
range boundaries, shared frozen resources and embedded popup/menu/textbox styles.
The contrast branch is simulated without changing Windows high contrast settings.
It does not measure whole-app performance, physical DPI/input or endpoint writes.
`--render-scrollbars <directory>` renders actual WPF controls offscreen at 96 DPI;
this is distinct from a visible-window or physical DPI/input check.
`--measure-sections --device-count=8` is a narrow unshown row construction/update
probe, not a CPU/working-set or first-visible-window benchmark.

`--app-scroll-layout <image-directory>` checks the actual application rows in
light/dark themes before and after vertical overflow. It verifies stable viewport,
slider width and expand-arrow position, the shared native scrollbar in the right
gutter, scrolling to the end, collapse recovery and absence of queued audio writes.
It renders the unshown product panel and uses only missing-PID audio reads.

`--app-row-names <image-directory>` checks the theme setting directly below the
peak-meter option, the peer classic-layout switch above it, saved-value/deferred-page migration, nine language labels,
application/device-name modes without experimental mode, custom/default/missing
device labels, unchanged-row reuse, name-above-slider layout, name-area hit
testing/native drag mapping, the retained thumb
and current-application theme highlight, classic layout geometry, hidden name
settings with preserved choices, the independent compact device-name child and
its defaults, and the scroll-gutter regression in all three display states.
It renders light/dark product controls
offscreen. Playback state uses managed read-only fixtures; route reads target
missing PIDs, and no real audio is written or capture state queried.

`--overview-mute <image-directory>` checks the overview playback mute button in
the actual product BAML. It covers nine languages, light/dark themes, 920/780-DIP
widths, reuse of QuickPanelToolButton, placement left of the slider, removal of
the device-row switches and plus/minus buttons, current-PID readback and Click,
ignored-write failure handling, no capture-state work, close guards and the
related hotkey/settings list. Writes use managed fake playback sessions only;
no windows are shown and no real audio is written.

Build each runtime separately (default NET10):

```powershell
dotnet build packaging/Tests/PanelChecks/Checks.csproj -c Release -p:PanelTestFramework=net8.0-windows
```

Use `net10.0-windows`, `net8.0-windows`, or `net48`. The custom property is needed
for restore as well as compilation. Lite and Legacy share the product package ID;
restoring both references in one multi-target graph can omit the executable from
the test runtime dependency manifest. Run each DLL with its matching installed
runtime host, or `PanelRemovalChecks.exe` for Framework.

`--tray-hit-only` checks the actual product tray identity and Shell rectangle path
on the current Windows desktop. It briefly adds its own temporary notification
icon, checks real hit boundaries, removal, 30-second fallback expiry and recovery,
then disposes the icon and service. It does not install a hook, inject input or
write audio state. This optional check requires a running Explorer notification
area; it does not validate physical mouse input, taskbar relocation or mixed DPI.

`--output-volumes <image-directory>` limits execution to the additional output
volume panel. It checks corner placement and constrained space, old config and
saved expansion mode, deferred controls, independent device targets, merged
pending slider changes, stale readback, device removal and collapse cleanup.
It renders the actual product controls in light/dark themes and nine languages.
No windows are shown and no queued audio writes are executed in this mode.

`Run-OutputPopupChecks.ps1 -Framework net10.0-windows` runs the optional native
popup check on a newly created private desktop, without switching the input
desktop. Build the matching test framework first. Use `net8.0-windows` or `net48`
for the other runtimes; `-DotNetHost` selects the matching modern runtime host,
and `-OutputDirectory` selects the log/image directory. The check shows its own
product panels, reads real audio state, verifies all four corners and unchanged
owner coordinates/height, focus, collapse/reopen and close disposal. It injects
no mouse/keyboard input and never adjusts real audio. Physical input, mixed-DPI
monitor transitions, Win10 and native ARM64 remain separate manual checks.

`--output-followup <image-directory>` checks the themed expand button, absence
of the extra close button, the independent retained-output setting directly
below the default-device switch, config defaults/serialization, retained-device
filtering, deferred control cleanup, and light/dark layouts in nine languages.
Add `-Followup` to `Run-OutputPopupChecks.ps1` for its focused native counterpart:
loaded settings events and persisted values, height caps at two main-panel
sizes, empty/single/all retained devices, subscription removal, and the original
main volume control remaining available when its device is filtered out.
This native variant uses one corner, reusing the existing positioning path.
It also checks synchronous cached device rows on reopen, feature-scoped
cancellation of queued rendering and pending reads, preservation of the main
panel lifetime, rapid collapse/reopen, and retired subscription disposal.

`--output-optimization` checks the actual product controls in light and dark
themes, defers targeted follow-up reads for notification changes until volume or
mute writes settle, protects newer rows from stale reads, reuses repeated
state resource references, and avoids rebinding an unchanged device list. It
does not show a window, requires no real audio endpoint IDs, and does not write
real audio state.
`Run-OutputPopupChecks.ps1 -Optimization` runs the focused native variant on an
isolated private desktop. It checks placement in all four corners, coalescing
100 placement requests, cached coordinates, simulated screen/DPI invalidation,
native owner movement alignment, separate recovery paths for endpoint-volume
and device notifications, retained-device filtering, height limits, quick
collapse/reopen, and resource disposal.

`--app-cards <absolute-image-directory>` limits execution to the application page.
It checks the actual BAML in light/dark themes, nine languages at three widths,
positive quick-panel visibility, independent top-left hidden and right-side
activity/mute markers, cached readback for unselected apps, the shared
input/output card and microphone collapse, pencil-triggered inline rename/save
and focus-loss behavior, readback without Click feedback,
empty selection and close cleanup. It uses missing-PID fixtures and an isolated
configuration directory; it does not show windows or write real audio state.
The saved offscreen PNGs include 96/144 raster DPI variants, not physical monitor
DPI, DWM presentation, hardware input or whole-app performance measurements.

`--overview-mute-readback` runs just the current-app overview state and click
checks. Programmatic readback must never write audio; actual Button.OnClick
toggles only the selected playback PID, verifies its readback and rejects an
ignored write. Removing capture controls must leave no capture mute reads or
writes, even with the microphone option enabled. It skips theme, language,
layout and hotkey-list checks when those paths have not changed.

`--row-loading-only` limits execution to application-row name refresh and panel
load cancellation. It covers compact/full default titles, alias and actual
default-device changes, equal aliases with different route IDs, unavailable
endpoints, application renames, one in-flight load and close/late-result guards.
The pending-load fixture allows existing read-only device discovery but never
shows a window or executes audio writes. Configuration remains isolated.

`--measure-row-names` (NET8/NET10) measures the actual name-update method against
a synthetic 65-device snapshot. Each compact/full/application-name case warms up
for 2,000 calls, then records five samples of 20,000 repeated unchanged updates.
JSON `NAME_METRIC` lines include elapsed time and current-thread allocation;
each sample includes the 40-byte Stopwatch allocation. Reflection overhead is
included. Compare the same harness against both product revisions; disabling
tiered compilation in both processes can help distinguish JIT warmup effects.
This is a local name-refresh probe, not a first-visible-load, idle CPU,
working-set or whole-app performance benchmark.
