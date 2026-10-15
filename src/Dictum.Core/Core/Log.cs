using System.Text;

namespace Dictum.Core;

public enum LogLevel { Debug = 0, Info = 1, Warning = 2, Error = 3 }

/// <summary>Minimal logging sink. Implementations must never throw.</summary>
public interface ILog
{
    void Write(LogLevel level, string message);
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string message) => log.Write(LogLevel.Debug, message);
    public static void Info(this ILog log, string message) => log.Write(LogLevel.Info, message);
    public static void Warn(this ILog log, string message) => log.Write(LogLevel.Warning, message);
    public static void Error(this ILog log, string message) => log.Write(LogLevel.Error, message);

    public static LogLevel ParseLevel(string value) => value.ToLowerInvariant() switch
    {
        "debug" => LogLevel.Debug,
        "warning" or "warn" => LogLevel.Warning,
        "error" => LogLevel.Error,
        _ => LogLevel.Info,
    };
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Write(LogLevel level, string message) { }
}

/// <summary>
/// Replaces registered secret values with a fixed marker. Every log line passes through this,
/// so an API key that ends up inside an exception message is still never written.
/// </summary>
public sealed class SecretRedactor
{
    private readonly object _gate = new();
    private string[] _secrets = [];

    public void Register(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 6) return;
        lock (_gate)
        {
            if (Array.IndexOf(_secrets, secret) >= 0) return;
            _secrets = [.. _secrets, secret];
        }
    }

    public string Redact(string text)
    {
        var secrets = _secrets;
        foreach (var s in secrets)
        {
            if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        }
        return text;
    }
}

/// <summary>
/// Daily-rotated text log in the app's logs folder (%LOCALAPPDATA%\Dictum\logs on Windows). Keeps the newest 14 files,
/// counting the dictate-*.log files the earlier build wrote and the one-time migration moved here.
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const int KeepFiles = 14;
    public const string FilePrefix = "dictum-";
    private const string LegacyPrefix = "dictate-";
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly SecretRedactor _redactor;
    private readonly bool _echoToStderr;
    private StreamWriter? _writer;
    private string _currentDay = "";

    public FileLog(string dir, SecretRedactor redactor, LogLevel minLevel, bool echoToStderr = false)
    {
        _dir = dir;
        _redactor = redactor;
        MinLevel = minLevel;
        _echoToStderr = echoToStderr;
    }

    public LogLevel MinLevel { get; set; }

    /// <summary>After "Remove Dictum completely": every later line is dropped, so the deleted logs folder is not made again.</summary>
    public bool Stopped { get; private set; }

    public void Stop()
    {
        Stopped = true;
        Release();
    }

    public void Write(LogLevel level, string message)
    {
        if (level < MinLevel || Stopped) return;
        try
        {
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} {LevelTag(level)} [{Environment.CurrentManagedThreadId,3}] {_redactor.Redact(message)}";
            lock (_gate)
            {
                if (Stopped) return;
                EnsureWriter(now);
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            if (_echoToStderr && level >= LogLevel.Warning) Console.Error.WriteLine(line);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        _ => "ERROR",
    };

    private void EnsureWriter(DateTime now)
    {
        var day = now.ToString("yyyyMMdd");
        if (_writer is not null && day == _currentDay) return;
        _writer?.Dispose();
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{FilePrefix}{day}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _currentDay = day;
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        try
        {
            static string Day(FileInfo f) => Path.GetFileNameWithoutExtension(f.Name).Split('-')[^1];
            var dir = new DirectoryInfo(_dir);
            var files = dir.GetFiles(FilePrefix + "*.log").Concat(dir.GetFiles(LegacyPrefix + "*.log"))
                .OrderByDescending(Day, StringComparer.Ordinal)
                .Skip(KeepFiles);
            foreach (var f in files) f.Delete();
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Closes the current file (Settings → Storage → Delete logs); the next line opens a new one.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            _currentDay = "";
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
