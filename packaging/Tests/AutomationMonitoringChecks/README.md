# Automation and hotkey checks

Build the desired framework with `dotnet build packaging/Tests/AutomationMonitoringChecks/Checks.csproj -c Release -f net10.0-windows` (or `net8.0-windows`, `net48`). Run the matching runtime host/EXE.

`--app-input-only` focuses on current-app microphone automation and shortcut removal. It executes the linked product hotkey dispatcher and automation step executor against fake audio boundaries, checking runtime current-app selection, explicit mute/unmute versus toggle, absent/failed/canceled targets, UI readback publication, retained global-microphone and specified-app actions, stable saved action IDs, and the retired app-microphone shortcut's inert dispatch. Playback and global microphone defaults remain. It creates no process children and does not write actual audio state.

`--mute-removal-only` checks restored playback mute dispatch/defaults and the
retired app-microphone action's absence from the action list, groups and defaults.
It retains checks for the remaining hotkey dispatcher and skips tray-wheel
scenarios. Use `--app-input-only` for the retired action's inert-dispatch and
preserved microphone automation checks.

`--hotkeys-only` is the existing wider hotkey/tray-dispatch check. Without a focused switch the existing monitoring checks also create short-lived child processes; that broader mode is separate from the focused microphone check.
