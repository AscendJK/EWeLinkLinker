using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EWeLinkLinker.Core.Lan;

/// <summary>
/// 一块网卡的快照。抽成纯数据是为了让"该扫哪个 /24"这件事能被单测驱动，
/// 不必真的在这台机器上插一块 VPN 网卡。
/// </summary>
internal readonly record struct NetAdapter(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    string? IPv4,
    bool HasGateway);

/// <summary>
/// 设备发现里那些"纯计算"的部分：挑网段、解析 arp -a、归一 MAC。
///
/// 放在这里不是因为好看，是因为它们原来都埋在 IO 中间：选错网段只能靠"发现不到设备"来感知，
/// arp 解析跟着子进程一起无法测试。搬到纯函数后，VPN 网卡、中文表头、畸形 MAC 这些情况都能被用例钉住。
/// </summary>
internal static class LanDiscoveryParsing
{
    /// <summary>
    /// 虚拟/隧道网卡的特征词。这类网卡在 Windows 上同样是 Up、同样可能带默认网关，
    /// 只按"有没有网关"挑网段会挑中 VPN 的 10.8.0.x，于是整个 /24 扫在错误的地方。
    /// </summary>
    private static readonly string[] VirtualAdapterHints =
    {
        "vpn", "tap", "tun", "wireguard", "openvpn", "wintun", "tailscale", "zerotier",
        "hyper-v", "vethernet", "virtualbox", "vmware", "loopback", "docker", "bluetooth"
    };

    internal static bool LooksVirtual(NetAdapter adapter)
    {
        var haystack = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
        if (haystack.Contains("virtual")) return true;
        foreach (var hint in VirtualAdapterHints)
            if (haystack.Contains(hint)) return true;
        return false;
    }

    private static bool IsUsable(NetAdapter a) =>
        a.Status == OperationalStatus.Up &&
        a.Type != NetworkInterfaceType.Loopback &&
        IPAddress.TryParse(a.IPv4, out var ip) &&
        ip.AddressFamily == AddressFamily.InterNetwork &&
        !a.IPv4!.StartsWith("169.254.", StringComparison.Ordinal);

    private static string SubnetOf(string ipv4)
    {
        var parts = ipv4.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : "";
    }

    /// <summary>
    /// 选一个要扫的 /24，返回前三段；没有可用 IPv4 时返回 null（调用方据此跳过 ping/TCP 扫描，
    /// 而不是像以前那样盲扫一个硬编码的 192.168.1 段）。
    ///
    /// 优先级：真实网卡+网关 → 真实网卡 → 任意网卡（虚拟网卡兜底，保持旧行为） → null。
    /// </summary>
    internal static string? SelectSubnet(IReadOnlyList<NetAdapter> adapters)
    {
        foreach (var a in adapters) if (IsUsable(a) && !LooksVirtual(a) && a.HasGateway) return SubnetOf(a.IPv4!);
        foreach (var a in adapters) if (IsUsable(a) && !LooksVirtual(a)) return SubnetOf(a.IPv4!);
        foreach (var a in adapters) if (IsUsable(a)) return SubnetOf(a.IPv4!);
        return null;
    }

    /// <summary>MAC 归一成 12 位小写十六进制；不像 MAC 的一律返回空串（调用方据此跳过匹配）。</summary>
    internal static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        var clean = mac.Replace(":", "").Replace("-", "").Replace(".", "").Replace(" ", "").ToLowerInvariant();
        if (clean.Length == 12 && clean.All(c => "0123456789abcdef".Contains(c)))
            return clean;
        return string.Empty;
    }

    /// <summary>
    /// 解析 <c>arp -a</c> 的输出（ip → 归一后的 MAC）。
    ///
    /// Windows 会按语言换表头（Interface:/接口:、Internet Address/Internet 地址）和分隔符（- 或 .），
    /// 所以不靠表头识别，只靠"这一行前两列能不能分别解析成 IPv4 和 MAC"。
    /// 无线网卡常留下 <c>ff-ff-ff-ff-ff-ff</c> 广播条目，它是合法 MAC，保留；
    /// 解析不出 MAC 的行（如 &lt;incomplete&gt;）丢弃。
    /// </summary>
    internal static Dictionary<string, string> ParseArpOutput(string? output)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return table;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;

            var mac = NormalizeMac(parts[1]);
            if (mac.Length == 0) continue;

            table[ip.ToString()] = mac;
        }

        return table;
    }
}
