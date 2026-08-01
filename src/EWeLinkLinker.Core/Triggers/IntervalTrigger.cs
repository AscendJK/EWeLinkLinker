using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 间隔执行触发器
/// 参数格式: 分钟数，如 "30" 表示每30分钟
/// </summary>
[Trigger("interval", "间隔执行", "每隔 N 分钟触发一次")]
public class IntervalTrigger : OptimizedTriggerBase
{
    private readonly int _intervalMinutes;
    private DateTime _lastTriggered;
    private bool _wasTriggered;

    public override string Type => "interval";
    public override string DisplayName => "间隔执行";

    protected override TimeSpan PollingInterval => TimeSpan.FromMinutes(1);

    public IntervalTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);
        _intervalMinutes = int.Parse(config.Parameter!);
        // H-18 修复：不在构造函数中设置 _lastTriggered，改为 OnStart 中设置
        // 这样首次触发时间从 Start 时刻算起，而非构造时刻
        _lastTriggered = DateTime.MinValue;
    }

    protected override void OnStart()
    {
        // H-18 修复：每次 Start 时重置计时器
        _lastTriggered = DateTime.Now;
        _wasTriggered = false;
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "间隔不能为空";
            return false;
        }
        if (!int.TryParse(parameter, out var minutes) || minutes < 1)
        {
            errorMessage = "间隔必须为大于等于 1 的整数（分钟）";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var elapsed = DateTime.Now - _lastTriggered;
        if (elapsed.TotalMinutes >= _intervalMinutes)
        {
            if (!_wasTriggered)
            {
                _wasTriggered = true;
                _lastTriggered = DateTime.Now;
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
