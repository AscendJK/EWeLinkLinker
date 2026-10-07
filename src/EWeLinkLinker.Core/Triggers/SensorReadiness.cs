namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// "补读到真读数"这件事的唯一定义。启动播种和事件路径都要先读到真实传感器值再做判定，
/// 两处各写一套判定的话，一处修了另一处一定忘（启动边沿重发动作的 bug 就是这么来的）。
/// </summary>
internal static class SensorReadiness
{
    /// <summary>
    /// 最后一遍的判定结果与读数可得性，下标与传入的 triggers 一一对应。
    /// </summary>
    internal readonly struct Outcome
    {
        public Outcome(bool[] triggered, bool[] readingKnown)
        {
            Triggered = triggered;
            ReadingKnown = readingKnown;
        }

        public bool[] Triggered { get; }
        public bool[] ReadingKnown { get; }

        public IEnumerable<int> UnknownIndexes =>
            ReadingKnown.Select((known, i) => (known, i)).Where(x => !x.known).Select(x => x.i);
    }

    /// <summary>
    /// 逐个触发器 Start＋Poll 一遍；只要还有触发器没拿到真读数，就按 resetCache 清掉
    /// "读不到"的缓存、等 pause 再读一遍，最多 maxRounds 遍。
    /// 跑满预算仍读不到时不抛不卡：返回的结果里对应位置 readingKnown=false，由调用方决定怎么记账。
    /// </summary>
    /// <param name="resetCache">每遍开始前清缓存。播种必须清（上一遍的 NaN 还压在缓存里，不清等于没重读）；
    /// 触发器没挂缓存时传 null。</param>
    internal static async Task<Outcome> PollUntilReadingAsync(
        IReadOnlyList<OptimizedTriggerBase> triggers,
        int maxRounds,
        TimeSpan pause,
        Action? resetCache,
        CancellationToken ct = default)
    {
        var triggered = new bool[triggers.Count];
        var readingKnown = new bool[triggers.Count];

        for (var round = 1; ; round++)
        {
            resetCache?.Invoke();

            for (int i = 0; i < triggers.Count; i++)
            {
                var trigger = triggers[i];
                // 必须先 Start 再 Poll：PollAsync 里靠 "State == Monitoring" 决定要不要把满足写成
                // Triggered，Idle 状态下读到的值不进状态 ⇒ 基线永远是"不满足"，第一轮真轮询就变成上升沿。
                trigger.Start();
                triggered[i] = await trigger.PollAsync(ct);
                readingKnown[i] = trigger.LastReadingAvailable;
            }

            if (round >= maxRounds || readingKnown.All(k => k))
                return new Outcome(triggered, readingKnown);

            // 这里不接 ct：预算最多几秒，取消时抛出来的异常会把调用方的判定路径变成"未定义"，
            // 不如跑完预算按"读数未知"交回调用方记账。
            await Task.Delay(pause);
        }
    }
}
