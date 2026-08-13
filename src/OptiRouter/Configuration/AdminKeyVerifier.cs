using System.Security.Cryptography;
using System.Text;

namespace OptiRouter.Configuration;

/// <summary>
/// 管理端密钥常量时间比较校验。供 Program.cs 鉴权中间件与 /login 登录页共用，
/// 避免两处重复实现 SHA256 + FixedTimeEquals。
/// </summary>
internal static class AdminKeyVerifier
{
    /// <summary>校验提供的密钥是否与已配置密钥匹配（常量时间比较，防时序侧信道）。</summary>
    public static bool IsValid(string? configuredKey, string? providedKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKey) || string.IsNullOrEmpty(providedKey))
            return false;

        byte[] configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        return CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);
    }
}
