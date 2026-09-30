namespace OptiRouter.Security;

/// <summary>管理面角色层级：Admin &gt; Operator &gt; Viewer（执法单点在 AdminOperations.Can）。</summary>
public enum AdminRole
{
    Admin,
    Operator,
    Viewer
}

/// <summary>
/// 角色字符串口径：持久化（admin-identities 文档 role 字段）与登录 Cookie 的
/// ClaimTypes.Role claim 统一用小写 "admin"/"operator"/"viewer"，两侧解析同走本类。
/// </summary>
public static class AdminRoles
{
    /// <summary>持久化/claim 用的规范字符串（小写）。</summary>
    public static string ToValue(AdminRole role) => role switch
    {
        AdminRole.Admin => "admin",
        AdminRole.Operator => "operator",
        _ => "viewer"
    };

    /// <summary>中文显示名（管理台 UI 用）。</summary>
    public static string ToDisplay(AdminRole role) => role switch
    {
        AdminRole.Admin => "管理员",
        AdminRole.Operator => "运维",
        _ => "只读"
    };

    /// <summary>大小写不敏感解析；未知值返回 false。</summary>
    public static bool TryParse(string? value, out AdminRole role)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "admin": role = AdminRole.Admin; return true;
            case "operator": role = AdminRole.Operator; return true;
            case "viewer": role = AdminRole.Viewer; return true;
            default: role = AdminRole.Viewer; return false;
        }
    }
}

/// <summary>
/// 管理面 API 的角色执法单点：中间件认证成功后、放行前调用 <see cref="Can"/>，
/// 不过即 403。矩阵安全默认：未显式归入 operator 白名单的非 GET/HEAD 请求一律仅 admin——
/// 新增写端点时必须显式加入 <see cref="IsOperatorWrite"/>，否则 operator/viewer 将被 403。
/// </summary>
public static class AdminOperations
{
    public static bool Can(AdminRole role, string httpMethod, string path)
    {
        // 会话保活（DashboardHandler.Helpers.cs:1571）：浏览器侧 Cookie 续期心跳，任何角色放行。
        if (path.Equals("/api/dashboard/session/ping", StringComparison.OrdinalIgnoreCase))
            return true;

        // admin 全放行（矩阵的"未匹配默认 admin-only"由此短路实现）。
        if (role == AdminRole.Admin)
            return true;

        bool isRead = httpMethod is "GET" or "HEAD";
        if (isRead)
        {
            // 例外 1：上游密钥明文查看仅 admin（ModelsConfigHandler.cs 的两条 reveal 路由
            // /api/models/apikey?name= 与 /api/models/{name}/apikey 返回同一明文，按同一规则拦截）。
            if (path.StartsWith("/api/models", StringComparison.Ordinal)
                && path.EndsWith("/apikey", StringComparison.Ordinal))
                return false;

            // 例外 2：管理身份列表仅 admin——viewer/operator 可读全管理面，但管理账号的
            // 存在与角色构成不对下位角色暴露（已决策口径，见 docs/CONFIGURATION.md 管理台角色）。
            if (path.StartsWith("/api/dashboard/identities", StringComparison.Ordinal))
                return false;

            // viewer+ 只读：GET/HEAD 的管理查询放行（/api/dashboard/*、/api/models/*）。
            return path.StartsWith("/api/dashboard", StringComparison.Ordinal)
                || path.StartsWith("/api/models", StringComparison.Ordinal);
        }

        return role == AdminRole.Operator && IsOperatorWrite(httpMethod, path);
    }

    /// <summary>
    /// operator+ 配置写白名单（全部为无密钥面、可由运维执行的配置类操作）。
    /// 密钥/身份/模型管理写操作不在列——默认规则兜底为 admin-only。
    /// </summary>
    private static bool IsOperatorWrite(string httpMethod, string path) => httpMethod switch
    {
        "POST" =>
            // 沙箱试路由（DashboardHandler.Helpers.cs:1010）
            path.StartsWith("/api/dashboard/sandbox/", StringComparison.Ordinal)
            // 评测运行/对比（DashboardHandler.Helpers.cs:1035,1077）
            || path.StartsWith("/api/dashboard/eval/", StringComparison.Ordinal)
            // 学习状态重置（DashboardHandler.Helpers.cs:859）
            || path.Equals("/api/dashboard/learning/reset", StringComparison.Ordinal)
            // 熔断手动覆写（DashboardHandler.Helpers.cs:1468）
            || path.StartsWith("/api/dashboard/circuits/", StringComparison.Ordinal)
            // 模型连通性测试（ModelsConfigHandler.cs:159,161 两条 test 路由，UI 走 ?name= 形式）
            || (path.StartsWith("/api/models/", StringComparison.Ordinal) && path.EndsWith("/test", StringComparison.Ordinal))
            || path.Equals("/api/models/test", StringComparison.Ordinal)
            // 上游模型发现（ModelsConfigHandler.cs:176）
            || path.Equals("/api/models/discover", StringComparison.Ordinal),
        "PUT" =>
            // 语义路由整表保存（DashboardHandler.Helpers.cs:1172）
            path.Equals("/api/dashboard/semantic-routes", StringComparison.Ordinal)
            // 路由/预算系统配置（CAS 版本，DashboardHandler.Helpers.cs:1330）
            || path.Equals("/api/dashboard/config", StringComparison.Ordinal),
        _ => false
    };
}
