using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// A3：事件型条件（app_start / app_close / 到点）在复合规则里只活一个轮询周期，
/// 于是 `app_start AND cpu_temp>=80` 要求两者落在同一拍——起游戏后温度要几十秒才爬上去，
/// 这个条件几乎永不成立，而且丢事件时没有任何日志。
///
/// 期望语义：事件产生一个待决脉冲，Triggered 保持到"被规则消费"或"窗口过期"为止。
/// </summary>
public class PulseConditionTests
{
    // ── 脉冲窗口机制本身 ──────────────────────────────────────────────

    [Fact]
    public async Task 脉冲_窗口内保持Triggered_过期后放开()
    {
        var pulse = new ScriptedPulseTrigger(TimeSpan.FromMilliseconds(80));
        pulse.Start();

        pulse.Raise();
        await pulse.PollAsync();
        Assert.Equal(TriggerState.Triggered, pulse.State);

        await pulse.PollAsync();                     // 窗口内：仍在待决
        Assert.Equal(TriggerState.Triggered, pulse.State);

        await Task.Delay(140);
        await pulse.PollAsync();                     // 过期：自动放开
        Assert.Equal(TriggerState.Monitoring, pulse.State);

        pulse.Dispose();
    }

    [Fact]
    public async Task 脉冲_被消费后立刻放开()
    {
        var pulse = new ScriptedPulseTrigger(TimeSpan.FromMinutes(5));
        pulse.Start();

        pulse.Raise();
        await pulse.PollAsync();
        Assert.Equal(TriggerState.Triggered, pulse.State);

        pulse.ConsumePulse();
        await pulse.PollAsync();
        Assert.Equal(TriggerState.Monitoring, pulse.State);

        pulse.Dispose();
    }

    // ── 规则集成：事件发生与另一条件不同拍 ────────────────────────────

    /// <summary>
    /// A3 的核心场景：应用先启动，温度过几拍才升上去。旧实现里事件早已消失，规则永不触发。
    /// </summary>
    [Fact]
    public async Task 应用启动后温度才升上去_规则仍要触发()
    {
        var app = new FakeAppStartTrigger();
        var cpu = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("app_start", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            app, cpu);

        app.Pids.Add(4321);              // 第 1 拍：应用启动
        await app.PollAsync();
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);     // 温度还没到，不派发（这一步是对的）

        await Task.Delay(30);            // 隔一拍（模拟温度爬升）
        cpu.Satisfy();                   // 第 2 拍：温度达标
        await cpu.PollAsync();
        await app.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.True(h.Trigger.ActionInFlight);      // 旧实现：事件已消失，这里永远是 false
        await h.WaitDispatchedAsync(1);
    }

    /// <summary>同一个事件只能触发一次：消费之后，另一个条件再抖动不得重发。</summary>
    [Fact]
    public async Task 事件被消费后_另一条件抖动不得重复触发()
    {
        var app = new FakeAppStartTrigger();
        var cpu = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("app_start", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            app, cpu);

        cpu.Satisfy();
        await cpu.PollAsync();
        app.Pids.Add(777);
        await app.PollAsync();
        h.Trigger.OnPollingComplete();              // 派发 #1
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
        h.ReleaseGate();
        await h.WaitIdleAsync();

        // 温度掉下去再回来：事件已经在 #1 时被消费掉了，不该再发一次
        cpu.Unsatisfy();
        await cpu.PollAsync();
        await app.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);

        cpu.Satisfy();
        await cpu.PollAsync();
        await app.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.False(h.Trigger.ActionInFlight);
        Assert.Equal(1, h.DispatchCount);
    }

    /// <summary>窗口过期之后事件作废：温度再升上来也不许拿旧事件触发。</summary>
    [Fact]
    public async Task 窗口过期后_旧事件不得再触发规则()
    {
        var app = new FakeAppStartTrigger(TimeSpan.FromMilliseconds(60));
        var cpu = new ScriptedTrigger();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("app_start", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            app, cpu);

        app.Pids.Add(555);
        await app.PollAsync();                      // 事件发生，温度未达标
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);

        await Task.Delay(140);                      // 事件过期
        cpu.Satisfy();
        await cpu.PollAsync();
        await app.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.False(h.Trigger.ActionInFlight);
        Assert.Equal(0, h.DispatchCount);
    }

    /// <summary>app_close 同样是事件：关闭后另一条件赶上来也要能触发。</summary>
    [Fact]
    public async Task 应用关闭后温度条件赶上_规则仍要触发()
    {
        var app = new FakeAppCloseTrigger();
        var cpu = new ScriptedTrigger();
        app.Pids.Add(999);              // 先让它处于"运行中"
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("app_close", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            app, cpu);

        app.Pids.Clear();               // 第 1 拍：应用关闭
        await app.PollAsync();
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);

        cpu.Satisfy();
        await cpu.PollAsync();
        await app.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
    }

    /// <summary>单条件的事件规则行为不变：仍然只发一次。</summary>
    [Fact]
    public async Task 单条件事件规则_只触发一次()
    {
        var app = new FakeAppStartTrigger();
        using var h = new RuleHarness(RuleHarness.BuildRule(("app_start", LogicalOperator.And)), app);

        app.Pids.Add(1234);
        await app.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.True(h.Trigger.ActionInFlight);
        await h.WaitDispatchedAsync(1);
        h.ReleaseGate();
        await h.WaitIdleAsync();

        for (var i = 0; i < 3; i++)
        {
            await app.PollAsync();
            h.Trigger.OnPollingComplete();
        }

        Assert.Equal(1, h.DispatchCount);
    }
}

/// <summary>受控进程表的 AppStart 触发器：不真的去起进程。</summary>
internal sealed class FakeAppStartTrigger : AppStartTrigger
{
    private readonly TimeSpan _window;

    public List<int> Pids { get; } = new();

    public FakeAppStartTrigger(TimeSpan? window = null)
        : base(new TriggerConfig { Type = "app_start", Parameter = "notepad" })
        => _window = window ?? TimeSpan.FromMinutes(5);

    protected override List<int> FindCurrentProcessIds() => Pids.ToList();
    protected override TimeSpan PulseWindow => _window;
}

/// <summary>受控进程表的 AppClose 触发器。</summary>
internal sealed class FakeAppCloseTrigger : AppCloseTrigger
{
    private readonly TimeSpan _window;

    public List<int> Pids { get; } = new();

    public FakeAppCloseTrigger(TimeSpan? window = null)
        : base(new TriggerConfig { Type = "app_close", Parameter = "notepad" })
        => _window = window ?? TimeSpan.FromMinutes(5);

    protected override List<int> FindCurrentProcessIds() => Pids.ToList();
    protected override TimeSpan PulseWindow => _window;
}
