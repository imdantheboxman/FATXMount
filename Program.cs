using System.Runtime.InteropServices;

namespace FatxMount;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] a)
    {
        InstallCrashHandlers(gui: a.Length == 0);

        if (a.Length == 0)
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        AttachConsole(-1);   // WinExe has no console of its own; reuse the parent's for CLI mode
        FatxFileSystem.Log = Console.Error.WriteLine;
        try
        {
            if (a[0] == "list" && a.Length >= 2) return List(a);
            if (a[0] == "mount" && a.Length >= 2) return Mount(a);
            if (a[0] == "create-image" && a.Length >= 3) return CreateImage(a);
            Usage();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static void InstallCrashHandlers(bool gui)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("FATAL unhandled exception (process is terminating):\n" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write("Unobserved task exception:\n" + e.Exception);
            e.SetObserved();
        };
        if (!gui) return;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            CrashLog.Write("UI thread exception:\n" + e.Exception);
            MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}\n\nDetails were written to:\n{CrashLog.LogPath}",
                "FatxMount", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
    }

    private static void Usage() => Console.WriteLine("""
        FatxMount            (no arguments: opens the GUI)

        Command line (run from an elevated terminal):
          FatxMount list <disk>
          FatxMount mount <disk> (--partition X|Y|Z|C|E|Data360 | --offset N [--size N])
                         [--drive Z] [--readonly] [--nojournal]
          FatxMount create-image <file.img> <sizeMB> [--x360]

        <disk> = disk number (e.g. 2), \\.\PhysicalDrive2, or a raw image file. Numbers accept 0x-hex.
        """);

    private static string? Opt(string[] a, string name)
    {
        int i = Array.IndexOf(a, name);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

    private static long Num(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(s[2..], 16) : long.Parse(s);

    private static int List(string[] a)
    {
        using var dev = BlockDevice.Open(a[1], writable: false);
        Console.WriteLine($"{dev.Description}: {dev.Length / (1024.0 * 1024 * 1024):F2} GiB");
        foreach (var p in KnownPartitions.All)
            Console.WriteLine($"  {p.Name,-8} offset 0x{p.Offset:X}  {FatxVolume.GetSignature(dev, p.Offset) ?? "-"}");
        Console.WriteLine($"  raw      offset 0x0  {FatxVolume.GetSignature(dev, 0) ?? "-"}");
        return 0;
    }

    private static int CreateImage(string[] a)
    {
        string path = a[1];
        long mb = long.Parse(a[2]);
        bool x360 = Flag(a, "--x360");
        using (var fs = File.Create(path)) fs.SetLength(mb << 20);
        using var dev = BlockDevice.Open(path, writable: true);
        var (clusters, clusterSize) = FatxVolume.Format(dev, 0, dev.Length, x360);
        Console.WriteLine($"Created {path}: {(x360 ? "XTAF (Xbox 360)" : "FATX (original Xbox)")}, {clusters} clusters of {clusterSize} bytes");
        return 0;
    }

    private static int Mount(string[] a)
    {
        bool ro = Flag(a, "--readonly");
        string drive = Opt(a, "--drive") ?? "X";
        string? part = Opt(a, "--partition");
        long off; long? size = null; string name;

        if (part != null)
        {
            var k = KnownPartitions.All.FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"Unknown partition '{part}'");
            off = k.Offset; size = k.Size; name = k.Name;
        }
        else
        {
            off = Num(Opt(a, "--offset") ?? throw new ArgumentException("Need --partition or --offset"));
            name = $"offset 0x{off:X}";
        }
        if (Opt(a, "--size") is { } s) size = Num(s);

        using var session = MountSession.Start(a[1], name, off, size, ro, drive, journaling: !Flag(a, "--nojournal"));
        if (session.Volume.RecoveredTransactions > 0)
            Console.WriteLine($"Recovered {session.Volume.RecoveredTransactions} pending transaction(s) from the journal.");
        Console.WriteLine($"{session.Volume.Flavor}: mounted on {session.MountPoint} ({(ro ? "read-only" : "read/write")}). Ctrl+C to unmount.");

        using var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        quit.Wait();

        Console.WriteLine("Unmounting...");
        return 0;
    }
}
