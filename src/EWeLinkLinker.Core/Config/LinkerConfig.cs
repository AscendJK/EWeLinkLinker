using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        catch (Exception ex)
        {
            // 以前这里把密文原样返回：调用方会拿一段 base64 垃圾去当密码/令牌用，
            // 云端报"凭证失效"，GUI 再把它 Protect 一遍存回去 ⇒ 同一段密文被二次加密，永久解不回来。
            // 解不开就是解不开，返回空并让上层用 HasUndecryptableProtectedField 判断要不要拒绝写盘。
            System.Diagnostics.Debug.WriteLine($"[LinkerConfig] DPAPI 解密失败({ex.GetType().Name})，该字段按空值处理");
            return string.Empty;
        }
    }

    private static readonly (string Obj, string Field)[] ProtectedJsonFields =
    {
        ("account", "password"), ("tokens", "accessToken"), ("tokens", "refreshToken"), ("tokens", "userApiKey"),
    };

    /// <summary>磁盘上该字段有密文但本机解不开。</summary>
    private static bool FieldUndecryptable(JsonElement root, string obj, string field)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object) return false;
        if (!o.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.String) return false;
        var raw = v.GetString();
        return !string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(Unprotect(raw));
    }

    /// <summary>
    /// 磁盘上存在密文、但本机 DPAPI 解不开（换机器、系统重装、字段被手改）⇒ 返回 true。
    /// 只看能不能解开，不返回任何内容。上层据此拒绝"用内存这份覆盖磁盘那份"。
    /// </summary>
    public static bool HasUndecryptableProtectedField(string path) => ScanProtectedFile(path, FieldUndecryptable);

    private static bool ScanProtectedFile(string path, Func<JsonElement, string, string, bool> test)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            return ProtectedJsonFields.Any(f => test(root, f.Obj, f.Field));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>要写的这份里对应字段是空的，而磁盘上那份解不开 ⇒ 覆盖就等于把唯一存在的凭据丢掉。</summary>
    private bool WouldWipeUndecryptable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var root = doc.RootElement;

            return FieldUndecryptable(root, "account", "password") && string.IsNullOrEmpty(Account.Password)
                || FieldUndecryptable(root, "tokens", "accessToken") && string.IsNullOrEmpty(Tokens.AccessToken)
                || FieldUndecryptable(root, "tokens", "refreshToken") && string.IsNullOrEmpty(Tokens.RefreshToken)
                || FieldUndecryptable(root, "tokens", "userApiKey") && string.IsNullOrEmpty(Tokens.UserApiKey);
        }
        catch
        {
            return false;
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
            MigrateLegacyReleaseBands(config);
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
    /// 一次性迁移：旧配置存的是绝对解除线（releaseParameter），换算成"带宽"这个意图值。
    /// 换算后把旧字段置空，下次保存就不再写出去，配置自动收敛到新模型。
    /// 落在错误一侧的值（比如 ≤70 配 62）不迁移，留给触发器构造时的校验去拦。
    /// </summary>
    private static void MigrateLegacyReleaseBands(LinkerConfig config)
    {
        foreach (var rule in config.Rules)
        {
            foreach (var c in rule.Conditions)
            {
                var legacy = c.LegacyReleaseParameter;
                c.LegacyReleaseParameter = null;
                if (string.IsNullOrWhiteSpace(legacy) || !string.IsNullOrWhiteSpace(c.ReleaseBand)) continue;
                if (!float.TryParse(c.Parameter, out var trigger)) continue;
                if (!float.TryParse(legacy, out var release)) continue;

                var rise = c.Comparison is ComparisonOperator.Gte or ComparisonOperator.Gt;
                var fall = c.Comparison is ComparisonOperator.Lte or ComparisonOperator.Lt;
                if (!((rise && release < trigger) || (fall && release > trigger))) continue;

                c.ReleaseBand = Math.Abs(release - trigger).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>与 Save 使用完全相同的序列化，供"内容没变就不重写文件"的比较。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// 磁盘上现有内容与将要写入的在语义上一致时返回 true。
    /// 不能逐字节比文本：DPAPI 每次加密都产生不同密文，同一份凭据重存一遍字符就变了，
    /// 于是"没变就不写"永远命中不了，打开再关掉工具也要重写一次配置。
    /// 比较前把密文换回明文。读不动文件一律当作"有变化"——宁可多写一次，
    /// 也不能把用户按下的保存变成静默失败。
    /// </summary>
    public bool MatchesFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var onDisk = JsonSerializer.Deserialize<LinkerConfig>(File.ReadAllText(path), JsonOptions);
            return onDisk is not null && JsonNode.DeepEquals(onDisk.ToComparableNode(), ToComparableNode());
        }
        catch
        {
            return false;
        }
    }

    private JsonNode ToComparableNode()
    {
        var root = JsonNode.Parse(ToJson())!.AsObject();

        if (root["tokens"] is JsonObject tokens)
        {
            tokens["accessToken"] = JsonValue.Create(Tokens.AccessToken);
            tokens["refreshToken"] = JsonValue.Create(Tokens.RefreshToken);
            tokens["userApiKey"] = JsonValue.Create(Tokens.UserApiKey);
        }

        if (root["account"] is JsonObject account && account.ContainsKey("password"))
            account["password"] = JsonValue.Create(Account.Password);

        return root;
    }

    public bool Save(string path)
    {
        FileStream? fileLock = null;
        try
        {
            fileLock = TryAcquireFileLock(path);

            // 服务侧的 TokenManager 也会整份重写：解不开旧密文而内存是空的时候落盘，
            // 就等于把唯一还存在的凭据字节抹掉，这里挡住（返回 false，调用方按保存失败处理）
            if (WouldWipeUndecryptable(path))
            {
                System.Diagnostics.Debug.WriteLine($"Config save refused: undecryptable credentials on disk, {path}");
                return false;
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

    /// <summary>
    /// 拿到当前这对 at/rt 的时刻（UTC）。官方接口不返回到期时间，只写明寿命
    /// （access token 30 天、refresh token 60 天），所以只能自己记。
    /// null＝老配置没这个值：不主动刷新，真失效交给云端返回码兜。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? TokenObtainedAtUtc { get; set; }
}