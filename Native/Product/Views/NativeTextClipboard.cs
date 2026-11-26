using System.Runtime.InteropServices;

namespace SonicRoute.Native.Product;

internal static partial class NativeTextClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint GmemZeroInit = 0x0040;

    internal static bool TryWrite(nint owner, string text) =>
        TryWrite(owner, text, null, 0, 0, out _);

    internal static bool TryWrite(nint owner, string text, uint? expectedSequence, uint remoteFormat, uint cloudFormat, out uint sequence)
    {
        sequence = 0;
        if (OpenClipboard(owner) == 0) return false;
        nint memory = 0;
        nint remoteFlag = 0;
        nint cloudFlag = 0;
        try
        {
            if (expectedSequence.HasValue && GetClipboardSequenceNumber() != expectedSequence.Value) return false;
            int bytes = checked((text.Length + 1) * sizeof(char));
            memory = GlobalAlloc(GmemMoveable | GmemZeroInit, (nuint)bytes);
            if (memory == 0) return false;
            nint pointer = GlobalLock(memory);
            if (pointer == 0) return false;
            try
            {
                char[] chars = text.ToCharArray();
                Marshal.Copy(chars, 0, pointer, chars.Length);
                Marshal.WriteInt16(pointer, chars.Length * sizeof(char), 0);
            }
            finally { GlobalUnlock(memory); }

            // Write anti-loop and cloud-exclusion markers matching WPF PhoneClipboardSync.
            if (remoteFormat != 0) remoteFlag = GlobalAlloc(GmemMoveable | GmemZeroInit, 4);
            if (cloudFormat != 0) cloudFlag = GlobalAlloc(GmemMoveable | GmemZeroInit, 4);

            if (EmptyClipboard() == 0) return false;
            if (remoteFormat != 0 && remoteFlag != 0)
            {
                if (SetClipboardData(remoteFormat, remoteFlag) != 0) remoteFlag = 0;
            }
            if (cloudFormat != 0 && cloudFlag != 0)
            {
                if (SetClipboardData(cloudFormat, cloudFlag) != 0) cloudFlag = 0;
            }
            if (SetClipboardData(CfUnicodeText, memory) == 0) return false;
            memory = 0; // Windows owns the movable block after successful transfer.
            sequence = GetClipboardSequenceNumber();
            return true;
        }
        finally
        {
            if (memory != 0) GlobalFree(memory);
            if (remoteFlag != 0) GlobalFree(remoteFlag);
            if (cloudFlag != 0) GlobalFree(cloudFlag);
            CloseClipboard();
        }
    }

    [LibraryImport("user32", EntryPoint = "OpenClipboard", SetLastError = true)] private static partial int OpenClipboard(nint owner);
    [LibraryImport("user32", EntryPoint = "CloseClipboard")] private static partial int CloseClipboard();
    [LibraryImport("user32", EntryPoint = "EmptyClipboard", SetLastError = true)] private static partial int EmptyClipboard();
    [LibraryImport("user32", EntryPoint = "SetClipboardData", SetLastError = true)] private static partial nint SetClipboardData(uint format, nint memory);
    [LibraryImport("user32", EntryPoint = "GetClipboardSequenceNumber")] private static partial uint GetClipboardSequenceNumber();
    [LibraryImport("kernel32", EntryPoint = "GlobalAlloc", SetLastError = true)] private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32", EntryPoint = "GlobalFree", SetLastError = true)] private static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32", EntryPoint = "GlobalLock", SetLastError = true)] private static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32", EntryPoint = "GlobalUnlock", SetLastError = true)] private static partial int GlobalUnlock(nint memory);
}
