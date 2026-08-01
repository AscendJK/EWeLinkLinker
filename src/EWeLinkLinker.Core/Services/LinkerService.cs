using System.Collections.Concurrent;
using EWeLinkLinker.Core.Config;
using EWeLinkLinker.Core.Lan;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Token;
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
                var tokens = await _tokenManager.GetValidTokensAsync();
                Log("Token validated successfully");
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

            Log($"  Rule '{rule.Name}': {rule.Actions.Count} actions");
            await ExecuteActionsByDeviceAsync(rule.Actions, config, ct);
        }

        Log($"Event {eventName} completed");
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
