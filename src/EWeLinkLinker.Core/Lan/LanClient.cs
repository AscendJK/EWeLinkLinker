using System.Diagnostics;
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
        SimpleLogger.Log("===== Device Discovery Start =====");

        var subnet = DetectLocalSubnet();
        SimpleLogger.Log($"Subnet: {subnet}");
        SimpleLogger.Log($"Devices to discover: {knownDevices.Count}");

        foreach (var device in knownDevices)
        {
            var status = string.IsNullOrEmpty(device.IpAddress) ? "需要发现IP" : $"已有IP={device.IpAddress}";
            var macInfo = device.HasLocalMac
                ? $"CloudMAC={device.MacAddress}" + (!string.IsNullOrEmpty(device.RealMacAddress) ? $", RealMAC={device.RealMacAddress}" : "")
                : "MAC全零(仅云端)";
            SimpleLogger.Log($"  {device.Name} ({device.DeviceId}): {status} | {macInfo}");
        }

        // Step 1: Ping sweep + ARP
        if (!string.IsNullOrEmpty(subnet))
        {
            SimpleLogger.Log($"Step 1: Ping sweeping {subnet}.1-254 (50 concurrent, 200ms timeout)...");
            await PingSweepAsync(subnet);
        }

        var arpTable = await GetArpTableAsync();
        SimpleLogger.Log($"ARP table: {arpTable.Count} entries");
        foreach (var entry in arpTable)
        {
            SimpleLogger.Log($"  ARP: {entry.Key} -> {entry.Value}");
        }

        // Step 2: MAC matching
        SimpleLogger.Log("Step 2: MAC matching...");
        // 跟踪已分配的 IP，防止重复分配给多个设备
        var assignedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先收集已有的 IP 分配
        foreach (var device in knownDevices)
        {
            if (!string.IsNullOrEmpty(device.IpAddress))
            {
                assignedIps.Add(device.IpAddress);
            }
        }

        foreach (var device in knownDevices)
        {
            if (!string.IsNullOrEmpty(device.IpAddress))
            {
                SimpleLogger.Log($"  {device.Name}: SKIP (already has IP={device.IpAddress})");
                continue;
            }
            if (!device.HasLocalMac)
            {
                SimpleLogger.Log($"  {device.Name}: SKIP (cloud-only device, no local MAC)");
                continue;
            }

            var effectiveMac = NormalizeMac(device.EffectiveMac);
            var macSource = !string.IsNullOrEmpty(device.RealMacAddress) ? "RealMac" : "CloudMac";
            SimpleLogger.Log($"  {device.Name}: Trying {macSource}={device.EffectiveMac} (normalized={effectiveMac})");

            // 查找 MAC 匹配的 IP，且该 IP 未被其他设备占用
            var matchingEntry = arpTable.FirstOrDefault(e =>
                NormalizeMac(e.Value).Equals(effectiveMac, StringComparison.OrdinalIgnoreCase) &&
                !assignedIps.Contains(e.Key));

            if (!string.IsNullOrEmpty(matchingEntry.Key))
            {
                device.IpAddress = matchingEntry.Key;
                assignedIps.Add(matchingEntry.Key);  // 标记为已占用
                SimpleLogger.Log($"  {device.Name}: MATCHED -> {matchingEntry.Key}");
            }
            else
            {
                // 检查是否是因为 IP 已被占用
                var macMatchButOccupied = arpTable.FirstOrDefault(e =>
                    NormalizeMac(e.Value).Equals(effectiveMac, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(macMatchButOccupied.Key))
                {
                    SimpleLogger.Log($"  {device.Name}: MAC found but IP {macMatchButOccupied.Key} already assigned to another device");
                }
                else
                {
                    SimpleLogger.Log($"  {device.Name}: NOT FOUND in ARP table");
                }
            }
        }

        // Step 3: TCP port scan for remaining devices
        var stillUnmatched = knownDevices.Where(d =>
            string.IsNullOrEmpty(d.IpAddress) && d.IsOnline && d.HasLocalMac).ToList();
        if (stillUnmatched.Count > 0 && !string.IsNullOrEmpty(subnet))
        {
            SimpleLogger.Log($"Step 3: TCP port scan ({stillUnmatched.Count} unmatched devices)...");
            await TcpPortScanAsync(subnet, stillUnmatched);
        }

        // Log cloud-only devices
        var cloudOnly = knownDevices.Where(d => string.IsNullOrEmpty(d.IpAddress) && !d.HasLocalMac).ToList();
        foreach (var device in cloudOnly)
        {
            SimpleLogger.Log($"  {device.Name}: 仅云端设备 (无本地API)");
        }

        // Final summary
        SimpleLogger.Log("===== Discovery Result =====");
        foreach (var device in knownDevices)
        {
            var result = string.IsNullOrEmpty(device.IpAddress) ? "未找到" : device.IpAddress;
            SimpleLogger.Log($"  {device.Name}: {result}");
        }
        var foundCount = knownDevices.Count(d => !string.IsNullOrEmpty(d.IpAddress));
        SimpleLogger.Log($"Total: {foundCount}/{knownDevices.Count} devices with IPs");
        SimpleLogger.Log("===== Device Discovery End =====\n");
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
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ip = addr.Address.ToString();
                    if (ip.StartsWith("169.254.") || ip.StartsWith("192.168.80.") || ip.StartsWith("192.168.84."))
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
                        SimpleLogger.Log($"  Ping OK: {ip} (TTL={reply.Options?.Ttl})");
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
        SimpleLogger.Log($"Ping sweep done: {successCount}/254 responded");
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
                    // 修复：确保使用 UTF-8 编码支持中文系统
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

                // 修复：兼容中英文 ARP 表头
                // 中文: "接口: 192.168.1.1 --- 0x..."
                // 英文: "Interface: 192.168.1.1 --- 0x..."
                if (trimmed.StartsWith("Interface") || trimmed.StartsWith("接口")
                    || trimmed.StartsWith("Internet")) continue;

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    var ip = parts[0];
                    // MAC 地址可能是 xx-xx-xx-xx-xx-xx 或 xx:xx:xx:xx:xx:xx 格式
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

        SimpleLogger.Log($"TCP: Scanning {subnet}.1-254:8081...");
        using var scanCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
        var portTasks = Enumerable.Range(1, 254).Select(i => $"{subnet}.{i}").Select(ip => Task.Run(async () =>
        {
            if (alreadyMatchedIps.Contains(ip)) return;
            await sem.WaitAsync(scanCts.Token);
            TcpClient? client = null;
            try
            {
                client = new TcpClient();
                // 修复：使用 scanCts.Token 取消连接
                var connectTask = client.ConnectAsync(ip, 8081, scanCts.Token).AsTask();
                var delayTask = Task.Delay(300, scanCts.Token);
                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask && client.Connected)
                {
                    openPorts.Add(ip);
                    SimpleLogger.Log($"TCP: {ip}:8081 OPEN");
                }
            }
            catch (OperationCanceledException)
            {
                // 取消时正常退出
            }
            catch { }
            finally
            {
                client?.Dispose();
                sem.Release();
            }
        }, scanCts.Token));
        await Task.WhenAll(portTasks);

        SimpleLogger.Log($"TCP: Found {openPorts.Count} open ports (excluding {alreadyMatchedIps.Count} already matched)");

        if (openPorts.Count == 0) return;

        var claimedIps = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var probeTasks = new List<Task>();
        var probeSem = new System.Threading.SemaphoreSlim(5);

        SimpleLogger.Log($"TCP: Probing {openPorts.Count} IPs with {unmatchedDevices.Count} devices...");

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
                                SimpleLogger.Log($"TCP MATCHED: {device.Name} ({device.DeviceId}) -> {ip}");
                            }
                        }
                    }
                    catch { }
                    finally { probeSem.Release(); }
                }));
            }
        }

        await Task.WhenAll(probeTasks);

        foreach (var device in unmatchedDevices.Where(d => string.IsNullOrEmpty(d.IpAddress)))
        {
            SimpleLogger.Log($"TCP: {device.Name} ({device.DeviceId}) could not be identified (cloud MAC may not match real MAC, no mDNS)");
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

            var buffer = new byte[4096];
            var bytesRead = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            var bodyStart = response.IndexOf("\r\n\r\n");
            if (bodyStart < 0) return false;
            var body = response[(bodyStart + 4)..].Trim();

            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("encrypt", out var enc) && enc.GetBoolean() &&
                doc.RootElement.TryGetProperty("data", out var dataProp))
            {
                var responseIv = doc.RootElement.TryGetProperty("iv", out var ivProp) ? ivProp.GetString() ?? "" : "";
                var encryptedResponse = dataProp.GetString() ?? "";

                if (!string.IsNullOrEmpty(encryptedResponse) && !string.IsNullOrEmpty(responseIv))
                {
                    try
                    {
                        var decrypted = AesCrypto.Decrypt(encryptedResponse, device.DeviceKey, responseIv);
                        SimpleLogger.Log($"TCP probe: {ip} decrypted for {device.DeviceId}: {decrypted[..Math.Min(100, decrypted.Length)]}...");
                        return true;
                    }
                    catch
                    {
                        SimpleLogger.Log($"TCP probe: {ip} decryption failed for {device.DeviceId} (wrong key)");
                        return false;
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("error", out var error) && error.GetInt32() == 0)
            {
                SimpleLogger.Log($"TCP probe: {ip} returned error:0 for {device.DeviceId} (no encrypted data to verify)");
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

    public async Task<bool> SetPowerAsync(DeviceInfo device, bool turnOn, int outlet = 0)
    {
        if (string.IsNullOrEmpty(device.IpAddress))
        {
            _logger?.LogWarning("Device {DeviceName} has no IP address", device.Name);
            return false;
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

        SimpleLogger.Log($"LAN Control: {device.Name} -> {state}");
        SimpleLogger.Log($"LAN Target: http://{device.IpAddress}:8081/zeroconf/switches");

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
            SimpleLogger.Log($"LAN Response: {json}");

            if (string.IsNullOrEmpty(json))
            {
                SimpleLogger.Log("LAN Response: Empty (assuming success)");
                return true;
            }

            using var doc = JsonDocument.Parse(json);
            bool success;
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var errorVal = error.GetInt32();
                SimpleLogger.Log($"LAN Error Code: {errorVal}");
                success = errorVal == 0;
            }
            else
            {
                success = true;
            }

            return success;
        }
        catch (HttpRequestException ex)
        {
            SimpleLogger.Log($"LAN HTTP Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                SimpleLogger.Log($"LAN Inner Error: {ex.InnerException.Message}");
            }

            return await SendViaSocketAsync(device, requestBody);
        }
        catch (TaskCanceledException)
        {
            SimpleLogger.Log("LAN Timeout");
            return false;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"LAN Error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> SendViaSocketAsync(DeviceInfo device, object requestBody)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(device.IpAddress, 8081);

            var requestJson = JsonSerializer.Serialize(requestBody);
            var httpRequest = $"POST /zeroconf/switches HTTP/1.1\r\n" +
                              $"Host: {device.IpAddress}:8081\r\n" +
                              $"Content-Type: application/json\r\n" +
                              $"Content-Length: {Encoding.UTF8.GetByteCount(requestJson)}\r\n" +
                              $"Connection: close\r\n" +
                              $"\r\n" +
                              requestJson;

            var bytes = Encoding.UTF8.GetBytes(httpRequest);
            await client.GetStream().WriteAsync(bytes, 0, bytes.Length);

            var buffer = new byte[4096];
            var bytesRead = await client.GetStream().ReadAsync(buffer, 0, buffer.Length);
            var response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            SimpleLogger.Log($"Socket Response: {response}");

            if (response.StartsWith("HTTP/1.1 200") || response.StartsWith("HTTP/1.0 200"))
                return true;

            var bodyStart = response.IndexOf("\r\n\r\n");
            if (bodyStart >= 0)
            {
                var jsonBody = response[(bodyStart + 4)..].Trim();
                if (jsonBody.Contains("\"error\":0") || jsonBody.Contains("\"error\": 0"))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            SimpleLogger.Log($"Socket Error: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetPowerWithRetryAsync(DeviceInfo device, bool turnOn, int outlet = 0, int maxRetries = 1)
    {
        for (int i = 0; i <= maxRetries; i++)
        {
            if (await SetPowerAsync(device, turnOn, outlet)) return true;
            if (i < maxRetries) await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// Query device power state via LAN protocol.
    /// Note: Most eWeLink devices do NOT support reading state via LAN.
    /// Use Cloud API (GetDevicesAsync) for state refresh instead.
    /// </summary>
    public Task<bool> RefreshDeviceStateAsync(DeviceInfo device)
    {
        SimpleLogger.Log($"State query: LAN not supported for {device.Name} ({device.DeviceId}), use Cloud API");
        return Task.FromResult(false);
    }
}
