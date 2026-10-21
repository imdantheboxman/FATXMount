namespace FatxMount;

/// <summary>Append-only diagnostic log. Never throws.</summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxMount", "FatxMount.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never cause a failure */ }
    }
}
