using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>
/// Постановка уже созданной пары на один хост (0.7.11): дописать открытый, проверить вход, записать секрет, снять старую строку службы.
/// Пароль в базе не стирается. Чужие строки authorized_keys не трогаются.
/// </summary>
public static class SshKeyPlace
{
    public const string CommentPrefix = "linuxops-web ";

    public static bool IsServerKey(string? keyName)
        => keyName != null && keyName.StartsWith(CommentPrefix, StringComparison.Ordinal);

    public static KeyInstallLine Apply(WebStore store, AuditLog audit, string who, StoredHost h, string user, bool? sudo,
        string privateKey, string publicLine, string fingerprint, string comment)
    {
        var title = h.Config.Title;
        HostConfig cfg;
        try { cfg = store.ForConnect(h); }
        catch (Exception ex) { return Fail(audit, who, h, "не прочитан секрет: " + ex.Message); }

        var login = user.Length > 0 ? user : cfg.Username;
        if (string.IsNullOrWhiteSpace(login)) return Fail(audit, who, h, "не задан логин");
        cfg.Username = login;
        if (string.IsNullOrEmpty(cfg.Password) && string.IsNullOrWhiteSpace(cfg.PrivateKeyText))
            return Fail(audit, who, h, "нет ключа или пароля, чтобы войти и записать новый");

        string? oldBlob = null;
        if (!string.Equals(cfg.Auth, "Password", StringComparison.OrdinalIgnoreCase))
        {
            try { oldBlob = SshAuth.OpenSshPublicBlob(cfg); }
            catch (Exception ex) { return Fail(audit, who, h, OneLine(ex.Message)); }
        }

        var put = TryRun(cfg, PutScript, new Dictionary<string, string> { ["LO_PUB"] = publicLine, ["LO_BLOB"] = SshKeyGen.BlobOfLine(publicLine) }, out var putErr);
        if (put is null) return Fail(audit, who, h, EnterFail(login, putErr));
        if (put.ExitCode != 0 || LastLine(put.StdOut) is not ("added" or "present"))
            return Fail(audit, who, h, "не записан открытый ключ: " + Why(put));

        var probe = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = login, Auth = "PrivateKey",
            PrivateKeyText = privateKey, UseSudo = false, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId
        };
        var check = TryRun(probe, "echo ok\n", null, out var checkErr);
        if (check is null) return Fail(audit, who, h, "не переключено: " + EnterFail(login, checkErr));
        if (check.ExitCode != 0 || !check.StdOut.Contains("ok", StringComparison.Ordinal))
            return Fail(audit, who, h, "не переключено: " + Why(check));

        var saved = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = login, Auth = "PrivateKey",
            UseSudo = sudo ?? h.Config.UseSudo, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId, IsSelected = h.Config.IsSelected
        };
        try { store.SaveHosts([(h, saved, null, privateKey, comment)]); }
        catch (Exception ex) { return Fail(audit, who, h, "вход новым есть, в хранилище не записан: " + ex.Message); }

        store.PutSetting("keyat:" + h.Id, DateTime.UtcNow.ToString("o"));
        store.PutSetting("keyerr:" + h.Id, "");

        if (oldBlob is null)
        {
            audit.Write(who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {fingerprint}, вход был паролем");
            return new(title, "пущено новым", true);
        }
        if (string.Equals(oldBlob, SshKeyGen.BlobOfLine(publicLine), StringComparison.Ordinal))
        {
            audit.Write(who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {fingerprint}, тот же ключ");
            return new(title, "пущено новым", true);
        }

        var drop = TryRun(probe, DropScript, new Dictionary<string, string> { ["LO_BLOB"] = oldBlob }, out var dropErr);
        if (drop is null || drop.ExitCode != 0)
        {
            var why = drop is null ? dropErr : Why(drop);
            store.PutSetting("keyerr:" + h.Id, "старый публичный не снят");
            audit.Write(who, "ключ SSH: старый публичный не снят", $"{title} ({cfg.Host}): {why}");
            return new(title, "пущено новым, старый публичный не снят", false);
        }
        if (LastLine(drop.StdOut) == "removed")
        {
            audit.Write(who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {fingerprint}, старый снят");
            return new(title, "пущено новым, старый снят", true);
        }
        store.PutSetting("keyerr:" + h.Id, "старый публичный не снят");
        audit.Write(who, "ключ SSH: старый публичный не снят", $"{title} ({cfg.Host}): строка не найдена");
        return new(title, "пущено новым, старый публичный не снят", false);
    }

    private static KeyInstallLine Fail(AuditLog audit, string who, StoredHost h, string text)
    {
        audit.Write(who, "ключ SSH: не переключено", $"{h.Config.Title} ({h.Config.Host}): {text}");
        return new(h.Config.Title, text, false);
    }

    private static ActionResult? TryRun(HostConfig cfg, string script, IReadOnlyDictionary<string, string>? env, out string error)
    {
        try
        {
            error = "";
            using var ssh = SshRunner.Connect(cfg, TimeSpan.FromSeconds(25));
            return ssh.Run("ssh-key", script, timeoutSeconds: 30, sudo: false, host: cfg, env: env);
        }
        catch (Exception ex)
        {
            error = OneLine(ex.Message);
            return null;
        }
    }

    private static string EnterFail(string login, string error)
        => error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            ? "не вошли как " + login + ": хост не принял этот ключ или пароль"
            : "не вошли как " + login + ": " + error;

    private static string Why(ActionResult r)
    {
        var text = string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr;
        return string.IsNullOrWhiteSpace(text) ? r.Summary : OneLine(text);
    }

    private static string OneLine(string text)
    {
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 180 ? line : line[..180];
    }

    private static string LastLine(string text)
        => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    private const string PutScript = """
        umask 077
        d="$HOME/.ssh"
        install -d -m 700 "$d"
        touch "$d/authorized_keys"
        chmod 600 "$d/authorized_keys"
        if awk -v b="$LO_BLOB" 'NF>=2 && $2==b { found=1 } END { exit !found }' "$d/authorized_keys"; then
          echo present
        else
          printf '%s\n' "$LO_PUB" >> "$d/authorized_keys"
          echo added
        fi
        """;

    private const string DropScript = """
        ak="$HOME/.ssh/authorized_keys"
        [ -f "$ak" ] || { echo missing; exit 0; }
        tmp=$(mktemp)
        awk -v b="$LO_BLOB" 'NF>=2 && $2==b { next } { print }' "$ak" > "$tmp"
        if cmp -s "$ak" "$tmp"; then echo missing; rm -f "$tmp"; exit 0; fi
        cat "$tmp" > "$ak"
        rm -f "$tmp"
        chmod 600 "$ak"
        echo removed
        """;
}
