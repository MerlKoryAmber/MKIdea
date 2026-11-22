using System.Collections.Concurrent;
using System.Text.Json;
using LinuxOps.Web.Services.Auth;

namespace LinuxOps.Web.Services.Api;

/// <summary>
/// REST API для внешних сервисов (Ф8) — только чтение: список информационных действий, хосты учётки,
/// запуск задания и его результат. Вход — `Authorization: Bearer lo_…` сервисной учётки; cookie не принимаются.
/// Ошибки — {"error":{"code","message"}}. Описание — /api/v1/openapi.json.
/// </summary>
public static class ApiEndpoints
{
    private sealed record Caller(ApiClient Client, UserInfo User, string Ip)
    {
        public string Who => $"api:{Client.Name} ({Ip})";
    }

    public sealed record JobRequest(string? Action, List<string>? Hosts, Dictionary<string, string>? Params);

    // отказы в аудит — не чаще раза в минуту на ключ (адрес или учётку): перебор токенов не засорит журнал
    private static readonly ConcurrentDictionary<string, DateTime> DenyLogged = new();
    private const int DenyLoggedMax = 10_000;

    private static bool ShouldLogDeny(string key)
    {
        var now = DateTime.UtcNow;
        if (DenyLogged.TryGetValue(key, out var last) && now - last <= TimeSpan.FromMinutes(1)) return false;
        if (DenyLogged.Count >= DenyLoggedMax)
            foreach (var kv in DenyLogged)
                if (now - kv.Value > TimeSpan.FromMinutes(1)) DenyLogged.TryRemove(kv.Key, out _);
        // и после чистки полно (идёт перебор с тысяч адресов) — пишем, но не запоминаем: журнал важнее памяти
        if (DenyLogged.Count < DenyLoggedMax) DenyLogged[key] = now;
        return true;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, ApiJob.Json, statusCode: status);

    /// <summary>Учётка по токену. null — ответ с ошибкой уже готов.</summary>
    private static (Caller? Caller, IResult? Fail) Authenticate(HttpContext ctx, WebStore store, AuditLog audit)
    {
        var ip = AuthEndpoints.ClientIp(ctx);
        var h = ctx.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : "";
        var client = token.StartsWith(ApiClient.Prefix) ? store.FindApiClient(token) : null;
        if (client is null || !client.Enabled)
        {
            if (ShouldLogDeny("ip:" + ip))
                audit.Write($"api ({ip})", "API: отказ", client is null ? (token.Length == 0 ? "нет токена" : "неверный токен") : $"учётка {client.Name} выключена");
            return (null, Error(401, "unauthorized", "Нужен действующий токен: Authorization: Bearer lo_…"));
        }
        var user = UserInfo.Build(client.Name, client.Name, UserKind.Api, new List<string>(), store.LoadRoles(), store.LoadAssignments());
        if (user.Grants.Count == 0)
        {
            if (ShouldLogDeny("client:" + client.Name))
                audit.Write($"api:{client.Name} ({ip})", "API: отказ", "у учётки нет назначений");
            return (null, Error(403, "no_access", "У учётки нет назначений в LinuxOps Web."));
        }
        if (client.LastUsed is null || DateTime.UtcNow - client.LastUsed > TimeSpan.FromMinutes(1))
            store.TouchApiClient(client.Name, DateTime.UtcNow);
        return (new Caller(client, user, ip), null);
    }

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AllowAnonymous().DisableAntiforgery();

        api.MapGet("/openapi.json", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "api", "openapi.json"), "application/json"));

        api.MapGet("/actions", (HttpContext ctx, WebStore store, AuditLog audit) =>
        {
            var (c, fail) = Authenticate(ctx, store, audit);
            if (c is null) return fail!;
            return Results.Json(ApiJobs.ReadOnlyActions.Select(a => new
            {
                id = a.Id, name = a.Name, description = a.Description, distros = a.Distros, timeout_seconds = a.TimeoutSeconds,
                @params = ApiJobs.ApiParams(a).Select(p => new { name = p.Name, label = p.Label, kind = p.Kind.Length > 0 ? p.Kind : "text", @default = p.Default })
            }), ApiJob.Json);
        });

        api.MapGet("/hosts", (HttpContext ctx, WebStore store, AuditLog audit) =>
        {
            var (c, fail) = Authenticate(ctx, store, audit);
            if (c is null) return fail!;
            var folders = store.LoadFolders();
            return Results.Json(store.LoadHosts().Where(h => !h.IsWindows && Access.CanOn(c.User, Perm.HostsView, h, folders)).Select(h => new
            {
                id = h.Id, name = h.Config.Title, address = h.Config.Host, port = h.Config.Port,
                folder = FolderPath(h.Config.FolderId, folders),
                can_run = Access.CanOn(c.User, Perm.RunCheck, h, folders)
            }), ApiJob.Json);
        });

        api.MapPost("/jobs", async (HttpContext ctx, WebStore store, AuditLog audit, ApiJobs jobs) =>
        {
            var (c, fail) = Authenticate(ctx, store, audit);
            if (c is null) return fail!;
            JobRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<JobRequest>(ApiJob.Json); }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { req = null; }
            if (req is null || string.IsNullOrWhiteSpace(req.Action)) return Error(400, "bad_request", "Тело: {\"action\":\"versions-check\",\"hosts\":[\"<id>\"],\"params\":{}}");
            var r = jobs.Start(c.Client, c.User, c.Ip, req.Action, req.Hosts ?? new(), req.Params);
            return r.Job is { } j
                ? Results.Json(View(j, false), ApiJob.Json, statusCode: 202)
                : Error(r.Code, r.Error, r.Message);
        });

        api.MapGet("/jobs", (HttpContext ctx, WebStore store, AuditLog audit) =>
        {
            var (c, fail) = Authenticate(ctx, store, audit);
            if (c is null) return fail!;
            return Results.Json(store.LoadApiJobs(c.Client.Name).Select(j => View(j, false)), ApiJob.Json);
        });

        api.MapGet("/jobs/{id}", (string id, HttpContext ctx, WebStore store, AuditLog audit) =>
        {
            var (c, fail) = Authenticate(ctx, store, audit);
            if (c is null) return fail!;
            var j = store.LoadApiJob(id);
            // чужое задание — «нет такого»
            if (j is null || !string.Equals(j.Client, c.Client.Name, StringComparison.OrdinalIgnoreCase))
                return Error(404, "job_not_found", "Нет такого задания.");
            return Results.Json(View(j, true), ApiJob.Json);
        });
    }

    /// <summary>Задание для ответа: со списком результатов или без них.</summary>
    private static ApiJob View(ApiJob j, bool full) => full ? j : new ApiJob
    {
        Id = j.Id, Client = j.Client, Action = j.Action, ActionName = j.ActionName, Params = j.Params, Hosts = j.Hosts,
        Status = j.Status, Message = j.Message, Started = j.Started, Finished = j.Finished, Results = new()
    };

    private static string FolderPath(string id, IReadOnlyList<LinuxOps.Models.HostFolder> folders)
    {
        var parts = new List<string>();
        for (var f = folders.FirstOrDefault(x => x.Id == id); f is not null; f = folders.FirstOrDefault(x => x.Id == f.ParentId))
            parts.Insert(0, f.Name);
        return string.Join(" / ", parts);
    }
}
