using System.Collections.Concurrent;
using EWeLinkLinker.Core.Config;
using EWeLinkLinker.Core.Lan;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Token;
using EWeLinkLinker.Core.Triggers;
using Microsoft.Extensions.Logging;

namespace EWeLinkLinker.Core.Services;

public class LinkerService
{
    private readonly LanClient _lanClient;
    private readonly TokenManager _tokenManager;
    private readonly string _configPath;
    private readonly string? _logPath;
    private readonly ILogger<LinkerService>? _logger;

    public LinkerService(LanClient lanClient, TokenManager tokenManager, string configPath, ILogger<LinkerService>? logger = null, string? logPath = null)
    {
        _lanClient = lanClient;
        _tokenManager = tokenManager;
        _configPath = configPath;
        _logger = logger;
        _logPath = logPath;
    }

    private void Log(string message)
    {
        // C-2 修复：统一使用 ILogger + SimpleLogger，移除静态 StreamWriter
        _logger?.LogInformation(message);
        if (LoggerConfig.IsEnabled && !string.IsNullOrEmpty(_logPath))
            SimpleLogger.Log($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void LogError(string message, Exception? ex = null)
    {
        _logger?.LogError(ex, message);
        if (LoggerConfig.IsEnabled && !string.IsNullOrEmpty(_logPath))
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss}] ERROR: {message}";
            if (ex != null)
                logEntry += $"\n  Exception: {ex.Message}";
            SimpleLogger.Log(logEntry);
        }
    }

    public async Task ExecuteEventAsync(string eventName, CancellationToken ct = default)
    {
        Log($"Executing event: {eventName}");

        var config = LinkerConfig.Load(_configPath);

        // 同步规则中的设备名称为最新配置
        SyncActionDeviceNames(config);

        // 查找匹配的规则（支持新旧格式，只选启用的规则）
        var rules = FindMatchingRules(config.Rules, eventName);

        if (rules.Count == 0)
        {
            Log($"No enabled rules configured for event: {eventName}");
            return;
        }

        bool isLocalOnlyEvent = eventName.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
                                 eventName.Equals("sleep", StringComparison.OrdinalIgnoreCase);

        // LAN 控制不需要 token，只有云端 API 才需要
        if (!isLocalOnlyEvent)
        {
            try
            {
                await _tokenManager.GetValidTokensAsync();
                // 这句原来叫 "Token validated successfully"，可它现在只在到了刷新期限时才联网，
                // 平时只是把盘上那份读出来——日志里别留一条自己做不到的承诺
                Log("Token check done (未到期则不联网，未向云端验证)");
            }
            catch (Exception ex)
            {
                Log($"Token validation failed (LAN control will still work): {ex.Message}");
            }
        }
        else
        {
            Log("Local-only event (shutdown/sleep), skipping token validation");
        }

        Log($"Found {rules.Count} enabled rules for event: {eventName}");

        // 遍历所有匹配的规则（不只是第一条）
        foreach (var rule in rules)
        {
            if (rule.Actions.Count == 0) continue;

            // 混合规则（电源事件 + 轮询条件）：事件触发时实时评估非电源条件，
            // 全部满足才执行动作，避免轮询条件被静默忽略
            if (!await EvaluateRuleConditionsOnEventAsync(rule, ct))
            {
                Log($"  Rule '{rule.Name}': 条件未满足，跳过");
                continue;
            }

            Log($"  Rule '{rule.Name}': {rule.Actions.Count} actions");
            await ExecuteActionsByDeviceAsync(rule.Actions, config, ct);
        }

        Log($"Event {eventName} completed");
    }

    /// <summary>
    /// 事件路径的补读预算：最多补 1 遍、间隔 300ms。
    /// 播种那套是 3 遍 ×1200ms，这里不能等那么久——boot/shutdown 事件整体有停机预算。
    /// </summary>
    private const int EventSeedRounds = 2;
    private static readonly TimeSpan EventSeedRoundPause = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 事件触发时评估规则条件：
    /// - 电源条件已由事件匹配，视为满足（不阻塞）
    /// - 非电源条件用临时触发器实时评估一次（先 Start 播种初始状态，语义与轮询一致）
    /// - 分组语义与 RuleTrigger.EvaluateCompositeCondition 一致：AND > OR
    /// </summary>
    private async Task<bool> EvaluateRuleConditionsOnEventAsync(LinkerRule rule, CancellationToken ct)
    {
        var conditions = rule.Conditions;
        if (conditions.Count == 0) return true;

        var results = new bool[conditions.Count];
        // 临时触发器一次建齐、一次补读：逐条各补一遍的话，N 个条件就是 N × 300ms
        var probes = new List<OptimizedTriggerBase>();
        var probeConditionIndex = new List<int>();

        try
        {
            for (int i = 0; i < conditions.Count; i++)
            {
                var condition = conditions[i];
                if (TriggerManager.IsPowerCondition(condition.Type))
                {
                    results[i] = true; // 电源条件已由事件匹配
                    continue;
                }

                try
                {
                    var config = new TriggerConfig
                    {
                        Type = condition.Type,
                        Parameter = condition.Parameter,
                        Parameter2 = condition.Parameter2,
                        ReleaseBand = condition.ReleaseBand,
                        Comparison = condition.Comparison
                    };
                    probes.Add(TriggerRegistry.Create(config));
                    probeConditionIndex.Add(i);
                }
                catch (Exception ex)
                {
                    LogError($"评估规则 [{rule.Name}] 条件 {condition.Type} 失败", ex);
                    return false;
                }
            }

            if (probes.Count > 0)
            {
                // 和轮询侧同一套判定：第一遍读不到（CPU 使用率要等采样窗口）不能当成"条件不满足"，
                // 否则开机事件一进来就把规则判死，日志里还写着"条件未满足"。
                var outcome = await SensorReadiness.PollUntilReadingAsync(
                    probes, EventSeedRounds, EventSeedRoundPause, resetCache: null, ct);

                for (int k = 0; k < probes.Count; k++)
                {
                    var i = probeConditionIndex[k];
                    results[i] = outcome.Triggered[k];
                    if (!outcome.ReadingKnown[k])
                        Log($"  Rule '{rule.Name}': 条件 {conditions[i].Type} 读数未知（补读一遍仍没拿到）——" +
                            "这不是判定为不满足，但为安全起见本次不执行");
                }
            }

            // 与 RuleTrigger.EvaluateCompositeCondition 一致：按 OR 分组，组内 AND，组间 OR
            if (conditions.Count == 1) return results[0];

            var groups = new List<List<int>>();
            var currentGroup = new List<int> { 0 };
            for (int i = 1; i < conditions.Count; i++)
            {
                if (conditions[i].Operator == LogicalOperator.Or)
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

            foreach (var group in groups)
            {
                bool groupResult = true;
                foreach (var idx in group)
                    groupResult = groupResult && results[idx];
                if (groupResult) return true;
            }
            return false;
        }
        finally
        {
            foreach (var probe in probes)
            {
                try { probe.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// 执行规则（由 RuleTrigger 调用）
    /// </summary>
    public async Task ExecuteRuleAsync(LinkerRule rule, CancellationToken ct = default)
    {
        Log($"Executing rule: {rule.Name}");

        if (rule.Actions.Count == 0)
        {
            Log($"No actions in rule: {rule.Name}");
            return;
        }

        // 重新加载配置以获取最新的设备信息
        var config = LinkerConfig.Load(_configPath);

        Log($"Executing {rule.Actions.Count} actions for rule: {rule.Name}");
        await ExecuteActionsByDeviceAsync(rule.Actions, config, ct);

        Log($"Rule '{rule.Name}' completed");
    }

    /// <summary>
    /// 按设备分组执行动作：同设备的不同 outlet 串行，不同设备并发。
    /// 避免对同一设备同时发多条命令（eWeLink 400 问题），同时最大化不同设备间的并行度。
    /// </summary>
    private async Task ExecuteActionsByDeviceAsync(IEnumerable<LinkerAction> actions, LinkerConfig config, CancellationToken ct)
    {
        // 按设备分组
        var groups = actions.GroupBy(a => a.DeviceId);
        var tasks = groups.Select(async group =>
        {
            // 同设备串行执行（按 action 顺序）
            foreach (var action in group)
            {
                await ExecuteActionAsync(config, action, ct);
            }
        });
        // 不同设备并发执行
        await Task.WhenAll(tasks);
    }

    private async Task ExecuteActionAsync(LinkerConfig config, LinkerAction action, CancellationToken ct)
    {
        var device = config.Devices.FirstOrDefault(d => d.DeviceId == action.DeviceId);
        if (device == null)
        {
            LogError($"Device not found: {action.DeviceId}");
            return;
        }

        try
        {
            // 使用 device.Name（来自配置）而不是 action.Name（可能过时）
            Log($"Controlling device {device.Name} ({device.DeviceId}) outlet={action.Outlet} -> {action.State} [IP: {device.IpAddress}]");

            bool turnOn = string.Equals(action.State, "on", StringComparison.OrdinalIgnoreCase);
            // LAN 控制只需要 DeviceKey，不需要 token
            var success = await _lanClient.SetPowerWithRetryAsync(device, turnOn, action.Outlet);

            if (success)
            {
                Log($"Device {device.Name} set to {action.State} successfully");
            }
            else
            {
                LogError($"Failed to control device {device.Name}");
            }

            // 设备命令必须无条件留痕：日志开关是为了降噪，不该顺带抹掉
            // 「硬件在什么时候被下过什么令」——重启时机尤其依赖这条记录
            SimpleLogger.Log($"[AUDIT] 设备命令 {device.Name} ({device.DeviceId}) 通道{action.Outlet} -> {action.State} IP={device.IpAddress} 结果={(success ? "ok" : "fail")}");
        }
        catch (Exception ex)
        {
            LogError($"Error controlling device {device.Name} ({device.DeviceId})", ex);
        }
    }

    /// <summary>
    /// 同步规则中的设备名称为配置中的最新名称
    /// </summary>
    private static void SyncActionDeviceNames(LinkerConfig config)
    {
        foreach (var rule in config.Rules)
        {
            foreach (var action in rule.Actions)
            {
                var device = config.Devices.FirstOrDefault(d => d.DeviceId == action.DeviceId);
                if (device != null && action.Name != device.Name)
                {
                    action.Name = device.Name;
                }
            }
        }
    }

    /// <summary>
    /// 查找匹配的规则（支持新旧格式）
    /// H-15 修复：移除下划线替换匹配，仅精确匹配
    /// </summary>
    private static List<LinkerRule> FindMatchingRules(List<LinkerRule> rules, string eventName)
    {
        // 首先尝试旧格式匹配（Event 属性）
        var matches = rules.Where(r =>
            !string.IsNullOrEmpty(r.Event) &&
            r.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase) &&
            r.Enabled) // 只选启用的规则
            .ToList();

        if (matches.Count > 0) return matches;

        // 新格式匹配（Conditions 中的 Type）
        return rules.Where(r =>
            r.Enabled && // 只选启用的规则
            r.Conditions.Any(c => c.Type.Equals(eventName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
