using System.Collections.Concurrent;
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

    private int _pollingIntervalSeconds = 5;
    public int PollingIntervalSeconds
    {
        get => _pollingIntervalSeconds;
        set => _pollingIntervalSeconds = Math.Clamp(value, 1, 30);
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EWeLinkLinker_v1");

    internal static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

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
            return cipherText;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new();

    public static LinkerConfig Load(string path)
    {
        if (!File.Exists(path))
            return new LinkerConfig();

        try
        {
            var pathLock = PathLocks.GetOrAdd(path, _ => new object());
            string json;
            lock (pathLock)
            {
                json = File.ReadAllText(path);
            }
            var config = JsonSerializer.Deserialize<LinkerConfig>(json, JsonOptions) ?? new LinkerConfig();
            foreach (var device in config.Devices)
                device.Validate();
            SyncActionDeviceNames(config);
            return config;
        }
        catch (FileNotFoundException)
        {
            return new LinkerConfig();
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config JSON parse error: {ex.Message}");
            throw new InvalidOperationException($"Config file has invalid JSON: {Path.GetFileName(path)}", ex);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Config load error: {ex.Message}");
            throw new InvalidOperationException($"Cannot load config file: {Path.GetFileName(path)}", ex);
        }
    }

    private static void SyncActionDeviceNames(LinkerConfig config)
    {
        foreach (var rule in config.Rules)
        {
            foreach (var action in rule.Actions)
            {
                var device = config.Devices.FirstOrDefault(d => d.DeviceId == action.DeviceId);
                if (device != null && action.Name != device.Name)
                    action.Name = device.Name;
            }
        }
    }

    public bool Save(string path)
    {
        // 命名 Mutex 用于跨进程互斥（ConfigApp <-> 服务进程）.
        // 必须用 Global\ 前缀——Windows 服务在 session 0，ConfigApp 在 session 1+，
        // 会话级 Mutex 无法跨 session。ConfigApp 用户态进程可能没有
        // SeCreateGlobalPrivilege 权限，此时抛 UnauthorizedAccessException，回退到进程内锁。
        System.Threading.Mutex? mutex = null;
        bool mutexAcquired = false;
        try
        {
            try
            {
                var mutexName = @"Global\EWeLinkLinker_Config_" + path.Replace('\\', '_').Replace('/', '_');
                mutex = new System.Threading.Mutex(false, mutexName);
                mutexAcquired = mutex.WaitOne(TimeSpan.FromSeconds(1.5));
                if (!mutexAcquired)
                {
                    System.Diagnostics.Debug.WriteLine($"Config save timeout: {path}");
                    return false;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // ConfigApp 用户态进程无 SeCreateGlobalPrivilege 权限
                // 回退到进程内锁（进程内 PathLocks 仍能防止同进程并发写）
                System.Diagnostics.Debug.WriteLine($"Global mutex not available (user mode), using process-level lock: {path}");
            }
            catch (AbandonedMutexException)
            {
                // 另一进程持有 Mutex 时崩溃，我们已获取所有权
                mutexAcquired = true;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

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
        finally
        {
            if (mutexAcquired) mutex?.ReleaseMutex();
            mutex?.Dispose();
        }
    }

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

    [JsonIgnore]
    public string Password
    {
        get => LinkerConfig.Unprotect(_password);
        set => _password = string.IsNullOrEmpty(value) ? string.Empty : LinkerConfig.Protect(value);
    }

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