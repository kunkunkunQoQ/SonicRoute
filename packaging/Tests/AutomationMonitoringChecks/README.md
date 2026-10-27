# Automation and hotkey checks

Build the desired framework with `dotnet build packaging/Tests/AutomationMonitoringChecks/Checks.csproj -c Release -f net10.0-windows` (or `net8.0-windows`, `net48`). Run the matching runtime host/EXE.

`--app-input-only` focuses on current-app microphone control. It executes the linked product hotkey dispatcher and automation step executor against fake audio boundaries, checking runtime current-app selection, explicit mute/unmute versus toggle, absent/failed/canceled targets, UI readback publication, retained global-microphone and specified-app actions, stable saved action IDs, and the new shortcut's empty default binding. It creates no process children and does not write actual audio state.

`--hotkeys-only` is the existing wider hotkey/tray-dispatch check. Without either switch the existing monitoring checks also create short-lived child processes; that broader mode is separate from the focused microphone check.
