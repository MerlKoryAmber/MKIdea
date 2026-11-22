using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>Расписание проверки парка: включено, во сколько (раз в сутки), сверка CVE через сервер.</summary>
public sealed record FleetSchedule(bool Enabled, string Time, bool ViaServer);

/// <summary>
/// Регулярная проверка парка (Ф2+): раз в сутки в заданное время — «Версии ПО» по всем хостам Linux;
/// итог каждого хоста — в сводку по парку, запуск — в History и аудит от имени «расписание».
/// Ключи хостов — только уже подтверждённые (спросить некого): новый ключ — отказ по этому хосту.
/// </summary>
public sealed class FleetCheck(WebStore store, HostLocks locks, AuditLog audit, ILogger<FleetCheck> log) : BackgroundService
{
    public const string Who = "расписание";
    private int _running;

    public bool Running => Volatile.Read(ref _running) == 1;

    /// <summary>Итог последнего запуска (для настроек).</summary>
    public string LastText => store.GetSetting("fleet.last") ?? "";

    public FleetSchedule Load() => new(
        store.GetSetting("fleet.enabled") == "1",
        ValidTime(store.GetSetting("fleet.time")) ?? "03:00",
        store.GetSetting("fleet.via") == "1");

    public void Save(FleetSchedule s, string who)
    {
        store.PutSetting("fleet.enabled", s.Enabled ? "1" : "0");
        store.PutSetting("fleet.time", ValidTime(s.Time) ?? "03:00");
        store.PutSetting("fleet.via", s.ViaServer ? "1" : "0");
        // включили после времени запуска — первый раз завтра, а не сразу
        if (s.Enabled && ValidTime(s.Time) is { } t && DateTime.Now >= DateTime.Now.Date + TimeOnly.ParseExact(t, "HH:mm").ToTimeSpan())
            store.PutSetting("fleet.day", DateTime.Now.ToString("yyyy-MM-dd"));
        audit.Write(who, "настройки: проверка по расписанию", s.Enabled ? $"ежедневно в {s.Time}" + (s.ViaServer ? ", CVE через сервер" : "") : "выключена");
    }

    public static string? ValidTime(string? s) =>
        TimeOnly.TryParseExact(s?.Trim(), "HH:mm", out var t) ? t.ToString("HH:mm") : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                var s = Load();
                if (!s.Enabled) continue;
                var now = DateTime.Now;
                var due = now.Date + TimeOnly.ParseExact(s.Time, "HH:mm").ToTimeSpan();
                var today = now.ToString("yyyy-MM-dd");
                // пропущенное время (служба была остановлена) догоняем в тот же день
                if (now < due || store.GetSetting("fleet.day") == today) continue;
                store.PutSetting("fleet.day", today);
                await Run(Who, s.ViaServer, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogError(ex, "проверка уязвимостей по расписанию"); }
        }
    }

    /// <summary>
    /// Запустить сейчас (из настроек). false — уже идёт. Проверка — та же, что по расписанию, и идёт от его имени
    /// (History, «Уязвимости», замки); кто нажал — отдельной записью в аудите.
    /// </summary>
    public bool RunNow(string pressedBy)
    {
        if (Running) return false;
        audit.Write(pressedBy, "проверка уязвимостей: запущена вручную", "проверка идёт от имени «" + Who + "»");
        var s = Load();
        const string who = Who;
        _ = Task.Run(async () =>
        {
            try { await Run(who, s.ViaServer, CancellationToken.None); }
            catch (Exception ex) { log.LogError(ex, "проверка уязвимостей"); }
        });
        return true;
    }

    private async Task Run(string who, bool viaServer, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        var got = new List<string>();
        try
        {
            var check = ActionCatalog.LoadAll().FirstOrDefault(a => a.Id == VersionsCheck.ActionId)
                        ?? throw new InvalidOperationException("нет действия «Версии ПО»");
            var hosts = store.LoadHosts().Where(h => !h.IsWindows).ToList();
            if (hosts.Count == 0) { Finish(who, "хостов Linux нет"); return; }

            var keys = hosts.GroupBy(h => HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            got = locks.TryLock(keys.Keys, who, "Версии ПО (проверка уязвимостей)", out var busy);
            var free = hosts.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)).ToList();
            audit.Write(who, "проверка уязвимостей: начата", $"хостов {free.Count}" + (busy.Count > 0 ? $", заняты: {string.Join(", ", busy.Select(b => b.Key))}" : ""));

            var map = new Dictionary<HostConfig, StoredHost>();
            var connect = free.Select(h => { var c = store.ForConnect(h); map[c] = h; return c; }).ToList();
            int ok = 0, fail = 0;
            var failed = new List<string>();
            var progress = new SyncProgress<PlanProgress>(p =>
            {
                if (p.Row is not { } row) return;
                StoredHost? stored = null;
                if (p.Host is not null) map.TryGetValue(p.Host, out stored);
                if (row.Versions is { } vr && stored is not null)
                {
                    store.SaveFleet(FleetSnapshot.From(stored, vr, who));
                    ok++;
                }
                else if (row.Status == "FAIL")
                {
                    fail++;
                    failed.Add(row.Title.Split(" · ")[0]);
                }
            });
            var args = new Dictionary<string, string> { [VersionsCheck.ActionId + ":" + VersionsCheck.ViaPcParam] = viaServer ? "да" : "нет" };
            var result = await new PlanRunner().RunAsync(connect, new[] { check }, false, progress, ct, args);
            if (result.History.Count > 0) store.AppendHistory(result.History, who);
            Finish(who, $"проверено {ok} из {free.Count}" + (fail > 0 ? $", ошибки: {string.Join(", ", failed.Distinct())}" : "")
                        + (busy.Count > 0 ? $", заняты: {busy.Count}" : ""));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Finish(who, "ошибка: " + ex.Message);
        }
        finally
        {
            if (got.Count > 0) locks.Release(got);
            Volatile.Write(ref _running, 0);
        }
    }

    private void Finish(string who, string text)
    {
        store.PutSetting("fleet.last", $"{DateTime.Now:yyyy-MM-dd HH:mm} · {who} · {text}");
        audit.Write(who, "проверка уязвимостей: итог", text);
    }

    /// <summary>Progress без контекста синхронизации: обработчик сразу, в потоке ядра (а не когда-нибудь потом в пуле).</summary>
    private sealed class SyncProgress<T>(Action<T> act) : IProgress<T>
    {
        private readonly object _gate = new();
        public void Report(T value) { lock (_gate) act(value); }
    }
}
