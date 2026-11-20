using System.Reflection;
using System.Windows.Input;
using SonicRoute;

internal static partial class Program
{
    private static void CheckPlaybackRange()
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var select = typeof(QuickPanelModernWindow).GetMethod("SelectPlaybackRange", statics)!
            .CreateDelegate<Func<string[], HashSet<string>, string, string?, ModifierKeys, string[]>>();
        string[] order = ["System", "Headset", "Speakers", "Phone"];
        string[] Pick(string[] selected, string clicked, string? anchor, ModifierKeys modifiers) =>
            select(order, new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase), clicked, anchor, modifiers);
        Check(Pick(["System"], "Phone", null, ModifierKeys.Control).SequenceEqual(["System", "Phone"])
            && Pick(["System", "Phone"], "System", "Phone", ModifierKeys.Control).SequenceEqual(["Phone"])
            && Pick(["Headset", "Phone"], "System", "Phone", ModifierKeys.None).SequenceEqual(["System"]),
            "Ctrl toggles only the clicked output and a plain click replaces the selection");
        Check(Pick(["System", "Phone"], "Headset", "Phone", ModifierKeys.Shift).SequenceEqual(["Headset", "Speakers", "Phone"])
            && Pick(["Headset"], "Phone", null, ModifierKeys.Shift).SequenceEqual(["Headset", "Speakers", "Phone"]),
            "Shift replaces the selection with the full inclusive range in either direction");
        Check(Pick(["System", "Speakers"], "Phone", "Speakers", ModifierKeys.Control | ModifierKeys.Shift)
                .SequenceEqual(["System", "Speakers", "Phone"]),
            "Ctrl+Shift adds the contiguous range and preserves previously selected outputs outside it");
    }
}
