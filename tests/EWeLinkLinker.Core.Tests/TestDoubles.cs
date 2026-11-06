using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Triggers;

// 日志目录与静态硬件句柄在用例之间共享，禁用并行以免互相踩。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 可脚本化驱动的触发器：不碰任何真实传感器，由测试决定"这一轮条件满不满足"。
/// 行为刻意与生产里的边沿型触发器一致：满足时把自己推到 Triggered 并只报告一次上升沿，
/// 不满足时回 Monitoring。
/// </summary>
internal sealed class ScriptedTrigger : OptimizedTriggerBase
{
    private bool _satisfied;

    public override string Type => "scripted";
    public override string DisplayName => "scripted";

    /// <summary>这一轮评估是否抛异常（用于验证"异常 = 本轮没有定论"的记账）。</summary>
    public bool FailNextPoll { get; set; }

    /// <summary>第一轮故意报"读数未知"，用来驱动多轮补读（SensorReadiness）。</summary>
    public bool UnknownOnFirstPoll { get; set; }

    private bool _firstPollDone;

    /// <summary>基类记账：上一次轮询是否真的拿到了读数（只有本程序集可见）。</summary>
    public bool ReadingKnown => LastReadingAvailable;

    public void Satisfy() => _satisfied = true;
    public void Unsatisfy() => _satisfied = false;

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        // 刻意不碰 LastReadingAvailable：生产里的 time/interval/app_* 触发器也不碰它，
        // 这正是"抛过一次异常就永久停在读数未知"那条缺陷的现场条件。
        if (UnknownOnFirstPoll && !_firstPollDone)
        {
            _firstPollDone = true;
            LastReadingAvailable = false;
            return ValueTask.FromResult(false);
        }

        if (FailNextPoll) throw new InvalidOperationException("传感器的假故障");

        if (_satisfied)
        {
            if (State != TriggerState.Triggered)
            {
                return ValueTask.FromResult(true);   // 基类会把 State 推到 Triggered
            }
            return ValueTask.FromResult(false);      // 锁存中，不再造上升沿
        }

        if (State == TriggerState.Triggered) State = TriggerState.Monitoring;
        return ValueTask.FromResult(false);
    }
}

/// <summary>
/// 只验证脉冲窗口机制的触发器：事件由测试显式 Raise() 触发，不模拟任何真实事件源。
/// </summary>
internal sealed class ScriptedPulseTrigger : OptimizedTriggerBase
{
    private readonly TimeSpan _window;
    private bool _emit;

    public ScriptedPulseTrigger(TimeSpan window) => _window = window;

    public override string Type => "scripted_pulse";
    public override string DisplayName => "scripted_pulse";

    protected override TimeSpan PulseWindow => _window;

    public void Raise() => _emit = true;

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        if (_emit)
        {
            _emit = false;
            RaisePulse();
            return ValueTask.FromResult(true);
        }
        return ValueTask.FromResult(false);
    }
}

/// <summary>
/// 驱动 RuleTrigger 判定/派发的最小台架：动作执行被换成一个"卡在闸门上"的委托，
/// 于是"这一拍有没有真的派发出去"可以精确断言，不用靠 sleep 猜。
/// </summary>
internal sealed class RuleHarness : IDisposable
{
    private readonly string _logDir =
        Path.Combine(Path.GetTempPath(), "ewl_tests_" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dispatchCount;

    public ServiceLogger Logger { get; }
    public PollingScheduler Scheduler { get; }
    public RuleTrigger Trigger { get; }

    public int DispatchCount => Volatile.Read(ref _dispatchCount);

    public RuleHarness(LinkerRule rule, params OptimizedTriggerBase[] conditionTriggers)
        : this(rule, static (_, _) => { }, conditionTriggers)
    {
    }

    public RuleHarness(LinkerRule rule, Action<string, string> logTriggered, params OptimizedTriggerBase[] conditionTriggers)
    {
        Logger = new ServiceLogger(Path.Combine(_logDir, "service-test.log"), enabled: false);
        Scheduler = new PollingScheduler(Logger);
        Trigger = new RuleTrigger(
            rule, Logger, Scheduler,
            async (_, _) =>
            {
                Interlocked.Increment(ref _dispatchCount);
                await _gate.Task.ConfigureAwait(false);
            },
            logTriggered,
            conditionTriggers);

        // 生产里是 PollingScheduler.Start() 给每个触发器 arm 这一下；
        // 少了它状态停在 Idle，PollAsync 的 "wasMonitoring" 永远 false，状态到不了 Triggered。
        foreach (var trigger in conditionTriggers) trigger.Start();
    }

    /// <summary>放闸：让已派发的那批动作跑完，_actionInFlight 归零。</summary>
    public void ReleaseGate() => _gate.TrySetResult();

    /// <summary>等到派发计数达到预期（动作是在线程池里跑的，计数自增异步）。</summary>
    public async Task WaitDispatchedAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DispatchCount < expected && DateTime.UtcNow < deadline)
            await Task.Delay(5).ConfigureAwait(false);
    }

    /// <summary>等到"没有动作在飞"（最多 2 秒）。</summary>
    public async Task WaitIdleAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Trigger.ActionInFlight && DateTime.UtcNow < deadline)
            await Task.Delay(5).ConfigureAwait(false);
    }

    public static LinkerRule BuildRule(params (string Type, LogicalOperator Op)[] conditions)
    {
        var rule = new LinkerRule { Name = "测试规则", Enabled = true };
        foreach (var (type, op) in conditions)
        {
            rule.Conditions.Add(new RuleCondition
            {
                Type = type,
                Parameter = "50",
                Comparison = ComparisonOperator.Gte,
                Operator = op,
            });
        }
        return rule;
    }

    public void Dispose()
    {
        _gate.TrySetResult();
        Trigger.Dispose();
        Logger.Dispose();
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }
}
