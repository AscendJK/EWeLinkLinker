using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 多轮补读（SensorReadiness）的语义：某条条件在第一轮就满足了、第二轮因为已锁存而返回 false，
/// 这个"看看这轮满不满足"的结果不能被后者覆盖成 false。
///
/// 覆盖的现场：`boot AND cpu_temp≥80 AND cpu_usage≥50` —— cpu_usage 首轮必然读不到（要一个采样窗口），
/// 于是补读一轮；那一轮 cpu_temp 已经是锁存状态、返回 false，把第一轮的真结果冲掉，
/// 整条规则在事件路径上永远不执行。
/// </summary>
public class SensorReadinessTests
{
    [Fact]
    public async Task 多轮补读_不得把已满足的条件覆盖成不满足()
    {
        var satisfied = new ScriptedTrigger();
        satisfied.Satisfy();

        var unknownFirst = new ScriptedTrigger { UnknownOnFirstPoll = true };
        unknownFirst.Satisfy();

        var outcome = await SensorReadiness.PollUntilReadingAsync(
            new[] { satisfied, unknownFirst }, maxRounds: 3, pause: TimeSpan.Zero, resetCache: null);

        Assert.True(outcome.Triggered[0], "第一轮已满足的条件被第二轮的 false 覆盖了");
        Assert.True(outcome.Triggered[1]);
        Assert.All(outcome.ReadingKnown, known => Assert.True(known));
    }

    [Fact]
    public async Task 始终读不到时_按未知上报而不是判成不满足()
    {
        var alwaysUnknown = new ScriptedTrigger { UnknownOnFirstPoll = true };
        // 让它每一轮都是"未知"：用 FailNextPoll 会让基类置 false 并保持
        alwaysUnknown.FailNextPoll = true;

        var outcome = await SensorReadiness.PollUntilReadingAsync(
            new[] { alwaysUnknown }, maxRounds: 2, pause: TimeSpan.Zero, resetCache: null);

        Assert.False(outcome.ReadingKnown[0]);
        Assert.False(outcome.Triggered[0]);
    }

    [Fact]
    public async Task 一次就读到时_只跑一轮()
    {
        var t = new ScriptedTrigger();
        t.Satisfy();

        var outcome = await SensorReadiness.PollUntilReadingAsync(
            new[] { t }, maxRounds: 3, pause: TimeSpan.Zero, resetCache: null);

        Assert.True(outcome.ReadingKnown[0]);
        Assert.True(outcome.Triggered[0]);
    }
}
