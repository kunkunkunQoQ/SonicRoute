namespace SonicRoute.Native.Product;

internal sealed class NativeMouseBindings(NativeApplicationWindow window, bool trayWheel, bool everywhere) : IDisposable
{
    private readonly Dictionary<MouseChord, int> enabled = new();
    private readonly Dictionary<int, string> actions = new();
    internal bool IsInstalled => window.MouseRegistered;
    internal void Reload(IEnumerable<KeyValuePair<string,string>> bindings)
    {
        var next = new Dictionary<MouseChord, int>();
        var mapping = new Dictionary<int, string>();
        int id = -1;
        foreach (var pair in bindings)
            if (TryChord(pair.Key, out var chord) && next.TryAdd(chord, id)) mapping[id--] = pair.Value;
        if (!window.ConfigureMouse(next, trayWheel, everywhere)) return;
        enabled.Clear(); actions.Clear();
        foreach (var item in next) enabled.Add(item.Key, item.Value);
        foreach (var item in mapping) actions.Add(item.Key, item.Value);
    }

    private static bool TryChord(string combo,out MouseChord chord)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        chord = default;
        if (parts.Length == 0) return false;
        MouseGesture? gesture = parts[^1].ToLowerInvariant() switch
        { "mbutton" => MouseGesture.Middle,"xbutton1" => MouseGesture.Side1,"xbutton2" => MouseGesture.Side2,
          "wheelup" => MouseGesture.WheelUp,"wheeldown" => MouseGesture.WheelDown,_ => null };
        parts[^1] = "F24";
        if (gesture == null || !HotkeyService.TryParse(string.Join("+",parts), out uint mods, out _)) return false;
        chord = new(gesture.Value, mods);
        return true;
    }
    internal bool IsRegistered(string combo,string action) => IsInstalled && TryChord(combo,out var key)
        && enabled.TryGetValue(key,out int id) && actions.TryGetValue(id,out string? bound) && action == bound;
    internal bool TryGetAction(int id,out string? action) => actions.TryGetValue(id,out action);
    public void Dispose() => window.ConfigureMouse(new Dictionary<MouseChord,int>(), false, false);
}
