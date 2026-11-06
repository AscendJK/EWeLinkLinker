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
    private readonly string _releaseBand;
    private readonly string _releaseParameter;
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;
    private int _pollCount;

    public override string Type => "cpu_temp";
    public override string DisplayName => "CPU温度";

    public CpuTempTrigger(TriggerConfig config) : base()
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
        // 使用传感器缓存（同一轮轮询中所有 CpuTempTrigger 共享同一个值）
        var temp = SensorCache != null
            ? SensorCache.GetOrCreate("cpu_temp", ReadCpuTemperature)
            : ReadCpuTemperature();

        LastReadingAvailable = !float.IsNaN(temp);
        if (float.IsNaN(temp))
        {
            if (!_loggedWmiError)
            {
                _loggedWmiError = true;
                Log(TraceLevel.Warning, "CPU温度读取失败: WMI 不可用");
            }
            return ValueTask.FromResult(false);
        }

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
                $"CPU温度: {temp:F1}°C ({_sensorSource ?? "?"}), 阈值: {_comparison} {threshold}, 状态: {(isTriggered ? "满足" : "不满足")}");
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
    // 当前这一次读数到底来自哪支传感器（供日志诊断："CPU Package" / "ACPI 热区0"）
    private static string? _sensorSource;

    private static float ReadCpuTemperature()
    {
        // 优先用 LHM 的 CPU 封装温度。ACPI 热区是主板 EC 上报的，本机实测：满载时读数
        // 卡 12–30 秒不动再一步跳 12°C，与封装温度的瞬时偏差中位 12°C、最大 18°C，
        // 负载结束后封装已降到 60°C 它仍显示 77°C —— 用它判 `CPU<65` 必然"人都凉了它还说热"。
        if (HardwareSensorReader.TryReadCpuTemperature(out var lhmTemp, out var lhmSource))
        {
            _sensorSource = lhmSource;
            return lhmTemp;
        }

        // 回退：LHM 打不开（没内核驱动 / 被内存完整性拦住）时退回 ACPI 热区，
        // 精度回到旧水平，但功能不退化。
        var acpi = ReadAcpiZoneTemperature(out var zoneIndex);
        _sensorSource = float.IsNaN(acpi)
            ? null
            : (zoneIndex >= 0 ? $"ACPI 热区{zoneIndex}" : "ACPI 热区");
        return acpi;
    }

    /// <summary>
    /// 旧的取数路径：ACPI 热区，多热区时取最高温的那个。
    /// 只有在 LHM 拿不到 CPU 温度时才会走到这里。
    /// </summary>
    private static float ReadAcpiZoneTemperature(out int hottestZoneIndex)
    {
        hottestZoneIndex = -1;
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");

            // 遍历所有热区，取最高有效温度（多 CCD/多 Die CPU 可能暴露多个 _TZ 热区，
            // 第一个热区不一定是最热的那一个）
            float maxTemp = float.NaN;
            int index = 0;
            foreach (var obj in searcher.Get())
            {
                using (obj)
                {
                    var tempK = Convert.ToUInt32(obj["CurrentTemperature"]);
                    var tempC = (tempK - 2732) / 10.0f;
                    if (tempC > 0 && tempC < 150)
                    {
                        if (float.IsNaN(maxTemp) || tempC > maxTemp)
                        {
                            maxTemp = tempC;
                            hottestZoneIndex = index;
                        }
                    }
                }
                index++;
            }

            if (!float.IsNaN(maxTemp)) return maxTemp;
        }
        catch { }

        return float.NaN;
    }
}
