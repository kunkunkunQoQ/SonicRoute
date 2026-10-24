namespace SonicRoute;
internal sealed class ForegroundChangeSource : IDisposable
{
    internal bool IsInstalled => true;
    internal ForegroundChangeSource(Action changed) { }
    internal bool TryRegister() => true;
    public void Dispose() { }
}
internal static class ForegroundAppService
{
    internal static int GetForegroundProcessId() => 0;
    internal static string? GetProcessNameSafe(int pid) => null;
}
internal sealed class ProcessChangeSource : IDisposable
{
    internal static bool AvailableOnStart = true;
    internal static int Created;
    private readonly Action<string?> _changed;
    internal bool IsAvailable, Disposed;
    internal Task Startup { get; private set; } = Task.CompletedTask;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    internal TaskCompletionSource<bool>? DisposalGate;
    internal ProcessChangeSource(Action<string?> changed) { _changed = changed; Created++; }
    internal Task StartAsync() { IsAvailable = AvailableOnStart; _changed(null); return Startup; }
    internal void Emit(string? name) { if (!Disposed) _changed(name); }
    internal void EmitRetired() => _changed(null);
    public void Dispose() { Disposed = true; IsAvailable = false; Completion = DisposalGate?.Task ?? Task.CompletedTask; }
}
