using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using OptiRouter.Configuration;

namespace OptiRouter.Pages;

public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty]
    public string? AdminKey { get; set; }

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnPostAsync()
    {
        var adminKey = _config["OptiRouter:AdminApiKey"];
        var proxyKey = _config["OptiRouter:ProxyApiKey"];

        if (string.IsNullOrWhiteSpace(AdminKey)
            || !(AdminKeyVerifier.IsValid(adminKey, AdminKey) || AdminKeyVerifier.IsValid(proxyKey, AdminKey)))
        {
            ErrorMessage = "密钥不正确，请重试";
            return Page();
        }

        var claims = new[] { new Claim(ClaimTypes.Name, "admin") };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Redirect("/overview");
    }
}
