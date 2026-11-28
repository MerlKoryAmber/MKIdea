using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Строка проверки «Закрыть доступ»: что сейчас на хосте и что будет сделано.</summary>
public sealed record AccessPreview(StoredHost Host, string Now, string Plan, bool Applicable);

/// <summary>Базовый пользователь для окна — без пароля.</summary>
public sealed record BaseUserView(string Login, bool Sudo, string Keys, string Comment, bool Enabled, bool HasPassword)
{
    public string KeysText
    {
        get
        {
            var fp = UserProvisioning.KeyFingerprints(Keys);
            return fp.Count == 0 ? "ключей нет" : $"ключей {fp.Count}: {string.Join(", ", fp)}";
        }
    }
}

/// <summary>Ключ учётки из последнего аудита — для «Отозвать ключ» галками.</summary>
public sealed class AuditKeyPick
{
    public required string Type { get; init; }
    public required string Fingerprint { get; init; }
    public required string Comment { get; init; }
    public required string Blob { get; init; }
    public List<string> Hosts { get; } = new();
    public bool Picked { get; set; }
    public string Line => Blob + (Comment.Length > 0 ? " " + Comment : "");
}

/// <summary>«Закрыть доступ» и «Базовые пользователи» в вебе (0.7.14) — как в настольной версии, на общем ядре.</summary>
public sealed partial class WebSession
{
    // ---------- что известно об учётках ----------

    /// <summary>Логины для списка: базовые пользователи и учётки из аудитов этой сессии (UID ≥ 1000) на этих хостах.</summary>
    public List<string> KnownLogins(IEnumerable<StoredHost> hosts)
    {
        var names = hosts.Select(h => h.Config.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var audited = _audits.Values.Where(a => names.Contains(a.Host)).SelectMany(a => a.Users).Where(u => u.Uid >= 1000).Select(u => u.Login);
        return _store.LoadBaseUsers().Select(u => u.Login).Concat(audited)
            .Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
    }

    /// <summary>По аудиту: строка на хост (sudo, блокировка, ключи) и ключи учётки — как в настольной.</summary>
    public (string Info, List<AuditKeyPick> Keys) AuditOf(string login, IEnumerable<StoredHost> hosts)
    {
        var lines = new List<string>();
        var keys = new Dictionary<string, AuditKeyPick>(StringComparer.Ordinal);
        if (login.Length == 0 || _audits.Count == 0) return ("", new());
        foreach (var h in hosts)
        {
            if (!_audits.TryGetValue(h.Config.Name, out var report)) { lines.Add($"{h.Config.Title}: аудита не было"); continue; }
            var user = report.Users.FirstOrDefault(u => u.Login == login);
            if (user is null) { lines.Add($"{h.Config.Title}: учётки нет"); continue; }
            var parts = new List<string>();
            if (user.HasSudo) parts.Add(user.SudoText);
            if (user.IsLocked) parts.Add(user.LockText);
            parts.Add(user.KeysTitle);
            lines.Add($"{h.Config.Title}: " + string.Join(" · ", parts));
            foreach (var k in user.Keys.Where(k => k.Blob.Length > 0))
            {
                if (!keys.TryGetValue(k.Blob, out var pick))
                    keys[k.Blob] = pick = new AuditKeyPick { Type = k.Type, Fingerprint = k.Fingerprint, Comment = k.Comment, Blob = k.Blob };
                pick.Hosts.Add(h.Config.Title);
            }
        }
        return (lines.Count > 0 ? "По аудиту — " + string.Join("; ", lines) : "", keys.Values.ToList());
    }

    // ---------- «Закрыть доступ» ----------

    /// <summary>«Проверить» — только чтение на хостах: право Dry-run на каждом.</summary>
    public async Task<List<AccessPreview>?> CheckAccess(AccessRequest request, List<StoredHost> hosts)
    {
        var what = UserAccess.ModeTitle(request.Mode) + ": проверка";
        if (!Allowed(Auth.Perm.RunCheck, what) || !AllHostsAllowed(Auth.Perm.RunCheck, hosts, what)) return null;
        WebPrompts.Channel.Value = Prompts;
        var byConfig = new Dictionary<HostConfig, StoredHost>();
        foreach (var h in hosts) byConfig[_store.ForConnect(h)] = h;
        var states = await UserAccess.CheckAsync(byConfig.Keys.ToList(), request, CancellationToken.None);
        return states.Select(s =>
        {
            var (applicable, plan) = UserAccess.Plan(s, request.Mode);
            return new AccessPreview(byConfig[s.Host], UserAccess.Now(s), plan, applicable);
        }).ToList();
    }

    /// <summary>Выполнить по хостам, где проверка нашла что делать: право «Применить» на каждом, хосты — под замком.</summary>
    public async Task CloseAccess(AccessRequest request, List<StoredHost> targets)
    {
        var title = UserAccess.ModeTitle(request.Mode);
        if (!Allowed(Auth.Perm.RunApply, title) || !AllHostsAllowed(Auth.Perm.RunApply, targets, title)) return;
        Results.Clear();
        Selected = null;
        StartRunning(title.ToLowerInvariant() + "…");
        _audit.Write(Who, "доступ: " + title.ToLowerInvariant(), $"{request.Login}" +
            (request.Mode == AccessMode.RevokeKey ? $", ключей: {request.KeyBlobs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}" : "") +
            "; хосты: " + string.Join(", ", targets.Select(h => h.Config.Title)));
        var progress = new Progress<PlanProgress>(p => { ApplyProgress(p); Notify(); });
        WebPrompts.Channel.Value = Prompts;
        var keys = targets.Select(h => HostLocks.Key(h.Config.Host, h.Config.Port)).ToList();
        var got = _locks.TryLock(keys, Who, title + " " + request.Login, out var busy);
        try
        {
            foreach (var (key, by) in busy)
                Results.Add(PlanRunner.MakeRow("WARN", key + " · занят", $"занят: {by.Who}, {by.What}, с {by.Since:HH:mm:ss}", "", ok: true));
            var free = targets.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase))
                .Select(_store.ForConnect).ToList();
            if (free.Count > 0) await UserAccess.ApplyAsync(free, request, progress, _runCts!.Token);
            _store.AppendHistory(Results.Select(row => new HistoryEntry
            {
                AtUtc = DateTime.UtcNow, Mode = "user-access", Host = row.Title, Action = title + " " + request.Login,
                Status = row.Status, Preview = row.Preview, Detail = row.Detail
            }).ToList(), Who);
            var failN = PlanRunner.Count(Results, "FAIL");
            var okN = PlanRunner.Count(Results, "OK");
            var note = okN > 0 ? SyncBaseUser(request) : "";
            RunStatus = (failN == 0 ? title + ": готово" : $"{failN} FAIL") + note;
            _audit.Write(Who, "доступ: итог", $"{title} {request.Login}: {okN} OK, {failN} FAIL{note}");
        }
        catch (OperationCanceledException)
        {
            RunStatus = "отменено";
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally
        {
            _locks.Release(got);
            StopRunning();
            Notify();
        }
    }

    // ---------- «Базовые пользователи» ----------

    /// <summary>Список для окна: без паролей (только «задан»).</summary>
    public List<BaseUserView> BaseUsers() => _store.LoadBaseUsers()
        .Select(u => new BaseUserView(u.Login, u.Sudo, u.Keys, u.Comment, u.Enabled, u.Password.Length > 0)).ToList();

    public int BaseUsersCount => _store.LoadBaseUsers().Count;

    /// <summary>
    /// Добавить или изменить запись. password: null — оставить прежний, "" — убрать, иначе новый.
    /// Список общий для всех: менять его может тот, у кого есть «Применить» (он же ставит учётки на хосты).
    /// </summary>
    public string? SaveBaseUser(string? oldLogin, BaseUserView edit, string? password)
    {
        if (!Allowed(Auth.Perm.RunApply, "Базовые пользователи: изменить")) return "Нет права «Применить».";
        var users = _store.LoadBaseUsers();
        var existing = oldLogin is null ? null : users.FirstOrDefault(u => u.Login == oldLogin);
        var pass = password ?? existing?.Password ?? "";
        var login = edit.Login.Trim();
        var keys = edit.Keys.Replace("\r\n", "\n").Trim();
        if (UserProvisioning.Validate(login, keys, pass, out _) is { } err) return err;
        if (users.Any(u => u != existing && u.Login == login)) return "Такой логин уже есть в списке.";
        var user = existing ?? new BaseUser();
        var was = existing is null ? "" : $" (было: sudo={(existing.Sudo ? "да" : "нет")}, ключей {UserProvisioning.KeyFingerprints(existing.Keys).Count}, вкл={(existing.Enabled ? "да" : "нет")})";
        user.Login = login; user.Sudo = edit.Sudo; user.Keys = keys; user.Comment = edit.Comment.Trim(); user.Enabled = edit.Enabled; user.Password = pass;
        if (existing is null) users.Add(user);
        _store.SaveBaseUsers(users);
        _audit.Write(Who, existing is null ? "базовые пользователи: добавлен" : "базовые пользователи: изменён",
            $"{login}: sudo={(user.Sudo ? "да" : "нет")}, ключей {UserProvisioning.KeyFingerprints(keys).Count}, пароль={(pass.Length > 0 ? "задан" : "нет")}" +
            (password is not null && existing is not null ? " (пароль сменён)" : "") + $", вкл={(user.Enabled ? "да" : "нет")}{was}");
        return null;
    }

    public void SetBaseUserEnabled(string login, bool on)
    {
        if (!Allowed(Auth.Perm.RunApply, "Базовые пользователи: изменить")) return;
        var users = _store.LoadBaseUsers();
        if (users.FirstOrDefault(u => u.Login == login) is not { } u || u.Enabled == on) return;
        u.Enabled = on;
        _store.SaveBaseUsers(users);
        _audit.Write(Who, "базовые пользователи: " + (on ? "включён" : "выключен"), login);
    }

    public void DeleteBaseUser(string login)
    {
        if (!Allowed(Auth.Perm.RunApply, "Базовые пользователи: убрать")) return;
        var users = _store.LoadBaseUsers();
        if (users.RemoveAll(u => u.Login == login) == 0) return;
        _store.SaveBaseUsers(users);
        _audit.Write(Who, "базовые пользователи: убран из списка", login + " (на хостах учётка не тронута)");
    }

    /// <summary>Учётки с ключом или sudo с хоста (пароли не читаются) — право Dry-run на хост.</summary>
    public async Task<(List<UserProvisioning.HostUserCandidate> Users, string Note)> ReadUsersFromHost(StoredHost h)
    {
        if (!AllowedOn(Auth.Perm.RunCheck, h, "Базовые пользователи: прочитать с хоста")) return (new(), "Нет права Dry-run на этот хост.");
        WebPrompts.Channel.Value = Prompts;
        var cfg = _store.ForConnect(h);
        var read = await Task.Run(() => UserProvisioning.ReadFromHost(cfg));
        return (read.Users.ToList(), read.OptionKeys > 0 ? $"Пропущено ключей с опциями: {read.OptionKeys}" : "");
    }

    /// <summary>
    /// Учётки из аудитов этой сессии со входом по ключу (как в настольной). Ключи с опциями (from=, command=) не берём —
    /// в базовых они потеряли бы ограничение; заблокированных пропускаем.
    /// </summary>
    public (List<UserProvisioning.HostUserCandidate> Users, string Note) BaseUsersFromAudit()
    {
        var byLogin = new Dictionary<string, (bool Sudo, List<string> Keys, SortedSet<string> Hosts)>(StringComparer.Ordinal);
        int optionKeys = 0, locked = 0;
        foreach (var report in _audits.Values)
        foreach (var user in report.Users.Where(u => u.Uid >= 1000))
        {
            var keys = user.Keys.Where(k => !k.Unreadable && k.Blob.Length > 0).ToList();
            if (keys.Count == 0) continue;
            if (user.IsLocked) { locked++; continue; }
            optionKeys += keys.Count(k => k.HasOptions);
            var usable = keys.Where(k => !k.HasOptions).Select(k => k.Blob + (k.Comment.Length > 0 ? " " + k.Comment : "")).ToList();
            if (usable.Count == 0) continue;
            if (!byLogin.TryGetValue(user.Login, out var e)) e = (false, new List<string>(), new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
            e.Keys.AddRange(usable);
            e.Hosts.Add(report.Host);
            byLogin[user.Login] = e with { Sudo = e.Sudo || user.HasSudo };
        }
        var list = byLogin.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new UserProvisioning.HostUserCandidate
        {
            Login = p.Key, Sudo = p.Value.Sudo, Keys = UserProvisioning.MergeKeys("", string.Join("\n", p.Value.Keys)),
            Comment = "из аудита: " + string.Join(", ", p.Value.Hosts)
        }).ToList();
        var skipped = new List<string>();
        if (optionKeys > 0) skipped.Add($"ключей с опциями: {optionKeys}");
        if (locked > 0) skipped.Add($"заблокированных учёток: {locked}");
        var note = _audits.Count == 0 ? "Аудита в этом окне ещё не было: запустите «Аудит пользователей / sudo» на хостах."
            : $"Из аудита ({string.Join(", ", _audits.Keys)}). " + (skipped.Count > 0 ? "Пропущено — " + string.Join(", ", skipped) + "." : "");
        return (list, note);
    }

    /// <summary>Добавить найденные в список: новых — как есть, существующим — sudo «или» и новые ключи.</summary>
    public void ImportBaseUsers(IEnumerable<UserProvisioning.HostUserCandidate> chosen, string from)
    {
        if (!Allowed(Auth.Perm.RunApply, "Базовые пользователи: добавить")) return;
        var users = _store.LoadBaseUsers();
        var names = new List<string>();
        foreach (var c in chosen)
        {
            var u = users.FirstOrDefault(x => x.Login == c.Login);
            if (u is null) users.Add(new BaseUser { Login = c.Login, Sudo = c.Sudo, Keys = c.Keys, Comment = c.Comment, Enabled = true });
            else
            {
                u.Sudo = u.Sudo || c.Sudo;
                u.Keys = UserProvisioning.MergeKeys(u.Keys, c.Keys);
                if (string.IsNullOrWhiteSpace(u.Comment)) u.Comment = c.Comment;
            }
            names.Add(c.Login);
        }
        if (names.Count == 0) return;
        _store.SaveBaseUsers(users);
        _audit.Write(Who, "базовые пользователи: импорт", $"{from}: {string.Join(", ", names)}");
    }

    /// <summary>«Применить» базовых пользователей на отмеченные хосты — тем же кодом, что «Добавить пользователя». Ничего не удаляется.</summary>
    public async Task ApplyBaseUsers(List<string> logins, List<StoredHost> hosts)
    {
        const string what = "Базовые пользователи: применить";
        if (!Allowed(Auth.Perm.RunApply, what) || !AllHostsAllowed(Auth.Perm.RunApply, hosts, what)) return;
        var users = _store.LoadBaseUsers().Where(u => u.Enabled && logins.Contains(u.Login)).ToList();
        if (users.Count == 0 || hosts.Count == 0) return;
        Results.Clear();
        Selected = null;
        StartRunning("базовые пользователи…");
        _audit.Write(Who, "базовые пользователи: применить", string.Join(", ", users.Select(u => u.Login)) + "; хосты: " + string.Join(", ", hosts.Select(h => h.Config.Title)));
        var progress = new Progress<PlanProgress>(p => { ApplyProgress(p); Notify(); });
        WebPrompts.Channel.Value = Prompts;
        var keys = hosts.Select(h => HostLocks.Key(h.Config.Host, h.Config.Port)).ToList();
        var got = _locks.TryLock(keys, Who, "Базовые пользователи", out var busy);
        try
        {
            foreach (var (key, by) in busy)
                Results.Add(PlanRunner.MakeRow("WARN", key + " · занят", $"занят: {by.Who}, {by.What}, с {by.Since:HH:mm:ss}", "", ok: true));
            var free = hosts.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase))
                .Select(_store.ForConnect).ToList();
            if (free.Count > 0)
                foreach (var u in users)
                {
                    UserProvisioning.Validate(u.Login, u.Keys, u.Password, out var fp);
                    var req = new AddUserRequest { Login = u.Login, Sudo = u.Sudo, Keys = u.Keys, Password = u.Password, Fingerprint = fp };
                    await UserProvisioning.ApplyAsync(free, req, progress, _runCts!.Token, pairTitle: true);
                }
            _store.AppendHistory(Results.Select(row => new HistoryEntry
            {
                AtUtc = DateTime.UtcNow, Mode = "add-user", Host = row.Title,
                Action = row.Title.Contains('·') ? row.Title.Split('·').Last().Trim() : row.Title,
                Status = row.Status, Preview = row.Preview, Detail = row.Detail
            }).ToList(), Who);
            var failN = PlanRunner.Count(Results, "FAIL");
            RunStatus = failN == 0 ? "базовые пользователи готовы" : $"{failN} FAIL";
            _audit.Write(Who, "базовые пользователи: итог", $"{PlanRunner.Count(Results, "OK")} OK, {failN} FAIL");
        }
        catch (OperationCanceledException) { RunStatus = "отменено"; }
        catch (Exception ex) { Warn(ex.Message); }
        finally
        {
            _locks.Release(got);
            StopRunning();
            Notify();
        }
    }

    /// <summary>
    /// Чтобы закрытый доступ не вернулся следующим «Применить» базовых пользователей:
    /// блокировка и удаление выключают запись, отзыв ключа убирает ключ из неё (как в настольной).
    /// </summary>
    private string SyncBaseUser(AccessRequest request)
    {
        var users = _store.LoadBaseUsers();
        var user = users.FirstOrDefault(u => string.Equals(u.Login, request.Login, StringComparison.Ordinal));
        if (user is null) return "";
        string note;
        switch (request.Mode)
        {
            case AccessMode.Lock or AccessMode.Delete when user.Enabled:
                user.Enabled = false;
                note = " · в базовых пользователях выключен";
                break;
            case AccessMode.RevokeKey:
                var blobs = request.KeyBlobs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var lines = user.Keys.Replace("\r\n", "\n").Split('\n');
                var keep = lines.Where(l => !blobs.Any(b => l.Contains(b, StringComparison.Ordinal))).ToList();
                if (keep.Count == lines.Length) return "";
                user.Keys = string.Join("\n", keep).Trim();
                note = " · ключ убран и из базовых пользователей";
                break;
            default:
                return "";
        }
        _store.SaveBaseUsers(users);
        _audit.Write(Who, "базовые пользователи: " + (request.Mode == AccessMode.RevokeKey ? "ключ убран" : "выключен"), request.Login);
        return note;
    }
}
