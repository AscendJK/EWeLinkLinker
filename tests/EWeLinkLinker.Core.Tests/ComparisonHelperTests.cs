using System.Globalization;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Triggers;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 比较与滞回：这些是规则语义的地基，纯函数，最容易也最值得锁死。
/// </summary>
public class ComparisonHelperTests
{
    // ── 解除线的派生（配置里存带宽，不存绝对值） ─────────────────────────

    [Theory]
    [InlineData("80", "3", ComparisonOperator.Gte, "77")]
    [InlineData("80", "3", ComparisonOperator.Gt, "77")]
    [InlineData("65", "3", ComparisonOperator.Lte, "68")]
    [InlineData("65", "3", ComparisonOperator.Lt, "68")]
    public void 解除线_按比较符方向从带宽派生(string trigger, string band, ComparisonOperator cmp, string expected)
        => Assert.Equal(expected, ComparisonHelper.ResolveRelease(trigger, band, cmp));

    [Theory]
    [InlineData("80", "", ComparisonOperator.Gte)]      // 不启用滞回
    [InlineData("80", "0", ComparisonOperator.Gte)]     // 带宽必须为正
    [InlineData("80", "-1", ComparisonOperator.Gte)]
    [InlineData("80", "3", ComparisonOperator.Eq)]      // 相等比较没有方向
    [InlineData("80", "3", ComparisonOperator.Range)]
    [InlineData("abc", "3", ComparisonOperator.Gte)]
    public void 解除线_不成立的情形返回空串(string trigger, string band, ComparisonOperator cmp)
        => Assert.Equal("", ComparisonHelper.ResolveRelease(trigger, band, cmp));

    [Fact]
    public void 解除线_保留两位小数且不跟系统区域走()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("58.5", ComparisonHelper.ResolveRelease("62.5", "4", ComparisonOperator.Gte));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ── 是否已越过解除线 ──────────────────────────────────────────────

    [Theory]
    [InlineData(76.9f, "77", ComparisonOperator.Gte, true)]    // 跌破解除线 → 放开
    [InlineData(77f, "77", ComparisonOperator.Gte, false)]     // 恰好等于解除线 → 仍锁存
    [InlineData(78f, "77", ComparisonOperator.Gte, false)]
    [InlineData(68.1f, "68", ComparisonOperator.Lte, true)]
    [InlineData(68f, "68", ComparisonOperator.Lte, false)]
    public void 解除判定_上升型要跌破_下降型要越过(float actual, string release, ComparisonOperator cmp, bool expected)
        => Assert.Equal(expected, ComparisonHelper.IsReleased(actual, release, cmp));

    [Fact]
    public void 解除判定_无解除线或读数失效时直接放开()
    {
        Assert.True(ComparisonHelper.IsReleased(80f, null, ComparisonOperator.Gte));   // 未启用滞回
        Assert.True(ComparisonHelper.IsReleased(80f, "", ComparisonOperator.Gte));
        Assert.True(ComparisonHelper.IsReleased(float.NaN, "77", ComparisonOperator.Gte)); // 读数失效 ⇒ 不额外锁住
    }

    // ── 带宽校验 ─────────────────────────────────────────────────────

    [Fact]
    public void 带宽校验_空值视为不启用()
        => Assert.True(ComparisonHelper.ValidateRelease("80", "", ComparisonOperator.Gte, out var err) && err is null);

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("abc")]
    [InlineData("3,5")]
    public void 带宽校验_非正数或非数字被拒(string band)
    {
        Assert.False(ComparisonHelper.ValidateRelease("80", band, ComparisonOperator.Gte, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Theory]
    [InlineData(ComparisonOperator.Eq)]
    [InlineData(ComparisonOperator.Range)]
    [InlineData(ComparisonOperator.OutsideRange)]
    public void 带宽校验_不支持滞回的比较符被拒(ComparisonOperator cmp)
        => Assert.False(ComparisonHelper.ValidateRelease("80", "3", cmp, out _));

    // ── 比较本体 ─────────────────────────────────────────────────────

    [Fact]
    public void 范围比较_含端点()
    {
        Assert.True(ComparisonHelper.Evaluate(70f, "70", "90", ComparisonOperator.Range));
        Assert.True(ComparisonHelper.Evaluate(90f, "70", "90", ComparisonOperator.Range));
        Assert.False(ComparisonHelper.Evaluate(90.1f, "70", "90", ComparisonOperator.Range));
        Assert.True(ComparisonHelper.Evaluate(90.1f, "70", "90", ComparisonOperator.OutsideRange));
        Assert.False(ComparisonHelper.Evaluate(70f, "70", "90", ComparisonOperator.OutsideRange));
    }

    [Fact]
    public void 相等比较_有绝对容差()
    {
        Assert.True(ComparisonHelper.Evaluate(62.5f, "62.5", "", ComparisonOperator.Eq));
        Assert.True(ComparisonHelper.Evaluate(62.5004f, "62.5", "", ComparisonOperator.Eq));
        Assert.False(ComparisonHelper.Evaluate(62.51f, "62.5", "", ComparisonOperator.Eq));
        Assert.True(ComparisonHelper.Evaluate(62.51f, "62.5", "", ComparisonOperator.Neq));
    }

    [Fact]
    public void 读数为NaN时安全失败()
    {
        foreach (var cmp in Enum.GetValues<ComparisonOperator>())
            Assert.False(ComparisonHelper.Evaluate(float.NaN, "50", "", cmp));
    }

    [Fact]
    public void 参数解析失败不当成阈值0()
    {
        // 参数坏了 → ParseSingle 返 NaN → 所有分支 false，而不是"阈值 0 ⇒ 恒满足"
        Assert.False(ComparisonHelper.Evaluate(10f, "oops", "", ComparisonOperator.Gte));
        Assert.False(ComparisonHelper.Evaluate(-5f, "oops", "", ComparisonOperator.Lte));
    }

    // ── 数字一律按不变文化解析 ────────────────────────────────────────

    [Fact]
    public void 数字按不变文化解析_不跟系统区域漂()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.True(ComparisonHelper.TryParseNumber("62.5", out var v));
            Assert.Equal(62.5f, v);
            Assert.False(ComparisonHelper.TryParseNumber("2,5", out _)); // 逗号绝不能被当成小数点或千分位
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ── 粘住计时 ─────────────────────────────────────────────────────

    [Fact]
    public void 粘住计时_没有起表就永不过期()
        => Assert.False(ComparisonHelper.IsHoldExpired(null, DateTime.UtcNow));

    [Fact]
    public void 粘住计时_满一个周期即过期()
    {
        var saved = ComparisonHelper.MaxReleaseHold;
        try
        {
            ComparisonHelper.MaxReleaseHold = TimeSpan.FromMinutes(30);
            var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
            Assert.False(ComparisonHelper.IsHoldExpired(now.AddMinutes(-29), now));
            Assert.True(ComparisonHelper.IsHoldExpired(now.AddMinutes(-30), now));
        }
        finally
        {
            ComparisonHelper.MaxReleaseHold = saved;
        }
    }
}
