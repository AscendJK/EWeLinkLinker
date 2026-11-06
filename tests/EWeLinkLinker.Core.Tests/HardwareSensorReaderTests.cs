using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// "该信哪一支温度传感器"是这次修复的核心：CPU 原来读的是 ACPI 热区（主板 EC 上报，
/// 12–30 秒一跳、偏差十几度），现在优先走 LHM 的封装温度。选错传感器比逻辑写错更难发现，
/// 所以把选择规则钉在用例里。
/// </summary>
public class HardwareSensorReaderTests
{
    private static SensorSnapshot S(string name, float? value) => new(name, value);

    // ── CPU ──────────────────────────────────────────────────────

    [Fact]
    public void CPU优先选封装温度()
    {
        var picked = HardwareSensorReader.PickCpuTemperature(new[]
        {
            S("CPU Core #1", 54f),
            S("CPU Package", 67f),
            S("Core Max", 65f),
            S("Core Average", 57.4f),
        });

        Assert.Equal("CPU Package", picked!.Value.Name);
        Assert.Equal(67f, picked.Value.Value);
    }

    [Fact]
    public void CPU没有封装温度时退到CoreMax()
    {
        var picked = HardwareSensorReader.PickCpuTemperature(new[]
        {
            S("CPU Core #1", 54f),
            S("Core Max", 65f),
            S("Core Average", 57.4f),
        });

        Assert.Equal("Core Max", picked!.Value.Name);
    }

    [Fact]
    public void CPU再退到CoreAverage()
    {
        var picked = HardwareSensorReader.PickCpuTemperature(new[] { S("CPU Core #1", 54f), S("Core Average", 57.4f) });
        Assert.Equal("Core Average", picked!.Value.Name);
    }

    [Fact]
    public void CPU认AMD的TctlTdie()
    {
        var picked = HardwareSensorReader.PickCpuTemperature(new[] { S("Core (Tctl/Tdie)", 71f), S("CPU Core #1", 60f) });
        Assert.Equal("Core (Tctl/Tdie)", picked!.Value.Name);
    }

    [Theory]
    [InlineData(0f)]        // 0 度是"没有读数"的占位，不是真温度
    [InlineData(-5f)]
    [InlineData(200f)]      // 明显不是温度的脏值
    public void CPU跳过明显无效的读数(float bad)
    {
        var picked = HardwareSensorReader.PickCpuTemperature(new[] { S("CPU Package", bad), S("Core Max", 65f) });
        Assert.Equal("Core Max", picked!.Value.Name);
    }

    [Fact]
    public void CPU没有可用的温度传感器时返回null()
        => Assert.Null(HardwareSensorReader.PickCpuTemperature(new[] { S("CPU Package", null), S("Core Max", float.NaN) }));

    // ── GPU ──────────────────────────────────────────────────────

    [Fact]
    public void GPU优先选GPU核温_排除热点()
    {
        var picked = HardwareSensorReader.PickGpuTemperature(new[]
        {
            S("GPU Hot Spot", 78f),
            S("GPU Core", 51f),
            S("GPU Memory Junction", 66f),
        });

        Assert.Equal("GPU Core", picked!.Value.Name);
        Assert.Equal(51f, picked.Value.Value);
    }

    [Fact]
    public void GPU没有GPU_Core时排除显存与热点()
    {
        var picked = HardwareSensorReader.PickGpuTemperature(new[]
        {
            S("GPU Memory", 66f),
            S("GPU Hot Spot", 78f),
            S("GPU Temperature", 52f),
        });

        Assert.Equal("GPU Temperature", picked!.Value.Name);
    }

    [Fact]
    public void GPU只有被排除的传感器时兜底取第一个()
    {
        var picked = HardwareSensorReader.PickGpuTemperature(new[] { S("GPU Hot Spot", 78f) });
        Assert.Equal("GPU Hot Spot", picked!.Value.Name);
    }

    [Fact]
    public void GPU没有任何温度传感器时返回null()
        => Assert.Null(HardwareSensorReader.PickGpuTemperature(new[] { S("GPU Core", null) }));
}
