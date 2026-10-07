using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.ConfigApp;

internal enum SaveDecision
{
    Allow,
    /// <summary>内存里的规则/设备比磁盘少，可能是加载不完整造成的，要他确认才覆盖。</summary>
    ConfirmShrink,
    /// <summary>加载阶段出过错，内存内容已知不完整，任何情况都不许覆盖磁盘。</summary>
    RefuseLoadFailed,
}

/// <summary>
/// 「用内存这份覆盖磁盘那份」之前的两道闸。历史事故都是同一形状：加载或云端只拿到一部分，
/// 而保存是无条件整份重建，于是把没拿到的那部分从盘上抹掉，界面还报"已保存"。
/// </summary>
internal static class ConfigSafety
{
    internal static SaveDecision Evaluate(bool loadFailed, int memRules, int diskRules, int memDevices, int diskDevices)
    {
        if (loadFailed) return SaveDecision.RefuseLoadFailed;

        var rulesShrunk = memRules == 0 && diskRules > 0;
        var devicesShrunk = memDevices == 0 && diskDevices > 0;
        return rulesShrunk || devicesShrunk ? SaveDecision.ConfirmShrink : SaveDecision.Allow;
    }

    /// <summary>
    /// 云端偶发只返回一部分设备（分页截断、设备被移到别的空间、账号侧异常）。
    /// 整表替换会让被漏掉那台在规则里的动作 DeviceId 变 null 并落盘，事后无法自愈，
    /// 所以本地已有但云端没返回的设备一律保留，并把名字报回去。
    /// </summary>
    internal static (List<DeviceInfo> Merged, List<string> KeptLocalOnly) KeepMissingLocalDevices(
        List<DeviceInfo> cloudDevices, List<DeviceInfo> localDevices)
    {
        var merged = new List<DeviceInfo>(cloudDevices);
        var kept = new List<string>();
        foreach (var local in localDevices)
        {
            if (string.IsNullOrEmpty(local.DeviceId)) continue;
            if (merged.Any(d => d.DeviceId == local.DeviceId)) continue;
            merged.Add(local);
            kept.Add(string.IsNullOrEmpty(local.Name) ? local.DeviceId : local.Name);
        }
        return (merged, kept);
    }
}
