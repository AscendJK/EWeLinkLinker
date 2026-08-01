using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 每天固定时间触发器
/// 参数格式: "HH:mm" 如 "08:10"
/// </summary>
[Trigger("time", "每天固定时间", "每天指定时间触发，格式 HH:mm")]
public class TimeTrigger : OptimizedTriggerBase
{
    private readonly TimeSpan _time;
    private readonly ComparisonOperator _comparison;
    private DateTime _lastTriggeredDate;
    private bool _wasTriggered;

    public override string Type => "time";
    public override string DisplayName => "每天固定时间";

    protected override TimeSpan PollingInterval => TimeSpan.FromSeconds(30);

    public TimeTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);

        _time = TimeSpan.Parse(config.Parameter!);
        _comparison = config.Comparison;
    }

    protected override void OnStart()
    {
        // H-? 修复：Stop/Start 或热重载后复位锁存状态，确保当天可再次触发
        _wasTriggered = false;
        _lastTriggeredDate = DateTime.MinValue;
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "时间不能为空";
            return false;
        }
        if (!TimeSpan.TryParse(parameter, out _))
        {
            errorMessage = "时间格式无效，应为 HH:mm，如 08:00";
            return false;
        }
        errorMessage = null;
        return true;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = now.Date;

        // 根据比较运算符判断
        var triggered = _comparison switch
        {
            ComparisonOperator.Eq => EqCheck(now, today),
            ComparisonOperator.Neq => NeqCheck(now, today),
            ComparisonOperator.Gte => GteCheck(now, today),
            ComparisonOperator.Lt => LtCheck(now, today),
            _ => false
        };

        // 边沿检测：从未满足变为满足时触发
        if (triggered && !_wasTriggered)
        {
            _wasTriggered = true;
            return ValueTask.FromResult(true);
        }

        // 条件不满足，复位锁存和状态
        if (!triggered && _wasTriggered)
        {
            _wasTriggered = false;
            State = TriggerState.Monitoring;
        }

        return ValueTask.FromResult(false);
    }

    private bool EqCheck(DateTime now, DateTime today)
    {
        // C-7 修复：使用"今日是否已触发" + "首次进入窗口时触发"
        // 扩大窗口到整个轮询间隔，避免边界错过
        if (_lastTriggeredDate == today) return false;
        var diff = now.TimeOfDay - _time;
        // 窗口：diff >= 0（已过目标时间）且 diff <= PollingInterval（首次进入窗口）
        var isMatch = diff >= TimeSpan.Zero && diff <= PollingInterval;
        if (isMatch) _lastTriggeredDate = today;
        return isMatch;
    }

    private bool NeqCheck(DateTime now, DateTime today)
    {
        if (_lastTriggeredDate == today) return false;
        var diff = now.TimeOfDay - _time;
        var isOutSide = diff > PollingInterval || diff < TimeSpan.Zero;
        if (isOutSide) _lastTriggeredDate = today;
        return isOutSide;
    }

    private bool GteCheck(DateTime now, DateTime today)
    {
        // C-8 修复：语义为"每天到达目标时间时触发一次"
        if (_lastTriggeredDate == today) return false;
        if (now.TimeOfDay >= _time)
        {
            _lastTriggeredDate = today;
            return true;
        }
        return false;
    }

    private bool LtCheck(DateTime now, DateTime today)
    {
        // C-8 修复：语义为"每天在目标时间之前触发一次"
        // 注意：如果启动时间已在目标时间之后，今天不再触发（等明天）
        if (_lastTriggeredDate == today) return false;
        if (now.TimeOfDay < _time)
        {
            _lastTriggeredDate = today;
            return true;
        }
        return false;
    }
}
