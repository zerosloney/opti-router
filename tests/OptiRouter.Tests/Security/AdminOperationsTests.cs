using OptiRouter.Security;
using Xunit;

namespace OptiRouter.Tests.Security;

/// <summary>
/// 管理面最小 RBAC 的角色×操作矩阵单测（执法单点 AdminOperations.Can）。
/// 安全默认：未显式归入 operator 白名单的非 GET/HEAD 请求一律仅 admin。
/// </summary>
public sealed class AdminOperationsTests
{
    [Theory]
    [InlineData(AdminRole.Viewer, true)]
    [InlineData(AdminRole.Operator, true)]
    [InlineData(AdminRole.Admin, true)]
    public void Can_SessionPing_AnyRoleAllowed(AdminRole role, bool expected)
    {
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/dashboard/session/ping"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer, true)]
    [InlineData(AdminRole.Operator, true)]
    [InlineData(AdminRole.Admin, true)]
    public void Can_DashboardRead_AllRolesAllowed(AdminRole role, bool expected)
    {
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/dashboard/config"));
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/dashboard/keys"));
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/dashboard/requests"));
        // Can 契约为纯 path（query 由中间件剥离）：带查询的同路径规则一致。
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/dashboard/requests/detail"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer, true)]
    [InlineData(AdminRole.Operator, true)]
    [InlineData(AdminRole.Admin, true)]
    public void Can_ModelsRead_AllRolesAllowed(AdminRole role, bool expected)
    {
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/models"));
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/models/raw"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer, false)]
    [InlineData(AdminRole.Operator, false)]
    [InlineData(AdminRole.Admin, true)]
    public void Can_ModelsApikeyReveal_AdminOnly(AdminRole role, bool expected)
    {
        // 明文密钥查看（ModelsConfigHandler 的 ?name= 与 {name}/ 两条 reveal 路由）仅 admin。
        // Can 契约为纯 path：query 由中间件剥离，/api/models/apikey 与 /api/models/{name}/apikey 同规则。
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/models/apikey"));
        Assert.Equal(expected, AdminOperations.Can(role, "GET", "/api/models/gpt-4/apikey"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer, false)]
    [InlineData(AdminRole.Operator, true)]
    [InlineData(AdminRole.Admin, true)]
    public void Can_PutConfig_OperatorAndAbove(AdminRole role, bool expected)
    {
        Assert.Equal(expected, AdminOperations.Can(role, "PUT", "/api/dashboard/config"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer, false)]
    [InlineData(AdminRole.Operator, true)]
    public void Can_OperatorWriteWhitelist(AdminRole role, bool expected)
    {
        Assert.Equal(expected, AdminOperations.Can(role, "PUT", "/api/dashboard/semantic-routes"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/dashboard/sandbox/route"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/dashboard/eval/run"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/dashboard/eval/compare"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/dashboard/learning/reset"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/dashboard/circuits/gpt-4/override"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/models/test"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/models/gpt-4/test"));
        Assert.Equal(expected, AdminOperations.Can(role, "POST", "/api/models/discover"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer)]
    [InlineData(AdminRole.Operator)]
    public void Can_KeyManagement_AdminOnly(AdminRole role)
    {
        // 密钥与身份管理写操作不在 operator 白名单（默认规则 admin-only）。
        Assert.False(AdminOperations.Can(role, "POST", "/api/dashboard/keys"));
        Assert.False(AdminOperations.Can(role, "PUT", "/api/dashboard/keys/kid-1"));
        Assert.False(AdminOperations.Can(role, "DELETE", "/api/dashboard/keys/kid-1"));
        Assert.False(AdminOperations.Can(role, "POST", "/api/dashboard/identities"));
        Assert.False(AdminOperations.Can(role, "DELETE", "/api/dashboard/identities/abc"));
    }

    [Theory]
    [InlineData(AdminRole.Viewer)]
    [InlineData(AdminRole.Operator)]
    public void Can_ModelManagement_AdminOnly(AdminRole role)
    {
        // 模型增删改（ModelsConfigHandler POST/PUT/DELETE）不在 operator 白名单。
        Assert.False(AdminOperations.Can(role, "POST", "/api/models"));
        Assert.False(AdminOperations.Can(role, "PUT", "/api/models/gpt-4"));
        Assert.False(AdminOperations.Can(role, "DELETE", "/api/models/gpt-4"));
    }

    [Fact]
    public void Can_UnmatchedWrite_DefaultsToAdminOnly()
    {
        // 安全默认：新增写端点未显式归入 operator 白名单时，operator/viewer 一律 403。
        Assert.False(AdminOperations.Can(AdminRole.Operator, "POST", "/api/dashboard/some-future-write-endpoint"));
        Assert.False(AdminOperations.Can(AdminRole.Viewer, "POST", "/api/dashboard/some-future-write-endpoint"));
        Assert.True(AdminOperations.Can(AdminRole.Admin, "POST", "/api/dashboard/some-future-write-endpoint"));
    }

    [Fact]
    public void Can_UnknownApiPath_Write_DefaultsToAdminOnly()
    {
        // 非 /api/dashboard、/api/models 前缀的未匹配写路径同样落入默认规则。
        Assert.False(AdminOperations.Can(AdminRole.Operator, "DELETE", "/api/unknown"));
        Assert.False(AdminOperations.Can(AdminRole.Viewer, "POST", "/api/unknown"));
    }

    [Fact]
    public void AdminRoles_TryParse_CaseInsensitive_AndRejectsUnknown()
    {
        Assert.True(AdminRoles.TryParse("Admin", out var admin) && admin == AdminRole.Admin);
        Assert.True(AdminRoles.TryParse("OPERATOR", out var op) && op == AdminRole.Operator);
        Assert.True(AdminRoles.TryParse("viewer", out var viewer) && viewer == AdminRole.Viewer);
        Assert.False(AdminRoles.TryParse("root", out _));
        Assert.False(AdminRoles.TryParse(null, out _));
        Assert.False(AdminRoles.TryParse("", out _));
    }

    [Fact]
    public void AdminRoles_ToValue_IsCanonicalLowercase()
    {
        Assert.Equal("admin", AdminRoles.ToValue(AdminRole.Admin));
        Assert.Equal("operator", AdminRoles.ToValue(AdminRole.Operator));
        Assert.Equal("viewer", AdminRoles.ToValue(AdminRole.Viewer));
    }
}
