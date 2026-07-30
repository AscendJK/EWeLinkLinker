using System.Collections.Concurrent;

namespace EWeLinkLinker.Core.Triggers;

/// <summary>
/// 传感器缓存 — 每轮轮询只读取一次传感器，所有触发器共享
/// 线程安全：每个 key 独立锁，不同传感器可并发读取
/// </summary>
public sealed class SensorCache
{
    private readonly ConcurrentDictionary<string, object> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// 获取或创建缓存值（线程安全，每个 key 只执行一次 factory）
    /// </summary>
    public T GetOrCreate<T>(string key, Func<T> factory)
    {
        var keyLock = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        keyLock.Wait();
        try
        {
            if (_cache.TryGetValue(key, out var value))
                return (T)value;

            T newValue;
            try
            {
                newValue = factory();
            }
            catch (Exception ex)
            {
                // 记录传感器读取失败
                EWeLinkLinker.Core.Logging.SimpleLogger.Log($"[SensorCache] Failed to read '{key}': {ex.Message}");
                throw;
            }

            if (newValue != null)
                _cache[key] = newValue;
            return newValue;
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>
    /// 清空缓存并释放所有资源（保留锁对象，避免每轮创建/销毁）
    /// </summary>
    public void Clear()
    {
        // 释放缓存中的资源
        foreach (var value in _cache.Values)
        {
            // 处理数组（如 Process[]）— 数组元素可能实现 IDisposable
            if (value is Array array)
            {
                foreach (var item in array)
                {
                    if (item is IDisposable d)
                    {
                        try { d.Dispose(); }
                        catch { }
                    }
                }
            }
            // 处理单个 IDisposable 对象
            else if (value is IDisposable d)
            {
                try { d.Dispose(); }
                catch { }
            }
        }
        _cache.Clear();

        // 注意：不释放 SemaphoreSlim 锁对象，保留它们供下一轮使用
        // 锁对象数量有限（每种传感器一个），复用避免 GC 压力
    }
}
