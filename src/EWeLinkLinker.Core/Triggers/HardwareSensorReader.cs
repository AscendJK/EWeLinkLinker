using LibreHardwareMonitor.Hardware;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 一条温度传感器的快照。<see cref="PickCpuTemperature"/> / <see cref="PickGpuTemperature"/>
/// 只吃这个结构，于是"该选哪个传感器"这件事可以被单测钉住，不用真的插一块 4090。
/// </summary>
internal readonly record struct SensorSnapshot(string Name, float? Value);

/// <summary>
/// CPU/GPU 温度的**唯一**硬件读取入口（LibreHardwareMonitor）。
///
/// 为什么要有这一层：GPU 原来自己 new 了一个 <c>Computer</c>，CPU 走的是 WMI 的
/// <c>MSAcpi_ThermalZoneTemperature</c>。后者是主板 EC 上报的 ACPI 热区，不是 CPU 封装温度，
/// 而且刷新粒度极粗——本机实测：满载时读数卡 12 秒不动再一步跳 12°C，与封装温度的瞬时偏差
/// 中位 12°C、最大 18°C，负载结束后封装已降到 60°C 它仍显示 77°C。
/// 于是 `CPU<65` 这种规则要么迟迟不满足、要么在真实温度早已回落时还判"热"。
///
/// 现在两者共用一个实例：LHM 的内核驱动只加载一次，Update() 按轮询节流，
/// 取不到时由调用方回退到 ACPI 热区（功能不退化，只是精度回到旧水平）。
/// </summary>
internal static class HardwareSensorReader
{
    private static readonly object Gate = new();
    private static Computer? _computer;
    private static IHardware? _cpu;
    private static IHardware? _gpu;
    private static bool _initialized;
    private static bool _initFailed;
    private static DateTime _lastUpdateUtc = DateTime.MinValue;

    /// <summary>同一轮里 CPU 与 GPU 各读一次，不该因此 Update 两遍。</summary>
    private static readonly TimeSpan MinUpdateGap = TimeSpan.FromMilliseconds(800);

    internal static bool Available
    {
        get { lock (Gate) { EnsureInitialized(); return _cpu != null || _gpu != null; } }
    }

    private static void EnsureInitialized()
    {
        if (_initialized || _initFailed) return;
        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();

            foreach (var hardware in _computer.Hardware)
            {
                if (_cpu == null && hardware.HardwareType == HardwareType.Cpu) _cpu = hardware;
                if (_gpu == null && (hardware.HardwareType == HardwareType.GpuNvidia
                                     || hardware.HardwareType == HardwareType.GpuAmd
                                     || hardware.HardwareType == HardwareType.GpuIntel))
                {
                    _gpu = hardware;
                }
            }

            _initialized = true;
            Logging.SimpleLogger.Log($"[Sensors] LHM 初始化完成: cpu={_cpu?.Name ?? "无"}, gpu={_gpu?.Name ?? "无"}");
        }
        catch (Exception ex)
        {
            _initFailed = true;
            _initialized = true;
            Logging.SimpleLogger.Log($"[Sensors] LHM 初始化失败，CPU 将回退到 ACPI 热区: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>按节流更新硬件读数（同一轮里 CPU/GPU 共用一次 Update）。</summary>
    private static void UpdateThrottled()
    {
        if (DateTime.UtcNow - _lastUpdateUtc < MinUpdateGap) return;
        _lastUpdateUtc = DateTime.UtcNow;

        try { _cpu?.Update(); } catch { }
        try { _gpu?.Update(); } catch { }
    }

    /// <summary>读 CPU 温度；返回 false 表示这一轮拿不到（调用方回退到 ACPI 热区）。</summary>
    internal static bool TryReadCpuTemperature(out float value, out string source)
    {
        value = float.NaN;
        source = "";
        lock (Gate)
        {
            EnsureInitialized();
            if (_cpu == null) return false;

            UpdateThrottled();

            var snapshots = new List<SensorSnapshot>();
            try
            {
                foreach (var sensor in _cpu.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;
                    snapshots.Add(new SensorSnapshot(sensor.Name, sensor.Value));
                }
            }
            catch { return false; }

            var picked = PickCpuTemperature(snapshots);
            if (picked == null) return false;

            value = picked.Value.Value!.Value;
            source = picked.Value.Name;
            return true;
        }
    }

    /// <summary>读 GPU 温度；返回 false 表示没有可用的 GPU 温度传感器。</summary>
    internal static bool TryReadGpuTemperature(out float value, out string source)
    {
        value = float.NaN;
        source = "";
        lock (Gate)
        {
            EnsureInitialized();
            if (_gpu == null) return false;

            UpdateThrottled();

            var snapshots = new List<SensorSnapshot>();
            try
            {
                foreach (var sensor in _gpu.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature) continue;
                    snapshots.Add(new SensorSnapshot(sensor.Name, sensor.Value));
                }
            }
            catch { return false; }

            var picked = PickGpuTemperature(snapshots);
            if (picked == null) return false;

            value = picked.Value.Value!.Value;
            source = picked.Value.Name;
            return true;
        }
    }

    /// <summary>
    /// CPU 温度优先选哪一支：
    /// <c>CPU Package</c>（Intel 封装）→ <c>Core Max</c> → <c>Core Average</c>
    /// → 名字里带 Package → AMD 的 <c>Tctl/Tdie</c> → 名字里同时带 CPU 和 Core/Temp 的。
    /// 找不到就返回 null，由调用方回退到 ACPI 热区。
    /// </summary>
    internal static SensorSnapshot? PickCpuTemperature(IEnumerable<SensorSnapshot> sensors)
    {
        var valid = sensors.Where(s => s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 150).ToList();
        if (valid.Count == 0) return null;

        SensorSnapshot? Find(Func<string, bool> predicate) =>
            valid.Cast<SensorSnapshot?>().FirstOrDefault(s => predicate(s!.Value.Name));

        return Find(n => n.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Equals("Core Max", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Equals("Core Average", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Contains("Package", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Contains("CPU", StringComparison.OrdinalIgnoreCase)
                        && (n.Contains("Core", StringComparison.OrdinalIgnoreCase)
                            || n.Contains("Temp", StringComparison.OrdinalIgnoreCase)))
            ?? valid.Cast<SensorSnapshot?>().First();
    }

    /// <summary>
    /// GPU 温度优先选哪一支：<c>GPU Core</c> / <c>Core</c> → 名字带 GPU 但排除
    /// Memory / Hot Spot / Junction / VRAM（这些会明显偏高或偏低）→ 任意温度兜底。
    /// </summary>
    internal static SensorSnapshot? PickGpuTemperature(IEnumerable<SensorSnapshot> sensors)
    {
        var valid = sensors.Where(s => s.Value.HasValue && s.Value.Value > 0 && s.Value.Value < 150).ToList();
        if (valid.Count == 0) return null;

        SensorSnapshot? Find(Func<string, bool> predicate) =>
            valid.Cast<SensorSnapshot?>().FirstOrDefault(s => predicate(s!.Value.Name));

        return Find(n => n.Contains("GPU Core", StringComparison.OrdinalIgnoreCase)
                         || n.Equals("Core", StringComparison.OrdinalIgnoreCase))
            ?? Find(n => n.Contains("GPU", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("Memory", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("Junction", StringComparison.OrdinalIgnoreCase)
                         && !n.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
            ?? valid.Cast<SensorSnapshot?>().First();
    }

}
