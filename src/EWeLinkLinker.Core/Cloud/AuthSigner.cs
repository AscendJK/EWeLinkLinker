using System.Security.Cryptography;
using System.Text;

namespace EWeLinkLinker.Core.Cloud;

public static class AuthSigner
{
    /// <summary>
    /// HMAC-SHA256 signature used by eWeLink cloud API.
    /// Sign = base64(HMAC-SHA256(JSON.stringify(body), appSecret))
    /// </summary>
    public static string Sign(string body, string appSecret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(appSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(bodyBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Build Authorization header value: "Sign {signature}"
    /// </summary>
    public static string BuildAuthorizationHeader(string body, string appSecret)
    {
        var signature = Sign(body, appSecret);
        return $"Sign {signature}";
    }
}
