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
    /// <summary>查询名要带尾点，但解析应答里的主名时尾点会被当成根标签吃掉，比对得用不带尾点的形式。</summary>
    private static readonly string ServiceSuffix = ServiceName.TrimEnd('.');
    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    public sealed record LanDeviceStatus(string DeviceId, Dictionary<int, bool> Channels, int Seq, string SourceIp);

    /// <summary>局域网这一段这次到底发生了什么——"读到"和"没读到"在界面上必须是不同的话。</summary>
    public enum LanAttempt
    {
        /// <summary>至少读到一台。</summary>
        Read,
        /// <summary>问了，但一台都没应答（设备掉线、换了 IP、被交换机隔离）。</summary>
        NoResponder,
        /// <summary>根本没问：配置里没有一台同时有 IP 和密钥。</summary>
        NoTargets,
        /// <summary>根本没问：本机 5353 端口没绑上。</summary>
        SocketFailed,
    }

    public sealed record LanStatusResult(
        Dictionary<string, LanDeviceStatus> Devices,
        int TargetsTried,
        LanAttempt Attempt,
        List<string> UnansweredNames);

    /// <summary>
    /// 发一次查询、收到 timeout 为止。返回的键是 deviceid（小写）；
    /// 没出现在结果里只代表"这次局域网没读到"，不能当成设备离线。
    /// </summary>
    public async Task<LanStatusResult> QueryStatusAsync(
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
        if (targets.Count == 0)
        {
            SimpleLogger.Log("[LanStatus] 没有可走局域网的设备（缺 IP 或密钥），本次未查询");
            return new LanStatusResult(result, 0, LanAttempt.NoTargets, new List<string>());
        }

        UdpClient udp;
        try
        {
            udp = OpenSocket();
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[LanStatus] 5353 绑定失败({ex.GetType().Name})，本次跳过局域网状态");
            return new LanStatusResult(result, targets.Count, LanAttempt.SocketFailed,
                targets.Values.Select(t => t.Name).ToList());
        }

        // 同一台设备在一轮里会收到好几份应答（组播 + 单播各一份、邻居重发、甚至别人仿的），
        // 谁先到就用谁是错的：先到的很可能是上一轮的旧状态。分两摞收，最后按 seq 挑最新的。
        var strictBest = new Dictionary<string, LanDeviceStatus>();   // 应答形态合格（QR=1 且 TXT 主名是 _ewelink 服务）
        var looseBest = new Dictionary<string, LanDeviceStatus>();    // 读得懂但形态不合格，只兜底用

        using (udp)
        {
            var query = BuildQuery(ServiceName);
            var endpoints = BuildQueryEndpoints(targets.Values, out var skippedEndpoints);
            if (skippedEndpoints > 0)
                SimpleLogger.Log($"[LanStatus] {skippedEndpoints} 台设备的 IP 无法解析，已跳过（不影响其它设备）");

            foreach (var ep in endpoints)
            {
                try { await udp.SendAsync(query, query.Length, ep).ConfigureAwait(false); }
                catch { /* 组播或单播哪条发得出去走哪条，另一条兜 */ }
            }

            var deadline = DateTime.UtcNow + timeout;
            var socketErrors = 0;
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
                catch (SocketException ex)
                {
                    // 不能一遇到收包异常就跳出整个循环：一台设备已下线时，Windows 会针对我们
                    // 发出去的 UDP 回 ICMP port-unreachable，ReceiveAsync 随即抛 SocketException(10054)，
                    // 原来的 `break` 会让整次"刷新状态"作废，界面却说"局域网没有可问的设备"。
                    // 但要限次：如果 socket 真坏了，别在剩余窗口里原地空转刷日志。
                    socketErrors++;
                    SimpleLogger.Log($"[LanStatus] 收包异常({ex.SocketErrorCode})，继续等其它设备");
                    if (socketErrors >= 5) break;
                    continue;
                }
                catch (Exception)
                {
                    break;
                }

                ParsePacket(recv.Buffer, recv.RemoteEndPoint.Address.ToString(), targets, strictBest, looseBest);
            }
        }

        // 两摞里按 seq 取新的那一份：seq 才是"哪份更晚"的证据。主名不合格的那份如果 seq 更新，
        // 说明设备换了广播形态，这时硬守着旧的那份反而会读到过期状态；seq 相同才让合格的赢。
        foreach (var (key, status) in strictBest) result[key] = status;
        var looseChosen = new HashSet<string>();
        foreach (var (key, status) in looseBest)
        {
            if (result.TryGetValue(key, out var current) && !IsNewerSeq(status.Seq, current.Seq)) continue;
            result[key] = status;
            looseChosen.Add(key);
        }
        if (looseChosen.Count > 0)
            SimpleLogger.Log($"[LanStatus] {looseChosen.Count} 台取到的是形态不合格的 mDNS 应答" +
                             "（不是应答包，或 TXT 主名不属于 _ewelink 服务）");

        var unanswered = targets.Values
            .Where(t => !result.ContainsKey(t.DeviceId.ToLowerInvariant()))
            .Select(t => t.Name)
            .ToList();

        SimpleLogger.Log($"[LanStatus] 局域网读到 {result.Count}/{targets.Count} 台：" +
            string.Join(", ", result.Values.Select(v => $"{(targets.TryGetValue(v.DeviceId.ToLowerInvariant(), out var t) ? t.Name : v.DeviceId)}" +
                $"[{string.Join(",", v.Channels.OrderBy(c => c.Key).Select(c => c.Key + (c.Value ? "=on" : "=off")))}] seq={v.Seq}")));
        if (unanswered.Count > 0)
            SimpleLogger.Log($"[LanStatus] 问了没应答 {unanswered.Count} 台：{string.Join(", ", unanswered)}");

        return new LanStatusResult(result, targets.Count,
            result.Count > 0 ? LanAttempt.Read : LanAttempt.NoResponder, unanswered);
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
        Dictionary<string, LanDeviceStatus> strictBest, Dictionary<string, LanDeviceStatus> looseBest)
    {
        if (buf.Length < 12) return;
        // QR=0 是查询包（我们自己发出去又被组播回来的、或邻居发的），就算带着 ANCOUNT 也不能当应答读
        if ((buf[2] & 0x80) == 0) return;

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

            if (type == 16) TryConsumeTxt(buf, pos, rdlen, name, sourceIp, targets, strictBest, looseBest);
            pos += rdlen;
        }
    }

    private static void TryConsumeTxt(byte[] buf, int start, int rdlen, string recordName, string sourceIp,
        Dictionary<string, DeviceInfo> targets, Dictionary<string, LanDeviceStatus> strictBest,
        Dictionary<string, LanDeviceStatus> looseBest)
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

        var data = BuildDataPayload(kv);
        if (data.Length == 0 || !kv.TryGetValue("iv", out var iv)) return;
        if (!kv.TryGetValue("encrypt", out var enc) || !enc.Equals("true", StringComparison.OrdinalIgnoreCase)) return;

        string plain;
        try { plain = AesCrypto.Decrypt(data, device.DeviceKey, iv); }
        catch (Exception) { return; }

        var channels = ReadSwitches(plain);
        if (channels.Count == 0) return;

        int seq = kv.TryGetValue("seq", out var seqText) && Triggers.ComparisonHelper.TryParseInt(seqText, out var parsedSeq) ? parsedSeq : 0;
        var status = new LanDeviceStatus(deviceId, channels, seq, sourceIp);
        // TXT 的主名必须是 _ewelink 服务下的实例名；别的服务（_http、_printer）的 TXT 里
        // 恰好也有 id=/data1= 时不该被当成插座状态，但它仍进兜底那一摞，不至于把真状态说成"没人应答"。
        // 注意比的是去掉尾点的形式：ReadName 解析出来的名字末尾没有那个点。
        var qualified = recordName.EndsWith(ServiceSuffix, StringComparison.OrdinalIgnoreCase);
        RememberBest(qualified ? strictBest : looseBest, deviceId.ToLowerInvariant(), status);
    }

    /// <summary>
    /// seq 到上限会回绕，所以"数值大"不等于"更新"：用半个窗口内的差判断先后。
    /// 直接比大小会在回绕那一刻把新应答判成旧的，然后整天读回回绕前的状态。
    /// </summary>
    private const int SeqWindow = 1 << 16;

    private static bool IsNewerSeq(int candidate, int current)
    {
        int diff = (candidate - current) % SeqWindow;
        if (diff < 0) diff += SeqWindow;
        return diff > 0 && diff < SeqWindow / 2;
    }

    private static void RememberBest(Dictionary<string, LanDeviceStatus> best, string key, LanDeviceStatus status)
    {
        if (!best.TryGetValue(key, out var existing) || IsNewerSeq(status.Seq, existing.Seq))
            best[key] = status;
    }

    /// <summary>
    /// 组播一条 + 每台设备一条单播。IP 解析不出来的只跳过那一台：
    /// 原来是在 Concat 里直接 <c>IPAddress.Parse</c>，配置里一个畸形 IP（带空格、写成 "ip:8081"）
    /// 就抛 FormatException 冲出整个方法，整次局域网状态读取作废，界面只剩一句"读取异常"。
    /// </summary>
    internal static List<IPEndPoint> BuildQueryEndpoints(IEnumerable<DeviceInfo> devices, out int skipped)
    {
        var endpoints = new List<IPEndPoint> { new(MdnsGroup, MdnsPort) };
        skipped = 0;
        foreach (var device in devices)
        {
            // Trim：配置里多一个空格不该让这台设备失去局域网状态读能力
            if (!IPAddress.TryParse(device.IpAddress?.Trim(), out var addr) ||
                addr.AddressFamily != AddressFamily.InterNetwork)
            {
                skipped++;
                continue;
            }
            endpoints.Add(new IPEndPoint(addr, MdnsPort));
        }
        return endpoints;
    }

    /// <summary>
    /// 把 TXT 里的 data1..dataN 按序号拼起来。
    /// 老实现写死 1..8：分片数随参数长度增长，第 9 片起被悄悄截断 ⇒ 解密失败 ⇒
    /// 设备被算成"没应答"，而真实原因是解析器读不懂。
    /// </summary>
    internal static string BuildDataPayload(IReadOnlyDictionary<string, string> txt)
    {
        List<(int Index, string Value)>? chunks = null;
        foreach (var kv in txt)
        {
            if (kv.Key.Length <= 4 || !kv.Key.StartsWith("data", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(kv.Key.AsSpan(4), out var index)) continue;
            (chunks ??= new List<(int, string)>()).Add((index, kv.Value));
        }

        if (chunks == null) return string.Empty;

        chunks.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        var sb = new StringBuilder(chunks.Count * 16);
        foreach (var (_, value) in chunks) sb.Append(value);
        return sb.ToString();
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
