using System;
using System.Collections.Generic;

namespace SonicRoute.Core.Models
{
    // Display order only. Persisted action numbers and execution semantics remain in AutoRuleAction.
    public sealed class AutoActionDisplayGroup
    {
        public string Key { get; }
        public IReadOnlyList<AutoRuleAction> Actions { get; }
        internal AutoActionDisplayGroup(string key, params AutoRuleAction[] actions)
        { Key = key; Actions = Array.AsReadOnly(actions); }
    }

    public static class AutoActionDisplay
    {
        public static IReadOnlyList<AutoActionDisplayGroup> Groups { get; } = Array.AsReadOnly(new[]
        {
            new AutoActionDisplayGroup("Auto.GroupSystem", AutoRuleAction.SetSystemOutput, AutoRuleAction.SetSystemInput,
                AutoRuleAction.SetSystemVolume, AutoRuleAction.AdjustSystemVolume, AutoRuleAction.SetSystemMute,
                AutoRuleAction.ToggleSystemMute, AutoRuleAction.SetPlaybackDeviceVolume, AutoRuleAction.AdjustPlaybackDeviceVolume,
                AutoRuleAction.SetPlaybackDeviceMute, AutoRuleAction.TogglePlaybackDeviceMute),
            new AutoActionDisplayGroup("Auto.GroupApp", AutoRuleAction.SetAppOutput, AutoRuleAction.SetAppInput,
                AutoRuleAction.SetAppVolume, AutoRuleAction.AdjustAppVolume, AutoRuleAction.SetAppMute, AutoRuleAction.ToggleAppMute,
                AutoRuleAction.SetAppInputMute, AutoRuleAction.ToggleAppInputMute,
                AutoRuleAction.SetCurrentAppInputMute, AutoRuleAction.ToggleCurrentAppInputMute,
                AutoRuleAction.SetAllAppsVolume, AutoRuleAction.AdjustAllAppsVolume,
                AutoRuleAction.SetAllAppsMute, AutoRuleAction.ToggleAllAppsMute, AutoRuleAction.ResetAllAppRouting),
            new AutoActionDisplayGroup("Auto.GroupMic", AutoRuleAction.SetMicrophoneMute, AutoRuleAction.ToggleMicrophoneMute,
                AutoRuleAction.SetGlobalMicMute, AutoRuleAction.ToggleGlobalMicMute,
                AutoRuleAction.SetDefaultInputVolume, AutoRuleAction.AdjustDefaultInputVolume,
                AutoRuleAction.SetDefaultInputMute, AutoRuleAction.ToggleDefaultInputMute),
            new AutoActionDisplayGroup("Auto.GroupProgram", AutoRuleAction.LaunchProgram, AutoRuleAction.RunPowerShell),
            new AutoActionDisplayGroup("Auto.GroupAutomation", AutoRuleAction.ExecuteRule),
            new AutoActionDisplayGroup("Auto.GroupNotice", AutoRuleAction.ShowOsd, AutoRuleAction.ShowQuickPanel, AutoRuleAction.HideQuickPanel)
        });
    }
}
