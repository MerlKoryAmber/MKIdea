using System.Text.Json;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Вход и роли: настройки входа и локальный админ — в settings (JSON), роли — в своей таблице.</summary>
public sealed partial class WebStore
{
    private static readonly JsonSerializerOptions AuthJson = new() { PropertyNameCaseInsensitive = true };

    private static readonly object MigrateGate = new();
    private bool _authReady;

    private void EnsureAuthSchema()
    {
        if (_authReady) return;
        lock (MigrateGate)
        {
            if (_authReady) return;
            using (var c = Open())
            {
                Exec(c, "CREATE TABLE IF NOT EXISTS roles(id TEXT PRIMARY KEY, json TEXT NOT NULL)");
                Exec(c, "CREATE TABLE IF NOT EXISTS assignments(id TEXT PRIMARY KEY, json TEXT NOT NULL)");
                Exec(c, "CREATE TABLE IF NOT EXISTS local_users(login TEXT PRIMARY KEY COLLATE NOCASE, json TEXT NOT NULL)");
            }
            if (GetSetting("auth_model") != "2") MigrateRoles();
            if (GetSetting("perm_fleet") != "1") GrantFleetView();
            _authReady = true;
        }
    }

    /// <summary>
    /// До 0.4.0 группы AD и папки жили внутри роли. Переносим в назначения: каждая группа и человек роли →
    /// роль на «всё» (если папок и хостов не было) или на каждую папку и хост. Роль остаётся только с правами.
    /// </summary>
    private void MigrateRoles()
    {
        var raw = new List<(string Id, string Json)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, json FROM roles";
            using var r = cmd.ExecuteReader();
            while (r.Read()) raw.Add((r.GetString(0), r.GetString(1)));
        }
        var made = 0;
        using (var c = Open())
        {
            foreach (var (id, json) in raw)
            {
                var old = JsonSerializer.Deserialize<LegacyRole>(json, AuthJson);
                if (old is null) continue;
                var targets = old.Folders.Count + old.Hosts.Count == 0
                    ? new List<(string, string)> { (Scope.All, "") }
                    : old.Folders.Select(f => (Scope.Folder, f)).Concat(old.Hosts.Select(h => (Scope.Host, h))).ToList();
                foreach (var (who, isGroup) in old.AdGroups.Select(g => (g, true)).Concat(old.AdUsers.Select(u => (u, false))))
                    foreach (var (scope, obj) in targets)
                    {
                        var a = new Assignment { Who = who.Trim(), IsGroup = isGroup, RoleId = id, Scope = scope, ObjectId = obj };
                        Exec(c, "INSERT INTO assignments(id, json) VALUES($id, $j)", ("$id", a.Id), ("$j", JsonSerializer.Serialize(a)));
                        made++;
                    }
                // встроенные роли не хранятся вовсе; свои — только имя и права
                if (Role.Builtins().Any(b => b.Id == id)) Exec(c, "DELETE FROM roles WHERE id=$id", ("$id", id));
                else if (JsonSerializer.Deserialize<Role>(json, AuthJson) is { } role)
                    Exec(c, "UPDATE roles SET json=$j WHERE id=$id", ("$id", id), ("$j", JsonSerializer.Serialize(new Role { Id = role.Id, Name = role.Name, Perms = role.Perms })));
            }
        }
        PutSetting("auth_model", "2");
        if (made > 0 || raw.Count > 0) Console.WriteLine($"auth: roles migrated to assignments ({made})");
    }

    /// <summary>
    /// 0.7.2: у вкладки «Уязвимости» своё право. Раньше её видел любой с «Видеть хосты» — свои роли с этим правом
    /// получают «Уязвимости» один раз, чтобы ни у кого ничего не пропало; дальше админ снимает сам. Встроенные — в коде.
    /// </summary>
    private void GrantFleetView()
    {
        var raw = new List<(string Id, string Json)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, json FROM roles";
            using var r = cmd.ExecuteReader();
            while (r.Read()) raw.Add((r.GetString(0), r.GetString(1)));
        }
        var n = 0;
        using (var c = Open())
            foreach (var (id, json) in raw)
                if (JsonSerializer.Deserialize<Role>(json, AuthJson) is { } role && role.Perms.Contains(Perm.HostsView) && !role.Perms.Contains(Perm.FleetView))
                {
                    role.Perms.Insert(role.Perms.IndexOf(Perm.HostsView) + 1, Perm.FleetView);
                    Exec(c, "UPDATE roles SET json=$j WHERE id=$id", ("$id", id), ("$j", JsonSerializer.Serialize(role)));
                    n++;
                }
        PutSetting("perm_fleet", "1");
        if (n > 0) Console.WriteLine($"auth: fleet.view granted to {n} custom roles");
    }

    // ---------- назначения ----------

    public List<Assignment> LoadAssignments()
    {
        EnsureAuthSchema();
        var list = new List<Assignment>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM assignments ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<Assignment>(r.GetString(0), AuthJson) is { } a) list.Add(a);
        return list;
    }

    public void SaveAssignment(Assignment a)
    {
        EnsureAuthSchema();
        using var c = Open();
        Exec(c, "INSERT INTO assignments(id, json) VALUES($id, $j) ON CONFLICT(id) DO UPDATE SET json=$j",
            ("$id", a.Id), ("$j", JsonSerializer.Serialize(a)));
    }

    public void DeleteAssignment(string id)
    {
        EnsureAuthSchema();
        using var c = Open();
        Exec(c, "DELETE FROM assignments WHERE id=$id", ("$id", id));
    }

    /// <summary>Удалили папку или хост — их назначения больше ни к чему.</summary>
    public void DeleteAssignmentsOn(string objectId)
    {
        foreach (var a in LoadAssignments().Where(a => a.Scope != Scope.All && a.ObjectId == objectId)) DeleteAssignment(a.Id);
    }

    // ---------- настройки входа ----------

    public AuthSettings LoadAuth()
    {
        var s = GetSetting("auth");
        return s is null ? new AuthSettings() : JsonSerializer.Deserialize<AuthSettings>(s, AuthJson) ?? new AuthSettings();
    }

    public void SaveAuth(AuthSettings a) => PutSetting("auth", JsonSerializer.Serialize(a));

    // ---------- локальный админ ----------

    public LocalAdmin LoadLocalAdmin()
    {
        var s = GetSetting("local_admin");
        return s is null ? new LocalAdmin() : JsonSerializer.Deserialize<LocalAdmin>(s, AuthJson) ?? new LocalAdmin();
    }

    public void SaveLocalAdmin(LocalAdmin a) => PutSetting("local_admin", JsonSerializer.Serialize(a));

    // ---------- локальные пользователи ----------

    public List<LocalUser> LoadLocalUsers()
    {
        EnsureAuthSchema();
        var list = new List<LocalUser>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM local_users ORDER BY login";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<LocalUser>(r.GetString(0), AuthJson) is { } u) list.Add(u);
        return list;
    }

    public LocalUser? LoadLocalUser(string login) =>
        LoadLocalUsers().FirstOrDefault(u => string.Equals(u.Login, login.Trim(), StringComparison.OrdinalIgnoreCase));

    public void SaveLocalUser(LocalUser u)
    {
        EnsureAuthSchema();
        using var c = Open();
        Exec(c, "INSERT INTO local_users(login, json) VALUES($l, $j) ON CONFLICT(login) DO UPDATE SET json=$j",
            ("$l", u.Login), ("$j", JsonSerializer.Serialize(u)));
    }

    /// <summary>Удалить пользователя и его назначения.</summary>
    public int DeleteLocalUser(string login)
    {
        EnsureAuthSchema();
        var gone = LoadAssignments().Where(a => a.Local && string.Equals(a.Who, login, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in gone) DeleteAssignment(a.Id);
        using var c = Open();
        Exec(c, "DELETE FROM local_users WHERE login=$l", ("$l", login));
        return gone.Count;
    }

    /// <summary>Учётка со своим TOTP по логину: локальный админ или локальный пользователь.</summary>
    public ITotpAccount? LoadTotpAccount(string login)
    {
        var la = LoadLocalAdmin();
        if (string.Equals(la.Login, login.Trim(), StringComparison.OrdinalIgnoreCase)) return la;
        return LoadLocalUser(login);
    }

    public void SaveTotpAccount(ITotpAccount a)
    {
        if (a is LocalAdmin la) SaveLocalAdmin(la);
        else if (a is LocalUser u) SaveLocalUser(u);
    }

    public string Protect(string plain) => _box.Protect(plain);
    public string Unprotect(string enc) => enc.Length == 0 ? "" : _box.Unprotect(enc);

    // ---------- роли ----------

    /// <summary>Роли: встроенные — из кода, свои — из базы (только имя и права).</summary>
    public List<Role> LoadRoles()
    {
        EnsureAuthSchema();
        var list = Role.Builtins();
        var builtin = list.Select(b => b.Id).ToHashSet();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM roles ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<Role>(r.GetString(0), AuthJson) is { } role && !builtin.Contains(role.Id))
            {
                role.Builtin = false;
                role.Perms.RemoveAll(Perm.AdminOnly.Contains);
                list.Add(role);
            }
        return list;
    }

    public void SaveRole(Role r)
    {
        if (r.Builtin || Role.Builtins().Any(b => b.Id == r.Id)) throw new InvalidOperationException("Встроенную роль менять нельзя.");
        r.Perms.RemoveAll(Perm.AdminOnly.Contains);
        EnsureAuthSchema();
        using var c = Open();
        Exec(c, "INSERT INTO roles(id, json) VALUES($id, $j) ON CONFLICT(id) DO UPDATE SET json=$j",
            ("$id", r.Id), ("$j", JsonSerializer.Serialize(r)));
    }

    public void DeleteRole(string id)
    {
        if (Role.Builtins().Any(b => b.Id == id)) throw new InvalidOperationException("Встроенную роль удалить нельзя.");
        EnsureAuthSchema();
        using var c = Open();
        Exec(c, "DELETE FROM roles WHERE id=$id", ("$id", id));
        Exec(c, "DELETE FROM assignments WHERE json LIKE $p", ("$p", $"%\"RoleId\":\"{id}\"%"));
    }

}
