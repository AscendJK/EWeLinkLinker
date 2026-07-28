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
    private bool _gpuInitialized;  // 防止重复初始化
    private IHardware? _gpuHardware;
    private Computer? _computer;

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

    protected override void OnStart()
    {
        try
        {
            InitializeGpu();
        }
        catch (Exception ex)
        {
            Log(TraceLevel.Warning, $"GPU 初始化失败: {ex.Message}");
        }
    }

    private void InitializeGpu()
    {
        if (_gpuInitialized) return;  // 只初始化一次
        _gpuInitialized = true;

        try
        {
            _computer = new Computer { IsGpuEnabled = true };
            _computer.Open();

            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType == HardwareType.GpuNvidia
                    || hardware.HardwareType == HardwareType.GpuAmd
                    || hardware.HardwareType == HardwareType.GpuIntel)
                {
                    _gpuHardware = hardware;
                    Log(TraceLevel.Info, $"检测到 GPU: {hardware.Name}");
                    break;
                }
            }

            if (_gpuHardware == null)
            {
                Log(TraceLevel.Warning, "未检测到支持的 GPU");
            }
        }
        catch (Exception ex)
        {
            // H-8 修复：初始化失败时重置标志，下次 Start 可重试
            Log(TraceLevel.Warning, $"GPU 初始化异常: {ex.Message}");
            _gpuInitialized = false;
            _computer?.Close();
            _computer = null;
        }
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        if (_gpuHardware == null)
        {
            InitializeGpu();
            if (_gpuHardware == null) return ValueTask.FromResult(false);
        }

        var temp = GetGpuTemperature();
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

    private int _pollCount;

    private float GetGpuTemperature()
    {
        if (_gpuHardware == null) return float.NaN;

        try
        {
            _gpuHardware.Update();

            foreach (var sensor in _gpuHardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    if (sensor.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    {
                        return sensor.Value.Value;
                    }
                }
            }

            foreach (var sensor in _gpuHardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    return sensor.Value.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log(TraceLevel.Warning, $"读取 GPU 温度失败: {ex.Message}");
        }

        return float.NaN;
    }

    protected override void OnDispose()
    {
        _gpuHardware = null;
        // 使用 Dispose 而非 Close，确保非托管资源释放
        if (_computer != null)
        {
            _computer.Close();
            (_computer as IDisposable)?.Dispose();
            _computer = null;
        }
    }
}
