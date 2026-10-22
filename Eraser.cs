using System.Security.Cryptography;

namespace FatxMount;

public enum EraseMode
{
    QuickFormat,        // write a fresh, empty file system only (fast; old data stays on the disk but unreachable)
    ZeroThenFormat,     // overwrite everything with zeros, then format
    RandomThenFormat,   // overwrite everything with random data, then format
    WipeZero            // overwrite everything with zeros and leave it unformatted
}

/// <summary>One partition to create: where it is, how big, and which flavour of file system.</summary>
public sealed record LayoutPartition(string Name, long Offset, long Size, bool BigEndian)
{
    public string FileSystem => BigEndian ? "XTAF" : "FATX";
}

public static class Eraser
{
    public static void Run(BlockDevice dev, IReadOnlyList<(string name, long off, long size)> targets, EraseMode mode,
                           bool bigEndian, uint sectorsPerCluster, Action<double, string> report, CancellationToken ct)
        => RunLayout(dev, targets.Select(t => new LayoutPartition(t.name, t.off, t.size, bigEndian)).ToList(),
                     mode, sectorsPerCluster, report, ct);

    public static void RunLayout(BlockDevice dev, IReadOnlyList<LayoutPartition> parts, EraseMode mode,
                                 uint sectorsPerCluster, Action<double, string> report, CancellationToken ct)
    {
        bool fill = mode != EraseMode.QuickFormat;
        bool format = mode != EraseMode.WipeZero;
        long total = fill ? parts.Sum(t => t.Size) : 0;
        long done = 0;
        var buf = new byte[4 << 20];                       // stays all zeros unless random mode refills it
        double lastReported = -1;

        for (int i = 0; i < parts.Count; i++)
        {
            var t = parts[i];
            ct.ThrowIfCancellationRequested();

            if (fill)
            {
                for (long pos = 0; pos < t.Size;)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buf.Length, t.Size - pos);
                    if (mode == EraseMode.RandomThenFormat) RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                    dev.Write(t.Offset + pos, buf.AsSpan(0, n));
                    pos += n; done += n;

                    double f = (double)done / total;
                    if (f - lastReported >= 0.001) { lastReported = f; report(f, $"Erasing {t.Name}…  {f:P0}"); }
                }
                dev.Flush();
            }

            if (format)
            {
                ct.ThrowIfCancellationRequested();
                report(fill ? (double)done / total : (double)i / parts.Count, $"Formatting {t.Name}…");
                CrashLog.Write($"Format: {t.Name} offset 0x{t.Offset:X} size {t.Size} ({t.FileSystem}, spc {sectorsPerCluster})");
                FatxVolume.Format(dev, t.Offset, t.Size, t.BigEndian, sectorsPerCluster);
            }
        }
        dev.Flush();
        report(1.0, "Finished");
    }
}

/// <summary>
/// Standard partition layouts for hard drives used in MODDED consoles. Nothing below 0x80000 is ever written.
/// </summary>
public static class DriveLayouts
{
    private const long Gi = 1L << 30;

    /// <summary>Original Xbox: X, Y, Z, C and E. E uses up to 128 GiB (the original kernel's 28-bit LBA limit).</summary>
    public static List<LayoutPartition>? OriginalXbox(long deviceLength, out string? problem)
    {
        problem = null;
        if (deviceLength < 0xABE80000L + Gi)
        {
            problem = "This disk is too small for the original Xbox layout (needs at least about 3 GiB).";
            return null;
        }
        return KnownPartitions.OriginalLayout(deviceLength)
            .Select(p => new LayoutPartition(p.name, p.offset, p.size, false)).ToList();
    }

    /// <summary>
    /// Xbox 360 internal drive layout: compatibility, cache and system partitions followed by the Data partition.
    /// EXPERIMENTAL: the offsets are written from memory of the standard layout and are not verified against a console.
    /// </summary>
    public static List<LayoutPartition>? Xbox360(long deviceLength, out string? problem)
    {
        problem = null;
        const long dataStart = 0x130EB0000;
        if (deviceLength < dataStart + Gi)
        {
            problem = "This disk is too small for the Xbox 360 layout (needs more than about 6 GiB).";
            return null;
        }
        return new List<LayoutPartition>
        {
            new("Compatibility", 0x000080000, 0x80000000, false),   // original-Xbox backwards compatibility (FATX)
            new("Cache 0",       0x080080000, 0x80000000, true),
            new("Cache 1",       0x100080000, 0x0C000000, true),
            new("System 1",      0x10C080000, 0x0CE30000, true),
            new("System 2",      0x118EB0000, 0x08000000, true),
            new("System 3",      0x120EB0000, 0x10000000, true),
            new("Data360",       dataStart,   deviceLength - dataStart, true),
        };
    }
}
