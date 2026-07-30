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
    private readonly SemaphoreSlim _semaphore;  // C-1 修复：构造函数初始化，避免竞态
    private int _isPolling;  // H-4 修复：防止重叠执行
    private Task? _currentPollTask;  // 当前正在执行的轮询任务（Dispose 时等待）
    private readonly SensorCache _sensorCache = new();  // 传感器缓存，每轮清空

    public PollingScheduler(ServiceLogger logger, TimeSpan? baseInterval = null)
    {
        _logger = logger;
        _baseInterval = baseInterval ?? TimeSpan.FromSeconds(10);
        _semaphore = new SemaphoreSlim(4, 4);
    }

    public void Register(OptimizedTriggerBase trigger)
    {
        lock (_lock)
        {
            _triggers[trigger.Id] = trigger;
            trigger.SensorCache = _sensorCache;  // 注入传感器缓存
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

    private Task PollAllAsync()
    {
        if (_disposed) return Task.CompletedTask;

        // H-4 修复：防止重叠执行。如果上次轮询还未完成，直接跳过本次
        if (Interlocked.CompareExchange(ref _isPolling, 1, 0) != 0)
        {
            return Task.CompletedTask;
        }

        // 启动轮询任务并保存引用（Dispose 时等待）
        var task = Task.Run(async () =>
        {
            try
            {
                await PollAllCoreAsync();
            }
            catch (Exception ex)
            {
                // 捕获轮询异常，防止任务崩溃导致后续轮询停止
                _logger.Error($"轮询异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _isPolling, 0);
            }
        });

        _currentPollTask = task;
        return task;
    }

    private async Task PollAllCoreAsync()
    {
        var pollStart = DateTime.UtcNow;

        // 每轮开始时清空传感器缓存（所有传感器只读一次）
        _sensorCache.Clear();

        // 在锁内复制引用到局部变量，避免闭包捕获字段
        // 注意：只复制引用（struct copy），不创建新集合，减少 GC 压力
        OptimizedTriggerBase[] triggerArray;
        IPostPollCallback[] callbackArray;
        lock (_lock)
        {
            if (!_isRunning) return;
            triggerArray = new OptimizedTriggerBase[_triggers.Count];
            _triggers.Values.CopyTo(triggerArray, 0);
            callbackArray = new IPostPollCallback[_postPollCallbacks.Count];
            _postPollCallbacks.CopyTo(callbackArray, 0);
        }

        // 统计本轮涉及的传感器类型（用于日志）
        var sensorTypes = new HashSet<string>();
        foreach (var trigger in triggerArray)
        {
            switch (trigger.Type)
            {
                case "cpu_temp": sensorTypes.Add("CPU温度"); break;
                case "cpu_usage": sensorTypes.Add("CPU使用率"); break;
                case "gpu_temp": sensorTypes.Add("GPU温度"); break;
                case "app_start":
                case "app_close":
                    sensorTypes.Add("进程"); break;
                case "time": sensorTypes.Add("时间"); break;
                case "interval": sensorTypes.Add("间隔"); break;
            }
        }
        string sensors = sensorTypes.Count > 0 ? string.Join(", ", sensorTypes) : "无";
        _logger.Info($"[轮询] 触发器: {triggerArray.Length}, 传感器: [{sensors}]");

        if (triggerArray.Length == 0) return;

        // 并发轮询所有触发器，但限制并发数（复用 semaphore 减少 GC）
        var tasks = new Task[triggerArray.Length];
        for (int i = 0; i < triggerArray.Length; i++)
        {
            var trigger = triggerArray[i];  // 局部变量避免闭包捕获
            tasks[i] = Task.Run(async () =>
            {
                await _semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    // 直接轮询，触发器内部通过 _wasTriggered 防止重复触发
                    var triggered = await trigger.PollAsync(CancellationToken.None);
                    if (triggered)
                    {
                        _logger.Info($"✓ 条件触发: {trigger.DisplayName} ({trigger.Id})");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"轮询错误 {trigger.Id}", ex);
                }
                finally
                {
                    _semaphore.Release();
                }
            });
        }

        await Task.WhenAll(tasks);

        // 轮询完成后调用回调，让组合触发器可以评估复合条件
        foreach (var callback in callbackArray)
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

        // 性能监控：轮询耗时过长时记录警告
        var elapsed = DateTime.UtcNow - pollStart;
        if (elapsed > TimeSpan.FromSeconds(3))
        {
            _logger.Warn($"轮询周期耗时过长: {elapsed.TotalMilliseconds:F0}ms, 触发器数量: {triggerArray.Length}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();

        // C-2 修复：等待当前轮询完成，避免释放正在使用的资源
        if (_currentPollTask != null)
        {
            try { await _currentPollTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* 超时或异常继续释放 */ }
        }

        // C-3 修复：先清空缓存（释放 Process[] 等），再释放静态硬件句柄
        _sensorCache.Clear();
        GpuTempTrigger.StaticDispose();
        CpuUsageTrigger.StaticDispose();
        _triggers.Clear();
        _postPollCallbacks.Clear();
        _semaphore.Dispose();
        _currentPollTask = null;
    }
}
