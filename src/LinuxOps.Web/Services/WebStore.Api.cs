using System.Text.Json;
using LinuxOps.Web.Services.Api;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>API (Ф8): сервисные учётки (токен — только хэш) и задания с результатами (хранятся 7 дней).</summary>
public sealed partial class WebStore
{
    private bool _apiReady;

    private void EnsureApiSchema()
    {
        if (_apiReady) return;
        using (var c = Open())
        {
            Exec(c, "CREATE TABLE IF NOT EXISTS api_clients(name TEXT PRIMARY KEY COLLATE NOCASE, json TEXT NOT NULL)");
            Exec(c, "CREATE TABLE IF NOT EXISTS api_jobs(id TEXT PRIMARY KEY, client TEXT NOT NULL COLLATE NOCASE, created TEXT NOT NULL, json TEXT NOT NULL)");
        }
        _apiReady = true;
    }

    public List<ApiClient> LoadApiClients()
    {
        EnsureApiSchema();
        var list = new List<ApiClient>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM api_clients ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<ApiClient>(r.GetString(0), AuthJson) is { } a) list.Add(a);
        return list;
    }

    public ApiClient? LoadApiClient(string name) =>
        LoadApiClients().FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Учётка по токену: сравнение хэшей за постоянное время.</summary>
    public ApiClient? FindApiClient(string token)
    {
        var hash = System.Text.Encoding.ASCII.GetBytes(ApiClient.Hash(token));
        return LoadApiClients().FirstOrDefault(a => a.TokenHash.Length > 0 &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a.TokenHash), hash));
    }

    public void SaveApiClient(ApiClient a)
    {
        EnsureApiSchema();
        using var c = Open();
        Exec(c, "INSERT INTO api_clients(name, json) VALUES($n, $j) ON CONFLICT(name) DO UPDATE SET json=$j",
            ("$n", a.Name), ("$j", JsonSerializer.Serialize(a)));
    }

    /// <summary>Удалить учётку и её назначения. Задания остаются до общей чистки (для разбора).</summary>
    public int DeleteApiClient(string name)
    {
        EnsureApiSchema();
        var gone = LoadAssignments().Where(a => a.Api && string.Equals(a.Who, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in gone) DeleteAssignment(a.Id);
        using var c = Open();
        Exec(c, "DELETE FROM api_clients WHERE name=$n", ("$n", name));
        return gone.Count;
    }

    public void SaveApiJob(ApiJob j)
    {
        EnsureApiSchema();
        using var c = Open();
        Exec(c, "INSERT INTO api_jobs(id, client, created, json) VALUES($id, $cl, $cr, $j) ON CONFLICT(id) DO UPDATE SET json=$j",
            ("$id", j.Id), ("$cl", j.Client), ("$cr", j.Started.ToString("O")), ("$j", JsonSerializer.Serialize(j, ApiJob.Json)));
    }

    public ApiJob? LoadApiJob(string id)
    {
        EnsureApiSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM api_jobs WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<ApiJob>(s, ApiJob.Json) : null;
    }

    /// <summary>Задания учётки за последние сутки (свежие сверху) — для списка в API.</summary>
    public List<ApiJob> LoadApiJobs(string client, int limit = 50)
    {
        EnsureApiSchema();
        var list = new List<ApiJob>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM api_jobs WHERE client=$cl ORDER BY created DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$cl", client);
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<ApiJob>(r.GetString(0), ApiJob.Json) is { } j) list.Add(j);
        return list;
    }

    public int PruneApiJobs(TimeSpan keep)
    {
        EnsureApiSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM api_jobs WHERE created < $t";
        cmd.Parameters.AddWithValue("$t", (DateTime.UtcNow - keep).ToString("O"));
        return cmd.ExecuteNonQuery();
    }
}
