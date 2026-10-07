namespace EWeLinkLinker.Core.Config;

/// <summary>
/// 把"外面来了一件事，请做一次处理"这类请求汇成一条串行通道：
/// 同一时刻只有一个处理在跑，跑的过程中攒下的请求由同一个循环补做一次（合并，不排队叠加）。
/// 配置热重载用它：一次保存常连着发 Changed+Renamed+Created 三个事件，
/// 每个事件各起一次重载会互相踩客户端和触发器的拆装。
/// </summary>
public sealed class ReloadGate
{
    private readonly Func<Task> _work;
    private int _pending;
    private int _loop;

    /// <summary>当前是否有循环在跑（测试与诊断用）。</summary>
    public bool IsRunning => Volatile.Read(ref _loop) != 0;

    /// <summary>是否还欠一次处理（测试与诊断用）。</summary>
    public bool IsPending => Volatile.Read(ref _pending) != 0;

    public ReloadGate(Func<Task> work)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
    }

    /// <summary>
    /// 请求处理一次。返回的 Task 只保证"请求已被登记"：
    /// 抢到通道的那次调用会把待处理项一路做干净，其余调用直接返回。
    /// </summary>
    public async Task RequestAsync()
    {
        Volatile.Write(ref _pending, 1);
        while (true)
        {
            if (Interlocked.CompareExchange(ref _loop, 1, 0) != 0) return;   // 有人在跑，交给它补做
            try
            {
                while (Interlocked.Exchange(ref _pending, 0) != 0)
                {
                    await _work();
                }
            }
            finally { Volatile.Write(ref _loop, 0); }

            // 复位与"刚好在这之后塞了一次请求"之间仍有缝：再看一眼，有就重新抢
            if (Volatile.Read(ref _pending) == 0) return;
        }
    }
}
