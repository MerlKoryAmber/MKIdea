using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;
using LinuxOps.Web.Services.Files;
using Microsoft.AspNetCore.Components.Forms;

namespace LinuxOps.Web.Services;

/// <summary>Файлы (Ф3, урезанно): одна панель — смотреть и скачать (files.read), загрузить и править текст (files.write).</summary>
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

    /// <summary>Открыть текстовый файл на просмотр или правку (0.7.9). null — отказ, причина — в Notice.</summary>
    /// <summary>
    /// Папка закрыта пользователю SSH (Permission denied): список от root, как «Права: sudo» в настольной версии.
    /// Условия те же, что у чтения файла через sudo: право «Файлы: запись» и sudo у хоста. null — нельзя (причина в тексте).
    /// </summary>
    public async Task<(string Dir, List<FileEntry> Items)?> ListAsRoot(StoredHost h, string dir, Action<string> why)
    {
        if (!CanOn(Perm.FilesWrite, h)) { why("нет прав на папку; через sudo — только с правом «Файлы: запись»"); return null; }
        var cfg = _store.ForConnect(h);
        if (!cfg.UseSudo && !SshRunner.IsRoot(cfg)) { why("нет прав на папку (у хоста не включён sudo)"); return null; }
        WebPrompts.Channel.Value = Prompts;
        var r = await Task.Run(() =>
        {
            using var ssh = SshRunner.Connect(cfg);
            return ssh.RunAsRoot("files-list", SftpBrowser.RootListScript, 60, cfg, new Dictionary<string, string> { ["LINUXOPS_DIR"] = dir });
        });
        if (r.ExitCode != 0)
        {
            why(string.IsNullOrWhiteSpace(r.StdErr) ? $"не удалось открыть через sudo (код {r.ExitCode})" : r.StdErr.Trim());
            return null;
        }
        var (canonical, items) = SftpBrowser.ParseRootList(r.StdOut);
        _audit.Write(Who, "файлы: папка через sudo", $"{h.Config.Title}: {canonical}");
        return (canonical, items);
    }

    public async Task<TextDoc?> OpenText(SftpBrowser b, StoredHost h, string path)
    {
        if (!AllowedOn(Perm.FilesRead, h, $"открыть {path}")) return null;
        var cfg = _store.ForConnect(h);
        WebPrompts.Channel.Value = Prompts;
        try
        {
            var d = await Task.Run(() => RemoteText.Read(b, cfg, path, CanOn(Perm.FilesWrite, h)));
            if (d.Elevated) _audit.Write(Who, "файлы: прочитан через sudo", $"{h.Config.Title}: {path}");
            return d;
        }
        catch (Exception ex)
        {
            ShowNotice($"{h.Config.Title}: {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Сохранить правку. Файл изменился на хосте после открытия — спросить. Итог — текст для строки состояния; null — не сохранено.</summary>
    public async Task<string?> SaveText(SftpBrowser b, StoredHost h, TextDoc d, string text)
    {
        if (!AllowedOn(Perm.FilesWrite, h, $"изменить {d.Path}")) return null;
        var cfg = _store.ForConnect(h);
        WebPrompts.Channel.Value = Prompts;
        try
        {
            if (await Task.Run(() => RemoteText.ChangedOnHost(b, cfg, d))
                && !await Dialogs.Confirm($"{d.Path} на {h.Config.Title} изменился после открытия.\nПерезаписать вашей версией?", "Файл изменился"))
                return null;
            var (ok, detail) = await Task.Run(() => RemoteText.Write(b, cfg, d, text));
            if (!ok)
            {
                _audit.Write(Who, "файлы: ошибка правки", $"{h.Config.Title}: {d.Path}: {detail}");
                ShowNotice($"{h.Config.Title}: {d.Path}: не сохранено — {detail}");
                return null;
            }
            d.Text = text;
            // записано; размер и время — для следующей проверки «изменился на хосте» (не вышло — спросим лишний раз, но не «не сохранено»)
            var size = "";
            try
            {
                var st = await Task.Run(() => RemoteText.Stat(b, cfg, d));
                d.Size = st.Size;
                d.Mtime = st.Mtime;
                size = $" ({FileEntry.FormatSize(st.Size)})";
            }
            catch (Exception) { }
            _audit.Write(Who, "файлы: изменён", $"{h.Config.Title}: {d.Path}{size}, {detail}");
            return $"сохранено {DateTime.Now:HH:mm:ss}, {detail}";
        }
        catch (Exception ex)
        {
            _audit.Write(Who, "файлы: ошибка правки", $"{h.Config.Title}: {d.Path}: {ex.Message}");
            ShowNotice($"{h.Config.Title}: {d.Path}: {ex.Message}");
            return null;
        }
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
