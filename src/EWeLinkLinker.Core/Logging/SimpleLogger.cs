using Microsoft.Extensions.Logging;

namespace EWeLinkLinker.Core.Logging;

/// <summary>
/// Simple file logger for non-host contexts (e.g., WPF app).
/// Writes to debug.log with automatic trimming.
/// Uses a single reused StreamWriter to avoid finalizer queue buildup.
/// </summary>
public static class SimpleLogger
{
    private static readonly object LogLock = new();
    private static string? _logPath;
    private static long _writeCount;
    private static StreamWriter? _writer;  // 复用 StreamWriter，避免频繁 new FileStream

    /// <summary>
    /// Initialize the logger with a log file path. Call once at startup.
    /// </summary>
    public static void Initialize(string logPath)
    {
        lock (LogLock)
        {
            _logPath = logPath;
            _writer?.Dispose();
            _writer = null;  // 路径变化时重新创建
        }
    }

    // H-19 修复：批量写入缓冲
    private static readonly List<string> _batchBuffer = new();
    private const int BatchThreshold = 20;

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
                // 复用 StreamWriter，避免每次 AppendAllText 都 new FileStream/StreamWriter
                if (_writer == null)
                {
                    var dir = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                        bufferSize: 4096, useAsync: false);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                }
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

                _writeCount++;
                if (_writeCount % 200 == 0)
                {
                    TrimLog();
                }
            }
        }
        catch (Exception) { /* H-10 修复：不吞致命异常 */ }
    }

    /// <summary>
    /// H-19 修复：批量写入日志，减少文件 IO 次数（适用于设备发现等高频场景）
    /// </summary>
    public static void LogBatch(IEnumerable<string> messages)
    {
        if (string.IsNullOrEmpty(_logPath)) return;

        try
        {
            lock (LogLock)
            {
                if (_writer == null)
                {
                    var dir = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                        bufferSize: 4096, useAsync: false);
                    _writer = new StreamWriter(stream) { AutoFlush = true };
                }
                foreach (var message in messages)
                {
                    _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                }

                _writeCount += BatchThreshold;
                if (_writeCount % 200 == 0)
                {
                    TrimLog();
                }
            }
        }
        catch (Exception) { /* H-10 修复：不吞致命异常 */ }
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
        catch (Exception) { /* H-10 修复：不吞致命异常 */ }
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
