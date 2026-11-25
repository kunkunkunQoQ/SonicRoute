using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SonicRoute.StoreInterop;

public static unsafe partial class NativePackageIdentity
{
    private const int InsufficientBuffer = 122, NoPackage = 15700, NoApplication = 15703;

    [LibraryImport("kernel32.dll")] private static partial int GetCurrentPackageFullName(ref uint length, char* buffer);
    [LibraryImport("kernel32.dll")] private static partial int GetCurrentApplicationUserModelId(ref uint length, char* buffer);
    [LibraryImport("kernel32.dll")] private static partial int GetCurrentPackageFamilyName(ref uint length, char* buffer);

    public static bool IsPackaged()
    {
        uint length = 0;
        return InterpretResult(GetCurrentPackageFullName(ref length, null), length);
    }

    internal static bool InterpretResult(int error, uint length)
    {
        if (error == NoPackage) return false;
        if (error == InsufficientBuffer && length > 0) return true;
        throw new Win32Exception(error, "Unable to determine SonicRoute package identity.");
    }

    public static string GetApplicationUserModelId()
    {
        uint length = 0;
        int error = GetCurrentApplicationUserModelId(ref length, null);
        bool familyOnly = error == NoApplication;
        if (familyOnly)
        {
            length = 0;
            error = GetCurrentPackageFamilyName(ref length, null);
        }
        if (error != InsufficientBuffer || length == 0) throw new Win32Exception(error);
        char[] buffer = new char[checked((int)length)];
        fixed (char* value = buffer)
        {
            error = familyOnly ? GetCurrentPackageFamilyName(ref length, value) : GetCurrentApplicationUserModelId(ref length, value);
            if (error != 0) throw new Win32Exception(error);
        }
        var name = new string(buffer, 0, checked((int)length - 1));
        return familyOnly ? name + "!App" : name;
    }
}
