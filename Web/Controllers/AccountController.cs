using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[AllowAnonymous]
public sealed class AccountController : Controller
{
    private static readonly LoginThrottle Throttle = new();

    [HttpGet]
    public IActionResult Login(string? returnUrl)
    {
        if (!new AuthOptions().Enabled) return Redirect("/");
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(Safe(returnUrl));
        return View(new LoginViewModel(returnUrl, null));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? password, string? returnUrl)
    {
        var auth = new AuthOptions();
        if (!auth.Enabled) return Redirect("/");
        var who = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Throttle.IsBlocked(who))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return View(new LoginViewModel(returnUrl, "Too many wrong attempts. Try again in a few minutes."));
        }
        if (!auth.Check(password))
        {
            Throttle.Fail(who);
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return View(new LoginViewModel(returnUrl, "Wrong password."));
        }
        Throttle.Reset(who);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "ui"), new Claim("pwv", auth.Version)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return LocalRedirect(Safe(returnUrl));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        if (!new AuthOptions().Enabled) return Redirect("/");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/Account/Login");
    }

    private string Safe(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
}