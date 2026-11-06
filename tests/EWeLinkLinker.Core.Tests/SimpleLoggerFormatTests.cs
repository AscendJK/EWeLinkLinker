using EWeLinkLinker.Core.Logging;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// detail 日志的时间戳只能有一个。
///
/// 现状：`OptimizedTriggerBase.Log` 与 `LinkerService.Log` 自己拼了 `[HH:mm:ss]`，
/// `SimpleLogger` 又拼一个，于是同一份 service-detail-*.log 里两种格式并存：
///   传感器行  [00:00:01] [00:00:01] [INFO] [CpuTempTrigger] CPU温度: 59°C
///   AUDIT 行  [00:18:13] [AUDIT] 设备命令 水冷 …
/// 按时间戳做正则抓数时会错位，人看也以为写了两遍。
/// </summary>
public class SimpleLoggerFormatTests
{
    private static string NewPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewl_log_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "service-detail-test.log");
    }

    /// <summary>把 SimpleLogger 指到临时文件，写完读回最后一行。</summary>
    private static string WriteAndReadBack(string message)
    {
        var path = NewPath();
        try
        {
            SimpleLogger.Initialize(path);
            SimpleLogger.Log(message);

            // 写入方一直持有写句柄（AutoFlush 的单例 StreamWriter），
            // 这里必须显式允许 ReadWrite 共享，否则读回会撞 IOException。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().TrimEnd('\r', '\n');
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void 不带时间戳的消息_由日志器补一个()
    {
        var line = WriteAndReadBack("[AUDIT] 设备命令 水冷 通道0 -> off 结果=ok");
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] \[AUDIT\] 设备命令", line);
    }

    [Fact]
    public void 已经带时间戳的消息_不再补第二个()
    {
        var line = WriteAndReadBack("[12:00:00] [INFO] [CpuTempTrigger] CPU温度: 59.0°C");
        Assert.Equal("[12:00:00] [INFO] [CpuTempTrigger] CPU温度: 59.0°C", line);
    }

    [Fact]
    public void 带毫秒的时间戳同样不重复()
    {
        var line = WriteAndReadBack("[12:00:00.123] ERROR: 出事了");
        Assert.Equal("[12:00:00.123] ERROR: 出事了", line);
    }

    [Fact]
    public void 方括号开头但不是时间戳的_仍然补时间()
    {
        var line = WriteAndReadBack("[LAN] 水冷 HTTP 200 error=0");
        Assert.Matches(@"^\[\d{2}:\d{2}:\d{2}\] \[LAN\] 水冷", line);
    }
}
