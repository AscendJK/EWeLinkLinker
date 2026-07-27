using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 复合触发器 - 支持 AND/OR 逻辑组合多个条件
/// 示例：CPU温度 > 80 AND GPU温度 > 75 时触发
/// </summary>
[Trigger("composite", "复合条件", "组合多个触发条件，支持 AND/OR 逻辑")]
public class CompositeTrigger : OptimizedTriggerBase, ICompositeTrigger
{
    private readonly List<ITrigger> _children = new();
    private readonly object _lock = new();

    public LogicalOperator Operator { get; set; }
    public IReadOnlyList<ITrigger> Children
    {
        get
        {
            lock (_lock) return _children.AsReadOnly();
        }
    }

    public CompositeTrigger(TriggerConfig config) : base()
    {
        // 解析逻辑运算符
        if (!string.IsNullOrEmpty(config.Comparer) &&
            Enum.TryParse<LogicalOperator>(config.Comparer, true, out var op))
        {
            Operator = op;
        }
        else
        {
            Operator = LogicalOperator.And; // 默认 AND
        }

        // 解析子触发器配置
        if (!string.IsNullOrEmpty(config.Parameter))
        {
            try
            {
                var childrenConfigs = System.Text.Json.JsonSerializer.Deserialize<List<TriggerConfig>>(config.Parameter);
                if (childrenConfigs != null)
                {
                    foreach (var childConfig in childrenConfigs)
                    {
                        try
                        {
                            var child = TriggerRegistry.Create(childConfig);
                            _children.Add(child);
                        }
                        catch (Exception ex)
                        {
                            // 记录但继续
                            System.Diagnostics.Debug.WriteLine($"Failed to create child trigger: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse composite config: {ex.Message}");
            }
        }
    }

    public override string Type => "composite";
    public override string DisplayName => Operator == LogicalOperator.And ? "全部满足 (AND)" : "任一满足 (OR)";

    public void AddChild(ITrigger trigger)
    {
        lock (_lock)
        {
            if (!_children.Contains(trigger))
            {
                _children.Add(trigger);
                if (State == TriggerState.Monitoring)
                {
                    trigger.Start();
                }
            }
        }
    }

    public void RemoveChild(string triggerId)
    {
        lock (_lock)
        {
            var child = _children.FirstOrDefault(c => c.Id == triggerId);
            if (child != null)
            {
                child.Stop();
                child.Dispose();
                _children.Remove(child);
            }
        }
    }

    protected override void OnStart()
    {
        lock (_lock)
        {
            foreach (var child in _children)
            {
                try { child.Start(); } catch { }
            }
        }
    }

    protected override void OnStop()
    {
        lock (_lock)
        {
            foreach (var child in _children)
            {
                try { child.Stop(); } catch { }
            }
        }
    }

    protected override async ValueTask<bool> EvaluateCoreAsync(CancellationToken ct)
    {
        List<ITrigger> children;
        lock (_lock)
        {
            children = _children.ToList();
        }

        if (children.Count == 0) return false;

        try
        {
            // 并行评估所有子条件
            var tasks = children.Select(c => c.PollAsync(ct)).ToList();
            var results = await Task.WhenAll(tasks);

            return Operator switch
            {
                LogicalOperator.And => results.All(r => r),
                LogicalOperator.Or => results.Any(r => r),
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    protected override void OnDispose()
    {
        lock (_lock)
        {
            foreach (var child in _children)
            {
                try { child.Dispose(); } catch { }
            }
            _children.Clear();
        }
    }
}
