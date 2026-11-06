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
    private int _errorRounds;

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
    /// 上一次轮询是否真的拿到了传感器读数。读不到（NaN）时子类置 false，
    /// 让启动播种知道"这个条件现在还没定论"，要换个采样窗口再读一次，
    /// 而不是把"未知"当成"不满足"建基线。非传感器型触发器恒为 true。
    /// </summary>
    internal bool LastReadingAvailable { get; set; } = true;

    // ─── 事件型条件的脉冲待决（A3） ─────────────────────────────────────
    //
    // app_start / app_close / 到点这类条件是"事件"，不是"持续状态"。原来它们只在自己被发现的那
    // 一个轮询周期里把 State 置成 Triggered，下一拍立刻回 Monitoring；而 RuleTrigger 的复合判定
    // 读的是当前 State，于是 `app_start AND cpu_temp>=80` 要求两者落在同一拍（3–5 秒内）——
    // 起游戏后温度要几十秒才爬上去，这个条件几乎永不成立，还没有任何日志说"我丢了一个事件"。
    //
    // 现在事件产生一个"待决脉冲"：Triggered 保持到一个窗口结束，或者被规则消费掉为止。

    private DateTime? _pulseSinceUtc;

    /// <summary>
    /// 待决脉冲最多保持多久。0 = 不是事件型条件（数值型靠滞回自己管锁存）。
    /// </summary>
    protected virtual TimeSpan PulseWindow => TimeSpan.Zero;

    /// <summary>这条触发器是不是事件型（只看窗口是否启用）。</summary>
    internal bool IsPulseCondition => PulseWindow > TimeSpan.Zero;

    /// <summary>当前是否存在一个既没被规则消费、也没过期的事件。</summary>
    protected bool PulsePending =>
        _pulseSinceUtc.HasValue && DateTime.UtcNow - _pulseSinceUtc.Value < PulseWindow;

    /// <summary>记一次事件（子类在"这一轮真的发现了事件"时调用，并同时返回 true）。</summary>
    protected void RaisePulse() => _pulseSinceUtc = DateTime.UtcNow;

    /// <summary>
    /// 规则把动作派发出去之后调用：同一个事件不能触发第二次。
    /// 置空之后，下一次轮询发现 PulsePending 为 false，基类会把状态放回 Monitoring。
    /// </summary>
    internal void ConsumePulse() => _pulseSinceUtc = null;

    // 没有"每个触发器自己的节奏"这回事：PollingScheduler 只用**一个**定时器，按全局
    // pollingIntervalSeconds（配置里那个 1–30 秒的下拉）把所有触发器统一刷一遍。
    // 这里曾经有 7 个 `protected override TimeSpan PollingInterval`（app 3s / 温度 10s /
    // 间隔 1min …），全是没人读的死属性，唯一真正读它的是 TimeTrigger 的每日窗口。
    // 要做独立节奏得改调度器按"下次到期时间"排程，而不是加回一个没人读的属性。

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
            // 每轮先按"这一轮拿到了读数"重置，只有真正读到 NaN 的子类才把它置回 false。
            // 不在入口重置的话，time / interval / app_start / app_close 这几种从不写这个标志的
            // 触发器只要抛过一次异常就永久停在"读数未知"：此后每次启动播种都把它们当没定论，
            // 白跑满 3 遍 ×1.2s，还写一条"跑了 3 遍仍没读到值"的误导性 WARN。
            LastReadingAvailable = true;

            var wasMonitoring = State == TriggerState.Monitoring;
            var triggered = await EvaluateCoreAsync(ct);
            _errorRounds = 0;
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

            // 事件型条件（app 启动/关闭、到点）：事件发生后 Triggered 不跟着"这一轮满不满足"走，
            // 而是挂到脉冲上——待决期间保持 Triggered，让复合条件里其它轮询条件有机会在这几轮里赶上；
            // 被规则消费掉或超出窗口后由这里统一放开。子类只管 RaisePulse()，不用各自写复位。
            if (IsPulseCondition && State == TriggerState.Triggered && !PulsePending)
            {
                State = TriggerState.Monitoring;
            }

            return triggered;
        }
        catch (Exception ex)
        {
            // 抛异常 = 这一轮没有定论，不等于"条件不再满足"。原来这里无条件打成 Error：
            // 已经锁存的条件被打回去之后，子类里的 _wasTriggered 还是 true，规则这边再也等不到
            // 新的上升沿 —— currentResult 长期为 false，而 30 分钟的强制重判要求 currentResult 为真
            // 才进得去，于是纠偏静默失效，界面上还显示"不满足"。所以锁住的继续锁住，
            // 只记下"这轮没读到值"（播种会为此再补读一轮）。
            LastReadingAvailable = false;
            _errorRounds++;
            var keptLatch = State == TriggerState.Triggered;
            if (!keptLatch) State = TriggerState.Error;
            if (_errorRounds == 1 || _errorRounds % 30 == 0)
                Log(TraceLevel.Warning,
                    $"第 {_errorRounds} 轮评估抛异常（{(keptLatch ? "保持锁存，不改判" : "转 Error，下一轮重试")}）：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public virtual void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // 已经是 Triggered 就不要打回 Monitoring：调度器 Start() 会对每个触发器再调一次 Start()，
        // 那会把播种刚测出来的"已满足"清掉，于是第一轮真轮询被当成新上升沿，
        // 服务一起来就把本来已经满足的规则重发一遍动作。
        if (State != TriggerState.Triggered) State = TriggerState.Monitoring;
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
    /// 全局轮询间隔（由 PollingScheduler 在 Register 时注入）。
    /// 用于需要感知轮询频率的窗口计算（如 TimeTrigger 的每日触发窗口）。
    /// </summary>
    internal TimeSpan? GlobalPollingInterval { get; set; }

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
