using System.IdentityModel.Tokens.Jwt;
using EWeLinkLinker.Core.Cloud;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.Core.Token;

public class TokenManager(CloudClient cloudClient, string configPath)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<AuthTokens> GetValidTokensAsync()
    {
        var config = Config.LinkerConfig.Load(configPath);

        if (string.IsNullOrEmpty(config.Tokens.AccessToken))
        {
            throw new TokenExpiredException("未配置登录凭证，请先通过 GUI 登录");
        }

        if (IsTokenExpired(config.Tokens.AccessToken))
        {
            await _refreshLock.WaitAsync();
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

        config.Tokens.AccessToken = newTokens.AccessToken;
        config.Tokens.RefreshToken = newTokens.RefreshToken;
        config.Tokens.UserApiKey = newTokens.UserApiKey;
        config.Save(configPath);

        return newTokens;
    }

    /// <summary>
    /// 检查 Token 是否过期（静态方法，不访问实例数据）
    /// </summary>
    public static bool IsTokenExpired(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(token);

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
            var config = Config.LinkerConfig.Load(configPath);
            config.Account.Region = ex.CorrectRegion;
            SaveTokensToConfig(tokens, config);
            return tokens;
        }
    }

    private void SaveTokensToConfig(AuthTokens tokens, Config.LinkerConfig? existingConfig = null)
    {
        var config = existingConfig ?? Config.LinkerConfig.Load(configPath);
        config.Tokens.AccessToken = tokens.AccessToken;
        config.Tokens.RefreshToken = tokens.RefreshToken;
        config.Tokens.UserApiKey = tokens.UserApiKey;
        config.Save(configPath);
    }
}

public class TokenExpiredException(string message) : Exception(message)
{
}
