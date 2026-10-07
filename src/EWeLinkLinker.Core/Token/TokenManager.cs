using EWeLinkLinker.Core.Cloud;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Token;

public class TokenManager(CloudClient cloudClient, string configPath) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// 官方文档只给固定寿命（access token 30 天、refresh token 60 天），接口不返回到期时间，
    /// 所以只能自己记"拿到这对 token 的时刻"。这里提前 5 天换，免得卡在动作执行途中正好到期。
    /// </summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromDays(25);

    public async Task<AuthTokens> GetValidTokensAsync(CancellationToken ct = default)
    {
        var config = Config.LinkerConfig.Load(configPath);

        if (string.IsNullOrEmpty(config.Tokens.AccessToken))
        {
            throw new TokenExpiredException("未配置登录凭证，请先通过 GUI 登录");
        }

        if (IsStale(config.Tokens.TokenObtainedAtUtc))
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                config = Config.LinkerConfig.Load(configPath);
                if (IsStale(config.Tokens.TokenObtainedAtUtc))
                {
                    return await RefreshTokensAsync(config);
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        return new AuthTokens
        {
            AccessToken = config.Tokens.AccessToken,
            RefreshToken = config.Tokens.RefreshToken,
            UserApiKey = config.Tokens.UserApiKey
        };
    }

    /// <summary>
    /// 不等 25 天，立刻用 refresh token 换一对新的。给"云端已经报凭证失效"时的自愈用——
    /// 计时救不了被别处登录顶号，但 refresh token 通常还活着，换一次就够了，不必动用密码。
    /// </summary>
    public async Task<AuthTokens> RefreshNowAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            return await RefreshTokensAsync(Config.LinkerConfig.Load(configPath));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 时刻不知道就不主动刷新。旧实现是把 access token 当 JWT 解，解不动就判"已过期"——
    /// 而 eWeLink 的 at 根本不是 JWT，于是每次调用都白跑一趟 refresh，还会顺带把 token 轮换掉。
    /// 真失效由云端的 401/402 返回码兜。
    /// </summary>
    public static bool IsStale(DateTime? obtainedAtUtc) =>
        obtainedAtUtc.HasValue && DateTime.UtcNow - obtainedAtUtc.Value >= AccessTokenLifetime;

    private async Task<AuthTokens> RefreshTokensAsync(Config.LinkerConfig config)
    {
        if (string.IsNullOrEmpty(config.Tokens.RefreshToken))
            throw new TokenExpiredException("没有可用的 refresh token，需要重新登录一次");

        cloudClient.Region = config.Account.Region;
        var newTokens = await cloudClient.RefreshTokenAsync(config.Tokens.RefreshToken);

        // C-1 修复：重新加载最新 config，避免覆盖 ConfigApp 的并发改动
        var freshConfig = Config.LinkerConfig.Load(configPath);
        freshConfig.Tokens.AccessToken = newTokens.AccessToken;
        freshConfig.Tokens.RefreshToken = newTokens.RefreshToken;
        freshConfig.Tokens.TokenObtainedAtUtc = DateTime.UtcNow;
        // H-? 修复：防止空 UserApiKey 覆盖已有的有效 key（某些刷新响应不含 apikey 字段）
        if (!string.IsNullOrEmpty(newTokens.UserApiKey))
            freshConfig.Tokens.UserApiKey = newTokens.UserApiKey;
        if (!freshConfig.Save(configPath))
        {
            // 这条不能只留一行 detail：盘上那份 refresh token 已经被云端消耗掉了，
            // 新的只活在内存里，服务一重启就拿着死 rt 再也换不动，只能人工重新登录一次。
            // 仍然返回新 token（内存里这份是有效的，本轮设备命令要继续走），但要把话说明白。
            var msg = $"[ERROR][Token] 刷新成功但新 token 未能写入配置，进程重启后会退回已被云端消耗的旧 rt，需要重新登录一次: {configPath}";
            Logging.SimpleLogger.Log(msg);
            try
            {
                System.Diagnostics.EventLog.WriteEntry("Application",
                    "EWeLinkLinker: refreshed tokens could not be persisted to " + configPath,
                    System.Diagnostics.EventLogEntryType.Error);
            }
            catch { }
        }

        return newTokens;
    }

    /// <summary>
    /// 登录并保存所有 Token（包括 RefreshToken）到配置文件
    /// </summary>
    public async Task<AuthTokens> LoginAsync(string email, string password, string countryCode = "+86", string region = "cn")
    {
        cloudClient.Region = region;

        try
        {
            var (tokens, _) = await cloudClient.LoginAsync(email, password, countryCode);
            SaveTokensToConfig(tokens);
            return tokens;
        }
        catch (WrongRegionException ex)
        {
            cloudClient.Region = ex.CorrectRegion;
            var (tokens, _) = await cloudClient.LoginAsync(email, password, countryCode);
            // Persist the correct region so next startup uses it
            // C-2 修复：重新加载最新 config，设置正确区域后保存 Token
            var config = Config.LinkerConfig.Load(configPath);
            config.Account.Region = ex.CorrectRegion;
            config.Tokens.AccessToken = tokens.AccessToken;
            config.Tokens.RefreshToken = tokens.RefreshToken;
            config.Tokens.UserApiKey = tokens.UserApiKey;
            config.Tokens.TokenObtainedAtUtc = DateTime.UtcNow;
            if (!config.Save(configPath))
                Logging.SimpleLogger.Log($"[Token] 纠正区域后的 token 未能写入配置: {configPath}");
            return tokens;
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
    }

    private void SaveTokensToConfig(AuthTokens tokens)
    {
        // C-2 修复：始终从磁盘加载最新 config，避免覆盖并发改动
        var config = Config.LinkerConfig.Load(configPath);
        config.Tokens.AccessToken = tokens.AccessToken;
        config.Tokens.RefreshToken = tokens.RefreshToken;
        config.Tokens.UserApiKey = tokens.UserApiKey;
        config.Tokens.TokenObtainedAtUtc = DateTime.UtcNow;
        if (!config.Save(configPath))
            Logging.SimpleLogger.Log($"[Token] 登录得到的 token 未能写入配置: {configPath}");
    }
}

public class TokenExpiredException(string message) : Exception(message)
{
}
