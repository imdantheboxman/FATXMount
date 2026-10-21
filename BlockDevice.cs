using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FatxMount;

/// <summary>
/// Random-access byte device over a raw physical drive (\\.\PhysicalDriveN) or an image file.
/// Raw drives require sector-aligned I/O, so unaligned reads/writes are done with read-modify-write.
/// </summary>
public sealed class BlockDevice : IDisposable
{
    private readonly SafeFileHandle _h;
    private readonly long _align;

    public long Length { get; }
    public bool Writable { get; }
    public string Description { get; }

    private BlockDevice(SafeFileHandle h, long length, bool writable, string desc, long align)
    {
        _h = h; Length = length; Writable = writable; Description = desc; _align = align;
    }

    /// <param name="target">Disk number ("2"), "\\.\PhysicalDrive2", or a path to an image file.</param>
    public static BlockDevice Open(string target, bool writable)
    {
        if (int.TryParse(target, out int n)) target = $@"\\.\PhysicalDrive{n}";

        if (target.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            uint access = 0x80000000u | (writable ? 0x40000000u : 0u); // GENERIC_READ [| GENERIC_WRITE]
            var h = CreateFileW(target, access, 3 /*share r/w*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid)
                throw new IOException(DescribeOpenFailure(target, writable, Marshal.GetLastWin32Error()));
            if (!DeviceIoControl(h, 0x0007405C /*IOCTL_DISK_GET_LENGTH_INFO*/, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query disk length");
            return new BlockDevice(h, len, writable, target, 4096);
        }

        var fh = File.OpenHandle(target, FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        return new BlockDevice(fh, RandomAccess.GetLength(fh), writable, target, 1);
    }

    private static string DescribeOpenFailure(string target, bool writable, int err)
    {
        string msg = $"Cannot open {target} {(writable ? "for read/write" : "read-only")}: " +
                     $"{new Win32Exception(err).Message} (Windows error {err}).";
        if (err == 5 && writable)
            msg += " Windows refused WRITE access to the raw disk even though read access worked. Common causes: " +
                   "Controlled folder access / ransomware protection or antivirus blocking FatxMount.exe; " +
                   "the disk is marked read-only (diskpart: attributes disk clear readonly); " +
                   "or Windows sees mounted volumes on this disk. Mount read-only to confirm the disk itself is fine.";
        else if (err == 5)
            msg += " Run FatxMount as Administrator.";
        else if (err == 32)
            msg += " Another program has the disk open without sharing it (close it, or unmount other FatxMount volumes on this disk).";
        else if (err == 19)
            msg += " The disk or its USB adapter is write-protected.";
        return msg;
    }

    public void Read(long offset, Span<byte> dest)
    {
        if (dest.Length == 0) return;
        if (offset < 0 || offset + dest.Length > Length) throw new IOException("Read beyond end of device");

        long start = offset & ~(_align - 1);
        long end = (offset + dest.Length + _align - 1) & ~(_align - 1);
        if (end > Length) end = Length;

        if (start == offset && end == offset + dest.Length) { ReadExact(offset, dest); return; }

        var tmp = new byte[end - start];
        ReadExact(start, tmp);
        tmp.AsSpan((int)(offset - start), dest.Length).CopyTo(dest);
    }

    public void Write(long offset, ReadOnlySpan<byte> src)
    {
        if (!Writable) throw new UnauthorizedAccessException("Device was opened read-only");
        if (src.Length == 0) return;
        if (offset < 0 || offset + src.Length > Length) throw new IOException("Write beyond end of device");

        long endData = offset + src.Length;
        long start = offset & ~(_align - 1);
        long end = (endData + _align - 1) & ~(_align - 1);
        if (end > Length) end = Length;

        if (start == offset && end == endData) { RandomAccess.Write(_h, src, offset); return; }

        // Read-modify-write only the partial head/tail blocks.
        var tmp = new byte[end - start];
        if (offset != start)
            ReadExact(start, tmp.AsSpan(0, (int)Math.Min(_align, end - start)));
        if (endData != end)
        {
            long tailStart = Math.Max(start, end - _align);
            ReadExact(tailStart, tmp.AsSpan((int)(tailStart - start)));
        }
        src.CopyTo(tmp.AsSpan((int)(offset - start)));
        RandomAccess.Write(_h, tmp, start);
    }

    public void Flush()
    {
        if (!Writable) return;
        try { RandomAccess.FlushToDisk(_h); } catch { /* some raw devices don't support it */ }
    }

    private void ReadExact(long pos, Span<byte> buf)
    {
        while (!buf.IsEmpty)
        {
            int n = RandomAccess.Read(_h, buf, pos);
            if (n <= 0) throw new EndOfStreamException();
            buf = buf[n..];
            pos += n;
        }
    }

    public void Dispose() => _h.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
        uint nInBufferSize, out long lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
