using System.Collections.Concurrent;
using EWeLinkLinker.Core.Logging;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 服务端日志的入队语义必须是"永不阻塞"。
///
/// 原来用的是 <c>BlockingCollection.TryAdd(item)</c>——那是**无限等待**的重载，队列满（容量 1000）
/// 就卡住调用方。而调用方包括跑在 SCM 控制线程上的 OnStart/OnStop：
/// 写线程一旦停止消费，服务就变成"既起不来也停不下"。
/// </summary>
public class ServiceLoggerQueueTests
{
    [Fact]
    public void 队列满时_立刻返回false而不是阻塞()
    {
        // 没有消费者：填满即满
        using var queue = new BlockingCollection<string>(boundedCapacity: 2);

        Assert.True(ServiceLogger.TryEnqueueWithoutBlocking(queue, "one"));
        Assert.True(ServiceLogger.TryEnqueueWithoutBlocking(queue, "two"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var accepted = ServiceLogger.TryEnqueueWithoutBlocking(queue, "three");
        sw.Stop();

        Assert.False(accepted);                                  // 满了：丢弃
        Assert.True(sw.ElapsedMilliseconds < 200,               // 而不是挂住
            $"入队阻塞了 {sw.ElapsedMilliseconds}ms，退化成无限等待");
    }

    [Fact]
    public async Task 日志器在写不进去时_不抛异常也不卡住()
    {
        // 把日志路径指向"某个文件下面"，OpenWriterForDate 必然失败
        var blocker = Path.Combine(Path.GetTempPath(), "ewl_blocker_" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(blocker, "x");
        try
        {
            using var logger = new ServiceLogger(Path.Combine(blocker, "nested", "service-test.log"), enabled: true);

            // 放后台跑：真要是退化成阻塞，用例会失败而不是把整个测试进程挂死
            var writerLoop = Task.Run(() =>
            {
                for (var i = 0; i < 3000; i++) logger.Info("line " + i);   // 远超队列容量
            });

            var completed = await Task.WhenAny(writerLoop, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(writerLoop, completed);
        }
        finally
        {
            try { File.Delete(blocker); } catch { }
        }
    }
}
