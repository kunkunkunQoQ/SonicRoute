using System.Runtime.InteropServices;

namespace SonicRoute.StoreInterop;

public static partial class StoreActivation
{
    public static void SetTaskbarIdentity(string appUserModelId)
        => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appUserModelId));

    public static unsafe int Activate(string appUserModelId, string arguments)
    {
        Guid clsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
        Guid iid = new("2E941141-7F97-4756-BA1D-9DECDE894A3D");
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 4, in iid, out nint manager));
        try
        {
            // IApplicationActivationManager::ActivateApplication, immediately after IUnknown.
            var call = (delegate* unmanaged[Stdcall]<nint, char*, char*, uint, uint*, int>)(*(nint**)manager)[3];
            uint processId = 0;
            fixed (char* id = appUserModelId, args = arguments)
                Marshal.ThrowExceptionForHR(call(manager, id, args, 0, &processId));
            return checked((int)processId);
        }
        finally { Marshal.Release(manager); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}
