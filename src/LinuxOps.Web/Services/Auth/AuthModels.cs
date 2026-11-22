namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// Права (как в vSphere): роль — набор прав; назначение — «кто (группа или человек AD) → роль → объект (всё, папка, хост)».
/// Назначение на папку действует на её хосты и вложенные папки. Глобальные права (настройки, аудит, vCenter) — только от назначения на «всё».
/// </summary>
public static class Perm
{
    public const string HostsView = "hosts.view";     // видеть хосты, детали, историю
    public const string FleetView = "fleet.view";     // вкладка «Уязвимости»: версии ПО и CVE по своим хостам
    public const string RunCheck = "run.check";       // опрос и Dry-run (только чтение на хостах)
    public const string RunApply = "run.apply";       // «Применить», «Обновить отмеченное», «Добавить пользователя»
    public const string HostsEdit = "hosts.edit";     // Inventory: хосты, папки, секреты
    public const string Terminal = "ssh.terminal";    // терминал SSH под учёткой хоста (обычно — полный доступ к хосту)
    public const string Rdp = "rdp";                  // RDP: пускает к машине, вход — своей доменной учёткой
    public const string FilesRead = "files.read";     // файлы: смотреть и скачивать
    public const string FilesWrite = "files.write";   // файлы: загружать, удалять, переименовывать
    public const string AuditView = "audit.view";     // журнал аудита и записи сеансов
    public const string Settings = "settings";        // настройки: вход, роли, назначения
    public const string Vcenter = "vcenter";          // vCenter и PowerShell — только встроенная «Админ»
    public const string SessionsWatch = "sessions.watch"; // смотреть чужой идущий сеанс вживую (только просмотр)
    public const string SessionsClose = "sessions.close"; // закрыть чужой идущий сеанс

    public static readonly (string Id, string Text)[] All =
    {
        (HostsView, "Видеть хосты, результаты и историю"),
        (FleetView, "Vulnerabilities: версии ПО и CVE по своим хостам"),
        (RunCheck, "Опрос и Dry-run (только чтение на хостах)"),
        (RunApply, "Применять: изменения на хостах, обновления, пользователи"),
        (HostsEdit, "Inventory: хосты, папки, пароли и ключи (только встроенная «Админ»)"),
        (Terminal, "Терминал SSH (под учёткой хоста — обычно полный доступ)"),
        (Rdp, "RDP (вход своей доменной учёткой)"),
        (FilesRead, "Файлы: смотреть и скачивать"),
        (FilesWrite, "Файлы: загружать, удалять, переименовывать"),
        (AuditView, "Журнал аудита и записи сеансов (только назначение на «всё»)"),
        (Settings, "Настройки: вход, роли, назначения (только назначение на «всё»)"),
        (SessionsWatch, "Сеансы: смотреть идущие вживую (только назначение на «всё»)"),
        (SessionsClose, "Сеансы: закрывать идущие (только назначение на «всё»)"),
        (Vcenter, "vCenter и PowerShell (только встроенная «Админ»)")
    };

    /// <summary>Действуют только из назначения на «всё»: у них нет объекта.</summary>
    public static readonly HashSet<string> Global = new() { AuditView, Settings, Vcenter, HostsEdit, SessionsWatch, SessionsClose };

    /// <summary>В своих ролях не выдаются.</summary>
    /// Inventory — только админы (владелец, 22.11.2026): остальным вкладка не показывается.
    public static readonly HashSet<string> AdminOnly = new() { Vcenter, HostsEdit };

    public static string Short(string id) => id switch
    {
        HostsView => "просмотр", FleetView => "уязвимости", RunCheck => "Dry-run", RunApply => "Применить", HostsEdit => "Inventory",
        Terminal => "терминал", Rdp => "RDP", FilesRead => "файлы: чтение", FilesWrite => "файлы: запись",
        AuditView => "аудит", Settings => "настройки", Vcenter => "vCenter",
        SessionsWatch => "сеансы: смотреть", SessionsClose => "сеансы: закрывать", _ => id
    };
}

/// <summary>Роль — только набор прав. Кому и на что — в назначениях.</summary>
public sealed class Role
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Встроенная: не удаляется и не меняется.</summary>
    public bool Builtin { get; set; }
    public List<string> Perms { get; set; } = new();

    public static List<Role> Builtins() => new()
    {
        new() { Id = "admin", Name = "Админ", Builtin = true, Perms = Perm.All.Select(p => p.Id).ToList() },
        new() { Id = "operator", Name = "Оператор", Builtin = true,
                Perms = { Perm.HostsView, Perm.FleetView, Perm.RunCheck, Perm.RunApply, Perm.Terminal, Perm.Rdp, Perm.FilesRead, Perm.FilesWrite } },
        new() { Id = "auditor", Name = "Аудитор", Builtin = true, Perms = { Perm.HostsView, Perm.FleetView, Perm.RunCheck, Perm.FilesRead, Perm.AuditView } },
        new() { Id = "viewer", Name = "Просмотр", Builtin = true, Perms = { Perm.HostsView } }
    };
}

public static class Scope
{
    public const string All = "all", Folder = "folder", Host = "host";
}

/// <summary>Кто вошёл: локальный админ, локальный пользователь сервиса, пользователь AD.</summary>
public static class UserKind
{
    public const string Admin = "local", Local = "user", Ad = "ad", Api = "api";
}

/// <summary>Назначение: группа AD, человек AD или локальный пользователь получает роль на объект.</summary>
public sealed class Assignment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>sAMAccountName группы или человека; для локального — его логин.</summary>
    public string Who { get; set; } = "";
    public bool IsGroup { get; set; } = true;
    /// <summary>Локальный пользователь сервиса (не AD). Тот же логин в AD — другой человек.</summary>
    public bool Local { get; set; }
    /// <summary>Сервисная учётка API (по токену). Права из ролей ей нужны те же, но API само по себе — только чтение.</summary>
    public bool Api { get; set; }
    public string RoleId { get; set; } = "";
    /// <summary>all | folder | host</summary>
    public string Scope { get; set; } = Auth.Scope.All;
    /// <summary>id папки или хоста; для all — пусто.</summary>
    public string ObjectId { get; set; } = "";

    public bool Matches(string login, ICollection<string> groups, string kind) =>
        Api ? kind == UserKind.Api && string.Equals(Who, login, StringComparison.OrdinalIgnoreCase)
        : Local ? kind == UserKind.Local && string.Equals(Who, login, StringComparison.OrdinalIgnoreCase)
        : kind != UserKind.Ad ? false
        : IsGroup ? groups.Contains(Who, StringComparer.OrdinalIgnoreCase)
        : string.Equals(Who, login, StringComparison.OrdinalIgnoreCase);

    public string Icon => Api ? "🔌" : Local ? "🔑" : IsGroup ? "👥" : "👤";
    public string KindText => Api ? "сервис API" : Local ? "локальный" : IsGroup ? "группа" : "человек";
}

/// <summary>
/// Сервисная учётка API (Ф8): внешний сервис по токену запускает только встроенные действия «только чтение»
/// и забирает их результат. Токен хранится только хэшем (SHA-256), показывается один раз при выпуске.
/// </summary>
public sealed class ApiClient
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TokenHash { get; set; } = "";
    /// <summary>Начало токена — чтобы узнать его в списке, не храня целиком.</summary>
    public string TokenHint { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }

    public const string Prefix = "lo_";

    public static string NewToken() =>
        Prefix + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

/// <summary>Учётка со своим TOTP: локальный админ и локальные пользователи.</summary>
public interface ITotpAccount
{
    string Login { get; }
    string TotpSecretEnc { get; set; }
    bool TotpConfirmed { get; set; }
    long TotpLastStep { get; set; }
}

/// <summary>
/// Локальный пользователь сервиса (не AD): пароль и TOTP с QR-кодом. Заводит тот, у кого право «Настройки»;
/// права — назначениями, как у людей AD. Без второго фактора не входит (лабораторного режима нет).
/// </summary>
public sealed class LocalUser : ITotpAccount
{
    public string Login { get; set; } = "";
    public string Display { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string PasswordHash { get; set; } = "";
    public string TotpSecretEnc { get; set; } = "";
    public bool TotpConfirmed { get; set; }
    public long TotpLastStep { get; set; }
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Логин: латиница, цифры, . _ - ; 2–64 символа (без @ и \ — не спутать с доменным).</summary>
    public static bool ValidLogin(string login) =>
        System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z0-9][A-Za-z0-9._-]{1,63}$");

    public const int MinPassword = 12;
}

/// <summary>Старая запись роли (до 0.4.0): группы и область внутри роли — только для переноса в назначения.</summary>
public sealed class LegacyRole
{
    public string Id { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public List<string> Hosts { get; set; } = new();
    public List<string> AdGroups { get; set; } = new();
    public List<string> AdUsers { get; set; } = new();
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
    /// <summary>Учётка для поиска групп и людей в настройках (только чтение каталога). Пусто — поиск выключен.</summary>
    public string AdLookupUser { get; set; } = "";
    /// <summary>Пароль учётки поиска, зашифрован (SecretBox).</summary>
    public string AdLookupPasswordEnc { get; set; } = "";
    /// <summary>Сессия без действий, минут.</summary>
    public int IdleMinutes { get; set; } = 30;
    /// <summary>Сессия всего, часов.</summary>
    public int MaxHours { get; set; } = 10;

    /// <summary>Второй фактор для входа пользователей AD — API MK 2FA. Выключен — AD входят только паролем.</summary>
    public bool MfaEnabled { get; set; }
    /// <summary>Адрес API: https://&lt;панель&gt;/api/v1/mfa</summary>
    public string MfaUrl { get; set; } = "";
    /// <summary>Секрет сервиса (mfa_…), зашифрован (SecretBox).</summary>
    public string MfaSecretEnc { get; set; } = "";
    /// <summary>Что видит человек в сообщении Express.</summary>
    public string MfaAction { get; set; } = "Вход в LinuxOps Web";

    public string BaseDn => !string.IsNullOrWhiteSpace(AdBaseDn) ? AdBaseDn
        : string.Join(",", AdDomain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => "DC=" + p));
}

/// <summary>Локальный админ — аварийный вход. Пароль и TOTP задаются только из CLI на сервере.</summary>
public sealed class LocalAdmin : ITotpAccount
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

/// <summary>Одно действующее назначение пользователя: права роли на объект.</summary>
public sealed record Grant(string RoleName, HashSet<string> Perms, string Scope, string ObjectId);

/// <summary>Кто вошёл: из cookie; назначения перечитываются при открытии окна и на каждом запросе.</summary>
public sealed class UserInfo
{
    public string Login { get; init; } = "";
    public string Display { get; init; } = "";
    /// <summary>UserKind: local (админ) | user (локальный) | ad | api (сервисная учётка)</summary>
    public string Kind { get; init; } = "";
    /// <summary>Группы AD на момент входа (для назначений на группы).</summary>
    public List<string> Groups { get; init; } = new();
    public List<Grant> Grants { get; init; } = new();

    /// <summary>Есть ли право где-нибудь (для показа вкладок и кнопок; на объекте проверяет WebSession).</summary>
    public bool Can(string perm) => Perm.Global.Contains(perm)
        ? Grants.Any(g => g.Scope == Scope.All && g.Perms.Contains(perm))
        : Grants.Any(g => g.Perms.Contains(perm));

    /// <summary>Видит не все хосты — только назначенные папки и хосты.</summary>
    public bool Limited => !Grants.Any(g => g.Scope == Scope.All && g.Perms.Contains(Perm.HostsView));

    public string RolesText => Grants.Count == 0 ? "без роли"
        : string.Join(", ", Grants.Select(g => g.RoleName + (g.Scope == Scope.All ? "" : "*")).Distinct());

    public static UserInfo Build(string login, string display, string kind, List<string> groups, List<Role> roles, List<Assignment> assignments)
    {
        var grants = new List<Grant>();
        foreach (var a in assignments.Where(a => a.Matches(login, groups, kind)))
            if (roles.FirstOrDefault(r => r.Id == a.RoleId) is { } role)
                grants.Add(new Grant(role.Name, role.Perms.ToHashSet(), a.Scope, a.ObjectId));
        return new UserInfo { Login = login, Display = display, Kind = kind, Groups = groups, Grants = grants };
    }

    /// <summary>Локальный админ: «Админ» на всё.</summary>
    public static UserInfo LocalAdmin(string login) => new()
    {
        Login = login, Display = "Локальный админ", Kind = "local",
        Grants = { new Grant("Админ", Perm.All.Select(p => p.Id).ToHashSet(), Scope.All, "") }
    };
}
