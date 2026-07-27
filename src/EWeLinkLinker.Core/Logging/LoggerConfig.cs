namespace EWeLinkLinker.Core.Logging;

/// <summary>
/// 全局日志配置 - 所有日志写入前检查此开关
/// </summary>
public static class LoggerConfig
{
    /// <summary>
    /// 全局日志开关。设为 false 后，所有日志写入方法立即返回。
    /// </summary>
    public static bool IsEnabled { get; set; } = true;
}
