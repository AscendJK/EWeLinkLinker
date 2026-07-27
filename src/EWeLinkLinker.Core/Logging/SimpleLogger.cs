using Microsoft.Extensions.Logging;

namespace EWeLinkLinker.Core.Logging;

/// <summary>
/// Simple file logger for non-host contexts (e.g., WPF app).
/// Writes to debug.log with automatic trimming.
/// </summary>
public static class SimpleLogger
{
    private static readonly object LogLock = new();
    private static string? _logPath;
    private static long _writeCount;

    /// <summary>
    /// Initialize the logger with a log file path. Call once at startup.
    /// </summary>
    public static void Initialize(string logPath)
    {
        _logPath = logPath;
    }

    /// <summary>
    /// Write a log message with timestamp.
    /// </summary>
    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(_logPath)) return;

        try
        {
            lock (LogLock)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");

                _writeCount++;
                if (_writeCount % 200 == 0)
                {
                    TrimLog();
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Trim log file if it exceeds 2MB.
    /// </summary>
    public static void TrimLog(long maxSizeBytes = 2_097_152)
    {
        try
        {
            if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath)) return;
            var fi = new FileInfo(_logPath);
            if (fi.Length > maxSizeBytes)
            {
                var backupPath = _logPath + ".old";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                fi.MoveTo(backupPath);
            }
        }
        catch { }
    }
}

/// <summary>
/// Adapter that implements ILogger and writes to SimpleLogger.
/// Can be passed to services that expect ILogger&lt;T&gt;.
/// </summary>
public class SimpleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        SimpleLogger.Log($"[{typeof(T).Name}] {message}");
    }
}
