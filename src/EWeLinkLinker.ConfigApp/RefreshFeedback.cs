using LanAttempt = EWeLinkLinker.Core.Lan.MdnsStatusClient.LanAttempt;
using LanStatusResult = EWeLinkLinker.Core.Lan.MdnsStatusClient.LanStatusResult;

namespace EWeLinkLinker.ConfigApp;

/// <summary>
/// 「刷新状态」的反馈文案。云端与局域网各自可能成/败，而"局域网整段没读到"和"一切正常"
/// 以前是同一句话，"整机断网"又被报成"云端坏了"——所以两侧的状态都要进文案。
/// 同理，凡是"已写入配置"这类断言，只有真的落盘成功才许说。
/// </summary>
internal static class RefreshFeedback
{
    internal static string Build(bool cloudOk, string? cloudError, LanStatusResult lan, int lanApplied,
                                 int totalDevices, bool renewedByRelogin,
                                 bool tokensPersisted = true, bool changesPersisted = true)
    {
        var renew = renewedByRelogin
            ? (tokensPersisted ? "（云端登录已自动续期，新 token 已写入配置）"
                               : "（云端登录已自动续期，但新 token 没能写入配置文件，重启后需重新登录）")
            : "";
        var notSaved = changesPersisted ? "" : "（本次改动没有写进配置文件）";

        if (!cloudOk)
        {
            var cloudPart = $"云端这次没走通（{cloudError ?? "没有异常信息"}）";
            if (lanApplied > 0)
                return $"{cloudPart}，已用局域网实时值更新 {lanApplied} 台设备的通道状态{Unanswered(lan, "，")}。{renew}{notSaved}";

            return $"两处都没拿到。{cloudPart}；{LanSilentShort(lan)}。";
        }

        if (lanApplied > 0)
            return $"状态刷新完成，其中 {lanApplied} 台的通道状态取自局域网实时值，其余 {Math.Max(0, totalDevices - lanApplied)} 台来自云端" +
                   $"{Unanswered(lan, "（", "）")}。{renew}{notSaved}";

        return $"状态刷新完成（{totalDevices} 台全部来自云端缓存）。{LanSilentLong(lan)}{renew}{notSaved}";
    }

    /// <summary>问了但没答的设备名。只在"局域网确实读到过、只漏了部分"时才添这句。</summary>
    private static string Unanswered(LanStatusResult lan, string prefix, string suffix = "")
        => lan.UnansweredNames.Count == 0
            ? ""
            : $"{prefix}另有 {lan.UnansweredNames.Count} 台问了没应答：{string.Join("、", lan.UnansweredNames)}{suffix}";

    private static string LanSilentShort(LanStatusResult lan) => lan.Attempt switch
    {
        LanAttempt.NoTargets => "局域网没有可问的设备（配置里缺 IP 或密钥）",
        LanAttempt.SocketFailed => "本机 5353 端口没绑上，局域网未尝试",
        _ => $"局域网问了 {lan.TargetsTried} 台都没应答",
    };

    private static string LanSilentLong(LanStatusResult lan) => lan.Attempt switch
    {
        LanAttempt.NoTargets => "这些设备在配置里没有 IP 或密钥，本来就走不了局域网。",
        LanAttempt.SocketFailed => "本机 5353 端口没绑上（常被系统的 mDNS 服务占用），局域网这次没有尝试。",
        _ => $"局域网问了 {lan.TargetsTried} 台都没有应答" +
             $"{Names(lan)}，若设备其实在线，可点「刷新IP」重新发现后再试。",
    };

    private static string Names(LanStatusResult lan) =>
        lan.UnansweredNames.Count > 0 ? $"（{string.Join("、", lan.UnansweredNames)}）" : "";
}
