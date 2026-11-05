using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[AllowAnonymous]
public sealed class AccountController(UserStore users, SessionStore sessions, AuditLog audit) : Controller
{
    private static readonly LoginThrottle Throttle = new();

    private bool AuthOn => new AuthOptions().Enabled || users.Enabled;

    [HttpGet]
    public IActionResult Login(string? returnUrl)
    {
        if (!AuthOn) return Redirect("/");
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(Safe(returnUrl));
        return View(new LoginViewModel(returnUrl, null, users.Enabled));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? username, string? password, string? returnUrl)
    {
        var auth = new AuthOptions();
        if (!AuthOn) return Redirect("/");
        var who = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (Throttle.IsBlocked(who))
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return View(new LoginViewModel(returnUrl, "Too many wrong attempts. Try again in a few minutes.", users.Enabled));
        }
        var account = users.Enabled ? users.Check(username, password) : null;
        var valid = users.Enabled ? account is not null : auth.Check(password);
        if (!valid)
        {
            Throttle.Fail(who);
            audit.Record(users.Enabled ? username ?? "" : "ui", who, "login_failed");
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return View(new LoginViewModel(returnUrl, users.Enabled ? "Wrong username or password." : "Wrong password.", users.Enabled));
        }
        Throttle.Reset(who);
        // Without accounts the shared password is the admin.
        var name = account?.Name ?? "ui";
        var role = account?.Role ?? "admin";
        var session = sessions.Start(name, role, who);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role),
            new Claim("pwv", auth.Version), new Claim("sid", session.Id)], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        audit.Record(name, who, "login", null, role);
        return LocalRedirect(Safe(returnUrl));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        if (!AuthOn) return Redirect("/");
        if (User.FindFirst("sid")?.Value is { } sid) sessions.Revoke(sid);
        audit.Record(User.Identity?.Name ?? "unknown", HttpContext.Connection.RemoteIpAddress?.ToString(), "logout");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/Account/Login");
    }

    private string Safe(string? returnUrl) => Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
}