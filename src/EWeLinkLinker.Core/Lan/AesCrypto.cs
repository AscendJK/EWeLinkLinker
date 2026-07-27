using System.Security.Cryptography;
using System.Text;

namespace EWeLinkLinker.Core.Lan;

public static class AesCrypto
{
    /// <summary>
    /// AES-128-CBC encryption used by eWeLink LAN protocol.
    /// Key = MD5(deviceKey), IV = random 16 bytes.
    /// </summary>
    public static (string EncryptedData, string Iv) Encrypt(string plainText, string deviceKey)
    {
        // Key = MD5(deviceKey)
        var key = MD5.HashData(Encoding.UTF8.GetBytes(deviceKey));

        // IV = random 16 bytes
        var iv = RandomNumberGenerator.GetBytes(16);

        // Create AES cipher with explicit PKCS7 padding (eWeLink standard)
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        // Encrypt
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Return base64 encoded values (matching Python's base64.b64encode)
        return (
            Convert.ToBase64String(encryptedBytes),
            Convert.ToBase64String(iv)
        );
    }

    public static string Decrypt(string encryptedBase64, string deviceKey, string ivBase64)
    {
        var key = MD5.HashData(Encoding.UTF8.GetBytes(deviceKey));
        var iv = Convert.FromBase64String(ivBase64);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var encryptedBytes = Convert.FromBase64String(encryptedBase64);
        var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

        return Encoding.UTF8.GetString(decryptedBytes);
    }
}
