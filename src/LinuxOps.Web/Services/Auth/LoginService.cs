using System.Collections.Concurrent;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Вход: локальный админ и локальные пользователи — пароль → свой TOTP (первый вход — привязка по QR-коду);
/// AD — пароль → назначения → второй фактор через MK 2FA (если включён в настройках).
/// Защита от перебора: 5 неудач за 15 минут на логин или на адрес — пауза 15 минут. Всё — в аудит.
/// </summary>
public sealed partial class LoginService(WebStore store, AuditLog audit, MkMfa mfa)
{
    public enum Step { Fail, Done, Totp, TotpSetup, Mfa }

    /// <summary>Итог шага. Для Mfa — User (ещё не вошёл) и Pending — номер ожидания подтверждения.</summary>
    public sealed record Outcome(Step Step, string Error = "", UserInfo? User = null, string Pending = "");

    private const int MaxFails = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, List<DateTime>> _fails = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object TotpGate = new();

    private bool Locked(string key)
    {
        if (!_fails.TryGetValue(key, out var list)) return false;
        lock (list) { list.RemoveAll(t => DateTime.UtcNow - t > Window); return list.Count >= MaxFails; }
    }

    private void Failed(params string[] keys)
    {
        foreach (var k in keys)
        {
            var list = _fails.GetOrAdd(k, _ => new List<DateTime>());
            lock (list) list.Add(DateTime.UtcNow);
        }
    }

    private void Clear(params string[] keys) { foreach (var k in keys) _fails.TryRemove(k, out _); }

    /// <summary>Логин со своим TOTP (админ или локальный пользователь) — для шага кода.</summary>
    public bool HasOwnTotp(string login) => store.LoadTotpAccount(login) is not null;

    /// <summary>Шаг 1: логин и пароль.</summary>
    public Outcome Password(string login, string password, string ip)
    {
        login = (login ?? "").Trim();
        var kLogin = "u:" + login; var kIp = "ip:" + ip;
        if (login.Length == 0) return new(Step.Fail, "Введите логин.");
        if (Locked(kLogin) || Locked(kIp))
        {
            audit.Write($"{login} ({ip})", "вход: пауза", "слишком много неудачных попыток");
            return new(Step.Fail, "Слишком много неудачных попыток. Подождите 15 минут.");
        }

        var la = store.LoadLocalAdmin();
        if (string.Equals(login, la.Login, StringComparison.OrdinalIgnoreCase))
        {
            if (!la.Enabled || !la.HasPassword)
            {
                Failed(kLogin, kIp);
                audit.Write($"{login} ({ip})", "вход: отказ", la.HasPassword ? "локальный админ выключен" : "пароль локального админа не задан");
                return new(Step.Fail, "Неверный логин или пароль.");
            }
            if (!PasswordHash.Verify(password ?? "", la.PasswordHash))
            {
                Failed(kLogin, kIp);
                audit.Write($"{login} ({ip})", "вход: отказ", "неверный пароль локального админа");
                return new(Step.Fail, "Неверный логин или пароль.");
            }
            if (la.NoTotp)
            {
                Clear(kLogin);
                var admin = UserInfo.LocalAdmin(la.Login);
                audit.Write($"{admin.Login} ({ip})", "вход", "локальный админ, лабораторный режим: без TOTP");
                return new(Step.Done, User: admin);
            }
            return new(la.TotpConfirmed ? Step.Totp : Step.TotpSetup);
        }

        // локальный пользователь — раньше AD: тот же логин в AD — другой человек, и сюда не попадёт
        if (store.LoadLocalUser(login) is { } lu)
        {
            if (!lu.Enabled || lu.PasswordHash.Length == 0 || !PasswordHash.Verify(password ?? "", lu.PasswordHash))
            {
                Failed(kLogin, kIp);
                audit.Write($"{login} ({ip})", "вход: отказ", lu.Enabled ? "неверный пароль локального пользователя" : "локальный пользователь выключен");
                return new(Step.Fail, "Неверный логин или пароль.");
            }
            if (LocalUserInfo(lu).Grants.Count == 0)
            {
                audit.Write($"{lu.Login} ({ip})", "вход: отказ", "локальный: пароль верный, но нет назначений");
                return new(Step.Fail, "У вашей учётки нет доступа к LinuxOps Web. Обратитесь к администратору.");
            }
            return new(lu.TotpConfirmed ? Step.Totp : Step.TotpSetup);
        }

        var s = store.LoadAuth();
        if (!s.AdEnabled)
        {
            Failed(kLogin, kIp);
            audit.Write($"{login} ({ip})", "вход: отказ", "вход через AD выключен");
            return new(Step.Fail, "Неверный логин или пароль.");
        }
        var r = LdapAuth.Authenticate(s, login, password ?? "");
        if (!r.Ok)
        {
            if (r.Error.StartsWith("Неверный")) Failed(kLogin, kIp);
            audit.Write($"{login} ({ip})", "вход: отказ", "AD: " + r.Error);
            return new(Step.Fail, r.Error);
        }
        var user = UserInfo.Build(r.Login, r.Display, UserKind.Ad, r.Groups, store.LoadRoles(), store.LoadAssignments());
        if (user.Grants.Count == 0)
        {
            audit.Write($"{r.Login} ({ip})", "вход: отказ", "AD: пароль верный, но нет назначений в LinuxOps Web; группы: " + string.Join(", ", r.Groups));
            return new(Step.Fail, "У вашей учётки нет доступа к LinuxOps Web. Обратитесь к администратору.");
        }
        Clear(kLogin);
        if (mfa.Enabled(out _)) return new(Step.Mfa, User: user);
        audit.Write($"{user.Login} ({ip})", "вход", $"AD без второго фактора (MK 2FA выключен), роли: {user.RolesText}");
        return new(Step.Done, User: user);
    }

    /// <summary>Новый секрет для привязки (хранится зашифрованным, пока не подтверждён кодом).</summary>
    public string TotpSetupSecret(string login)
    {
        lock (TotpGate)
        {
            var a = store.LoadTotpAccount(login) ?? throw new InvalidOperationException("Нет такой учётки.");
            if (a.TotpConfirmed) throw new InvalidOperationException("TOTP уже привязан.");
            var current = store.Unprotect(a.TotpSecretEnc);
            if (current.Length > 0) return current;
            var secret = Totp.NewSecret();
            a.TotpSecretEnc = store.Protect(secret);
            store.SaveTotpAccount(a);
            return secret;
        }
    }

    /// <summary>Шаг 2: код TOTP (или подтверждение привязки) — админ или локальный пользователь.</summary>
    public Outcome TotpCode(string login, string code, string ip)
    {
        var kLogin = "u:" + login; var kIp = "ip:" + ip;
        if (Locked(kLogin) || Locked(kIp)) return new(Step.Fail, "Слишком много неудачных попыток. Подождите 15 минут.");
        ITotpAccount? acc;
        bool setup;
        lock (TotpGate)
        {
            acc = store.LoadTotpAccount(login);
            switch (acc)
            {
                case LocalAdmin { Enabled: false }: return new(Step.Fail, "Локальный админ выключен.");
                case LocalUser { Enabled: false }: return new(Step.Fail, "Учётка выключена.");
                case null: return new(Step.Fail, "Время на ввод кода вышло. Войдите снова.");
            }
            var step = Totp.Verify(store.Unprotect(acc.TotpSecretEnc), code, acc.TotpLastStep);
            if (step < 0)
            {
                Failed(kLogin, kIp);
                audit.Write($"{acc.Login} ({ip})", "вход: отказ", "неверный код TOTP");
                return new(Step.Fail, "Неверный код. Проверьте время на телефоне.");
            }
            setup = !acc.TotpConfirmed;
            acc.TotpConfirmed = true;
            acc.TotpLastStep = step;
            store.SaveTotpAccount(acc);
            Clear(kLogin);
        }
        if (acc is LocalUser lu)
        {
            if (setup) audit.Write($"{lu.Login} ({ip})", "локальный пользователь: TOTP привязан");
            var user = LocalUserInfo(lu);
            if (user.Grants.Count == 0) return new(Step.Fail, "У вашей учётки нет доступа к LinuxOps Web. Обратитесь к администратору.");
            audit.Write($"{user.Login} ({ip})", "вход", $"локальный пользователь, пароль + TOTP, роли: {user.RolesText}");
            return new(Step.Done, User: user);
        }
        if (setup) audit.Write($"{acc.Login} ({ip})", "локальный админ: TOTP привязан");
        var admin = UserInfo.LocalAdmin(acc.Login);
        audit.Write($"{admin.Login} ({ip})", "вход", "локальный админ, пароль + TOTP");
        return new(Step.Done, User: admin);
    }

    private UserInfo LocalUserInfo(LocalUser u) =>
        UserInfo.Build(u.Login, u.Display.Length > 0 ? u.Display : u.Login, UserKind.Local, new List<string>(), store.LoadRoles(), store.LoadAssignments());

    /// <summary>
    /// Права при открытии окна и на каждом запросе: назначения перечитываются (их могли поменять после входа).
    /// Группы — с момента входа (в cookie); новая группа в AD — после повторного входа.
    /// </summary>
    public UserInfo? Resolve(string login, string kind, string display, IEnumerable<string> groups)
    {
        switch (kind)
        {
            case UserKind.Admin:
                var la = store.LoadLocalAdmin();
                if (!la.Enabled || !string.Equals(la.Login, login, StringComparison.OrdinalIgnoreCase)) return null;
                return UserInfo.LocalAdmin(login);
            case UserKind.Local:
                return store.LoadLocalUser(login) is { Enabled: true } lu && LocalUserInfo(lu) is { Grants.Count: > 0 } u ? u : null;
            default:
                var user = UserInfo.Build(login, display, kind, groups.ToList(), store.LoadRoles(), store.LoadAssignments());
                return user.Grants.Count == 0 ? null : user;
        }
    }
}
