using System.Collections.Concurrent;
using System.Security.Cryptography;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services.Files;

/// <summary>
/// Скачивание файла с хоста в браузер. Окно (после проверки права «файлы: чтение») выдаёт одноразовую ссылку
/// на 60 с для того же пользователя; по ней сервис сам читает файл по SFTP и отдаёт потоком — без временных файлов.
/// </summary>
public sealed class FileDownloads
{
    private sealed record Ticket(string Login, string Who, string HostTitle, HostConfig Cfg, string Path, DateTime Expires);

    private readonly ConcurrentDictionary<string, Ticket> _map = new();

    public string Issue(string login, string who, string hostTitle, HostConfig cfg, string path)
    {
        foreach (var old in _map.Where(kv => kv.Value.Expires < DateTime.UtcNow).Select(kv => kv.Key).ToList()) _map.TryRemove(old, out _);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _map[id] = new Ticket(login, who, hostTitle, cfg, path, DateTime.UtcNow.AddSeconds(60));
        return id;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/files/download/{id}", Download).RequireAuthorization();
    }

    private static IResult Download(HttpContext ctx, string id, FileDownloads tickets, AuditLog audit)
    {
        if (!tickets._map.TryRemove(id, out var t) || t.Expires < DateTime.UtcNow
            || !string.Equals(t.Login, ctx.User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(403);
        var name = System.IO.Path.GetFileName(t.Path.TrimEnd('/'));
        // ключ хоста уже подтверждён в окне (KnownHostsStore); без окна новый ключ не примется
        return Results.Stream(async body =>
        {
            try
            {
                using var b = await Task.Run(() => SftpBrowser.Connect(t.Cfg));
                long bytes = 0;
                await b.DownloadToAsync(t.Path, body, new Progress<long>(n => bytes = n), ctx.RequestAborted);
                audit.Write(t.Who, "файлы: скачан", $"{t.HostTitle}: {t.Path}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                audit.Write(t.Who, "файлы: ошибка скачивания", $"{t.HostTitle}: {t.Path}: {ex.Message}");
                throw;
            }
        }, "application/octet-stream", name);
    }
}
