using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// interval 的语义是"节拍"：每 N 分钟敲一下，守卫条件在那一拍上判。
/// 所以它不需要 A3 那种脉冲窗口（给了反而变成"每 30 分钟里偷看 60 秒"）。
///
/// 这里锁两件事：
///  1. 文档承诺的范围 1–1440 必须真的被校验，否则 interval 100000 静默等于"永不触发"；
///  2. 节拍不能因为"改了别的规则导致触发器重建"而清零。
/// </summary>
public class IntervalTriggerTests
{
    private static TriggerConfig Config(string parameter, string key = "") =>
        new() { Type = "interval", Parameter = parameter, Key = key };

    // ── 范围校验 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("1")]
    [InlineData("30")]
    [InlineData("1440")]
    public void 范围内_可以创建(string parameter)
        => Assert.NotNull(new IntervalTrigger(Config(parameter)));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1441")]
    [InlineData("100000")]
    [InlineData("abc")]
    [InlineData("")]
    public void 范围外_必须拒绝(string parameter)
        => Assert.ThrowsAny<ArgumentException>(() => new IntervalTrigger(Config(parameter)));

    [Fact]
    public void 拒绝时要说清范围()
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => new IntervalTrigger(Config("1441")));
        Assert.Contains("1440", ex.Message);
    }

    // ── 节拍 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 距上次已满间隔_第一拍就触发()
    {
        var key = "cond-" + Guid.NewGuid().ToString("N")[..8];
        IntervalTickState.Remember(key + "|1", DateTime.Now.AddMinutes(-2));

        var trigger = new IntervalTrigger(Config("1", key));
        trigger.Start();
        Assert.True(await trigger.PollAsync());

        trigger.Dispose();
    }

    [Fact]
    public async Task 距上次未满间隔_不触发()
    {
        var key = "cond-" + Guid.NewGuid().ToString("N")[..8];

        var trigger = new IntervalTrigger(Config("30", key));
        trigger.Start();
        Assert.False(await trigger.PollAsync());

        trigger.Dispose();
    }

    [Fact]
    public async Task 触发之后_下一拍不再重复触发()
    {
        var key = "cond-" + Guid.NewGuid().ToString("N")[..8];
        IntervalTickState.Remember(key + "|1", DateTime.Now.AddMinutes(-2));

        var trigger = new IntervalTrigger(Config("1", key));
        trigger.Start();
        Assert.True(await trigger.PollAsync());
        Assert.False(await trigger.PollAsync());     // 刚敲过，节拍已重置

        trigger.Dispose();
    }

    /// <summary>
    /// 核心：改别的规则会重建触发器（TriggerManager.ReloadAsync 重新 new）。
    /// 重建时若把倒计时清零，"每隔 30 分钟"就变成"每隔 30 分钟且期间没人动过配置"。
    /// </summary>
    [Fact]
    public async Task 触发器重建后_节拍沿用上次_不从头计时()
    {
        var key = "cond-" + Guid.NewGuid().ToString("N")[..8];
        IntervalTickState.Remember(key + "|1", DateTime.Now.AddMinutes(-2));

        var first = new IntervalTrigger(Config("1", key));
        first.Start();
        Assert.True(await first.PollAsync());        // 敲了第一拍
        first.Dispose();

        // 模拟一次热重载：同一条条件、同一间隔，重新构造一个实例
        var rebuilt = new IntervalTrigger(Config("1", key));
        rebuilt.Start();
        Assert.False(await rebuilt.PollAsync());     // 刚敲过，重建不该立刻再敲
        rebuilt.Dispose();
    }

    [Fact]
    public async Task 间隔值变了_重新计时()
    {
        var key = "cond-" + Guid.NewGuid().ToString("N")[..8];
        IntervalTickState.Remember(key + "|30", DateTime.Now.AddMinutes(-2));

        // 同一条条件，但间隔从 30 分钟改成 30 分钟以外的值 → 旧节拍不该沿用
        var trigger = new IntervalTrigger(Config("60", key));
        trigger.Start();
        Assert.False(await trigger.PollAsync());

        trigger.Dispose();
    }

    [Fact]
    public async Task 没有Key时_退化为每次Start都从头计时()
    {
        // 事件路径的临时触发器不带 Key：它们只判一次，不该污染任何节拍
        var trigger = new IntervalTrigger(Config("30"));
        trigger.Start();
        Assert.False(await trigger.PollAsync());
        trigger.Dispose();
    }

    /// <summary>
    /// 光有 IntervalTickState 没用：RuleTrigger 必须把「条件 Id」真的传下来，
    /// 否则每条 interval 条件的 Key 都是空串，节拍根本存不住。
    /// </summary>
    [Fact]
    public async Task RuleTrigger必须把条件Id传给interval()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewl_tests_" + Guid.NewGuid().ToString("N")[..8]);
        using var logger = new EWeLinkLinker.Core.Logging.ServiceLogger(Path.Combine(dir, "service-test.log"), enabled: false);
        try
        {
            var rule = new LinkerRule { Name = "间隔规则", Enabled = true };
            rule.Conditions.Add(new RuleCondition
            {
                Id = "abc12345",
                Type = "interval",
                Parameter = "30",
                Comparison = ComparisonOperator.Gte,
                Operator = LogicalOperator.And,
            });

            var scheduler = new PollingScheduler(logger);
            var ruleTrigger = new RuleTrigger(rule, logger, scheduler, (_, _) => Task.CompletedTask,
                                              static (_, _) => { }, presets: null);
            await ruleTrigger.InitializeAsync();

            var trigger = Assert.IsType<IntervalTrigger>(Assert.Single(ruleTrigger.GetTriggers()));
            Assert.Equal("abc12345|30", trigger.TickKey);

            ruleTrigger.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
