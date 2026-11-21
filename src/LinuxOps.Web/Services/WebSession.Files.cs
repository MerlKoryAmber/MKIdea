using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;
using LinuxOps.Web.Services.Files;
using Microsoft.AspNetCore.Components.Forms;

namespace LinuxOps.Web.Services;

/// <summary>Файлы (Ф3, урезанно): одна панель — смотреть и скачать (files.read), загрузить (files.write).</summary>
public sealed partial class WebSession
{
    /// <summary>Самый большой файл для загрузки из браузера (идёт через соединение окна — не для образов дисков).</summary>
    public const long MaxUpload = 1L << 30;

    /// <summary>Подключиться по SFTP к хосту (ключ хоста — как в Actions). null — отказ, причина — в Notice.</summary>
    public async Task<SftpBrowser?> OpenFiles(StoredHost h)
    {
        if (!AllowedOn(Perm.FilesRead, h, "файлы")) return null;
        var cfg = _store.ForConnect(h);
        WebPrompts.Channel.Value = Prompts;
        try
        {
            return await Task.Run(() =>
            {
                var b = SftpBrowser.Connect(cfg);
                // имена владельцев вместо UID/GID — как в настольной версии; не вышло — останутся числа
                try
                {
                    using var ssh = SshRunner.Connect(cfg);
                    var r = ssh.Run("files-names", "getent passwd | cut -d: -f1,3; echo ---; getent group | cut -d: -f1,3", 30);
                    var u = new Dictionary<int, string>();
                    var g = new Dictionary<int, string>();
                    var target = u;
                    foreach (var raw in r.StdOut.Split('\n'))
                    {
                        var line = raw.Trim();
                        if (line == "---") { target = g; continue; }
                        var parts = line.Split(':');
                        if (parts.Length == 2 && int.TryParse(parts[1], out var id)) target.TryAdd(id, parts[0]);
                    }
                    b.SetNames(u, g);
                }
                catch (Exception) { }
                return b;
            });
        }
        catch (Exception ex)
        {
            var msg = ex switch
            {
                Renci.SshNet.Common.SshAuthenticationException => $"сервер не принял учётку {cfg.Username} — проверьте пароль или ключ в Inventory",
                Renci.SshNet.Common.SshConnectionException e when e.Message.Contains("Key exchange", StringComparison.OrdinalIgnoreCase) => "ключ хоста не подтверждён — подключение отклонено",
                Renci.SshNet.Common.SshOperationTimeoutException => "хост не ответил вовремя",
                _ => ex.Message
            };
            ShowNotice($"{h.Config.Title}: {msg}");
            return null;
        }
    }

    /// <summary>Ссылка на скачивание файла (одноразовая, 60 с). null — нет права.</summary>
    public string? DownloadLink(StoredHost h, string path, FileDownloads downloads)
    {
        if (!AllowedOn(Perm.FilesRead, h, $"скачать {path}")) return null;
        return "files/download/" + downloads.Issue(User.Login, Who, h.Config.Title, _store.ForConnect(h), path);
    }

    /// <summary>Загрузить файл из браузера в папку на хосте. Файл есть — спросить, заменить ли.</summary>
    public async Task<bool> Upload(SftpBrowser b, StoredHost h, string dir, IBrowserFile file, IProgress<long> progress, CancellationToken ct)
    {
        var path = (dir.TrimEnd('/') + "/" + file.Name).Replace("//", "/");
        if (!AllowedOn(Perm.FilesWrite, h, $"загрузить {path}")) return false;
        if (file.Size > MaxUpload) { ShowNotice($"{file.Name}: больше {FileEntry.FormatSize(MaxUpload)} — загрузите другим способом"); return false; }
        if (b.Exists(path) && !await Dialogs.Confirm($"На {h.Config.Title} уже есть {path}.\nЗаменить?", "Загрузка")) return false;
        try
        {
            await using var src = file.OpenReadStream(MaxUpload, ct);
            await b.UploadFromAsync(src, path, true, progress, ct);
            _audit.Write(Who, "файлы: загружен", $"{h.Config.Title}: {path} ({FileEntry.FormatSize(file.Size)})");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _audit.Write(Who, "файлы: ошибка загрузки", $"{h.Config.Title}: {path}: {ex.Message}");
            ShowNotice($"{h.Config.Title}: {path}: {ex.Message}");
            return false;
        }
    }
}
