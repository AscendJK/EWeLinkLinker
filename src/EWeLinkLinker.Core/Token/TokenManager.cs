using System.IdentityModel.Tokens.Jwt;
using EWeLinkLinker.Core.Cloud;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Token;

public class TokenManager(CloudClient cloudClient, string configPath) : IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    // 静态复用 JwtSecurityTokenHandler（线程安全）
    private static readonly JwtSecurityTokenHandler JwtHandler = new();

    public async Task<AuthTokens> GetValidTokensAsync(CancellationToken ct = default)
    {
        var config = Config.LinkerConfig.Load(configPath);

        if (string.IsNullOrEmpty(config.Tokens.AccessToken))
        {
            throw new TokenExpiredException("未配置登录凭证，请先通过 GUI 登录");
        }

        if (IsTokenExpired(config.Tokens.AccessToken))
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                // Double-check after acquiring lock
                config = Config.LinkerConfig.Load(configPath);
                if (IsTokenExpired(config.Tokens.AccessToken))
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

    private async Task<AuthTokens> RefreshTokensAsync(Config.LinkerConfig config)
    {
        cloudClient.Region = config.Account.Region;
        var newTokens = await cloudClient.RefreshTokenAsync(config.Tokens.RefreshToken);

        // C-1 修复：重新加载最新 config，避免覆盖 ConfigApp 的并发改动
        var freshConfig = Config.LinkerConfig.Load(configPath);
        freshConfig.Tokens.AccessToken = newTokens.AccessToken;
        freshConfig.Tokens.RefreshToken = newTokens.RefreshToken;
        // H-? 修复：防止空 UserApiKey 覆盖已有的有效 key（某些刷新响应不含 apikey 字段）
        if (!string.IsNullOrEmpty(newTokens.UserApiKey))
            freshConfig.Tokens.UserApiKey = newTokens.UserApiKey;
        freshConfig.Save(configPath);

        return newTokens;
    }

    /// <summary>
    /// 检查 Token 是否过期（静态方法，不访问实例数据）
    /// </summary>
    public static bool IsTokenExpired(string token)
    {
        try
        {
            // 复用静态 Handler，避免重复创建
            var jwtToken = JwtHandler.ReadJwtToken(token);

            if (jwtToken.ValidTo == DateTime.MinValue)
            {
                return false;
            }

            return jwtToken.ValidTo.AddMinutes(5) < DateTime.UtcNow;
        }
        catch
        {
            return true;
        }
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
            config.Save(configPath);
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
        config.Save(configPath);
    }
}

public class TokenExpiredException(string message) : Exception(message)
{
}
