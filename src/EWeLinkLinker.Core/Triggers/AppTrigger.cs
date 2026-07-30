using System.Diagnostics;
using EWeLinkLinker.Core.Logging;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 应用启动触发器（优化版 - 无独立定时器）
/// 参数格式: 进程名（不含.exe），如 "notepad", "chrome", "palworld"
/// </summary>
[Trigger("app_start", "应用启动", "指定应用启动时触发")]
public class AppStartTrigger : OptimizedTriggerBase
{
    private readonly string _processName;
    private readonly HashSet<int> _knownProcesses = new();

    public override string Type => "app_start";
    public override string DisplayName => "应用启动";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(3);

    public AppStartTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);

        // 提取纯进程名（去除版本号、 trainer 等）
        _processName = ExtractProcessName(config.Parameter!);
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "进程名不能为空";
            return false;
        }
        if (parameter.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errorMessage = "进程名包含非法字符";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override void OnStart()
    {
        lock (_knownProcesses)
        {
            _knownProcesses.Clear();
            var existing = FindMatchingProcessIds(_processName, SensorCache);
            foreach (var id in existing)
            {
                _knownProcesses.Add(id);
            }
        }
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        lock (_knownProcesses)
        {
            // 传入 SensorCache，同一轮轮询中共享 Process[] 数组
            var currentIds = FindMatchingProcessIds(_processName, SensorCache);

            foreach (var id in currentIds)
            {
                if (!_knownProcesses.Contains(id))
                {
                    _knownProcesses.Add(id);
                    return ValueTask.FromResult(true);
                }
            }

            // 清理已退出的进程
            _knownProcesses.RemoveWhere(id => !currentIds.Contains(id));
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// 智能匹配进程 - 返回匹配进程的 ID 列表
    /// 支持传感器缓存，同一轮轮询中所有 AppTrigger 共享同一个 Process[] 数组
    /// </summary>
    /// <param name="config">进程名配置</param>
    /// <param name="cache">传感器缓存（可为 null，为 null 时独立读取）</param>
    public static List<int> FindMatchingProcessIds(string config, SensorCache? cache = null)
    {
        // 从缓存获取 Process[] 数组，或独立读取
        Process[] allProcesses;
        bool ownsProcesses = cache == null;

        if (cache != null)
        {
            allProcesses = cache.GetOrCreate("processes", () => Process.GetProcesses());
        }
        else
        {
            allProcesses = Process.GetProcesses();
        }

        var matchedIds = new HashSet<int>();
        try
        {
            // 方式1: 精确匹配进程名（不含.exe）
            var cleanName = config.ToLower().Replace(".exe", "").Trim();
            foreach (var p in allProcesses)
            {
                if (p.ProcessName.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                    matchedIds.Add(p.Id);
            }

            if (matchedIds.Count > 0) return matchedIds.ToList();

            // 方式2: 尝试第一个单词（"Palworld v1.0" -> "palworld"）
            var firstWord = cleanName.Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(firstWord) && firstWord != cleanName)
            {
                foreach (var p in allProcesses)
                {
                    if (p.ProcessName.Equals(firstWord, StringComparison.OrdinalIgnoreCase))
                        matchedIds.Add(p.Id);
                }
            }

            if (matchedIds.Count > 0) return matchedIds.ToList();

            // 方式3: 包含匹配（进程名包含配置字符串）
            foreach (var p in allProcesses)
            {
                if (p.ProcessName.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    cleanName.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                    matchedIds.Add(p.Id);
            }

            if (matchedIds.Count > 0) return matchedIds.ToList();

            // 方式4: 尝试匹配 MainWindowTitle（窗口标题）
            foreach (var p in allProcesses)
            {
                if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                    p.MainWindowTitle.Contains(config, StringComparison.OrdinalIgnoreCase))
                    matchedIds.Add(p.Id);
            }

            return matchedIds.ToList();
        }
        finally
        {
            // 只有独立读取时才释放 Process 句柄（缓存的由 SensorCache.Clear() 统一释放）
            if (ownsProcesses)
            {
                foreach (var p in allProcesses)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 智能匹配进程 - 返回匹配的 Process 列表（供需要 Process 对象的场景使用）
    /// 注意：返回的 Process 对象需要调用者释放
    /// </summary>
    public static List<Process> FindMatchingProcesses(string config)
    {
        var allProcesses = Process.GetProcesses();
        var matches = new List<Process>();
        try
        {
            // 方式1: 精确匹配进程名（不含.exe）
            var cleanName = config.ToLower().Replace(".exe", "").Trim();
            foreach (var p in allProcesses)
            {
                if (p.ProcessName.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(p);
            }

            if (matches.Count > 0) return matches.Distinct().ToList();

            // 方式2: 尝试第一个单词
            var firstWord = cleanName.Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(firstWord) && firstWord != cleanName)
            {
                foreach (var p in allProcesses)
                {
                    if (p.ProcessName.Equals(firstWord, StringComparison.OrdinalIgnoreCase))
                        matches.Add(p);
                }
            }

            if (matches.Count > 0) return matches.Distinct().ToList();

            // 方式3: 包含匹配
            foreach (var p in allProcesses)
            {
                if (p.ProcessName.Contains(cleanName, StringComparison.OrdinalIgnoreCase) ||
                    cleanName.Contains(p.ProcessName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(p);
            }

            if (matches.Count > 0) return matches.Distinct().ToList();

            // 方式4: 尝试匹配 MainWindowTitle
            foreach (var p in allProcesses)
            {
                if (!string.IsNullOrEmpty(p.MainWindowTitle) &&
                    p.MainWindowTitle.Contains(config, StringComparison.OrdinalIgnoreCase))
                    matches.Add(p);
            }

            return matches.Distinct().ToList();
        }
        finally
        {
            // 释放不需要的 Process 句柄，只保留匹配的
            var matchedIds = new HashSet<int>();
            foreach (var m in matches)
            {
                try { matchedIds.Add(m.Id); } catch { }
            }
            foreach (var p in allProcesses)
            {
                if (!matchedIds.Contains(p.Id))
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 从配置中提取纯进程名（保持向后兼容）
    /// </summary>
    public static string ExtractProcessName(string config)
    {
        if (string.IsNullOrEmpty(config)) return "";

        // 如果是可执行文件路径，提取文件名
        if (config.Contains('\\') || config.Contains('/'))
        {
            return Path.GetFileNameWithoutExtension(config).ToLower();
        }

        // 如果是带空格的名称，尝试提取第一个单词作为进程名
        var parts = config.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            return parts[0].ToLower();
        }

        return config.ToLower().Replace(".exe", "");
    }
}

/// <summary>
/// 应用关闭触发器（优化版 - 无独立定时器）
/// 参数格式: 进程名（不含.exe）
/// </summary>
[Trigger("app_close", "应用关闭", "指定应用关闭时触发")]
public class AppCloseTrigger : OptimizedTriggerBase
{
    private readonly string _processName;
    private readonly HashSet<int> _trackedProcesses = new();

    public override string Type => "app_close";
    public override string DisplayName => "应用关闭";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(3);

    public AppCloseTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);

        // 提取纯进程名
        _processName = AppStartTrigger.ExtractProcessName(config.Parameter!);
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "进程名不能为空";
            return false;
        }
        if (parameter.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            errorMessage = "进程名包含非法字符";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override void OnStart()
    {
        lock (_trackedProcesses)
        {
            _trackedProcesses.Clear();
            var existing = AppStartTrigger.FindMatchingProcessIds(_processName, SensorCache);
            foreach (var id in existing)
            {
                _trackedProcesses.Add(id);
            }
        }
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        lock (_trackedProcesses)
        {
            var currentIds = AppStartTrigger.FindMatchingProcessIds(_processName, SensorCache);

            // 检查哪些进程已退出
            var exited = _trackedProcesses.Where(id => !currentIds.Contains(id)).ToList();
            if (exited.Count > 0)
            {
                // 更新跟踪列表
                _trackedProcesses.RemoveWhere(id => !currentIds.Contains(id));
                foreach (var id in currentIds)
                {
                    _trackedProcesses.Add(id);
                }
                return ValueTask.FromResult(true);
            }

            // 更新跟踪列表
            foreach (var id in currentIds)
            {
                _trackedProcesses.Add(id);
            }
        }

        return ValueTask.FromResult(false);
    }
}
