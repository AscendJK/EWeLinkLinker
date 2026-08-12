using System.Diagnostics;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// CPU使用率触发器（边沿检测型）
/// 参数格式: 阈值（百分比），如 "90" 表示90%
/// </summary>
[Trigger("cpu_usage", "CPU使用率", "CPU使用率超过阈值时触发")]
public class CpuUsageTrigger : OptimizedTriggerBase
{
    private readonly string _parameter;
    private readonly string _parameter2;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;

    // 静态共享 PerformanceCounter（所有 CpuUsageTrigger 共享，线程安全）
    private static PerformanceCounter? _sharedCounter;
    private static readonly object _counterLock = new();
    private static bool _counterInitialized;

    public override string Type => "cpu_usage";
    public override string DisplayName => "CPU使用率";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(5);

    public CpuUsageTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _parameter2 = config.Parameter2;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
            throw new ArgumentException("使用率阈值必须为数字");
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "使用率阈值不能为空";
            return false;
        }
        if (!float.TryParse(parameter, out var usage) || usage < 0 || usage > 100)
        {
            errorMessage = "使用率阈值必须为 0-100 之间的数字";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        // 使用传感器缓存（同一轮轮询中所有 CpuUsageTrigger 共享同一个值）
        var usage = SensorCache != null
            ? SensorCache.GetOrCreate("cpu_usage", ReadCpuUsage)
            : ReadCpuUsage();

        // 读取失败（NaN）时不触发也不复位，保持当前状态（与温度触发器一致，安全失败）
        if (float.IsNaN(usage)) return ValueTask.FromResult(false);

        var isTriggered = ComparisonHelper.Evaluate(usage, _parameter, _parameter2, _comparison);

        // 边沿检测：从未满足变为满足时触发，保持锁存直到条件消失
        if (isTriggered && !_wasTriggered)
        {
            _wasTriggered = true;
            return ValueTask.FromResult(true);
        }

        // 条件不再满足，复位状态和锁存
        if (!isTriggered && _wasTriggered)
        {
            _wasTriggered = false;
            State = TriggerState.Monitoring;
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>
    /// 线程安全地读取 CPU 使用率
    /// </summary>
    private static float ReadCpuUsage()
    {
        lock (_counterLock)
        {
            try
            {
                if (!_counterInitialized)
                {
                    _sharedCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                    _sharedCounter.NextValue();  // 首次调用返回 0，需要预热
                    _counterInitialized = true;
                }
                return _sharedCounter?.NextValue() ?? float.NaN;
            }
            catch
            {
                // 读取失败返回 NaN（安全失败），避免低阈值/反向比较误触发
                return float.NaN;
            }
        }
    }

    /// <summary>
    /// 静态释放 PerformanceCounter（由 PollingScheduler.DisposeAsync 调用）
    /// </summary>
    internal static void StaticDispose()
    {
        lock (_counterLock)
        {
            try { _sharedCounter?.Dispose(); } catch { }
            _sharedCounter = null;
            _counterInitialized = false;
        }
    }
}
