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
    private readonly OptiRouter.Configuration.AdminKeyStore _adminKeyStore;
    private readonly LoginRateLimiter _rateLimiter;
    private readonly IConfiguration _config;

    public LoginModel(OptiRouter.Configuration.AdminKeyStore adminKeyStore, LoginRateLimiter rateLimiter, IConfiguration config)
    {
        _adminKeyStore = adminKeyStore;
        _rateLimiter = rateLimiter;
        _config = config;
    }

    [BindProperty]
    public string? AdminKey { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        bool trustProxy = _config.GetValue<bool?>("OptiRouter:TrustProxyHeaders") ?? false;
        string clientIp = LoginRateLimiter.ResolveClientIp(HttpContext, trustProxy);

        // 失败限流：同一 IP 短时间内失败过多即临时锁定，防字典爆破。
        if (_rateLimiter.IsLocked(clientIp))
        {
            ErrorMessage = "登录失败次数过多，请稍后再试";
            return Page();
        }

        // 管理密钥存配置库（SHA256 哈希，AdminKeyStore 统一校验），appsettings 仅首启种子源。
        if (string.IsNullOrWhiteSpace(AdminKey) || !_adminKeyStore.IsValid(AdminKey))
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
}
