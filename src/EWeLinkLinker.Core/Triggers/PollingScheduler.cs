using EWeLinkLinker.Core.Logging;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 轮询后回调接口 - 允许触发器在每次轮询后执行逻辑
/// </summary>
public interface IPostPollCallback
{
    /// <summary>
    /// 所有触发器轮询完成后调用
    /// </summary>
    void OnPollingComplete();
}

/// <summary>
/// 轮询调度器 - 统一管理所有触发器的轮询，减少线程占用
/// </summary>
public sealed class PollingScheduler : IAsyncDisposable
{
    private readonly Dictionary<string, OptimizedTriggerBase> _triggers = new();
    private readonly List<IPostPollCallback> _postPollCallbacks = new();
    private readonly TimeSpan _baseInterval;
    private Timer? _timer;
    private bool _isRunning;
    private bool _disposed;
    private readonly object _lock = new();
    private readonly ServiceLogger _logger;
    private SemaphoreSlim? _semaphore;

    public PollingScheduler(ServiceLogger logger, TimeSpan? baseInterval = null)
    {
        _logger = logger;
        _baseInterval = baseInterval ?? TimeSpan.FromSeconds(10);
    }

    public void Register(OptimizedTriggerBase trigger)
    {
        lock (_lock)
        {
            _triggers[trigger.Id] = trigger;
            // 如果已经在运行，立即启动新注册的触发器
            if (_isRunning)
            {
                try { trigger.Start(); } catch { }
            }
        }
        EnsureTimer();
    }

    /// <summary>
    /// 注册轮询后回调
    /// </summary>
    public void RegisterPostPollCallback(IPostPollCallback callback)
    {
        lock (_lock)
        {
            if (!_postPollCallbacks.Contains(callback))
            {
                _postPollCallbacks.Add(callback);
            }
        }
    }

    /// <summary>
    /// 注销轮询后回调（防止内存泄漏）
    /// </summary>
    public void UnregisterPostPollCallback(IPostPollCallback callback)
    {
        lock (_lock)
        {
            _postPollCallbacks.Remove(callback);
        }
    }

    public void Unregister(string triggerId)
    {
        lock (_lock)
        {
            _triggers.Remove(triggerId);
        }
    }

    public void Start()
    {
        if (_isRunning || _disposed) return;

        lock (_lock)
        {
            if (_isRunning) return;

            // 启动所有触发器（设置状态为 Monitoring）
            foreach (var trigger in _triggers.Values)
            {
                try { trigger.Start(); } catch { }
            }

            _isRunning = true;
            _timer = new Timer(PollAllSafe, null, TimeSpan.Zero, _baseInterval);
        }

        _logger.Info($"轮询调度器启动，间隔: {_baseInterval.TotalSeconds}秒，监控 {_triggers.Count} 个条件");
    }

    public void Stop()
    {
        if (!_isRunning) return;

        lock (_lock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _timer?.Dispose();
            _timer = null;
        }

        _logger.Info("轮询调度器已停止");
    }

    private void EnsureTimer()
    {
        if (_isRunning || _disposed) return;
        Start();
    }

    /// <summary>
    /// 安全的轮询入口，捕获所有异常防止进程崩溃
    /// </summary>
    private void PollAllSafe(object? state)
    {
        try
        {
            _ = PollAllAsync();
        }
        catch (Exception ex)
        {
            _logger.Error("轮询调度器未捕获异常", ex);
        }
    }

    private async Task PollAllAsync()
    {
        if (_disposed) return;

        // 使用局部变量避免闭包捕获字段
        Dictionary<string, OptimizedTriggerBase> triggers;
        List<IPostPollCallback> callbacks;
        lock (_lock)
        {
            if (!_isRunning) return;
            triggers = new Dictionary<string, OptimizedTriggerBase>(_triggers);
            callbacks = new List<IPostPollCallback>(_postPollCallbacks);
        }

        if (triggers.Count == 0) return;

        // 并发轮询所有触发器，但限制并发数（复用 semaphore 减少 GC）
        _semaphore ??= new SemaphoreSlim(4, 4);
        var tasks = triggers.Select(async kvp =>
        {
            await _semaphore.WaitAsync();
            try
            {
                // 直接轮询，触发器内部通过 _wasTriggered 防止重复触发
                var triggered = await kvp.Value.PollAsync(CancellationToken.None);
                if (triggered)
                {
                    _logger.Info($"✓ 条件触发: {kvp.Value.DisplayName} ({kvp.Value.Id})");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"轮询错误 {kvp.Key}", ex);
            }
            finally
            {
                _semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // 轮询完成后调用回调，让组合触发器可以评估复合条件
        foreach (var callback in callbacks)
        {
            try
            {
                callback.OnPollingComplete();
            }
            catch (Exception ex)
            {
                _logger.Error("轮询后回调异常", ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _triggers.Clear();
        _postPollCallbacks.Clear();
        _semaphore?.Dispose();
        _semaphore = null;
    }
}
