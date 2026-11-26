using System.Runtime.InteropServices;
using SonicRoute.Core;
using SonicRoute.Core.Interop;
using SonicRoute.Native.Audio;
using SonicRoute.Native.Ui;

namespace SonicRoute.Native.Product.Views;

// IAssocHandler and IEnumAssocHandlers use the same Shell association source as WPF.
// Native AOT calls the three needed ABI methods through the existing owned COM wrapper.
internal static unsafe class OpenWithChoices
{
    internal static List<ComboChoice> ForFile(string path)
    {
        var choices = new List<ComboChoice>();
        string extension = OpenWithService.NormalizeExtension(path);
        if (extension.Length == 0) return choices;
        using var apartment = new MtaScope();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int filter in new[] { AssocHandlerNative.AssocFilterRecommended, AssocHandlerNative.AssocFilterNone })
        {
            if (SHAssocEnumHandlers(extension, filter, out nint raw) < 0 || raw == 0) continue;
            using var enumerator = new Com(raw);
            var next = (delegate* unmanaged[Stdcall]<nint, uint, nint*, uint*, int>)enumerator.Slot(3);
            while (true)
            {
                nint ptr = 0;
                uint count = 0;
                int hr = next(enumerator.Handle, 1, &ptr, &count);
                if (ptr == 0) break;
                using var handler = new Com(ptr);
                if (hr < 0 || count == 0) break;
                string exe = ReadName(handler, 3);
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) || !paths.Add(exe)) continue;
                string label = ReadName(handler, 4);
                choices.Add(new(exe, string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(exe) : label,
                    AppIcon: ComboAppIcon.ForExecutable(exe)));
            }
        }
        return choices;
    }

    private static string ReadName(Com handler, int slot)
    {
        nint text = 0;
        try
        {
            var get = (delegate* unmanaged[Stdcall]<nint, nint*, int>)handler.Slot(slot);
            return get(handler.Handle, &text) >= 0 ? Marshal.PtrToStringUni(text) ?? "" : "";
        }
        finally { if (text != 0) Marshal.FreeCoTaskMem(text); }
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, out nint enumerator);
}
