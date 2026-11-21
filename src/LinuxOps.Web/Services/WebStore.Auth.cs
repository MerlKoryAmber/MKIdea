using System.Text.Json;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>Вход и роли: настройки входа и локальный админ — в settings (JSON), роли — в своей таблице.</summary>
public sealed partial class WebStore
{
    private static readonly JsonSerializerOptions AuthJson = new() { PropertyNameCaseInsensitive = true };

    private void EnsureAuthSchema()
    {
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS roles(id TEXT PRIMARY KEY, json TEXT NOT NULL)");
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

    public string Protect(string plain) => _box.Protect(plain);
    public string Unprotect(string enc) => enc.Length == 0 ? "" : _box.Unprotect(enc);

    // ---------- роли ----------

    /// <summary>Роли: встроенные всегда есть, их права — из кода (группы AD и папки — из базы).</summary>
    public List<Role> LoadRoles()
    {
        EnsureAuthSchema();
        var stored = new Dictionary<string, Role>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT json FROM roles ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (JsonSerializer.Deserialize<Role>(r.GetString(0), AuthJson) is { } role) stored[role.Id] = role;
        }
        var list = new List<Role>();
        foreach (var b in Role.Builtins())
        {
            if (stored.Remove(b.Id, out var s)) { b.AdGroups = s.AdGroups; b.AdUsers = s.AdUsers; b.Folders = s.Folders; b.Hosts = s.Hosts; }
            list.Add(b);
        }
        list.AddRange(stored.Values.Select(r => { r.Builtin = false; return r; }));
        return list;
    }

    public void SaveRole(Role r)
    {
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
    }

}
