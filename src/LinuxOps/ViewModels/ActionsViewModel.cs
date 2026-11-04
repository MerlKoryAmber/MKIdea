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
    private CancellationTokenSource? _runCts;
    private bool _bulkHosts;

    public ActionsViewModel(AppSession session)
    {
        _session = session;
        Hosts.CollectionChanged += HostsChanged;
        foreach (var h in Hosts)
            h.PropertyChanged += HostChanged;
        UpdateHostSelection();
    }

    public ObservableCollection<HostConfig> Hosts => _session.Hosts;
    public ObservableCollection<ActionItemView> Actions { get; } = new();
    public ObservableCollection<ResultRow> Results { get; } = new();

    public Func<Window?>? Owner { get; set; }
    public Func<IReadOnlyList<ActionDefinition>>? ReadActions { get; set; }
    public Func<ActionItemView?>? CurrentAction { get; set; }
    public event Action? ActionsReloaded;

    [ObservableProperty] private string hostsSelectedText = "ничего не выбрано";
    [ObservableProperty] private bool? selectAllChecked;
    [ObservableProperty] private string runStatus = "";
    [ObservableProperty] private string detail = "Выберите строку плана.";
    [ObservableProperty] private ResultRow? selectedResult;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool canEditOne;
    [ObservableProperty] private bool canRemove;

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

    public void NoteCursor(ActionItemView? item)
    {
        CanEditOne = item is not null;
        CanRemove = item is not null;
    }

    public void PersistPlan()
        => DefaultsStore.SaveSelectedActions(Actions.Where(a => a.IsSelected).Select(a => a.Definition.Id));

    public void RefreshPreview()
    {
        if (IsRunning) return;
        Results.Clear();
        SelectedResult = null;
        var planned = Actions.Where(a => a.IsSelected).Select(a => a.Definition).ToList();
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        if (hosts.Count == 0 || planned.Count == 0)
        {
            Results.Add(PlanRunner.MakeRow("INFO", "План пуст", "Отметьте хосты и «В плане» у действий.", "", true));
            return;
        }

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
        Detail = value is null
            ? "Выберите строку плана."
            : string.IsNullOrWhiteSpace(value.Detail) ? value.Preview : value.Detail;
    }

    partial void OnIsRunningChanged(bool value)
    {
        DryRunCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        AddUserCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanEditOneChanged(bool value)
    {
        EditCommand.NotifyCanExecuteChanged();
        CloneCommand.NotifyCanExecuteChanged();
    }

    partial void OnCanRemoveChanged(bool value) => RemoveCommand.NotifyCanExecuteChanged();

    public void SetAllHosts(bool all)
    {
        _bulkHosts = true;
        foreach (var h in Hosts)
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

    [RelayCommand(CanExecute = nameof(EditCan))]
    private void Edit() => EditCore(CurrentAction?.Invoke(), clone: false);

    [RelayCommand(CanExecute = nameof(EditCan))]
    private void Clone() => EditCore(CurrentAction?.Invoke(), clone: true);

    public void EditFromCard(ActionItemView? item) => EditCore(item, clone: false);

    [RelayCommand(CanExecute = nameof(RemoveCan))]
    private void Remove()
    {
        if (CurrentAction?.Invoke() is not ActionItemView item)
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
        if (targets.Count == 0) targets = Hosts.ToList();
        if (targets.Count == 0) return;

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

    public void EditHost(HostConfig? host)
    {
        host ??= Hosts.FirstOrDefault(h => h.IsSelected) ?? Hosts.FirstOrDefault();
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _runCts?.Cancel();

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
    private bool EditCan() => CanEditOne;
    private bool RemoveCan() => CanRemove;

    private async Task RunAsync(bool apply)
    {
        var hosts = Hosts.Where(h => h.IsSelected).ToList();
        var actions = ReadActions?.Invoke() ?? Array.Empty<ActionDefinition>();
        if (hosts.Count == 0)
        {
            Warn("Отметьте хост.");
            return;
        }

        if (actions.Count == 0)
        {
            Warn("Отметьте действие.");
            return;
        }

        if (apply)
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
        RunStatus = apply ? "apply…" : "dry-run…";
        IsRunning = true;
        _runCts = new CancellationTokenSource();
        var progress = new Progress<PlanProgress>(ApplyProgress);

        try
        {
            var result = await _runner.RunAsync(hosts, actions, apply, progress, _runCts.Token);
            if (result.History.Count > 0)
                HistoryStore.Append(result.History);
            _session.Persist();

            var failN = PlanRunner.Count(Results, "FAIL");
            var okN = PlanRunner.Count(Results, "OK");
            RunStatus = result.Canceled
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

        if (p.Row is not null)
        {
            Results.Add(p.Row);
            SelectedResult ??= p.Row;
        }

        if (p.Status is not null)
            RunStatus = p.Status;
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
        if (item is null)
        {
            AppDialog.Info(OwnerWin(), "Кликните карточку действия (появится зелёная рамка), затем Изменить / Клон.");
            return;
        }

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
            foreach (var h in Hosts)
                h.PropertyChanged += HostChanged;
        }
        else
        {
            if (e.OldItems is not null)
                foreach (HostConfig h in e.OldItems)
                    h.PropertyChanged -= HostChanged;
            if (e.NewItems is not null)
                foreach (HostConfig h in e.NewItems)
                    h.PropertyChanged += HostChanged;
        }

        UpdateHostSelection();
    }

    private void HostChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostConfig.IsSelected))
            UpdateHostSelection();
    }

    private void UpdateHostSelection()
    {
        if (_bulkHosts) return;
        var n = Hosts.Count(h => h.IsSelected);
        HostsSelectedText = n == 0 ? "ничего не выбрано" : $"{n} выбрано";
        var all = Hosts.Count > 0 && n == Hosts.Count;
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
