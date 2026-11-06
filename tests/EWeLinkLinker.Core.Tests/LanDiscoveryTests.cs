using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using EWeLinkLinker.Core.Lan;
using EWeLinkLinker.Core.Models;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 发现层：挑网段、解析 arp、mDNS 分片与单播端点。
/// 这些逻辑原来全埋在 IO 中间，只能靠"发现不到设备"来感知错误——现在都搬进了纯函数。
/// </summary>
public class LanDiscoveryTests
{
    // ── 挑网段：VPN/虚拟网卡不该抢走扫描 ─────────────────────────────

    private static NetAdapter Adapter(string name, string desc, string? ipv4,
                                      bool gateway = false,
                                      OperationalStatus status = OperationalStatus.Up,
                                      NetworkInterfaceType type = NetworkInterfaceType.Ethernet)
        => new(name, desc, type, status, ipv4, gateway);

    [Fact]
    public void 挑网段_带网关的VPN不得抢走真实网卡()
    {
        // 实际机器上 VPN 的 TUN/TAP 同样 Up、同样有默认网关
        var adapters = new[]
        {
            Adapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.20.16.1", gateway: true),
            Adapter("WireGuard", "WireGuard Tunnel", "10.8.0.5", gateway: true),
            Adapter("以太网", "Realtek PCIe GbE Family Controller", "192.168.1.42", gateway: true),
        };

        Assert.Equal("192.168.1", LanDiscoveryParsing.SelectSubnet(adapters));
    }

    [Fact]
    public void 挑网段_真实网卡没网关时仍优先于虚拟网卡()
    {
        var adapters = new[]
        {
            Adapter("WireGuard", "WireGuard Tunnel", "10.8.0.5", gateway: true),
            Adapter("以太网", "Realtek PCIe GbE", "192.168.31.7"),
        };

        Assert.Equal("192.168.31", LanDiscoveryParsing.SelectSubnet(adapters));
    }

    [Fact]
    public void 挑网段_只剩虚拟网卡时退回用它_保持旧行为()
    {
        var adapters = new[] { Adapter("OpenVPN TAP-Windows6", "TAP-Windows Adapter V9", "10.8.0.5", gateway: true) };
        Assert.Equal("10.8.0", LanDiscoveryParsing.SelectSubnet(adapters));
    }

    [Fact]
    public void 挑网段_没有可用IPv4时返回null_不再盲扫192点168点1()
    {
        var adapters = new[]
        {
            Adapter("以太网", "Realtek", "169.254.13.7", gateway: true),   // APIPA
            Adapter("Loopback", "Loopback", "127.0.0.1", type: NetworkInterfaceType.Loopback),
            Adapter("拔了网线", "Realtek", "192.168.1.9", status: OperationalStatus.Down),
        };

        Assert.Null(LanDiscoveryParsing.SelectSubnet(adapters));
    }

    // ── arp -a 解析 ────────────────────────────────────────────────

    [Fact]
    public void 解析arp_英文表头与短横线分隔()
    {
        const string output = """
            Interface: 192.168.1.42 --- 0x8
              Internet Address      Physical Address      Type
              192.168.1.1           d0-27-04-e1-9a-e6     dynamic
              192.168.1.34          a4-c1-38-77-12-0f     dynamic
            """;

        var table = LanDiscoveryParsing.ParseArpOutput(output);

        Assert.Equal(2, table.Count);
        Assert.Equal("d02704e19ae6", table["192.168.1.1"]);
        Assert.Equal("a4c13877120f", table["192.168.1.34"]);
    }

    [Fact]
    public void 解析arp_中文表头()
    {
        const string output = """
            接口: 192.168.1.42 --- 0x8
              Internet 地址         物理地址              类型
              192.168.1.7           b4-f4-5b-1b-3f-2c     动态
            """;

        var table = LanDiscoveryParsing.ParseArpOutput(output);

        Assert.Equal("b4f45b1b3f2c", table["192.168.1.7"]);
    }

    [Fact]
    public void 解析arp_丢弃解析不出MAC的行()
    {
        const string output = """
              192.168.1.50          <incomplete>          
              不是IP                 d0-27-04-e1-9a-e6     
            """;

        Assert.Empty(LanDiscoveryParsing.ParseArpOutput(output));
    }

    [Fact]
    public void 解析arp_广播MAC是合法条目()
    {
        var table = LanDiscoveryParsing.ParseArpOutput("192.168.1.51  ff-ff-ff-ff-ff-ff  static");
        Assert.Equal("ffffffffffff", table["192.168.1.51"]);
    }

    [Fact]
    public void 解析arp_点分MAC也能归一()
    {
        var table = LanDiscoveryParsing.ParseArpOutput("192.168.1.9  d027.04e1.9ae6  dynamic");
        Assert.Equal("d02704e19ae6", table["192.168.1.9"]);
    }

    // ── MAC 归一 ──────────────────────────────────────────────────

    [Theory]
    [InlineData("D0:27:04:E1:9A:E6", "d02704e19ae6")]
    [InlineData("d0-27-04-e1-9a-e6", "d02704e19ae6")]
    [InlineData("d027.04e1.9ae6", "d02704e19ae6")]
    [InlineData("d02704e19ae6", "d02704e19ae6")]
    [InlineData("AA:BB:CC", "")]              // 3 段不是 MAC：必须归一成空串，否则会和 ARP 里的空串互相匹配
    [InlineData("", "")]
    [InlineData(null, "")]
    public void MAC归一(string? input, string expected)
        => Assert.Equal(expected, LanDiscoveryParsing.NormalizeMac(input));

    // ── 子进程超时（arp -a 卡住不再永久挂起） ────────────────────────

    [Fact]
    public async Task 子进程超时_到点被杀掉并返回null()
    {
        var sw = Stopwatch.StartNew();
        var output = await LanClient.RunProcessWithTimeoutAsync(
            "cmd.exe", "/c ping -n 20 127.0.0.1", TimeSpan.FromMilliseconds(400));

        sw.Stop();
        Assert.Null(output);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"应当在超时后立刻返回，实际 {sw.Elapsed.TotalSeconds:F1}s");
    }

    [Fact]
    public async Task 子进程正常_返回标准输出()
    {
        var output = await LanClient.RunProcessWithTimeoutAsync("cmd.exe", "/c echo ewl-probe", TimeSpan.FromSeconds(5));
        Assert.Contains("ewl-probe", output);
    }

    // ── mDNS 分片与端点 ───────────────────────────────────────────

    [Fact]
    public void mDNS分片_按数字顺序拼接()
    {
        var txt = new Dictionary<string, string>
        {
            ["data1"] = "AA",
            ["data10"] = "JJ",
            ["data2"] = "BB",
            ["data9"] = "II",
            ["iv"] = "xx",
            ["id"] = "1002b56d8a",
        };

        // 按数字排，不是按字典序（"data10" 排在 "data2" 前面就拼错位了）
        Assert.Equal("AABBIIJJ", MdnsStatusClient.BuildDataPayload(txt));
    }

    [Fact]
    public void mDNS分片_超过八片不再被截断()
    {
        var txt = new Dictionary<string, string>();
        for (var i = 1; i <= 12; i++) txt["data" + i] = i <= 9 ? i.ToString() : "X";

        // 老实现写死 1..8：第 9 片起被丢，解密必然失败
        Assert.Equal(9 + 3, MdnsStatusClient.BuildDataPayload(txt).Length);
        Assert.EndsWith("XXX", MdnsStatusClient.BuildDataPayload(txt));
    }

    [Fact]
    public void mDNS分片_没有data字段时返回空()
        => Assert.Equal("", MdnsStatusClient.BuildDataPayload(new Dictionary<string, string> { ["id"] = "x" }));

    [Fact]
    public void mDNS端点_畸形IP只跳过那一台_不拖垮整次读取()
    {
        var devices = new List<DeviceInfo>
        {
            new() { DeviceId = "a", Name = "好的", IpAddress = "192.168.1.42", DeviceKey = "k" },
            new() { DeviceId = "b", Name = "带端口", IpAddress = "192.168.1.43:8081", DeviceKey = "k" },
            new() { DeviceId = "c", Name = "空的", IpAddress = "", DeviceKey = "k" },
            new() { DeviceId = "d", Name = "太好了", IpAddress = " 192.168.1.44 ", DeviceKey = "k" },
        };

        var endpoints = MdnsStatusClient.BuildQueryEndpoints(devices, out var skipped);

        Assert.Equal(2, skipped);                                  // 带端口 / 空串
        Assert.Equal(3, endpoints.Count);                          // 组播 + 两台有效
        Assert.Contains(endpoints, e => e.Address.ToString() == "192.168.1.42");
        Assert.Contains(endpoints, e => e.Address.ToString() == "192.168.1.44");
    }
}
