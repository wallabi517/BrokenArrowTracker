using System.Collections.Concurrent;
using System.Text;

namespace BrokenArrowSquadTracker;

public static class DiagnosticLog
{
    private const int MaxEntries = 1500;
    private static readonly ConcurrentQueue<string> Entries = new();
    private static readonly object FileLock = new();
    public static readonly string LivePath = Path.Combine(Path.GetTempPath(), "BrokenArrowTracker", "diagnostic-live.log");

    static DiagnosticLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LivePath)!);
            File.WriteAllText(LivePath, string.Empty, new UTF8Encoding(true));
        }
        catch { }
    }

    public static void Write(string category, string message)
    {
        var safeMessage = message.Replace("\r", " ").Replace("\n", " ");
        var entry = $"{DateTimeOffset.Now:O} [{category}] {safeMessage}";
        Entries.Enqueue(entry);
        while (Entries.Count > MaxEntries) Entries.TryDequeue(out _);
        try
        {
            lock (FileLock) File.AppendAllText(LivePath, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    public static void Export(string path)
    {
        var lines = new List<string>
        {
            "Broken Arrow Player Tracker diagnostic log",
            $"Exported: {DateTimeOffset.Now:O}",
            $"App version: {Application.ProductVersion}",
            $"OS: {Environment.OSVersion}",
            $"64-bit process: {Environment.Is64BitProcess}",
            ""
        };
        lines.AddRange(Entries);
        File.WriteAllLines(path, lines, new UTF8Encoding(true));
    }
}
