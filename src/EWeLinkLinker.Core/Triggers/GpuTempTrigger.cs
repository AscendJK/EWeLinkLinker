using System.Diagnostics;
using EWeLinkLinker.Core.Models;
using LibreHardwareMonitor.Hardware;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// GPU温度触发器（边沿检测型）
/// 参数格式: 阈值（摄氏度），如 "80"
/// 范围参数格式: "min,max" 如 "70,90"
/// </summary>
[Trigger("gpu_temp", "GPU温度", "GPU温度超过阈值时触发（支持NVIDIA/AMD/Intel）")]
public class GpuTempTrigger : OptimizedTriggerBase
{
    private readonly string _parameter;
    private readonly string _parameter2;
    private readonly string _releaseBand;
    private readonly string _releaseParameter;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;
    private int _pollCount;
    // 当前实际读取的传感器名称（供日志诊断：确认是否选到 GPU Core / Hot Spot / Memory）
    private static string? _sensorName;

    public override string Type => "gpu_temp";
    public override string DisplayName => "GPU温度";

    public GpuTempTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _parameter2 = config.Parameter2;
        _releaseBand = config.ReleaseBand;
        _comparison = config.Comparison;

        if (!ComparisonHelper.TryParseNumber(config.Parameter, out _))
            throw new ArgumentException("温度阈值必须为数字");

        if (!ComparisonHelper.ValidateRelease(config.Parameter, _releaseBand, _comparison, out var releaseError))
            throw new ArgumentException(releaseError);

        _releaseParameter = ComparisonHelper.ResolveRelease(config.Parameter, _releaseBand, _comparison);
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "温度阈值不能为空";
            return false;
        }
        if (!ComparisonHelper.TryParseNumber(parameter, out var temp) || temp < 0)
        {
            errorMessage = "温度阈值必须为大于等于 0 的数字";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        // 使用传感器缓存（同一轮轮询中所有 GpuTempTrigger 共享同一个值）
        var temp = SensorCache != null
            ? SensorCache.GetOrCreate("gpu_temp", ReadGpuTemperature)
            : ReadGpuTemperature();

        LastReadingAvailable = !float.IsNaN(temp);
        if (float.IsNaN(temp)) return ValueTask.FromResult(false);

        // 最长粘住的计时挂在规则上（RuleTrigger），不在这里：每个条件各一个钟会让
        // 一条 AND 规则每满一次计时被每个条件各逼重发一次。

        // 滞回：已锁存时，只有越过解除线才算不再满足
        var isTriggered = ComparisonHelper.Evaluate(temp, _parameter, _parameter2, _comparison)
                          || (_wasTriggered && !ComparisonHelper.IsReleased(temp, _releaseParameter, _comparison));

        _pollCount++;
        if (_pollCount % 10 == 0)
        {
            // 记录实际读数 vs 阈值，便于判断比较语义是否正确
            var threshold = string.IsNullOrEmpty(_parameter2)
                ? $"{_parameter}°C"
                : $"{_parameter}~{_parameter2}°C";
            Log(TraceLevel.Info,
                $"GPU温度: {temp:F1}°C ({_sensorName ?? "?"}), 阈值: {_comparison} {threshold}, 状态: {(isTriggered ? "满足" : "不满足")}");
        }

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
    /// 读 GPU 温度：统一走 HardwareSensorReader（与 CPU 共用同一个 LHM 实例，
    /// 内核驱动只加载一次）。传感器优先级见 PickGpuTemperature。
    /// </summary>
    private static float ReadGpuTemperature()
    {
        if (HardwareSensorReader.TryReadGpuTemperature(out var value, out var source))
        {
            _sensorName = source;
            return value;
        }

        _sensorName = null;
        return float.NaN;
    }
}
