using System.Diagnostics;
using EWeLinkLinker.Core.Logging;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 优化的触发器基类 - 统一轮询管理，减少资源占用
/// </summary>
public abstract class OptimizedTriggerBase : ITrigger
{
    private TriggerState _state = TriggerState.Idle;
    private bool _disposed;
    private CancellationTokenSource? _resetCts;

    /// <summary>
    /// 验证参数是否有效
    /// </summary>
    /// <param name="parameter">参数字符串</param>
    /// <param name="errorMessage">错误信息输出</param>
    /// <returns>是否有效</returns>
    public virtual bool ValidateParameter(string parameter, out string? errorMessage)
    {
        errorMessage = null;
        return true;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public abstract string Type { get; }
    public abstract string DisplayName { get; }

    public TriggerState State
    {
        get => _state;
        protected set
        {
            if (_state != value)
            {
                var oldState = _state;
                _state = value;
                StateChanged?.Invoke(this, new TriggerStateChangedEventArgs { OldState = oldState, NewState = value });
            }
        }
    }

    public event EventHandler<TriggerStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 统一的轮询间隔（由 TriggerManager 控制）
    /// </summary>
    protected virtual TimeSpan PollingInterval => TimeSpan.FromSeconds(10);

    /// <summary>
    /// 子类实现具体逻辑，评估当前条件是否满足
    /// </summary>
    protected abstract ValueTask<bool> EvaluateCoreAsync(CancellationToken ct);

    /// <summary>
    /// 触发后是否自动复位为 Monitoring。
    /// 边沿检测型触发器应返回 false，避免 OnPollingComplete 错过触发状态。
    /// </summary>
    protected virtual bool AutoReset => false;

    /// <summary>
    /// 轮询检查（测试按钮和服务端统一调用）
    /// </summary>
    public async Task<bool> PollAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;

        // H-12 修复：Error 状态下允许重试，不永久卡死
        if (State == TriggerState.Error)
        {
            State = TriggerState.Monitoring;
        }

        try
        {
            var wasMonitoring = State == TriggerState.Monitoring;
            var triggered = await EvaluateCoreAsync(ct);
            if (triggered && wasMonitoring)
            {
                State = TriggerState.Triggered;

                if (AutoReset)
                {
                    // H-1 修复：安全取消并复用 CTS
                    var oldCts = _resetCts;
                    var newCts = new CancellationTokenSource();
                    _resetCts = newCts;

                    // 取消旧任务
                    try { oldCts?.Cancel(); } catch { }
                    try { oldCts?.Dispose(); } catch { }

                    // 自动复位：等待一段时间后复位状态
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(1000, newCts.Token).ConfigureAwait(false);
                            if (!_disposed && State == TriggerState.Triggered)
                                State = TriggerState.Monitoring;
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception) { /* H-10 修复：不吞致命异常 */ }
                    });
                }
            }
            return triggered;
        }
        catch (Exception)
        {
            State = TriggerState.Error;
            return false;
        }
    }

    public virtual void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        State = TriggerState.Monitoring;
        OnStart();
    }

    public virtual void Stop()
    {
        if (_disposed) return;
        State = TriggerState.Idle;
        OnStop();
    }

    protected virtual void OnStart() { }
    protected virtual void OnStop() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 取消复位任务，防止访问已 Dispose 对象
        _resetCts?.Cancel();
        _resetCts?.Dispose();
        _resetCts = null;
        Stop();
        OnDispose();
        StateChanged = null; // 断开事件订阅链
        GC.SuppressFinalize(this);
    }

    protected virtual void OnDispose() { }

    protected bool IsDisposed => _disposed;

    /// <summary>
    /// 传感器缓存（由 PollingScheduler 在 Register 时注入）
    /// 触发器通过此属性获取缓存的传感器值，避免重复读取
    /// </summary>
    internal SensorCache? SensorCache { get; set; }

    /// <summary>
    /// 日志路径（由 TriggerManager 设置）
    /// </summary>
    private string? _logPath;

    /// <summary>
    /// 设置日志路径（ITrigger 接口实现）
    /// </summary>
    public void SetLogPath(string logPath)
    {
        _logPath = logPath;
        OnLogPathSet();
    }

    /// <summary>
    /// 子类可重写以响应日志路径设置
    /// </summary>
    protected virtual void OnLogPathSet() { }

    /// <summary>
    /// 记录日志 - C-3 修复：统一使用 SimpleLogger，避免静态 StreamWriter 跨触发器竞争
    /// </summary>
    protected void Log(TraceLevel level, string message)
    {
        // 检查全局日志开关和日志路径
        if (!LoggerConfig.IsEnabled || string.IsNullOrEmpty(_logPath)) return;

        var levelName = level switch
        {
            TraceLevel.Error => "ERROR",
            TraceLevel.Warning => "WARN",
            TraceLevel.Info => "INFO",
            _ => "DEBUG"
        };
        var logEntry = $"[{DateTime.Now:HH:mm:ss}] [{levelName}] [{GetType().Name}] {message}";
        SimpleLogger.Log(logEntry);
    }
}
