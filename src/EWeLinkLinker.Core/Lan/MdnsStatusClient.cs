using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Lan;

/// <summary>
/// 走 mDNS 从局域网直接读设备此刻的状态。设备对 `_ewelink._tcp.local.` 的应答里，
/// TXT 的 data1..dataN 拼起来用设备 deviceKey AES 解密就是它当前的 params（含 switches）。
/// HTTP 的 /zeroconf/getState 只表示"我在线"（实测灯回 error=0 但没有 data，水冷直接回 400），状态不在那儿。
/// </summary>
public class MdnsStatusClient
{
    private const string ServiceName = "_ewelink._tcp.local.";
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    public sealed record LanDeviceStatus(string DeviceId, Dictionary<int, bool> Channels, int Seq, string SourceIp);

    /// <summary>
    /// 发一次查询、收到 timeout 为止。返回的键是 deviceid（小写）；
    /// 没出现在结果里只代表"这次局域网没读到"，不能当成设备离线。
    /// </summary>
    public async Task<Dictionary<string, LanDeviceStatus>> QueryStatusAsync(
        IReadOnlyList<DeviceInfo> devices, TimeSpan timeout, CancellationToken ct = default)
    {
        var targets = new Dictionary<string, DeviceInfo>();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.DeviceId) || string.IsNullOrEmpty(d.IpAddress) || string.IsNullOrEmpty(d.DeviceKey))
                continue;
            targets[d.DeviceId.ToLowerInvariant()] = d;
        }

        var result = new Dictionary<string, LanDeviceStatus>();
        if (targets.Count == 0) return result;

        UdpClient udp;
        try
        {
            udp = OpenSocket();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[LanStatus] 5353 绑定失败({ex.GetType().Name})，本次跳过局域网状态");
            return result;
        }

        using (udp)
        {
            var query = BuildQuery(ServiceName);
            foreach (var ep in new[] { new IPEndPoint(MdnsGroup, MdnsPort) }
                        .Concat(targets.Values.Select(d => new IPEndPoint(IPAddress.Parse(d.IpAddress), MdnsPort))))
            {
                try { await udp.SendAsync(query, query.Length, ep).ConfigureAwait(false); }
                catch { /* 组播或单播哪条发得出去走哪条，另一条兜 */ }
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var receive = udp.ReceiveAsync();
                if (await Task.WhenAny(receive, Task.Delay(remaining, CancellationToken.None)).ConfigureAwait(false) != receive)
                {
                    _ = receive.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }

                UdpReceiveResult recv;
                try { recv = await receive.ConfigureAwait(false); }
                catch (Exception) { break; }

                ParsePacket(recv.Buffer, recv.RemoteEndPoint.Address.ToString(), targets, result);
            }
        }

        SimpleLogger.Log($"[LanStatus] 局域网读到 {result.Count}/{targets.Count} 台：" +
            string.Join(", ", result.Values.Select(v => $"{(targets.TryGetValue(v.DeviceId.ToLowerInvariant(), out var t) ? t.Name : v.DeviceId)}" +
                $"[{string.Join(",", v.Channels.OrderBy(c => c.Key).Select(c => c.Key + (c.Value ? "=on" : "=off")))}] seq={v.Seq}")));
        return result;
    }

    /// <summary>
    /// svchost 常驻 0.0.0.0:5353，绑 IPAddress.Any 会被它饿死 ⇒ 绑网卡自己的 IPv4 地址，
    /// 设备发给本机的单播应答才会命中这个更具体的 socket（实测有效）。
    /// </summary>
    private static UdpClient OpenSocket()
    {
        var localIp = PickLocalIPv4();
        var udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(localIp ?? IPAddress.Any, MdnsPort));
        if (localIp != null)
        {
            try { udp.JoinMulticastGroup(MdnsGroup, localIp); } catch { /* 不进组播也收得到单播应答 */ }
        }
        return udp;
    }

    private static IPAddress? PickLocalIPv4()
    {
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = iface.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address != null && !g.Address.Equals(IPAddress.Any))) continue;
                var v4 = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                    && !a.Address.ToString().StartsWith("169.254."));
                if (v4 != null) return v4.Address;
            }
        }
        catch { /* 拿不到网卡就退回 Any */ }
        return null;
    }

    private static byte[] BuildQuery(string name)
    {
        var q = new List<byte>();
        q.AddRange(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // QDCOUNT=1，其余计数为 0
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            q.Add((byte)label.Length);
            q.AddRange(Encoding.UTF8.GetBytes(label));
        }
        q.Add(0); // 根标签
        q.AddRange(new byte[] { 0, 12, 0, 1 }); // QTYPE=PTR, QCLASS=IN
        return q.ToArray();
    }

    private static string ReadName(byte[] b, ref int pos, int depth = 0)
    {
        var sb = new StringBuilder();
        // 名字压缩指针可以互相跳转，限深防止畸形包把解析绕成死循环
        while (pos < b.Length && depth < 8)
        {
            int len = b[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= b.Length) break;
                var ptr = ((len & 0x3F) << 8) | b[pos + 1];
                pos += 2;
                var jumped = ReadName(b, ref ptr, depth + 1);
                if (sb.Length > 0 && jumped.Length > 0) sb.Append('.');
                sb.Append(jumped);
                break;
            }
            pos++;
            if (pos + len > b.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(b, pos, len));
            pos += len;
        }
        return sb.ToString();
    }

    private static void ParsePacket(byte[] buf, string sourceIp, Dictionary<string, DeviceInfo> targets,
        Dictionary<string, LanDeviceStatus> result)
    {
        if (buf.Length < 12) return;
        int qd = (buf[4] << 8) | buf[5], an = (buf[6] << 8) | buf[7], ns = (buf[8] << 8) | buf[9], ar = (buf[10] << 8) | buf[11];
        int pos = 12;

        for (int i = 0; i < qd && pos < buf.Length; i++)
        {
            ReadName(buf, ref pos);
            pos += 4;
        }

        for (int i = 0; i < an + ns + ar && pos < buf.Length; i++)
        {
            var name = ReadName(buf, ref pos);
            if (pos + 10 > buf.Length) return;
            // RR 固定部分：TYPE(2) CLASS(2) TTL(4) RDLENGTH(2)，RDLENGTH 在 +8/+9
            int type = (buf[pos] << 8) | buf[pos + 1];
            int rdlen = (buf[pos + 8] << 8) | buf[pos + 9];
            pos += 10;
            if (pos + rdlen > buf.Length) return;

            if (type == 16) TryConsumeTxt(buf, pos, rdlen, name, sourceIp, targets, result);
            pos += rdlen;
        }
    }

    private static void TryConsumeTxt(byte[] buf, int start, int rdlen, string recordName, string sourceIp,
        Dictionary<string, DeviceInfo> targets, Dictionary<string, LanDeviceStatus> result)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int end = start + rdlen, t = start;
        while (t < end)
        {
            int len = buf[t];
            t++;
            if (len == 0 || t + len > end) break;
            var s = Encoding.UTF8.GetString(buf, t, len);
            t += len;
            var eq = s.IndexOf('=');
            if (eq <= 0) continue;
            kv[s[..eq]] = s[(eq + 1)..];
        }

        if (!kv.TryGetValue("id", out var rawId)) return;
        var deviceId = rawId.Trim();
        if (!targets.TryGetValue(deviceId.ToLowerInvariant(), out var device)) return;
        if (result.ContainsKey(deviceId.ToLowerInvariant())) return;

        var data = new StringBuilder();
        for (int i = 1; i <= 8; i++)
            if (kv.TryGetValue("data" + i, out var chunk)) data.Append(chunk);

        if (data.Length == 0 || !kv.TryGetValue("iv", out var iv)) return;
        if (!kv.TryGetValue("encrypt", out var enc) || !enc.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

        string plain;
        try { plain = AesCrypto.Decrypt(data.ToString(), device.DeviceKey, iv); }
        catch (Exception) { return; }

        var channels = ReadSwitches(plain);
        if (channels.Count == 0) return;

        int seq = kv.TryGetValue("seq", out var seqText) && int.TryParse(seqText, out var parsedSeq) ? parsedSeq : 0;
        result[deviceId.ToLowerInvariant()] = new LanDeviceStatus(deviceId, channels, seq, sourceIp);
    }

    private static Dictionary<int, bool> ReadSwitches(string json)
    {
        var map = new Dictionary<int, bool>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("switches", out var switches) || switches.ValueKind != JsonValueKind.Array)
                return map;
            foreach (var item in switches.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("outlet", out var outletEl) || outletEl.ValueKind != JsonValueKind.Number) continue;
                if (!outletEl.TryGetInt32(out var outlet)) continue;
                if (!item.TryGetProperty("switch", out var stateEl) || stateEl.ValueKind != JsonValueKind.String) continue;
                var state = stateEl.GetString();
                if (string.IsNullOrEmpty(state)) continue;
                map[outlet] = state.Equals("on", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception) { return map; }
        return map;
    }
}
