using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Формы входа (POST /auth/*). Между паролем и кодом TOTP — защищённая cookie «ждём второй шаг» на 5 минут.
/// Текст ошибки — тоже в защищённой cookie на минуту, не в адресе (чужой текст на странице входа не подставить).
/// </summary>
public static class AuthEndpoints
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    private const string PendingCookie = "lo.pending", MsgCookie = "lo.msg";

    public static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } a
        ? (a.IsIPv4MappedToIPv6 ? a.MapToIPv4().ToString() : a.ToString())
        : "?";

    private static ITimeLimitedDataProtector Protector(HttpContext ctx, string purpose) =>
        ctx.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("lo.auth." + purpose).ToTimeLimitedDataProtector();

    private static CookieOptions Opts(HttpContext ctx, TimeSpan life) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = "/", MaxAge = life
    };

    /// <summary>Логин, который прошёл пароль и ждёт код (или null).</summary>
    public static string? Pending(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(PendingCookie, out var v)) return null;
        try { return Protector(ctx, "pending").Unprotect(v); } catch { return null; }
    }

    /// <summary>Сообщение для страницы входа (одноразовое).</summary>
    public static string? TakeMessage(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(MsgCookie, out var v)) return null;
        ctx.Response.Cookies.Delete(MsgCookie);
        try { return Protector(ctx, "msg").Unprotect(v); } catch { return null; }
    }

    private static IResult Back(HttpContext ctx, string message, string step = "")
    {
        ctx.Response.Cookies.Append(MsgCookie, Protector(ctx, "msg").Protect(message, TimeSpan.FromMinutes(1)), Opts(ctx, TimeSpan.FromMinutes(1)));
        return Results.Redirect("/login" + (step.Length > 0 ? "?step=" + step : ""));
    }

    private static async Task SignIn(HttpContext ctx, UserInfo user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Login), new("display", user.Display), new("kind", user.Kind),
            new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        claims.AddRange(user.Roles.Select(r => new Claim("role", r.Id)));
        await ctx.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)),
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = DateTimeOffset.UtcNow });
        ctx.Response.Cookies.Delete(PendingCookie);
    }

    /// <summary>Нет или не тот токен антиподделки — 400, а не 500.</summary>
    private static async Task<bool> FormOk(HttpContext ctx, IAntiforgery af)
    {
        try { await af.ValidateRequestAsync(ctx); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    public static void MapAuth(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext ctx, LoginService svc, IAntiforgery af) =>
        {
            if (!await FormOk(ctx, af)) return Back(ctx, "Страница входа устарела. Попробуйте ещё раз.");
            var f = await ctx.Request.ReadFormAsync();
            var login = f["login"].ToString();
            // PBKDF2 и LDAP — в пуле, не на потоке запроса
            var r = await Task.Run(() => svc.Password(login, f["password"].ToString(), ClientIp(ctx)));
            switch (r.Step)
            {
                case LoginService.Step.Done:
                    await SignIn(ctx, r.User!);
                    return Results.Redirect("/");
                case LoginService.Step.Totp:
                case LoginService.Step.TotpSetup:
                    ctx.Response.Cookies.Append(PendingCookie, Protector(ctx, "pending").Protect(login.Trim(), TimeSpan.FromMinutes(5)), Opts(ctx, TimeSpan.FromMinutes(5)));
                    return Results.Redirect(r.Step == LoginService.Step.Totp ? "/login?step=totp" : "/login?step=setup");
                default:
                    return Back(ctx, r.Error);
            }
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/auth/totp", async (HttpContext ctx, LoginService svc, IAntiforgery af) =>
        {
            if (!await FormOk(ctx, af)) return Back(ctx, "Страница входа устарела. Войдите снова.");
            var pending = Pending(ctx);
            if (pending is null || !svc.IsLocalAdmin(pending)) return Back(ctx, "Время на ввод кода вышло. Войдите снова.");
            var f = await ctx.Request.ReadFormAsync();
            var r = svc.TotpCode(f["code"].ToString(), ClientIp(ctx));
            if (r.Step != LoginService.Step.Done) return Back(ctx, r.Error, f["setup"] == "1" ? "setup" : "totp");
            await SignIn(ctx, r.User!);
            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/auth/logout", async (HttpContext ctx, AuditLog audit, IAntiforgery af) =>
        {
            if (!await FormOk(ctx, af)) return Results.BadRequest();
            if (ctx.User.Identity?.IsAuthenticated == true) audit.Write($"{ctx.User.Identity.Name} ({ClientIp(ctx)})", "выход");
            await ctx.SignOutAsync(Scheme);
            return Results.Redirect("/login");
        }).DisableAntiforgery();
    }

    /// <summary>
    /// Проверка cookie на каждом запросе: общий срок сессии, простой (без запросов дольше IdleMinutes),
    /// локальный админ не выключен, роли ещё есть. Иначе — выход.
    /// </summary>
    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var store = ctx.HttpContext.RequestServices.GetRequiredService<WebStore>();
        var svc = ctx.HttpContext.RequestServices.GetRequiredService<LoginService>();
        var s = store.LoadAuth();
        var p = ctx.Principal!;
        var now = DateTimeOffset.UtcNow;
        var iat = long.TryParse(p.FindFirstValue("iat"), out var t) ? DateTimeOffset.FromUnixTimeSeconds(t) : DateTimeOffset.MinValue;
        var issued = ctx.Properties.IssuedUtc ?? iat;
        var user = svc.Resolve(p.Identity?.Name ?? "", p.FindFirstValue("kind") ?? "", p.FindFirstValue("display") ?? "", p.FindAll("role").Select(c => c.Value));
        if (now - iat > TimeSpan.FromHours(Math.Max(1, s.MaxHours)) || now - issued > TimeSpan.FromMinutes(Math.Max(5, s.IdleMinutes)) || user is null)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(Scheme);
            return;
        }
        if (now - issued > TimeSpan.FromMinutes(1)) ctx.ShouldRenew = true;
    }
}
