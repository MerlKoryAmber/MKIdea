using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinuxOps.Models;
using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services.Api;

/// <summary>Итог действия на одном хосте.</summary>
public sealed class ApiHostResult
{
    public string HostId { get; set; } = "";
    public string Host { get; set; } = "";
    /// <summary>OK | WARN | FAIL | SKIP</summary>
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    /// <summary>Подключение к хосту: «SSH OK · CentOS Stream 9 (rhel)».</summary>
    public string Connect { get; set; } = "";
    public string Output { get; set; } = "";
    /// <summary>Разобранный отчёт, если действие его даёт: versions | users | proxy.</summary>
    public object? Report { get; set; }
}

/// <summary>Задание API: действие на хостах, его состояние и результаты.</summary>
public sealed class ApiJob
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // кириллица — как есть, без \u: ответ читают и люди
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter() }
    };

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Client { get; set; } = "";
    public string Action { get; set; } = "";
    public string ActionName { get; set; } = "";
    public Dictionary<string, string> Params { get; set; } = new();
    public List<string> Hosts { get; set; } = new();
    /// <summary>running | done | failed</summary>
    public string Status { get; set; } = "running";
    public string Message { get; set; } = "";
    public DateTime Started { get; set; } = DateTime.UtcNow;
    public DateTime? Finished { get; set; }
    public List<ApiHostResult> Results { get; set; } = new();
}

/// <summary>
/// Запуск действий через API (Ф8). Только встроенные действия с пометкой «только чтение» и без пометки «опасное».
/// Защита — именно этот список: режим dry-run (apply:false) сдерживает только «опасные» действия, а их здесь нет,
/// так что скрипт действия выполняется целиком. Параметры, которые меняют что-то вне хоста (туннель «через этот ПК»), закрыты.
/// Нужно право «Опрос и Dry-run» на каждом хосте. Ключи хостов — только уже подтверждённые (спросить некого).
/// Хосты на время — под замком, как у людей; итог — в History и аудит от имени учётки.
/// </summary>
public sealed class ApiJobs(WebStore store, HostLocks locks, AuditLog audit, ILogger<ApiJobs> log)
{
    public const int MaxHosts = 50, MaxRunning = 3;
    private readonly ConcurrentDictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Что доступно через API: встроенное, «только чтение», не «опасное», bash.</summary>
    public static IEnumerable<ActionDefinition> ReadOnlyActions =>
        BuiltinActions.All.Where(a => a.ReadOnly && !a.Destructive && !a.IsPowerShell);

    /// <summary>
    /// Параметры, которых в API нет: туннель «через этот ПК» открывает хосту выход в интернет через сервер —
    /// по подсказке действия только по согласованию с ИБ, решает человек, а не внешний сервис. Всегда — значение по умолчанию.
    /// </summary>
    public static readonly HashSet<string> ClosedParams = new(StringComparer.OrdinalIgnoreCase) { VersionsCheck.ViaPcParam };

    public static IEnumerable<ActionParam> ApiParams(ActionDefinition a) => a.Parameters.Where(p => !ClosedParams.Contains(p.Name));

    public sealed record StartResult(int Code, string Error = "", string Message = "", ApiJob? Job = null);

    public StartResult Start(ApiClient client, UserInfo user, string ip, string actionId, List<string> hostIds, Dictionary<string, string>? args)
    {
        var who = $"api:{client.Name} ({ip})";
        actionId = (actionId ?? "").Trim();
        var action = ReadOnlyActions.FirstOrDefault(a => a.Id == actionId);
        if (action is null)
        {
            var known = ActionCatalog.LoadAll().Any(a => a.Id == actionId);
            audit.Write(who, "API: отказ", known ? $"действие «{actionId}» меняет хост или не встроенное — через API не запускается" : $"нет действия «{actionId}»");
            return known
                ? new(403, "action_not_readonly", "Через API запускаются только встроенные действия, которые получают информацию.")
                : new(404, "action_not_found", "Нет такого действия.");
        }
        hostIds = (hostIds ?? new()).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        if (hostIds.Count == 0 || hostIds.Count > MaxHosts) return new(400, "bad_request", $"Нужно от 1 до {MaxHosts} хостов.");
        var all = store.LoadHosts();
        var folders = store.LoadFolders();
        var hosts = new List<StoredHost>();
        foreach (var id in hostIds)
        {
            var h = all.FirstOrDefault(x => x.Id == id && !x.IsWindows);
            // хост, которого учётка не видит, — «нет такого», как и несуществующий
            if (h is null || !Access.CanOn(user, Perm.HostsView, h, folders)) return new(404, "host_not_found", $"Нет хоста {id}.");
            if (!Access.CanOn(user, Perm.RunCheck, h, folders))
            {
                audit.Write(who, "API: отказ", $"{action.Name}: нет права «Опрос и Dry-run» на {h.Config.Title}");
                return new(403, "forbidden", $"Нет права запускать действия на {h.Config.Title}.");
            }
            hosts.Add(h);
        }
        // параметры — только объявленные у действия и открытые для API; остальное — значения по умолчанию
        if (args?.Keys.FirstOrDefault(k => ClosedParams.Contains(k)) is { } closed)
        {
            audit.Write(who, "API: отказ", $"{action.Name}: параметр «{closed}» через API не задаётся");
            return new(403, "param_forbidden", $"Параметр «{closed}» через API не задаётся (туннель через сервер — только из панели).");
        }
        if (args?.Keys.FirstOrDefault(k => action.Parameters.All(x => x.Name != k)) is { } extra)
            return new(400, "bad_request", $"У действия нет параметра «{extra}».");
        var p = new Dictionary<string, string>();
        foreach (var prm in action.Parameters)
            p[prm.Name] = !ClosedParams.Contains(prm.Name) && args is not null && args.TryGetValue(prm.Name, out var v) ? v : prm.Default;

        try { store.PruneApiJobs(TimeSpan.FromDays(7)); } catch (Exception ex) { log.LogWarning(ex, "API: чистка заданий"); }
        var n = _running.AddOrUpdate(client.Name, 1, (_, c) => c + 1);
        if (n > MaxRunning)
        {
            _running.AddOrUpdate(client.Name, 0, (_, c) => c - 1);
            return new(429, "too_many_jobs", $"Уже идёт {MaxRunning} задания этой учётки — дождитесь окончания.");
        }
        var job = new ApiJob
        {
            Client = client.Name, Action = action.Id, ActionName = action.Name, Params = p,
            Hosts = hosts.Select(h => h.Config.Title).ToList()
        };
        try { store.SaveApiJob(job); }
        catch (Exception ex)
        {
            // задание не началось — место в лимите учётки вернуть
            _running.AddOrUpdate(client.Name, 0, (_, c) => Math.Max(0, c - 1));
            log.LogError(ex, "API: сохранить новое задание");
            return new(503, "store_busy", "Не удалось записать задание — повторите позже.");
        }
        audit.Write(who, "API: запуск", $"{action.Name} на {string.Join(", ", job.Hosts)} · задание {job.Id}");
        _ = Task.Run(() => Run(job, action, hosts, who));
        return new(202, Job: job);
    }

    private async Task Run(ApiJob job, ActionDefinition action, List<StoredHost> hosts, string who)
    {
        var got = new List<string>();
        try
        {
            var keys = hosts.GroupBy(h => HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            got = locks.TryLock(keys.Keys, who, action.Name + " (API)", out var busy);
            foreach (var b in busy)
                if (keys.TryGetValue(b.Key, out var bh))
                    job.Results.Add(new ApiHostResult { HostId = bh.Id, Host = bh.Config.Title, Status = "SKIP", Summary = $"занят: {b.By.Who}, {b.By.What}" });
            // две записи Inventory на один адрес и порт — одна машина: идёт первая, остальные — SKIP (замок у них общий)
            foreach (var dup in hosts.Where(h => !ReferenceEquals(keys[HostLocks.Key(h.Config.Host, h.Config.Port)], h)))
                job.Results.Add(new ApiHostResult
                {
                    HostId = dup.Id, Host = dup.Config.Title, Status = "SKIP",
                    Summary = $"тот же адрес, что у {keys[HostLocks.Key(dup.Config.Host, dup.Config.Port)].Config.Title}, — проверяется один раз"
                });
            var free = keys.Values.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)).ToList();
            var map = new Dictionary<HostConfig, StoredHost>();
            var connect = free.Select(h => { var c = store.ForConnect(h); map[c] = h; return c; }).ToList();
            var gate = new object();
            var preflight = new Dictionary<string, string>();
            var progress = new SyncProgress<PlanProgress>(pr =>
            {
                if (pr.Row is not { } row || row.Status is "RUN" or "INFO" or "DRY-RUN") return;
                StoredHost? stored = null;
                if (pr.Host is not null) map.TryGetValue(pr.Host, out stored);
                if (stored is null) return;
                // проверка подключения — не результат действия: удалась — в поле connect, нет — это и есть итог хоста
                if (row.Title.StartsWith("Pre-flight") && row.Status == "OK")
                {
                    lock (gate) preflight[stored.Id] = row.Preview;
                    return;
                }
                if (row.Versions is { } vr) try { store.SaveFleet(FleetSnapshot.From(stored, vr, who)); } catch { }
                lock (gate)
                    job.Results.Add(new ApiHostResult
                    {
                        HostId = stored.Id, Host = stored.Config.Title, Status = row.Status, Summary = row.Preview, Output = row.Detail,
                        Report = (object?)row.Versions ?? (object?)row.Audit ?? row.Proxy
                    });
            });
            var args = job.Params.ToDictionary(kv => action.Id + ":" + kv.Key, kv => kv.Value);
            // apply: false — всегда dry-run
            var result = await new PlanRunner().RunAsync(connect, new[] { action }, false, progress, CancellationToken.None, args);
            if (result.History.Count > 0) store.AppendHistory(result.History, who);
            foreach (var h in free.Where(h => job.Results.All(r => r.HostId != h.Id)))
                job.Results.Add(new ApiHostResult { HostId = h.Id, Host = h.Config.Title, Status = "FAIL", Summary = "нет результата (см. History)" });
            foreach (var r in job.Results)
                if (preflight.TryGetValue(r.HostId, out var pf)) r.Connect = pf;
            job.Status = "done";
            var c = (string s) => job.Results.Count(r => r.Status == s);
            job.Message = $"{c("OK")} OK, {c("WARN")} WARN, {c("FAIL")} FAIL, {c("SKIP")} SKIP";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "API: задание {Id}", job.Id);
            job.Status = "failed";
            job.Message = ex.Message;
        }
        finally
        {
            if (got.Count > 0) locks.Release(got);
            _running.AddOrUpdate(job.Client, 0, (_, n) => Math.Max(0, n - 1));
            job.Finished = DateTime.UtcNow;
            try { store.SaveApiJob(job); } catch (Exception ex) { log.LogError(ex, "API: сохранить задание"); }
            audit.Write(who, "API: итог", $"{job.ActionName}: {job.Message} · задание {job.Id}");
        }
    }

    private sealed class SyncProgress<T>(Action<T> act) : IProgress<T>
    {
        private readonly object _gate = new();
        public void Report(T value) { lock (_gate) act(value); }
    }
}
