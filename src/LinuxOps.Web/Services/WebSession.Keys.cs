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
                lines.Add(await Task.Run(() => SshKeyPlace.Apply(_store, _audit, Who, h, user, sudo, _genPrivate, GeneratedPublic, GeneratedFingerprint, GeneratedComment)));
        }
        finally
        {
            _locks.Release(got);
        }
        Notify();
        return lines;
    }
}
