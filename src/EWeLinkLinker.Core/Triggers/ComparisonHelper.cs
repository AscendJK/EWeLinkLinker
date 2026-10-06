using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 比较运算辅助类
/// </summary>
// 需要被 ConfigApp 调用（保存前校验解除线方向），所以是 public 而非 internal
public static class ComparisonHelper
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
        if (!float.TryParse(releaseParameter, out var release)) return true;

        return comparison switch
        {
            ComparisonOperator.Gte or ComparisonOperator.Gt => actualValue < release,
            ComparisonOperator.Lte or ComparisonOperator.Lt => actualValue > release,
            _ => true
        };
    }

    /// <summary>
    /// 校验解除线是否在触发线的另一侧。填反了会导致条件一旦满足就永不重新武装，
    /// 所以要么保存时报错，要么触发器构造时拒绝加载。空解除线 = 不启用滞回，直接通过。
    /// </summary>
    public static bool ValidateRelease(string triggerParameter, string? releaseParameter,
                                       ComparisonOperator comparison, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(releaseParameter)) return true;

        if (!float.TryParse(triggerParameter, out var trigger))
        {
            errorMessage = "触发阈值不是数字，无法校验解除线";
            return false;
        }
        if (!float.TryParse(releaseParameter, out var release))
        {
            errorMessage = "解除线必须是数字";
            return false;
        }

        switch (comparison)
        {
            case ComparisonOperator.Gte:
            case ComparisonOperator.Gt:
                if (release >= trigger)
                {
                    errorMessage = $"解除线必须小于触发阈值 {trigger:0.##}（当前填的是 {release:0.##}），否则条件一旦满足就再也无法重新触发";
                    return false;
                }
                break;
            case ComparisonOperator.Lte:
            case ComparisonOperator.Lt:
                if (release <= trigger)
                {
                    errorMessage = $"解除线必须大于触发阈值 {trigger:0.##}（当前填的是 {release:0.##}），否则条件一旦满足就再也无法重新触发";
                    return false;
                }
                break;
            default:
                errorMessage = "解除线只支持 ≥ > ≤ < 这几种比较方式";
                return false;
        }

        return true;
    }

    private static float ParseSingle(string parameter)
    {
        // 解析失败返回 NaN：所有比较分支与 NaN 比较均为 false（安全失败），
        // 避免坏参数变成阈值 0 导致规则恒满足
        return float.TryParse(parameter, out var value) ? value : float.NaN;
    }
}
