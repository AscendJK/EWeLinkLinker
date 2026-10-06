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
    private readonly string _releaseBand;
    private readonly string _releaseParameter;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;
    private DateTime? _latchedSinceUtc;
    private int _pollCount;

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
        _releaseBand = config.ReleaseBand;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
            throw new ArgumentException("使用率阈值必须为数字");

        if (!ComparisonHelper.ValidateRelease(config.Parameter, _releaseBand, _comparison, out var releaseError))
            throw new ArgumentException(releaseError);

        _releaseParameter = ComparisonHelper.ResolveRelease(config.Parameter, _releaseBand, _comparison);
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

        var nowUtc = DateTime.UtcNow;

        // 锁存超时：强制松开，让本轮重新判定。读数仍然满足时也会重发一次动作，
        // 用来纠正"规则以为已经处理过、设备其实被人手动改过"。只在启用滞回时生效。
        if (_wasTriggered && !string.IsNullOrEmpty(_releaseParameter)
            && ComparisonHelper.IsHoldExpired(_latchedSinceUtc, nowUtc))
        {
            _wasTriggered = false;
            _latchedSinceUtc = null;
            State = TriggerState.Monitoring;
            // 这一轮只松开，不顺手补一个脉冲：否则 PollAsync 拿到的是过期的 wasMonitoring，
            // 状态回不到 Triggered，日志写着"条件触发"而规则那边判定为不满足、不会执行动作。
            // 动作交给下一轮真正的上升沿发出。
            return ValueTask.FromResult(false);
        }

        // 滞回：已锁存时，只有越过解除线才算不再满足
        var isTriggered = ComparisonHelper.Evaluate(usage, _parameter, _parameter2, _comparison)
                          || (_wasTriggered && !ComparisonHelper.IsReleased(usage, _releaseParameter, _comparison));

        _pollCount++;
        if (_pollCount % 10 == 0)
        {
            // 记录实际读数 vs 阈值，便于判断比较语义是否正确
            var threshold = string.IsNullOrEmpty(_parameter2)
                ? $"{_parameter}%"
                : $"{_parameter}~{_parameter2}%";
            Log(TraceLevel.Info,
                $"CPU使用率: {usage:F1}%, 阈值: {_comparison} {threshold}, 状态: {(isTriggered ? "满足" : "不满足")}");
        }

        // 边沿检测：从未满足变为满足时触发，保持锁存直到条件消失
        if (isTriggered && !_wasTriggered)
        {
            _wasTriggered = true;
            _latchedSinceUtc = DateTime.UtcNow;
            return ValueTask.FromResult(true);
        }

        // 条件不再满足，复位状态和锁存
        if (!isTriggered && _wasTriggered)
        {
            _wasTriggered = false;
            _latchedSinceUtc = null;
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
