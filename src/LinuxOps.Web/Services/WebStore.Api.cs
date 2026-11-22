using System.Text.Json;
using LinuxOps.Web.Services.Api;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services;

/// <summary>API (Ф8): сервисные учётки (токен — только хэш) и задания с результатами (хранятся 7 дней).</summary>
public sealed partial class WebStore
{
    private volatile bool _apiReady;
    private readonly object _apiInit = new();

    private void EnsureApiSchema()
    {
        if (_apiReady) return;
        lock (_apiInit)
        {
            if (_apiReady) return;
            using (var c = Open())
            {
                Exec(c, "CREATE TABLE IF NOT EXISTS api_clients(name TEXT PRIMARY KEY COLLATE NOCASE, json TEXT NOT NULL)");
                Exec(c, "CREATE TABLE IF NOT EXISTS api_jobs(id TEXT PRIMARY KEY, client TEXT NOT NULL COLLATE NOCASE, created TEXT NOT NULL, json TEXT NOT NULL)");
                // первое обращение после старта службы: «running» из прошлого запуска уже не идут
                FailInterruptedApiJobs(c);
            }
            _apiReady = true;
        }
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

    /// <summary>Новая учётка. false — такая уже есть.</summary>
    public bool AddApiClient(ApiClient a)
    {
        EnsureApiSchema();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO api_clients(name, json) VALUES($n, $j) ON CONFLICT(name) DO NOTHING";
        cmd.Parameters.AddWithValue("$n", a.Name);
        cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(a));
        return cmd.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Изменить учётку по свежей копии из базы: запись — только если строка не поменялась с чтения (иначе ещё раз).
    /// Так правка админа не затирает другую правку, а удалённая учётка не воскресает. null — учётки нет.
    /// </summary>
    public ApiClient? UpdateApiClient(string name, Action<ApiClient> change)
    {
        EnsureApiSchema();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var c = Open();
            string? old;
            using (var get = c.CreateCommand())
            {
                get.CommandText = "SELECT json FROM api_clients WHERE name=$n";
                get.Parameters.AddWithValue("$n", name);
                old = get.ExecuteScalar() as string;
            }
            if (old is null || JsonSerializer.Deserialize<ApiClient>(old, AuthJson) is not { } a) return null;
            change(a);
            using var put = c.CreateCommand();
            put.CommandText = "UPDATE api_clients SET json=$j WHERE name=$n AND json=$old";
            put.Parameters.AddWithValue("$n", name);
            put.Parameters.AddWithValue("$old", old);
            put.Parameters.AddWithValue("$j", JsonSerializer.Serialize(a));
            if (put.ExecuteNonQuery() == 1) return a;
        }
        throw new InvalidOperationException($"Учётку API {name} меняют одновременно — повторите.");
    }

    /// <summary>Время последнего запроса — только это поле: запрос с токеном не перепишет учётку, которую в это время меняет админ.</summary>
    public void TouchApiClient(string name, DateTime utc)
    {
        EnsureApiSchema();
        using var c = Open();
        Exec(c, "UPDATE api_clients SET json=json_set(json, '$.LastUsed', $t) WHERE name=$n",
            ("$n", name), ("$t", utc.ToUniversalTime().ToString("O")));
    }

    /// <summary>Удалить учётку, её назначения и задания: новая учётка с тем же именем не увидит чужих результатов.</summary>
    public int DeleteApiClient(string name)
    {
        EnsureApiSchema();
        var gone = LoadAssignments().Where(a => a.Api && string.Equals(a.Who, name, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in gone) DeleteAssignment(a.Id);
        using var c = Open();
        Exec(c, "DELETE FROM api_clients WHERE name=$n", ("$n", name));
        Exec(c, "DELETE FROM api_jobs WHERE client=$n", ("$n", name));
        return gone.Count;
    }

    /// <summary>Задания, оборванные остановкой службы (остались «running»), — в «failed». Из EnsureApiSchema под замком, раз за запуск.</summary>
    private static void FailInterruptedApiJobs(Microsoft.Data.Sqlite.SqliteConnection c) =>
        Exec(c, "UPDATE api_jobs SET json=json_set(json, '$.status', 'failed', '$.message', $m, '$.finished', $t) WHERE json_extract(json, '$.status')='running'",
            ("$m", "прервано: служба LinuxOps Web перезапускалась во время задания"), ("$t", DateTime.UtcNow.ToString("O")));

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

    /// <summary>Последние задания учётки (свежие сверху, не больше limit; старше 7 дней убирает PruneApiJobs) — для списка в API.</summary>
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
