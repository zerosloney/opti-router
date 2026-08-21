using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OptiRouter.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }

    // GET 不再执行登出：登出必须经带 AntiForgeryToken 的表单 POST（防跨站登出）
    public IActionResult OnGet() => Redirect("/login");
}
