using System.Text.RegularExpressions;
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
    private static DateTime _writerDate = DateTime.MinValue;

    /// <summary>
    /// 文件名里带日期时（服务端的 service-detail-yyyy-MM-dd.log），跨零点必须自己换文件：
    /// 路径是启动时算一次的，不滚就会整天往昨天的文件里写。
    /// </summary>
    private static void RollToTodayLocked()
    {
        if (_logPath == null) return;
        var today = DateTime.Now.Date;
        if (_writerDate == today) return;
        _writerDate = today;

        var name = Path.GetFileName(_logPath);
        var m = Regex.Match(name, @"\d{4}-\d{2}-\d{2}");
        if (!m.Success) return;   // 不带日期的（GUI 的 debug.log）不动

        var rolled = Path.Combine(Path.GetDirectoryName(_logPath) ?? "",
            name.Remove(m.Index, m.Length).Insert(m.Index, today.ToString("yyyy-MM-dd")));
        if (rolled == _logPath) return;
        _writer?.Dispose();
        _writer = null;
        _logPath = rolled;
    }

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
                RollToTodayLocked();
                // 复用 StreamWriter，避免每次 AppendAllText 都 new FileStream/StreamWriter
                if (_writer == null)
                {
                    var dir = Path.GetDirectoryName(_logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete,
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
                // H-? 修复：先释放 writer 句柄再移动文件，防止 FILE_SHARE_DELETE 不足时 MoveTo 失败
                _writer?.Dispose();
                _writer = null;
                fi.MoveTo(backupPath);
            }
        }
        catch (Exception) { }
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
