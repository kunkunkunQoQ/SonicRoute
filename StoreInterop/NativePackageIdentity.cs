using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;

namespace SonicRoute.StoreInterop;

public static class NativePackageIdentity
{
    private const int InsufficientBuffer = 122;
    private const int NoPackage = 15700;
    private const int NoApplication = 15703;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetCurrentApplicationUserModelId(ref uint length, StringBuilder? buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? buffer);

    public static bool IsPackaged()
    {
        uint length = 0;
        int error = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return InterpretResult(error, length);
    }

    internal static bool InterpretResult(int error, uint length)
    {
        if (error == NoPackage) return false;
        if (error == InsufficientBuffer && length > 0) return true;
        // Detection failures must never turn a Store startup change into a Run-key write.
        throw new Win32Exception(error, "Unable to determine SonicRoute package identity.");
    }

    public static string GetApplicationUserModelId()
    {
        uint length = 0;
        int error = GetCurrentApplicationUserModelId(ref length, null);
        if (error == NoApplication)
        {
            // A direct executable launch can carry only package identity. Both Store
            // manifests register SonicRoute.exe as the single application, "App".
            length = 0;
            error = GetCurrentPackageFamilyName(ref length, null);
            if (error != InsufficientBuffer) throw new Win32Exception(error);
            var family = new StringBuilder((int)length);
            error = GetCurrentPackageFamilyName(ref length, family);
            if (error != 0) throw new Win32Exception(error);
            return family + "!App";
        }

        if (error != InsufficientBuffer) throw new Win32Exception(error);
        var id = new StringBuilder((int)length);
        error = GetCurrentApplicationUserModelId(ref length, id);
        if (error != 0) throw new Win32Exception(error);
        return id.ToString();
    }
}
