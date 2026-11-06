using System.Diagnostics;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 一条真实进程的冒烟测试：其余用例用注入的进程表驱动逻辑，
/// 这里确认真实路径（Process.GetProcesses + FindMatchingProcessIds）也真的会产生事件。
/// </summary>
public class AppTriggerRealProcessTests
{
    [Fact]
    public async Task 真实进程启动时产生脉冲_并且不会立刻消失()
    {
        var trigger = new AppStartTrigger(new TriggerConfig { Type = "app_start", Parameter = "cmd" });
        trigger.Start();                    // 先快照现有 cmd，之后起的才算"新"

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(child);

        try
        {
            await Task.Delay(400);          // 等它进了进程表
            await trigger.PollAsync();
            Assert.Equal(TriggerState.Triggered, trigger.State);

            // 关键：下一拍脉冲仍在（这正是 A3 之前的缺陷——事件只活一个轮询周期）
            await trigger.PollAsync();
            Assert.Equal(TriggerState.Triggered, trigger.State);
        }
        finally
        {
            try { child!.Kill(entireProcessTree: true); } catch { }
            trigger.Dispose();
        }
    }
}
