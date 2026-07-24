using System.Globalization;
using System.IO;
using System.Text;

namespace AgentCompanion.Services;

internal static class AppLogger
{
    private const long MaxLogBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "pet_data",
        "agentcompanion.log");

    public static void Error(string message, Exception exception)
    {
        Write("ERROR", $"{message} {exception.GetType().Name}: {exception.Message}");
    }

    public static void Warning(string message)
    {
        Write("WARN", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (directory != null)
                    Directory.CreateDirectory(directory);
                RotateIfNeeded();
                var sanitized = message.Replace("\r", " ", StringComparison.Ordinal)
                    .Replace("\n", " ", StringComparison.Ordinal);
                var line = $"[{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)}] [{level}] {sanitized}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // A logging failure must not terminate the desktop process.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath) || new FileInfo(LogPath).Length < MaxLogBytes)
            return;
        var backupPath = LogPath + ".1";
        if (File.Exists(backupPath))
            File.Delete(backupPath);
        File.Move(LogPath, backupPath);
    }
}
