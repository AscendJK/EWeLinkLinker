using System.Diagnostics;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Services;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// app_running：**状态**条件——问的是"此刻在不在跑"，不是"刚刚启动过"。
///
/// 这一条决定了整个功能的形状：做成事件（像 app_start）的话，
/// `wake AND app_running(chrome)` 在唤醒那一刻根本没有"刚启动"，永远不成立。
/// 所以它必须锁存，且**不挂脉冲窗口**。
///
/// 用例全部走 TriggerRegistry / LinkerService 这些公共入口，用一份真实进程做探针
/// （把系统自带的 ping.exe 复制成唯一名字再跑起来），不依赖机器上恰好开着什么。
/// </summary>
public class AppRunningTriggerTests
{
    /// <summary>起一个名字唯一的探针进程；Dispose 时杀掉。</summary>
    private sealed class MarkerProcess : IDisposable
    {
        private readonly string _exePath;
        public Process Process { get; }
        public string Name { get; }

        public MarkerProcess()
        {
            Name = "ewl_marker_" + Guid.NewGuid().ToString("N")[..8];
            _exePath = Path.Combine(Path.GetTempPath(), Name + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), _exePath, overwrite: true);

            Process = Process.Start(new ProcessStartInfo(_exePath, "-n 120 127.0.0.1")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            Thread.Sleep(300);   // 等它进进程表
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { Process.Dispose(); } catch { }
            try { File.Delete(_exePath); } catch { }
        }
    }

    private static TriggerConfig Config(string parameter) =>
        new() { Type = "app_running", Parameter = parameter };

    // ── 注册与参数校验 ────────────────────────────────────────────

    [Fact]
    public void 类型已注册_能从注册表建出来()
    {
        var trigger = TriggerRegistry.Create(Config("chrome"));
        Assert.Equal("app_running", trigger.Type);
        Assert.Equal("应用运行中", trigger.DisplayName);
        trigger.Dispose();
    }

    [Fact]
    public void 空进程名被拒_且消息点名进程名()
    {
        // TriggerRegistry.Create 会把构造函数抛的 ArgumentException 包成 InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() => TriggerRegistry.Create(Config("")));
        Assert.Contains("进程名", ex.Message);
    }

    [Fact]
    public void 保存前的试建也能过()
        => Assert.True(TriggerRegistry.TryValidate(Config("chrome"), out var err), err);

    // ── 状态语义（核心） ──────────────────────────────────────────

    [Fact]
    public async Task 进程在跑_满足并锁存_不是脉冲()
    {
        using var marker = new MarkerProcess();
        var trigger = TriggerRegistry.Create(Config(marker.Name));
        trigger.Start();

        Assert.True(await trigger.PollAsync());
        Assert.Equal(TriggerState.Triggered, trigger.State);

        // 关键：下一拍仍是 Triggered。如果做成脉冲/事件，这里会掉回 Monitoring。
        Assert.False(await trigger.PollAsync());
        Assert.Equal(TriggerState.Triggered, trigger.State);

        trigger.Dispose();
    }

    [Fact]
    public async Task 进程退出_复位为不满足()
    {
        using var marker = new MarkerProcess();
        var trigger = TriggerRegistry.Create(Config(marker.Name));
        trigger.Start();

        Assert.True(await trigger.PollAsync());
        marker.Dispose();
        Thread.Sleep(300);

        Assert.False(await trigger.PollAsync());
        Assert.Equal(TriggerState.Monitoring, trigger.State);

        trigger.Dispose();
    }

    [Fact]
    public async Task 没有这个进程_不满足()
    {
        var trigger = TriggerRegistry.Create(Config("绝对不存在的进程_" + Guid.NewGuid().ToString("N")[..6]));
        trigger.Start();
        Assert.False(await trigger.PollAsync());
        trigger.Dispose();
    }

    // ── 与电源事件的组合：这才是它存在的理由 ──────────────────────

    [Fact]
    public async Task 唤醒事件里按此刻是否在跑判定()
    {
        using var marker = new MarkerProcess();

        var rule = new LinkerRule { Name = "唤醒时看进程", Enabled = true };
        rule.Conditions.Add(new RuleCondition { Type = "wake", Parameter = "", Operator = LogicalOperator.And });
        rule.Conditions.Add(new RuleCondition
        {
            Type = "app_running",
            Parameter = marker.Name,
            Operator = LogicalOperator.And,
        });

        var service = new LinkerService(null!, null!, "unused.json");
        Assert.True(await service.EvaluateRuleConditionsOnEventAsync(rule, CancellationToken.None, "wake"));

        // 进程杀了之后，同一个规则在事件路径上必须判"不满足"
        marker.Dispose();
        Thread.Sleep(300);
        Assert.False(await service.EvaluateRuleConditionsOnEventAsync(rule, CancellationToken.None, "wake"));
    }

    // ── 与状态条件的组合 ──────────────────────────────────────────

    [Fact]
    public async Task 与温度条件AND_两者都成立才算满足()
    {
        var cpu = new ScriptedTrigger();
        using var marker = new MarkerProcess();
        using var h = new RuleHarness(
            RuleHarness.BuildRule(("app_running", LogicalOperator.And), ("cpu_temp", LogicalOperator.And)),
            TriggerRegistry.Create(Config(marker.Name)),
            cpu);

        // 只有应用在跑、温度没到 → 不派发
        await h.Trigger.GetTriggers()[0].PollAsync();
        await cpu.PollAsync();
        h.Trigger.OnPollingComplete();
        Assert.False(h.Trigger.ActionInFlight);
    }
}
