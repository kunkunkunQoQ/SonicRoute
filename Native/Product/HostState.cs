using System.Text.Json.Serialization;
using SonicRoute.Core.Models;

namespace SonicRoute.Native.Product;

internal interface IMainThread
{
    bool TryPost(Action work);
    IDisposable ScheduleOnce(TimeSpan delay, Action work);
}

internal enum FeedbackKind { OpenMain, TogglePanel, HidePanel, ShowPanel, TrayMenu, Osd, SettingsWarning }
internal sealed record HostFeedback(long Generation, FeedbackKind Kind, string Title, string Text);

// Optional views pull the latest state on attachment. Audio writes and rules belong to the host.
internal sealed class FeedbackState
{
    internal HostFeedback? Last { get; private set; }
    internal event Action<HostFeedback>? Changed;
    internal void Publish(FeedbackKind kind, string title = "", string text = "")
    {
        Last = new((Last?.Generation ?? 0) + 1, kind, title, text);
        Changed?.Invoke(Last);
    }
}

internal sealed record HostStatus(int Pid, string Edition, bool Packaged, bool NativeAot,
    bool ReflectionJson, string DataDirectory, long Window, bool TrayRegistered, long Messages, long TrayRegistrations, long Dispatches, int Deadlines, long TimerFires, int Hotkeys,
    long SystemEvents, string LastSignal, string Language, HostFeedback? Feedback,
    long Allocated, long ManagedHeap, long LastGcHeap, int Gen0, int Gen1, int Gen2, long Generation);
internal sealed record ControlRequest(string Verb, string[] Args);

[JsonSerializable(typeof(HostStatus))]
[JsonSerializable(typeof(ControlRequest))]
internal partial class HostJson : JsonSerializerContext { }
