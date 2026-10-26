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
