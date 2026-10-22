using DokanNet;
using DokanNet.Logging;

namespace FatxMount;

public sealed record KnownPartition(string Name, long Offset, long? Size);

public static class KnownPartitions
{
    /// <summary>Largest area the original Xbox kernel can address (28-bit LBA, 128 GiB).</summary>
    public const long OriginalXboxLimit = 0x2000000000;

    // Standard original-Xbox retail layout + the Xbox 360 "Data" partition.
    // For anything else (large-disk F:/G:, other 360 partitions) use a custom offset/size.
    public static readonly KnownPartition[] All =
    {
        new("X", 0x00080000, 0x2EE00000),
        new("Y", 0x2EE80000, 0x2EE00000),
        new("Z", 0x5DC80000, 0x2EE00000),
        new("C", 0x8CA80000, 0x1F400000),
        new("E", 0xABE80000, null),
        new("Data360", 0x130EB0000, null),
    };

    /// <summary>Standard original-Xbox partitions in layout order (X, Y, Z, C, E) for a disk of the given size.</summary>
    public static List<(string name, long offset, long size)> OriginalLayout(long deviceLength)
    {
        var list = new List<(string, long, long)>();
        foreach (var k in All.Where(q => q.Name != "Data360"))
        {
            long size = k.Size ?? Math.Min(deviceLength, OriginalXboxLimit) - k.Offset;
            list.Add((k.Name, k.Offset, size));
        }
        return list;
    }
}

/// <summary>One mounted FATX volume: device + volume + Dokan instance. Shared by the GUI and the CLI.</summary>
public sealed class MountSession : IDisposable
{
    private static Dokan? _dokan;
    private static readonly object DokanLock = new();

    private readonly BlockDevice _dev;
    private readonly FatxFileSystem _fs;
    private readonly DokanInstance _instance;

    public string Source { get; }
    public string PartitionName { get; }
    public long Offset { get; }
    public string MountPoint { get; }          // e.g. "Z:\"
    public bool ReadOnly { get; }
    public FatxVolume Volume { get; }

    private MountSession(string source, string partName, long offset, string mountPoint, bool ro,
                         BlockDevice dev, FatxVolume vol, FatxFileSystem fs, DokanInstance inst)
    {
        Source = source; PartitionName = partName; Offset = offset; MountPoint = mountPoint; ReadOnly = ro;
        _dev = dev; Volume = vol; _fs = fs; _instance = inst;
    }

    /// <summary>True if the same device + partition offset is already mounted by this process.</summary>
    public bool IsSameTarget(string source, long offset) =>
        string.Equals(Source, source, StringComparison.OrdinalIgnoreCase) && Offset == offset;

    public static MountSession Start(string target, string partName, long offset, long? size, bool readOnly,
                                     string driveLetter, bool journaling = true)
    {
        string mountPoint = driveLetter.TrimEnd(':', '\\').ToUpperInvariant() + ":\\";
        CrashLog.Write($"Mount: opening {target} (writable={!readOnly}, journaling={journaling && !readOnly}), partition {partName}, offset 0x{offset:X}");
        var dev = BlockDevice.Open(target, writable: !readOnly);
        FatxVolume? vol = null;
        try
        {
            CrashLog.Write($"Mount: device opened, length {dev.Length}");
            vol = FatxVolume.Open(dev, offset, size ?? dev.Length - offset, journaling && !readOnly);
            CrashLog.Write($"Mount: volume parsed ({vol.Flavor}, {vol.ClusterCount} clusters, {vol.FreeClusters} free, recovered {vol.RecoveredTransactions})");
            var fs = new FatxFileSystem(vol, readOnly);

            Dokan dokan;
            lock (DokanLock) dokan = _dokan ??= new Dokan(new NullLogger());

            var opts = DokanOptions.FixedDrive | DokanOptions.MountManager
                       | (readOnly ? DokanOptions.WriteProtection : 0);
            var instance = new DokanInstanceBuilder(dokan)
                .ConfigureOptions(o => { o.Options = opts; o.MountPoint = mountPoint; })
                .Build(fs);
            CrashLog.Write($"Mount: Dokan instance built on {mountPoint}");

            return new MountSession(target, partName, offset, mountPoint, readOnly, dev, vol, fs, instance);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Mount failed:\n" + ex);
            try { vol?.Dispose(); } catch { }
            dev.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        CrashLog.Write($"Unmount: {MountPoint}");
        try { _instance.Dispose(); } catch (Exception ex) { CrashLog.Write("Unmount dokan: " + ex); }   // unmounts the drive, no more callbacks after this
        try { if (!ReadOnly) Volume.Close(); } catch (Exception ex) { CrashLog.Write("Unmount close volume: " + ex); }   // zero-fill tails, flush, empty journal
        try { Volume.Dispose(); } catch (Exception ex) { CrashLog.Write("Unmount journal: " + ex); }
        try { _dev.Dispose(); } catch (Exception ex) { CrashLog.Write("Unmount device: " + ex); }
    }

    /// <summary>Call once when the application exits.</summary>
    public static void ShutdownDokan()
    {
        lock (DokanLock) { _dokan?.Dispose(); _dokan = null; }
    }
}
