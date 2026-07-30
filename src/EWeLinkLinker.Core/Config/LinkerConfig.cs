using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Config;

public class LinkerConfig
{
    public AccountConfig Account { get; set; } = new();
    public TokenConfig Tokens { get; set; } = new();
    public List<DeviceInfo> Devices { get; set; } = new();
    public List<LinkerRule> Rules { get; set; } = new();
    public bool LoggingEnabled { get; set; } = true;

    /// <summary>
    /// 轮询间隔（秒），范围 1-30，默认 5
    /// </summary>
    private int _pollingIntervalSeconds = 5;
    public int PollingIntervalSeconds
    {
        get => _pollingIntervalSeconds;
        set => _pollingIntervalSeconds = Math.Clamp(value, 1, 30);
    }

    // H-22/H-23 修复：DPAPI 加密辅助方法
    // M-4 修复：使用 LocalMachine 范围，使服务端（SYSTEM 账户）也能解密
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EWeLinkLinker_v1");

    /// <summary>
    /// 加密敏感字符串（使用本机范围的 DPAPI，服务端和客户端均可解密）
    /// </summary>
    internal static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    /// <summary>
    /// 解密敏感字符串
    /// </summary>
    internal static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(cipherText);
            var decrypted = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            // 解密失败时返回原值（兼容未加密的旧配置）
            return cipherText;
        }
    }

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
        catch (FileNotFoundException)
        {
            return new LinkerConfig();
        }
        catch (JsonException ex)
        {
            // H-11 修复：JSON 格式错误时记录日志但不静默吞掉
            System.Diagnostics.Debug.WriteLine($"Config JSON parse error: {ex.Message}");
            throw new InvalidOperationException($"配置文件 JSON 格式错误: {Path.GetFileName(path)}", ex);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config load error: {ex.Message}");
            throw new InvalidOperationException($"无法加载配置文件: {Path.GetFileName(path)}", ex);
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

    /// <summary>
    /// 保存配置到文件（原子写入）
    /// </summary>
    /// <returns>是否保存成功</returns>
    public bool Save(string path)
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
            return true;
        }
        catch (Exception ex)
        {
            // M-2 修复：至少写入 EventLog，不静默吞掉
            try
            {
                System.Diagnostics.Debug.WriteLine($"Config save failed: {ex.Message}");
                System.Diagnostics.EventLog.WriteEntry("Application",
                    $"EWeLinkLinker config save failed: {ex.Message}",
                    System.Diagnostics.EventLogEntryType.Error);
            }
            catch { }
            return false;
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
    private string _password = string.Empty;

    public string Account { get; set; } = string.Empty;

    /// <summary>
    /// H-22 修复：密码自动加密存储，解密读取
    /// JSON 序列化器直接操作 PasswordEncrypted 字段，避免双重加密
    /// </summary>
    [JsonIgnore]
    public string Password
    {
        get => LinkerConfig.Unprotect(_password);
        set => _password = string.IsNullOrEmpty(value) ? string.Empty : LinkerConfig.Protect(value);
    }

    /// <summary>
    /// JSON 序列化属性：存储加密后的密码
    /// </summary>
    [JsonPropertyName("password")]
    public string PasswordEncrypted
    {
        get => _password;
        set => _password = value;
    }

    public string CountryCode { get; set; } = "+86";
    public string Region { get; set; } = "cn";
}

public class TokenConfig
{
    private string _accessToken = string.Empty;
    private string _refreshToken = string.Empty;
    private string _userApiKey = string.Empty;

    /// <summary>
    /// H-23 修复：Token 自动加密存储，解密读取
    /// </summary>
    [JsonIgnore]
    public string AccessToken
    {
        get => LinkerConfig.Unprotect(_accessToken);
        set => _accessToken = string.IsNullOrEmpty(value) ? string.Empty : LinkerConfig.Protect(value);
    }

    [JsonIgnore]
    public string RefreshToken
    {
        get => LinkerConfig.Unprotect(_refreshToken);
        set => _refreshToken = string.IsNullOrEmpty(value) ? string.Empty : LinkerConfig.Protect(value);
    }

    [JsonIgnore]
    public string UserApiKey
    {
        get => LinkerConfig.Unprotect(_userApiKey);
        set => _userApiKey = string.IsNullOrEmpty(value) ? string.Empty : LinkerConfig.Protect(value);
    }

    // JSON 序列化属性：存储加密后的值
    [JsonPropertyName("accessToken")]
    public string AccessTokenEncrypted
    {
        get => _accessToken;
        set => _accessToken = value;
    }

    [JsonPropertyName("refreshToken")]
    public string RefreshTokenEncrypted
    {
        get => _refreshToken;
        set => _refreshToken = value;
    }

    [JsonPropertyName("userApiKey")]
    public string UserApiKeyEncrypted
    {
        get => _userApiKey;
        set => _userApiKey = value;
    }
}
