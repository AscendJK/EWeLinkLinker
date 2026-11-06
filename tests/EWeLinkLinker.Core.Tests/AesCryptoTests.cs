using System.Security.Cryptography;
using EWeLinkLinker.Core.Lan;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 局域网报文加密：纯函数，且是"设备收到的到底是不是那条命令"的唯一依据。
/// </summary>
public class AesCryptoTests
{
    private const string DeviceKey = "b4f45b1b-3f2c-4a9e-9c1d-6f0a2b7c8d9e";

    [Fact]
    public void 加密解密可往返()
    {
        const string plain = "{\"switches\":[{\"outlet\":0,\"switch\":\"on\"}]}";
        var (data, iv) = AesCrypto.Encrypt(plain, DeviceKey);
        Assert.Equal(plain, AesCrypto.Decrypt(data, DeviceKey, iv));
    }

    [Fact]
    public void 每次加密的IV与密文都不同()
    {
        const string plain = "{\"switches\":[]}";
        var first = AesCrypto.Encrypt(plain, DeviceKey);
        var second = AesCrypto.Encrypt(plain, DeviceKey);

        Assert.NotEqual(first.Iv, second.Iv);
        Assert.NotEqual(first.EncryptedData, second.EncryptedData);
        // 但两者都能解回同一份明文
        Assert.Equal(plain, AesCrypto.Decrypt(first.EncryptedData, DeviceKey, first.Iv));
        Assert.Equal(plain, AesCrypto.Decrypt(second.EncryptedData, DeviceKey, second.Iv));
    }

    [Fact]
    public void 密钥不匹配时解密失败而不是返回垃圾()
    {
        var (data, iv) = AesCrypto.Encrypt("{\"switches\":[]}", DeviceKey);
        Assert.ThrowsAny<CryptographicException>(() => AesCrypto.Decrypt(data, "另一个设备的key", iv));
    }

    [Fact]
    public void 空明文也能往返()
    {
        var (data, iv) = AesCrypto.Encrypt("", DeviceKey);
        Assert.Equal("", AesCrypto.Decrypt(data, DeviceKey, iv));
    }
}
