using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OptiRouter.Configuration;

namespace OptiRouter.Pages;

// 登录页含密钥输入与失败状态，禁止任何缓存（后退键/共享终端可回看认证态页面）。
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class LoginModel : PageModel
{
    private readonly IConfiguration _config;
    private readonly LoginRateLimiter _rateLimiter;

    public LoginModel(IConfiguration config, LoginRateLimiter rateLimiter)
    {
        _config = config;
        _rateLimiter = rateLimiter;
    }

    [BindProperty]
    public string? AdminKey { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        bool trustProxy = _config.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false;
        string clientIp = ResolveClientIp(HttpContext, trustProxy);

        // 失败限流：同一 IP 短时间内失败过多即临时锁定，防字典爆破。
        if (_rateLimiter.IsLocked(clientIp))
        {
            ErrorMessage = "登录失败次数过多，请稍后再试";
            return Page();
        }

        var adminKey = _config["OptiRouter:AdminApiKey"];

        // 仅接受 AdminApiKey：ProxyApiKey 发给 API 客户端，允许其登录管理台构成权限越界。
        if (string.IsNullOrWhiteSpace(AdminKey) || !AdminKeyVerifier.IsValid(adminKey, AdminKey))
        {
            _rateLimiter.RecordFailure(clientIp);
            ErrorMessage = "密钥不正确，请重试";
            return Page();
        }

        _rateLimiter.Reset(clientIp);

        var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Redirect("/overview");
    }

    // 客户端 IP 解析，与 Program.ResolvePartitionKey 的 IP 分支保持一致（TrustProxyHeaders 控制）。
    private static string ResolveClientIp(HttpContext context, bool trustProxyHeaders)
    {
        var headers = context.Request.Headers;
        if (trustProxyHeaders && headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrEmpty(cfIp))
            return cfIp.ToString();
        if (trustProxyHeaders && headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrEmpty(xff))
            return xff.ToString().Split(',')[0].Trim();
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
