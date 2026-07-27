using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 比较运算辅助类
/// </summary>
internal static class ComparisonHelper
{
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

    private static float ParseSingle(string parameter)
    {
        return float.TryParse(parameter, out var value) ? value : 0;
    }
}
