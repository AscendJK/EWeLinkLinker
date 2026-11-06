using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using EWeLinkLinker.Core.Services;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 规则触发器 - 处理复合条件（AND/OR）
///
/// 工作原理：
/// - 每个子触发器自己管理状态（是否自动复位）
/// - 轮询完成后，检查所有子触发器的当前状态
/// - 根据 AND/OR 逻辑决定是否触发规则
/// - 不需要额外的 _hasFired 标志，触发器状态本身就是防重复的守卫
/// </summary>
public sealed class RuleTrigger : IDisposable, IPostPollCallback
{
    private readonly LinkerRule _rule;
    /// <summary>
    /// 执行动作的委托。生产路径就是 LinkerService.ExecuteRuleAsync；
    /// 抽成委托是为了让判定/派发逻辑能在没有设备、没有配置文件的测试里被驱动。
    /// </summary>
    private readonly Func<LinkerRule, CancellationToken, Task> _executeActions;
    /// <summary>记 "!! 规则触发" 的委托，同样是为了可测（见 §A1：这条日志抛异常不该把 _actionInFlight 锁死）。</summary>
    private readonly Action<string, string> _logTriggered;
    private readonly ServiceLogger _logger;
    private readonly PollingScheduler _scheduler;
    private readonly List<OptimizedTriggerBase> _conditionTriggers = new();

    /// <summary>
    /// <see cref="_conditionTriggers"/> 里第 k 个监控器对应 <c>_rule.Conditions</c> 的哪个下标。
    /// 电源事件条件（boot/shutdown/sleep/wake）不可轮询，不进这个列表——所以两者不再能按下标直接对齐。
    /// </summary>
    private readonly List<int> _conditionIndexes = new();
    private readonly object _evalLock = new();  // H-17 修复：保护 _previousCompositeResult 读写
    private bool _disposed;
    private bool _previousCompositeResult; // 防重复触发

    /// <summary>
    /// 规则级"上次发出动作"的时刻。最长粘住计时必须挂在规则上而不是挂在每个条件上：
    /// 每个条件各一个钟时，一条 AND 规则每满一次计时就会被每个条件各逼重发一次
    /// （生产实测规则 3 两个条件 ⇒ 每小时约 4 次，而不是口径说的每 30 分钟一次）。
    /// </summary>
    private DateTime? _actionStampedUtc;

    /// <summary>强制重判要两拍：这一拍记下降沿，下一拍才允许升回去发动作。</summary>
    private bool _reArmPending;

    /// <summary>
    /// 纠偏重发周期（来自规则配置，0 = 关闭）。关闭时不重发——见 LinkerRule.ReconcileMinutes。
    /// </summary>
    private readonly TimeSpan _reconcileHold;

    /// <summary>测试用：把纠偏周期压到 0，不用真的等。</summary>
    internal TimeSpan? ReconcileHoldOverride { get; set; }

    /// <summary>当前生效的纠偏周期（0 = 不纠偏）。internal 供测试确认默认值。</summary>
    internal TimeSpan ReconcileHold => ReconcileHoldOverride ?? _reconcileHold;

    /// <summary>
    /// 这条规则当前是否有一批动作正在执行（0/1）。上一批还没跑完就再发一批，
    /// 两条执行队列会交错写同一个插座，后发的那批可能把前一批刚设好的状态改回去。
    /// </summary>
    private int _actionInFlight;

    public RuleTrigger(LinkerRule rule, LinkerService linkerService, ServiceLogger logger, PollingScheduler scheduler)
        : this(rule, logger, scheduler,
               (r, ct) => linkerService.ExecuteRuleAsync(r, ct),
               logger.LogRuleTriggered,
               presets: null)
    {
    }

    /// <summary>
    /// 测试用构造：条件触发器由调用方直接给（生产路径走 InitializeAsync 从 TriggerRegistry 建）。
    /// 没有这个缝，判定/派发逻辑就只能靠真传感器驱动，等于不可测。
    /// </summary>
    internal RuleTrigger(LinkerRule rule, ServiceLogger logger, PollingScheduler scheduler,
                         Func<LinkerRule, CancellationToken, Task> executeActions,
                         Action<string, string> logTriggered,
                         IReadOnlyList<OptimizedTriggerBase>? presets)
    {
        _rule = rule;
        _logger = logger;
        _scheduler = scheduler;
        _executeActions = executeActions;
        _logTriggered = logTriggered;
        _reconcileHold = TimeSpan.FromMinutes(Math.Max(0, rule.ReconcileMinutes));
        // 可轮询条件的位置表：电源事件条件被排除在外
        for (var i = 0; i < rule.Conditions.Count; i++)
        {
            if (!TriggerManager.IsPowerCondition(rule.Conditions[i].Type)) _conditionIndexes.Add(i);
        }

        if (presets != null)
        {
            if (presets.Count != _conditionIndexes.Count)
                throw new ArgumentException(
                    $"预置条件触发器数量 ({presets.Count}) 与可轮询条件数 ({_conditionIndexes.Count}) 不一致", nameof(presets));
            _conditionTriggers.AddRange(presets);
        }
    }

    public async Task InitializeAsync()
    {
        // 只为"可轮询"的条件创建监控器：电源事件条件由 LinkerWindowsService 的系统事件路径处理，
        // 在这里建会直接抛 "Unknown trigger type: boot"。
        for (var index = 0; index < _rule.Conditions.Count; index++)
        {
            var condition = _rule.Conditions[index];
            if (TriggerManager.IsPowerCondition(condition.Type)) continue;

            var config = new TriggerConfig
            {
                Type = condition.Type,
                Parameter = condition.Parameter,
                Parameter2 = condition.Parameter2,
                ReleaseBand = condition.ReleaseBand,
                Comparison = condition.Comparison,
                // 节拍型触发器（interval）靠它把上一次敲钟接回来，否则热重载会清零倒计时
                Key = condition.Id
            };
            var trigger = TriggerRegistry.Create(config);
            _conditionTriggers.Add(trigger);
            _conditionIndexes.Add(index);
        }

        _logger.Info($"规则 [{_rule.Name}] 初始化完成: {_conditionTriggers.Count} 个条件监控器" +
                     (_conditionTriggers.Count < _rule.Conditions.Count
                         ? $"（另有 {_rule.Conditions.Count - _conditionTriggers.Count} 个电源事件条件，由系统事件路径处理）"
                         : ""));
    }

    public IReadOnlyList<OptimizedTriggerBase> GetTriggers() => _conditionTriggers.AsReadOnly();

    /// <summary>这条规则当前是否有一批动作还在跑（供服务停止前等一等）。</summary>
    public bool ActionInFlight => Volatile.Read(ref _actionInFlight) != 0;

    /// <summary>
    /// 轮询完成后评估复合条件（由 PollingScheduler 调用）
    /// H-17 修复：使用 lock 保护 _previousCompositeResult 读写，防止并发竞态
    /// </summary>
    public void OnPollingComplete()
    {
        if (_disposed || !_rule.Enabled) return;

        // H-17 修复：lock 保护边沿检测逻辑（复合判定也搬进锁里：判定与边沿记账必须是一个原子动作）
        lock (_evalLock)
        {
            var nowUtc = DateTime.UtcNow;
            var currentResult = EvaluateCompositeCondition();

            if (_reArmPending)
            {
                // 上一轮已经按"不满足"记过一笔下降沿，这一轮正常判定：条件还锁着 ⇒ 立刻变成上升沿 ⇒ 重发
                _reArmPending = false;
            }
            else if (currentResult && _previousCompositeResult
                     && ComparisonHelper.IsHoldExpired(_actionStampedUtc, nowUtc, ReconcileHold))
            {
                // 这一段只在规则显式配了 ReconcileMinutes > 0 时才可能进入（默认 0 = 不纠偏）。
                // 以前这里是写死的 30 分钟：条件只要持续满足，每半小时就把整套动作重发一遍，
                // 现场实测一条恒真规则一天多发 55 次命令——用户看到的是"温度没变它也在动"。
                //
                // 条件里必须有 _previousCompositeResult：纠偏管的是"一直满足、计时已经满了"这一种情况。
                // 少了这半句，规则解除后过了很久又满足时，那个真实的上升沿也会被当成"该纠偏了"按成不满足，
                // 动作白等一个轮询周期，日志还写着"已连续满足满 30 分钟"——与事实相反。
                // 本轮当作"不满足"造一个下降沿；条件还锁着，所以下一轮重新变成满足 ⇒ 一次上升沿 ⇒
                // 整套动作重发。计时挂在规则上，不是每个条件各挂一个（那样一条 AND 规则会被乘倍）。
                // 注意必须两拍：单靠"本轮判 false"不会刷新盖章，下一轮"计时已过期"仍然成立，
                // 于是每一轮都被按成不满足，永远等不到上升沿，动作一次都发不出去。
                _reArmPending = true;
                _logger.Info($"规则 [{_rule.Name}] 已连续满足满 {ReconcileHold}，本轮强制重判（纠偏：设备可能被人手动改过）");
                currentResult = false;
            }

            var conditionResults = CurrentStates();
            var stateParts = new List<string>(_rule.Conditions.Count);
            for (var i = 0; i < _rule.Conditions.Count; i++)
            {
                stateParts.Add(TriggerManager.IsPowerCondition(_rule.Conditions[i].Type)
                    ? $"{_rule.Conditions[i].Type}=事件型"
                    : $"{_rule.Conditions[i].Type}={_conditionTriggers[_conditionIndexes.IndexOf(i)].State}");
            }
            var states = string.Join(", ", stateParts);
            _logger.Info($"[RuleTrigger:{_rule.Name}] {states} => {(currentResult ? "满足" : "不满足")}");
            // 边沿检测：只在从"不满足"变为"满足"时触发
            if (currentResult && !_previousCompositeResult)
            {
                var reason = string.Join(", ", _rule.Conditions.Select((c, i) =>
                    $"{c.Type}={c.Parameter}({(conditionResults[i] ? "满足" : "不满足")})"));

                // 上一批动作还在跑就再发一批：两条队列会交错写同一个插座，后发的那批
                // 可能把前一批刚设好的状态改回去（口径 A：跳过这一发并记日志）。
                // 这一发不会补回来——最长粘住计时还没盖章，最多 30 分钟的强制重判会再造一次上升沿。
                if (Interlocked.CompareExchange(ref _actionInFlight, 1, 0) != 0)
                {
                    _logger.Warn($"规则 [{_rule.Name}] 已有动作在飞，本次跳过（条件: {reason}）");
                }
                else
                {
                    // 归还 _actionInFlight 的责任必须留在这一层：原来的 try/finally 写在 Task.Run 内部，
                    // 只要在起任务之前抛了异常（日志、调度器拒绝排队……），finally 就永远不执行，
                    // 闸门恒为 1 ⇒ 这条规则此后每轮只打"已有动作在飞，本次跳过"，再不动作。
                    var dispatched = false;
                    try
                    {
                        _logTriggered(_rule.Name, reason);

                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await _executeActions(_rule, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"规则 [{_rule.Name}] 执行失败", ex);
                            }
                            finally
                            {
                                Interlocked.Exchange(ref _actionInFlight, 0);
                            }
                        });

                        _actionStampedUtc = nowUtc;
                        dispatched = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"规则 [{_rule.Name}] 派发动作失败（本拍作废，下一拍重试）", ex);
                    }
                    finally
                    {
                        if (!dispatched) Interlocked.Exchange(ref _actionInFlight, 0);
                    }

                    if (!dispatched)
                    {
                        // 没派出去就不算"这一拍满足过"：否则要等到条件解除再满足才会重试，
                        // 而持续满足的场景根本等不到那一下。
                        currentResult = false;
                    }
                    else
                    {
                        // 事件型条件（app 启动/关闭、到点）的脉冲只管一次：动作已经发出去了，
                        // 留着会让同一个事件在后面的轮次里再触发一遍。
                        // 派发失败/已有动作在飞时**不**消费，下一拍还能拿同一个事件重试。
                        foreach (var condition in _conditionTriggers)
                        {
                            if (condition.IsPulseCondition) condition.ConsumePulse();
                        }
                    }
                }
            }

            _previousCompositeResult = currentResult;
        }
    }

    /// <summary>
    /// 建立启动基线：把当前复合结果记为「已经有过」的状态，使服务重启或触发器重建后，
    /// 原本就已满足的规则不会在第一轮被当成新跳变而重发设备动作。
    /// 由 TriggerManager 在启动调度器之前调用，此时 OnPollingComplete 还没跑过。
    /// </summary>
    public void SeedBaseline()
    {
        lock (_evalLock)
        {
            _previousCompositeResult = EvaluateCompositeCondition();
            if (_previousCompositeResult)
            {
                // 基线也起表：启动时就已满足的规则，第一次纠偏同样要等满一个计时周期，
                // 而不是服务一起来就补发一次
                _actionStampedUtc = DateTime.UtcNow;
                _logger.Info($"规则 [{_rule.Name}] 启动时条件已满足，按基线处理（本次不执行动作）");
            }
        }
    }

    /// <summary>
    /// 当前每个条件的判定结果（下标与 <c>_rule.Conditions</c> 对齐）。
    /// 电源事件条件不可轮询，在轮询路径里恒为 false —— 这正是 `boot OR app_start` 这类规则
    /// 的 OR 分支能被正常评估、而 `boot AND xxx` 不会被误判成立的原因。
    /// </summary>
    private bool[] CurrentStates()
    {
        var results = new bool[_rule.Conditions.Count];
        for (var k = 0; k < _conditionTriggers.Count; k++)
        {
            results[_conditionIndexes[k]] = _conditionTriggers[k].State == TriggerState.Triggered;
        }
        return results;
    }

    /// <summary>
    /// 评估复合条件（返回当前是否满足）。分组算法在 <see cref="CompositeRuleEvaluator"/>，
    /// 与电源事件路径共用同一份实现。
    /// </summary>
    private bool EvaluateCompositeCondition() => CompositeRuleEvaluator.Evaluate(_rule, CurrentStates());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 从调度器注销回调，防止内存泄漏
        _scheduler.UnregisterPostPollCallback(this);

        foreach (var trigger in _conditionTriggers)
        {
            try { trigger.Stop(); } catch { }
            try { trigger.Dispose(); } catch { }
        }
        _conditionTriggers.Clear();
    }
}
