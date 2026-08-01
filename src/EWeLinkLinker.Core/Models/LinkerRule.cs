using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EWeLinkLinker.Core.Models;

using System.Text.Json.Serialization;

/// <summary>
/// 设备动作
/// </summary>
public class LinkerAction : INotifyPropertyChanged
{
    private string _deviceId = string.Empty;
    private string _name = string.Empty;
    private string _state = "on";
    private int _outlet;

    public string DeviceId
    {
        get => _deviceId;
        set { if (_deviceId != value) { _deviceId = value; OnPropertyChanged(); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); UpdateDeviceIdFromName(); } }
    }

    public string State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    public int Outlet
    {
        get => _outlet;
        set { if (_outlet != value) { _outlet = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// 获取设备完整名称（用于显示）
    /// </summary>
    [JsonIgnore]
    public string DisplayText => $"{Name} 通道{Outlet}: {(State == "on" ? "开" : "关")}";

    private void UpdateDeviceIdFromName()
    {
        // 当名称变化时，尝试从全局设备列表更新 DeviceId
        // 这里不直接引用 MainWindow，避免循环依赖
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// 规则条件
/// </summary>
public partial class RuleCondition : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    private string _type = "time";
    public string Type
    {
        get => _type;
        set
        {
            if (SetProperty(ref _type, value))
            {
                // 注意：直接写字段而非走属性 setter，避免 STJ 反序列化时
                // parameter 键先于 type 到达导致已加载的参数被清空。
                // UI 层在类型变更时（SubTypeComboBox_SelectionChanged）会自行设置默认参数。
                // _parameter 不再清空——STJ 反序列化顺序不确定，type 先于 parameter 到达时会清掉已加载的值
                // UI 层（SubTypeComboBox_SelectionChanged）在类型变更时自行设置默认参数
                OnPropertyChanged(nameof(Parameter));
                OnPropertyChanged(nameof(IsTime));
                OnPropertyChanged(nameof(IsInterval));
                OnPropertyChanged(nameof(IsCpuTemp));
                OnPropertyChanged(nameof(IsCpuUsage));
                OnPropertyChanged(nameof(IsGpuTemp));
                OnPropertyChanged(nameof(IsNumeric));
                OnPropertyChanged(nameof(IsAppEvent));
                OnPropertyChanged(nameof(IsPowerEvent));
                OnPropertyChanged(nameof(IsNoParameter));
                OnPropertyChanged(nameof(ParameterLabel));
                OnPropertyChanged(nameof(ParameterPlaceholder));
                OnPropertyChanged(nameof(ParameterUnit));
                OnPropertyChanged(nameof(ShowComparison));
            }
        }
    }

    private string _parameter = "";
    public string Parameter
    {
        get => _parameter;
        set => SetProperty(ref _parameter, value);
    }

    /// <summary>
    /// 第二参数（用于范围）
    /// </summary>
    private string _parameter2 = "";
    public string Parameter2
    {
        get => _parameter2;
        set => SetProperty(ref _parameter2, value);
    }

    /// <summary>
    /// 比较运算符
    /// </summary>
    private ComparisonOperator _comparison = ComparisonOperator.Gte;
    public ComparisonOperator Comparison
    {
        get => _comparison;
        set
        {
            if (SetProperty(ref _comparison, value))
            {
                OnPropertyChanged(nameof(IsRangeComparison));
            }
        }
    }

    public LogicalOperator Operator { get; set; } = LogicalOperator.And;

    /// <summary>
    /// 获取数值类型的参数（用于数字输入控件，不序列化）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int NumericValue
    {
        get => int.TryParse(Parameter, out var val) ? val : 0;
        set => Parameter = value.ToString();
    }

    // 辅助属性（用于 UI 绑定，不序列化）
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsTime => Type == "time";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInterval => Type == "interval";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCpuTemp => Type == "cpu_temp";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCpuUsage => Type == "cpu_usage";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGpuTemp => Type == "gpu_temp";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNumeric => IsInterval || IsCpuTemp || IsCpuUsage || IsGpuTemp;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAppEvent => Type == "app_start" || Type == "app_close";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPowerEvent => Type is "boot" or "shutdown" or "sleep" or "wake";
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNoParameter => IsPowerEvent;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ShowComparison => IsTime || IsCpuTemp || IsCpuUsage || IsGpuTemp;

    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterLabel => Type switch
    {
        "time" => "时间",
        "interval" => "间隔",
        "cpu_temp" => "温度",
        "cpu_usage" => "使用率",
        "gpu_temp" => "温度",
        "app_start" or "app_close" => "进程名",
        _ => ""
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterUnit => Type switch
    {
        "interval" => "分钟",
        "cpu_temp" or "gpu_temp" => "°C",
        "cpu_usage" => "%",
        _ => ""
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterPlaceholder => Type switch
    {
        "time" => "08:00",
        "interval" => "30",
        "cpu_temp" => "80",
        "cpu_usage" => "90",
        "gpu_temp" => "80",
        "app_start" or "app_close" => "notepad",
        _ => ""
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRangeComparison => Comparison == ComparisonOperator.Range || Comparison == ComparisonOperator.OutsideRange;

    /// <summary>
    /// 参数范围提示文本
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ParameterHint => Type switch
    {
        "time" => "格式: HH:mm，如 08:00",
        "interval" => "单位: 分钟，范围: 1-1440",
        "cpu_temp" => "单位: °C，范围: 0-100",
        "cpu_usage" => "单位: %，范围: 0-100",
        _ => ""
    };
}

/// <summary>
/// 可观察对象基类
/// </summary>
public partial class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// <summary>
/// 逻辑运算符（条件之间的组合方式）
/// </summary>
public enum LogicalOperator
{
    And,
    Or
}

/// <summary>
/// 比较运算符（条件值与实际值的比较方式）
/// </summary>
public enum ComparisonOperator
{
    /// <summary>大于等于</summary>
    Gte,
    /// <summary>大于</summary>
    Gt,
    /// <summary>小于等于</summary>
    Lte,
    /// <summary>小于</summary>
    Lt,
    /// <summary>等于</summary>
    Eq,
    /// <summary>不等于</summary>
    Neq,
    /// <summary>范围内（参数格式：min,max）</summary>
    Range,
    /// <summary>范围外（参数格式：min,max）</summary>
    OutsideRange
}

/// <summary>
/// 联动规则 - 统一模型
/// </summary>
public class LinkerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新规则";
    public bool Enabled { get; set; } = true;
    public ObservableCollection<RuleCondition> Conditions { get; set; } = new();
    public ObservableCollection<LinkerAction> Actions { get; set; } = new();

    /// <summary>
    /// 事件类型（兼容旧版，新代码使用 Conditions）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Event { get; set; }

    /// <summary>
    /// 触发器配置 JSON（兼容旧版）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? TriggerConfig { get; set; }
}

