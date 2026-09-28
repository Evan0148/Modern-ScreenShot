using System.IO;
using System.Text;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Services;

/// <summary>Thread-safe append-only file logger at %LOCALAPPDATA%\Modern-ScreenShot\logs\app.log, rolling at 2 MB.</summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int MaxConsecutiveFailures = 10;
    private static readonly object Gate = new();
    private static bool _broken;
    private static int _consecutiveFailures;

    public static string FilePath { get; } = Path.Combine(AppPaths.LogDir, "app.log");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);
    public static void Error(Exception ex) => Write("ERROR", ex.Message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(level).Append("] [T")
          .Append(Environment.CurrentManagedThreadId).Append("] ").Append(message);
        if (ex is not null) sb.AppendLine().Append(ex);
        sb.AppendLine();
        string line = sb.ToString();
        System.Diagnostics.Debug.Write(line);

        lock (Gate)
        {
            if (_broken) return;
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = FilePath + ".1";
                    File.Move(FilePath, old, overwrite: true);
                }
                File.AppendAllText(FilePath, line, Encoding.UTF8);
                _consecutiveFailures = 0;
            }
            catch (Exception ioEx) when (ioEx is IOException or UnauthorizedAccessException)
            {
                // A transient sharing violation (AV scan, --smoke alongside the app) must not kill
                // logging forever; only give up after repeated consecutive failures.
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _broken = true;
                    System.Diagnostics.Debug.WriteLine($"Log disabled after {_consecutiveFailures} failures: {ioEx.Message}");
                }
            }
        }
    }
}
