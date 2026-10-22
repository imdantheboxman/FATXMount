using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FatxMount;

internal readonly record struct PendingWrite(long Offset, byte[] Data);

internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    /// <summary>Chainable: pass the previous result as <paramref name="crc"/> to continue.</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        uint c = ~crc;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}

/// <summary>
/// Write-ahead (redo) journal for FATX metadata (FAT blocks and directory entries).
/// Every metadata change is appended here and flushed to disk BEFORE it touches the Xbox disk, so after a crash or
/// power loss the committed changes are replayed on the next writable mount. File data is not journaled
/// (ordered mode: data is written first, then the metadata that references it).
/// The journal lives on this PC, not on the Xbox disk (FATX has no room for one).
/// </summary>
internal sealed class Journal : IDisposable
{
    private const uint FileMagic = 0x324A5846;     // "FXJ2"
    private const uint RecMagic = 0x44434552;      // "RECD"
    private const int HeaderSize = 16;
    private const int RecHeaderSize = 20;
    private const long CheckpointBytes = 8L << 20;

    private readonly FileStream _fs;
    private long _sinceCheckpoint;
    private uint _seq;

    public string Path { get; }

    public static string PathFor(string deviceDescription, long partitionOffset, uint volumeId)
    {
        string key = $"{deviceDescription.ToLowerInvariant()}|{partitionOffset}|{volumeId}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxMount", "journals", hash + ".fxj");
    }

    private Journal(FileStream fs, string path) { _fs = fs; Path = path; }

    public static Journal Open(string path, uint volumeId)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        var j = new Journal(fs, path);

        var hdr = new byte[HeaderSize];
        bool valid = false;
        if (fs.Length >= HeaderSize)
        {
            fs.Position = 0;
            fs.ReadExactly(hdr);
            valid = BinaryPrimitives.ReadUInt32LittleEndian(hdr) == FileMagic
                    && BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(4)) == volumeId;
        }
        if (!valid)
        {
            fs.SetLength(0);
            Array.Clear(hdr);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr, FileMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), volumeId);
            fs.Write(hdr);
            fs.Flush(true);
        }
        return j;
    }

    /// <summary>Re-applies every complete, checksummed record left behind by a crash; returns how many.</summary>
    public int Replay(BlockDevice dev)
    {
        int applied = 0;
        _fs.Position = HeaderSize;
        var rh = new byte[RecHeaderSize];

        while (_fs.Length - _fs.Position >= RecHeaderSize)
        {
            _fs.ReadExactly(rh);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rh) != RecMagic) break;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(rh.AsSpan(8));
            uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(rh.AsSpan(12));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(rh.AsSpan(16));
            if (bodyLen > _fs.Length - _fs.Position) break;   // torn tail: never committed

            var body = new byte[bodyLen];
            _fs.ReadExactly(body);
            if (Crc32.Compute(body, Crc32.Compute(rh.AsSpan(4, 12))) != crc) break;

            int pos = 0;
            bool ok = true;
            var writes = new List<PendingWrite>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (pos + 12 > body.Length) { ok = false; break; }
                long off = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos));
                int len = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos + 8));
                pos += 12;
                if (len < 0 || pos + len > body.Length || off < 0 || off + len > dev.Length) { ok = false; break; }
                writes.Add(new PendingWrite(off, body.AsSpan(pos, len).ToArray()));
                pos += len;
            }
            if (!ok) break;
            foreach (var w in writes) dev.Write(w.Offset, w.Data);
            applied++;
        }

        if (applied > 0 && !dev.Flush())
            throw new IOException("Could not flush the disk after journal recovery");
        Reset();
        return applied;
    }

    /// <summary>Durably appends one transaction. Must complete before the writes are applied to the disk.</summary>
    public void Append(List<PendingWrite> writes)
    {
        int bodyLen = 0;
        foreach (var w in writes) bodyLen += 12 + w.Data.Length;

        var rec = new byte[RecHeaderSize + bodyLen];
        BinaryPrimitives.WriteUInt32LittleEndian(rec, RecMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), ++_seq);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(8), (uint)writes.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), (uint)bodyLen);

        int pos = RecHeaderSize;
        foreach (var w in writes)
        {
            BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(pos), w.Offset);
            BinaryPrimitives.WriteInt32LittleEndian(rec.AsSpan(pos + 8), w.Data.Length);
            w.Data.CopyTo(rec.AsSpan(pos + 12));
            pos += 12 + w.Data.Length;
        }
        uint crc = Crc32.Compute(rec.AsSpan(RecHeaderSize), Crc32.Compute(rec.AsSpan(4, 12)));
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(16), crc);

        _fs.Seek(0, SeekOrigin.End);
        _fs.Write(rec);
        _fs.Flush(true);
        _sinceCheckpoint += rec.Length;
    }

    public void MaybeCheckpoint(BlockDevice dev)
    {
        if (_sinceCheckpoint >= CheckpointBytes) Checkpoint(dev);
    }

    /// <summary>Forces applied writes to the disk, then empties the journal.</summary>
    public void Checkpoint(BlockDevice dev)
    {
        if (!dev.Flush()) return;   // keep the journal if the disk did not confirm the flush
        Reset();
    }

    private void Reset()
    {
        _fs.SetLength(HeaderSize);
        _fs.Flush(true);
        _sinceCheckpoint = 0;
    }

    public void Dispose() => _fs.Dispose();
}
