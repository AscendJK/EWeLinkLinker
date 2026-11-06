using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// B2：非传感器触发器（time / interval / app_start / app_close）从不写 LastReadingAvailable，
/// 一旦某轮评估抛过异常，基类把它置成 false 之后再也没有人改回来 ⇒ 之后每次启动播种
/// 都把这几个条件当成"读数未知"，空跑满 3 遍 ×1.2s，还写一条误导性的 WARN。
/// </summary>
public class OptimizedTriggerBaseTests
{
    [Fact]
    public async Task 评估抛异常后_下一轮成功必须恢复读数可得()
    {
        var trigger = new ScriptedTrigger();
        trigger.Start();

        trigger.FailNextPoll = true;
        await trigger.PollAsync();
        Assert.False(trigger.ReadingKnown);      // 这一轮确实没有定论（这条是对的，要保住）

        trigger.FailNextPoll = false;
        await trigger.PollAsync();
        Assert.True(trigger.ReadingKnown);       // 旧实现永远停在 false

        trigger.Dispose();
    }

    [Fact]
    public async Task 评估抛异常时_已锁存的条件保持锁存不改判()
    {
        var trigger = new ScriptedTrigger();
        trigger.Start();

        trigger.Satisfy();
        await trigger.PollAsync();
        Assert.Equal(TriggerState.Triggered, trigger.State);

        trigger.FailNextPoll = true;
        await trigger.PollAsync();

        // 抛异常 = 这一轮没有定论，不能把已满足判成未满足
        Assert.Equal(TriggerState.Triggered, trigger.State);
        Assert.False(trigger.ReadingKnown);

        trigger.Dispose();
    }

    [Fact]
    public async Task 未锁存时抛异常_转Error并在下一轮自动重试()
    {
        var trigger = new ScriptedTrigger();
        trigger.Start();

        trigger.FailNextPoll = true;
        await trigger.PollAsync();
        Assert.Equal(TriggerState.Error, trigger.State);

        trigger.FailNextPoll = false;
        await trigger.PollAsync();
        Assert.Equal(TriggerState.Monitoring, trigger.State);

        trigger.Dispose();
    }
}
