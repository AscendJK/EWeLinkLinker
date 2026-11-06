using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Services;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// B1：含电源事件条件（boot/shutdown/sleep/wake）的规则原来被整条踢出轮询，
/// 非电源条件只在电源事件到来时评估一次。后果：
///  · `boot OR app_start` 的 app_start 分支**永不触发**；
///  · `wake AND cpu_temp>80` 只在唤醒那一瞬看一眼温度；
///  · 事件路径还把**所有**电源条件无条件当满足，`wake AND sleep` 任一端都能白送一次。
/// </summary>
public class MixedPowerRuleTests
{
    private static LinkerRule BuildRule(params (string Type, LogicalOperator Op)[] conditions)
    {
        var rule = new LinkerRule { Name = "混合规则", Enabled = true };
        foreach (var (type, op) in conditions)
        {
            rule.Conditions.Add(new RuleCondition
            {
                Type = type,
                Parameter = type is "boot" or "shutdown" or "sleep" or "wake" ? "" : "50",
                Comparison = ComparisonOperator.Gte,
                Operator = op,
            });
        }
        return rule;
    }

    // ── 事件路径：只有与本次事件同名的电源条件算满足 ──────────────────

    [Fact]
    public async Task 事件路径_同一条规则里的其它电源条件不算满足()
    {
        var service = new LinkerService(null!, null!, "unused.json");
        var rule = BuildRule(("wake", LogicalOperator.And), ("sleep", LogicalOperator.And));

        // 旧实现把两个电源条件都当满足 ⇒ 唤醒事件会白送一次睡眠规则
        Assert.False(await service.EvaluateRuleConditionsOnEventAsync(rule, CancellationToken.None, "wake"));
    }

    [Fact]
    public async Task 事件路径_与事件同名的电源条件算满足()
    {
        var service = new LinkerService(null!, null!, "unused.json");
        var rule = BuildRule(("wake", LogicalOperator.And), ("sleep", LogicalOperator.Or));

        // wake 匹配 → 该分支满足；OR 组间任一组成立即为真
        Assert.True(await service.EvaluateRuleConditionsOnEventAsync(rule, CancellationToken.None, "wake"));
    }

    // ── 轮询路径：混合规则必须进轮询，否则非电源分支永远不生效 ────────

    [Fact]
    public async Task 混合规则_boot或应用启动_必须进轮询()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewl_tests_" + Guid.NewGuid().ToString("N")[..8]);
        using var logger = new ServiceLogger(Path.Combine(dir, "service-test.log"), enabled: false);
        try
        {
            var rules = new List<LinkerRule> { BuildRule(("boot", LogicalOperator.And), ("app_start", LogicalOperator.Or)) };
            var manager = new TriggerManager(new LinkerService(null!, null!, "unused.json"),
                                             Path.Combine(dir, "service-test.log"), logger);

            await manager.LoadRulesAsync(rules, pollingIntervalSeconds: 2);

            // 旧实现：整条规则被跳过（RuleTriggers 为空），app_start 分支永不生效
            Assert.Single(manager.RuleTriggers);

            await manager.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task 纯事件规则_仍然不进轮询()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ewl_tests_" + Guid.NewGuid().ToString("N")[..8]);
        using var logger = new ServiceLogger(Path.Combine(dir, "service-test.log"), enabled: false);
        try
        {
            var rules = new List<LinkerRule> { BuildRule(("shutdown", LogicalOperator.And)) };
            var manager = new TriggerManager(new LinkerService(null!, null!, "unused.json"),
                                             Path.Combine(dir, "service-test.log"), logger);

            await manager.LoadRulesAsync(rules, pollingIntervalSeconds: 2);

            Assert.Empty(manager.RuleTriggers);
            await manager.DisposeAsync();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── 轮询路径：电源条件在轮询里恒为不满足（否则 AND 规则会被误判） ──

    [Fact]
    public async Task 轮询路径_电源条件恒不满足_AND规则不得成立()
    {
        var cpu = new ScriptedTrigger();
        using var h = new RuleHarness(
            BuildRule(("boot", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            cpu);

        cpu.Satisfy();
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.False(h.Trigger.ActionInFlight);
        Assert.Equal(0, h.DispatchCount);
    }

    [Fact]
    public async Task 轮询路径_电源条件恒不满足_OR规则由可轮询分支决定()
    {
        var cpu = new ScriptedTrigger();
        using var h = new RuleHarness(
            BuildRule(("boot", LogicalOperator.And), ("cpu_temp", LogicalOperator.Or)),
            cpu);

        cpu.Satisfy();
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();

        Assert.True(h.Trigger.ActionInFlight);   // 电源分支恒 false，但 OR 的另一支成立
        await h.WaitDispatchedAsync(1);
    }
}
