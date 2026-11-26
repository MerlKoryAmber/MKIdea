using LinuxOps.Models;
using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Одна строка постановки ключа — в окне «Учётка SSH».</summary>
public sealed record KeyInstallLine(string Host, string Text, bool Ok);

/// <summary>
/// Ключ, созданный службой (0.7.11): пара в памяти окна, на хост уходит только открытый.
/// Применить — дописать authorized_keys текущим доступом, проверить вход новым, записать секрет, снять старую строку.
/// </summary>
public sealed partial class WebSession
{
    private string _genPrivate = "";

    public bool HasGeneratedKey => _genPrivate.Length > 0;
    public string GeneratedPublic { get; private set; } = "";
    public string GeneratedFingerprint { get; private set; } = "";
    public string GeneratedComment { get; private set; } = "";

    /// <summary>Новая пара ed25519. В браузер попадает только открытый ключ. Пустая строка — успех, иначе причина.</summary>
    public string CreateServerKey()
    {
        try
        {
            var comment = "linuxops-web " + DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
            var pair = SshKeyGen.Create(Path.Combine(WebPaths.DataDir, "keygen"), comment);
            _genPrivate = pair.PrivateOpenSsh;
            GeneratedPublic = pair.PublicLine;
            GeneratedFingerprint = pair.Fingerprint;
            GeneratedComment = comment;
            _audit.Write(Who, "ключ SSH: создан", $"{comment}, {pair.Fingerprint}");
            return "";
        }
        catch (Exception ex)
        {
            _audit.Write(Who, "ключ SSH: не создан", ex.Message);
            return ex.Message;
        }
    }

    public void ClearGeneratedKey()
    {
        _genPrivate = "";
        GeneratedPublic = "";
        GeneratedFingerprint = "";
        GeneratedComment = "";
    }

    /// <summary>
    /// Поставить созданный ключ на отмеченные хосты Linux. Право «Inventory» — на каждый, без права хост пропускается.
    /// Пароль в базе не стирается. Открытые сеансы не закрываются.
    /// </summary>
    public async Task<IReadOnlyList<KeyInstallLine>> InstallGeneratedKey(IReadOnlyList<StoredHost> hosts, string user, bool? sudo)
    {
        if (!HasGeneratedKey || !SshKeyGen.IsPublicLine(GeneratedPublic))
            return [new("", "Сначала создайте ключ на сервере.", false)];
        var list = hosts.Where(h => !h.IsWindows && Hosts.Contains(h)).ToList();
        if (list.Count == 0) return [new("", "Не выбрано ни одного хоста Linux.", false)];
        if (!Allowed(Perm.HostsEdit, "ключ SSH")) return [new("", "Нет права «Inventory».", false)];

        var lines = new List<KeyInstallLine>();
        var todo = new List<StoredHost>();
        foreach (var h in list)
        {
            if (!CanOn(Perm.HostsEdit, h))
            {
                Deny(Perm.HostsEdit, $"ключ SSH: {h.Config.Title}");
                lines.Add(new(h.Config.Title, "нет права «Inventory»", false));
            }
            else todo.Add(h);
        }
        if (todo.Count == 0) return lines;

        var keys = todo.GroupBy(h => HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var got = _locks.TryLock(keys.Keys, Who, "Ключ SSH", out var busy);
        foreach (var (key, by) in busy)
            lines.Add(new(keys[key].Config.Title, $"занят: {by.Who}, {by.What}", false));
        var free = todo.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)).ToList();
        WebPrompts.Channel.Value = Prompts;
        try
        {
            foreach (var h in free)
                lines.Add(await Task.Run(() => InstallOne(h, user, sudo)));
        }
        finally
        {
            _locks.Release(got);
        }
        Notify();
        return lines;
    }

    private KeyInstallLine InstallOne(StoredHost h, string user, bool? sudo)
    {
        var title = h.Config.Title;
        HostConfig cfg;
        try { cfg = _store.ForConnect(h); }
        catch (Exception ex) { return Fail(h, "не прочитан секрет: " + ex.Message); }

        var login = user.Length > 0 ? user : cfg.Username;
        if (string.IsNullOrWhiteSpace(login)) return Fail(h, "не задан логин");
        cfg.Username = login;
        if (string.IsNullOrEmpty(cfg.Password) && string.IsNullOrWhiteSpace(cfg.PrivateKeyText))
            return Fail(h, "нет ключа или пароля, чтобы войти и записать новый");

        string? oldBlob = null;
        if (!string.Equals(cfg.Auth, "Password", StringComparison.OrdinalIgnoreCase))
        {
            try { oldBlob = SshAuth.OpenSshPublicBlob(cfg); }
            catch (Exception ex) { return Fail(h, OneLine(ex.Message)); }
        }

        var put = TryRun(cfg, PutScript, new Dictionary<string, string> { ["LO_PUB"] = GeneratedPublic, ["LO_BLOB"] = SshKeyGen.BlobOfLine(GeneratedPublic) }, out var putErr);
        if (put is null) return Fail(h, EnterFail(login, putErr));
        if (put.ExitCode != 0) return Fail(h, "не записан открытый ключ: " + Why(put));
        var putMark = LastLine(put.StdOut);
        if (putMark is not ("added" or "present")) return Fail(h, "не записан открытый ключ: " + Why(put));

        var probe = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = login, Auth = "PrivateKey",
            PrivateKeyText = _genPrivate, UseSudo = false, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId
        };
        var check = TryRun(probe, "echo ok\n", null, out var checkErr);
        if (check is null) return Fail(h, "не переключено: " + EnterFail(login, checkErr));
        if (check.ExitCode != 0 || !check.StdOut.Contains("ok", StringComparison.Ordinal))
            return Fail(h, "не переключено: " + Why(check));

        var saved = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = login, Auth = "PrivateKey",
            UseSudo = sudo ?? h.Config.UseSudo, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId, IsSelected = h.Config.IsSelected
        };
        try { _store.SaveHosts([(h, saved, null, _genPrivate, GeneratedComment)]); }
        catch (Exception ex) { return Fail(h, "вход новым есть, в хранилище не записан: " + ex.Message); }

        if (oldBlob is null)
        {
            _audit.Write(Who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {GeneratedFingerprint}, вход был паролем");
            return new(title, "пущено новым", true);
        }
        if (string.Equals(oldBlob, SshKeyGen.BlobOfLine(GeneratedPublic), StringComparison.Ordinal))
        {
            _audit.Write(Who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {GeneratedFingerprint}, тот же ключ");
            return new(title, "пущено новым", true);
        }

        var drop = TryRun(probe, DropScript, new Dictionary<string, string> { ["LO_BLOB"] = oldBlob }, out var dropErr);
        if (drop is null)
        {
            _audit.Write(Who, "ключ SSH: старый публичный не снят", $"{title} ({cfg.Host}): {dropErr}");
            return new(title, "пущено новым, старый публичный не снят", false);
        }
        if (drop.ExitCode != 0)
        {
            _audit.Write(Who, "ключ SSH: старый публичный не снят", $"{title} ({cfg.Host}): {Why(drop)}");
            return new(title, "пущено новым, старый публичный не снят", false);
        }
        var mark = LastLine(drop.StdOut);
        if (mark == "removed")
        {
            _audit.Write(Who, "ключ SSH: поставлен", $"{title} ({cfg.Host}), {GeneratedFingerprint}, старый снят");
            return new(title, "пущено новым, старый снят", true);
        }
        _audit.Write(Who, "ключ SSH: старый публичный не снят", $"{title} ({cfg.Host}): строка не найдена");
        return new(title, "пущено новым, старый публичный не снят", false);
    }

    private KeyInstallLine Fail(StoredHost h, string text)
    {
        _audit.Write(Who, "ключ SSH: не переключено", $"{h.Config.Title} ({h.Config.Host}): {text}");
        return new(h.Config.Title, text, false);
    }

    /// <summary>Connect бросает исключение (не тот логин, хост не пустил) — в окно, а не в обрыв сеанса.</summary>
    private static ActionResult? TryRun(HostConfig cfg, string script, IReadOnlyDictionary<string, string>? env, out string error)
    {
        try
        {
            error = "";
            return Run(cfg, script, env);
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

    private static ActionResult Run(HostConfig cfg, string script, IReadOnlyDictionary<string, string>? env)
    {
        using var ssh = SshRunner.Connect(cfg, TimeSpan.FromSeconds(25));
        return ssh.Run("ssh-key", script, timeoutSeconds: 30, sudo: false, host: cfg, env: env);
    }

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

    // LO_PUB / LO_BLOB приходят через stdin в base64 (SshRunner.EncodeEnv). Чужие строки файла не трогаем.
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
