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
/// 复合触发器接口 - 支持 AND/OR 逻辑
/// </summary>
public interface ICompositeTrigger : ITrigger
{
    /// <summary>
    /// 逻辑运算符
    /// </summary>
    LogicalOperator Operator { get; }

    /// <summary>
    /// 子触发器列表
    /// </summary>
    IReadOnlyList<ITrigger> Children { get; }

    /// <summary>
    /// 添加子触发器
    /// </summary>
    void AddChild(ITrigger trigger);

    /// <summary>
    /// 移除子触发器
    /// </summary>
    void RemoveChild(string triggerId);
}

/// <summary>
/// 触发器配置基类
/// </summary>
public class TriggerConfig
{
    public string Type { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string Parameter2 { get; set; } = "";
    public ComparisonOperator Comparison { get; set; } = ComparisonOperator.Gte;
    public string Comparer { get; set; } = "and"; // 保留用于兼容

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
