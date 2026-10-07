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

    public async Task<List<DeviceInfo>> DiscoverDevicesAsync(List<DeviceInfo> knownDevices)
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
        Config.LinkerConfig.TrimLogFile(logPath, 2_097_152);

        var subnet = DetectLocalSubnet();
        var needDiscovery = knownDevices.Count(d => string.IsNullOrEmpty(d.IpAddress) && d.HasLocalMac);
        SimpleLogger.Log($"[Discovery] Start: {knownDevices.Count} devices, {needDiscovery} need IP, subnet={subnet ?? "none"}");

        // Step 1: Ping sweep + ARP
        if (!string.IsNullOrEmpty(subnet))
        {
            await PingSweepAsync(subnet);
        }

        var arpTable = await GetArpTableAsync();

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

            var effectiveMac = NormalizeMac(device.EffectiveMac);
            var matchingEntry = arpTable.FirstOrDefault(e =>
                NormalizeMac(e.Value).Equals(effectiveMac, StringComparison.OrdinalIgnoreCase) &&
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
        if (stillUnmatched.Count > 0 && !string.IsNullOrEmpty(subnet))
        {
            SimpleLogger.Log($"[Discovery] TCP scan for {stillUnmatched.Count} unmatched devices...");
            await TcpPortScanAsync(subnet, stillUnmatched);
        }

        SimpleLogger.Log($"[Discovery] Done: {matched} matched via ARP, {stillUnmatched.Count} via TCP");

        // Final summary
        var foundCount = knownDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
        var cloudOnlyCount = knownDevices.Count(d => string.IsNullOrEmpty(d.IpAddress) && !d.HasLocalMac);
        SimpleLogger.Log($"[Discovery] Result: {foundCount}/{knownDevices.Count} with IP, {cloudOnlyCount} cloud-only");
        return knownDevices;
    }

    private static string NormalizeMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        var clean = mac.Replace(":", "").Replace("-", "").Replace(".", "").Replace(" ", "").ToLowerInvariant();
        if (clean.Length == 12 && clean.All(c => "0123456789abcdef".Contains(c)))
            return clean;
        return string.Empty;
    }

    private static string DetectLocalSubnet()
    {
        try
        {
            // 第一遍：优先选择带默认网关的 IPv4 接口（真实上网网卡），
            // 避免 VPN / Hyper-V 虚拟网卡（Up 状态但无网关）被误选
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = iface.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null && !g.Address.Equals(IPAddress.Any));
                if (!hasGateway) continue;

                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;

                    var parts = ip.Split('.');
                    if (parts.Length == 4)
                        return $"{parts[0]}.{parts[1]}.{parts[2]}";
                }
            }

            // 回退：任意 Up 状态的 IPv4 接口
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = addr.Address.ToString();
                    // H-? 修复：仅排除已知的虚拟网卡段，不再硬编码排除 192.168.80/84
                    // 同时保留 169.254.x.x APIPA 段的排除
                    if (ip.StartsWith("169.254."))
                        continue;

                    var parts = ip.Split('.');
                    if (parts.Length == 4)
                    {
                        return $"{parts[0]}.{parts[1]}.{parts[2]}";
                    }
                }
            }
        }
        catch { }

        return "192.168.1";
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
        await Task.Delay(300);
    }

    private async Task<Dictionary<string, string>> GetArpTableAsync()
    {
        var arpTable = new Dictionary<string, string>();

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("Interface") || trimmed.StartsWith("接口")
                    || trimmed.StartsWith("Internet")) continue;

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var ip = parts[0];
                    var mac = parts[1].Replace("-", ":").Replace(".", ":");

                    if (IPAddress.TryParse(ip, out var parsed) &&
                        parsed.AddressFamily == AddressFamily.InterNetwork &&
                        (mac.Contains(':') || mac.Length == 17))
                    {
                        arpTable[ip] = mac.ToLower();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get ARP table");
        }

        return arpTable;
    }

    private async Task TcpPortScanAsync(string subnet, List<DeviceInfo> unmatchedDevices)
    {
        var alreadyMatchedIps = new HashSet<string>(
            unmatchedDevices.Where(d => !string.IsNullOrEmpty(d.IpAddress)).Select(d => d.IpAddress!));

        using var sem = new System.Threading.SemaphoreSlim(50);
        var openPorts = new System.Collections.Concurrent.ConcurrentBag<string>();

        using var scanCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        var portTasks = Enumerable.Range(1, 254).Select(i => $"{subnet}.{i}").Select(ip => Task.Run(async () =>
        {
            if (alreadyMatchedIps.Contains(ip)) return;
            await sem.WaitAsync(scanCts.Token);
            TcpClient? client = null;
            try
            {
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
                sem.Release();
            }
        }, scanCts.Token));
        await Task.WhenAll(portTasks);

        if (openPorts.Count == 0) return;

        var claimedIps = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var probeTasks = new List<Task>();
        var probeSem = new System.Threading.SemaphoreSlim(5);

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

        var stillUnmatchedCount = unmatchedDevices.Count(d => string.IsNullOrEmpty(d.IpAddress));
        if (stillUnmatchedCount > 0)
        {
            SimpleLogger.Log($"[Discovery] TCP: {stillUnmatchedCount} devices could not be identified");
        }
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

    private async Task<SendStatus> SendPowerAsync(DeviceInfo device, bool turnOn, int outlet)
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

            var response = await _http.SendAsync(request);
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
            return await SendViaSocketAsync(device, requestBody);
        }
        catch (TaskCanceledException)
        {
            // 超时不等于"没送到"：请求已经发出去了，设备很可能已经执行、只是没回话。
            // 原来这里再用裸 socket 补发一次 ⇒ 同一条命令发两遍；外层 SetPowerWithRetryAsync
            // 还会再补一遍。现在按"已发出未确认"返回，重试逻辑见到它就停手。
            SimpleLogger.Log($"[LAN] {device.Name} HTTP 超时（{_http.Timeout.TotalSeconds:F0}s），" +
                             "命令可能已被设备执行，不再补发 → 判定失败（未确认）");
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

    private async Task<SendStatus> SendViaSocketAsync(DeviceInfo device, object requestBody)
    {
        var written = false;
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    public async Task<bool> SetPowerWithRetryAsync(DeviceInfo device, bool turnOn, int outlet = 0, int maxRetries = 1)
    {
        for (int i = 0; i <= maxRetries; i++)
        {
            var status = await SendPowerAsync(device, turnOn, outlet);
            if (status == SendStatus.Delivered) return true;

            if (status == SendStatus.Unacked)
            {
                // 命令已经送到设备门口、只是没等到回话：再发一遍就是把同一条开/关执行两次。
                // 这里停手，按失败上报（[AUDIT] 会留 fail），让下一轮判定或人工去纠偏。
                SimpleLogger.Log($"[LAN] {device.Name} 已发出未确认，跳过重试");
                return false;
            }
            if (i < maxRetries) await Task.Delay(500);
        }
        return false;
    }
}