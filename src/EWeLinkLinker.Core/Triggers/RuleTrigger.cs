using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Services;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 规则触发器 - 处理复合条件（AND/OR）
///
/// 工作原理：
/// - 每个子触发器自己管理状态（是否自动复位）
/// - 轮询完成后，检查所有子触发器的当前状态
/// - 根据 AND/OR 逻辑决定是否触发规则
/// - 不需要额外的 _hasFired 标志，触发器状态本身就是防重复的守卫
/// </summary>
public sealed class RuleTrigger : IDisposable, IPostPollCallback
{
    private readonly LinkerRule _rule;
    private readonly LinkerService _linkerService;
    private readonly ServiceLogger _logger;
    private readonly PollingScheduler _scheduler;
    private readonly List<OptimizedTriggerBase> _conditionTriggers = new();
    private readonly object _evalLock = new();  // H-17 修复：保护 _previousCompositeResult 读写
    private bool _disposed;
    private bool _previousCompositeResult; // 防重复触发

    /// <summary>
    /// 规则级"上次发出动作"的时刻。最长粘住计时必须挂在规则上而不是挂在每个条件上：
    /// 每个条件各一个钟时，一条 AND 规则每满一次计时就会被每个条件各逼重发一次
    /// （生产实测规则 3 两个条件 ⇒ 每小时约 4 次，而不是口径说的每 30 分钟一次）。
    /// </summary>
    private DateTime? _actionStampedUtc;

    /// <summary>强制重判要两拍：这一拍记下降沿，下一拍才允许升回去发动作。</summary>
    private bool _reArmPending;

    public RuleTrigger(LinkerRule rule, LinkerService linkerService, ServiceLogger logger, PollingScheduler scheduler)
    {
        _rule = rule;
        _linkerService = linkerService;
        _logger = logger;
        _scheduler = scheduler;
    }

    public async Task InitializeAsync()
    {
        // 为每个条件创建触发器
        foreach (var condition in _rule.Conditions)
        {
            var config = new TriggerConfig
            {
                Type = condition.Type,
                Parameter = condition.Parameter,
                Parameter2 = condition.Parameter2,
                ReleaseBand = condition.ReleaseBand,
                Comparison = condition.Comparison
            };
            var trigger = TriggerRegistry.Create(config);
            _conditionTriggers.Add(trigger);
        }

        _logger.Info($"规则 [{_rule.Name}] 初始化完成: {_conditionTriggers.Count} 个条件监控器");
    }

    public IReadOnlyList<OptimizedTriggerBase> GetTriggers() => _conditionTriggers.AsReadOnly();

    /// <summary>
    /// 轮询完成后评估复合条件（由 PollingScheduler 调用）
    /// H-17 修复：使用 lock 保护 _previousCompositeResult 读写，防止并发竞态
    /// </summary>
    public void OnPollingComplete()
    {
        if (_disposed || !_rule.Enabled) return;

        // H-17 修复：lock 保护边沿检测逻辑（复合判定也搬进锁里：判定与边沿记账必须是一个原子动作）
        lock (_evalLock)
        {
            var nowUtc = DateTime.UtcNow;
            var currentResult = EvaluateCompositeCondition();

            if (_reArmPending)
            {
                // 上一轮已经按"不满足"记过一笔下降沿，这一轮正常判定：条件还锁着 ⇒ 立刻变成上升沿 ⇒ 重发
                _reArmPending = false;
            }
            else if (currentResult && ComparisonHelper.IsHoldExpired(_actionStampedUtc, nowUtc))
            {
                // 本轮当作"不满足"造一个下降沿；条件还锁着，所以下一轮重新变成满足 ⇒ 一次上升沿 ⇒
                // 整套动作重发。计时挂在规则上，不是每个条件各挂一个（那样一条 AND 规则会被乘倍）。
                // 注意必须两拍：单靠"本轮判 false"不会刷新盖章，下一轮"计时已过期"仍然成立，
                // 于是每一轮都被按成不满足，永远等不到上升沿，动作一次都发不出去。
                _reArmPending = true;
                _logger.Info($"规则 [{_rule.Name}] 已连续满足满 {ComparisonHelper.MaxReleaseHold}，本轮强制重判（纠偏：设备可能被人手动改过）");
                currentResult = false;
            }

            var states = string.Join(", ", _conditionTriggers.Select((t, i) =>
                $"{_rule.Conditions[i].Type}={t.State}"));
            _logger.Info($"[RuleTrigger:{_rule.Name}] {states} => {(currentResult ? "满足" : "不满足")}");
            // 边沿检测：只在从"不满足"变为"满足"时触发
            if (currentResult && !_previousCompositeResult)
            {
                var reason = string.Join(", ", _rule.Conditions.Select((c, i) =>
                {
                    var state = _conditionTriggers[i].State == TriggerState.Triggered ? "满足" : "不满足";
                    return $"{c.Type}={c.Parameter}({state})";
                }));

                _logger.LogRuleTriggered(_rule.Name, reason);
                _actionStampedUtc = nowUtc;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _linkerService.ExecuteRuleAsync(_rule).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"规则 [{_rule.Name}] 执行失败", ex);
                    }
                });
            }

            _previousCompositeResult = currentResult;
        }
    }

    /// <summary>
    /// 建立启动基线：把当前复合结果记为「已经有过」的状态，使服务重启或触发器重建后，
    /// 原本就已满足的规则不会在第一轮被当成新跳变而重发设备动作。
    /// 由 TriggerManager 在启动调度器之前调用，此时 OnPollingComplete 还没跑过。
    /// </summary>
    public void SeedBaseline()
    {
        lock (_evalLock)
        {
            _previousCompositeResult = EvaluateCompositeCondition();
            if (_previousCompositeResult)
            {
                // 基线也起表：启动时就已满足的规则，第一次纠偏同样要等满一个计时周期，
                // 而不是服务一起来就补发一次
                _actionStampedUtc = DateTime.UtcNow;
                _logger.Info($"规则 [{_rule.Name}] 启动时条件已满足，按基线处理（本次不执行动作）");
            }
        }
    }

    /// <summary>
    /// 评估复合条件（返回当前是否满足）
    /// 支持标准布尔优先级：AND > OR
    /// </summary>
    private bool EvaluateCompositeCondition()
    {
        if (_conditionTriggers.Count == 0) return false;
        if (_conditionTriggers.Count == 1)
            return _conditionTriggers[0].State == TriggerState.Triggered;

        // 按 OR 分组，每组内 AND 运算
        var groups = new List<List<int>>();
        var currentGroup = new List<int> { 0 };

        for (int i = 1; i < _conditionTriggers.Count; i++)
        {
            if (_rule.Conditions[i].Operator == LogicalOperator.Or)
            {
                groups.Add(currentGroup);
                currentGroup = new List<int> { i };
            }
            else
            {
                currentGroup.Add(i);
            }
        }
        groups.Add(currentGroup);

        // 每组内 AND 运算，组间 OR 运算
        foreach (var group in groups)
        {
            bool groupResult = true;
            foreach (var idx in group)
            {
                groupResult = groupResult && (_conditionTriggers[idx].State == TriggerState.Triggered);
            }
            if (groupResult) return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 从调度器注销回调，防止内存泄漏
        _scheduler.UnregisterPostPollCallback(this);

        foreach (var trigger in _conditionTriggers)
        {
            try { trigger.Stop(); } catch { }
            try { trigger.Dispose(); } catch { }
        }
        _conditionTriggers.Clear();
    }
}
