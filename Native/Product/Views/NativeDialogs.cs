using System.Runtime.InteropServices;

namespace SonicRoute.Native.Product;

internal static unsafe class NativeDialogs
{
    internal static string? Open(nint owner, string filter, string? title = null) => FileDialog(owner, filter, false, "", title);
    internal static string? Save(nint owner, string filter, string defaultExtension, string? title = null) => FileDialog(owner, filter, true, defaultExtension, title);

    internal static string[] OpenMultiple(nint owner, string filter, string? title = null)
    {
        var buffer = new char[65536];
        string spec = filter.Replace('|', '\0') + "\0\0";
        fixed (char* name = buffer, filters = spec, pTitle = title)
        {
            var request = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName), Owner = owner, Filter = filters, File = name,
                MaxFile = (uint)buffer.Length, Title = pTitle,
                Flags = 0x00080000u /*OFN_EXPLORER*/ | 0x00001000u /*OFN_FILEMUSTEXIST*/ | 0x00000200u /*OFN_ALLOWMULTISELECT*/ | 0x00000008u /*OFN_NOCHANGEDIR*/
            };
            if (GetOpenFileNameW(ref request) == 0) return [];
            int i = 0;
            while (i < buffer.Length && buffer[i] != '\0') i++;
            if (i == 0) return [];
            string first = new string(buffer, 0, i);
            i++;
            if (i >= buffer.Length || buffer[i] == '\0') return [first];
            var results = new List<string>();
            while (i < buffer.Length && buffer[i] != '\0')
            {
                int start = i;
                while (i < buffer.Length && buffer[i] != '\0') i++;
                string file = new string(buffer, start, i - start);
                results.Add(Path.Combine(first, file));
                i++;
            }
            return results.ToArray();
        }
    }

    internal static string? BrowseFolder(nint owner, string title, string? initialPath = null)
    {
        var clsid = new Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
        var iid = new Guid("d57c7288-d4ad-4768-be02-9d969532d960"); // IFileOpenDialog
        if (CoCreateInstance(ref clsid, 0, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out nint dlg) < 0 || dlg == 0)
            return null;
        try
        {
            // IFileDialog slots: GetOptions(10), SetOptions(9), SetTitle(17), Show(3), GetResult(20)
            uint opts = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint*, int>)(*((void***)dlg))[10])(dlg, &opts);
            opts |= 0x00000020u /*FOS_PICKFOLDERS*/ | 0x00000040u /*FOS_FORCEFILESYSTEM*/ | 0x00000008u /*FOS_NOCHANGEDIR*/;
            ((delegate* unmanaged[Stdcall]<nint, uint, int>)(*((void***)dlg))[9])(dlg, opts);
            if (!string.IsNullOrEmpty(title))
            {
                fixed (char* pTitle = title)
                    ((delegate* unmanaged[Stdcall]<nint, char*, int>)(*((void***)dlg))[17])(dlg, pTitle);
            }
            if (((delegate* unmanaged[Stdcall]<nint, nint, int>)(*((void***)dlg))[3])(dlg, owner) != 0)
                return null;
            nint item = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*((void***)dlg))[20])(dlg, &item) != 0 || item == 0)
                return null;
            try
            {
                // IShellItem::GetDisplayName(slot 5, SIGDN_FILESYSPATH = 0x80058000)
                nint pszName = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*((void***)item))[5])(item, 0x80058000u, &pszName) == 0 && pszName != 0)
                {
                    try { return Marshal.PtrToStringUni(pszName); }
                    finally { Marshal.FreeCoTaskMem(pszName); }
                }
                return null;
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*((void***)item))[2])(item); }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*((void***)dlg))[2])(dlg);
        }
    }

    private static string? FileDialog(nint owner, string filter, bool save, string extension, string? title)
    {
        var buffer = new char[32768];
        string spec = filter.Replace('|', '\0') + "\0\0";
        fixed (char* name = buffer, filters = spec, suffix = extension, pTitle = title)
        {
            var request = new OpenFileName
            {
                Size = (uint)sizeof(OpenFileName), Owner = owner, Filter = filters, File = name,
                MaxFile = (uint)buffer.Length, DefaultExtension = suffix, Title = pTitle,
                Flags = 0x00080000u | 0x00001000u | 0x00000008u | (save ? 0x00000006u : 0x00000800u)
            };
            int ok = save ? GetSaveFileNameW(ref request) : GetOpenFileNameW(ref request);
            return ok == 0 ? null : new string(name);
        }
    }

    internal static void ShowWarning(nint owner, string title, string message) => MessageBoxW(owner, message, title, 0x30);

    internal static void ShowError(nint owner, string title, string message) => MessageBoxW(owner, message, title, 0x10);

    internal static bool ConfirmImport(nint owner, string title, string message)
        => MessageBoxW(owner, message, title, 0x41) == 1; // MB_OKCANCEL | MB_ICONINFORMATION

    internal static bool Confirm(nint owner, string title, string message)
        => MessageBoxW(owner, message, title, 0x00000124) == 6;

    [StructLayout(LayoutKind.Sequential)] private struct OpenFileName
    {
        public uint Size;
        public nint Owner, Instance;
        public char* Filter, CustomFilter;
        public uint MaxCustomFilter, FilterIndex;
        public char* File;
        public uint MaxFile;
        public char* FileTitle;
        public uint MaxFileTitle;
        public char* InitialDirectory, Title;
        public uint Flags;
        public ushort FileOffset, ExtensionOffset;
        public char* DefaultExtension;
        public nint Data, Hook, TemplateName, Reserved;
        public uint Reserved2, ExtendedFlags;
    }
    [DllImport("comdlg32.dll")] private static extern int GetOpenFileNameW(ref OpenFileName request);
    [DllImport("comdlg32.dll")] private static extern int GetSaveFileNameW(ref OpenFileName request);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid rclsid, nint pUnkOuter, uint dwClsContext, ref Guid riid, out nint ppv);
}
