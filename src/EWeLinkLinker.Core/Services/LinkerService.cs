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

        // 查找匹配的规则（支持新旧格式）
        var rule = FindMatchingRule(config.Rules, eventName);

        if (rule == null || rule.Actions.Count == 0)
        {
            Log($"No actions configured for event: {eventName}");
            return;
        }

        // LAN 控制不需要 token，只有云端 API 才需要
        // 关机/睡眠时网络可能已断开，跳过 token 验证
        bool isLocalOnlyEvent = eventName.Equals("shutdown", StringComparison.OrdinalIgnoreCase) ||
                                 eventName.Equals("sleep", StringComparison.OrdinalIgnoreCase);

        if (!isLocalOnlyEvent)
        {
            // 开机/唤醒时尝试获取 token（用于可能的云端操作）
            try
            {
                var tokens = await _tokenManager.GetValidTokensAsync();
                Log("Token validated successfully");
            }
            catch (Exception ex)
            {
                Log($"Token validation failed (LAN control will still work): {ex.Message}");
                // LAN 控制不需要 token，继续执行
            }
        }
        else
        {
            Log("Local-only event (shutdown/sleep), skipping token validation");
        }

        Log($"Found {rule.Actions.Count} actions for event: {eventName}");

        // Execute all actions concurrently (LAN control only)
        var tasks = rule.Actions.Select(action => ExecuteActionAsync(config, action, ct));
        await Task.WhenAll(tasks);

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

        // 并发执行所有动作
        var tasks = rule.Actions.Select(action => ExecuteActionAsync(config, action, ct));
        await Task.WhenAll(tasks);

        Log($"Rule '{rule.Name}' completed");
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
    private static LinkerRule? FindMatchingRule(List<LinkerRule> rules, string eventName)
    {
        // 首先尝试旧格式匹配（Event 属性）
        var match = rules.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.Event) &&
            r.Event.Equals(eventName, StringComparison.OrdinalIgnoreCase));

        if (match != null) return match;

        // 新格式匹配（Conditions 中的 Type）— 精确匹配，不做模糊替换
        return rules.FirstOrDefault(r =>
            r.Conditions.Any(c => c.Type.Equals(eventName, StringComparison.OrdinalIgnoreCase)));
    }
}
