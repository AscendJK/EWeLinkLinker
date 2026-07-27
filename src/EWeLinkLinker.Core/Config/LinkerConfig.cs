using System.Text.Json;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Config;

public class LinkerConfig
{
    public AccountConfig Account { get; set; } = new();
    public TokenConfig Tokens { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<LinkerRule> Rules { get; set; } = new();
    public bool LoggingEnabled { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    // Use a dictionary of locks per file path to avoid blocking unrelated config files
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> PathLocks = new();

    public static LinkerConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LinkerConfig();
        }

        try
        {
            var pathLock = PathLocks.GetOrAdd(path, _ => new object());
            string json;
            lock (pathLock)
            {
                json = File.ReadAllText(path);
            }
            var config = JsonSerializer.Deserialize<LinkerConfig>(json, JsonOptions) ?? new LinkerConfig();
            // 反序列化后验证所有设备数据
            foreach (var device in config.Devices)
            {
                device.Validate();
            }
            // 同步规则中的设备名称为配置中的最新名称
            SyncActionDeviceNames(config);
            return config;
        }
        catch (Exception)
        {
            return new LinkerConfig();
        }
    }

    /// <summary>
    /// 同步规则中的设备名称为配置中的最新名称
    /// </summary>
    private static void SyncActionDeviceNames(LinkerConfig config)
    {
        foreach (var rule in config.Rules)
        {
            foreach (var action in rule.Actions)
            {
                var device = config.Devices.FirstOrDefault(d => d.DeviceId == action.DeviceId);
                if (device != null && action.Name != device.Name)
                {
                    action.Name = device.Name;
                }
            }
        }
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, JsonOptions);

            // Atomic write: write to temp then rename
            var tempPath = path + ".tmp";
            var pathLock = PathLocks.GetOrAdd(path, _ => new object());
            lock (pathLock)
            {
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Trim log file if it exceeds maxSizeBytes (default 1MB).
    /// </summary>
    public static void TrimLogFile(string logPath, long maxSizeBytes = 1_048_576)
    {
        try
        {
            if (!File.Exists(logPath)) return;
            var fi = new FileInfo(logPath);
            if (fi.Length > maxSizeBytes)
            {
                var backupPath = logPath + ".old";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                fi.MoveTo(backupPath);
            }
        }
        catch { }
    }
}

public class AccountConfig
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "+86";
    public string Region { get; set; } = "cn";
}

public class TokenConfig
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string UserApiKey { get; set; } = string.Empty;
}
