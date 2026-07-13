using System.IO;

namespace SimpleDAW;

/// <summary>
/// Minimal best-effort file logger so hardware/driver failures that are
/// otherwise silently swallowed (e.g. an ASIO probe failing, a device
/// enumeration error) can be diagnosed after the fact without attaching a
/// debugger. Logging itself must never throw or affect app behaviour, so
/// every entry point swallows its own failures.
/// </summary>
public static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024; // 2 MB, then start fresh.

    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleDAW", "logs", "app.log");

    /// <summary>Appends a timestamped line describing a caught failure.</summary>
    public static void Warn(string context, Exception? ex = null)
    {
        try
        {
            string line = ex == null
                ? $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}"
                : $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}: {ex.GetType().Name}: {ex.Message}";

            lock (Sync)
            {
                string? dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                {
                    File.Delete(LogPath);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw or crash the app.
        }
    }
}
