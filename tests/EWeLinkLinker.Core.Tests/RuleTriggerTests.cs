using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// RuleTrigger 的判定/派发语义。
///
/// 判定"这一拍有没有派发"用 <see cref="RuleTrigger.ActionInFlight"/> —— 它在 CAS 那一刻
/// 同步置位，是确定性的；派发计数是在线程池里自增的，只能等。
/// </summary>
public class RuleTriggerTests
{
    /// <summary>
    /// A2：规则先满足 → 解除 → 再满足。旧的纠偏判据只看"盖章已过期"，没要求上一拍也满足，
    /// 于是这一拍被强行按成不满足，动作白等一个轮询周期（日志还会谎称"已连续满足满 30 分钟"）。
    /// </summary>
    [Fact]
    public async Task 解除后再次满足_必须在同一拍派发()
    {
        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And)), condition);
        h.Trigger.ReconcileHoldOverride = TimeSpan.FromTicks(1);   // 视作"计时早就满了"

        // 第 1 拍：满足 → 派发
        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
        h.ReleaseGate();
        await h.WaitIdleAsync();

        // 第 2 拍：解除（造下降沿）
        condition.Unsatisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);

        // 第 3 拍：重新满足 —— 这是真实的上升沿，必须当拍派发
        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);      // 旧实现这里是 false（被纠偏吃掉）
        await h.WaitDispatchedAsync(2);
        Assert.Equal(2, h.DispatchCount);
    }

    /// <summary>
    /// 默认（ReconcileMinutes = 0）不纠偏：条件一直满足时**不会**每 30 分钟重发一遍。
    /// 现场那条恒真规则以前一天多发 55 次命令，就是这么来的。
    /// </summary>
    [Fact]
    public async Task 默认不纠偏_持续满足只发一次()
    {
        var rule = RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And));
        Assert.Equal(0, rule.ReconcileMinutes);

        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(rule, condition);
        Assert.Equal(TimeSpan.Zero, h.Trigger.ReconcileHold);

        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();
        h.ReleaseGate();
        await h.WaitIdleAsync();

        for (var i = 0; i < 20; i++)
        {
            await condition.PollAsync();
            h.Trigger.OnPollingComplete();
        }

        Assert.Equal(1, h.DispatchCount);
    }

    /// <summary>
    /// A1：派发前的日志调用抛异常时，旧的 try/finally 写在 Task.Run 内部，永远不会执行，
    /// 于是 _actionInFlight 恒为 1，这条规则此后只会打"已有动作在飞，本次跳过"。
    /// </summary>
    [Fact]
    public async Task 派发前抛异常_不得锁死动作闸门()
    {
        var boom = true;
        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And)),
            (_, _) =>
            {
                if (boom) throw new InvalidOperationException("日志调用炸了");
            },
            condition);

        condition.Satisfy();
        await condition.PollAsync();

        // 旧实现会把异常抛出 OnPollingComplete，并留下 _actionInFlight = 1
        try { h.Trigger.OnPollingComplete(); }
        catch (InvalidOperationException) { }

        Assert.False(h.Trigger.ActionInFlight);
        Assert.Equal(0, h.DispatchCount);

        // 恢复后必须能正常派发
        boom = false;
        condition.Unsatisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();

        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.True(h.Trigger.ActionInFlight);          // 旧实现：闸门焊死，这里永远 false
        await h.WaitDispatchedAsync(1);
        Assert.Equal(1, h.DispatchCount);
    }

    /// <summary>
    /// 显式开了 ReconcileMinutes 的规则，仍保留"每满一个周期重发一次"的对账能力
    /// （给"设备可能被人手动改过"这种场景用）。默认是关的，见上面那条用例。
    /// </summary>
    [Fact]
    public async Task 显式开启纠偏后_每满一个周期重发一次()
    {
        var rule = RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And));
        rule.ReconcileMinutes = 30;

        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(rule, condition);
        h.Trigger.ReconcileHoldOverride = TimeSpan.FromTicks(1);   // 视作"周期已满"

        async Task PollAsync()
        {
            condition.Satisfy();
            await condition.PollAsync();
            h.Trigger.OnPollingComplete();
        }

        await PollAsync();                              // 派发 1
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
        h.ReleaseGate();
        await h.WaitIdleAsync();

        await PollAsync();                              // 强制重判（造下降沿），不派发
        Assert.False(h.Trigger.ActionInFlight);

        await PollAsync();                              // 重新上升 → 派发 2
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(2);
        Assert.Equal(2, h.DispatchCount);
    }

    /// <summary>组内 AND：缺一个不派发，凑齐才派发。</summary>
    [Fact]
    public async Task 组内AND_缺一个条件不派发()
    {
        var a = new ScriptedTrigger();
        var b = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And), ("gpu_temp", LogicalOperator.And)),
            a, b);

        a.Satisfy();
        await a.PollAsync();
        await b.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);

        b.Satisfy();
        await b.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
    }

    /// <summary>组间 OR：任一组的条件凑齐即派发。</summary>
    [Fact]
    public async Task 组间OR_任一组满足即派发()
    {
        var a = new ScriptedTrigger();
        var b = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And), ("time", LogicalOperator.Or)),
            a, b);

        b.Satisfy();                 // 只满足第二组
        await a.PollAsync();
        await b.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
    }

    /// <summary>边沿语义：持续满足只派发一次（不叠加重复下发）。</summary>
    [Fact]
    public async Task 持续满足_只派发一次()
    {
        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And)), condition);

        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
        h.ReleaseGate();
        await h.WaitIdleAsync();

        for (var i = 0; i < 3; i++)
        {
            await condition.PollAsync();
            h.Trigger.OnPollingComplete();
            Assert.False(h.Trigger.ActionInFlight);
        }

        Assert.Equal(1, h.DispatchCount);
    }

    /// <summary>启动基线：服务起来时条件已满足，第一拍不得当成新跳变重发动作。</summary>
    [Fact]
    public async Task 启动基线_已满足的规则第一拍不派发()
    {
        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And)), condition);

        condition.Satisfy();
        await condition.PollAsync();

        h.Trigger.SeedBaseline();
        h.Trigger.OnPollingComplete();

        Assert.False(h.Trigger.ActionInFlight);
        Assert.Equal(0, h.DispatchCount);
    }

    /// <summary>上一批还在飞时，新的一拍只跳过、不并发下发（保护同一插座不被两批写）。</summary>
    [Fact]
    public async Task 动作在飞时不并发派发第二批()
    {
        var rule = RuleHarness.BuildRule(("cpu_temp", LogicalOperator.And));
        rule.ReconcileMinutes = 30;     // 打开纠偏，好让它反复尝试派发

        var condition = new ScriptedTrigger();
        using var h = new RuleHarness(rule, condition);
        h.Trigger.ReconcileHoldOverride = TimeSpan.FromTicks(1);

        condition.Satisfy();
        await condition.PollAsync();
        h.Trigger.OnPollingComplete();      // 派发，闸门不放
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);

        // 强制重判 → 再上升，但闸门还关着
        for (var i = 0; i < 3; i++)
        {
            await condition.PollAsync();
            h.Trigger.OnPollingComplete();
        }

        await Task.Delay(50);
        Assert.Equal(1, h.DispatchCount);   // 没有第二批

        h.ReleaseGate();
        await h.WaitIdleAsync();
    }
}
