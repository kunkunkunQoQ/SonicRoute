using System.Runtime.InteropServices;

namespace SonicRoute.StoreInterop;

public static class StoreActivation
{
    public static void SetTaskbarIdentity(string appUserModelId)
        => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appUserModelId));

    public static int Activate(string appUserModelId, string arguments)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            manager.ActivateApplication(appUserModelId, arguments, 0, out int processId);
            return processId;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appUserModelId);

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out int processId);
    }
}
