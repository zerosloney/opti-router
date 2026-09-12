namespace OptiRouter.Configuration;

/// <summary>
/// 配置文档的纯计算契约，SQLite（<see cref="AppConfigDbStore"/>）与 MariaDB
/// （<see cref="MariaDbAppConfigStore"/>）两套后端共享。
/// <para>
/// 版本哈希必须单源：快照读取与 CAS 保存（MariaDB 条件更新/SQLite 文件锁）三方
/// 依赖同一版本串，实现漂移会让乐观锁误判冲突或漏判并发覆盖。
/// </para>
/// </summary>
internal static class ConfigDocumentContract
{
    /// <summary>
    /// 计算路由/预算两份文档的内容版本（SHA256，小写 hex）。
    /// 长度前缀防拼接歧义（"ab"+"c" 与 "a"+"bc" 产生不同内容）。
    /// </summary>
    public static string ComputeDocumentsVersion(string? routingJson, string? budgetJson)
    {
        routingJson ??= string.Empty;
        budgetJson ??= string.Empty;
        string content = $"{routingJson.Length}:{routingJson}{budgetJson.Length}:{budgetJson}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }
}
