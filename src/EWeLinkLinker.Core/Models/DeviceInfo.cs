namespace EWeLinkLinker.Core.Models;

public class DeviceInfo
{
    private int _channelCount = 1;
    private List<string> _channelStates = new() { "off" };

    /// <summary>
    /// 验证并同步 ChannelStates 和 ChannelCount（反序列化后调用）
    /// </summary>
    public void Validate()
    {
        SyncChannelStates();
    }

    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string DeviceKey { get; set; } = string.Empty;
    public string DeviceApiKey { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;  // Cloud MAC (read-only, may be fake)
    public string RealMacAddress { get; set; } = string.Empty;  // User-entered real MAC
    public int Uuid { get; set; }
    public bool IsOnline { get; set; }

    /// <summary>
    /// First channel power state (backward compatibility).
    /// Not serialized — ChannelStates[0] is the canonical source.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PowerState
    {
        get => ChannelCount > 0 ? ChannelStates[0] : "off";
        set { if (ChannelCount > 0) ChannelStates[0] = value; }
    }

    /// <summary>
    /// Per-channel power states. Always kept in sync with ChannelCount.
    /// </summary>
    public List<string> ChannelStates
    {
        get => _channelStates;
        set => _channelStates = value ?? new() { "off" };
    }

    /// <summary>
    /// Number of channels (1-8). Automatically syncs ChannelStates list.
    /// </summary>
    public int ChannelCount
    {
        get => _channelCount;
        set
        {
            _channelCount = Math.Clamp(value, 1, 8);
            SyncChannelStates();
        }
    }

    /// <summary>
    /// Ensure ChannelStates list has exactly ChannelCount elements.
    /// </summary>
    private void SyncChannelStates()
    {
        while (_channelStates.Count < _channelCount)
            _channelStates.Add("off");
        if (_channelStates.Count > _channelCount)
            _channelStates.RemoveRange(_channelCount, _channelStates.Count - _channelCount);
    }

    /// <summary>
    /// Whether this device has a valid local MAC (not all zeros).
    /// All-zero MAC = IR bridge / cloud-only device with no local API.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLocalMac => !string.IsNullOrEmpty(RealMacAddress) ||
        (!string.IsNullOrEmpty(MacAddress) && MacAddress != "00:00:00:00:00:00" &&
         NormalizeMac(MacAddress) != "000000000000");

    /// <summary>
    /// Best MAC for discovery: RealMac first, then CloudMac.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveMac => !string.IsNullOrEmpty(RealMacAddress) ? RealMacAddress : MacAddress;

    /// <summary>
    /// Display string for all channel states in device list.
    /// Single channel: "on" / "off"
    /// Multi-channel: "通1:开 通2:关 通3:开"
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string StateDisplay
    {
        get
        {
            if (ChannelCount <= 1)
                return PowerState;

            var parts = new List<string>();
            for (int i = 0; i < ChannelCount; i++)
            {
                string state = i < ChannelStates.Count ? ChannelStates[i] : "off";
                parts.Add($"通{i}:{(state == "on" ? "开" : "关")}");
            }
            return string.Join(" ", parts);
        }
    }

    /// <summary>
    /// Display string for cloud MAC in UI (always shows original cloud MAC).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CloudMacDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(MacAddress) || NormalizeMac(MacAddress) == "000000000000")
                return "无 (仅云端)";
            return MacAddress;
        }
    }

    /// <summary>
    /// 标准化 MAC 地址（移除所有分隔符，转为小写）
    /// </summary>
    public static string NormalizeMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return string.Empty;
        return mac.Replace(":", "").Replace("-", "").Replace(".", "").Replace(" ", "").ToLowerInvariant();
    }

    /// <summary>
    /// 格式化 MAC 地址为带冒号的格式（每2位一组）
    /// </summary>
    public static string FormatMac(string mac)
    {
        var normalized = NormalizeMac(mac);
        if (normalized.Length != 12) return mac; // 不是有效长度，原样返回

        // 每2位插入冒号
        var parts = new List<string>();
        for (int i = 0; i < 12; i += 2)
        {
            parts.Add(normalized.Substring(i, 2));
        }
        return string.Join(":", parts);
    }

    /// <summary>
    /// 自动格式化 MAC 地址（如果输入有效则格式化为带冒号格式）
    /// </summary>
    public static string AutoFormatMac(string mac)
    {
        var normalized = NormalizeMac(mac);
        // 如果是12位十六进制字符，自动格式化
        if (normalized.Length == 12 && normalized.All(c => "0123456789abcdef".Contains(c)))
        {
            return FormatMac(normalized);
        }
        return mac; // 无效输入，原样返回
    }
}
