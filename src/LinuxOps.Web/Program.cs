using LinuxOps.Services;
using LinuxOps.Web.Components;
using LinuxOps.Web.Services;
using LinuxOps.Web.Services.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

// Данные сервиса — до всего остального: ядро берёт каталог из LINUXOPS_DATA_DIR.
WebPaths.Prepare();

// Консоль сервера: LinuxOps.Web admin … — локальный админ (пароль, TOTP, вкл/выкл), без запуска веба.
if (args.Length > 0 && args[0] == "admin")
    return AdminCli.Run(args[1..]);

UserPrompts.Current = new WebPrompts();
AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write("Необработанное исключение", e.ExceptionObject as Exception ?? new Exception("?"));

var builder = WebApplication.CreateBuilder(args);
// Адрес: --urls / ASPNETCORE_URLS / appsettings; по умолчанию только локально.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddRazorComponents().AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddSingleton<SecretBox>();
builder.Services.AddSingleton<WebStore>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<HostLocks>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddScoped<PromptChannel>();
builder.Services.AddScoped<UiDialogs>();
builder.Services.AddScoped<WebSession>();

// Ключи cookie — в каталоге данных: перезапуск службы не выкидывает пользователей.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(WebPaths.DataDir, "dp-keys")))
    .SetApplicationName("LinuxOps.Web");
builder.Services.AddAuthentication(AuthEndpoints.Scheme).AddCookie(o =>
{
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/login";
    o.Cookie.Name = "lo.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // За nginx — https (Secure); напрямую Kestrel слушает только 127.0.0.1.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(24);   // верхняя граница; простой и общий срок — в Validate
    o.SlidingExpiration = false;
    o.Events = new CookieAuthenticationEvents { OnValidatePrincipal = AuthEndpoints.Validate };
});
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();
// За обратным прокси на этом же сервере: адрес клиента — из X-Forwarded-For (доверяем только 127.0.0.1 и ::1).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.Services.GetRequiredService<AuditLog>().Write("сервис", "запуск", $"LinuxOps Web {typeof(WebSession).Assembly.GetName().Version?.ToString(3)}, данные {WebPaths.DataDir}");
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAuth();
// Окно открыто и с ним работают — продлить сессию (иначе через IdleMinutes — выход).
app.MapGet("/auth/ping", () => Results.NoContent()).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();
return 0;
