namespace LinuxOps.Web.Services.Auth;

/// <summary>Права. Роль — набор прав + папки хостов + группы AD.</summary>
public static class Perm
{
    public const string HostsView = "hosts.view";     // видеть хосты, детали, историю
    public const string RunCheck = "run.check";       // опрос и Dry-run (только чтение на хостах)
    public const string RunApply = "run.apply";       // «Применить», «Обновить отмеченное», «Добавить пользователя»
    public const string HostsEdit = "hosts.edit";     // Inventory: хосты, папки, секреты
    public const string AuditView = "audit.view";     // журнал аудита
    public const string Settings = "settings";        // вкладка «Настройки»: вход, роли

    public static readonly (string Id, string Text)[] All =
    {
        (HostsView, "Видеть хосты, результаты и историю"),
        (RunCheck, "Опрос и Dry-run (только чтение на хостах)"),
        (RunApply, "Применять: изменения на хостах, обновления, пользователи"),
        (HostsEdit, "Inventory: добавлять и менять хосты, папки, пароли и ключи"),
        (AuditView, "Журнал аудита"),
        (Settings, "Настройки: вход, роли (только админ)")
    };
}

public sealed class Role
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Встроенная: не удаляется, права не меняются; группы AD и папки — можно.</summary>
    public bool Builtin { get; set; }
    public List<string> Perms { get; set; } = new();
    /// <summary>Пусто — все папки. Иначе id папок (с вложенными).</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>Группы AD (имя, sAMAccountName или CN; вложенность учитывается).</summary>
    public List<string> AdGroups { get; set; } = new();
    /// <summary>Отдельные люди (логин AD) — без группы.</summary>
    public List<string> AdUsers { get; set; } = new();

    public static List<Role> Builtins() => new()
    {
        new() { Id = "admin", Name = "Админ", Builtin = true, Perms = Perm.All.Select(p => p.Id).ToList() },
        new() { Id = "operator", Name = "Оператор", Builtin = true, Perms = { Perm.HostsView, Perm.RunCheck, Perm.RunApply, Perm.HostsEdit } },
        new() { Id = "auditor", Name = "Аудитор", Builtin = true, Perms = { Perm.HostsView, Perm.RunCheck, Perm.AuditView } }
    };
}

public enum LdapSecurity { Ldaps, StartTls, None }

/// <summary>Настройки входа (JSON в settings, ключ auth).</summary>
public sealed class AuthSettings
{
    public bool AdEnabled { get; set; }
    /// <summary>Контроллер(ы) домена через запятую: dc-01.merl.loc,192.168.0.175.</summary>
    public string AdServers { get; set; } = "";
    public int AdPort { get; set; } = 636;
    public LdapSecurity AdSecurity { get; set; } = LdapSecurity.Ldaps;
    /// <summary>DNS-имя домена: merl.loc — для входа user@merl.loc.</summary>
    public string AdDomain { get; set; } = "";
    /// <summary>Где искать пользователей и группы: DC=merl,DC=loc (пусто — из домена).</summary>
    public string AdBaseDn { get; set; } = "";
    /// <summary>Сессия без действий, минут.</summary>
    public int IdleMinutes { get; set; } = 30;
    /// <summary>Сессия всего, часов.</summary>
    public int MaxHours { get; set; } = 10;

    public string BaseDn => !string.IsNullOrWhiteSpace(AdBaseDn) ? AdBaseDn
        : string.Join(",", AdDomain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => "DC=" + p));
}

/// <summary>Локальный админ — аварийный вход. Пароль и TOTP задаются только из CLI на сервере.</summary>
public sealed class LocalAdmin
{
    public string Login { get; set; } = "admin";
    public bool Enabled { get; set; }
    public string PasswordHash { get; set; } = "";
    /// <summary>Секрет TOTP зашифрован (SecretBox). Пусто — привязать при следующем входе.</summary>
    public string TotpSecretEnc { get; set; } = "";
    public bool TotpConfirmed { get; set; }
    public long TotpLastStep { get; set; }
    /// <summary>Лабораторный режим: вход паролем без TOTP (только CLI: admin lab on). В шапке — плашка «ЛАБА».</summary>
    public bool NoTotp { get; set; }
    public bool HasPassword => PasswordHash.Length > 0;
}

/// <summary>Кто вошёл: из cookie, права — из ролей на момент входа в окно.</summary>
public sealed class UserInfo
{
    public string Login { get; init; } = "";
    public string Display { get; init; } = "";
    /// <summary>local | ad</summary>
    public string Kind { get; init; } = "";
    public List<Role> Roles { get; init; } = new();
    public HashSet<string> Perms { get; init; } = new();
    /// <summary>null — все папки.</summary>
    public HashSet<string>? Folders { get; init; }

    public bool Can(string perm) => Perms.Contains(perm);
    public string RolesText => Roles.Count == 0 ? "без роли" : string.Join(", ", Roles.Select(r => r.Name));

    public static UserInfo Build(string login, string display, string kind, IEnumerable<Role> roles)
    {
        var list = roles.ToList();
        var perms = list.SelectMany(r => r.Perms).ToHashSet();
        // папки: если хоть одна роль без ограничения — все
        HashSet<string>? folders = list.Count == 0 || list.Any(r => r.Folders.Count == 0) ? null : list.SelectMany(r => r.Folders).ToHashSet();
        return new UserInfo { Login = login, Display = display, Kind = kind, Roles = list, Perms = perms, Folders = folders };
    }
}
