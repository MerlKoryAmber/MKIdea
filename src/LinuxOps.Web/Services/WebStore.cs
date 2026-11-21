using System.Text.Json;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Data.Sqlite;

namespace LinuxOps.Web.Services;

/// <summary>Хост в базе сервиса: id строки + настройки (без секретов) + есть ли пароль / ключ.</summary>
public sealed class StoredHost
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public HostConfig Config { get; set; } = new();
    public bool HasPassword { get; set; }
    public bool HasKey { get; set; }
    /// <summary>Имя файла ключа, загруженного в сервис (для подписи).</summary>
    public string KeyName { get; set; } = "";
}

/// <summary>
/// База сервиса (SQLite, WAL): хосты и папки, секреты (зашифрованы <see cref="SecretBox"/>), история, аудит.
/// Аудит — только дописывание: удаления и правки в коде нет.
/// </summary>
public sealed partial class WebStore
{
    private readonly SecretBox _box;
    private readonly string _cs;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public WebStore(SecretBox box)
    {
        _box = box;
        _cs = new SqliteConnectionStringBuilder { DataSource = WebPaths.DbFile }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS folders(id TEXT PRIMARY KEY, name TEXT NOT NULL, parent TEXT NOT NULL DEFAULT '', expanded INTEGER NOT NULL DEFAULT 1, sort INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS hosts(id TEXT PRIMARY KEY, json TEXT NOT NULL, sort INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS secrets(owner TEXT NOT NULL, kind TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(owner, kind));
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, mode TEXT, host TEXT, action TEXT, status TEXT, preview TEXT, detail TEXT, who TEXT);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, who TEXT NOT NULL, what TEXT NOT NULL, detail TEXT);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---------- хосты и папки ----------

    public List<StoredHost> LoadHosts()
    {
        using var c = Open();
        var list = new List<StoredHost>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, json FROM hosts ORDER BY sort, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cfg = JsonSerializer.Deserialize<HostConfig>(r.GetString(1), Json) ?? new HostConfig();
                list.Add(new StoredHost { Id = r.GetString(0), Config = cfg });
            }
        }
        var kinds = SecretKinds(c);
        foreach (var h in list)
        {
            h.HasPassword = kinds.Contains((h.Id, "password"));
            h.HasKey = kinds.Contains((h.Id, "key"));
            h.KeyName = Setting(c, "keyname:" + h.Id) ?? "";
        }
        return list;
    }

    private static HashSet<(string, string)> SecretKinds(SqliteConnection c)
    {
        var set = new HashSet<(string, string)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT owner, kind FROM secrets";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add((r.GetString(0), r.GetString(1)));
        return set;
    }

    /// <summary>Сохранить хост. password / key: null — не менять, "" — убрать.</summary>
    public void SaveHost(StoredHost h, string? password = null, string? key = null, string? keyName = null)
    {
        var cfg = h.Config;
        var clean = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = cfg.Username, Auth = cfg.Auth,
            UseSudo = cfg.UseSudo, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId, IsSelected = cfg.IsSelected
        };
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "INSERT INTO hosts(id, json, sort) VALUES($id, $json, (SELECT COALESCE(MAX(sort),0)+1 FROM hosts)) ON CONFLICT(id) DO UPDATE SET json=$json",
            ("$id", h.Id), ("$json", JsonSerializer.Serialize(clean)));
        if (password is not null) SetSecret(c, h.Id, "password", password);
        if (key is not null)
        {
            SetSecret(c, h.Id, "key", key);
            SetSetting(c, "keyname:" + h.Id, key.Length == 0 ? "" : keyName ?? "");
        }
        tx.Commit();
        if (password is not null) h.HasPassword = password.Length > 0;
        if (key is not null) { h.HasKey = key.Length > 0; h.KeyName = key.Length == 0 ? "" : keyName ?? ""; }
    }

    public void DeleteHost(string id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM hosts WHERE id=$id", ("$id", id));
        Exec(c, "DELETE FROM secrets WHERE owner=$id", ("$id", id));
        Exec(c, "DELETE FROM settings WHERE key=$k", ("$k", "keyname:" + id));
    }

    private void SetSecret(SqliteConnection c, string owner, string kind, string plain)
    {
        if (plain.Length == 0) Exec(c, "DELETE FROM secrets WHERE owner=$o AND kind=$k", ("$o", owner), ("$k", kind));
        else Exec(c, "INSERT INTO secrets(owner, kind, value) VALUES($o, $k, $v) ON CONFLICT(owner, kind) DO UPDATE SET value=$v",
            ("$o", owner), ("$k", kind), ("$v", _box.Protect(plain)));
    }

    /// <summary>Копия настроек хоста с расшифрованными паролем и ключом — только для подключения, в интерфейс не попадает.</summary>
    public HostConfig ForConnect(StoredHost h)
    {
        using var c = Open();
        string Secret(string kind)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM secrets WHERE owner=$o AND kind=$k";
            cmd.Parameters.AddWithValue("$o", h.Id);
            cmd.Parameters.AddWithValue("$k", kind);
            return cmd.ExecuteScalar() is string v ? _box.Unprotect(v) : "";
        }
        var cfg = h.Config;
        return new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = cfg.Username,
            Auth = cfg.Auth is "Password" ? "Password" : "PrivateKey",
            UseSudo = cfg.UseSudo, DistroHint = cfg.DistroHint, FolderId = cfg.FolderId,
            Password = Secret("password"), PrivateKeyText = Secret("key")
        };
    }

    public List<HostFolder> LoadFolders()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent, expanded FROM folders ORDER BY sort, rowid";
        using var r = cmd.ExecuteReader();
        var list = new List<HostFolder>();
        while (r.Read())
            list.Add(new HostFolder { Id = r.GetString(0), Name = r.GetString(1), ParentId = r.GetString(2), IsExpanded = r.GetInt64(3) != 0 });
        return list;
    }

    public void SaveFolder(HostFolder f)
    {
        using var c = Open();
        Exec(c, "INSERT INTO folders(id, name, parent, expanded, sort) VALUES($id, $n, $p, $e, (SELECT COALESCE(MAX(sort),0)+1 FROM folders)) " +
                "ON CONFLICT(id) DO UPDATE SET name=$n, parent=$p, expanded=$e",
            ("$id", f.Id), ("$n", f.Name), ("$p", f.ParentId), ("$e", f.IsExpanded ? 1 : 0));
    }

    public void DeleteFolder(string id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM folders WHERE id=$id", ("$id", id));
    }

    // ---------- настройки ----------

    private static string? Setting(SqliteConnection c, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetSetting(SqliteConnection c, string key, string value) =>
        Exec(c, "INSERT INTO settings(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    public string? GetSetting(string key)
    {
        using var c = Open();
        return Setting(c, key);
    }

    public void PutSetting(string key, string value)
    {
        using var c = Open();
        SetSetting(c, key, value);
    }

    // ---------- история ----------

    public void AppendHistory(IEnumerable<HistoryEntry> entries, string who)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var e in entries)
            Exec(c, "INSERT INTO history(at, mode, host, action, status, preview, detail, who) VALUES($at, $m, $h, $a, $s, $p, $d, $w)",
                ("$at", e.AtUtc.ToString("O")), ("$m", e.Mode), ("$h", e.Host), ("$a", e.Action), ("$s", e.Status),
                ("$p", e.Preview), ("$d", e.Detail), ("$w", who));
        tx.Commit();
    }

    public List<(HistoryEntry Entry, string Who)> LoadHistory(int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, mode, host, action, status, preview, detail, who FROM history ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<(HistoryEntry, string)>();
        while (r.Read())
            list.Add((new HistoryEntry
            {
                AtUtc = DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                Mode = r.IsDBNull(1) ? "" : r.GetString(1), Host = r.IsDBNull(2) ? "" : r.GetString(2),
                Action = r.IsDBNull(3) ? "" : r.GetString(3), Status = r.IsDBNull(4) ? "" : r.GetString(4),
                Preview = r.IsDBNull(5) ? "" : r.GetString(5), Detail = r.IsDBNull(6) ? "" : r.GetString(6)
            }, r.IsDBNull(7) ? "" : r.GetString(7)));
        return list;
    }

    // ---------- аудит (только дописывание) ----------

    public void AppendAudit(DateTime atUtc, string who, string what, string detail)
    {
        using var c = Open();
        Exec(c, "INSERT INTO audit(at, who, what, detail) VALUES($at, $w, $x, $d)",
            ("$at", atUtc.ToString("O")), ("$w", who), ("$x", what), ("$d", detail));
    }

    public List<(DateTime At, string Who, string What, string Detail)> LoadAudit(int limit = 500)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, who, what, detail FROM audit ORDER BY id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<(DateTime, string, string, string)>();
        while (r.Read())
            list.Add((DateTime.Parse(r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind),
                r.GetString(1), r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3)));
        return list;
    }
}
