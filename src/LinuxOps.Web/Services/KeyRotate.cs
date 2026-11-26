using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Строка таблицы в ⚙ → «Ключи SSH». Закрытого ключа здесь нет.</summary>
public sealed record KeyRotateRow(string Host, string Fingerprint, string Changed, string Next, string Error);

/// <summary>
/// Смена ключа, созданного службой (0.7.12). Срок — число дней, пусто и 0 — не менять.
/// «Сменить сейчас» и срок делают одно и то же: одна новая пара на все подходящие хосты,
/// дописать, проверить вход, записать секрет, снять прежнюю строку службы. Свой файл и чужие строки не трогаются.
/// Служба была выключена в день срока — смена при следующем запуске, если срок уже вышел.
/// </summary>
public sealed class KeyRotate(WebStore store, HostLocks locks, AuditLog audit, ILogger<KeyRotate> log) : BackgroundService
{
    public const string Who = "расписание";
    public const int MaxDays = 3650;
    private int _running;

    public bool Running => Volatile.Read(ref _running) == 1;
    public string LastText => store.GetSetting("sshkey.last") ?? "";

    public int Days => ParseDays(store.GetSetting("sshkey.days")) ?? 0;

    /// <summary>Пустая строка — сохранено. Иначе текст ошибки, настройка не менялась.</summary>
    public string SaveDays(string raw, string who)
    {
        var text = raw.Trim();
        int days;
        if (text.Length == 0 || text == "0") days = 0;
        else if (!int.TryParse(text, out days) || days < 1 || days > MaxDays)
            return "Число дней от 1 до " + MaxDays + ". Пусто или 0 — не менять.";
        store.PutSetting("sshkey.days", days.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (days > 0)
            foreach (var h in ServerHosts())
                if (At(h) is null) Stamp(h);
        audit.Write(who, "настройки: смена ключа SSH", days > 0 ? $"каждые {days} дн." : "выключена");
        return "";
    }

    public IReadOnlyList<KeyRotateRow> Rows()
    {
        var days = Days;
        var list = new List<KeyRotateRow>();
        foreach (var h in ServerHosts())
        {
            var at = At(h);
            var fp = FingerprintOf(h);
            var changed = at is { } t ? When(t) : "ещё не отмечено";
            var next = days < 1 ? "срок не задан" : at is { } a ? When(a.AddDays(days)) : "после отметки";
            list.Add(new(h.Config.Title, fp, changed, next, store.GetSetting("keyerr:" + h.Id) ?? ""));
        }
        return list;
    }

    /// <summary>Сменить сейчас все ключи службы, не глядя на срок. false — смена уже идёт.</summary>
    public bool RunNow(string pressedBy)
    {
        if (Running) return false;
        audit.Write(pressedBy, "ключ SSH: смена запущена", "от имени «" + Who + "»");
        _ = Task.Run(async () =>
        {
            try { await Run(Who, onlyDue: false, CancellationToken.None); }
            catch (Exception ex) { log.LogError(ex, "смена ключа SSH"); }
        });
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                if (Days < 1) continue;
                await Run(Who, onlyDue: true, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "смена ключа SSH по сроку"); }
        }
    }

    private async Task Run(string who, bool onlyDue, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        var got = new List<string>();
        try
        {
            var days = Days;
            var hosts = ServerHosts();
            foreach (var h in hosts)
                if (At(h) is null) Stamp(h);
            if (onlyDue)
            {
                if (days < 1) return;
                hosts = hosts.Where(h => At(h) is { } t && DateTime.UtcNow >= t.AddDays(days)).ToList();
                if (hosts.Count == 0) return;
            }
            else if (hosts.Count == 0)
            {
                Finish("ключей службы нет");
                return;
            }

            var keys = hosts.GroupBy(h => HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            got = locks.TryLock(keys.Keys, who, "Ключ SSH", out var busy);
            foreach (var (key, by) in busy)
                store.PutSetting("keyerr:" + keys[key].Id, $"занят: {by.Who}, {by.What}");
            var free = hosts.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)).ToList();
            if (free.Count == 0)
            {
                Finish("все хосты заняты");
                return;
            }

            var comment = SshKeyPlace.CommentPrefix + DateTime.UtcNow.AddHours(3).ToString("yyyy-MM-dd");
            SshKeyPair pair;
            try { pair = SshKeyGen.Create(Path.Combine(WebPaths.DataDir, "keygen"), comment); }
            catch (Exception ex)
            {
                audit.Write(who, "ключ SSH: не создан", ex.Message);
                Finish(ex.Message);
                return;
            }
            audit.Write(who, "ключ SSH: создан", $"{comment}, {pair.Fingerprint}");

            var ok = 0;
            var bad = 0;
            foreach (var h in free)
            {
                ct.ThrowIfCancellationRequested();
                var line = await Task.Run(() => SshKeyPlace.Apply(store, audit, who, h, "", null, pair.PrivateOpenSsh, pair.PublicLine, pair.Fingerprint, comment), ct);
                if (line.Ok) ok++;
                else
                {
                    bad++;
                    if (!line.Text.StartsWith("пущено новым", StringComparison.Ordinal))
                        store.PutSetting("keyerr:" + h.Id, line.Text);
                }
            }
            var tail = busy.Count > 0 ? $", заняты {busy.Count}" : "";
            Finish($"{ok} сменено, {bad} ошибок{tail}");
            audit.Write(who, "ключ SSH: смена закончена", $"{ok} сменено, {bad} ошибок{tail}");
        }
        finally
        {
            locks.Release(got);
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private List<StoredHost> ServerHosts()
        => store.LoadHosts().Where(h => !h.IsWindows && SshKeyPlace.IsServerKey(h.KeyName)).ToList();

    private void Stamp(StoredHost h) => store.PutSetting("keyat:" + h.Id, DateTime.UtcNow.ToString("o"));

    private DateTime? At(StoredHost h)
        => DateTime.TryParse(store.GetSetting("keyat:" + h.Id), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;

    private static string When(DateTime utc) => utc.AddHours(3).ToString("dd.MM.yyyy HH:mm") + " МСК";

    private string FingerprintOf(StoredHost h)
    {
        try
        {
            var blob = SshAuth.OpenSshPublicBlob(store.ForConnect(h));
            return blob is null ? "нет ключа" : SshKeyGen.Fingerprint(blob);
        }
        catch (Exception ex)
        {
            var text = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 80 ? text : text[..80];
        }
    }

    private static int? ParseDays(string? raw)
        => int.TryParse(raw?.Trim(), out var n) && n is >= 0 and <= MaxDays ? n : null;

    private void Finish(string text)
        => store.PutSetting("sshkey.last", DateTime.UtcNow.AddHours(3).ToString("dd.MM.yyyy HH:mm") + " МСК: " + text);
}
