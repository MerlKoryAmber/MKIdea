using LinuxOps.Services;
using LinuxOps.Web.Services.Guac;
using LinuxOps.Web.Components;
using LinuxOps.Web.Services;
using LinuxOps.Web.Services.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

// Данные сервиса — до всего остального: ядро берёт каталог из LINUXOPS_DATA_DIR.
WebPaths.Prepare();
// Тексты ядра («через этот ПК») — про сервер; до первого обращения к BuiltinActions.
RunPlace.Server();

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
builder.Services.AddSingleton<MkMfa>();
builder.Services.AddSingleton<PsConsole>();
builder.Services.AddSingleton<LinuxOps.Web.Services.Api.ApiJobs>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddSingleton<LinuxOps.Web.Services.Guac.GuacTickets>();
builder.Services.AddSingleton<LinuxOps.Web.Services.Guac.GuacLive>();
builder.Services.AddSingleton<LinuxOps.Web.Services.Files.FileDownloads>();
builder.Services.AddSingleton<FleetCheck>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetCheck>());
builder.Services.AddSingleton<KeyRotate>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<KeyRotate>());
builder.Services.AddSingleton<RecordingsKeeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RecordingsKeeper>());
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
// Выход в интернет (CVE, туннель через сервер) — только прокси из ⚙, системный не подхватывается (0.7.9).
NetProxy.Apply(app.Services.GetRequiredService<WebStore>());
// Статика (стили, скрипты, _framework Blazor): браузер хранит, но каждый раз сверяет с сервером (ETag → 304).
// После обновления у всех сразу свежие файлы, даже если в адресе нет ?v= (у _framework его нет). Пользователей мало — проверки ничего не стоят.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});
// Страницы (вход, приложение) и ответы /auth — не хранить вовсе: форма входа с токеном, QR и строка TOTP при привязке.
app.Use((ctx, next) =>
{
    var p = ctx.Request.Path;
    if (p == "/" || p.StartsWithSegments("/login") || p.StartsWithSegments("/auth"))
        ctx.Response.OnStarting(() => { ctx.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
    return next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAuth();
LinuxOps.Web.Services.Api.ApiEndpoints.MapApi(app);
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.MapGuac();
LinuxOps.Web.Services.Files.FileDownloads.Map(app);
// Окно открыто и с ним работают — продлить сессию (иначе через IdleMinutes — выход).
app.MapGet("/auth/ping", () => Results.NoContent()).RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
app.Run();
return 0;
