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
    private DateTime? _latchedSinceUtc;
    private int _pollCount;
    // 当前实际读取的传感器名称（供日志诊断：确认是否选到 GPU Core / Hot Spot / Memory）
    private static string? _sensorName;

    // 静态共享的 GPU 硬件实例（所有 GpuTempTrigger 共享）
    private static Computer? _sharedComputer;
    private static IHardware? _sharedGpu;
    private static bool _gpuInitialized;
    private static bool _gpuInitFailed;
    private static readonly object _initLock = new();

    public override string Type => "gpu_temp";
    public override string DisplayName => "GPU温度";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(10);

    public GpuTempTrigger(TriggerConfig config) : base()
    {
        _parameter = config.Parameter;
        _parameter2 = config.Parameter2;
        _releaseBand = config.ReleaseBand;
        _comparison = config.Comparison;

        if (!float.TryParse(config.Parameter, out _))
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
        // 使用传感器缓存（同一轮轮询中所有 GpuTempTrigger 共享同一个值）
        var temp = SensorCache != null
            ? SensorCache.GetOrCreate("gpu_temp", ReadGpuTemperature)
            : ReadGpuTemperature();

        if (float.IsNaN(temp)) return ValueTask.FromResult(false);

        var nowUtc = DateTime.UtcNow;

        // 锁存超时：强制松开，让本轮重新判定。读数仍然满足时也会重发一次动作，
        // 用来纠正"规则以为已经处理过、设备其实被人手动改过"。只在启用滞回时生效。
        if (_wasTriggered && !string.IsNullOrEmpty(_releaseParameter)
            && ComparisonHelper.IsHoldExpired(_latchedSinceUtc, nowUtc))
        {
            _wasTriggered = false;
            _latchedSinceUtc = null;
            State = TriggerState.Monitoring;
        }

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
    /// 静态释放 GPU 硬件实例（由 PollingScheduler.DisposeAsync 调用）
    /// </summary>
    internal static void StaticDispose()
    {
        lock (_initLock)
        {
            try { _sharedComputer?.Close(); } catch { }
            try { (_sharedComputer as IDisposable)?.Dispose(); } catch { }
            _sharedComputer = null;
            _sharedGpu = null;
            _gpuInitialized = false;
            _gpuInitFailed = false;
        }
    }

    private static void InitializeGpu()
    {
        try
        {
            _sharedComputer = new Computer { IsGpuEnabled = true };
            _sharedComputer.Open();

            foreach (var hardware in _sharedComputer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.GpuNvidia
                    || hardware.HardwareType == HardwareType.GpuAmd
                    || hardware.HardwareType == HardwareType.GpuIntel)
                {
                    _sharedGpu = hardware;
                    break;
                }
            }

            if (_sharedGpu == null)
            {
                _gpuInitFailed = true;
                EWeLinkLinker.Core.Logging.SimpleLogger.Log("[GPU] No supported GPU found");
            }

            _gpuInitialized = true;
        }
        catch (Exception ex)
        {
            _gpuInitFailed = true;
            _gpuInitialized = true;
            EWeLinkLinker.Core.Logging.SimpleLogger.Log($"[GPU] Initialization failed: {ex.Message}");
        }
    }

    private static float ReadGpuTemperature()
    {
        // 在锁内获取引用，避免释放锁后被其他线程置为 null
        IHardware? gpu;
        lock (_initLock)
        {
            if (!_gpuInitialized && !_gpuInitFailed)
                InitializeGpu();

            // H-? 修复：在锁内调用 gpu.Update()，防止多线程并发 Update 导致竞态
            if (_sharedGpu != null)
            {
                try { _sharedGpu.Update(); } catch { }
            }
            gpu = _sharedGpu;
        }

        if (gpu == null) return float.NaN;

        try
        {
            // 优先级1：GPU 核心温度（GPU Core / Core），避免选到 Hot Spot（偏高）或 Memory（偏低）
            foreach (var sensor in gpu.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                var name = sensor.Name;
                if (name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Core", StringComparison.OrdinalIgnoreCase))
                {
                    _sensorName = name;
                    return sensor.Value.Value;
                }
            }

            // 优先级2：通用 GPU 温度（排除易误导的显存/热点/结温传感器）
            foreach (var sensor in gpu.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue) continue;
                var name = sensor.Name;
                if (name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("Junction", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
                {
                    _sensorName = name;
                    return sensor.Value.Value;
                }
            }

            // 优先级3：任意温度传感器（兜底）
            foreach (var sensor in gpu.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    _sensorName = sensor.Name;
                    return sensor.Value.Value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GPU] Read temperature failed: {ex.Message}");
        }

        return float.NaN;
    }
}
