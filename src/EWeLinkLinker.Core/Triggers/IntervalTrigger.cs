using System.Diagnostics;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 间隔执行触发器
/// 参数格式: 分钟数，如 "30" 表示每30分钟
/// </summary>
[Trigger("interval", "间隔执行", "每隔 N 分钟触发一次")]
public class IntervalTrigger : OptimizedTriggerBase
{
    /// <summary>
    /// 文档（README 与界面提示）承诺的范围是 1–1440 分钟。以前只校验了 "&gt;= 1"，
    /// 于是 `interval 100000` 能存进去、看着在跑，实际等于"永远不触发"。
    /// </summary>
    internal const int MaxIntervalMinutes = 1440;

    private readonly int _intervalMinutes;
    private readonly string _tickKey;
    private DateTime _lastTriggered;
    private bool _wasTriggered;

    public override string Type => "interval";
    public override string DisplayName => "间隔执行";

    /// <summary>节拍键（条件 Id|间隔）。测试用来确认 RuleTrigger 真的把条件下发下来了。</summary>
    internal string TickKey => _tickKey;

    protected override TimeSpan PollingInterval => TimeSpan.FromMinutes(1);

    public IntervalTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);
        // 不接系统区域：同一个 "1,5" 在中文区域会被 int.Parse 当成 15，间隔直接差十倍
        if (!ComparisonHelper.TryParseInt(config.Parameter, out _intervalMinutes))
            throw new ArgumentException($"间隔必须是整数分钟（当前填的是 {config.Parameter}）{ComparisonHelper.NumberFormatHint(config.Parameter)}");

        // 节拍键：条件 Id + 间隔值。间隔值变了就不沿用旧节拍（见 IntervalTickState）
        _tickKey = IntervalTickState.BuildKey(config.Key, config.Parameter);
        _lastTriggered = DateTime.MinValue;
    }

    protected override void OnStart()
    {
        // 续用上一次的节拍：配置一改就要重建触发器，若每次重建都从"现在"重新计时，
        // "每隔 30 分钟"实际是"每隔 30 分钟、且这期间没人改过配置"——而且完全看不出来。
        // 没有 Key 的（事件路径的临时触发器）退化为从 Start 时刻算起。
        _lastTriggered = IntervalTickState.Restore(_tickKey) ?? DateTime.Now;
        _wasTriggered = false;
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "间隔不能为空";
            return false;
        }
        if (!ComparisonHelper.TryParseInt(parameter, out var minutes) ||
            minutes < 1 || minutes > MaxIntervalMinutes)
        {
            errorMessage = $"间隔必须是 1–{MaxIntervalMinutes} 之间的整数分钟（当前填的是 {parameter}）{ComparisonHelper.NumberFormatHint(parameter)}";
            return false;
        }
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 节拍语义：每到点敲一下，这一拍只在本轮成立（不挂脉冲窗口）。
    /// 所以 `interval 30 AND cpu_temp>80` 读作"每 30 分钟看一眼温度，达标就执行"，
    /// 而不是"每 30 分钟里随便哪一刻温度高过就算"。
    /// </summary>
    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var elapsed = DateTime.Now - _lastTriggered;
        if (elapsed.TotalMinutes >= _intervalMinutes)
        {
            if (!_wasTriggered)
            {
                _wasTriggered = true;
                var now = DateTime.Now;
                _lastTriggered = now;
                IntervalTickState.Remember(_tickKey, now);
                // 记录自然时间间隔语义（含睡眠/待机时间）：
                // 睡眠期间轮询停止，唤醒后若已超时会立即触发一次
                Log(TraceLevel.Info,
                    $"间隔触发: 距上次 {elapsed.TotalMinutes:F1} 分钟（自然时间，含睡眠），间隔 {_intervalMinutes} 分钟");
                return ValueTask.FromResult(true);
            }
            return ValueTask.FromResult(false);
        }

        // 条件不满足，复位锁存和状态
        if (_wasTriggered)
        {
            _wasTriggered = false;
            State = TriggerState.Monitoring;
        }
        return ValueTask.FromResult(false);
    }
}
