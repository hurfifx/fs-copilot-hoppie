namespace FsCopilot.Hoppie;

using System.Text;

internal static class HoppieDump
{
    private static readonly object Sync = new();

    public const string FileName = "hoppie-dump.txt";

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static void MarkRunStart(string version, string buildLabel)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory);
                using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [RUN] START pid={Environment.ProcessId} version={version} build={buildLabel}");
            }
        }
        catch
        {
            // ignored
        }
    }

    public static void MarkRunEnd() => Write("RUN", $"END pid={Environment.ProcessId}");

    public static void Write(string category, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? AppContext.BaseDirectory);
                using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                var lines = (message ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                foreach (var line in lines)
                    writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{category}] {line}");
            }
        }
        catch
        {
            // ignored
        }
    }

    public static void WriteException(string category, string context, Exception exception)
    {
        Write(category, $"{context}{Environment.NewLine}{exception}");
    }
}
