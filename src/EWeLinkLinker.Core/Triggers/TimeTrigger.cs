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

    public override string Type => "time";
    public override string DisplayName => "每天固定时间";

    /// <summary>
    /// 每日触发窗口的下限：全局轮询间隔很小时窗口也不会短于这个值，
    /// 免得一次轮询抖动就错过当天的触发点。
    /// </summary>
    private static readonly TimeSpan DailyWindowFloor = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 到点也是事件：命中之后把状态保持到既有的有效窗口结束，让 AND 里其它条件（温度/使用率）
    /// 在这段窗口内赶上来，而不是要求两者落在同一拍。窗口与"每日触发窗口"用同一个值，
    /// 语义不变——仍然是每天最多一次，只是不再要求同一轮询周期。
    /// </summary>
    protected override TimeSpan PulseWindow => EffectiveWindow;

    /// <summary>
    /// 有效触发窗口：至少覆盖两个全局轮询间隔（30s 全局间隔时窗口为 60s），
    /// 避免轮询间隔较大时首次轮询落在窗口之外导致当日错过触发
    /// </summary>
    private TimeSpan EffectiveWindow
    {
        get
        {
            var global = GlobalPollingInterval ?? TimeSpan.Zero;
            var twiceGlobal = TimeSpan.FromTicks(global.Ticks * 2);
            return twiceGlobal > DailyWindowFloor ? twiceGlobal : DailyWindowFloor;
        }
    }

    public TimeTrigger(TriggerConfig config) : base()
    {
        if (!ValidateParameter(config.Parameter, out var error))
            throw new ArgumentException(error);

        _time = ParseTime(config.Parameter!);
        _comparison = config.Comparison;
    }

    protected override void OnStart()
    {
        // Stop/Start 或热重载后复位"今天已触发"的记账，确保当天可再次触发
        _lastTriggeredDate = DateTime.MinValue;
    }

    public override bool ValidateParameter(string parameter, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(parameter))
        {
            errorMessage = "时间不能为空";
            return false;
        }
        if (!TryParseTime(parameter, out _))
        {
            errorMessage = "时间格式无效，应为 HH:mm，如 08:00";
            return false;
        }
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 时间串按不变文化解析：跟着系统区域走的话，有些区域的时间分隔符不是冒号（用点），
    /// 同一份 linker.json 换台机器就解析不出 "08:00"，规则会直接建不起来。
    /// 语法仍然保持原来的宽（8:10、08:10:00 都收）。
    /// </summary>
    private static bool TryParseTime(string? text, out TimeSpan value) =>
        TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static TimeSpan ParseTime(string text)
    {
        if (!TryParseTime(text, out var value))
            throw new ArgumentException($"时间格式无效，应为 HH:mm，如 08:00（当前填的是 {text}）");
        return value;
    }

    protected override ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        var now = DateTime.Now;
        var today = now.Date;

        // 根据比较运算符判断（各 Check 内部用 _lastTriggeredDate 保证每天最多命中一次）
        var matched = _comparison switch
        {
            ComparisonOperator.Eq => EqCheck(now, today),
            ComparisonOperator.Neq => NeqCheck(now, today),
            ComparisonOperator.Gte => GteCheck(now, today),
            ComparisonOperator.Lt => LtCheck(now, today),
            _ => false
        };

        // 命中即产生待决事件；状态由基类按脉冲窗口保持到窗口结束或被规则消费，
        // 不再"这一轮结束就复位"——那会让 time AND cpu_temp 这种规则要求两者同拍。
        if (matched && !PulsePending)
        {
            RaisePulse();
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }

    private bool EqCheck(DateTime now, DateTime today)
    {
        // C-7 修复：使用"今日是否已触发" + "首次进入窗口时触发"
        // 窗口至少覆盖两个全局轮询间隔，避免轮询间隔较大时错过每日触发窗口
        if (_lastTriggeredDate == today) return false;
        var diff = now.TimeOfDay - _time;
        // 窗口：diff >= 0（已过目标时间）且 diff <= EffectiveWindow（首次进入窗口）
        var isMatch = diff >= TimeSpan.Zero && diff <= EffectiveWindow;
        if (isMatch) _lastTriggeredDate = today;
        return isMatch;
    }

    private bool NeqCheck(DateTime now, DateTime today)
    {
        if (_lastTriggeredDate == today) return false;
        var diff = now.TimeOfDay - _time;
        var isOutSide = diff > EffectiveWindow || diff < TimeSpan.Zero;
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
