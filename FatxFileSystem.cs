using System.Security.AccessControl;
using DokanNet;
using FileAccess = DokanNet.FileAccess;   // avoid clash with System.IO.FileAccess

namespace FatxMount;

/// <summary>Raw NTSTATUS codes, so we don't depend on which names a given DokanNet version defines.</summary>
internal static class Nt
{
    public static readonly NtStatus BadPathName = unchecked((NtStatus)0xC0000033);   // STATUS_OBJECT_NAME_INVALID
    public static readonly NtStatus DiskFull = unchecked((NtStatus)0xC000007F);      // STATUS_DISK_FULL
    public static readonly NtStatus InternalError = unchecked((NtStatus)0xC00000E5); // STATUS_INTERNAL_ERROR
}

/// <summary>Bridges Dokan callbacks (Windows Explorer) to <see cref="FatxVolume"/>.</summary>
public sealed class FatxFileSystem : IDokanOperations
{
    private sealed class FileCtx { public bool Dirty; }

    private readonly FatxVolume _vol;
    private readonly bool _readOnly;
    private readonly object _gate = new();   // Dokan is multi-threaded; the volume is not

    /// <summary>Receives error messages (shown in the GUI log / console).</summary>
    public static Action<string>? Log;

    public FatxFileSystem(FatxVolume vol, bool readOnly) { _vol = vol; _readOnly = readOnly; }

    public void Flush() { lock (_gate) _vol.Flush(); }

    // ------------------------------------------------------------------ helpers

    private NtStatus Run(Func<NtStatus> body)
    {
        try { lock (_gate) return body(); }
        catch (FatxDiskFullException) { return Nt.DiskFull; }
        catch (FatxBadNameException) { return Nt.BadPathName; }
        catch (FatxExistsException) { return DokanResult.FileExists; }
        catch (FatxNotEmptyException) { return DokanResult.DirectoryNotEmpty; }
        catch (Exception ex)
        {
            CrashLog.Write("Exception in Dokan callback:\n" + ex);
            Log?.Invoke($"[FATX] {ex.GetType().Name}: {ex.Message}");
            return Nt.InternalError;
        }
    }

    private static (string parent, string name) Split(string path)
    {
        int i = path.LastIndexOf('\\');
        return (i <= 0 ? "\\" : path[..i], path[(i + 1)..]);
    }

    private static FileAttributes ToAttrs(DirEntry e)
    {
        var a = (FileAttributes)(e.Attributes & 0x37);   // RO|Hidden|System|Directory|Archive
        return a == 0 ? FileAttributes.Normal : a;
    }

    private static FileInformation ToInfo(DirEntry e) => new()
    {
        FileName = e.Name,
        Attributes = ToAttrs(e),
        CreationTime = e.Created,
        LastAccessTime = e.Accessed,
        LastWriteTime = e.Written,
        Length = e.IsDirectory ? 0 : e.Size
    };

    private static bool IsShellJunk(string path)
    {
        string top = path.TrimStart('\\').Split('\\')[0];
        return top.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || top.Equals("RECYCLER", StringComparison.OrdinalIgnoreCase);
    }

    private NtStatus NotFound(string path)
    {
        var parent = _vol.Lookup(Split(path).parent);
        return parent is { IsDirectory: true } ? DokanResult.FileNotFound : DokanResult.PathNotFound;
    }

    // ------------------------------------------------------------------ create / open

    public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
                               FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        => Run(() => CreateCore(fileName, mode, info));

    private NtStatus CreateCore(string path, FileMode mode, IDokanFileInfo info)
    {
        if (path == "\\")
        {
            info.IsDirectory = true;
            return mode is FileMode.Open or FileMode.OpenOrCreate ? DokanResult.Success : DokanResult.AccessDenied;
        }

        var existing = _vol.Lookup(path);
        bool wantsDir = info.IsDirectory;

        if (_readOnly && (mode is FileMode.CreateNew or FileMode.Create or FileMode.Truncate
                          || (existing == null && mode is FileMode.OpenOrCreate or FileMode.Append)))
            return DokanResult.AccessDenied;

        if (existing != null)
        {
            if (existing.IsDirectory)
            {
                switch (mode)
                {
                    case FileMode.Open:
                    case FileMode.OpenOrCreate:
                        info.IsDirectory = true;
                        info.Context = new FileCtx();
                        return DokanResult.Success;
                    case FileMode.CreateNew:
                        return DokanResult.FileExists;
                    default:
                        return DokanResult.AccessDenied;
                }
            }

            // existing is a file
            if (wantsDir) return mode == FileMode.CreateNew ? DokanResult.FileExists : DokanResult.NotADirectory;

            switch (mode)
            {
                case FileMode.CreateNew:
                    return DokanResult.FileExists;
                case FileMode.Create:
                case FileMode.Truncate:
                    _vol.SetLength(existing, 0);
                    info.Context = new FileCtx { Dirty = true };
                    return mode == FileMode.Create ? DokanResult.AlreadyExists : DokanResult.Success;
                case FileMode.OpenOrCreate:
                    info.Context = new FileCtx();
                    return DokanResult.AlreadyExists;
                default: // Open, Append
                    info.Context = new FileCtx();
                    return DokanResult.Success;
            }
        }

        // does not exist
        if (mode is FileMode.Open or FileMode.Truncate) return NotFound(path);
        if (IsShellJunk(path)) return DokanResult.AccessDenied;   // don't litter the Xbox disk

        var (parentPath, name) = Split(path);
        var parent = _vol.Lookup(parentPath);
        if (parent == null || !parent.IsDirectory) return DokanResult.PathNotFound;

        _vol.Create(parent, name, wantsDir);
        info.Context = new FileCtx();
        return DokanResult.Success;
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        try { CleanupCore(fileName, info); }
        catch (Exception ex) { CrashLog.Write("Cleanup: " + ex); }
    }

    private void CleanupCore(string fileName, IDokanFileInfo info)
    {
        lock (_gate)
        {
            try
            {
                if (info.DeleteOnClose)
                {
                    var e = _vol.Lookup(fileName);
                    if (e != null && !_readOnly) _vol.Delete(e);
                }
                else if (info.Context is FileCtx { Dirty: true } && !_readOnly)
                {
                    var e = _vol.Lookup(fileName);
                    if (e != null && !e.IsDirectory)
                    {
                        e.Written = DateTime.Now;
                        _vol.UpdateEntry(e);
                    }
                }
            }
            catch (Exception ex) { CrashLog.Write("Cleanup: " + ex); Log?.Invoke($"[FATX] cleanup: {ex.Message}"); }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        try { info.Context = null; }
        catch (Exception ex) { CrashLog.Write("CloseFile: " + ex); }
    }

    // ------------------------------------------------------------------ data

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        int read = 0;
        var st = Run(() =>
        {
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (e.IsDirectory) return DokanResult.AccessDenied;
            read = _vol.Read(e, offset, buffer);
            return DokanResult.Success;
        });
        bytesRead = read;
        return st;
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        int written = 0;
        var st = Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (e.IsDirectory) return DokanResult.AccessDenied;

            int count = buffer.Length;
            long pos = info.WriteToEndOfFile ? e.Size : offset;
            if (info.PagingIo)   // paging I/O must never extend the file
            {
                if (pos >= e.Size) return DokanResult.Success;
                count = (int)Math.Min(count, e.Size - pos);
            }
            _vol.Write(e, pos, buffer.AsSpan(0, count));
            written = count;
            if (info.Context is FileCtx c) c.Dirty = true;
            return DokanResult.Success;
        });
        bytesWritten = written;
        return st;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        => Run(() => { _vol.Flush(); return DokanResult.Success; });

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (e.IsDirectory) return DokanResult.AccessDenied;
            _vol.SetLength(e, length);
            if (info.Context is FileCtx c) c.Dirty = true;
            return DokanResult.Success;
        });

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (e.IsDirectory) return DokanResult.AccessDenied;
            if (length < e.Size) _vol.SetLength(e, length);   // growth happens lazily on write
            return DokanResult.Success;
        });

    // ------------------------------------------------------------------ metadata

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        FileInformation fi = default;
        var st = Run(() =>
        {
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            fi = ToInfo(e);
            return DokanResult.Success;
        });
        fileInfo = fi;
        return st;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        IList<FileInformation> list = new List<FileInformation>();
        var st = Run(() =>
        {
            var dir = _vol.Lookup(fileName);
            if (dir == null) return DokanResult.PathNotFound;
            if (!dir.IsDirectory) return DokanResult.NotADirectory;
            foreach (var e in _vol.ReadDir(dir.FirstCluster)) list.Add(ToInfo(e));
            return DokanResult.Success;
        });
        files = list;
        return st;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        return DokanResult.NotImplemented;   // Dokan falls back to FindFiles and filters itself
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (attributes != 0 && e.Index >= 0)
            {
                e.Attributes = (byte)((e.Attributes & 0x10) | ((int)attributes & 0x27));
                _vol.UpdateEntry(e);
            }
            return DokanResult.Success;
        });

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            if (e.Index < 0) return DokanResult.Success;
            if (creationTime.HasValue) e.Created = creationTime.Value;
            if (lastAccessTime.HasValue) e.Accessed = lastAccessTime.Value;
            if (lastWriteTime.HasValue) e.Written = lastWriteTime.Value;
            _vol.UpdateEntry(e);
            if (info.Context is FileCtx c) c.Dirty = false;   // explicit time set wins over auto-touch
            return DokanResult.Success;
        });

    // ------------------------------------------------------------------ delete / rename

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return NotFound(fileName);
            return e.IsDirectory ? DokanResult.AccessDenied : DokanResult.Success;   // real delete happens in Cleanup
        });

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(fileName);
            if (e == null) return DokanResult.PathNotFound;
            if (!e.IsDirectory) return DokanResult.NotADirectory;
            if (e.Index < 0) return DokanResult.AccessDenied;
            return _vol.ReadDir(e.FirstCluster).Count > 0 ? DokanResult.DirectoryNotEmpty : DokanResult.Success;
        });

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        => Run(() =>
        {
            if (_readOnly) return DokanResult.AccessDenied;
            var e = _vol.Lookup(oldName);
            if (e == null) return NotFound(oldName);
            if (e.Index < 0) return DokanResult.AccessDenied;
            if (newName.StartsWith(oldName + "\\", StringComparison.OrdinalIgnoreCase))
                return DokanResult.AccessDenied;   // directory into itself

            var (parentPath, name) = Split(newName);
            var parent = _vol.Lookup(parentPath);
            if (parent == null || !parent.IsDirectory) return DokanResult.PathNotFound;

            _vol.Move(e, parent, name, replace);
            return DokanResult.Success;
        });

    // ------------------------------------------------------------------ volume

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        long free = 0, total = 0;
        try
        {
            lock (_gate)
            {
                free = (long)_vol.FreeClusters * _vol.ClusterSize;
                total = (long)_vol.ClusterCount * _vol.ClusterSize;
            }
        }
        catch (Exception ex) { CrashLog.Write("GetDiskFreeSpace: " + ex); }
        freeBytesAvailable = free;
        totalNumberOfFreeBytes = free;
        totalNumberOfBytes = total;
        return DokanResult.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
                                         out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = _vol.BigEndian ? "XBOX360" : "XBOX";
        fileSystemName = _vol.BigEndian ? "XTAF" : "FATX";
        maximumComponentLength = FatxVolume.MaxNameLength;
        features = FileSystemFeatures.CasePreservedNames | (_readOnly ? FileSystemFeatures.ReadOnlyVolume : FileSystemFeatures.None);
        return DokanResult.Success;
    }

    // ------------------------------------------------------------------ unsupported / trivial

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info) => DokanResult.Success;

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections, IDokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
        => DokanResult.NotImplemented;

    public NtStatus Mounted(string mountPoint, IDokanFileInfo info) => DokanResult.Success;
    public NtStatus Unmounted(IDokanFileInfo info) => DokanResult.Success;

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        streams = new List<FileInformation>();
        return DokanResult.NotImplemented;
    }
}
