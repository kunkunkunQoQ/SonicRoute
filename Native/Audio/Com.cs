using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace SonicRoute.Native.Audio;

internal static unsafe partial class NativeMethods
{
    [LibraryImport("combase.dll")]
    internal static partial int RoInitialize(uint mode);
    [LibraryImport("combase.dll")]
    internal static partial void RoUninitialize();
    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);
    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(PropVariant* variant);
    [LibraryImport("combase.dll")]
    internal static partial int WindowsCreateString(char* value, uint length, nint* result);
    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(nint value);
    [LibraryImport("combase.dll")]
    internal static partial char* WindowsGetStringRawBuffer(nint value, uint* length);
    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(nint name, Guid* iid, nint* value);
    [LibraryImport("mmdevapi.dll")]
    internal static partial int ActivateAudioInterfaceAsync(char* path, Guid* iid, PropVariant* parameters, nint callback, nint* operation);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] internal ushort Type;
    [FieldOffset(8)] internal nint Value;
    [FieldOffset(8)] internal uint BlobSize;
    [FieldOffset(16)] internal nint Blob;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    internal Guid Format;
    internal uint Id;
}

internal sealed unsafe class Com : IDisposable
{
    private nint _handle;
    private static int _owners;
    internal static int Owners => Volatile.Read(ref _owners);
    internal nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(Com));
    internal Com(nint owned)
    {
        if (owned == 0) throw new ArgumentNullException(nameof(owned));
        _handle = owned;
        Interlocked.Increment(ref _owners);
    }

    internal static void Check(int hr, string operation)
    {
        if (hr < 0) throw new COMException($"{operation} failed (0x{hr:X8})", hr);
    }
    internal static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    internal nint Slot(int index) => Slot(Handle, index);
    internal static Com Create(Guid clsid, Guid iid)
    {
        nint result = 0;
        Check(NativeMethods.CoCreateInstance(&clsid, 0, 1, &iid, &result), "CoCreateInstance");
        return new Com(result);
    }
    internal Com Query(Guid iid)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(0))(Handle, &iid, &result), "QueryInterface");
        return new Com(result);
    }
    internal uint UInt32(int slot)
    {
        uint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(slot))(Handle, &value), "read integer");
        return value;
    }
    internal float Scalar(int slot)
    {
        float value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, float*, int>)Slot(slot))(Handle, &value), "read scalar");
        return value;
    }
    internal Com Service(int slot, Guid iid)
    {
        nint value = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(slot))(Handle, &iid, &value), "GetService");
        return new Com(value);
    }
    internal int Call(int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot))(Handle);
    internal static void Release(nint value)
    {
        if (value != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(value, 2))(value);
    }
    public void Dispose()
    {
        nint owned = Interlocked.Exchange(ref _handle, 0);
        if (owned == 0) return;
        Release(owned);
        Interlocked.Decrement(ref _owners);
    }
}

internal sealed class MtaScope : IDisposable
{
    internal MtaScope() => Com.Check(NativeMethods.RoInitialize(1), "RoInitialize MTA");
    public void Dispose() => NativeMethods.RoUninitialize();
}

internal sealed unsafe class HString : IDisposable
{
    private nint _handle;
    internal nint Handle => _handle;
    internal HString(string? value)
    {
        if (value == null) return;
        nint handle = 0;
        fixed (char* chars = value)
            Com.Check(NativeMethods.WindowsCreateString(chars, (uint)value.Length, &handle), "WindowsCreateString");
        _handle = handle;
    }
    public void Dispose() => NativeMethods.WindowsDeleteString(Interlocked.Exchange(ref _handle, 0));
}
