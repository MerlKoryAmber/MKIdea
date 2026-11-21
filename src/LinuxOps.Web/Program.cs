using LinuxOps.Services;
using LinuxOps.Web.Components;
using LinuxOps.Web.Services;

// Данные сервиса — до всего остального: ядро берёт каталог из LINUXOPS_DATA_DIR.
WebPaths.Prepare();
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
builder.Services.AddScoped<PromptChannel>();
builder.Services.AddScoped<UiDialogs>();
builder.Services.AddScoped<WebSession>();

var app = builder.Build();
// За обратным прокси на этом же сервере: адрес клиента — из X-Forwarded-For (доверяем только 127.0.0.1 и ::1).
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
app.Services.GetRequiredService<AuditLog>().Write("сервис", "запуск", $"LinuxOps Web {typeof(WebSession).Assembly.GetName().Version?.ToString(3)}, данные {WebPaths.DataDir}");
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
