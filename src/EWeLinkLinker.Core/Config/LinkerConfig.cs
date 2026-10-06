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

    private const int CrossProcessLockTimeoutMs = 1500;

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

    /// <summary>
    /// 跨进程独占锁用锁文件（FileShare.None），不用命名 Mutex：命名 Mutex 需要
    /// SeCreateGlobalPrivilege，用户态 ConfigApp 建不了 Global\ 对象，只会静默退回进程内锁，
    /// 于是服务（session 0）与 ConfigApp 实际并不互斥。锁文件句柄由内核管理，
    /// 进程崩溃即自动释放，不会留下残留锁。
    /// 返回 null 表示没抢到或没有权限——调用方 best-effort 继续写，
    /// 不把一次本来写得成的保存变成失败。
    /// </summary>
    private static FileStream? TryAcquireFileLock(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        // 前导点：FileSystemWatcher 的 Filter 是前缀匹配，
        // "linker.json.lock" 会被 "linker.json" 命中，不能让它去触发重载
        var lockPath = Path.Combine(dir, "." + Path.GetFileName(path) + ".lock");
        var deadline = Environment.TickCount64 + CrossProcessLockTimeoutMs;

        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    System.Diagnostics.Debug.WriteLine($"Config lock contended, saving without it: {path}");
                    return null;
                }
                Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Config lock unavailable ({ex.GetType().Name}), saving without it: {path}");
                return null;
            }
        }
    }

    public bool Save(string path)
    {
        FileStream? fileLock = null;
        try
        {
            fileLock = TryAcquireFileLock(path);

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
            fileLock?.Dispose();
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