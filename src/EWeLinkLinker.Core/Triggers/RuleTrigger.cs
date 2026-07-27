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
    private bool _disposed;
    private bool _previousCompositeResult; // 防重复触发

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
    /// </summary>
    public void OnPollingComplete()
    {
        if (_disposed || !_rule.Enabled) return;

        bool currentResult = EvaluateCompositeCondition();

        var states = string.Join(", ", _conditionTriggers.Select((t, i) =>
            $"{_rule.Conditions[i].Type}={t.State}"));
        _logger.Info($"[RuleTrigger:{_rule.Name}] {states} => {(currentResult ? "满足" : "不满足")}, prev={_previousCompositeResult}");

        // 边沿检测：只在从"不满足"变为"满足"时触发
        if (currentResult && !_previousCompositeResult)
        {
            var reason = string.Join(", ", _rule.Conditions.Select((c, i) =>
            {
                var state = _conditionTriggers[i].State == TriggerState.Triggered ? "满足" : "不满足";
                return $"{c.Type}={c.Parameter}({state})";
            }));

            _logger.LogRuleTriggered(_rule.Name, reason);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _linkerService.ExecuteRuleAsync(_rule);
                }
                catch (Exception ex)
                {
                    _logger.Error($"规则 [{_rule.Name}] 执行失败", ex);
                }
            });
        }

        _previousCompositeResult = currentResult;
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
