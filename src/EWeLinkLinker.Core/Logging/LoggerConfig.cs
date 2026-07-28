namespace EWeLinkLinker.Core.Logging;

/// <summary>
/// 全局日志配置 - 所有日志写入前检查此开关
/// M-11 修复：使用 volatile 确保多线程可见性
/// </summary>
public static class LoggerConfig
{
    private static volatile bool _isEnabled = true;

    /// <summary>
    /// 全局日志开关。设为 false 后，所有日志写入方法立即返回。
    /// </summary>
    public static bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }
}
