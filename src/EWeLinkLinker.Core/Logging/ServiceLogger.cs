using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Logging;

/// <summary>
/// 统一的服务端日志记录器
/// </summary>
public sealed class ServiceLogger
{
    private readonly string _logPath;
    private static readonly object LogLock = new();
    private volatile bool _enabled;

    public ServiceLogger(string logPath, bool enabled = true)
    {
        _logPath = logPath;
        _enabled = enabled;
        var logDir = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(logDir))
        {
            try { Directory.CreateDirectory(logDir); } catch { }
        }
        // 启动时清理旧日志
        CleanupOldLogs(logDir);
    }

    /// <summary>
    /// 清理超过 7 天的旧日志文件
    /// </summary>
    private static void CleanupOldLogs(string? logDir)
    {
        if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir)) return;
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            var oldFiles = Directory.GetFiles(logDir, "service-*.log")
                .Where(f => Path.GetFileName(f).Length >= 22 &&  // service-YYYY-MM-DD.log
                           DateTime.TryParseExact(
                               Path.GetFileName(f).Substring(8, 10),
                               "yyyy-MM-dd",
                               null,
                               System.Globalization.DateTimeStyles.None,
                               out var date) && date < cutoff);
            foreach (var file in oldFiles)
            {
                try { File.Delete(file); } catch { }
            }
            // 同时清理 .old 文件
            foreach (var oldFile in Directory.GetFiles(logDir, "*.log.old"))
            {
                try { File.Delete(oldFile); } catch { }
            }
        }
        catch { }
    }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public void Info(string message) => Log("INFO", message);
    public void Warn(string message) => Log("WARN", message);
    public void Error(string message, Exception? ex = null) => Log("ERROR", $"{message}{(ex != null ? $": {ex.Message}" : "")}");
    public void Debug(string message) => Log("DEBUG", message);

    /// <summary>
    /// 记录规则加载信息
    /// </summary>
    public void LogRulesLoaded(int totalRules, int activeRules, int totalConditions, int totalActions)
    {
        Log("INFO", $"========== 规则加载完成 ==========");
        Log("INFO", $"  总规则数: {totalRules}");
        Log("INFO", $"  活跃规则: {activeRules}");
        Log("INFO", $"  总条件数: {totalConditions}");
        Log("INFO", $"  总动作数: {totalActions}");
        Log("INFO", $"=================================");
    }

    /// <summary>
    /// 记录单个规则信息
    /// </summary>
    public void LogRuleDetails(string ruleName, string ruleId, List<ConditionInfo> conditions, List<ActionInfo> actions)
    {
        Log("INFO", $"规则 [{ruleName}] ({ruleId})");
        Log("INFO", $"  条件 ({conditions.Count}):");
        foreach (var cond in conditions)
        {
            Log("INFO", $"    - {GetConditionDescription(cond)}");
        }
        Log("INFO", $"  动作 ({actions.Count}):");
        foreach (var action in actions)
        {
            Log("INFO", $"    - {GetActionDescription(action)}");
        }
    }

    /// <summary>
    /// 记录触发器状态
    /// </summary>
    public void LogTriggerStatus(string triggerType, string triggerId, bool isMonitoring, string? parameter = null)
    {
        var status = isMonitoring ? "监控中" : "已停止";
        var param = string.IsNullOrEmpty(parameter) ? "" : $" 参数={parameter}";
        Log("INFO", $"[{triggerType}] {triggerId} -> {status}{param}");
    }

    /// <summary>
    /// 记录条件评估结果
    /// </summary>
    public void LogConditionEvaluated(string conditionType, string parameter, bool result)
    {
        var resultText = result ? "✓ 满足" : "✗ 不满足";
        Log("DEBUG", $"条件评估 [{conditionType}={parameter}] -> {resultText}");
    }

    /// <summary>
    /// 记录规则触发
    /// </summary>
    public void LogRuleTriggered(string ruleName, string reason)
    {
        Log("INFO", $"!! 规则触发 [{ruleName}] 原因: {reason}");
    }

    /// <summary>
    /// 记录动作执行
    /// </summary>
    public void LogActionExecuted(string deviceName, int outlet, string state, bool success)
    {
        var result = success ? "成功" : "失败";
        Log("INFO", $"动作执行 {deviceName} 通道{outlet} -> {state} [{result}]");
    }

    /// <summary>
    /// 记录配置重载
    /// </summary>
    public void LogConfigReloaded(bool success, int ruleCount, string? error = null)
    {
        if (success)
        {
            Log("INFO", $"配置重载成功: {ruleCount} 条规则");
        }
        else
        {
            Log("ERROR", $"配置重载失败: {error}");
        }
    }

    /// <summary>
    /// 记录监听器启动
    /// </summary>
    public void LogListenerStarted(string listenerType, string interval)
    {
        Log("INFO", $"监听器启动 [{listenerType}] 间隔: {interval}");
    }

    /// <summary>
    /// 记录监听器停止
    /// </summary>
    public void LogListenerStopped(string listenerType)
    {
        Log("INFO", $"监听器停止 [{listenerType}]");
    }

    private static string GetConditionDescription(ConditionInfo cond)
    {
        var opStr = GetComparisonString(cond.Comparison);
        return cond.Type switch
        {
            "time" => $"时间 {opStr} {cond.Parameter}",
            "interval" => $"间隔 {cond.Parameter} 分钟",
            "cpu_temp" => $"CPU温度 {opStr} {cond.Parameter}°C",
            "cpu_usage" => $"CPU使用率 {opStr} {cond.Parameter}%",
            "gpu_temp" => $"GPU温度 {opStr} {cond.Parameter}°C",
            "app_start" => $"应用启动: {cond.Parameter}",
            "app_close" => $"应用关闭: {cond.Parameter}",
            "boot" => "开机",
            "shutdown" => "关机",
            "sleep" => "睡眠",
            "wake" => "唤醒",
            _ => $"{cond.Type} {opStr} {cond.Parameter}"
        };
    }

    private static string GetComparisonString(ComparisonOperator comparison)
    {
        return comparison switch
        {
            ComparisonOperator.Gte => "≥",
            ComparisonOperator.Gt => ">",
            ComparisonOperator.Lte => "≤",
            ComparisonOperator.Lt => "<",
            ComparisonOperator.Eq => "=",
            ComparisonOperator.Neq => "≠",
            ComparisonOperator.Range => "范围",
            _ => "="
        };
    }

    private static string GetActionDescription(ActionInfo action)
    {
        var state = action.State == "on" ? "开" : "关";
        return $"设备[{action.DeviceName}] 通道{action.Outlet} -> {state}";
    }

    private void Log(string level, string message)
    {
        // 检查全局开关和本地开关
        if (!LoggerConfig.IsEnabled || !_enabled) return;
        try
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            lock (LogLock)
            {
                File.AppendAllText(_logPath, logEntry + "\n");
            }
        }
        catch { }
    }
}

/// <summary>
/// 条件信息（用于日志）
/// </summary>
public record ConditionInfo(string Type, string Parameter, ComparisonOperator Comparison = ComparisonOperator.Gte);

/// <summary>
/// 动作信息（用于日志）
/// </summary>
public record ActionInfo(string DeviceName, int Outlet, string State);
