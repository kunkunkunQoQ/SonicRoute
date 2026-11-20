using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace SonicRoute.WindowsLink;

internal sealed record LinkBrowseEntry(string Name, string Path, bool IsDirectory, long? Length, DateTime? ModifiedUtc);
internal sealed record LinkBrowsePage(LinkBrowseEntry[] Entries, int? NextOffset);
internal sealed record LinkBrowseApproval(Guid Id, string PeerId, string Path, bool IsDirectory, bool IsDownload);

// Pin each component while listing or sending it. Reparse points and mapped network drives are never traversed.
internal sealed class LinkBrowseDirectory : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    private static bool LocalDrive(DriveInfo drive) =>
        drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;

    internal static bool ValidPath(string? path, bool allowDrives)
    {
        if (path is "") return allowDrives;
        if (path == null || path.Length is < 3 or > 1024 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path[2] != '\\' || path.Contains('/')) return false;
        return path.Length == 3 || path[3..].Split('\\').All(WindowsLinkService.IsSafeTransferName);
    }

    internal static LinkBrowseDirectory Pin(string path, bool directory)
    {
        if (!ValidPath(path, false) || !LocalDrive(new DriveInfo(path[..3]))) throw new IOException("Choose a local disk path.");
        var result = new LinkBrowseDirectory();
        try
        {
            string component = path[..3];
            result.Open(component, true);
            CheckLocalVolume(result._handles[0]);
            if (path.Length == 3 && !directory) throw new IOException("Choose a file.");
            if (path.Length > 3)
            {
                foreach (string name in path[3..].Split('\\'))
                {
                    component = Path.Combine(component, name);
                    result.Open(component, component.Length != path.Length || directory);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void CheckLocalVolume(SafeFileHandle root)
    {
        var name = new StringBuilder(260);
        uint chars = GetFinalPathNameByHandleW(root, name, (uint)name.Capacity, 0);
        if (chars >= name.Capacity && chars <= 32768)
        {
            name.EnsureCapacity((int)chars + 1);
            chars = GetFinalPathNameByHandleW(root, name, (uint)name.Capacity, 0);
        }
        // Drive aliases can point to a network redirector. Require a resolved DOS volume on this host.
        if (chars is < 7 || chars >= name.Capacity || !name.ToString(0, 4).Equals(@"\\?\", StringComparison.Ordinal)
            || !char.IsAsciiLetter(name[4]) || name[5] != ':' || name[6] != '\\')
            throw new IOException("Choose a local disk path.");
    }

    private void Open(string path, bool directory)
    {
        // FILE_FLAG_OPEN_REPARSE_POINT inspects the entry itself. Sharing only for reads prevents renames
        // and changes to its reparse data between validation and the ordinary file/archive sender.
        var handle = CreateFileW(path, 0x81 /* READ_ATTRIBUTES | READ_DATA (LIST_DIRECTORY for directories) */, FileShare.Read, 0, FileMode.Open,
            0x02200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, 0);
        _handles.Add(handle);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8)
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException("This item is unavailable for sharing.");
    }

    internal sealed class Cursor : IDisposable
    {
        private IEnumerator<LinkBrowseEntry>? _entries;
        private LinkBrowseEntry? _next;
        private string? _path;
        private int _offset;

        internal LinkBrowsePage Read(string path, int offset, CancellationToken token)
        {
            lock (this)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidPath(path, true) || offset < 0) throw new IOException("Invalid directory.");
                using var access = path.Length == 0 ? null : Pin(path, true);
                if (offset == 0)
                {
                    Reset();
                    _path = path;
                    _entries = (path.Length == 0 ? Drives() : Entries(path)).GetEnumerator();
                }
                if (_entries == null || _path != path || _offset != offset) throw new IOException("Refresh the folder before continuing.");
                var page = new List<LinkBrowseEntry>();
                int bytes = 0;
                try
                {
                    while (_next != null || _entries.MoveNext())
                    {
                        token.ThrowIfCancellationRequested();
                        var entry = _next ?? _entries.Current;
                        int cost = JsonSerializer.SerializeToUtf8Bytes(entry).Length + 1;
                        if (page.Count >= 64 || bytes + cost > LinkConnection.VideoChunk)
                        {
                            _next = entry;
                            _offset += page.Count;
                            return new(page.ToArray(), _offset);
                        }
                        _next = null;
                        bytes += cost;
                        page.Add(entry);
                    }
                    Reset();
                    return new(page.ToArray(), null);
                }
                catch { Reset(); throw; }
            }
        }

        private void Reset() { _entries?.Dispose(); _entries = null; _path = null; _next = null; _offset = 0; }
        public void Dispose() { lock (this) Reset(); }
    }

    private static IEnumerable<LinkBrowseEntry> Drives()
    {
        foreach (var drive in DriveInfo.GetDrives())
            if (LocalDrive(drive) && drive.IsReady) yield return new(drive.Name, drive.Name, true, null, null);
    }

    private static IEnumerable<LinkBrowseEntry> Entries(string path)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            if (!ValidPath(item.FullName, false)) continue;
            LinkBrowseEntry entry;
            try { entry = new(item.Name, item.FullName, item is DirectoryInfo, (item as FileInfo)?.Length, item.LastWriteTimeUtc); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            yield return entry;
        }
    }

    public void Dispose()
    {
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { internal FileAttributes Attributes; internal uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing, nint security, FileMode mode, uint flags, nint template);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
}
