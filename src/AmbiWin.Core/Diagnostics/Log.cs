using System.Globalization;

namespace AmbiWin.Core.Diagnostics;

/// <summary>
/// Minimal thread-safe logger: one line per event to an optional file and to the console.
/// Local only — nothing is ever sent anywhere.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    /// <summary>Starts writing to <paramref name="path"/> (appends). Safe to call once at startup.</summary>
    public static void OpenFile(string path)
    {
        lock (Gate)
        {
            _file?.Dispose();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            FilePath = path;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}");
        lock (Gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
