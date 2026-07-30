using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Services;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 高性能触发器管理器
/// </summary>
public sealed class TriggerManager : IAsyncDisposable
{
    private readonly LinkerService _linkerService;
    private readonly ServiceLogger _logger;
    private readonly string _logPath;
    private readonly Dictionary<string, RuleTrigger> _ruleTriggers = new();
    private PollingScheduler? _scheduler;
    private bool _disposed;

    public TriggerManager(LinkerService linkerService, string logPath, ServiceLogger logger)
    {
        _linkerService = linkerService;
        _logger = logger;  // 使用传入的 logger，不创建新实例
        _logPath = logPath;
    }

    /// <summary>
    /// 加载规则并创建触发器
    /// </summary>
    /// <param name="rules">规则列表</param>
    /// <param name="pollingIntervalSeconds">轮询间隔（秒）</param>
    public async ValueTask LoadRulesAsync(List<LinkerRule> rules, int pollingIntervalSeconds = 5)
    {
        // 清理旧触发器
        await ClearTriggersAsync();

        // 创建调度器（使用配置的轮询间隔）
        var interval = TimeSpan.FromSeconds(Math.Clamp(pollingIntervalSeconds, 1, 30));
        _scheduler = new PollingScheduler(_logger, interval);

        int activeRules = 0;
        int totalConditions = 0;
        int totalActions = 0;

        foreach (var rule in rules)
        {
            // 跳过没有条件的规则
            if (rule.Conditions.Count == 0)
            {
                _logger.Warn($"规则 [{rule.Name}] 没有条件，跳过");
                continue;
            }

            // 跳过纯电源事件规则（由 ServiceBase 事件处理）
            if (rule.Conditions.All(c => IsPowerCondition(c.Type)))
            {
                _logger.Info($"规则 [{rule.Name}] 仅包含电源事件，跳过（由系统事件处理）");
                continue;
            }

            try
            {
                // 为规则创建复合触发器
                var ruleTrigger = new RuleTrigger(rule, _linkerService, _logger, _scheduler);
                await ruleTrigger.InitializeAsync();

                // 记录规则详情
                var conditions = rule.Conditions.Select(c => new ConditionInfo(c.Type, c.Parameter, c.Comparison)).ToList();
                var actions = rule.Actions.Select(a => new ActionInfo(a.Name, a.Outlet, a.State)).ToList();
                _logger.LogRuleDetails(rule.Name, rule.Id, conditions, actions);

                // 注册到调度器，并设置日志路径
                foreach (var conditionTrigger in ruleTrigger.GetTriggers())
                {
                    conditionTrigger.SetLogPath(_logPath);
                    _scheduler.Register(conditionTrigger);
                    _logger.LogTriggerStatus(conditionTrigger.Type, conditionTrigger.Id, false, GetParameter(conditionTrigger));
                }

                // 修复：注册轮询后回调，让 RuleTrigger 可以评估复合条件
                if (ruleTrigger is IPostPollCallback callback)
                {
                    _scheduler.RegisterPostPollCallback(callback);
                }

                _ruleTriggers[rule.Id] = ruleTrigger;
                activeRules++;
                totalConditions += rule.Conditions.Count;
                totalActions += rule.Actions.Count;
            }
            catch (Exception ex)
            {
                _logger.Error($"创建规则 [{rule.Name}] 触发器失败", ex);
            }
        }

        // 记录加载摘要
        _logger.LogRulesLoaded(rules.Count, activeRules, totalConditions, totalActions);
    }

    /// <summary>
    /// 启动所有触发器
    /// </summary>
    public async ValueTask StartAllAsync()
    {
        _scheduler?.Start();
        _logger.LogListenerStarted("PollingScheduler", "5s");

        // 记录每个触发器的状态
        foreach (var (id, trigger) in _ruleTriggers)
        {
            _logger.LogTriggerStatus("Rule", id, true);
        }

        _logger.Info($"已启动 {_ruleTriggers.Count} 条规则的监控");
    }

    /// <summary>
    /// 停止所有触发器
    /// </summary>
    public async ValueTask StopAllAsync()
    {
        _scheduler?.Stop();
        _logger.LogListenerStopped("PollingScheduler");
        _logger.Info("已停止所有规则监控");
    }

    /// <summary>
    /// 重新加载配置
    /// </summary>
    /// <param name="newRules">新规则列表</param>
    /// <param name="pollingIntervalSeconds">轮询间隔（秒）</param>
    public async Task ReloadAsync(List<LinkerRule> newRules, int pollingIntervalSeconds = 5)
    {
        try
        {
            _logger.Info("开始重新加载配置...");

            // 停止当前监控
            await StopAllAsync();

            // 清理旧触发器
            foreach (var trigger in _ruleTriggers.Values)
            {
                try { trigger.Dispose(); } catch { }
            }
            _ruleTriggers.Clear();

            // 加载新规则（使用配置的轮询间隔）
            await LoadRulesAsync(newRules, pollingIntervalSeconds);

            // 启动监控
            await StartAllAsync();

            _logger.LogConfigReloaded(true, newRules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogConfigReloaded(false, 0, ex.Message);
            throw;
        }
    }

    private static string? GetParameter(OptimizedTriggerBase trigger)
    {
        return trigger.Type switch
        {
            "time" => "时间触发",
            "interval" => "间隔触发",
            _ => trigger.Type
        };
    }

    public static bool IsPowerCondition(string type) =>
        type.Equals("boot", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("sleep", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("wake", StringComparison.OrdinalIgnoreCase);

    private async ValueTask ClearTriggersAsync()
    {
        if (_scheduler != null)
        {
            await _scheduler.DisposeAsync();
            _scheduler = null;
        }

        foreach (var trigger in _ruleTriggers.Values)
        {
            try { trigger.Dispose(); } catch { }
        }
        _ruleTriggers.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await ClearTriggersAsync();
    }
}
