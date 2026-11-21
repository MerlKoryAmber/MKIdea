using System.Text;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.Web.Services;

/// <summary>
/// Состояние одного окна браузера — перенос ActionsViewModel настольного LinuxOps: хосты и выбор, каталог и план,
/// запуск (строки RUN, живой вывод, время), «Версии ПО» и «Обновить отмеченное», опрос, «Добавить пользователя».
/// Хосты и папки — общие в базе сервиса; отметки хостов и плана — свои у окна.
/// </summary>
public sealed partial class WebSession : IDisposable
{
    private readonly WebStore _store;
    private readonly HostLocks _locks;
    private readonly AuditLog _audit;
    private readonly PlanRunner _runner = new();
    private Timer? _tick;

    public WebSession(WebStore store, HostLocks locks, AuditLog audit, PromptChannel prompts, UiDialogs dialogs)
    {
        _store = store;
        _locks = locks;
        _audit = audit;
        Prompts = prompts;
        Dialogs = dialogs;
    }

    public PromptChannel Prompts { get; }
    public UiDialogs Dialogs { get; }

    /// <summary>Кто: на стенде без входа — адрес клиента.</summary>
    public string Who { get; set; } = "?";

    /// <summary>Перерисовка из чужого потока: задаёт компонент (InvokeAsync).</summary>
    public Func<Action, Task>? Dispatch { get; set; }
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    // ---------- данные ----------

    public List<StoredHost> Hosts { get; private set; } = new();
    public List<HostFolder> Folders { get; private set; } = new();
    /// <summary>Папки, отмеченные целиком (в дереве Actions); остальные с отмеченными хостами — «частично».</summary>
    public HashSet<string> CheckedFolders { get; } = new();
    public List<ActionItemView> Actions { get; } = new();
    public List<ResultRow> Results { get; } = new();
    public ResultRow? Selected { get; private set; }
    public string RunStatus { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public bool ShowRawDetail { get; set; }
    public string Tab { get; set; } = "actions";

    /// <summary>Хост, который надо открыть на вкладке после перехода (меню правой кнопки: «Открыть в SSH Files» и т. п.).</summary>
    public StoredHost? OpenHost { get; set; }

    /// <summary>Перейти на вкладку и открыть там хост.</summary>
    public void OpenOn(string tab, StoredHost h)
    {
        OpenHost = h;
        Tab = tab;
        Notify();
    }

    /// <summary>Аудит пользователей по хосту — для «Закрыть доступ» (позже).</summary>
    private readonly Dictionary<string, UserAuditReport> _audits = new(StringComparer.OrdinalIgnoreCase);
    private PackageUpdateReport? _lastUpdate;
    private Dictionary<string, string> _lastArgs = new();
    private CancellationTokenSource? _runCts;

    public void Load()
    {
        // отметки и статус опроса — свои у окна, переживают перечитывание из базы
        var old = Hosts.ToDictionary(h => h.Id);
        Hosts = _store.LoadHosts();
        foreach (var h in Hosts)
        {
            var was = old.GetValueOrDefault(h.Id);
            h.Config.IsSelected = was?.Config.IsSelected ?? false;
            if (was is not null) h.Config.Status = was.Config.Status;
        }
        Folders = _store.LoadFolders();
        ApplyFolderScope();
        LoadCatalog();
    }

    public void LoadCatalog()
    {
        var planned = new HashSet<string>(Actions.Where(a => a.IsSelected).Select(a => a.Definition.Id));
        Actions.Clear();
        foreach (var def in ActionCatalog.LoadAll().Where(a => !a.IsPowerShell))
            Actions.Add(ActionItemView.From(def, planned.Contains(def.Id)));
    }

    /// <summary>Хосты Linux (Actions, опрос, терминал, файлы); Windows — только RDP.</summary>
    public IEnumerable<StoredHost> LinuxHosts => Hosts.Where(h => !h.IsWindows);
    public IEnumerable<StoredHost> SelectedHosts => LinuxHosts.Where(h => h.Config.IsSelected);
    public IEnumerable<ActionDefinition> PlannedActions => Actions.Where(a => a.IsSelected).Select(a => a.Definition);

    public string HostsSelectedText
    {
        get
        {
            var n = SelectedHosts.Count();
            return n == 0 ? "ничего не выбрано" : $"{n} выбрано";
        }
    }

    public string PlanSummary
    {
        get
        {
            var defs = PlannedActions.ToList();
            if (defs.Count == 0) return "План пуст: щёлкните действие в каталоге";
            var n = SelectedHosts.Count();
            return "План: " + string.Join(", ", defs.Select(a => a.Name)) +
                   (n > 0 ? $" → {n} {Plural(n, "хост", "хоста", "хостов")}" : " — отметьте хосты");
        }
    }

    private static string Plural(int n, string one, string few, string many)
        => (n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many;

    // ---------- предпросмотр плана ----------

    public void RefreshPreview()
    {
        if (IsRunning) return;
        Results.Clear();
        Selected = null;
        var planned = PlannedActions.ToList();
        var hosts = SelectedHosts.ToList();
        if (planned.Count == 0)
        {
            Results.Add(PlanRunner.MakeRow("INFO", "План пуст", "Отметьте хосты или vCenter слева и действия в каталоге.", "", true));
            Selected = Results.FirstOrDefault();
            return;
        }
        if (hosts.Count == 0)
            Results.Add(PlanRunner.MakeRow("INFO", "Bash-действия", "отметьте хосты Linux", string.Join(", ", planned.Select(a => a.Name)), true));

        foreach (var host in hosts)
        {
            var distro = DistroInfo.FromHint(host.Config.DistroHint);
            foreach (var action in planned)
            {
                Results.Add(ActionCatalog.Supports(action, distro)
                    ? PlanRunner.MakeRow("INFO", $"{host.Config.Title} · {action.Name}", "войдёт в запуск",
                        $"{action.Privilege}. Дистрибутив по подсказке: {distro.Id} ({distro.Family}).", true)
                    : PlanRunner.MakeRow("SKIP", $"{host.Config.Title} · {action.Name}", $"не для {distro.Id} / {distro.Family}",
                        $"Действие {action.Id} не совпало с id={distro.Id}, family={distro.Family}, like={distro.Like}.", true));
            }
        }
        Selected = Results.FirstOrDefault();
    }

    // ---------- детали ----------

    public void Select(ResultRow? row)
    {
        Selected = row;
        ShowRawDetail = false;
    }

    public string Detail
    {
        get
        {
            if (Selected is null) return "";
            if (Selected.Status == "RUN" && _running.TryGetValue(Selected.Title, out var run))
                return LiveTail(run) ?? "Ждём вывод с сервера…";
            return Selected.Detail;
        }
    }

    // ---------- запуск ----------

    public async Task RunPlan(bool apply)
    {
        if (!Allowed(apply ? Auth.Perm.RunApply : Auth.Perm.RunCheck, apply ? "Применить" : "Dry-run")) return;
        var hosts = SelectedHosts.ToList();
        var actions = PlannedActions.ToList();
        if (actions.Count == 0) { Warn("Отметьте действие."); return; }
        if (hosts.Count == 0) { Warn("Отметьте хост Linux слева — для Bash-действий."); return; }
        if (!AllHostsAllowed(apply ? Auth.Perm.RunApply : Auth.Perm.RunCheck, hosts, apply ? "Применить" : "Dry-run")) return;

        var args = new Dictionary<string, string>();
        if (actions.Any(a => a.Parameters.Count > 0))
        {
            var got = await Dialogs.Params(actions, remembered: _lastArgs);
            if (got is null) return;
            args = got;
            foreach (var kv in got) _lastArgs[kv.Key] = kv.Value;
        }

        if (apply)
        {
            var destructive = actions.Where(a => a.Destructive).ToList();
            if (destructive.Count > 0)
            {
                var list = string.Join(", ", hosts.Select(h => h.Config.Title));
                var msg = destructive.All(a => a.Id == "install-updates")
                    ? $"Установить обновления на {hosts.Count} хостах ({list})? Продолжить?"
                    : $"Применить {string.Join(", ", destructive.Select(a => a.Name))} на {hosts.Count} хостах ({list})? Продолжить?";
                if (!await Dialogs.Confirm(msg)) return;
            }
        }

        Results.Clear();
        Selected = null;
        await Execute(apply ? "применение…" : "dry-run…", hosts, actions, apply, args, apply ? "apply" : "dry-run");
    }

    /// <summary>Общий запуск: блокировка хостов, вопросы ядра в этом окне, история и аудит.</summary>
    private async Task<PlanRunResult?> Execute(string status, List<StoredHost> hosts, List<ActionDefinition> actions, bool apply,
        Dictionary<string, string>? args, string mode, bool keepRunning = false)
    {
        var what = string.Join(", ", actions.Select(a => a.Name)) + (apply ? " (применить)" : "");
        // два хоста с одним адресом — один замок
        var keys = hosts.GroupBy(h => HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var got = _locks.TryLock(keys.Keys, Who, what, out var busy);
        foreach (var (key, by) in busy)
            Results.Add(PlanRunner.MakeRow("WARN", $"{keys[key].Config.Title} · занят",
                $"занят: {by.Who}, {by.What}, с {by.Since:HH:mm:ss}", "Хост занят другим запуском — повторите позже.", ok: true));
        var free = hosts.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase)).ToList();
        Selected ??= Results.FirstOrDefault();
        if (busy.Count > 0)
            _audit.Write(Who, $"{mode}: хост занят", string.Join(", ", busy.Select(b => $"{b.Key} — {b.By.Who}, {b.By.What}")));
        if (free.Count == 0) { RunStatus = "хосты заняты"; Notify(); return null; }

        _audit.Write(Who, $"{mode}: {what}", "хосты: " + string.Join(", ", free.Select(h => $"{h.Config.Title} ({h.Config.Host})")));
        if (!keepRunning) StartRunning(status);
        var map = new Dictionary<HostConfig, StoredHost>();
        var connect = free.Select(h => { var c = _store.ForConnect(h); map[c] = h; return c; }).ToList();
        var progress = new Progress<PlanProgress>(p =>
        {
            if (p.Host is not null && map.TryGetValue(p.Host, out var stored))
            {
                if (p.HostStatus is not null) stored.Config.Status = p.HostStatus;
                if (!string.IsNullOrWhiteSpace(p.DistroHint) && stored.Config.DistroHint != p.DistroHint)
                {
                    stored.Config.DistroHint = p.DistroHint;
                    try { _store.SaveHost(stored); } catch { }
                }
                // «Версии ПО» — последний итог хоста в сводку по парку
                if (p.Row?.Versions is { } vr) try { _store.SaveFleet(FleetSnapshot.From(stored, vr, Who)); } catch { }
            }
            ApplyProgress(p);
            Notify();
        });
        var from = Results.Count;
        WebPrompts.Channel.Value = Prompts;
        try
        {
            var result = await _runner.RunAsync(connect, actions, apply, progress, _runCts!.Token, args);
            if (result.History.Count > 0) _store.AppendHistory(result.History, Who);
            var failN = PlanRunner.Count(Results, "FAIL");
            var okN = PlanRunner.Count(Results, "OK");
            var mine = Results.Skip(Math.Min(from, Results.Count)).ToList();
            _audit.Write(Who, $"{mode}: итог", $"{what}: {PlanRunner.Count(mine, "OK")} OK, {PlanRunner.Count(mine, "WARN")} WARN, {PlanRunner.Count(mine, "FAIL")} FAIL" + (result.Canceled ? ", отменено" : ""));
            if (!keepRunning)
                RunStatus = result.Canceled ? "отменено"
                    : failN == 0 ? (apply ? $"готово · {okN} OK" : $"dry-run · {okN} OK") : $"{failN} FAIL / {okN} OK";
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var row = PlanRunner.MakeRow("FAIL", "система", ex.Message, ex.ToString(), ok: false);
            Results.Insert(0, row);
            Selected = row;
            RunStatus = "error";
            return null;
        }
        finally
        {
            _locks.Release(got);
            if (!keepRunning) StopRunning();
            Notify();
        }
    }

    public void Cancel() => _runCts?.Cancel();

    public void ClearResults()
    {
        Results.Clear();
        Selected = null;
        RunStatus = "";
    }

    private void Warn(string text)
    {
        var row = PlanRunner.MakeRow("WARN", "система", text, text, ok: true);
        Results.Add(row);
        Selected = row;
        ShowNotice(text);
    }

    /// <summary>Сообщение поверх любой вкладки (не только Actions), гаснет через 7 с.</summary>
    public string? Notice { get; private set; }
    private int _noticeSeq;

    public void ShowNotice(string text)
    {
        Notice = text;
        var seq = ++_noticeSeq;
        Notify();
        _ = Task.Delay(7000).ContinueWith(_ => { if (seq == _noticeSeq) { Notice = null; Notify(); } });
    }

    public void HideNotice() { Notice = null; Notify(); }

    // ---------- ход запуска: строки RUN, живой вывод, время ----------

    private sealed class RunningRow
    {
        public required ResultRow Row { get; init; }
        public required string What { get; init; }
        public DateTime Started { get; } = DateTime.Now;
        public StringBuilder Output { get; } = new();
    }

    private readonly Dictionary<string, RunningRow> _running = new();
    private DateTime _runStarted;
    private string _statusBase = "";
    private bool _autoFollow;

    private void StartRunning(string status)
    {
        RunStatus = status;
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        _runStarted = DateTime.Now;
        _running.Clear();
        _autoFollow = true;
        _statusBase = status;
        _tick?.Dispose();
        _tick = new Timer(_ => Dispatch?.Invoke(OnTick), null, 500, 500);
    }

    private void StopRunning()
    {
        _tick?.Dispose();
        _tick = null;
        foreach (var run in _running.Values.ToList())
        {
            var i = Results.IndexOf(run.Row);
            var stopped = PlanRunner.MakeRow("WARN", run.Row.Title, "прервано через " + Elapsed(run.Started), LiveTail(run) ?? "", ok: true);
            if (i >= 0) Results[i] = stopped;
            if (ReferenceEquals(Selected, run.Row)) Selected = stopped;
        }
        _running.Clear();
        if (!string.IsNullOrEmpty(RunStatus)) RunStatus += " · за " + Elapsed(_runStarted);
        IsRunning = false;
        _runCts?.Dispose();
        _runCts = null;
    }

    private void OnTick()
    {
        if (!IsRunning) return;
        RunStatus = string.IsNullOrEmpty(_statusBase) ? Elapsed(_runStarted) : $"{_statusBase} · {Elapsed(_runStarted)}";
        foreach (var run in _running.Values)
        {
            var last = LastLine(run);
            run.Row.Preview = $"{run.What}… {Elapsed(run.Started)}" + (last is null ? "" : " · " + last);
        }
        Notify();
    }

    private void ApplyProgress(PlanProgress p)
    {
        if (p.Live is not null && p.LiveKey is not null && _running.TryGetValue(p.LiveKey, out var live))
        {
            lock (live.Output)
            {
                live.Output.Append(p.Live);
                if (live.Output.Length > 400_000) live.Output.Remove(0, live.Output.Length - 300_000);
            }
        }

        if (p.Row?.Audit is { } audit) _audits[audit.Host] = audit;
        if (p.Row?.PackageUpdate is { } upd) _lastUpdate = upd;

        if (p.Row is { Status: "RUN" } started)
        {
            _running[started.Title] = new RunningRow { Row = started, What = started.Preview.TrimEnd('…') };
            Results.Add(started);
            if (_autoFollow || Selected is null) Selected = started;
        }
        else if (p.Row is not null && _running.Remove(p.Row.Title, out var done))
        {
            var wasSelected = ReferenceEquals(Selected, done.Row);
            var i = Results.IndexOf(done.Row);
            if (i >= 0) Results[i] = p.Row;
            else Results.Add(p.Row);
            if (wasSelected) Selected = p.Row;
        }
        else if (p.Row is not null)
        {
            Results.Add(p.Row);
            Selected ??= p.Row;
        }

        if (p.Status is not null)
        {
            _statusBase = p.Status;
            RunStatus = IsRunning ? $"{p.Status} · {Elapsed(_runStarted)}" : p.Status;
        }
    }

    /// <summary>Щелчок по строке пользователем — больше не прыгать за новыми строками RUN.</summary>
    public void UserSelect(ResultRow row)
    {
        Select(row);
        if (IsRunning) _autoFollow = false;
    }

    private static string Elapsed(DateTime from)
    {
        var t = DateTime.Now - from;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string Clean(StringBuilder sb)
    {
        string text;
        lock (sb) text = sb.ToString();
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string? LiveTail(RunningRow run, int maxLines = 300)
    {
        var lines = Clean(run.Output).Split('\n');
        if (lines.All(l => l.Trim().Length == 0)) return null;
        var tail = lines.Length > maxLines ? lines[^maxLines..] : lines;
        var head = lines.Length > maxLines ? $"… выше ещё {lines.Length - maxLines} строк\n" : "";
        return $"{run.Row.Title}\n{run.What}, идёт {Elapsed(run.Started)}\n\n" + head + string.Join('\n', tail).TrimEnd();
    }

    private static string? LastLine(RunningRow run)
    {
        var line = Clean(run.Output).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        if (line is null) return null;
        return line.Length > 90 ? line[..87] + "…" : line;
    }

    // ---------- опрос ----------

    public async Task Probe()
    {
        if (!Allowed(Auth.Perm.RunCheck, "Опросить")) return;
        var targets = SelectedHosts.ToList();
        if (targets.Count == 0) targets = LinuxHosts.ToList();
        targets = targets.Where(h => CanOn(Auth.Perm.RunCheck, h)).ToList();
        if (targets.Count == 0) return;
        WebPrompts.Channel.Value = Prompts;
        var connect = targets.Select(h => (h, c: _store.ForConnect(h))).ToList();
        var results = await Task.Run(() => connect.Select(x =>
        {
            try
            {
                using var ssh = SshRunner.Connect(x.c);
                return (x.h, ok: true, hint: ssh.DetectDistro().Id);
            }
            catch
            {
                return (x.h, ok: false, hint: x.h.Config.DistroHint);
            }
        }).ToList());
        _audit.Write(Who, "опрос", string.Join(", ", results.Select(r => $"{r.h.Config.Title} — {(r.ok ? "online" : "offline")}")));
        foreach (var (h, ok, hint) in results)
        {
            h.Config.Status = ok ? "Online" : "Offline";
            if (ok && !string.IsNullOrWhiteSpace(hint) && h.Config.DistroHint != hint)
            {
                h.Config.DistroHint = hint;
                _store.SaveHost(h);
            }
        }
        Notify();
    }

    // ---------- «Добавить пользователя» ----------

    public async Task AddUser(AddUserRequest request, List<StoredHost> hosts)
    {
        if (!Allowed(Auth.Perm.RunApply, "Добавить пользователя")) return;
        if (!AllHostsAllowed(Auth.Perm.RunApply, hosts, "Добавить пользователя")) return;
        Results.Clear();
        Selected = null;
        StartRunning("пользователь…");
        _audit.Write(Who, "add-user: " + request.Login, $"sudo={(request.Sudo ? "да" : "нет")}, ключ={(request.Keys.Length > 0 ? request.Fingerprint : "нет")}, " +
                                                        $"пароль={(request.Password.Length > 0 ? "задан" : "нет")}; хосты: " + string.Join(", ", hosts.Select(h => h.Config.Title)));
        var progress = new Progress<PlanProgress>(p => { ApplyProgress(p); Notify(); });
        WebPrompts.Channel.Value = Prompts;
        var keys = hosts.Select(h => HostLocks.Key(h.Config.Host, h.Config.Port)).ToList();
        var got = _locks.TryLock(keys, Who, "Добавить пользователя " + request.Login, out var busy);
        try
        {
            foreach (var (key, by) in busy)
                Results.Add(PlanRunner.MakeRow("WARN", key + " · занят", $"занят: {by.Who}, {by.What}, с {by.Since:HH:mm:ss}", "", ok: true));
            var free = hosts.Where(h => got.Contains(HostLocks.Key(h.Config.Host, h.Config.Port), StringComparer.OrdinalIgnoreCase))
                .Select(_store.ForConnect).ToList();
            if (free.Count > 0) await UserProvisioning.ApplyAsync(free, request, progress, _runCts!.Token);
            _store.AppendHistory(Results.Select(row => new HistoryEntry
            {
                AtUtc = DateTime.UtcNow, Mode = "add-user", Host = row.Title, Action = request.Login,
                Status = row.Status, Preview = row.Preview, Detail = row.Detail
            }).ToList(), Who);
            var failN = PlanRunner.Count(Results, "FAIL");
            RunStatus = failN == 0 ? "пользователь готов" : $"{failN} FAIL";
            _audit.Write(Who, "add-user: итог", $"{request.Login}: {PlanRunner.Count(Results, "OK")} OK, {failN} FAIL");
        }
        catch (OperationCanceledException)
        {
            RunStatus = "отменено";
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally
        {
            _locks.Release(got);
            StopRunning();
            Notify();
        }
    }

    // ---------- «Версии ПО»: обновить отмеченное ----------

    public VersionReport? DetailVersions => ShowRawDetail ? null : Selected?.Versions;
    public UserAuditReport? DetailAudit => ShowRawDetail ? null : Selected?.Audit;
    public ProxyReport? DetailProxy => ShowRawDetail ? null : Selected?.Proxy;
    public bool HasTable => Selected?.Versions is not null || Selected?.Audit is not null || Selected?.Proxy is not null;

    public bool UpdateViaPc { get; set; }

    public void PickSecurity()
    {
        if (Selected?.Versions is not { } r) return;
        var items = r.Items.Where(i => i.CanUpdate).ToList();
        static bool Sec(VersionItem i) => i.SecurityUpdate || i.CveLevel == "now";
        var sec = items.Where(Sec).ToList();
        foreach (var i in items) i.IsChecked = sec.Count > 0 ? Sec(i) : true;
    }

    public int CheckedCount => Selected?.Versions?.Items.Count(i => i.CanUpdate && i.IsChecked) ?? 0;
    public string UpdateSelectedText => CheckedCount == 0 ? "Обновить отмеченное" : $"Обновить отмеченное ({CheckedCount})";

    public async Task UpdateSelected()
    {
        if (!Allowed(Auth.Perm.RunApply, "Обновить отмеченное")) return;
        if (Selected?.Versions is not { } report) return;
        var packages = report.Items.Where(i => i.CanUpdate && i.IsChecked).Select(i => i.Package).Distinct(StringComparer.Ordinal).ToList();
        if (packages.Count == 0) return;
        var host = Hosts.FirstOrDefault(h => string.Equals(h.Config.Title, report.Host, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(h.Config.Name, report.Host, StringComparison.OrdinalIgnoreCase));
        if (host is null) { Warn($"Хост «{report.Host}» не найден в списке — обновите проверку версий."); return; }
        if (!AllowedOn(Auth.Perm.RunApply, host, "Обновить отмеченное")) return;

        var viaPc = UpdateViaPc;
        var origin = Selected;
        var def = PackageUpdate.Definition(packages, viaPc);
        StartRunning("обновление: предпросмотр…");
        try
        {
            // 1. Предпросмотр — ничего не ставит.
            _lastUpdate = null;
            var pre = await Execute("обновление: предпросмотр…", new() { host }, new() { def }, false, null, "update-preview", keepRunning: true);
            if (pre is null || pre.Canceled) { RunStatus = "отменено"; return; }
            if (_lastUpdate is not { Preview: true } preview || preview.Error.Length > 0) { RunStatus = "обновление: предпросмотр не удался"; return; }
            if (preview.NothingToDo || preview.Changes.Count == 0)
            {
                RunStatus = "обновление: нечего ставить";
                Select(origin);
                Notify();
                await Dialogs.Info($"На {host.Config.Title} обновлять нечего: {string.Join(", ", packages)} уже свежие.");
                return;
            }

            // 2. Подтверждение со списком.
            var lines = preview.Changes.Take(25).Select(c => $"  {c.Name} {c.Version}" + (c.SectionText == "обновление" ? "" : $" — {c.SectionText}"));
            var more = preview.Changes.Count > 25 ? $"\n  … и ещё {preview.Changes.Count - 25}" : "";
            var removal = preview.RemovalCount > 0 ? $"\n\nВнимание: будет удалено пакетов: {preview.RemovalCount}." : "";
            var tunnelNote = viaPc ? "\n\nПакеты скачиваются через сервер (туннель SSH)." : "";
            var msg = $"Установить на {host.Config.Title} ({preview.Changes.Count(c => !c.IsRemoval)} пакетов)?\n\n{string.Join("\n", lines)}{more}{removal}{tunnelNote}\n\n" +
                      "Службы сами не перезапускаются — после установки программа покажет, каким нужен перезапуск.";
            Notify();
            if (!await Dialogs.Confirm(msg, "Обновить отмеченное")) { RunStatus = "обновление: отменено"; Select(origin); return; }

            // 3. Установка.
            RunStatus = "обновление: установка…";
            _statusBase = RunStatus;
            _lastUpdate = null;
            var run = await Execute("обновление: установка…", new() { host }, new() { def }, true, null, "update-apply", keepRunning: true);
            if (run is null || run.Canceled) { RunStatus = "отменено"; return; }
            var done = _lastUpdate;

            // 4. Перезапуск — только того, что отметят в окне.
            if (done is { Preview: false, RestartServices.Count: > 0 })
            {
                var pick = new ActionDefinition
                {
                    Id = "restart-pick",
                    Name = $"Перезапустить службы на {host.Config.Title} — у них в памяти старые файлы",
                    Parameters = done.RestartServices
                        .OrderBy(u => PackageUpdate.IsProductService(u) ? 0 : 1).ThenBy(u => u, StringComparer.Ordinal)
                        .Select(u => new ActionParam
                        {
                            Name = u, Kind = "bool", Label = PackageUpdateReport.ShortUnit(u),
                            Default = PackageUpdate.IsProductService(u) ? "да" : "нет",
                            Hint = u is "sshd.service" or "ssh.service" ? "Сначала проверка sshd -t; открытые сеансы SSH не рвутся."
                                : PackageUpdate.IsProductService(u) ? "Короткий перерыв в работе службы." : "-"
                        }).ToList()
                };
                var firstSystem = pick.Parameters.FirstOrDefault(p => p.Hint == "-");
                if (firstSystem is not null) firstSystem.Group = "Системные службы — перезапускайте, если понимаете последствия (или перезагрузите хост в окно обслуживания):";
                Notify();
                var answer = await Dialogs.Params(new[] { pick }, "Перезапуск служб",
                    "Отмеченные будут перезапущены (systemctl restart). Остальные продолжат работать со старыми файлами до своего перезапуска или перезагрузки.");
                if (answer is not null)
                {
                    var units = done.RestartServices.Where(u => answer.TryGetValue("restart-pick:" + u, out var v) && ActionParam.IsYes(v)).ToList();
                    if (units.Count > 0)
                    {
                        RunStatus = "обновление: перезапуск служб…";
                        _statusBase = RunStatus;
                        var rs = await Execute(RunStatus, new() { host }, new() { PackageUpdate.RestartDefinition(units) }, true, null, "restart-services", keepRunning: true);
                        if (rs is null || rs.Canceled) { RunStatus = "отменено"; return; }
                    }
                }
            }

            // 5. Повторная проверка версий по этому хосту.
            if (ActionCatalog.LoadAll().FirstOrDefault(a => a.Id == VersionsCheck.ActionId) is { } check)
            {
                RunStatus = "обновление: повторная проверка…";
                _statusBase = RunStatus;
                var args = new Dictionary<string, string> { [VersionsCheck.ActionId + ":" + VersionsCheck.ViaPcParam] = viaPc ? "да" : "нет" };
                await Execute(RunStatus, new() { host }, new() { check }, false, args, "dry-run", keepRunning: true);
                if (Results.LastOrDefault(r => r.Versions is not null) is { } fresh) Select(fresh);
            }

            RunStatus = done is null ? "обновление: нет итога"
                : done.ExitCode == 0 && done.Error.Length == 0 ? $"обновление: готово · {done.Summary}" : $"обновление: ошибка · {done.Summary}";
        }
        catch (OperationCanceledException)
        {
            RunStatus = "отменено";
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally
        {
            StopRunning();
            Notify();
        }
    }

    // ---------- хосты и папки (Inventory) ----------

    public void SaveHost(StoredHost h, string? password, string? key, string? keyName)
    {
        if (!Allowed(Auth.Perm.HostsEdit, "хост: сохранить")) return;
        var isNew = !Hosts.Contains(h);
        // новый — в папку, где есть право «Inventory»; существующий — право на сам хост
        if (isNew ? !AllowedOnFolder(Auth.Perm.HostsEdit, h.Config.FolderId, "хост: добавить в эту папку") : !AllowedOn(Auth.Perm.HostsEdit, h, "хост: изменить")) return;
        _store.SaveHost(h, password, key, keyName);
        if (isNew) Hosts.Add(h);
        _audit.Write(Who, isNew ? "хост: добавлен" : "хост: изменён",
            (h.IsWindows ? $"{h.Config.Title} (Windows, RDP {h.Config.Host}:{h.Config.Port})" : $"{h.Config.Title} ({h.Config.Username}@{h.Config.Host}:{h.Config.Port})") +
            (password is not null ? (password.Length > 0 ? ", пароль задан" : ", пароль убран") : "") +
            (key is not null ? (key.Length > 0 ? $", ключ загружен ({keyName})" : ", ключ убран") : ""));
        Notify();
    }

    /// <summary>«Проверить подключение» в окне «Хост»: поля окна + секреты из хранилища, если их не меняли.</summary>
    public async Task<(bool Ok, string Text)> TestConnect(StoredHost existing, HostConfig cfg, string? password, string? key)
    {
        if (!Can(Auth.Perm.HostsEdit)) return (false, "Нет права менять хосты.");
        var stored = Hosts.Contains(existing) ? _store.ForConnect(existing) : null;
        var test = new HostConfig
        {
            Name = cfg.Name, Host = cfg.Host, Port = cfg.Port, Username = cfg.Username, Auth = cfg.Auth, UseSudo = cfg.UseSudo,
            Password = password ?? stored?.Password ?? "",
            PrivateKeyText = key ?? stored?.PrivateKeyText ?? ""
        };
        WebPrompts.Channel.Value = Prompts;
        try
        {
            var d = await Task.Run(() =>
            {
                using var ssh = SshRunner.Connect(test);
                return ssh.DetectDistro();
            });
            _audit.Write(Who, "хост: проверка подключения", $"{test.Username}@{test.Host}:{test.Port} — OK, {d.Id}");
            return (true, $"Подключено. {d.PrettyName} ({d.Id})");
        }
        catch (Exception ex)
        {
            _audit.Write(Who, "хост: проверка подключения", $"{test.Username}@{test.Host}:{test.Port} — {ex.Message}");
            return (false, ex.Message);
        }
    }

    public void DeleteHost(StoredHost h)
    {
        if (!AllowedOn(Auth.Perm.HostsEdit, h, "хост: удалить")) return;
        _store.DeleteHost(h.Id);
        _store.DeleteFleet(h.Id);
        _store.DeleteAssignmentsOn(h.Id);
        Hosts.Remove(h);
        _audit.Write(Who, "хост: удалён", $"{h.Config.Title} ({h.Config.Host})");
        Notify();
    }

    public void SaveFolder(HostFolder f)
    {
        var isNew = !_allFolders.Any(x => x.Id == f.Id);
        // новая — внутри папки, где есть право «Inventory» (в корне — только с правом на «всё»); переименовать — право на саму папку
        if (isNew ? !AllowedOnFolder(Auth.Perm.HostsEdit, f.ParentId, "папка: создать здесь") : !AllowedOnFolder(Auth.Perm.HostsEdit, f.Id, "папка: изменить «" + f.Name + "»")) return;
        _store.SaveFolder(f);
        if (isNew) { Folders.Add(f); _allFolders.Add(f); }
        _audit.Write(Who, isNew ? "папка: создана" : "папка: изменена", FolderPath(f.Id));
        Notify();
    }

    public void DeleteFolder(HostFolder f)
    {
        // содержимое переезжает в родителя — нужно право «Inventory» на родителя (в корне — на «всё»)
        if (!AllowedOnFolder(Auth.Perm.HostsEdit, f.ParentId, "папка: удалить «" + f.Name + "»")) return;
        var path = FolderPath(f.Id);
        // все хосты и подпапки из базы, не только видимые этому пользователю
        foreach (var h in _store.LoadHosts().Where(h => h.Config.FolderId == f.Id)) { h.Config.FolderId = f.ParentId; _store.SaveHost(h); }
        foreach (var h in Hosts.Where(h => h.Config.FolderId == f.Id)) h.Config.FolderId = f.ParentId;
        foreach (var sub in _allFolders.Where(x => x.ParentId == f.Id)) { sub.ParentId = f.ParentId; _store.SaveFolder(sub); }
        _store.DeleteFolder(f.Id);
        _store.DeleteAssignmentsOn(f.Id);
        Folders.Remove(f);
        _allFolders.RemoveAll(x => x.Id == f.Id);
        _audit.Write(Who, "папка: удалена", path + " (содержимое — уровнем выше)");
        Notify();
    }

    public void ToggleFolder(HostFolder f)
    {
        f.IsExpanded = !f.IsExpanded;
        _store.SaveFolder(f);
    }

    public void Dispose()
    {
        _tick?.Dispose();
        _runCts?.Cancel();
    }
}
