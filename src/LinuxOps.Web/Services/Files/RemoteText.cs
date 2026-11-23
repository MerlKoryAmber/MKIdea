using System.Text;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services.Files;

/// <summary>Открытый на правку текстовый файл хоста: что прочитали и когда — для проверки «изменился ли на хосте».</summary>
public sealed class TextDoc
{
    public required string Path { get; init; }
    public required string Text { get; set; }
    public long Size { get; set; }
    public DateTime Mtime { get; set; }
    /// <summary>В файле были переводы строк Windows — вернуть их при записи (textarea отдаёт только \n).</summary>
    public bool Crlf { get; init; }
    /// <summary>Прочитан через sudo — и записывать так же.</summary>
    public bool Elevated { get; init; }
}

/// <summary>
/// Правка текстовых файлов в Files (0.7.9) — по образцу настольной (RemoteEditSession): чтение по SFTP, нет прав и у хоста
/// «sudo» — через копию в /tmp (600); запись на месте (inode, права, владелец, SELinux сохраняются) с копией прежней версии
/// в ~/.linuxops-bak, через sudo — в ~root/.linuxops-bak; sudoers — только после visudo -cf.
/// </summary>
public static class RemoteText
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static bool IsSudoers(string path) => path == "/etc/sudoers" || path.StartsWith("/etc/sudoers.d/", StringComparison.Ordinal);

    /// <summary>Прочитать. Ошибка — исключение с понятным текстом.</summary>
    public static TextDoc Read(SftpBrowser b, HostConfig cfg, string path, bool allowSudo)
    {
        void CanElevate()
        {
            if (!allowSudo) throw new InvalidOperationException("нет прав на чтение; через sudo — только с правом «Файлы: запись»");
            if (!cfg.UseSudo && !SshRunner.IsRoot(cfg)) throw new InvalidOperationException("нет прав на чтение (у хоста не включён sudo)");
        }
        var elevated = false;
        (long Size, DateTime Mtime, bool IsDirectory) st;
        // файл в закрытой папке (/etc/sshd_config.d): SFTP не даёт даже stat — всё через root
        try { st = b.StatFull(path); }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException) { CanElevate(); st = RootStat(cfg, path); elevated = true; }
        if (st.IsDirectory) throw new InvalidOperationException("это папка");
        if (st.Size > MaxBytes) throw new InvalidOperationException($"больше {FileEntry.FormatSize(MaxBytes)} — скачайте и правьте на ПК");
        byte[] data;
        if (elevated) data = ReadElevated(b, cfg, path);
        else
        {
            try { data = b.ReadAll(path); }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException) { CanElevate(); data = ReadElevated(b, cfg, path); elevated = true; }
        }
        if (Array.IndexOf(data, (byte)0, 0, Math.Min(data.Length, 8192)) >= 0) throw new InvalidOperationException("файл не текстовый");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("файл не в UTF-8 — скачайте и правьте на ПК"); }
        if (text.Length > 0 && text[0] == '﻿') throw new InvalidOperationException("файл с меткой BOM — скачайте и правьте на ПК");
        // окно правки отдаёт только \n: файл целиком в CRLF — вернём CRLF; смешанные переводы строк сохранить не выйдет
        var crlfN = CountOf(text, "\r\n");
        var crlf = crlfN > 0;
        if (crlf && crlfN != CountOf(text, "\n")) throw new InvalidOperationException("в файле смешаны переводы строк Windows и Linux — скачайте и правьте на ПК");
        return new TextDoc { Path = path, Text = crlf ? text.Replace("\r\n", "\n") : text, Size = st.Size, Mtime = st.Mtime, Crlf = crlf, Elevated = elevated };
    }

    /// <summary>Изменился ли файл на хосте после открытия (размер или время с точностью 2 с).</summary>
    public static bool ChangedOnHost(SftpBrowser b, HostConfig cfg, TextDoc d)
    {
        var now = Stat(b, cfg, d);
        return now.Size != d.Size || Math.Abs((now.Mtime - d.Mtime).TotalSeconds) >= 2;
    }

    /// <summary>Записать. Итог — (сохранено, пояснение: где копия или почему нет).</summary>
    public static (bool Ok, string Detail) Write(SftpBrowser b, HostConfig cfg, TextDoc d, string text)
    {
        if (d.Crlf) text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = new UTF8Encoding(false).GetBytes(text);
        if (bytes.Length > MaxBytes) return (false, $"больше {FileEntry.FormatSize(MaxBytes)}");
        var dir = Directory.CreateTempSubdirectory("lo-edit-");
        var local = System.IO.Path.Combine(dir.FullName, "f");
        try
        {
            File.WriteAllBytes(local, bytes);
            if (d.Elevated || IsSudoers(d.Path)) return Elevated(b, cfg, local, d.Path);
            try
            {
                var note = b.WriteInPlace(local, d.Path);
                return (true, note);
            }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException)
            {
                if (!cfg.UseSudo && !SshRunner.IsRoot(cfg)) return (false, "нет прав на запись (у хоста не включён sudo)");
                return Elevated(b, cfg, local, d.Path);
            }
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private static int CountOf(string s, string what)
    {
        var n = 0;
        for (var i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static (bool, string) Elevated(SftpBrowser b, HostConfig cfg, string local, string dest)
    {
        const string script = """
            set -euo pipefail
            trap 'rm -f -- "$LINUXOPS_SRC"' EXIT
            case "$LINUXOPS_DST" in
              /etc/sudoers|/etc/sudoers.d/*)
                visudo -cf "$LINUXOPS_SRC"
                ;;
            esac
            b="$(getent passwd root | cut -d: -f6)/.linuxops-bak"
            install -d -m 700 -- "$b"
            cp --preserve=mode,ownership -- "$LINUXOPS_DST" "$b/$LINUXOPS_BAK"
            cat -- "$LINUXOPS_SRC" > "$LINUXOPS_DST"
            echo "$b/$LINUXOPS_BAK"
            """;
        var staged = b.Stage(local);
        ActionResult r;
        try
        {
            using var ssh = SshRunner.Connect(cfg);
            r = ssh.RunAsRoot("files-save", script, 60, cfg, new Dictionary<string, string>
            {
                ["LINUXOPS_SRC"] = staged,
                ["LINUXOPS_DST"] = dest,
                ["LINUXOPS_BAK"] = SftpBrowser.BackupName(dest)
            });
        }
        catch
        {
            DropStaged(b, staged);
            throw;
        }
        if (r.ExitCode == 0) return (true, "копия " + r.StdOut.Trim().Split('\n').Last());
        // скрипт мог не начаться (sudo не пустил) — тогда trap не убрал копию в /tmp
        DropStaged(b, staged);
        var text = (r.StdErr + "\n" + r.StdOut).Trim();
        return (false, text.Length == 0 ? "ошибка записи, код " + r.ExitCode : text);
    }

    private static void DropStaged(SftpBrowser b, string staged)
    {
        try { if (b.Exists(staged)) b.Delete(staged); } catch { /* уже удалён скриптом или нет связи */ }
    }

    /// <summary>Размер и время файла; файл в закрытой папке, открытый через sudo, — от root.</summary>
    public static (long Size, DateTime Mtime) Stat(SftpBrowser b, HostConfig cfg, TextDoc d)
    {
        try { return b.Stat(d.Path); }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException) when (d.Elevated) { var r = RootStat(cfg, d.Path); return (r.Size, r.Mtime); }
    }

    /// <summary>stat от root: размер, время изменения (местное, как у SFTP), папка ли.</summary>
    private static (long Size, DateTime Mtime, bool IsDirectory) RootStat(HostConfig cfg, string path)
    {
        using var ssh = SshRunner.Connect(cfg);
        var r = ssh.RunAsRoot("files-stat", "stat -L -c '%s %Y %F' -- \"$LINUXOPS_DST\"", 30, cfg,
            new Dictionary<string, string> { ["LINUXOPS_DST"] = path });
        var p = r.StdOut.Trim().Split('\n').Last().Split(' ', 3);
        if (r.ExitCode != 0 || p.Length < 3 || !long.TryParse(p[0], out var size) || !long.TryParse(p[1], out var sec))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.StdErr) ? "не удалось прочитать через sudo" : r.StdErr.Trim());
        return (size, DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime, p[2] == "directory");
    }

    private static byte[] ReadElevated(SftpBrowser b, HostConfig cfg, string path)
    {
        var staged = "/tmp/linuxops-read-" + Guid.NewGuid().ToString("N");
        using var ssh = SshRunner.Connect(cfg);
        var r = ssh.RunAsRoot("files-read", """
            set -euo pipefail
            umask 077
            cp -- "$LINUXOPS_DST" "$LINUXOPS_SRC"
            chown -- "$LINUXOPS_USER" "$LINUXOPS_SRC"
            chmod 600 -- "$LINUXOPS_SRC"
            """, 30, cfg, new Dictionary<string, string>
        {
            ["LINUXOPS_SRC"] = staged,
            ["LINUXOPS_DST"] = path,
            ["LINUXOPS_USER"] = cfg.Username
        });
        if (r.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.StdErr) ? "не удалось прочитать через sudo" : r.StdErr.Trim());
        try { return b.ReadAll(staged); }
        finally
        {
            try { b.Delete(staged); }
            catch { ssh.RunAsRoot("files-clean", "rm -f -- \"$LINUXOPS_SRC\"", 15, cfg, new Dictionary<string, string> { ["LINUXOPS_SRC"] = staged }); }
        }
    }
}
