using System.Diagnostics;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// CPU温度触发器（边沿检测型）
/// 参数格式: 阈值（摄氏度），如 "80" 表示80度
/// 范围参数格式: "min,max" 如 "70,90"
/// </summary>
[Trigger("cpu_temp", "CPU温度", "CPU温度超过阈值时触发")]
public class CpuTempTrigger : OptimizedTriggerBase
{
    private readonly string _parameter;
    private readonly string _parameter2;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;

    public override string Type => "cpu_temp";
    public override string DisplayName => "CPU温度";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(10);

    public CpuTempTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _parameter2 = config.Parameter2;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
            throw new ArgumentException("温度阈值必须为数字");
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "温度阈值不能为空";
            return false;
        }
        if (!float.TryParse(parameter, out var temp) || temp < 0)
        {
            errorMessage = "温度阈值必须为大于等于 0 的数字";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var temp = GetCpuTemperature();
        if (float.IsNaN(temp))
        {
            if (!_loggedWmiError)
            {
                _loggedWmiError = true;
                Log(TraceLevel.Warning, "CPU温度读取失败: WMI 不可用");
            }
            return ValueTask.FromResult(false);
        }

        var isTriggered = ComparisonHelper.Evaluate(temp, _parameter, _parameter2, _comparison);

        _pollCount++;
        if (_pollCount % 10 == 0)
        {
            Log(TraceLevel.Info, $"CPU温度: {temp:F1}°C, 状态: {(isTriggered ? "满足" : "不满足")}");
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

    private bool _loggedWmiError;
    private int _pollCount;

    private static float GetCpuTemperature()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");

            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var tempK = Convert.ToUInt32(obj["CurrentTemperature"]);
                    var tempC = (tempK - 2732) / 10.0f;
                    if (tempC > 0 && tempC < 150)
                        return tempC;
                }
            }
        }
        catch { }

        return float.NaN;
    }
}

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
    private PerformanceCounter? _counter;

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

    protected override void OnStart()
    {
        try
        {
            if (_counter == null)
            {
                _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _counter.NextValue();
            }
        }
        catch { }
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        if (_counter == null) return ValueTask.FromResult(false);

        var usage = _counter.NextValue();
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

    protected override void OnDispose()
    {
        _counter?.Dispose();
        _counter = null;
    }
}
