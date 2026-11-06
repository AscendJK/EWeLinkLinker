using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 触发器状态变更事件参数
/// </summary>
public class TriggerStateChangedEventArgs : EventArgs
{
    public TriggerState OldState { get; init; }
    public TriggerState NewState { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// 触发器事件参数（触发时）
/// </summary>
public class TriggerEventArgs : EventArgs
{
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// 触发器状态
/// </summary>
public enum TriggerState
{
    Idle,       // 空闲
    Monitoring, // 监控中
    Triggered,  // 已触发
    Error       // 错误
}

/// <summary>
/// 触发器接口 - 所有触发器必须实现
/// </summary>
public interface ITrigger : IDisposable
{
    string Id { get; }
    string Type { get; }
    string DisplayName { get; }
    TriggerState State { get; }

    /// <summary>
    /// 轮询检查（测试按钮和服务端都调用这个）
    /// </summary>
    Task<bool> PollAsync(CancellationToken ct = default);

    void Start();
    void Stop();

    event EventHandler<TriggerStateChangedEventArgs>? StateChanged;

    void SetLogPath(string logPath);
}

/// <summary>
/// 触发器配置基类
/// </summary>
public class TriggerConfig
{
    public string Type { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string Parameter2 { get; set; } = "";
    /// <summary>滞回解除带宽（阈值两侧允许抖动的幅度）；为空表示不启用滞回（维持原有边沿行为）</summary>
    public string ReleaseBand { get; set; } = "";
    public ComparisonOperator Comparison { get; set; } = ComparisonOperator.Gte;
    public string Comparer { get; set; } = "and"; // 保留用于兼容

    /// <summary>
    /// 条件身份（RuleCondition.Id）。只有 interval 这类"节拍型"触发器用得到：
    /// 热重载会重建触发器实例，靠这个键把上一次的节拍接回来，否则倒计时会被清零。
    /// 事件路径的临时触发器不填，等于"一次性"，不污染任何节拍。
    /// </summary>
    public string Key { get; set; } = "";

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

    public static TriggerConfig? FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<TriggerConfig>(json);
}

/// <summary>
/// 触发器特性 - 用于自动注册
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class TriggerAttribute : Attribute
{
    public string TypeKey { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public TriggerAttribute(string typeKey, string displayName, string description = "")
    {
        TypeKey = typeKey;
        DisplayName = displayName;
        Description = description;
    }
}
