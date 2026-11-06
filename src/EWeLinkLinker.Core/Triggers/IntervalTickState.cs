using System.Collections.Concurrent;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 节拍型触发器（目前只有 interval）的"上一次敲钟时刻"。
///
/// 为什么需要它：配置一改，TriggerManager.ReloadAsync 会把所有触发器重新 new 一遍，
/// 而 IntervalTrigger 原来在 OnStart 里把基准时间设为"现在"——于是"每隔 30 分钟"实际变成
/// "每隔 30 分钟、且这期间没人动过配置"。改一条不相干的规则就把所有间隔规则的节拍清零，
/// 而且没有任何日志能看出来。
///
/// 范围刻意限定在进程内：服务重启后按"现在"重新计时是合理的（没人希望重启后立刻补发一轮
/// 几小时前的定时），跨重启持久化反而是另一种意外。
/// </summary>
internal static class IntervalTickState
{
    private static readonly ConcurrentDictionary<string, DateTime> Ticks = new(StringComparer.Ordinal);

    /// <summary>超过这个数量就清理太旧的条目，避免反复改配置把字典撑大。</summary>
    private const int MaxEntries = 256;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// 键 = 条件 Id + 间隔值。间隔值变了就不沿用旧节拍——
    /// 把 30 分钟改成 5 分钟时，用户期待的是"从现在开始每 5 分钟"，不是"立刻补一发"。
    /// </summary>
    internal static string BuildKey(string? conditionId, string? parameter) =>
        string.IsNullOrEmpty(conditionId) ? "" : $"{conditionId}|{parameter}";

    internal static DateTime? Restore(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        return Ticks.TryGetValue(key, out var at) ? at : null;
    }

    internal static void Remember(string? key, DateTime at)
    {
        if (string.IsNullOrEmpty(key)) return;
        Ticks[key] = at;
        if (Ticks.Count > MaxEntries) Prune();
    }

    private static void Prune()
    {
        var cutoff = DateTime.Now - MaxAge;
        foreach (var kv in Ticks)
            if (kv.Value < cutoff) Ticks.TryRemove(kv.Key, out _);
    }

    /// <summary>仅测试使用：清掉所有节拍，避免用例之间互相影响。</summary>
    internal static void Reset() => Ticks.Clear();
}
