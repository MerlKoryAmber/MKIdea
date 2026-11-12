using System.Text;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

public partial class ActionsViewModel : ObservableObject
{
    private readonly AppSession _session;
    private readonly PlanRunner _runner = new();
    private readonly PsActionRunner _ps = new();
    private CancellationTokenSource? _runCts;
    private bool _bulkHosts;

    public ActionsViewModel(AppSession session)
    {
        _session = session;
        Hosts.CollectionChanged += HostsChanged;
        VCenters.CollectionChanged += HostsChanged;
        foreach (var h in _session.Items)
            h.PropertyChanged += HostChanged;
        UpdateHostSelection();
        RefreshBaseUsers();
    }

    [ObservableProperty] private string baseUsersText = "Базовые пользователи (0)";
    private readonly Dictionary<string, UserAuditReport> _audits = new(StringComparer.OrdinalIgnoreCase);

    public void RefreshBaseUsers()
        => BaseUsersText = "Базовые пользователи (" + UserListStore.Load().Count + ")";

    public AppSession Session => _session;
    public ObservableCollection<HostConfig> Hosts => _session.Hosts;
    public ObservableCollection<VCenterProfile> VCenters => _session.VCenters;
    public ObservableCollection<ActionItemView> Actions { get; } = new();
    public ObservableCollection<ResultRow> Results { get; } = new();

    public Func<Window?>? Owner { get; set; }
    public Func<IReadOnlyList<ActionDefinition>>? ReadActions { get; set; }
    public event Action? ActionsReloaded;

    [ObservableProperty] private string hostsSelectedText = "ничего не выбрано";
    [ObservableProperty] private bool? selectAllChecked;
    [ObservableProperty] private string runStatus = "";
    [ObservableProperty] private string detail = "Выберите строку плана.";
    [ObservableProperty] private ResultRow? selectedResult;
    /// <summary>Аудит пользователей: таблица (по умолчанию) или текст.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowAuditTable))] [NotifyPropertyChangedFor(nameof(ShowProxyTable))] [NotifyPropertyChangedFor(nameof(DetailViewText))] private bool showRawDetail;
    public LinuxOps.Services.UserAuditReport? DetailAudit => SelectedResult?.Audit;
    public LinuxOps.Services.ProxyReport? DetailProxy => SelectedResult?.Proxy;
    /// <summary>У строки есть табличный вид (аудит или прокси) — показываем переключатель «Таблица / Текст».</summary>
    public bool HasDetailAudit => DetailAudit is not null || DetailProxy is not null;
    public bool ShowAuditTable => DetailAudit is not null && !ShowRawDetail;
    public bool ShowProxyTable => DetailProxy is not null && !ShowRawDetail;
    public string DetailViewText => ShowRawDetail ? "Таблица" : "Текст";
    [ObservableProperty] private bool isRunning;
    /// <summary>Что запустят Dry-run и «Применить» — строка внизу окна.</summary>
    [ObservableProperty] private string planSummary = "";

    public bool SuspendSelection { get; private set; }

    public void Load()
    {
        var saved = DefaultsStore.Load().SelectedActionIds;
        var prev = Actions.Count > 0
            ? Actions.Where(a => a.IsSelected).Select(a => a.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : saved.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SuspendSelection = true;
        try
        {
            Actions.Clear();
            foreach (var def in ActionCatalog.LoadAll())
                Actions.Add(ActionItemView.From(def, prev.Contains(def.Id)));
        }
        finally
        {
            SuspendSelection = false;
        }

        ActionsReloaded?.Invoke();
    }

    public void PersistPlan()
        => DefaultsStore.SaveSelectedActions(Actions.Where(a => a.IsSelected).Select(a => a.Definition.Id));

    public void RefreshPreview()
    {
        UpdatePlanSummary();
        if (IsRunning) return;
        Results.Clear();
        SelectedResult = null;
        var all = Actions.Where(a => a.IsSelected).Select(a => a.Definition).ToList();
        var planned = all.Where(a => !a.IsPowerShell).ToList();
        var psPlanned = all.Where(a => a.IsPowerShell).ToList();
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        var vcs = VCenters.Where(v => v.IsSelected).ToList();
        if (all.Count == 0)
        {
            Results.Add(PlanRunner.MakeRow("INFO", "План пуст", "Отметьте хосты или vCenter слева и действия в каталоге.", "", true));
            SelectedResult = Results.FirstOrDefault();
            return;
        }

        if (planned.Count > 0 && hosts.Count == 0)
            Results.Add(PlanRunner.MakeRow("INFO", "Bash-действия", "отметьте хосты Linux", string.Join(", ", planned.Select(a => a.Name)), true));
        if (psPlanned.Count > 0 && vcs.Count == 0)
            Results.Add(PlanRunner.MakeRow("INFO", "PowerShell-действия", "отметьте vCenter", string.Join(", ", psPlanned.Select(a => a.Name)), true));
        foreach (var vc in vcs)
        foreach (var ps in psPlanned)
            Results.Add(PlanRunner.MakeRow("INFO", $"{vc.Title} · {ps.Name}", "войдёт в запуск",
                $"vCenter {vc.Server}. {ps.Description}", true));
        if (planned.Count == 0) hosts.Clear();

        foreach (var host in hosts)
        {
            var distro = GuessDistro(host.DistroHint);
            foreach (var action in planned)
            {
                if (!ActionCatalog.Supports(action, distro))
                {
                    Results.Add(PlanRunner.MakeRow(
                        "SKIP",
                        $"{host.Title} · {action.Name}",
                        $"не для {distro.Id} / {distro.Family}",
                        $"Действие {action.Id} не совпало с id={distro.Id}, family={distro.Family}, like={distro.Like}.",
                        true));
                }
                else
                {
                    Results.Add(PlanRunner.MakeRow(
                        "INFO",
                        $"{host.Title} · {action.Name}",
                        "войдёт в запуск",
                        $"{action.Privilege}. Дистрибутив по подсказке: {distro.Id} ({distro.Family}).",
                        true));
                }
            }
        }

        SelectedResult = Results.FirstOrDefault();
    }

    private void UpdatePlanSummary()
    {
        var defs = Actions.Where(a => a.IsSelected).Select(a => a.Definition).ToList();
        if (defs.Count == 0)
        {
            PlanSummary = "План пуст: щёлкните действие в каталоге";
            return;
        }

        var targets = new List<string>();
        var missing = new List<string>();
        if (defs.Any(a => !a.IsPowerShell))
        {
            var n = Hosts.Count(h => h.IsSelected);
            if (n == 0) missing.Add("хосты");
            else targets.Add(n + " " + Plural(n, "хост", "хоста", "хостов"));
        }

        if (defs.Any(a => a.IsPowerShell))
        {
            var n = VCenters.Count(v => v.IsSelected);
            if (n == 0) missing.Add("vCenter");
            else targets.Add(n + " vCenter");
        }

        PlanSummary = "План: " + string.Join(", ", defs.Select(a => a.Name)) +
                      (targets.Count > 0 ? " → " + string.Join(" и ", targets) : "") +
                      (missing.Count > 0 ? " — отметьте " + string.Join(" и ", missing) : "");
    }

    private static string Plural(int n, string one, string few, string many)
        => (n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many;

    private static DistroInfo GuessDistro(string? hint)
    {
        var id = string.IsNullOrWhiteSpace(hint) ? "unknown" : hint.Trim();
        var blob = id.ToLowerInvariant();
        var family = "unknown";
        if (blob.Contains("rhel") || blob.Contains("centos") || blob.Contains("fedora") ||
            blob.Contains("rocky") || blob.Contains("alma") || blob.Contains("oracle") || blob == "ol")
            family = "rhel";
        else if (blob.Contains("debian") || blob.Contains("ubuntu"))
            family = "debian";
        else if (blob.Contains("suse") || blob.Contains("sles"))
            family = "suse";
        return new DistroInfo { Id = id, Like = id, PrettyName = id, Family = family };
    }

    partial void OnSelectedResultChanged(ResultRow? value)
    {
        OnPropertyChanged(nameof(DetailAudit));
        OnPropertyChanged(nameof(DetailProxy));
        OnPropertyChanged(nameof(HasDetailAudit));
        OnPropertyChanged(nameof(ShowAuditTable));
        OnPropertyChanged(nameof(ShowProxyTable));
        // Пользователь сам выбрал строку во время запуска — не перескакиваем на новые RUN.
        if (!_programmatic && IsRunning) _autoFollow = false;
        if (value is { Status: "RUN" } && _running.TryGetValue(value.Title, out var run))
        {
            Detail = LiveTail(run) ?? "Ждём вывод с сервера…";
            return;
        }

        Detail = value is null
            ? "Выберите строку плана."
            : string.IsNullOrWhiteSpace(value.Detail) ? value.Preview : value.Detail;
    }

    // ---------- Ход запуска: строки RUN, живой вывод, время ----------

    private sealed class RunningRow
    {
        public required ResultRow Row { get; init; }
        public required string What { get; init; }
        public DateTime Started { get; } = DateTime.Now;
        public StringBuilder Output { get; } = new();
    }

    private readonly Dictionary<string, RunningRow> _running = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime _runStarted;
    private string _statusBase = "";
    private bool _liveDirty;
    private bool _autoFollow;
    private bool _programmatic;

    partial void OnIsRunningChanged(bool value)
    {
        NotifyRunCommands();
        if (value)
        {
            _runStarted = DateTime.Now;
            _running.Clear();
            _autoFollow = true;
            _statusBase = RunStatus;
            _tick.Tick -= OnTick;
            _tick.Tick += OnTick;
            _tick.Start();
            return;
        }

        _tick.Stop();
        // Отмена или обрыв: незавершённые RUN не должны висеть «выполняется».
        foreach (var run in _running.Values.ToList())
        {
            var i = Results.IndexOf(run.Row);
            var stopped = PlanRunner.MakeRow("WARN", run.Row.Title, "прервано через " + Elapsed(run.Started),
                LiveTail(run) ?? "", ok: true);
            var wasSelected = ReferenceEquals(SelectedResult, run.Row);
            _programmatic = true;
            try
            {
                if (i >= 0) Results[i] = stopped;
            }
            finally
            {
                _programmatic = false;
            }

            if (wasSelected) Select(stopped);
        }

        _running.Clear();
        if (!string.IsNullOrEmpty(RunStatus)) RunStatus += " · за " + Elapsed(_runStarted);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        RunStatus = string.IsNullOrEmpty(_statusBase) ? Elapsed(_runStarted) : $"{_statusBase} · {Elapsed(_runStarted)}";
        foreach (var run in _running.Values)
        {
            var last = LastLine(run);
            run.Row.Preview = $"{run.What}… {Elapsed(run.Started)}" + (last is null ? "" : " · " + last);
        }

        if (_liveDirty && SelectedResult is { Status: "RUN" } sel && _running.TryGetValue(sel.Title, out var shown))
            Detail = LiveTail(shown) ?? "Ждём вывод с сервера…";
        _liveDirty = false;
    }

    private void Select(ResultRow? row)
    {
        _programmatic = true;
        try { SelectedResult = row; }
        finally { _programmatic = false; }
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

    private void NotifyRunCommands()
    {
        DryRunCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        AddUserCommand.NotifyCanExecuteChanged();
        OpenBaseUsersCommand.NotifyCanExecuteChanged();
        CloseAccessCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void SetAllHosts(bool all)
    {
        _bulkHosts = true;
        foreach (var h in _session.Items)
            h.IsSelected = all;
        _bulkHosts = false;
        UpdateHostSelection();
    }

    [RelayCommand]
    private void Add()
    {
        var dlg = new ActionEditWindow { Owner = OwnerWin() };
        if (dlg.ShowDialog() != true) return;
        ActionCatalog.SaveCustom(dlg.Result);
        Load();
    }

    // Правка, клон и удаление — с самой карточки (значки и контекстное меню). Выделения карточек больше нет:
    // рамка означает только «в плане».
    public void EditFromCard(ActionItemView? item) => EditCore(item, clone: false);

    public void CloneFromCard(ActionItemView? item) => EditCore(item, clone: true);

    public void RemoveFromCard(ActionItemView? item)
    {
        if (item is null)
            return;
        if (item.Definition.BuiltIn)
        {
            AppDialog.Info(OwnerWin(), "Встроенные нельзя удалить. Склонируйте и правьте копию.");
            return;
        }

        if (!AppDialog.Confirm(OwnerWin(), $"Удалить «{item.Definition.Name}»?"))
            return;
        ActionCatalog.DeleteCustom(item.Definition.Id);
        Load();
    }

    [RelayCommand]
    private async Task Probe()
    {
        var targets = Hosts.Where(h => h.IsSelected).ToList();
        var vcs = VCenters.Where(v => v.IsSelected).ToList();
        if (targets.Count == 0 && vcs.Count == 0)
        {
            targets = Hosts.ToList();
            vcs = VCenters.ToList();
        }

        if (targets.Count == 0 && vcs.Count == 0) return;
        // vCenter — только доступность HTTPS (443): вход с паролем здесь не нужен.
        var vcProbe = Task.WhenAll(vcs.Select(async v =>
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await tcp.ConnectAsync(v.Server.Trim(), 443, cts.Token);
                v.Status = "Online";
            }
            catch
            {
                v.Status = "Offline";
            }
        }));

        var window = OwnerWin();
        window.Cursor = Cursors.Wait;
        try
        {
            var results = await Task.Run(() =>
            {
                var list = new List<(HostConfig Host, bool Ok, string Hint)>();
                foreach (var host in targets)
                {
                    try
                    {
                        using var ssh = SshRunner.Connect(host);
                        var distro = ssh.DetectDistro();
                        list.Add((host, true, distro.Id));
                    }
                    catch
                    {
                        list.Add((host, false, host.DistroHint));
                    }
                }

                return list;
            });

            foreach (var (host, ok, hint) in results)
            {
                host.Status = ok ? "Online" : "Offline";
                if (ok && !string.IsNullOrWhiteSpace(hint))
                    host.DistroHint = hint;
            }

            await vcProbe;
            _session.Persist();
        }
        catch (Exception ex)
        {
            AppDialog.Info(OwnerWin(), "Опрос хостов: " + ex.Message);
        }
        finally
        {
            window.Cursor = Cursors.Arrow;
        }
    }

    public void EditHost(IInventoryItem? host)
    {
        host ??= _session.Items.FirstOrDefault(h => h.IsSelected) ?? _session.Items.FirstOrDefault();
        if (host is null)
        {
            AppDialog.Info(OwnerWin(), "Нет хоста для правки.");
            return;
        }

        HostDialogs.TryEdit(OwnerWin(), _session, host);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task DryRun() => RunAsync(apply: false);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Apply() => RunAsync(apply: true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddUser()
    {
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        if (hosts.Count == 0)
        {
            Warn("Отметьте хост.");
            return;
        }

        var dlg = new AddUserWindow(hosts) { Owner = OwnerWin() };
        if (dlg.ShowDialog() != true || dlg.Request is null) return;

        Results.Clear();
        SelectedResult = null;
        RunStatus = "пользователь…";
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<PlanProgress>(ApplyProgress);
        var history = new List<HistoryEntry>();
        try
        {
            await UserProvisioning.ApplyAsync(hosts, dlg.Request, progress, _runCts.Token);
            foreach (var row in Results)
            {
                history.Add(new HistoryEntry
                {
                    AtUtc = DateTime.UtcNow,
                    Mode = "add-user",
                    Host = row.Title,
                    Action = dlg.Request.Login,
                    Status = row.Status,
                    Preview = row.Preview,
                    Detail = row.Detail
                });
            }

            if (history.Count > 0)
                HistoryStore.Append(history);
            var failN = PlanRunner.Count(Results, "FAIL");
            RunStatus = failN == 0 ? "пользователь готов" : $"{failN} FAIL";
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
            IsRunning = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    /// <summary>«Закрыть доступ»: блокировка, отзыв ключа или удаление; в том же окне — «Открыть доступ».</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CloseAccess()
    {
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        if (hosts.Count == 0)
        {
            Warn("Отметьте хост.");
            return;
        }

        var baseUsers = UserListStore.Load();
        var audits = hosts.Select(h => _audits.GetValueOrDefault(h.Name)).OfType<UserAuditReport>().ToList();
        var dlg = new AccessWindow(hosts, baseUsers.Select(u => u.Login), audits) { Owner = OwnerWin() };
        if (dlg.ShowDialog() != true || dlg.Request is not { } request) return;
        var targets = dlg.Targets.ToList();

        Results.Clear();
        SelectedResult = null;
        RunStatus = UserAccess.ModeTitle(request.Mode).ToLowerInvariant() + "…";
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<PlanProgress>(ApplyProgress);
        try
        {
            await UserAccess.ApplyAsync(targets, request, progress, _runCts.Token);
            HistoryStore.Append(Results.Select(row => new HistoryEntry
            {
                AtUtc = DateTime.UtcNow,
                Mode = "user-access",
                Host = row.Title,
                Action = UserAccess.ModeTitle(request.Mode) + " " + request.Login,
                Status = row.Status,
                Preview = row.Preview,
                Detail = row.Detail
            }).ToList());

            var failN = PlanRunner.Count(Results, "FAIL");
            var note = failN < Results.Count ? SyncBaseUser(baseUsers, request) : "";
            RunStatus = (failN == 0 ? UserAccess.ModeTitle(request.Mode) + ": готово" : $"{failN} FAIL") + note;
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
            IsRunning = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    /// <summary>
    /// Чтобы закрытый доступ не вернулся при следующем «Применить» базовых пользователей:
    /// блокировка и удаление выключают запись, отзыв ключа убирает ключ из неё.
    /// </summary>
    private string SyncBaseUser(List<BaseUser> users, AccessRequest request)
    {
        var user = users.FirstOrDefault(u => string.Equals(u.Login, request.Login, StringComparison.Ordinal));
        if (user is null) return "";
        string note;
        switch (request.Mode)
        {
            case AccessMode.Lock or AccessMode.Delete when user.Enabled:
                user.Enabled = false;
                note = " · в базовых пользователях выключен";
                break;
            case AccessMode.RevokeKey:
                var blobs = request.KeyBlobs.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var lines = user.Keys.Replace("\r\n", "\n").Split('\n');
                var keep = lines.Where(l => !blobs.Any(b => l.Contains(b, StringComparison.Ordinal))).ToList();
                if (keep.Count == lines.Length) return "";
                user.Keys = string.Join("\n", keep).Trim();
                note = " · ключ убран и из базовых пользователей";
                break;
            default:
                return "";
        }

        UserListStore.Save(users);
        RefreshBaseUsers();
        return note;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task OpenBaseUsers()
    {
        var dlg = new UserListWindow(Hosts.ToList(), _audits.Values.ToList()) { Owner = OwnerWin() };
        var apply = dlg.ShowDialog() == true ? dlg.ToApply : null;
        RefreshBaseUsers();
        if (apply is not { Count: > 0 }) return;

        Results.Clear();
        SelectedResult = null;
        RunStatus = "базовые пользователи…";
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<PlanProgress>(ApplyProgress);
        var history = new List<HistoryEntry>();
        try
        {
            var hosts = Hosts.Where(h => h.IsSelected).ToList();
            foreach (var request in apply)
                await UserProvisioning.ApplyAsync(hosts, request, progress, _runCts.Token, pairTitle: true);

            foreach (var row in Results)
            {
                var login = row.Title.Contains('·') ? row.Title.Split('·').Last().Trim() : row.Title;
                history.Add(new HistoryEntry
                {
                    AtUtc = DateTime.UtcNow,
                    Mode = "add-user",
                    Host = row.Title,
                    Action = login,
                    Status = row.Status,
                    Preview = row.Preview,
                    Detail = row.Detail
                });
            }

            if (history.Count > 0)
                HistoryStore.Append(history);
            var failN = PlanRunner.Count(Results, "FAIL");
            RunStatus = failN == 0 ? "базовые пользователи готовы" : $"{failN} FAIL";
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
            IsRunning = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private void ToggleDetailView() => ShowRawDetail = !ShowRawDetail;

    [RelayCommand]
    private void CopyDetail()
    {
        if (!string.IsNullOrWhiteSpace(Detail))
            Clipboard.SetText(Detail);
    }

    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        SelectedResult = null;
        RunStatus = "";
    }

    private bool CanStart() => !IsRunning;
    private bool CanCancel() => IsRunning;

    private async Task RunAsync(bool apply)
    {
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        var planned = ReadActions?.Invoke() ?? Array.Empty<ActionDefinition>();
        // Bash — по SSH на отмеченных хостах; PowerShell — против vCenter, хосты им не нужны.
        var actions = planned.Where(a => !a.IsPowerShell).ToList();
        var psActions = planned.Where(a => a.IsPowerShell).ToList();
        if (planned.Count == 0)
        {
            Warn("Отметьте действие.");
            return;
        }

        if (actions.Count > 0 && hosts.Count == 0)
        {
            Warn("Отметьте хост Linux слева — для Bash-действий.");
            return;
        }

        var vcs = VCenters.Where(v => v.IsSelected).ToList();
        if (psActions.Count > 0 && vcs.Count == 0)
        {
            Warn("Отметьте vCenter слева — для PowerShell-действий. Добавить vCenter — вкладка Inventory.");
            return;
        }

        // Параметры (Bash и PowerShell) — одним окном, только если есть что спросить.
        Dictionary<string, string> psArgs = new();
        if (planned.Any(a => a.Parameters.Count > 0))
        {
            var dlg = new PsRunWindow(planned) { Owner = OwnerWin() };
            if (dlg.ShowDialog() != true) return;
            psArgs = dlg.Args;
        }

        if (psActions.Count > 0)
        {
            var psDestructive = psActions.Where(a => a.Destructive).ToList();
            if (apply && psDestructive.Count > 0 &&
                !AppDialog.Confirm(OwnerWin(), $"Применить {string.Join(", ", psDestructive.Select(a => a.Name))} на vCenter: {string.Join(", ", vcs.Select(v => v.Title))}? Продолжить?"))
                return;
        }

        if (apply && actions.Count > 0)
        {
            var destructive = actions.Where(a => a.Destructive).ToList();
            if (destructive.Count > 0)
            {
                var list = string.Join(", ", hosts.Select(h => string.IsNullOrWhiteSpace(h.Name) ? h.Host : h.Name));
                var msg = destructive.All(a => string.Equals(a.Id, "install-updates", StringComparison.OrdinalIgnoreCase))
                    ? $"Установить обновления на {hosts.Count} хостах ({list})? Продолжить?"
                    : $"Применить {string.Join(", ", destructive.Select(a => a.Name))} на {hosts.Count} хостах ({list})? Продолжить?";
                if (!AppDialog.Confirm(OwnerWin(), msg))
                    return;
            }
        }

        Results.Clear();
        SelectedResult = null;
        RunStatus = apply ? "применение…" : "dry-run…";
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<PlanProgress>(ApplyProgress);

        try
        {
            var canceled = false;
            if (actions.Count > 0)
            {
                var result = await _runner.RunAsync(hosts, actions, apply, progress, _runCts.Token, psArgs);
                if (result.History.Count > 0)
                    HistoryStore.Append(result.History);
                canceled = result.Canceled;
            }

            // vCenter — по очереди: у PowerShell-действий один движок, подключения к каждому держатся до закрытия.
            foreach (var vc in psActions.Count > 0 ? vcs : new List<VCenterProfile>())
            {
                if (canceled) break;
                try
                {
                    _ps.Owner = OwnerWin;
                    var psHistory = await _ps.RunAsync(psActions, vc, psArgs, apply, progress, _runCts.Token);
                    if (psHistory.Count > 0) HistoryStore.Append(psHistory);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
            }

            _session.Persist();

            var failN = PlanRunner.Count(Results, "FAIL");
            var okN = PlanRunner.Count(Results, "OK");
            RunStatus = canceled
                ? "отменено"
                : failN == 0
                    ? (apply ? $"готово · {okN} OK" : $"dry-run · {okN} OK")
                    : $"{failN} FAIL / {okN} OK";
        }
        catch (Exception ex)
        {
            var row = PlanRunner.MakeRow("FAIL", "система", ex.Message, ex.ToString(), ok: false);
            Results.Insert(0, row);
            SelectedResult = row;
            RunStatus = "error";
        }
        finally
        {
            IsRunning = false;
            _runCts.Dispose();
            _runCts = null;
        }
    }

    private void ApplyProgress(PlanProgress p)
    {
        if (p.Host is not null)
        {
            if (p.HostStatus is not null)
                p.Host.Status = p.HostStatus;
            if (!string.IsNullOrWhiteSpace(p.DistroHint))
                p.Host.DistroHint = p.DistroHint;
        }

        if (p.Live is not null && p.LiveKey is not null && _running.TryGetValue(p.LiveKey, out var live))
        {
            lock (live.Output)
            {
                live.Output.Append(p.Live);
                // Держим хвост: полный вывод всё равно придёт в итоговой строке.
                if (live.Output.Length > 400_000) live.Output.Remove(0, live.Output.Length - 300_000);
            }

            _liveDirty = true;
        }

        // Аудит пользователей запоминаем по хосту — «Закрыть доступ» подставит найденные учётки и ключи.
        if (p.Row?.Audit is { } audit) _audits[audit.Host] = audit;

        if (p.Row is { Status: "RUN" } started)
        {
            _running[started.Title] = new RunningRow { Row = started, What = started.Preview.TrimEnd('…') };
            Results.Add(started);
            if (_autoFollow || SelectedResult is null) Select(started);
        }
        else if (p.Row is not null && _running.Remove(p.Row.Title, out var done))
        {
            // Итог заменяет строку RUN на том же месте.
            // Список сбрасывает выбор, когда меняют выбранный элемент, — запоминаем до замены.
            var wasSelected = ReferenceEquals(SelectedResult, done.Row);
            var i = Results.IndexOf(done.Row);
            _programmatic = true;
            try
            {
                if (i >= 0) Results[i] = p.Row;
                else Results.Add(p.Row);
            }
            finally
            {
                _programmatic = false;
            }

            if (wasSelected) Select(p.Row);
        }
        else if (p.Row is not null)
        {
            Results.Add(p.Row);
            if (SelectedResult is null) Select(p.Row);
        }

        if (p.Status is not null)
        {
            _statusBase = p.Status;
            RunStatus = IsRunning ? $"{p.Status} · {Elapsed(_runStarted)}" : p.Status;
        }
    }

    private void Warn(string text)
    {
        var row = PlanRunner.MakeRow("WARN", "система", text, text, ok: true);
        Results.Insert(0, row);
        SelectedResult = row;
        RunStatus = text;
    }

    private void EditCore(ActionItemView? item, bool clone)
    {
        if (item is null) return;

        var dlg = new ActionEditWindow(item.Definition, clone) { Owner = OwnerWin() };
        if (dlg.ShowDialog() != true) return;
        ActionCatalog.SaveCustom(dlg.Result);
        Load();
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;

    private void HostsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var h in (System.Collections.IEnumerable?)sender ?? Array.Empty<object>())
                if (h is IInventoryItem i) i.PropertyChanged += HostChanged;
        }
        else
        {
            if (e.OldItems is not null)
                foreach (IInventoryItem h in e.OldItems)
                    h.PropertyChanged -= HostChanged;
            if (e.NewItems is not null)
                foreach (IInventoryItem h in e.NewItems)
                    h.PropertyChanged += HostChanged;
        }

        UpdateHostSelection();
    }

    private void HostChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Галочка папки меняет сразу много ресурсов — пересчёт один раз, после всех.
        if (e.PropertyName != nameof(IInventoryItem.IsSelected) || _selectionQueued) return;
        _selectionQueued = true;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _selectionQueued = false;
            UpdateHostSelection();
        }, DispatcherPriority.Background);
    }

    private bool _selectionQueued;

    private void UpdateHostSelection()
    {
        if (_bulkHosts) return;
        var items = _session.Items.ToList();
        var n = items.Count(h => h.IsSelected);
        HostsSelectedText = n == 0 ? "ничего не выбрано" : $"{n} выбрано";
        var all = items.Count > 0 && n == items.Count;
        if (SelectAllChecked == all) 
        {
            if (!IsRunning) RefreshPreview();
            return;
        }
        _bulkHosts = true;
        SelectAllChecked = all;
        _bulkHosts = false;
        if (!IsRunning) RefreshPreview();
    }
}
