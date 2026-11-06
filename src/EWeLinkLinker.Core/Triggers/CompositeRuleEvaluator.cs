using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 复合条件的唯一实现：按 OR 分组、组内 AND、组间 OR（标准布尔优先级 AND &gt; OR）。
///
/// 原来这套分组算法在 RuleTrigger 和 LinkerService 各写了一遍（注释里互相承认"与另一处一致"），
/// 两处任何一方改判据都会悄悄漂移。轮询路径（RuleTrigger）和电源事件路径（LinkerService）
/// 现在共用这一份。
///
/// 判定输入是一张与 <see cref="LinkerRule.Conditions"/> 等长的布尔表：
/// - 轮询路径：电源条件永远为 false（它们不可轮询），其余取触发器当前状态；
/// - 事件路径：只有"与本次事件同名"的电源条件为 true，其它电源条件为 false，其余实时评估。
/// </summary>
internal static class CompositeRuleEvaluator
{
    internal static bool Evaluate(LinkerRule rule, IReadOnlyList<bool> conditionResults)
    {
        var count = conditionResults.Count;
        if (count == 0) return false;
        if (count == 1) return conditionResults[0];

        // 第 0 条的 Operator 被忽略：它表示"与前一组"的连接，而它前面没有组
        var groups = new List<List<int>> { new() { 0 } };
        for (var i = 1; i < count; i++)
        {
            if (rule.Conditions[i].Operator == LogicalOperator.Or)
                groups.Add(new List<int> { i });
            else
                groups[^1].Add(i);
        }

        foreach (var group in groups)
        {
            var all = true;
            foreach (var idx in group) all &= conditionResults[idx];
            if (all) return true;
        }

        return false;
    }
}
