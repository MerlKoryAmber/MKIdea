using System.Collections.Concurrent;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Вход: локальный админ (пароль → TOTP; при первом входе — привязка TOTP) и AD (пароль → роли по группам).
/// Защита от перебора: 5 неудач за 15 минут на логин или на адрес — пауза 15 минут. Всё — в аудит.
/// </summary>
public sealed class LoginService(WebStore store, AuditLog audit)
{
    public enum Step { Fail, Done, Totp, TotpSetup }

    public sealed record Outcome(Step Step, string Error = "", UserInfo? User = null);

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

    public bool IsLocalAdmin(string login) =>
        store.LoadLocalAdmin() is { } a && string.Equals(login.Trim(), a.Login, StringComparison.OrdinalIgnoreCase);

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
        var user = UserInfo.Build(r.Login, r.Display, "ad", r.Groups, store.LoadRoles(), store.LoadAssignments());
        if (user.Grants.Count == 0)
        {
            audit.Write($"{r.Login} ({ip})", "вход: отказ", "AD: пароль верный, но нет назначений в LinuxOps Web; группы: " + string.Join(", ", r.Groups));
            return new(Step.Fail, "У вашей учётки нет доступа к LinuxOps Web. Обратитесь к администратору.");
        }
        Clear(kLogin);
        audit.Write($"{user.Login} ({ip})", "вход", $"AD, роли: {user.RolesText}");
        return new(Step.Done, User: user);
    }

    /// <summary>Новый секрет для привязки (хранится зашифрованным, пока не подтверждён кодом).</summary>
    public string TotpSetupSecret()
    {
        lock (TotpGate)
        {
            var la = store.LoadLocalAdmin();
            if (la.TotpConfirmed) throw new InvalidOperationException("TOTP уже привязан.");
            var current = store.Unprotect(la.TotpSecretEnc);
            if (current.Length > 0) return current;
            var secret = Totp.NewSecret();
            la.TotpSecretEnc = store.Protect(secret);
            store.SaveLocalAdmin(la);
            return secret;
        }
    }

    /// <summary>Шаг 2: код TOTP (или подтверждение привязки).</summary>
    public Outcome TotpCode(string code, string ip)
    {
        var la = store.LoadLocalAdmin();
        var kLogin = "u:" + la.Login; var kIp = "ip:" + ip;
        if (Locked(kLogin) || Locked(kIp)) return new(Step.Fail, "Слишком много неудачных попыток. Подождите 15 минут.");
        if (!la.Enabled) return new(Step.Fail, "Локальный админ выключен.");
        lock (TotpGate)
        {
            la = store.LoadLocalAdmin();
            var secret = store.Unprotect(la.TotpSecretEnc);
            var step = Totp.Verify(secret, code, la.TotpLastStep);
            if (step < 0)
            {
                Failed(kLogin, kIp);
                audit.Write($"{la.Login} ({ip})", "вход: отказ", "неверный код TOTP");
                return new(Step.Fail, "Неверный код. Проверьте время на телефоне.");
            }
            var setup = !la.TotpConfirmed;
            la.TotpConfirmed = true;
            la.TotpLastStep = step;
            store.SaveLocalAdmin(la);
            Clear(kLogin);
            if (setup) audit.Write($"{la.Login} ({ip})", "локальный админ: TOTP привязан");
        }
        var user = UserInfo.LocalAdmin(la.Login);
        audit.Write($"{user.Login} ({ip})", "вход", "локальный админ, пароль + TOTP");
        return new(Step.Done, User: user);
    }

    /// <summary>
    /// Права при открытии окна и на каждом запросе: назначения перечитываются (их могли поменять после входа).
    /// Группы — с момента входа (в cookie); новая группа в AD — после повторного входа.
    /// </summary>
    public UserInfo? Resolve(string login, string kind, string display, IEnumerable<string> groups)
    {
        if (kind == "local")
        {
            var la = store.LoadLocalAdmin();
            if (!la.Enabled || !string.Equals(la.Login, login, StringComparison.OrdinalIgnoreCase)) return null;
            return UserInfo.LocalAdmin(login);
        }
        var user = UserInfo.Build(login, display, kind, groups.ToList(), store.LoadRoles(), store.LoadAssignments());
        return user.Grants.Count == 0 ? null : user;
    }
}
