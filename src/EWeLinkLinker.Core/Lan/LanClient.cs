using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EWeLinkLinker.Core.Logging;
using EWeLinkLinker.Core.Models;
using Microsoft.Extensions.Logging;

namespace EWeLinkLinker.Core.Lan;

public class LanClient
{
    private readonly HttpClient _http;
    private readonly ILogger<LanClient>? _logger;

    public LanClient(HttpClient http, ILogger<LanClient>? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<List<DeviceInfo>> DiscoverDevicesAsync(List<DeviceInfo> knownDevices, CancellationToken ct = default)
    {
        var subnet = DetectLocalSubnet();
        var needDiscovery = knownDevices.Count(d => string.IsNullOrEmpty(d.IpAddress) && d.HasLocalMac);
        SimpleLogger.Log($"[Discovery] Start: {knownDevices.Count} devices, {needDiscovery} need IP, subnet={subnet ?? "none"}");

        // Step 1: Ping sweep + ARP（只是把邻居塞进 ARP 缓存，失败不影响后续）
        if (!string.IsNullOrEmpty(subnet))
        {
            await PingSweepAsync(subnet);
        }

        var arpTable = await GetArpTableAsync(ct);

        // Step 2: MAC matching
        var assignedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in knownDevices)
        {
            if (!string.IsNullOrEmpty(device.IpAddress))
                assignedIps.Add(device.IpAddress);
        }

        int matched = 0, notFound = 0;
        foreach (var device in knownDevices)
        {
            if (!string.IsNullOrEmpty(device.IpAddress) || !device.HasLocalMac)
                continue;

            var effectiveMac = LanDiscoveryParsing.NormalizeMac(device.EffectiveMac);
            if (effectiveMac.Length == 0)
            {
                // 真实 MAC 填成 "AA:BB:CC" 这种也会走到这里：两边归一后都是空串，
                // 以前会让"空 == 空"配上随便一个解析失败的 ARP 行，等于把 IP 配错。
                notFound++;
                SimpleLogger.Log($"[Discovery] {device.Name}: MAC 不是有效的 12 位十六进制，跳过匹配");
                continue;
            }

            var matchingEntry = arpTable.FirstOrDefault(e =>
                e.Value.Equals(effectiveMac, StringComparison.OrdinalIgnoreCase) &&
                !assignedIps.Contains(e.Key));

            if (!string.IsNullOrEmpty(matchingEntry.Key))
            {
                device.IpAddress = matchingEntry.Key;
                assignedIps.Add(matchingEntry.Key);
                matched++;
            }
            else
            {
                notFound++;
                var macSource = !string.IsNullOrEmpty(device.RealMacAddress) ? "RealMac" : "CloudMac";
                SimpleLogger.Log($"[Discovery] {device.Name}: {macSource} not found in ARP ({arpTable.Count} entries)");
            }
        }

        // Step 3: TCP port scan for remaining devices
        var stillUnmatched = knownDevices.Where(d =>
            string.IsNullOrEmpty(d.IpAddress) && d.IsOnline && d.HasLocalMac).ToList();
        var tcpMatched = 0;
        if (stillUnmatched.Count > 0 && !string.IsNullOrEmpty(subnet))
        {
            SimpleLogger.Log($"[Discovery] TCP scan for {stillUnmatched.Count} unmatched devices...");
            try
            {
                tcpMatched = await TcpPortScanAsync(subnet, stillUnmatched, ct);
            }
            catch (OperationCanceledException)
            {
                // 扫描是尽力而为：超时/取消不该让整次刷新失败——ARP 已经匹配到的设备是有效结果。
                // 原来这个异常会一路抛到界面上，用户看到"刷新失败"，其实大部分设备已经拿到了。
                SimpleLogger.Log("[Discovery] TCP 扫描超时/取消，保留已匹配到的结果");
            }
        }

        var stillNoIp = knownDevices.Count(d => string.IsNullOrEmpty(d.IpAddress));
        // 原来这里打的是扫描前的快照数，跟 TCP 实际认领了几台无关——运维据此判断会得到反的结论
        SimpleLogger.Log($"[Discovery] Done: {matched} matched via ARP, {tcpMatched} via TCP, {stillNoIp} 仍无 IP");

        // Final summary
        var foundCount = knownDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
        var cloudOnlyCount = knownDevices.Count(d => string.IsNullOrEmpty(d.IpAddress) && !d.HasLocalMac);
        SimpleLogger.Log($"[Discovery] Result: {foundCount}/{knownDevices.Count} with IP, {cloudOnlyCount} cloud-only, {notFound} MAC 未命中");
        return knownDevices;
    }

    /// <summary>
    /// 起一个子进程并等它，超过 timeout（或 ct 取消）就杀掉并把 null 还给调用方。
    /// 抽出来是因为 <c>arp -a</c> 原来是裸的 <c>WaitForExitAsync()</c>：没有超时、没有取消，
    /// arp 一旦卡住，"刷新IP"就永久挂起，而界面上只有一句没有进度提示的"刷新中"。
    /// </summary>
    internal static async Task<string?> RunProcessWithTimeoutAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            if (!process.Start()) return null;

            // 先开始读，再等退出：反过来会在输出缓冲区填满时互等
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                SimpleLogger.Log($"[Discovery] {fileName} 超过 {timeout.TotalSeconds:F0}s 未返回，已终止");
                return null;
            }

            return await stdoutTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[Discovery] {fileName} 执行失败: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 取一块网卡快照交给 <see cref="LanDiscoveryParsing.SelectSubnet"/> 挑网段。
    /// 挑选规则（虚拟网卡优先剔除、网关优先）在纯函数里，可单测；这里只负责读真实网卡。
    /// 枚举失败不再静默：以前 catch { } 之后盲扫 192.168.1 段，用户只看到"刷新IP 没找到设备"。
    /// </summary>
    private static string? DetectLocalSubnet()
    {
        try
        {
            var adapters = new List<NetAdapter>();
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = iface.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault();
                var hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null && !g.Address.Equals(IPAddress.Any));

                adapters.Add(new NetAdapter(iface.Name, iface.Description, iface.NetworkInterfaceType,
                                            iface.OperationalStatus, ipv4, hasGateway));
            }

            var subnet = LanDiscoveryParsing.SelectSubnet(adapters);
            if (subnet == null)
                SimpleLogger.Log("[Discovery] 没有可用的 IPv4 网卡，本次跳过网段扫描（只看 ARP 现有条目）");
            return subnet;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[Discovery] 网卡枚举失败: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task PingSweepAsync(string subnet)
    {
        var tasks = new List<Task>();
        var pingOptions = new PingOptions { DontFragment = true };
        var buffer = Encoding.UTF8.GetBytes("ewelink-discovery");
        int successCount = 0;

        using var semaphore = new System.Threading.SemaphoreSlim(50);

        for (int i = 1; i <= 254; i++)
        {
            var ip = $"{subnet}.{i}";
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, 200, buffer, pingOptions);
                    if (reply.Status == IPStatus.Success)
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
                catch { }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
        // 这个计数原来只增不读（死状态）。打出来才有用：能区分"网段选错了（0 台应答）"
        // 和"网段对但设备都不在线"，排障时是两条完全不同的结论。
        SimpleLogger.Log($"[Discovery] ping 扫段 {subnet}.0/24: {Volatile.Read(ref successCount)} 台有应答");
        await Task.Delay(300);
    }

    private async Task<Dictionary<string, string>> GetArpTableAsync(CancellationToken ct = default)
    {
        // 5 秒上限：arp 本身是本地命令，正常情况下几十毫秒返回；卡住时宁可没有 ARP 表，
        // 也不能让"刷新IP"一直转圈。解析逻辑搬到了 LanDiscoveryParsing（可单测）。
        var output = await RunProcessWithTimeoutAsync("arp", "-a", TimeSpan.FromSeconds(5), ct)
            .ConfigureAwait(false);

        if (output == null)
            _logger?.LogWarning("Failed to get ARP table (timeout or error)");

        return LanDiscoveryParsing.ParseArpOutput(output);
    }

    /// <summary>
    /// 扫 8081 端口并把开放端口与"还没 IP 的设备"对上。返回这次 TCP 认领到的台数。
    /// </summary>
    private async Task<int> TcpPortScanAsync(string subnet, List<DeviceInfo> unmatchedDevices, CancellationToken ct = default)
    {
        using var sem = new System.Threading.SemaphoreSlim(50);
        var openPorts = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(TimeSpan.FromSeconds(30));
        var portTasks = Enumerable.Range(1, 254).Select(i => $"{subnet}.{i}").Select(ip => Task.Run(async () =>
        {
            TcpClient? client = null;
            var acquired = false;
            try
            {
                // WaitAsync 必须在 try 里：原来它在外面，30 秒预算一到就抛 OperationCanceledException，
                // 从 lambda 里逃出去，Task.WhenAll 把它聚合成"整次发现失败"——界面上是"刷新失败"，
                // 而 ARP 已经匹配到的设备其实都是有效结果。
                await sem.WaitAsync(scanCts.Token);
                acquired = true;

                client = new TcpClient();
                var connectTask = client.ConnectAsync(ip, 8081, scanCts.Token).AsTask();
                var delayTask = Task.Delay(300, scanCts.Token);
                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask && client.Connected)
                {
                    openPorts.Add(ip);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                client?.Dispose();
                if (acquired) sem.Release();
            }
        }, scanCts.Token));

        try
        {
            await Task.WhenAll(portTasks);
        }
        catch (OperationCanceledException)
        {
            SimpleLogger.Log($"[Discovery] 端口扫描 30s 预算用尽，按已扫到的 {openPorts.Count} 个端口继续");
        }

        if (openPorts.Count == 0) return 0;

        var claimedIps = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var probeTasks = new List<Task>();
        using var probeSem = new System.Threading.SemaphoreSlim(5);

        foreach (var ip in openPorts)
        {
            foreach (var device in unmatchedDevices.Where(d =>
                !string.IsNullOrEmpty(d.DeviceKey) && string.IsNullOrEmpty(d.IpAddress)))
            {
                probeTasks.Add(Task.Run(async () =>
                {
                    await probeSem.WaitAsync();
                    try
                    {
                        if (claimedIps.ContainsKey(ip)) return;

                        var identified = await ProbeDeviceAsync(ip, device);
                        if (identified)
                        {
                            if (claimedIps.TryAdd(ip, device.DeviceId))
                            {
                                device.IpAddress = ip;
                            }
                        }
                    }
                    catch { }
                    finally { probeSem.Release(); }
                }));
            }
        }

        await Task.WhenAll(probeTasks);

        // 进来时这些设备都没有 IP，现在有 IP 的就是这一次 TCP 认领到的
        var tcpMatched = unmatchedDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
        var stillUnmatchedCount = unmatchedDevices.Count - tcpMatched;
        if (stillUnmatchedCount > 0)
        {
            SimpleLogger.Log($"[Discovery] TCP: {stillUnmatchedCount} devices could not be identified");
        }

        return tcpMatched;
    }

    private static async Task<bool> ProbeDeviceAsync(string ip, DeviceInfo device)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            var connectTask = client.ConnectAsync(ip, 8081);
            var timeoutTask = Task.Delay(500);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask || !client.Connected)
                return false;

            var data = new { };
            var dataJson = JsonSerializer.Serialize(data);
            var (encryptedData, iv) = AesCrypto.Encrypt(dataJson, device.DeviceKey);

            var payload = new
            {
                sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
                deviceid = device.DeviceId,
                selfApikey = "123",
                encrypt = true,
                data = encryptedData,
                iv
            };

            var json = JsonSerializer.Serialize(payload);
            var request = $"POST /zeroconf/getState HTTP/1.1\r\n" +
                         $"Host: {ip}:8081\r\n" +
                         $"Content-Type: application/json\r\n" +
                         $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n" +
                         $"Connection: close\r\n" +
                         $"\r\n" +
                         json;

            var bytes = Encoding.UTF8.GetBytes(request);
            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);

            // H-? 修复：循环读取完整 HTTP 响应，避免 TCP 分段截断
            var fullResponse = await ReadHttpResponseAsync(client);
            if (fullResponse == null) return false;

            var bodyStart = fullResponse.IndexOf("\r\n\r\n");
            if (bodyStart < 0) return false;
            var body = fullResponse[(bodyStart + 4)..].Trim();
            if (string.IsNullOrEmpty(body)) return false;

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("encrypt", out var enc) && enc.GetBoolean() &&
                doc.RootElement.TryGetProperty("data", out var dataProp))
            {
                var responseIv = doc.RootElement.TryGetProperty("iv", out var ivProp) ? ivProp.GetString() ?? "" : "";
                var encrypted = dataProp.GetString() ?? "";

                if (!string.IsNullOrEmpty(encrypted) && !string.IsNullOrEmpty(responseIv))
                {
                    string decryptedJson;
                    try
                    {
                        decryptedJson = AesCrypto.Decrypt(encrypted, device.DeviceKey, responseIv);
                    }
                    catch
                    {
                        // 解密失败 → 该 IP 不是此设备
                        return false;
                    }

                    // H-? 修复：额外验证解密后的 JSON 中包含目标 deviceid
                    // 防止不同设备巧合通过 PKCS7 padding 校验（概率约 1/256）
                    try
                    {
                        // 解析 JSON 验证 deviceid 精确匹配，防止 Contains 假阳性
                        using var decryptedDoc = JsonDocument.Parse(decryptedJson);
                        if (decryptedDoc.RootElement.TryGetProperty("deviceid", out var did) &&
                            did.GetString() == device.DeviceId)
                            return true;
                    }
                    catch
                    {
                        // 解密后内容非合法 JSON → 不是此设备
                        return false;
                    }
                }
            }

            return false;
        }
        catch { }
        finally
        {
            client?.Dispose();
        }

        return false;
    }

    /// <summary>
    /// H-? 修复：循环读取 TCP 流直到获取完整 HTTP 响应。
    /// 防止 TCP 分段 + 4096 缓冲区不够导致截断。
    /// 支持 Content-Length 模式，没有 Content-Length 时读取到连接关闭。
    /// </summary>
    private static async Task<string?> ReadHttpResponseAsync(TcpClient client)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stream = client.GetStream();
        var buffer = new byte[16384]; // 16KB 缓冲区，减少循环读取次数
        var sb = new StringBuilder();
        int contentLength = -1;

        // 循环读取，从头部开始
        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cts.Token);
            if (bytesRead == 0) return sb.Length > 0 ? sb.ToString() : null;

            sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            var current = sb.ToString();

            if (contentLength < 0)
            {
                var headerEnd = current.IndexOf("\r\n\r\n");
                if (headerEnd < 0)
                {
                    if (sb.Length > 16384) return null; // 头部太大，防畸形响应
                    continue;
                }

                // 解析 Content-Length
                var headers = current[..headerEnd];
                foreach (var line in headers.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        Triggers.ComparisonHelper.TryParseInt(trimmed["Content-Length:".Length..].Trim(), out contentLength);
                    }
                }
            }

            if (contentLength > 0)
            {
                var bodyOffset = current.IndexOf("\r\n\r\n") + 4;
                var bodyRead = current.Length - bodyOffset;
                if (bodyRead >= contentLength) break;

                // 继续读身体
                var remaining = contentLength - bodyRead;
                var toRead = (int)Math.Min(remaining, buffer.Length);
                bytesRead = await stream.ReadAsync(buffer, 0, toRead, cts.Token);
                if (bytesRead == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            }
            else
            {
                // 没有 Content-Length（Transfer-Encoding: chunked 或 Connection: close）
                // 循环读直到连接关闭
                var moreBytes = await stream.ReadAsync(buffer, cts.Token);
                if (moreBytes == 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, moreBytes));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 一条 LAN 命令的送达结论。分不出"没送到"和"送到了没回话"，重试就会把同一条命令发两遍。
    /// </summary>
    private enum SendStatus
    {
        /// <summary>设备确认收到（HTTP 200 且 error 为 0，或老固件 200 空 body）</summary>
        Delivered,
        /// <summary>明确没执行（连不上、非 2xx、设备报错）——补发是安全的</summary>
        NotSent,
        /// <summary>请求已经发出去但没拿到确认（超时）——补发等于把命令再执行一遍</summary>
        Unacked,
    }

    private async Task<SendStatus> SendPowerAsync(DeviceInfo device, bool turnOn, int outlet, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(device.IpAddress))
        {
            _logger?.LogWarning("Device {DeviceName} has no IP address", device.Name);
            return SendStatus.NotSent;
        }

        var state = turnOn ? "on" : "off";
        var data = new { switches = new[] { new { outlet, @switch = state } } };
        var dataJson = JsonSerializer.Serialize(data);

        var (encryptedData, iv) = AesCrypto.Encrypt(dataJson, device.DeviceKey);

        var requestBody = new
        {
            sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            deviceid = device.DeviceId,
            selfApikey = "123",
            encrypt = true,
            data = encryptedData,
            iv
        };

        try
        {
            var requestJson = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"http://{device.IpAddress}:8081/zeroconf/switches")
            {
                Content = content
            };

            var response = await _http.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync();

            // 设备会用"非 2xx + 空 body"表达拒绝。不判状态码就会把没做成的事记成成功，
            // 既不重试，[AUDIT] 留痕也跟着骗人
            if (!response.IsSuccessStatusCode)
            {
                SimpleLogger.Log($"[LAN] {device.Name} HTTP {(int)response.StatusCode} rejected: {Shorten(json)}");
                return SendStatus.NotSent;
            }

            // 区分 HTTP 路径和 Socket 路径的空响应语义：
            // HttpClient 路径收到 HTTP 200（即使空 body）说明设备已接收请求。
            // Socket 路径的空响应由 ReadHttpResponseAsync 返回 null 处理。
            if (string.IsNullOrEmpty(json))
                return SendStatus.Delivered; // 老固件不返回响应体，但命令已执行

            using var doc = JsonDocument.Parse(json);
            if (TryGetDeviceError(doc.RootElement, out var code, out var msg))
            {
                // eWeLink 把业务错误装在 HTTP 200 里：不记 error 和 msg 的话，
                // 日志只有一句"失败"，分不清是密钥不对、通道号越界还是设备正忙
                var ok = IsErrorZero(code);
                SimpleLogger.Log($"[LAN] {device.Name} HTTP 200 error={code}" +
                                 (ok ? "" : $" msg={Shorten(msg)}") + $" → {(ok ? "确认收到" : "设备拒绝")}");
                return ok ? SendStatus.Delivered : SendStatus.NotSent;
            }
            return SendStatus.Delivered;
        }
        catch (HttpRequestException ex)
        {
            // 连不上/连接被拒：命令根本没送到设备，走 socket 补一次是安全的
            SimpleLogger.Log($"[LAN] {device.Name} HTTP error: {ex.Message}");
            if (ct.IsCancellationRequested) return SendStatus.NotSent;   // 预算已经用完，不再另开一条路
            return await SendViaSocketAsync(device, requestBody, ct);
        }
        catch (TaskCanceledException)
        {
            // 超时不等于"没送到"：请求已经发出去了，设备很可能已经执行、只是没回话。
            // 原来这里再用裸 socket 补发一次 ⇒ 同一条命令发两遍；外层 SetPowerWithRetryAsync
            // 还会再补一遍。现在按"已发出未确认"返回，重试逻辑见到它就停手。
            var why = ct.IsCancellationRequested
                ? "整体预算到点（关机/睡眠来不及等）"
                : $"HTTP 超时（{_http.Timeout.TotalSeconds:F0}s）";
            SimpleLogger.Log($"[LAN] {device.Name} {why}，命令可能已被设备执行，不再补发 → 判定失败（未确认）");
            return SendStatus.Unacked;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[LAN] {device.Name} error: {ex.Message}");
            return SendStatus.NotSent;
        }
    }

    /// <summary>
    /// 取出设备返回体里的 error 码和 msg。不同固件的 error 有给数字的、有给字符串的（"4002"），
    /// 用 GetInt32 直接解会在字符串型上抛异常 ⇒ 被外层 catch 吞成一次普通失败，码和 msg 都丢了。
    /// </summary>
    private static bool TryGetDeviceError(JsonElement root, out string code, out string msg)
    {
        code = string.Empty;
        msg = string.Empty;
        if (!root.TryGetProperty("error", out var error)) return false;

        code = error.ValueKind switch
        {
            JsonValueKind.String => error.GetString() ?? string.Empty,
            JsonValueKind.Number => error.GetRawText(),
            _ => error.GetRawText(),
        };
        if (root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
            msg = m.GetString() ?? string.Empty;
        return true;
    }

    private static bool IsErrorZero(string code) =>
        double.TryParse(code, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == 0;

    private static string Shorten(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        return text.Length > 120 ? text[..120] : text;
    }

    private async Task<SendStatus> SendViaSocketAsync(DeviceInfo device, object requestBody, CancellationToken ct)
    {
        var written = false;
        try
        {
            using var client = new TcpClient();
            // 5 秒是这条路自己的上限，整体预算（关机/睡眠）更紧时以预算为准
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(device.IpAddress, 8081, cts.Token);

            var requestJson = JsonSerializer.Serialize(requestBody);
            var httpRequest = $"POST /zeroconf/switches HTTP/1.1\r\n" +
                              $"Host: {device.IpAddress}:8081\r\n" +
                              $"Content-Type: application/json\r\n" +
                              $"Content-Length: {Encoding.UTF8.GetByteCount(requestJson)}\r\n" +
                              $"Connection: close\r\n" +
                              $"\r\n" +
                              requestJson;

            var bytes = Encoding.UTF8.GetBytes(httpRequest);
            await client.GetStream().WriteAsync(bytes, cts.Token);
            written = true;

            // 使用 ReadHttpResponseAsync 循环读取完整响应
            var fullResponse = await ReadHttpResponseAsync(client);
            if (fullResponse == null) return SendStatus.NotSent;

            if (fullResponse.StartsWith("HTTP/1.1 200") || fullResponse.StartsWith("HTTP/1.0 200"))
            {
                var bodyStart = fullResponse.IndexOf("\r\n\r\n");
                if (bodyStart >= 0)
                {
                    var jsonBody = fullResponse[(bodyStart + 4)..].Trim();
                    // 与 HTTP 路径一致：已确认是 200，老固件不返 body，命令其实执行了
                    if (string.IsNullOrEmpty(jsonBody)) return SendStatus.Delivered;
                    try
                    {
                        // 与 HTTP 路径一致，用 JSON 解析判断 error 字段
                        using var doc = JsonDocument.Parse(jsonBody);
                        if (TryGetDeviceError(doc.RootElement, out var code, out var msg))
                        {
                            var ok = IsErrorZero(code);
                            SimpleLogger.Log($"[LAN] {device.Name} socket 200 error={code}" +
                                             (ok ? "" : $" msg={Shorten(msg)}") +
                                             $" → {(ok ? "确认收到" : "设备拒绝")}");
                            return ok ? SendStatus.Delivered : SendStatus.NotSent;
                        }
                    }
                    catch (JsonException) { }
                    return SendStatus.Delivered; // 200 且无 error 字段，视为成功
                }
                return SendStatus.Delivered;
            }

            return SendStatus.NotSent;
        }
        catch (OperationCanceledException)
        {
            // 写出去之后才超时 = 设备可能已经在执行，和 HTTP 超时同一处理
            SimpleLogger.Log($"[LAN] {device.Name} socket timeout" +
                             (written ? "（命令已写出，未确认，不再补发）" : "（连接阶段，命令未发出）"));
            return written ? SendStatus.Unacked : SendStatus.NotSent;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"[LAN] {device.Name} socket error: {ex.Message}");
            return written ? SendStatus.Unacked : SendStatus.NotSent;
        }
    }

    /// <summary>
    /// 下发一条开/关。ct 是"整体预算"（关机/睡眠只有几秒），预算到点就一台都不再开：
    /// 原来这里没有预算概念，五台设备各奔着 5 秒超时+重试去，系统在 3.5 秒外把进程杀掉，
    /// 日志里既没说哪台没发、也没说为什么没发。
    /// </summary>
    public async Task<bool> SetPowerWithRetryAsync(DeviceInfo device, bool turnOn, int outlet = 0,
                                                   int maxRetries = 1, CancellationToken ct = default)
    {
        for (int i = 0; i <= maxRetries; i++)
        {
            if (ct.IsCancellationRequested)
            {
                SimpleLogger.Log($"[LAN] {device.Name} 未发出（预算已用完，这一台一次都没尝试）");
                return false;
            }
            var status = await SendPowerAsync(device, turnOn, outlet, ct);
            if (status == SendStatus.Delivered) return true;

            if (status == SendStatus.Unacked)
            {
                // 命令已经送到设备门口、只是没等到回话：再发一遍就是把同一条开/关执行两次。
                // 这里停手，按失败上报（[AUDIT] 会留 fail），让下一轮判定或人工去纠偏。
                SimpleLogger.Log($"[LAN] {device.Name} 已发出未确认，跳过重试");
                return false;
            }
            if (i < maxRetries)
            {
                if (ct.IsCancellationRequested)
                {
                    SimpleLogger.Log($"[LAN] {device.Name} 未重试（预算已用完）");
                    return false;
                }
                await Task.Delay(500, CancellationToken.None);
            }
        }
        return false;
    }
}