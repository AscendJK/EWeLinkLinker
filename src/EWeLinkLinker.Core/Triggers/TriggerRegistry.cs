using System.Reflection;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 触发器注册表 - 自动发现所有触发器类型
/// 插件式：新触发器只需添加 [Trigger] 特性即可自动注册
/// </summary>
public static class TriggerRegistry
{
    private static readonly Dictionary<string, (Type Type, TriggerAttribute Attribute)> _types = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    /// <summary>
    /// 初始化注册表 - 自动扫描程序集中的所有触发器类型
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_types)
        {
            if (_initialized) return;

            var assembly = typeof(ITrigger).Assembly;
            var triggerTypes = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ITrigger).IsAssignableFrom(t));

            foreach (var type in triggerTypes)
            {
                var attr = type.GetCustomAttribute<TriggerAttribute>();
                // 修复：使用更稳定的 key 生成逻辑
                var key = attr?.TypeKey ?? type.Name.ToLower().Replace("trigger", "").Replace("_", "");

                if (!_types.ContainsKey(key))
                {
                    _types[key] = (type, attr ?? new TriggerAttribute(key, type.Name));
                }
            }

            _initialized = true;
        }
    }

    /// <summary>
    /// 创建触发器实例
    /// </summary>
    public static OptimizedTriggerBase Create(TriggerConfig config)
    {
        EnsureInitialized();

        if (!_types.TryGetValue(config.Type, out var entry))
            throw new ArgumentException($"Unknown trigger type: {config.Type}. Supported: {string.Join(", ", _types.Keys)}");

        try
        {
            // 所有触发器都继承自 OptimizedTriggerBase
            if (Activator.CreateInstance(entry.Type, config) is OptimizedTriggerBase trigger)
                return trigger;

            throw new InvalidOperationException($"Trigger type '{config.Type}' does not inherit from OptimizedTriggerBase");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create trigger of type '{config.Type}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 试建一遍再丢掉，只为了拿参数是否可用的结论（GUI 保存前调用）。
    /// 各触发器的构造函数只做参数解析，不碰传感器/进程/WMI，所以这里没有副作用。
    /// 服务侧的行为是"建不出来就把整条规则连同所有动作一起跳过"，界面必须提前点名拦下，
    /// 否则用户看到「配置已保存！」而这条规则再也不会执行。
    /// </summary>
    public static bool TryValidate(TriggerConfig config, out string? error)
    {
        error = null;
        try
        {
            Create(config).Dispose();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    /// <summary>
    /// 获取所有支持的触发器类型（修复：使用缓存的属性信息）
    /// </summary>
    public static IEnumerable<(string TypeKey, string DisplayName, string Description)> GetSupportedTypes()
    {
        EnsureInitialized();

        foreach (var (key, (_, attr)) in _types)
        {
            yield return (key, attr.DisplayName, attr.Description);
        }
    }

    /// <summary>
    /// 检查类型是否支持
    /// </summary>
    public static bool IsSupported(string typeKey)
    {
        EnsureInitialized();
        return _types.ContainsKey(typeKey);
    }
}
