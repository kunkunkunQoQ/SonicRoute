using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Monitoring;

public readonly record struct ProcessTrace(bool Started, uint ProcessId, string ProcessName);

// All interfaces belong to the MTA. WMI owns its callback reference until cancellation.
// Consumers must return promptly without synchronously waiting for unsubscription; no private worker, queue or sampling timer is needed.
public sealed partial class ProcessTraceSubscription : IAsyncDisposable
{
    private nint _services, _mtaCookie;
    private readonly List<(ProcessTraceSink Sink, nint Identity)> _sinks = new(2);
    private readonly object _closeLock = new();
    private Task? _closing;

    private ProcessTraceSubscription() { }

    public static async Task<ProcessTraceSubscription> StartAsync(Action<ProcessTrace> changed, Action<int> stopped,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(stopped);
        cancellation.ThrowIfCancellationRequested();
        var registration = Task.Run(() => Start(e => { if (!cancellation.IsCancellationRequested) changed(e); },
            hr => { if (!cancellation.IsCancellationRequested) stopped(hr); }));
        try { return await registration.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _ = ReleaseLateRegistration(registration);
            throw;
        }
    }

    private static async Task ReleaseLateRegistration(Task<ProcessTraceSubscription> registration)
    {
        try { await (await registration.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Canceled WMI registration: {0:X8}", error.HResult); }
    }

    private static unsafe ProcessTraceSubscription Start(Action<ProcessTrace> changed, Action<int> stopped)
    {
        var owner = new ProcessTraceSubscription();
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        nint locator = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out owner._mtaCookie));
            Guid clsid = new("4590F811-1D3A-11D0-891F-00AA004B2E24");
            Guid iid = new("DC12A687-737F-11CF-884D-00AA004B2E24");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out locator));
            using var ns = new OwnedBstr(@"ROOT\CIMV2");
            var connect = (delegate* unmanaged[Stdcall]<nint,nint,nint,nint,nint,int,nint,nint,nint*,int>)Slot(locator, 3);
            nint services = 0;
            Marshal.ThrowExceptionForHR(connect(locator, ns.Value, 0, 0, 0, 0x80 /* WBEM_FLAG_CONNECT_USE_MAX_WAIT */, 0, 0, &services));
            owner._services = services;
            // Local DCOM with authentication and impersonation; preserve the current user's token.
            Marshal.ThrowExceptionForHR(CoSetProxyBlanket(services, 10, 0, 0, 3, 3, 0, 0));
            owner.Subscribe(true, changed, stopped);
            owner.Subscribe(false, changed, stopped);
            return owner;
        }
        catch { owner.CloseOnMta(); throw; }
        finally { if (locator != 0) Marshal.Release(locator); CoUninitialize(); }
    }

    private unsafe void Subscribe(bool start, Action<ProcessTrace> changed, Action<int> stopped)
    {
        var sink = new ProcessTraceSink(start, changed, stopped);
        nint pointer = (nint)ComInterfaceMarshaller<IProcessTraceSink>.ConvertToUnmanaged(sink);
        using var language = new OwnedBstr("WQL");
        using var query = new OwnedBstr("SELECT * FROM Win32_Process" + (start ? "Start" : "Stop") + "Trace");
        var sub = (delegate* unmanaged[Stdcall]<nint,nint,nint,int,nint,nint,int>)Slot(_services, 23);
        try { Marshal.ThrowExceptionForHR(sub(_services, language.Value, query.Value, 0, 0, pointer)); }
        catch { sink.Stop(); ComInterfaceMarshaller<IProcessTraceSink>.Free((void*)pointer); throw; }
        _sinks.Add((sink, pointer));
    }

    public ValueTask DisposeAsync()
    {
        lock (_closeLock)
        {
            _closing ??= Task.Run(() =>
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
                try { CloseOnMta(); }
                finally { CoUninitialize(); }
            });
            return new ValueTask(_closing);
        }
    }

    private unsafe void CloseOnMta()
    {
        // Deactivate both managed targets before making an outgoing RPC that can reenter.
        foreach (var (sink, _) in _sinks) sink.Stop();
        int firstError = 0;
        foreach (var (_, identity) in _sinks)
        {
            var cancel = (delegate* unmanaged[Stdcall]<nint,nint,int>)Slot(_services, 4);
            int hr = cancel(_services, identity);
            if (hr < 0 && firstError == 0) firstError = hr;
            ComInterfaceMarshaller<IProcessTraceSink>.Free((void*)identity);
        }
        _sinks.Clear();
        if (_services != 0) { Marshal.Release(_services); _services = 0; }
        if (_mtaCookie != 0) { CoDecrementMTAUsage(_mtaCookie); _mtaCookie = 0; }
        Marshal.ThrowExceptionForHR(firstError);
    }

    internal static unsafe nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private sealed class OwnedBstr(string text) : IDisposable
    {
        internal nint Value { get; } = Marshal.StringToBSTR(text);
        public void Dispose() => Marshal.FreeBSTR(Value);
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoIncrementMTAUsage(out nint cookie);
    [LibraryImport("ole32.dll")] private static partial int CoDecrementMTAUsage(nint cookie);
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("ole32.dll")] private static partial int CoSetProxyBlanket(nint proxy, uint authn, uint authz, nint principal, uint level, uint impersonation, nint identity, uint caps);
}

[GeneratedComInterface, Guid("7C857801-7381-11CF-884D-00AA004B2E24")]
internal partial interface IProcessTraceSink
{
    [PreserveSig] int Indicate(int count, nint objects);
    [PreserveSig] int SetStatus(int flags, int result, nint message, nint obj);
}

[GeneratedComClass]
internal sealed partial class ProcessTraceSink(bool start, Action<ProcessTrace> changed, Action<int> stopped) : IProcessTraceSink
{
    private readonly object _sync = new();
    private bool _closed;

    internal void Stop() { lock (_sync) _closed = true; }

    public unsafe int Indicate(int count, nint objects)
    {
        try
        {
            lock (_sync)
            {
                if (_closed) return 0;
                for (int i = 0; i < count; i++)
                {
                    nint obj = ((nint*)objects)[i];
                    if (obj == 0) continue;
                    var get = (delegate* unmanaged[Stdcall]<nint,char*,int,Variant*,nint,nint,int>)ProcessTraceSubscription.Slot(obj, 4);
                    Variant name = default, pid = default;
                    try
                    {
                        fixed (char* keyName = "ProcessName", keyPid = "ProcessID")
                        {
                            if (get(obj,keyName,0,&name,0,0) < 0 || name.Type != 8 || name.Value == 0 ||
                                get(obj,keyPid,0,&pid,0,0) < 0 || (pid.Type != 3 && pid.Type != 19)) continue;
                        }
                        changed(new ProcessTrace(start, unchecked((uint)pid.Value), Marshal.PtrToStringBSTR(name.Value)));
                    }
                    finally { VariantClear(ref name); VariantClear(ref pid); }
                }
            }
            return 0;
        }
        catch (Exception e) { return e.HResult; }
    }

    public int SetStatus(int flags, int result, nint message, nint obj)
    {
        try { if (flags == 0) lock (_sync) { if (!_closed) stopped(result); } return 0; }
        catch (Exception e) { return e.HResult; }
    }

    // x64 and ARM64 VARIANT contain a 16-byte union after the 8-byte type header.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Value;
    }
    [LibraryImport("oleaut32.dll")] private static partial int VariantClear(ref Variant variant);
}