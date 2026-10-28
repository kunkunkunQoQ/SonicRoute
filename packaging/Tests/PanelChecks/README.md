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

`--overview-mute <image-directory>` checks the relocated playback/capture mute
switches in the actual product BAML. It covers nine languages, light/dark themes,
920/780-DIP widths, shared switch styling, current-PID readback, microphone-option
hiding, absent recording sessions, no Click on programmatic updates, close guards,
and the independent unbound app-microphone hotkey entry. Explicit mute writes use
only managed fake sessions for missing PIDs; capture verification subsequently
uses native read-only discovery. No windows are shown and no real audio is written.

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

`--overview-mute <absolute-image-directory>` checks the overview mute controls,
current-app state/readback and the related hotkey/settings list. It checks the
card padding, label and knob's visual hit route, the non-interactive shared
switch visual, and calls the actual ToggleButton click path for playback mute
and unmute. Each click must write once to the selected fake session and leave
the switch enabled after readback. It shows no window, injects no physical
input, and writes only managed fake audio sessions.
