namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 智能轮询器 - 根据状态动态调整轮询间隔
/// 优化：空闲时降低频率，触发时提高频率，错误时退避
/// </summary>
public sealed class SmartPoller : IDisposable
{
    private readonly TimeSpan _idleInterval;
    private readonly TimeSpan _activeInterval;
    private readonly TimeSpan _errorInterval;
    private Timer? _timer;
    private Func<CancellationToken, ValueTask<bool>>? _checkAction;
    private CancellationToken _ct;
    private bool _isTriggered;
    private bool _disposed;

    /// <summary>
    /// 创建智能轮询器
    /// </summary>
    /// <param name="idleInterval">空闲间隔（默认轮询频率）</param>
    /// <param name="activeInterval">活跃间隔（触发后的轮询频率）</param>
    /// <param name="errorInterval">错误间隔（发生错误时的退避频率）</param>
    public SmartPoller(TimeSpan idleInterval, TimeSpan activeInterval, TimeSpan? errorInterval = null)
    {
        _idleInterval = idleInterval;
        _activeInterval = activeInterval;
        _errorInterval = errorInterval ?? idleInterval;
    }

    /// <summary>
    /// 开始轮询
    /// </summary>
    public void Start(Func<CancellationToken, ValueTask<bool>> checkAction, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        _checkAction = checkAction;
        _ct = ct;
        _isTriggered = false;

        _timer = new Timer(CheckCallbackSafe, null, TimeSpan.Zero, _idleInterval);
    }

    /// <summary>
    /// 停止轮询
    /// </summary>
    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _checkAction = null;
    }

    /// <summary>
    /// 安全的回调入口，防止 async void 异常崩溃
    /// </summary>
    private void CheckCallbackSafe(object? state)
    {
        try
        {
            _ = CheckCallbackAsync();
        }
        catch
        {
            // 不应该到达这里，但保险起见
        }
    }

    private async Task CheckCallbackAsync()
    {
        if (_disposed || _checkAction == null || _ct.IsCancellationRequested) return;

        try
        {
            // 取消上一个未完成的检查
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // 单次检查超时10秒

            var triggered = await _checkAction(cts.Token);
            _isTriggered = triggered;

            // 动态调整间隔
            var nextInterval = triggered ? _activeInterval : _idleInterval;
            _timer?.Change(nextInterval, nextInterval);
        }
        catch (OperationCanceledException)
        {
            // 超时或取消，使用错误间隔
            _timer?.Change(_errorInterval, _errorInterval);
        }
        catch
        {
            // 其他错误，使用错误间隔
            _timer?.Change(_errorInterval, _errorInterval);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 一次性定时器 - 用于延迟触发（如等待网络恢复）
/// </summary>
public sealed class DelayedAction : IDisposable
{
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// 延迟执行
    /// </summary>
    public void Schedule(TimeSpan delay, Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            try { action(); } catch { }
        }, null, delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 取消
    /// </summary>
    public void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        GC.SuppressFinalize(this);
    }
}
