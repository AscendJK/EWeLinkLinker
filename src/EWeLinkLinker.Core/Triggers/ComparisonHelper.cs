using System.Globalization;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 比较运算辅助类
/// </summary>
// 需要被 ConfigApp 调用（保存前校验解除线方向），所以是 public 而非 internal
public static class ComparisonHelper
{
    /// <summary>
    /// 配置和协议里的数字一律按不变文化解析。跟着系统区域走有两个坑：
    /// 同一份 linker.json 换台机器阈值就变；中文/英文区域把逗号当千分位，
    /// "2,5" 会被悄悄解析成 25 ⇒ 阈值差十倍。宁可判"不是数字"，让上层把问题报出来。
    /// </summary>
    public static bool TryParseNumber(string? text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    public static bool TryParseInt(string? text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>填了逗号时给一句人话：只说"不是数字"，用户不知道该改成什么。</summary>
    public static string NumberFormatHint(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains(',')
            ? "（小数点请用英文句点，例如 62.5；这里的逗号会被当成千分位或范围分隔符）"
            : "";
    /// <summary>
    /// 根据比较运算符判断值是否满足条件
    /// </summary>
    /// <param name="actualValue">实际值</param>
    /// <param name="parameter">主参数值（单值或范围最小值）</param>
    /// <param name="parameter2">第二参数值（范围最大值，可为空）</param>
    /// <param name="comparison">比较运算符</param>
    /// <returns>是否满足</returns>
    public static bool Evaluate(float actualValue, string parameter, string parameter2, ComparisonOperator comparison)
    {
        // 传感器读取失败或参数解析失败（NaN）时安全失败：不触发、不误判
        if (float.IsNaN(actualValue)) return false;

        switch (comparison)
        {
            case ComparisonOperator.Gte:
                return actualValue >= ParseSingle(parameter);

            case ComparisonOperator.Gt:
                return actualValue > ParseSingle(parameter);

            case ComparisonOperator.Lte:
                return actualValue <= ParseSingle(parameter);

            case ComparisonOperator.Lt:
                return actualValue < ParseSingle(parameter);

            case ComparisonOperator.Eq:
                return Math.Abs(actualValue - ParseSingle(parameter)) < 0.001f;

            case ComparisonOperator.Neq:
                return Math.Abs(actualValue - ParseSingle(parameter)) >= 0.001f;

            case ComparisonOperator.Range:
                var min = ParseSingle(parameter);
                var max = string.IsNullOrEmpty(parameter2) ? ParseSingle(parameter) : ParseSingle(parameter2);
                return actualValue >= min && actualValue <= max;

            case ComparisonOperator.OutsideRange:
                var min2 = ParseSingle(parameter);
                var max2 = string.IsNullOrEmpty(parameter2) ? ParseSingle(parameter) : ParseSingle(parameter2);
                return actualValue < min2 || actualValue > max2;

            default:
                return false;
        }
    }

    /// <summary>
    /// 兼容旧版单参数调用（范围用 "min,max" 格式）
    /// </summary>
    public static bool Evaluate(float actualValue, string parameter, ComparisonOperator comparison)
    {
        // 尝试从 parameter 中解析范围
        var parameter2 = "";
        if (comparison == ComparisonOperator.Range || comparison == ComparisonOperator.OutsideRange)
        {
            var parts = parameter.Split(',');
            if (parts.Length >= 2)
            {
                parameter = parts[0].Trim();
                parameter2 = parts[1].Trim();
            }
        }
        return Evaluate(actualValue, parameter, parameter2, comparison);
    }

    /// <summary>
    /// 滞回：已锁存的条件现在该不该放开。解除线为空时返回 true，
    /// 等价于"不满足即放开"，即完全维持旧行为。
    /// 上升型（≥ / &gt;）要跌破解除线，下降型（≤ / &lt;）要越过解除线。
    /// </summary>
    public static bool IsReleased(float actualValue, string? releaseParameter, ComparisonOperator comparison)
    {
        // 读数失效时不额外锁住状态，交由调用方的安全失败处理
        if (float.IsNaN(actualValue)) return true;
        if (!TryParseNumber(releaseParameter, out var release)) return true;

        return comparison switch
        {
            ComparisonOperator.Gte or ComparisonOperator.Gt => actualValue < release,
            ComparisonOperator.Lte or ComparisonOperator.Lt => actualValue > release,
            _ => true
        };
    }

    /// <summary>
    /// 锁存最长可保持多久，到期强制松开重新判定（纠正"规则以为已经处理过、
    /// 但设备实际被人手动改过"的状态）。默认 30 分钟；测试可缩短。
    /// </summary>
    public static TimeSpan MaxReleaseHold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 锁存是否已经超时。未记录起始时间（即没有滞回）时永不超时，维持旧行为。
    /// </summary>
    public static bool IsHoldExpired(DateTime? latchedSinceUtc, DateTime nowUtc) =>
        latchedSinceUtc.HasValue && nowUtc - latchedSinceUtc.Value >= MaxReleaseHold;

    /// <summary>
    /// 配置里存的是"带宽"（意图：允许抖动多少），解除线是它派生出来的数。
    /// 方向由比较符决定：≥ / &gt; 往低走才算松开，≤ / &lt; 往高走才算松开。
    /// 这样以后改阈值，回差跟着阈值走，不会悄悄变成另一个值。
    /// </summary>
    public static string ResolveRelease(string triggerParameter, string? releaseBand, ComparisonOperator comparison)
    {
        if (string.IsNullOrWhiteSpace(releaseBand)) return "";
        if (!TryParseNumber(triggerParameter, out var trigger)) return "";
        if (!TryParseNumber(releaseBand, out var band) || band <= 0) return "";

        return comparison switch
        {
            ComparisonOperator.Gte or ComparisonOperator.Gt =>
                (trigger - band).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            ComparisonOperator.Lte or ComparisonOperator.Lt =>
                (trigger + band).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            _ => ""
        };
    }

    /// <summary>
    /// 校验解除带宽。带宽为空 = 不启用滞回，直接通过；方向不再可能填反（由比较符推导），
    /// 所以这里只查"是不是正数"和"这个比较符支不支持滞回"。
    /// </summary>
    public static bool ValidateRelease(string triggerParameter, string? releaseBand,
                                       ComparisonOperator comparison, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(releaseBand)) return true;

        if (!TryParseNumber(triggerParameter, out _))
        {
            errorMessage = "触发阈值不是数字，无法计算解除线";
            return false;
        }
        if (!TryParseNumber(releaseBand, out var band))
        {
            errorMessage = $"解除带宽必须是数字（当前填的是 {releaseBand}）{NumberFormatHint(releaseBand)}";
            return false;
        }
        if (band <= 0)
        {
            errorMessage = "解除带宽必须大于 0";
            return false;
        }
        if (comparison is not (ComparisonOperator.Gte or ComparisonOperator.Gt
                                   or ComparisonOperator.Lte or ComparisonOperator.Lt))
        {
            errorMessage = "解除带宽只支持 ≥ > ≤ < 这几种比较方式";
            return false;
        }

        return true;
    }

    private static float ParseSingle(string parameter)
    {
        // 解析失败返回 NaN：所有比较分支与 NaN 比较都是 false（安全失败），避免坏参数变成阈值 0
        return TryParseNumber(parameter, out var value) ? value : float.NaN;
    }
}
