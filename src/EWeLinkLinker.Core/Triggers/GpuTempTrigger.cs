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
    private readonly ComparisonOperator _comparison;
    private bool _wasTriggered;
    private int _pollCount;

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
        // 使用传感器缓存（同一轮轮询中所有 GpuTempTrigger 共享同一个值）
        var temp = SensorCache != null
            ? SensorCache.GetOrCreate("gpu_temp", ReadGpuTemperature)
            : ReadGpuTemperature();

        if (float.IsNaN(temp)) return ValueTask.FromResult(false);

        var isTriggered = ComparisonHelper.Evaluate(temp, _parameter, _parameter2, _comparison);

        _pollCount++;
        if (_pollCount % 10 == 0)
        {
            Log(TraceLevel.Info, $"GPU温度: {temp:F1}°C, 状态: {(isTriggered ? "满足" : "不满足")}");
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

            gpu = _sharedGpu;
        }

        if (gpu == null) return float.NaN;

        try
        {
            gpu.Update();

            foreach (var sensor in gpu.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    if (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        return sensor.Value.Value;
                }
            }

            foreach (var sensor in gpu.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                    return sensor.Value.Value;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GPU] Read temperature failed: {ex.Message}");
        }

        return float.NaN;
    }
}
