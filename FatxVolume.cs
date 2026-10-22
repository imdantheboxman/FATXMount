using System.Buffers.Binary;
using System.Text;

namespace FatxMount;

public class FatxException : IOException { public FatxException(string m) : base(m) { } }
public sealed class FatxDiskFullException : FatxException { public FatxDiskFullException() : base("Volume is full") { } }
public sealed class FatxExistsException : FatxException { public FatxExistsException() : base("Name already exists") { } }
public sealed class FatxNotEmptyException : FatxException { public FatxNotEmptyException() : base("Directory not empty") { } }
public sealed class FatxBadNameException : FatxException { public FatxBadNameException(string n) : base($"Invalid FATX name: '{n}'") { } }
public sealed class FatxFileTooLargeException : FatxException { public FatxFileTooLargeException() : base("FATX files are limited to 4 GiB - 1") { } }

public sealed class DirEntry
{
    public string Name = "";
    public byte Attributes;          // 0x01 RO, 0x02 hidden, 0x04 system, 0x10 dir, 0x20 archive
    public uint FirstCluster;
    public uint Size;
    public DateTime Created, Written, Accessed;
    public uint ParentCluster;       // directory that contains this entry
    public int Index = -1;           // slot index inside that directory (-1 = root)
    public bool IsDirectory => (Attributes & 0x10) != 0;
}

/// <summary>
/// FATX (original Xbox, little-endian "FATX") and XTAF (Xbox 360, big-endian) volume.
/// NOT thread-safe: callers must serialise access.
///
/// Crash safety: every public mutating operation is one transaction. FAT blocks and directory entries are
/// collected, written to the write-ahead <see cref="Journal"/> (flushed), and only then applied to the disk.
/// File data is written first (ordered mode). Extending a file is instant: new space is zeroed lazily
/// (tracked as a "valid data length") instead of being written up front, which is what caused Dokan
/// request timeouts on multi-GB copies.
/// </summary>
public sealed class FatxVolume : IDisposable
{
    public const int DirEntrySize = 64;
    public const int MaxNameLength = 42;
    private const int Header = 0x1000;
    private const byte DeletedMark = 0xE5;

    private readonly BlockDevice _dev;
    private readonly Journal? _journal;
    private readonly bool _fat32;
    private readonly long _fatOff;
    private readonly long _dataOff;          // absolute offset of cluster 1
    private readonly byte[] _fat;
    private readonly HashSet<int> _dirtyFat = new();
    private readonly Dictionary<uint, List<uint>> _chains = new();
    private readonly Dictionary<uint, List<DirEntry>> _dirs = new();
    private readonly Dictionary<uint, (long valid, long size)> _valid = new();   // first cluster -> valid data length
    private readonly List<PendingWrite> _pending = new();
    private readonly int _epc;               // directory entries per cluster
    private int _txnDepth;
    private uint _freeHint = 1;
    private bool _disposed;

    public bool BigEndian { get; }
    public uint ClusterSize { get; }
    public uint ClusterCount { get; }
    public uint FreeClusters { get; private set; }
    public uint VolumeId { get; }
    public DirEntry Root { get; }
    public bool Journaled => _journal != null;
    public int RecoveredTransactions { get; }
    public string Flavor => BigEndian ? "Xbox 360 (XTAF, big-endian)" : "Original Xbox (FATX, little-endian)";

    private int Epoch => BigEndian ? 1980 : 2000;
    private uint EocMark => _fat32 ? 0xFFFFFFFFu : 0xFFFFu;
    private bool IsEnd(uint v) => _fat32 ? v >= 0xFFFFFFF0u : v >= 0xFFF0u;

    // ------------------------------------------------------------------ open / format

    /// <summary>Returns "FATX" or "XTAF" if a signature is present at <paramref name="off"/>, otherwise null.</summary>
    public static string? GetSignature(BlockDevice dev, long off)
    {
        if (off < 0 || off + 16 > dev.Length) return null;
        var b = new byte[4];
        dev.Read(off, b);
        string m = Encoding.ASCII.GetString(b);
        return m == "FATX" || m == "XTAF" ? m : null;
    }

    public static bool HasSignature(BlockDevice dev, long off) => GetSignature(dev, off) != null;

    public static FatxVolume Open(BlockDevice dev, long partOff, long partSize, bool journaling = false)
    {
        if (partOff < 0 || partSize <= 0 || partOff + partSize > dev.Length)
            throw new FatxException("Partition range lies outside the device");

        var hdr = new byte[16];
        dev.Read(partOff, hdr);
        string magic = Encoding.ASCII.GetString(hdr, 0, 4);
        bool be = magic switch
        {
            "FATX" => false,
            "XTAF" => true,
            _ => throw new FatxException($"No FATX/XTAF signature at offset 0x{partOff:X}")
        };
        uint volId = be ? BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4));
        uint spc = be ? BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
        if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 256)
            throw new FatxException($"Implausible sectors-per-cluster value {spc}");

        Journal? journal = null;
        int recovered = 0;
        if (journaling && dev.Writable)
        {
            journal = Journal.Open(Journal.PathFor(dev.Description, partOff, volId), volId);
            try
            {
                recovered = journal.Replay(dev);     // must happen before the FAT is read
                if (recovered > 0) CrashLog.Write($"Journal: replayed {recovered} transaction(s) after an unclean shutdown");
            }
            catch { journal.Dispose(); throw; }
        }

        try { return new FatxVolume(dev, partOff, partSize, be, volId, spc, journal, recovered); }
        catch { journal?.Dispose(); throw; }
    }

    /// <summary>Writes an empty filesystem (header, FAT, empty root). Existing file data is left in place but unreachable.</summary>
    public static (uint clusters, uint clusterSize) Format(BlockDevice dev, long partOff, long partSize, bool bigEndian,
                                                           uint sectorsPerCluster = 32, uint volumeId = 0)
    {
        if (volumeId == 0) volumeId = (uint)Random.Shared.NextInt64(1, uint.MaxValue);
        uint cs = sectorsPerCluster * 512;
        Geometry(partSize, cs, bigEndian, out bool fat32, out long fatBytes, out uint usable);

        var hdr = new byte[Header];
        Array.Fill(hdr, (byte)0xFF);
        Encoding.ASCII.GetBytes(bigEndian ? "XTAF" : "FATX").CopyTo(hdr, 0);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(4), volumeId);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(8), sectorsPerCluster);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12), 1);   // FAT copies
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), volumeId);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), sectorsPerCluster);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), 1); // root cluster
        }
        dev.Write(partOff, hdr);

        // FAT: first block carries the media descriptor and the root-directory end-of-chain, the rest is zero.
        var first = new byte[Header];
        void Put(int idx, uint v)
        {
            if (fat32) { if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(idx * 4), v); else BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(idx * 4), v); }
            else { if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(idx * 2), (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(first.AsSpan(idx * 2), (ushort)v); }
        }
        Put(0, fat32 ? 0xFFFFFFF8u : 0xFFF8u);
        Put(1, fat32 ? 0xFFFFFFFFu : 0xFFFFu);
        dev.Write(partOff + Header, first);

        long done = Header;
        var zeros = new byte[4 << 20];
        while (done < fatBytes)
        {
            int n = (int)Math.Min(zeros.Length, fatBytes - done);
            dev.Write(partOff + Header + done, zeros.AsSpan(0, n));
            done += n;
        }

        var root = new byte[cs];
        Array.Fill(root, (byte)0xFF);
        dev.Write(partOff + Header + fatBytes, root);
        dev.Flush();
        return (usable, cs);
    }

    private static void Geometry(long partSize, uint clusterSize, bool bigEndian, out bool fat32, out long fatBytes, out uint usableClusters)
    {
        long total = partSize / clusterSize;
        fat32 = total >= (bigEndian ? 0xFFF4 : 0xFFF0);
        long entries = total + 1; // entry 0 is reserved
        fatBytes = (entries * (fat32 ? 4 : 2) + (Header - 1)) & ~(long)(Header - 1);
        usableClusters = (uint)((partSize - Header - fatBytes) / clusterSize);
    }

    private FatxVolume(BlockDevice dev, long partOff, long partSize, bool be, uint volId, uint spc, Journal? journal, int recovered)
    {
        _dev = dev;
        _journal = journal;
        RecoveredTransactions = recovered;
        BigEndian = be;
        VolumeId = volId;
        ClusterSize = spc * 512;
        Geometry(partSize, ClusterSize, be, out _fat32, out long fatBytes, out uint usable);
        ClusterCount = usable;
        _fatOff = partOff + Header;
        _dataOff = _fatOff + fatBytes;
        _epc = (int)ClusterSize / DirEntrySize;

        _fat = new byte[fatBytes];
        dev.Read(_fatOff, _fat);

        for (uint c = 1; c <= ClusterCount; c++)
            if (GetFat(c) == 0) FreeClusters++;

        var now = DateTime.Now;
        Root = new DirEntry { Name = "", Attributes = 0x10, FirstCluster = 1, ParentCluster = 0, Index = -1, Created = now, Written = now, Accessed = now };
    }

    // ------------------------------------------------------------------ endian helpers

    private ushort R16(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    private uint R32(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    private void W16(Span<byte> s, ushort v) { if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(s, v); else BinaryPrimitives.WriteUInt16LittleEndian(s, v); }
    private void W32(Span<byte> s, uint v) { if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(s, v); else BinaryPrimitives.WriteUInt32LittleEndian(s, v); }

    // ------------------------------------------------------------------ transactions / journal

    private void Begin() => _txnDepth++;

    private void End()
    {
        if (--_txnDepth > 0) return;
        _txnDepth = 0;
        CommitPending();
    }

    /// <summary>Throws away an unfinished transaction and restores the in-memory FAT from the disk.</summary>
    private void Abort()
    {
        _txnDepth = 0;
        _pending.Clear();
        try
        {
            long before = 0, after = 0;
            foreach (int b in _dirtyFat)
            {
                before += CountFree(b);
                _dev.Read(_fatOff + (long)b * Header, _fat.AsSpan(b * Header, Header));
                after += CountFree(b);
            }
            FreeClusters = (uint)Math.Max(0, FreeClusters - before + after);
        }
        catch (Exception ex) { CrashLog.Write("Abort: could not restore FAT blocks: " + ex.Message); }
        _dirtyFat.Clear();
        _chains.Clear();
        _dirs.Clear();
    }

    private uint CountFree(int block)
    {
        int per = Header / (_fat32 ? 4 : 2);
        uint n = 0;
        for (int i = 0; i < per; i++)
        {
            long c = (long)block * per + i;
            if (c < 1 || c > ClusterCount) continue;
            if (GetFat((uint)c) == 0) n++;
        }
        return n;
    }

    private void MetaWrite(long off, byte[] data)
    {
        if (_txnDepth == 0) { _dev.Write(off, data); return; }
        _pending.Add(new PendingWrite(off, data));
    }

    private void CommitPending()
    {
        foreach (int b in _dirtyFat)
            _pending.Add(new PendingWrite(_fatOff + (long)b * Header, _fat.AsSpan(b * Header, Header).ToArray()));
        if (_pending.Count == 0) { _dirtyFat.Clear(); return; }
        try
        {
            _journal?.Append(_pending);                        // durable BEFORE the disk is touched
            foreach (var w in _pending) _dev.Write(w.Offset, w.Data);
            _journal?.MaybeCheckpoint(_dev);
        }
        finally
        {
            _pending.Clear();
            _dirtyFat.Clear();
        }
    }

    /// <summary>Makes directory reads inside a transaction see writes that are queued but not yet applied.</summary>
    private void Overlay(long off, Span<byte> buf)
    {
        foreach (var w in _pending)
        {
            long s = Math.Max(off, w.Offset), e = Math.Min(off + buf.Length, w.Offset + w.Data.Length);
            if (s < e) w.Data.AsSpan((int)(s - w.Offset), (int)(e - s)).CopyTo(buf.Slice((int)(s - off)));
        }
    }

    /// <summary>Forces applied writes to the disk and empties the journal.</summary>
    public void Flush()
    {
        if (_journal != null) _journal.Checkpoint(_dev); else _dev.Flush();
    }

    /// <summary>Zero-fills any lazily extended file tails, then checkpoints. Call before unmounting.</summary>
    public void Close()
    {
        FinalizeAll();
        Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _journal?.Dispose();
    }

    // ------------------------------------------------------------------ FAT

    private uint GetFat(uint c) => _fat32 ? R32(_fat.AsSpan((int)(c * 4))) : R16(_fat.AsSpan((int)(c * 2)));

    private void SetFat(uint c, uint v)
    {
        uint old = GetFat(c);
        if (old == 0 && v != 0) FreeClusters--;
        else if (old != 0 && v == 0) FreeClusters++;

        int byteOff = (int)(c * (_fat32 ? 4u : 2u));
        if (_fat32) W32(_fat.AsSpan(byteOff), v); else W16(_fat.AsSpan(byteOff), (ushort)v);
        _dirtyFat.Add(byteOff / Header);
    }

    private long ClusterOffset(uint c) => _dataOff + (long)(c - 1) * ClusterSize;

    /// <summary>Cluster chain of a file/dir. The returned list is the cache itself; do not mutate outside this class.</summary>
    private List<uint> Chain(uint first)
    {
        if (_chains.TryGetValue(first, out var cached)) return cached;

        var list = new List<uint>();
        uint c = first;
        while (c >= 1 && c <= ClusterCount)
        {
            list.Add(c);
            if (list.Count > ClusterCount) throw new FatxException("Cluster chain loop detected");
            uint next = GetFat(c);
            if (next == 0 || IsEnd(next)) break;
            c = next;
        }
        if (_chains.Count > 256) _chains.Clear();
        if (first != 0) _chains[first] = list;
        return list;
    }

    private List<uint> AllocateClusters(int n)
    {
        if (n > FreeClusters) throw new FatxDiskFullException();
        var list = new List<uint>(n);
        uint c = _freeHint;
        for (uint scanned = 0; list.Count < n && scanned < ClusterCount; scanned++)
        {
            if (c < 1 || c > ClusterCount) c = 1;
            if (GetFat(c) == 0) list.Add(c);
            c++;
        }
        if (list.Count < n) throw new FatxDiskFullException();
        _freeHint = c > ClusterCount ? 1 : c;
        return list;
    }

    /// <summary>
    /// Grows or shrinks a chain to exactly <paramref name="wanted"/> clusters; returns the (possibly new) first
    /// cluster, 0 if empty. New clusters are only written if <paramref name="fill"/> is given (directories).
    /// </summary>
    private uint ResizeChain(uint first, int wanted, byte? fill = null)
    {
        var chain = first == 0 ? new List<uint>() : Chain(first);
        if (wanted == chain.Count) return first;

        if (wanted < chain.Count)
        {
            for (int i = wanted; i < chain.Count; i++) SetFat(chain[i], 0);
            if (wanted == 0) { _chains.Remove(first); return 0; }
            SetFat(chain[wanted - 1], EocMark);
            chain.RemoveRange(wanted, chain.Count - wanted);
            return first;
        }

        int need = wanted - chain.Count;
        var fresh = AllocateClusters(need);   // throws before anything is modified

        if (fill is byte f)
        {
            var buf = new byte[ClusterSize];
            Array.Fill(buf, f);
            foreach (uint c in fresh) _dev.Write(ClusterOffset(c), buf);   // unreferenced until the transaction commits
        }

        if (chain.Count > 0) SetFat(chain[^1], fresh[0]);
        for (int i = 0; i < fresh.Count; i++)
            SetFat(fresh[i], i + 1 < fresh.Count ? fresh[i + 1] : EocMark);

        chain.AddRange(fresh);
        if (first == 0) { first = chain[0]; _chains[first] = chain; }
        return first;
    }

    // ------------------------------------------------------------------ timestamps

    private (ushort date, ushort time) EncodeTime(DateTime dt)
    {
        int y = Math.Clamp(dt.Year - Epoch, 0, 127);
        ushort date = (ushort)((y << 9) | (dt.Month << 5) | dt.Day);
        ushort time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
        return (date, time);
    }

    private DateTime DecodeTime(ushort date, ushort time)
    {
        int y = Epoch + (date >> 9);
        int mo = Math.Clamp((date >> 5) & 0xF, 1, 12);
        int d = Math.Clamp(date & 0x1F, 1, DateTime.DaysInMonth(y, mo));
        int h = Math.Min(time >> 11, 23), mi = Math.Min((time >> 5) & 0x3F, 59), s = Math.Min((time & 0x1F) * 2, 59);
        return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Local);
    }

    // ------------------------------------------------------------------ directory entries

    private DirEntry ParseEntry(ReadOnlySpan<byte> b, uint parent, int index)
    {
        int len = b[0];
        var e = new DirEntry
        {
            Attributes = b[1],
            Name = Encoding.ASCII.GetString(b.Slice(2, len)),
            FirstCluster = R32(b.Slice(0x2C)),
            Size = R32(b.Slice(0x30)),
            ParentCluster = parent,
            Index = index
        };
        if (BigEndian)
        {   // 360: create, access, write; each date then time
            e.Created = DecodeTime(R16(b.Slice(0x34)), R16(b.Slice(0x36)));
            e.Accessed = DecodeTime(R16(b.Slice(0x38)), R16(b.Slice(0x3A)));
            e.Written = DecodeTime(R16(b.Slice(0x3C)), R16(b.Slice(0x3E)));
        }
        else
        {   // Xbox: write, create, access; each time then date
            e.Written = DecodeTime(R16(b.Slice(0x36)), R16(b.Slice(0x34)));
            e.Created = DecodeTime(R16(b.Slice(0x3A)), R16(b.Slice(0x38)));
            e.Accessed = DecodeTime(R16(b.Slice(0x3E)), R16(b.Slice(0x3C)));
        }
        return e;
    }

    private void SerializeEntry(DirEntry e, Span<byte> b)
    {
        b.Clear();
        var name = Encoding.ASCII.GetBytes(e.Name);
        b[0] = (byte)name.Length;
        b[1] = e.Attributes;
        name.CopyTo(b.Slice(2));
        b.Slice(2 + name.Length, MaxNameLength - name.Length).Fill(0xFF);
        W32(b.Slice(0x2C), e.FirstCluster);
        W32(b.Slice(0x30), e.Size);

        var (cd, ct) = EncodeTime(e.Created);
        var (ad, at) = EncodeTime(e.Accessed);
        var (wd, wt) = EncodeTime(e.Written);
        if (BigEndian)
        {
            W16(b.Slice(0x34), cd); W16(b.Slice(0x36), ct);
            W16(b.Slice(0x38), ad); W16(b.Slice(0x3A), at);
            W16(b.Slice(0x3C), wd); W16(b.Slice(0x3E), wt);
        }
        else
        {
            W16(b.Slice(0x34), wt); W16(b.Slice(0x36), wd);
            W16(b.Slice(0x38), ct); W16(b.Slice(0x3A), cd);
            W16(b.Slice(0x3C), at); W16(b.Slice(0x3E), ad);
        }
    }

    public IReadOnlyList<DirEntry> ReadDir(uint dirCluster)
    {
        if (_dirs.TryGetValue(dirCluster, out var cached)) return cached;

        var result = new List<DirEntry>();
        var chain = Chain(dirCluster);
        var buf = new byte[ClusterSize];
        for (int ci = 0; ci < chain.Count; ci++)
        {
            long pos = ClusterOffset(chain[ci]);
            _dev.Read(pos, buf);
            if (_pending.Count > 0) Overlay(pos, buf);
            for (int i = 0; i < _epc; i++)
            {
                byte len = buf[i * DirEntrySize];
                if (len == 0x00 || len == 0xFF) goto done;      // end of directory
                if (len == DeletedMark || len > MaxNameLength) continue;
                result.Add(ParseEntry(buf.AsSpan(i * DirEntrySize, DirEntrySize), dirCluster, ci * _epc + i));
            }
        }
    done:
        if (_dirs.Count > 512) _dirs.Clear();
        _dirs[dirCluster] = result;
        return result;
    }

    private long EntryPos(uint dir, int idx)
    {
        var chain = Chain(dir);
        int ci = idx / _epc;
        if (ci >= chain.Count) throw new FatxException("Directory entry outside directory chain");
        return ClusterOffset(chain[ci]) + (long)(idx % _epc) * DirEntrySize;
    }

    private void WriteEntry(uint dir, int idx, DirEntry e)
    {
        var b = new byte[DirEntrySize];
        SerializeEntry(e, b);
        MetaWrite(EntryPos(dir, idx), b);
        _dirs.Remove(dir);
    }

    private void MarkDeleted(uint dir, int idx)
    {
        MetaWrite(EntryPos(dir, idx), new[] { DeletedMark });
        _dirs.Remove(dir);
    }

    private int FindFreeSlot(uint dir)
    {
        var chain = Chain(dir);
        var buf = new byte[ClusterSize];
        for (int ci = 0; ci < chain.Count; ci++)
        {
            long pos = ClusterOffset(chain[ci]);
            _dev.Read(pos, buf);
            if (_pending.Count > 0) Overlay(pos, buf);
            for (int i = 0; i < _epc; i++)
            {
                byte len = buf[i * DirEntrySize];
                if (len == DeletedMark || len == 0x00 || len == 0xFF) return ci * _epc + i;
            }
        }
        int oldCount = chain.Count;
        ResizeChain(dir, oldCount + 1, fill: 0xFF);
        return oldCount * _epc;
    }

    // ------------------------------------------------------------------ public namespace API

    public DirEntry? FindInDir(uint dirCluster, string name)
    {
        foreach (var e in ReadDir(dirCluster))
            if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public DirEntry? Lookup(string path)
    {
        DirEntry cur = Root;
        foreach (var part in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!cur.IsDirectory) return null;
            var next = FindInDir(cur.FirstCluster, part);
            if (next == null) return null;
            cur = next;
        }
        return cur;
    }

    public static bool IsValidName(string n)
    {
        if (n.Length == 0 || n.Length > MaxNameLength) return false;
        foreach (char ch in n)
            if (!(char.IsAsciiLetterOrDigit(ch) || "!#$%&'()-.@[]^_`{}~+,;= ".Contains(ch))) return false;
        return true;
    }

    public DirEntry Create(DirEntry parent, string name, bool directory)
    {
        if (!parent.IsDirectory) throw new FatxException("Parent is not a directory");
        if (!IsValidName(name)) throw new FatxBadNameException(name);
        if (FindInDir(parent.FirstCluster, name) != null) throw new FatxExistsException();

        Begin();
        try
        {
            var now = DateTime.Now;
            var e = new DirEntry
            {
                Name = name,
                Attributes = directory ? (byte)0x10 : (byte)0x20,
                Created = now, Written = now, Accessed = now,
                ParentCluster = parent.FirstCluster
            };
            if (directory) e.FirstCluster = ResizeChain(0, 1, fill: 0xFF);
            e.Index = FindFreeSlot(parent.FirstCluster);
            WriteEntry(e.ParentCluster, e.Index, e);
            End();
            return e;
        }
        catch { Abort(); throw; }
    }

    public void Delete(DirEntry e)
    {
        if (e.Index < 0) throw new FatxException("Cannot delete the root directory");
        if (e.IsDirectory && ReadDir(e.FirstCluster).Count > 0) throw new FatxNotEmptyException();

        Begin();
        try
        {
            MarkDeleted(e.ParentCluster, e.Index);
            if (e.FirstCluster != 0)
            {
                _valid.Remove(e.FirstCluster);
                ResizeChain(e.FirstCluster, 0);
            }
            End();   // entry removal and cluster release commit together: no leaked clusters
        }
        catch { Abort(); throw; }
    }

    public void Move(DirEntry e, DirEntry newParent, string newName, bool replace)
    {
        if (e.Index < 0) throw new FatxException("Cannot move the root directory");
        if (!IsValidName(newName)) throw new FatxBadNameException(newName);

        Begin();
        try
        {
            var existing = FindInDir(newParent.FirstCluster, newName);
            bool same = existing != null && existing.ParentCluster == e.ParentCluster && existing.Index == e.Index;
            if (existing != null && !same)
            {
                if (!replace || existing.IsDirectory) throw new FatxExistsException();
                Delete(existing);
            }

            if (newParent.FirstCluster == e.ParentCluster)
            {
                e.Name = newName;
                WriteEntry(e.ParentCluster, e.Index, e);
            }
            else
            {
                int slot = FindFreeSlot(newParent.FirstCluster);
                uint oldParent = e.ParentCluster; int oldIdx = e.Index;
                e.Name = newName; e.ParentCluster = newParent.FirstCluster; e.Index = slot;
                WriteEntry(e.ParentCluster, slot, e);
                MarkDeleted(oldParent, oldIdx);
            }
            End();
        }
        catch { Abort(); throw; }
    }

    /// <summary>Persist changed metadata (attributes, times, name...) of an entry.</summary>
    public void UpdateEntry(DirEntry e)
    {
        if (e.Index < 0) return;
        Begin();
        try { WriteEntry(e.ParentCluster, e.Index, e); End(); }
        catch { Abort(); throw; }
    }

    // ------------------------------------------------------------------ file data

    private long ValidOf(DirEntry e) =>
        e.FirstCluster != 0 && _valid.TryGetValue(e.FirstCluster, out var v) ? v.valid : e.Size;

    private int ReadChain(List<uint> chain, long offset, Span<byte> dest)
    {
        int done = 0;
        while (done < dest.Length)
        {
            long pos = offset + done;
            int ci = (int)(pos / ClusterSize), co = (int)(pos % ClusterSize);
            if (ci >= chain.Count) break;   // corrupt chain: return what we have
            int run = Math.Min(dest.Length - done, (int)ClusterSize - co);
            int k = ci;
            while (done + run < dest.Length && k + 1 < chain.Count && chain[k + 1] == chain[k] + 1)
            {
                k++;
                run += Math.Min(dest.Length - done - run, (int)ClusterSize);
            }
            _dev.Read(ClusterOffset(chain[ci]) + co, dest.Slice(done, run));
            done += run;
        }
        return done;
    }

    private void WriteChain(List<uint> chain, long offset, ReadOnlySpan<byte> data)
    {
        int done = 0;
        while (done < data.Length)
        {
            long pos = offset + done;
            int ci = (int)(pos / ClusterSize), co = (int)(pos % ClusterSize);
            if (ci >= chain.Count) throw new FatxException("Cluster chain is shorter than the file size");
            int run = Math.Min(data.Length - done, (int)ClusterSize - co);
            int k = ci;
            while (done + run < data.Length && k + 1 < chain.Count && chain[k + 1] == chain[k] + 1)
            {
                k++;
                run += Math.Min(data.Length - done - run, (int)ClusterSize);
            }
            _dev.Write(ClusterOffset(chain[ci]) + co, data.Slice(done, run));
            done += run;
        }
    }

    private void ZeroRange(List<uint> chain, long from, long to)
    {
        if (to <= from) return;
        var zeros = new byte[(int)Math.Min(to - from, 1 << 20)];
        for (long pos = from; pos < to;)
        {
            int n = (int)Math.Min(zeros.Length, to - pos);
            WriteChain(chain, pos, zeros.AsSpan(0, n));
            pos += n;
        }
    }

    public int Read(DirEntry e, long offset, Span<byte> dest)
    {
        if (e.FirstCluster == 0 || offset >= e.Size) return 0;
        int count = (int)Math.Min(dest.Length, e.Size - offset);
        long valid = ValidOf(e);
        int fromDisk = (int)Math.Clamp(valid - offset, 0, count);

        var chain = Chain(e.FirstCluster);
        int got = ReadChain(chain, offset, dest.Slice(0, fromDisk));
        if (got < fromDisk) return got;
        if (fromDisk < count) dest.Slice(fromDisk, count - fromDisk).Clear();   // lazily extended region reads as zeros
        return count;
    }

    public void Write(DirEntry e, long offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        long end = offset + data.Length;
        if (end > uint.MaxValue) throw new FatxFileTooLargeException();

        Begin();
        try
        {
            if (end > e.Size) SetLengthCore(e, end);

            long valid = ValidOf(e);
            var chain = Chain(e.FirstCluster);
            if (offset > valid) ZeroRange(chain, valid, offset);   // never expose stale disk contents in a gap
            WriteChain(chain, offset, data);

            long newValid = Math.Max(valid, end);
            if (e.FirstCluster != 0)
            {
                if (newValid >= e.Size) _valid.Remove(e.FirstCluster);
                else _valid[e.FirstCluster] = (newValid, e.Size);
            }
            End();   // data is already on the disk; now commit the metadata that references it
        }
        catch { Abort(); throw; }
    }

    public void SetLength(DirEntry e, long newLen)
    {
        Begin();
        try { SetLengthCore(e, newLen); End(); }
        catch { Abort(); throw; }
    }

    private void SetLengthCore(DirEntry e, long newLen)
    {
        if (e.IsDirectory) throw new FatxException("Not a file");
        if (newLen < 0 || newLen > uint.MaxValue) throw new FatxFileTooLargeException();

        long oldValid = ValidOf(e);
        uint oldFirst = e.FirstCluster;
        int wanted = (int)((newLen + ClusterSize - 1) / ClusterSize);

        uint first = ResizeChain(oldFirst, wanted);          // instant: no zero-filling here

        if (oldFirst != 0) _valid.Remove(oldFirst);
        e.FirstCluster = first;
        e.Size = (uint)newLen;

        long newValid = Math.Min(oldValid, newLen);
        if (first != 0 && newValid < newLen) _valid[first] = (newValid, newLen);

        WriteEntry(e.ParentCluster, e.Index, e);
    }

    /// <summary>Zero-fills the never-written tail of one file (called when its last handle closes).</summary>
    public void FinalizeFile(DirEntry e)
    {
        if (e.FirstCluster == 0 || !_valid.TryGetValue(e.FirstCluster, out var v)) return;
        ZeroRange(Chain(e.FirstCluster), v.valid, v.size);
        _valid.Remove(e.FirstCluster);
    }

    private void FinalizeAll()
    {
        foreach (var kv in _valid.ToList())
        {
            try { ZeroRange(Chain(kv.Key), kv.Value.valid, kv.Value.size); }
            catch (Exception ex) { CrashLog.Write("FinalizeAll: " + ex.Message); }
            _valid.Remove(kv.Key);
        }
    }
}
