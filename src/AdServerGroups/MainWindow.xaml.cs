using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdServerGroups.Abstractions;
using AdServerGroups.Models;
using AdServerGroups.Services;
using AdServerGroups.Views;
using Microsoft.Win32;

namespace AdServerGroups;

public partial class MainWindow : Window
{
    private enum WorkMode
    {
        LocalAdmins,
        Sql
    }

    private AppSettings _settings;
    private readonly SessionCredentials _credentials = new();
    private readonly ObservableCollection<AdComputerItem> _computers = new();
    private readonly ObservableCollection<GroupMappingItem> _mappings = new();
    private readonly ObservableCollection<SqlDiscoveryRow> _sqlRows = new();
    private IReadOnlyList<PlannedAction> _lastPlan = Array.Empty<PlannedAction>();
    private IReadOnlyList<SqlPlanLine> _lastSqlPlan = Array.Empty<SqlPlanLine>();
    private string _selectedContainerDn = string.Empty;
    private bool _adReady;
    private AppWorkState _workState = new();
    private readonly ObservableCollection<ServerWorkRecord> _historyRows = new();
    private ISqlSysadminProvider? _sqlProvider;
    private WorkMode _workMode = WorkMode.LocalAdmins;
    private bool _applyingWorkMode;
    private readonly List<LocalProbeSnapshot> _localProbes = new();
    private bool _loadingProbeCombo;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _workState = StateStore.Load();
        _credentials.UseIntegrated();
        ApplyWindowLayoutFromSettings();
        ComputersGrid.ItemsSource = _computers;
        MappingsGrid.ItemsSource = _mappings;
        HistoryGrid.ItemsSource = _historyRows;
        SqlDiscoveryGrid.ItemsSource = _sqlRows;
        LoadTaskFieldsFromSettings();
        ApplySettingsToUi();
        SyncOptionCheckboxesFromSettings();
        RefreshHistoryGrid();
        InitSqlProviderUi();
        ApplyWorkMode(log: false);
        SetRunMode("ещё нет плана", "ожидание dry-run", dryRunStyle: true);
        LogInfo("Готово к работе. Ошибки и статус — в этом логе внизу, без всплывающих окон.");
        LogInfo($"Настройки (DPAPI): {SettingsStore.SettingsPath}");
        LogInfo($"Базовые (JSON): {SettingsStore.DefaultsPath}");
        LogInfo($"История (DPAPI): {StateStore.StatePath}");
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => await ConnectCoreAsync(promptOnFailure: true, silentSuccess: true, reloadDirectory: true);
    }

    private void InitSqlProviderUi()
    {
        var providers = ProviderLoader.LoadSqlProviders();
        _sqlProvider = providers.FirstOrDefault();
        if (_sqlProvider is null)
        {
            ModeSqlRadio.IsEnabled = false;
            ModeSqlRadio.ToolTip = $"Нет DLL в {ProviderLoader.ProvidersDirectory}";
            LogInfo($"SQL-провайдер не найден. Чтобы включить контур SQL, положите DLL в: {ProviderLoader.ProvidersDirectory}");
            return;
        }

        ModeSqlRadio.IsEnabled = true;
        SqlProviderInfoText.Text = $"{_sqlProvider.DisplayName} · {ProviderLoader.ProvidersDirectory}";
        if (SqlTaskTemplateBox is not null)
            SqlTaskTemplateBox.Text = _settings.SqlGroupNameTemplate;
        LogInfo($"SQL-провайдер загружен: {_sqlProvider.Id} из {ProviderLoader.ProvidersDirectory}");
    }

    private void WorkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingWorkMode)
            return;
        if (ModeSqlRadio.IsChecked == true && _sqlProvider is null)
        {
            _applyingWorkMode = true;
            ModeLocalRadio.IsChecked = true;
            _applyingWorkMode = false;
            LogWarn("Контур SQL недоступен: нет провайдера в Providers\\.");
            return;
        }

        ApplyWorkMode(log: true);
    }

    private void ApplyWorkMode(bool log)
    {
        _applyingWorkMode = true;
        try
        {
            FlushSessionTemplateQuiet();
            _workMode = ModeSqlRadio.IsChecked == true ? WorkMode.Sql : WorkMode.LocalAdmins;

            if (_workMode == WorkMode.Sql)
            {
                BrandSubText.Text = "Серверы из OU → AD-группы (SQL-шаблон) → SQL sysadmin";
                LocalTab.Visibility = Visibility.Collapsed;
                RunTab.Visibility = Visibility.Collapsed;
                SqlTab.Visibility = Visibility.Visible;
                SourceTab.Header = "1 · Источник";
                GroupsTab.Header = "2 · Группы";
                SqlTab.Header = "3 · SQL sysadmin";
                GroupsNextButton.Content = "К SQL →";
                GroupsNextButton.ToolTip = "Discovery и apply SQL sysadmin";
                if (MainTabs.SelectedItem == LocalTab || MainTabs.SelectedItem == RunTab)
                    MainTabs.SelectedItem = SourceTab;
            }
            else
            {
                BrandSubText.Text = "Серверы из OU → группы по шаблону → локальные Administrators";
                LocalTab.Visibility = Visibility.Visible;
                RunTab.Visibility = Visibility.Visible;
                SqlTab.Visibility = Visibility.Collapsed;
                SourceTab.Header = "1 · Источник";
                GroupsTab.Header = "2 · Группы";
                LocalTab.Header = "3 · Локальные админы";
                RunTab.Header = "4 · Выполнение";
                GroupsNextButton.Content = "К локальным админам →";
                GroupsNextButton.ToolTip = null;
                if (MainTabs.SelectedItem == SqlTab)
                    MainTabs.SelectedItem = SourceTab;
            }

            LoadTaskFieldsFromSettings();
            if (log)
            {
                LogInfo(_workMode == WorkMode.Sql
                    ? "Контур: SQL sysadmin (вкладки LA скрыты)."
                    : "Контур: Local Admins (вкладка SQL скрыта).");
            }
        }
        finally
        {
            _applyingWorkMode = false;
        }
    }

    /// <summary>Сохраняет текст шаблона с вкладки «Группы» в поле текущего контура (без валидации).</summary>
    private void FlushSessionTemplateQuiet()
    {
        var template = TaskTemplateBox?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template) ||
            template.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        if (_workMode == WorkMode.Sql)
        {
            _settings.SqlGroupNameTemplate = template;
            if (SqlTaskTemplateBox is not null)
                SqlTaskTemplateBox.Text = template;
        }
        else
        {
            _settings.GroupNameTemplate = template;
        }
    }

    private void ApplySqlTemplateToSettings()
    {
        // Сессия: вкладка «Группы» (TaskTemplateBox) приоритетнее поля на вкладке SQL.
        var fromGroups = TaskTemplateBox?.Text.Trim() ?? string.Empty;
        var fromSqlTab = SqlTaskTemplateBox?.Text.Trim() ?? string.Empty;
        var template = fromGroups.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) >= 0
            ? fromGroups
            : fromSqlTab;

        if (string.IsNullOrWhiteSpace(template) ||
            template.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Шаблон SQL-группы должен содержать {ComputerName}.");
        }

        _settings.SqlGroupNameTemplate = template;
        if (TaskTemplateBox is not null)
            TaskTemplateBox.Text = template;
        if (SqlTaskTemplateBox is not null)
            SqlTaskTemplateBox.Text = template;
    }

    private bool RequireMode(WorkMode mode, string action)
    {
        if (_workMode == mode)
            return true;
        LogWarn($"{action}: недоступно в контуре «{(_workMode == WorkMode.Sql ? "SQL" : "Local Admins")}». Переключите контур сверху.");
        return false;
    }

    private void ResetSqlTemplate_Click(object sender, RoutedEventArgs e)
    {
        SqlTaskTemplateBox.Text = _settings.SqlGroupNameTemplate;
        if (_workMode == WorkMode.Sql && TaskTemplateBox is not null)
            TaskTemplateBox.Text = _settings.SqlGroupNameTemplate;
    }

    private void RefreshHistoryGrid()
    {
        _historyRows.Clear();
        foreach (var row in _workState.Servers
                     .OrderByDescending(s => s.AppliedUtc)
                     .ThenBy(s => s.Computer, StringComparer.OrdinalIgnoreCase))
        {
            _historyRows.Add(row);
        }
    }

    private void RefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        _workState = StateStore.Load();
        RefreshHistoryGrid();
        LogInfo($"История: {_workState.Servers.Count} записей.");
    }

    private void ClearHistorySelected_Click(object sender, RoutedEventArgs e)
    {
        var names = _computers.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        if (names.Count == 0)
        {
            LogWarn("Сброс истории: отметьте серверы на вкладке «Источник».");
            return;
        }

        StateStore.RemoveSelected(_workState, names);
        StateStore.Save(_workState);
        RefreshHistoryGrid();
        LogInfo($"История сброшена для: {string.Join(", ", names)}.");
    }

    private async void ShowAdminsDiff_Click(object sender, RoutedEventArgs e)
    {
        var selected = _computers.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            LogWarn("Diff: отметьте серверы на вкладке «Источник».");
            return;
        }

        try
        {
            SetBusy(true);
            SetRunMode("читаю diff…", "сравнение с историей", dryRunStyle: true);
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var history = StateStore.Load();
            _workState = history;

            var text = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("# DIFF Administrators vs снимок в истории");
                sb.AppendLine("# «+» появился после apply, «-» исчез");
                sb.AppendLine();

                var local = new LocalAdminService(settings, creds);
                foreach (var computer in selected)
                {
                    var groupName = SettingsStore.ResolveGroupName(settings, computer.Name);
                    var record = StateStore.Find(history, computer.Name, groupName);
                    sb.AppendLine($"## {computer.Name} → {groupName}");

                    if (record is null)
                    {
                        sb.AppendLine("  (нет записи в истории)");
                        sb.AppendLine();
                        continue;
                    }

                    if (!StateStore.HasAdminsSnapshot(record))
                    {
                        sb.AppendLine($"  История есть ({record.AppliedLocal}), но снимок Administrators ещё не записан.");
                        sb.AppendLine("  Снимок появится после следующего «Применить план» (0.2.2+).");
                        sb.AppendLine();
                        continue;
                    }

                    try
                    {
                        var current = local.GetAdministrators(computer.RemoteHost);
                        if (StateStore.AdminsHashMatches(record, current))
                        {
                            sb.AppendLine("  без изменений (hash совпал)");
                        }
                        else
                        {
                            var (added, removed) = StateStore.DiffAdmins(record.AdminsSnapshot, current);
                            sb.AppendLine("  " + OperationPlanner.FormatAdminsConflict(computer.Name, added, removed));
                            if (added.Count == 0 && removed.Count == 0)
                            {
                                sb.AppendLine("  было: " + string.Join(", ", record.AdminsSnapshot));
                                sb.AppendLine("  сейчас: " + string.Join(", ", current));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("  ERR: " + RemoteErrorHints.Annotate(ex));
                    }

                    sb.AppendLine();
                }

                return sb.ToString();
            });

            PlanLogBox.Text = text;
            RefreshHistoryGrid();
            SetRunMode("diff готов", "сравнение с историей", dryRunStyle: true);
            LogInfo($"Diff Administrators: {selected.Count} сервер(ов).");
        }
        catch (Exception ex)
        {
            LogError($"Diff: {RemoteErrorHints.Annotate(ex)}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyWindowLayoutFromSettings()
    {
        if (_settings.WindowWidth >= MinWidth)
            Width = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight)
            Height = _settings.WindowHeight;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }

        if (Enum.TryParse<WindowState>(_settings.WindowState, ignoreCase: true, out var state) &&
            state != WindowState.Minimized)
        {
            WindowState = state;
        }

        ApplyLogPanelHeightFromSettings();
    }

    private void ApplyLogPanelHeightFromSettings()
    {
        if (LogRow is null) return;
        var h = _settings.LogPanelHeight;
        if (h < LogRow.MinHeight) h = LogRow.MinHeight;
        if (h > LogRow.MaxHeight) h = LogRow.MaxHeight;
        LogRow.Height = new GridLength(h);
    }

    private void CaptureLogPanelHeightToSettings()
    {
        if (LogRow is null) return;
        if (LogRow.Height.IsAbsolute)
            _settings.LogPanelHeight = LogRow.Height.Value;
        else if (LogRow.ActualHeight > 0)
            _settings.LogPanelHeight = LogRow.ActualHeight;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            if (WindowState == WindowState.Maximized)
            {
                _settings.WindowWidth = RestoreBounds.Width;
                _settings.WindowHeight = RestoreBounds.Height;
                _settings.WindowLeft = RestoreBounds.Left;
                _settings.WindowTop = RestoreBounds.Top;
                _settings.WindowState = nameof(WindowState.Maximized);
            }
            else if (WindowState == WindowState.Normal)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.WindowLeft = Left;
                _settings.WindowTop = Top;
                _settings.WindowState = nameof(WindowState.Normal);
            }

            CaptureLogPanelHeightToSettings();
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            // закрытие не блокируем
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void ClearAppLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogBox.Clear();
        SetLogBadge("лог", ok: true);
    }

    private void LogInfo(string message) => AppendAppLog("INFO", message, ok: true);
    private void LogWarn(string message) => AppendAppLog("WARN", RemoteErrorHints.Annotate(message), ok: false);
    private void LogError(string message) => AppendAppLog("ERR ", RemoteErrorHints.Annotate(message), ok: false);

    private void AppendAppLog(string level, string message, bool ok)
    {
        if (AppLogBox is null) return;

        void Write()
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {level}  {message}";
            if (AppLogBox.Text.Length > 0)
                AppLogBox.AppendText(Environment.NewLine);
            AppLogBox.AppendText(line);
            AppLogBox.CaretIndex = AppLogBox.Text.Length;
            AppLogBox.ScrollToEnd();
            SetLogBadge(level.Trim(), ok);
        }

        if (!Dispatcher.CheckAccess())
            Dispatcher.Invoke(Write);
        else
            Write();
    }

    private void SetLogBadge(string text, bool ok)
    {
        if (LogLevelText is null || LogLevelBadge is null) return;
        LogLevelText.Text = text;
        LogLevelBadge.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ok ? "#DDF3EF" : "#F8EBD4"));
        LogLevelText.Foreground = ok
            ? (Brush)FindResource("AccentDeepBrush")
            : (Brush)FindResource("WarnBrush");
    }

    private void ApplySettingsToUi()
    {
        StatusDomainText.Text = _settings.DomainDns;
        StatusUserText.Text = GetEffectiveUserDisplay();
        AuthModeText.Text = _credentials.UseIntegratedWindowsAuth ? "SSO" : "пароль";
        var containerDn = string.IsNullOrWhiteSpace(_selectedContainerDn)
            ? _settings.DefaultContainerDn
            : _selectedContainerDn;
        StatusContainerText.Text = DnDisplay.ToFriendly(containerDn);
        StatusContainerText.ToolTip = containerDn;
        UpdateTaskOuFriendly();

        var ver = GetFileVersion();
        FooterText.Text =
            $"v{ver}  ·  Merl  ·  настройки рядом с exe (шифр.)  ·  изменения только после «Применить план»";
        Title = $"AD Server Groups {ver}";
    }

    private void LoadTaskFieldsFromSettings()
    {
        TaskTemplateBox.Text = _workMode == WorkMode.Sql
            ? _settings.SqlGroupNameTemplate
            : _settings.GroupNameTemplate;
        TaskGroupsOuBox.Text = _settings.GroupsOuDn;
        if (SqlTaskTemplateBox is not null)
            SqlTaskTemplateBox.Text = _settings.SqlGroupNameTemplate;
        UpdateTaskOuFriendly();
    }

    private void UpdateTaskOuFriendly()
    {
        if (TaskGroupsOuFriendly is null || TaskGroupsOuBox is null)
            return;
        var dn = TaskGroupsOuBox.Text.Trim();
        TaskGroupsOuFriendly.Text = string.IsNullOrWhiteSpace(dn)
            ? string.Empty
            : DnDisplay.ToFriendly(dn);
    }

    private void ResetTaskFields_Click(object sender, RoutedEventArgs e)
    {
        LoadTaskFieldsFromSettings();
    }

    private void ApplyTaskFieldsToSettings()
    {
        var template = TaskTemplateBox.Text.Trim();
        var ou = TaskGroupsOuBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(template) ||
            template.IndexOf("{ComputerName}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException("Шаблон имени группы должен содержать {ComputerName}.");
        }

        if (string.IsNullOrWhiteSpace(ou))
            throw new InvalidOperationException("Укажите OU для групп.");

        if (_workMode == WorkMode.Sql)
            _settings.SqlGroupNameTemplate = template;
        else
            _settings.GroupNameTemplate = template;

        _settings.GroupsOuDn = ou;
        if (SqlTaskTemplateBox is not null && _workMode == WorkMode.Sql)
            SqlTaskTemplateBox.Text = template;
    }

    private void SetRunMode(string badge, string journalHint, bool dryRunStyle)
    {
        RunModeText.Text = badge;
        JournalHintText.Text = journalHint;
        RunModeBadge.Background = dryRunStyle
            ? new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#DDF3EF"))
            : new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F8EBD4"));
        RunModeText.Foreground = dryRunStyle
            ? (System.Windows.Media.Brush)FindResource("AccentDeepBrush")
            : (System.Windows.Media.Brush)FindResource("WarnBrush");
    }

    private string GetEffectiveUserDisplay()
    {
        if (_credentials.HasExplicitCredentials)
            return _credentials.Username;

        try
        {
            return WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            return _settings.Username;
        }
    }

    private void WarnIfLocalSsoForRemoteAdmins()
    {
        if (_credentials.HasExplicitCredentials)
            return;

        var identity = GetEffectiveUserDisplay();
        if (!LooksLikeLocalWindowsAccount(identity))
            return;

        LogWarn(
            $"SSO сейчас как {identity} (локальная учётка ПК). AD может открываться, но Administrators на серверах часто даёт Access denied. " +
            $"Нажмите «Переподключить» и введите доменную учётку (например MERL\\user), которая локальный админ на целевых серверах.");
    }

    private static bool LooksLikeLocalWindowsAccount(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return false;

        var slash = identity.IndexOf('\\');
        if (slash <= 0)
            return false;

        var domainPart = identity[..slash];
        return string.Equals(domainPart, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(AppContext.BaseDirectory, "AdServerGroups.exe");

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion;
            }
        }
        catch
        {
            // ignore
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private void SyncOptionCheckboxesFromSettings()
    {
        OptAddLocal.IsChecked = _settings.AddGroupToLocalAdministrators;
        OptMigrate.IsChecked = _settings.MigrateDirectMembersToGroup;
        OptExpand.IsChecked = _settings.ExpandNestedGroupsOnMigrate;
        if (OptSqlMigrate is not null)
            OptSqlMigrate.IsChecked = _settings.SqlMigrateDomainSysadmins;
        if (OptSqlExpand is not null)
            OptSqlExpand.IsChecked = _settings.ExpandNestedGroupsOnMigrate;
        OptRemove.IsChecked = _settings.RemoveNonTargetMembers;
    }

    private void SyncSettingsFromOptionCheckboxes()
    {
        _settings.AddGroupToLocalAdministrators = OptAddLocal.IsChecked == true;
        _settings.MigrateDirectMembersToGroup = OptMigrate.IsChecked == true;
        _settings.ExpandNestedGroupsOnMigrate = OptExpand.IsChecked == true;
        if (OptSqlMigrate is not null)
            _settings.SqlMigrateDomainSysadmins = OptSqlMigrate.IsChecked == true;
        if (OptSqlExpand is not null)
            _settings.ExpandNestedGroupsOnMigrate = OptSqlExpand.IsChecked == true;
        _settings.RemoveNonTargetMembers = OptRemove.IsChecked == true;
    }

    private AdDirectoryService CreateAd() => new(_settings, _credentials);

    private LocalAdminService CreateLocal() => new(_settings, _credentials);

    private OperationPlanner CreatePlanner(AdDirectoryService ad)
        => new(_settings, ad, CreateLocal());

    private async void Connect_Click(object sender, RoutedEventArgs e)
        // Ручное «Переподключить»: сразу спросить доменную учётку (на workgroup SSO «успешен», но Remote SAM нет).
        => await ConnectCoreAsync(
            promptOnFailure: true,
            silentSuccess: false,
            reloadDirectory: !_adReady,
            forceCredentials: true);

    private async Task ConnectCoreAsync(
        bool promptOnFailure,
        bool silentSuccess,
        bool reloadDirectory,
        bool forceCredentials = false)
    {
        try
        {
            SetBusy(true);

            if (forceCredentials)
            {
                if (!PromptForCredentials())
                {
                    LogInfo("Переподключение отменено.");
                    return;
                }

                if (!await TryConnectOnceAsync())
                {
                    LogError("Не удалось подключиться с указанными учётными данными.");
                    return;
                }
            }
            else
            {
                // автостарт: прозрачная авторизация текущим Windows-токеном
                _credentials.UseIntegrated();
                if (!await TryConnectOnceAsync())
                {
                    if (!promptOnFailure || !PromptForCredentials())
                    {
                        LogWarn("Не удалось подключиться к AD прозрачной авторизацией.");
                        return;
                    }

                    if (!await TryConnectOnceAsync())
                    {
                        LogError("Не удалось подключиться с указанными учётными данными.");
                        return;
                    }
                }
            }

            if (reloadDirectory || !_adReady)
            {
                using var ad = CreateAd();
                var tree = await Task.Run(() => ad.LoadOuTree());
                OuTree.Items.Clear();
                OuTree.Items.Add(BuildTreeItem(tree, expandThis: true));

                if (string.IsNullOrWhiteSpace(_selectedContainerDn))
                    _selectedContainerDn = _settings.DefaultContainerDn;

                await LoadComputersAsync(_selectedContainerDn);
                _adReady = true;
                LogInfo(silentSuccess
                    ? $"Подключено к {_settings.DomainDns} ({GetEffectiveUserDisplay()})."
                    : $"Подключение к AD установлено: {_settings.DomainDns}.");
            }
            else
            {
                _adReady = true;
                LogInfo($"Сессия AD в порядке ({_settings.DomainDns}, {GetEffectiveUserDisplay()}). Список серверов и галки не трогал — для обновления списка есть «Обновить список».");
            }

            WarnIfLocalSsoForRemoteAdmins();
            ApplySettingsToUi();
        }
        catch (Exception ex)
        {
            LogError($"Подключение: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            ApplySettingsToUi();
        }
    }

    private async Task<bool> TryConnectOnceAsync()
    {
        try
        {
            using var ad = CreateAd();
            await Task.Run(ad.TestConnection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool PromptForCredentials()
    {
        var suggested = string.IsNullOrWhiteSpace(_settings.Username)
            ? WindowsIdentity.GetCurrent().Name
            : _settings.Username;

        var dlg = new CredentialsWindow(suggested) { Owner = this };
        if (dlg.ShowDialog() != true)
            return false;

        _credentials.UseExplicit(dlg.Username, dlg.Password);
        _settings.Username = dlg.Username;
        SettingsStore.Save(_settings);
        return true;
    }

    private TreeViewItem BuildTreeItem(OuNode node, bool expandThis = false)
    {
        var item = new TreeViewItem
        {
            Header = node.Name,
            Tag = node.DistinguishedName,
            // Только корень развёрнут (первый уровень OU виден); дальше — вручную.
            IsExpanded = expandThis
        };

        foreach (var child in node.Children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            item.Items.Add(BuildTreeItem(child, expandThis: false));

        return item;
    }

    private async void OuTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (OuTree.SelectedItem is not TreeViewItem item || item.Tag is not string dn)
            return;

        _selectedContainerDn = dn;
        ApplySettingsToUi();
        await LoadComputersAsync(dn);
    }

    private async void RefreshComputers_Click(object sender, RoutedEventArgs e)
    {
        var dn = string.IsNullOrWhiteSpace(_selectedContainerDn)
            ? _settings.DefaultContainerDn
            : _selectedContainerDn;
        await LoadComputersAsync(dn);
    }

    private void SelectAllComputers_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box)
            return;

        // Стандарт: если есть хотя бы одна снятая — выбрать все; если все стоят — снять все.
        // (IsThreeState иначе крутит true→null и старый код снова ставил «все».)
        var selectAll = _computers.Count == 0 || _computers.Any(c => !c.IsSelected);
        foreach (var computer in _computers)
            computer.IsSelected = selectAll;
        box.IsChecked = selectAll;
    }

    private async Task LoadComputersAsync(string containerDn)
    {
        try
        {
            SetBusy(true);
            // Сохраняем отмеченные имена — иначе любой reload сбрасывает галки.
            var keepSelected = _computers
                .Where(c => c.IsSelected)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            using var ad = CreateAd();
            var list = await Task.Run(() => ad.FindWindowsServers(containerDn));
            _computers.Clear();
            foreach (var c in list)
            {
                c.IsSelected = keepSelected.Contains(c.Name);
                _computers.Add(c);
            }

            ComputerCountText.Text = $"{_computers.Count} шт.";
            if (SelectAllComputersBox is not null)
            {
                if (_computers.Count == 0)
                    SelectAllComputersBox.IsChecked = false;
                else if (_computers.All(c => c.IsSelected))
                    SelectAllComputersBox.IsChecked = true;
                else if (_computers.Any(c => c.IsSelected))
                    SelectAllComputersBox.IsChecked = null; // indeterminate
                else
                    SelectAllComputersBox.IsChecked = false;
            }
        }
        catch (Exception ex)
        {
            LogError($"Список компьютеров: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new SettingsWindow(_settings) { Owner = this };
        if (wnd.ShowDialog() == true)
        {
            // Геометрию окна из диалога настроек не затираем — она живёт в _settings текущего окна
            CaptureLogPanelHeightToSettings();
            var geometry = (
                _settings.WindowWidth, _settings.WindowHeight,
                _settings.WindowLeft, _settings.WindowTop, _settings.WindowState,
                _settings.LogPanelHeight);
            _settings = wnd.Settings;
            _settings.WindowWidth = geometry.WindowWidth;
            _settings.WindowHeight = geometry.WindowHeight;
            _settings.WindowLeft = geometry.WindowLeft;
            _settings.WindowTop = geometry.WindowTop;
            _settings.WindowState = geometry.WindowState;
            _settings.LogPanelHeight = geometry.LogPanelHeight;
            SettingsStore.Save(_settings);
            LoadTaskFieldsFromSettings();
            ApplySettingsToUi();
            SyncOptionCheckboxesFromSettings();
            LogInfo("Настройки сохранены (зашифрованный файл рядом с exe).");
        }
    }

    private void GoGroups_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = GroupsTab;
        RefreshMappings_Click(sender, e);
    }

    private void GroupsNext_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyTaskFieldsToSettings();
        }
        catch (Exception ex)
        {
            LogWarn($"Группы → далее: {ex.Message}");
            return;
        }

        if (_workMode == WorkMode.Sql)
            MainTabs.SelectedItem = SqlTab;
        else
            MainTabs.SelectedItem = LocalTab;
    }

    private void GoRun_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.LocalAdmins, "Выполнение"))
            return;
        MainTabs.SelectedItem = RunTab;
    }

    private async void SqlDiscover_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.Sql, "SQL discovery"))
            return;
        if (_sqlProvider is null)
        {
            LogWarn("SQL-провайдер не загружен.");
            return;
        }

        var targets = _computers.Where(c => c.IsSelected).ToList();
        if (targets.Count == 0)
        {
            LogWarn("SQL: отметьте серверы на вкладке «Источник».");
            return;
        }

        try
        {
            ApplyTaskFieldsToSettings();
            ApplySqlTemplateToSettings();
            SetBusy(true);
            SqlStatusText.Text = "поиск…";
            _sqlRows.Clear();
            SqlPlanBox.Clear();
            _lastSqlPlan = Array.Empty<SqlPlanLine>();

            var provider = _sqlProvider;
            var settings = CloneSettings(_settings);
            var rows = await Task.Run(() =>
            {
                var list = new List<SqlDiscoveryRow>();
                foreach (var c in targets)
                {
                    var discovery = provider.Discover(c.Name, c.DnsHostName);
                    var group = SettingsStore.ResolveSqlGroupName(settings, c.Name);
                    list.Add(new SqlDiscoveryRow
                    {
                        ComputerName = c.Name,
                        DnsHostName = c.DnsHostName,
                        SqlFound = discovery.SqlFound,
                        Instances = discovery.Instances,
                        InstancesText = discovery.Instances.Count == 0
                            ? "—"
                            : string.Join(", ", discovery.Instances),
                        Access = discovery.Access,
                        GroupName = group,
                        Detail = discovery.Detail
                    });
                }

                return list;
            });

            foreach (var row in rows)
                _sqlRows.Add(row);

            var found = rows.Count(r => r.SqlFound);
            var ok = rows.Count(r => r.CanPlan);
            SqlStatusText.Text = $"SQL: {found} найдено, доступ OK: {ok}";
            LogInfo($"SQL discovery: отмечено {targets.Count}, найдено {found}, доступ OK {ok}. Auth = Windows-токен процесса ({GetEffectiveUserDisplay()}).");
        }
        catch (Exception ex)
        {
            LogError($"SQL discovery: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SqlBuildPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.Sql, "SQL план"))
            return;
        await BuildSqlPlanAsync(apply: false);
    }

    private async void SqlApply_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.Sql, "SQL apply"))
            return;
        if (_lastSqlPlan.Count == 0)
            await BuildSqlPlanAsync(apply: false);

        if (_lastSqlPlan.Count == 0 || _lastSqlPlan.All(l => l.IsSkip || l.Kind == "ERR"))
        {
            LogWarn("SQL: нечего применять (нет доступных инстансов или всё SKIP).");
            return;
        }

        var migrateHint = OptSqlMigrate?.IsChecked == true
            ? "\n\nДоменные Windows-sysadmin будут добавлены в целевую AD-группу" +
              (OptSqlExpand?.IsChecked == true ? " (группы распакуем до пользователей)" : "") +
              ". В SQL ничего не удаляется. Компьютеры (*$) пропускаются."
            : string.Empty;
        if (!ConfirmWindow.Ask(this,
                "Применить к SQL",
                "Будут созданы SQL login (Windows-группа) и выданы права sysadmin на найденных инстансах." +
                migrateHint,
                "Применить"))
        {
            LogInfo("SQL apply отменён.");
            return;
        }

        await BuildSqlPlanAsync(apply: true);
    }

    private async Task BuildSqlPlanAsync(bool apply)
    {
        if (_workMode != WorkMode.Sql)
        {
            LogWarn("SQL план: только в контуре SQL.");
            return;
        }
        if (_sqlProvider is null)
        {
            LogWarn("SQL-провайдер не загружен.");
            return;
        }

        var actionable = _sqlRows.Where(r => r.CanPlan).ToList();
        if (actionable.Count == 0)
        {
            LogWarn("SQL: сначала «Найти SQL» — нужны строки с SQL=есть и Доступ=OK.");
            return;
        }

        try
        {
            if (OptSqlMigrate is not null)
                _settings.SqlMigrateDomainSysadmins = OptSqlMigrate.IsChecked == true;
            if (OptSqlExpand is not null)
                _settings.ExpandNestedGroupsOnMigrate = OptSqlExpand.IsChecked == true;
            ApplyTaskFieldsToSettings();
            ApplySqlTemplateToSettings();
            SetBusy(true);
            var provider = _sqlProvider;
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var domainNetBios = SettingsStore.ResolveDomainNetBios(settings);

            var lines = await Task.Run(() =>
            {
                var all = new List<SqlPlanLine>();
                var ensured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var migrateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var ad = new AdDirectoryService(settings, creds);
                var localHints = new LocalAdminService(settings, creds);

                foreach (var row in actionable)
                {
                    var group = string.IsNullOrWhiteSpace(row.GroupName)
                        ? SettingsStore.ResolveSqlGroupName(settings, row.ComputerName)
                        : row.GroupName;

                    if (ensured.Add(group))
                    {
                        var exists = ad.GroupExists(group);
                        var sqlDesc = SettingsStore.ResolveSqlGroupDescription(settings, row.ComputerName);
                        if (!exists && settings.CreateMissingGroups)
                        {
                            if (apply)
                            {
                                ad.EnsureGroup(group, sqlDesc);
                                WaitUntilGroupVisible(ad, group, TimeSpan.FromSeconds(12));
                            }

                            all.Add(new SqlPlanLine
                            {
                                Kind = "ENSURE_LOGIN",
                                Text = apply
                                    ? $"[AD] CREATE  {group}  in groups OU"
                                    : $"[AD] CREATE  {group}  in groups OU  (будет при apply)"
                            });
                        }
                        else if (exists)
                        {
                            if (apply)
                                ad.EnsureGroup(group, sqlDesc); // обновит Description при необходимости
                            all.Add(new SqlPlanLine
                            {
                                Kind = "SKIP",
                                Text = $"[AD] EXISTS  {group}"
                            });
                        }
                        else
                        {
                            all.Add(new SqlPlanLine
                            {
                                Kind = "SKIP",
                                Text = $"[AD] SKIP create {group} (CreateMissingGroups=false)"
                            });
                        }
                    }

                    foreach (var inst in row.Instances)
                    {
                        var target = new SqlTarget
                        {
                            ComputerName = row.ComputerName,
                            DnsHostName = row.DnsHostName,
                            Instance = inst
                        };
                        var part = provider.BuildPlan(target, group, domainNetBios, apply);
                        all.AddRange(part);

                        if (!settings.SqlMigrateDomainSysadmins)
                            continue;

                        try
                        {
                            var principals = provider.ListWindowsSysadminPrincipals(target);
                            var pending = new List<(string Display, string Sam)>();
                            foreach (var principal in principals)
                            {
                                if (!IsOurDomainWindowsPrincipal(principal, settings.DomainDns))
                                    continue;

                                var rootSam = StripDomainPrefix(principal);
                                if (string.Equals(rootSam, group, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (rootSam.EndsWith('$'))
                                {
                                    all.Add(new SqlPlanLine
                                    {
                                        Kind = "SKIP",
                                        Text = $"[AD] SKIP migrate {principal} (компьютер)"
                                    });
                                    continue;
                                }

                                if (localHints.IsProtectedMember(principal) || localHints.IsProtectedMember(rootSam))
                                {
                                    all.Add(new SqlPlanLine
                                    {
                                        Kind = "SKIP",
                                        Text = $"[AD] SKIP migrate {principal} (защищённый)"
                                    });
                                    continue;
                                }

                                // Как в LA: ExpandNestedGroupsOnMigrate → пользователи; иначе группа целиком.
                                IReadOnlyList<MigrationLeaf> leaves;
                                try
                                {
                                    leaves = ad.ExpandForMigration(principal, localHints.IsProtectedMember);
                                }
                                catch (Exception ex)
                                {
                                    all.Add(new SqlPlanLine
                                    {
                                        Kind = "SKIP",
                                        Text = $"[AD] SKIP unpack {principal}: {RemoteErrorHints.Annotate(ex)}"
                                    });
                                    continue;
                                }

                                foreach (var leaf in leaves)
                                {
                                    var sam = StripDomainPrefix(leaf.Member);
                                    if (string.IsNullOrWhiteSpace(sam) ||
                                        string.Equals(sam, group, StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    if (sam.EndsWith('$'))
                                    {
                                        all.Add(new SqlPlanLine
                                        {
                                            Kind = "SKIP",
                                            Text = $"[AD] SKIP migrate {leaf.Member} (компьютер)"
                                        });
                                        continue;
                                    }

                                    if (localHints.IsProtectedMember(leaf.Member) || localHints.IsProtectedMember(sam))
                                    {
                                        all.Add(new SqlPlanLine
                                        {
                                            Kind = "SKIP",
                                            Text = $"[AD] SKIP migrate {leaf.Member} (защищённый)"
                                        });
                                        continue;
                                    }

                                    var key = $"{group}\0{sam}";
                                    if (!migrateKeys.Add(key))
                                        continue;

                                    if (ad.IsMemberOfGroup(group, sam))
                                    {
                                        all.Add(new SqlPlanLine
                                        {
                                            Kind = "SKIP",
                                            Text = $"[AD] SKIP  {leaf.Member} уже в {group}"
                                        });
                                        continue;
                                    }

                                    var display = string.IsNullOrWhiteSpace(leaf.UnpackedFrom)
                                        ? leaf.Member
                                        : $"{leaf.Member} ← {leaf.UnpackedFrom}";
                                    pending.Add((display, sam));
                                }
                            }

                            if (apply && pending.Count > 0)
                            {
                                ad.AddMembersToGroup(group, pending.Select(p => p.Sam));
                                foreach (var p in pending)
                                {
                                    all.Add(new SqlPlanLine
                                    {
                                        Kind = "ENSURE_LOGIN",
                                        Text = $"[AD] ADD     {p.Display}  → {group}  OK"
                                    });
                                }
                            }
                            else if (!apply)
                            {
                                foreach (var p in pending)
                                {
                                    all.Add(new SqlPlanLine
                                    {
                                        Kind = "ENSURE_LOGIN",
                                        Text = $"[AD] ADD     {p.Display}  → {group}  (будет при apply)"
                                    });
                                }
                            }
                        }
                        catch (Exception mex)
                        {
                            all.Add(new SqlPlanLine
                            {
                                Kind = "ERR",
                                Text = $"[AD] ERR migrate @{row.ComputerName}\\{inst}: {RemoteErrorHints.Annotate(mex)}"
                            });
                        }
                    }
                }

                return all;
            });

            _lastSqlPlan = lines;
            var sb = new StringBuilder();
            sb.AppendLine(apply ? "# SQL APPLY (Windows Auth процесса)" : "# SQL DRY-RUN (Windows Auth процесса)");
            sb.AppendLine($"# user={GetEffectiveUserDisplay()}");
            sb.AppendLine();
            foreach (var line in lines)
                sb.AppendLine(line.Text);
            SqlPlanBox.Text = sb.ToString();
            SqlStatusText.Text = apply ? "SQL apply завершён" : $"SQL план: {lines.Count} строк";
            LogInfo(apply ? "SQL apply выполнен." : "SQL dry-run готов.");
        }
        catch (Exception ex)
        {
            LogError($"SQL план: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RefreshMappings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyTaskFieldsToSettings();
            using var ad = CreateAd();
            var planner = CreatePlanner(ad);
            var items = planner.BuildGroupMappings(_computers, useSqlGroupTemplate: _workMode == WorkMode.Sql);
            _mappings.Clear();
            foreach (var item in items)
                _mappings.Add(item);
        }
        catch (Exception ex)
        {
            LogError($"Группы: {ex.Message}");
        }
    }

    private async void ProbeLocal_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.LocalAdmins, "Локальные админы"))
            return;
        SyncSettingsFromOptionCheckboxes();
        var targets = _computers.Where(c => c.IsSelected).ToList();
        if (targets.Count == 0)
        {
            LogWarn("Локальные админы: отметьте галками один или несколько серверов на вкладке «Источник».");
            return;
        }

        try
        {
            ApplyTaskFieldsToSettings();
        }
        catch (Exception ex)
        {
            LogWarn($"Локальные админы: {ex.Message}");
            return;
        }

        SetBusy(true);
        _localProbes.Clear();
        LocalNowList.Items.Clear();
        LocalAfterList.Items.Clear();
        LocalProbeServerBox.ItemsSource = null;
        LocalNowHeader.Text = "Чтение Administrators…";
        LocalProbeCountText.Text = string.Empty;
        ProbeStatusText.Text = $"0 / {targets.Count}";

        var settings = CloneSettings(_settings);
        var creds = CloneCredentials(_credentials);
        var ok = 0;
        var fail = 0;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var computer = targets[i];
                ProbeStatusText.Text = $"{i + 1} / {targets.Count}: {computer.Name}";

                try
                {
                    var snapshot = await Task.Run(() =>
                    {
                        using var ad = new AdDirectoryService(settings, creds);
                        var local = new LocalAdminService(settings, creds);
                        var groupName = SettingsStore.ResolveGroupName(settings, computer.Name);
                        var members = local.GetAdministrators(computer.RemoteHost);
                        var migratedLeaves = new List<string>();
                        if (settings.MigrateDirectMembersToGroup)
                        {
                            foreach (var member in members)
                            {
                                if (local.IsProtectedMember(member) ||
                                    string.Equals(member, groupName, StringComparison.OrdinalIgnoreCase))
                                    continue;
                                foreach (var leaf in ad.ExpandForMigration(member, local.IsProtectedMember))
                                    migratedLeaves.Add(leaf.Member);
                            }
                        }

                        var after = BuildAfterMembers(local, members, groupName, settings);
                        return new LocalProbeSnapshot
                        {
                            ComputerName = computer.Name,
                            RemoteHost = computer.RemoteHost,
                            GroupName = groupName,
                            MembersNow = members.ToList(),
                            MembersAfterLocal = after,
                            MigrateLeaves = migratedLeaves
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList()
                        };
                    });

                    _localProbes.Add(snapshot);
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    string groupName;
                    try { groupName = SettingsStore.ResolveGroupName(settings, computer.Name); }
                    catch { groupName = string.Empty; }

                    _localProbes.Add(new LocalProbeSnapshot
                    {
                        ComputerName = computer.Name,
                        RemoteHost = computer.RemoteHost,
                        GroupName = groupName,
                        Error = RemoteErrorHints.Annotate(ex)
                    });
                    LogWarn($"{computer.Name} ({computer.RemoteHost}): {ex.Message}");
                }
            }

            ProbeStatusText.Text = $"готово: ок {ok}, ошибок {fail}";
            LocalProbeCountText.Text = $"прочитано: {_localProbes.Count}";
            BindLocalProbeCombo(selectLastOk: true);

            if (ok == 0 && fail > 0)
            {
                LogWarn($"Ни один сервер не ответил ({fail} ошибок). Выберите сервер в списке — справа будет текст ошибки.");
            }
            else if (fail > 0)
                LogWarn($"Прочитано {ok} из {targets.Count}, ошибок: {fail}. Переключайте сервер в комбобоксе.");
            else
                LogInfo($"Прочитано серверов: {ok}. Выберите сервер в списке сверху.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void BindLocalProbeCombo(bool selectLastOk)
    {
        _loadingProbeCombo = true;
        try
        {
            LocalProbeServerBox.ItemsSource = null;
            LocalProbeServerBox.ItemsSource = _localProbes.ToList();
            if (_localProbes.Count == 0)
            {
                LocalNowHeader.Text = "Сейчас в Administrators";
                LocalAfterHeader.Text = "Прогноз после apply";
                return;
            }

            LocalProbeSnapshot? pick = null;
            if (selectLastOk)
                pick = _localProbes.LastOrDefault(p => p.Error is null) ?? _localProbes[^1];
            LocalProbeServerBox.SelectedItem = pick ?? _localProbes[0];
        }
        finally
        {
            _loadingProbeCombo = false;
        }

        ShowLocalProbeSnapshot(LocalProbeServerBox.SelectedItem as LocalProbeSnapshot);
    }

    private void LocalProbeServer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProbeCombo)
            return;
        ShowLocalProbeSnapshot(LocalProbeServerBox.SelectedItem as LocalProbeSnapshot);
    }

    private void ShowLocalProbeSnapshot(LocalProbeSnapshot? snap)
    {
        LocalNowList.Items.Clear();
        LocalAfterList.Items.Clear();
        if (snap is null)
        {
            LocalNowHeader.Text = "Сейчас в Administrators";
            LocalAfterHeader.Text = "Прогноз после apply";
            return;
        }

        LocalNowHeader.Text = $"{snap.ComputerName} · сейчас в Administrators";
        LocalAfterHeader.Text = $"{snap.ComputerName} · прогноз после apply";

        if (snap.Error is not null)
        {
            LocalNowList.Items.Add("(не прочитано)");
            LocalAfterList.Items.Add($"Ошибка: {snap.Error}");
            return;
        }

        foreach (var m in snap.MembersNow)
            LocalNowList.Items.Add(m);

        LocalAfterList.Items.Add($"— В AD-группу «{snap.GroupName}» будут добавлены —");
        if (snap.MigrateLeaves.Count == 0)
            LocalAfterList.Items.Add("  (некого / migrate выкл)");
        else
        {
            foreach (var leaf in snap.MigrateLeaves)
                LocalAfterList.Items.Add($"  → {leaf}");
        }

        LocalAfterList.Items.Add(string.Empty);
        LocalAfterList.Items.Add("— В локальных Administrators останутся —");
        if (snap.MembersAfterLocal.Count == 0)
            LocalAfterList.Items.Add("  (пусто)");
        else
        {
            foreach (var m in snap.MembersAfterLocal)
                LocalAfterList.Items.Add($"  {m}");
        }
    }

    private List<string> BuildAfterMembers(
        LocalAdminService local,
        IReadOnlyList<string> members,
        string groupName,
        AppSettings settings)
    {
        var after = new List<string>();
        foreach (var m in members)
        {
            if (local.IsProtectedMember(m) || IsSameGroup(m, groupName))
                after.Add(m);
            else if (!settings.RemoveNonTargetMembers && !settings.MigrateDirectMembersToGroup)
                after.Add(m);
        }

        if (settings.AddGroupToLocalAdministrators &&
            !after.Any(m => IsSameGroup(m, groupName)))
        {
            after.Add($"{settings.DomainDns}\\{groupName}");
        }

        return after
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void TaskGroupsOuBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateTaskOuFriendly();

    private async void BuildPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.LocalAdmins, "План LA"))
            return;
        SyncSettingsFromOptionCheckboxes();
        try
        {
            ApplyTaskFieldsToSettings();
            SetBusy(true);
            SetRunMode("считаю план…", "dry-run", dryRunStyle: true);
            PlanLogBox.Text = "# DRY-RUN — идёт опрос (недоступные серверы пропускаются по таймауту)…";

            var computers = _computers.ToList();
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var probeLocal = settings.AddGroupToLocalAdministrators
                             || settings.MigrateDirectMembersToGroup
                             || settings.RemoveNonTargetMembers;
            var useHistory = OptUseHistory.IsChecked == true;

            _lastPlan = await Task.Run(() =>
            {
                using var ad = new AdDirectoryService(settings, creds);
                var planner = new OperationPlanner(settings, ad, new LocalAdminService(settings, creds));
                var history = StateStore.Load();
                return planner.BuildPlan(computers, probeLocal, history, useHistory);
            });

            var sb = new StringBuilder();
            sb.AppendLine("# DRY-RUN — изменения ещё НЕ применены");
            sb.AppendLine("# [SKIP] = уже в истории / уже на месте; [CONFLICT] = Administrators изменились; недоступные — таймаут");
            sb.AppendLine();
            foreach (var line in _lastPlan)
            {
                sb.AppendLine(string.IsNullOrWhiteSpace(line.Detail) ? line.ToLogLine() : line.Detail);
            }

            PlanLogBox.Text = sb.ToString();
            var selected = computers.Count(c => c.IsSelected);
            var create = _lastPlan.Count(a => a.Kind == PlanActionKind.CreateGroup && !IsSkipAction(a));
            var addLocal = _lastPlan.Count(a => a.Kind == PlanActionKind.AddGroupToLocalAdmins && !IsSkipAction(a));
            var skipped = _lastPlan.Count(a => IsSkipAction(a) && !IsConflictAction(a));
            var conflicts = _lastPlan.Count(IsConflictAction);
            var migrate = _lastPlan.Count(a => a.Kind == PlanActionKind.AddMemberToGroup && !IsSkipAction(a));
            var remove = _lastPlan.Count(a => a.Kind == PlanActionKind.RemoveLocalMember && !IsSkipAction(a));
            PlanSummaryText.Text =
                $"План (dry-run) на {selected} сервер(ов): создать {create}, в Administrators {addLocal}, перенести {migrate}, удалить {remove}, skip {skipped}, conflict {conflicts}" +
                (useHistory ? " · история вкл" : " · история выкл");
            SetRunMode("dry-run · ничего не изменено", "план готов, AD не тронут", dryRunStyle: true);
            LogInfo($"Dry-run готов: {PlanSummaryText.Text}");
        }
        catch (Exception ex)
        {
            LogError($"План: {RemoteErrorHints.Annotate(ex)}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.LocalAdmins, "Бэкап Administrators"))
            return;
        if (!_computers.Any(c => c.IsSelected))
        {
            LogWarn("Бэкап: отметьте хотя бы один сервер на вкладке «Источник».");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"admins-backup-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Title = "Куда сохранить бэкап Administrators"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            SetBusy(true);
            var computers = _computers.ToList();
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var fileName = dlg.FileName;

            var path = await Task.Run(() =>
            {
                var backup = new BackupService();
                var local = new LocalAdminService(settings, creds);
                return backup.CreateLocalAdminsBackup(computers, local, fileName);
            });

            var txt = Path.ChangeExtension(path, ".txt");
            PlanLogBox.Text =
                $"# БЭКАП сохранён{Environment.NewLine}JSON: {path}{Environment.NewLine}TXT:  {txt}{Environment.NewLine}" +
                $"{Environment.NewLine}Недоступные серверы попадут в errors. План не применялся.";
            SetRunMode("бэкап сохранён", "снимок Administrators", dryRunStyle: true);
            LogInfo($"Бэкап сохранён: {path} ; {txt}");
        }
        catch (Exception ex)
        {
            LogError($"Бэкап: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ExportPlan_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PlanLogBox.Text))
        {
            LogWarn("Экспорт: сначала нажмите «Показать план (dry-run)».");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"ad-server-groups-plan-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        File.WriteAllText(dlg.FileName, PlanLogBox.Text ?? string.Empty, Encoding.UTF8);
        LogInfo($"План экспортирован: {dlg.FileName}");
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireMode(WorkMode.LocalAdmins, "Применить LA"))
            return;
        SyncSettingsFromOptionCheckboxes();

        try
        {
            ApplyTaskFieldsToSettings();
        }
        catch (Exception ex)
        {
            LogWarn($"Применить: {ex.Message}");
            return;
        }

        if (_lastPlan.Count == 0)
        {
            LogInfo("Плана ещё нет — сначала строю dry-run…");
            await BuildPlanAndWaitAsync();
        }

        if (_lastPlan.Count == 0)
        {
            LogWarn("План пуст — применять нечего.");
            return;
        }

        var laMessage = _settings.RemoveNonTargetMembers
            ? "Будут выполнены изменения в AD и на серверах, включая удаление членов Administrators. Недоступные серверы будут пропущены."
            : "Будут выполнены изменения в AD и на серверах по текущему плану. Недоступные серверы будут пропущены.";
        if (!ConfirmWindow.Ask(this, "Применить план", laMessage, "Применить"))
        {
            LogInfo("Применение отменено.");
            return;
        }

        try
        {
            SetBusy(true);
            SetRunMode("применяю…", "apply", dryRunStyle: false);
            PlanLogBox.Text = "# ПРИМЕНЕНИЕ — недоступные серверы пропускаются по таймауту…";

            var plan = _lastPlan.ToList();
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var appliedBy = GetEffectiveUserDisplay();

            var log = await Task.Run(() =>
            {
                using var ad = new AdDirectoryService(settings, creds);
                var planner = new OperationPlanner(settings, ad, new LocalAdminService(settings, creds));
                var state = StateStore.Load();
                return planner.Apply(plan, state, appliedBy);
            });

            _workState = StateStore.Load();
            RefreshHistoryGrid();

            PlanLogBox.Text = "# ПРИМЕНЕНО" + Environment.NewLine +
                              "# История обновлена (успешные шаги)" + Environment.NewLine + Environment.NewLine +
                              string.Join(Environment.NewLine, log);
            PlanSummaryText.Text = "План применён. История сохранена.";
            SetRunMode("применено · изменения внесены", "результат apply", dryRunStyle: false);
            LogInfo("План применён. История: " + StateStore.StatePath);
            RefreshMappings_Click(sender, e);
        }
        catch (Exception ex)
        {
            LogError($"Применить: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task BuildPlanAndWaitAsync()
    {
        SyncSettingsFromOptionCheckboxes();
        ApplyTaskFieldsToSettings();
        SetBusy(true);
        try
        {
            var computers = _computers.ToList();
            var settings = CloneSettings(_settings);
            var creds = CloneCredentials(_credentials);
            var probeLocal = settings.AddGroupToLocalAdministrators
                             || settings.MigrateDirectMembersToGroup
                             || settings.RemoveNonTargetMembers;
            var useHistory = OptUseHistory.IsChecked == true;

            _lastPlan = await Task.Run(() =>
            {
                using var ad = new AdDirectoryService(settings, creds);
                var planner = new OperationPlanner(settings, ad, new LocalAdminService(settings, creds));
                return planner.BuildPlan(computers, probeLocal, StateStore.Load(), useHistory);
            });

            var sb = new StringBuilder();
            sb.AppendLine("# DRY-RUN — изменения ещё НЕ применены");
            sb.AppendLine();
            foreach (var line in _lastPlan)
                sb.AppendLine(string.IsNullOrWhiteSpace(line.Detail) ? line.ToLogLine() : line.Detail);
            PlanLogBox.Text = sb.ToString();
            SetRunMode("dry-run · ничего не изменено", "план готов", dryRunStyle: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string StripDomainPrefix(string name)
    {
        var i = name.IndexOf('\\');
        return i >= 0 && i < name.Length - 1 ? name[(i + 1)..] : name;
    }

    private static void WaitUntilGroupVisible(AdDirectoryService ad, string groupName, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (ad.GroupExists(groupName))
                {
                    Thread.Sleep(1000);
                    return;
                }
            }
            catch
            {
                // retry
            }

            Thread.Sleep(400);
        }
    }

    private static bool IsOurDomainWindowsPrincipal(string principalName, string domainDns)
    {
        if (string.IsNullOrWhiteSpace(principalName) || !principalName.Contains('\\'))
            return false;

        var domainPart = principalName[..principalName.IndexOf('\\')].Trim();
        if (domainPart.Length == 0)
            return false;

        if (domainPart.Equals("BUILTIN", StringComparison.OrdinalIgnoreCase) ||
            domainPart.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) ||
            domainPart.Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase) ||
            domainPart.Equals("APPLICATION PACKAGE AUTHORITY", StringComparison.OrdinalIgnoreCase) ||
            domainPart.Equals("Window Manager", StringComparison.OrdinalIgnoreCase))
            return false;

        var dns = (domainDns ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dns))
            return true;

        if (domainPart.Equals(dns, StringComparison.OrdinalIgnoreCase))
            return true;

        var netbios = dns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(netbios) &&
               domainPart.Equals(netbios, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictAction(PlannedAction a) =>
        !string.IsNullOrWhiteSpace(a.Detail) &&
        a.Detail.StartsWith("[CONFLICT]", StringComparison.Ordinal);

    private static bool IsSkipAction(PlannedAction a) =>
        a.Kind == PlanActionKind.SkipExistingGroup ||
        (!string.IsNullOrWhiteSpace(a.Detail) &&
         (a.Detail.StartsWith("[SKIP]", StringComparison.Ordinal) ||
          a.Detail.StartsWith("[AD] SKIP", StringComparison.Ordinal) ||
          a.Detail.StartsWith("[LOCAL] SKIP", StringComparison.Ordinal) ||
          a.Detail.StartsWith("[INFO]", StringComparison.Ordinal) ||
          a.Detail.StartsWith("[CONFLICT]", StringComparison.Ordinal)));

    private static AppSettings CloneSettings(AppSettings s) => new()
    {
        DomainDns = s.DomainDns,
        LdapHost = s.LdapHost,
        Username = s.Username,
        DefaultContainerDn = s.DefaultContainerDn,
        GroupsOuDn = s.GroupsOuDn,
        GroupNameTemplate = s.GroupNameTemplate,
        GroupDescriptionTemplate = s.GroupDescriptionTemplate,
        SqlGroupNameTemplate = s.SqlGroupNameTemplate,
        SqlGroupDescriptionTemplate = s.SqlGroupDescriptionTemplate,
        DomainNetBios = s.DomainNetBios,
        SqlMigrateDomainSysadmins = s.SqlMigrateDomainSysadmins,
        OperatingSystemContains = s.OperatingSystemContains,
        IncludeNestedContainers = s.IncludeNestedContainers,
        CreateMissingGroups = s.CreateMissingGroups,
        AddGroupToLocalAdministrators = s.AddGroupToLocalAdministrators,
        MigrateDirectMembersToGroup = s.MigrateDirectMembersToGroup,
        ExpandNestedGroupsOnMigrate = s.ExpandNestedGroupsOnMigrate,
        RemoveNonTargetMembers = s.RemoveNonTargetMembers,
        AlwaysKeepMembers = s.AlwaysKeepMembers,
        WindowWidth = s.WindowWidth,
        WindowHeight = s.WindowHeight,
        WindowLeft = s.WindowLeft,
        WindowTop = s.WindowTop,
        WindowState = s.WindowState,
        LogPanelHeight = s.LogPanelHeight
    };

    private static SessionCredentials CloneCredentials(SessionCredentials c)
    {
        var copy = new SessionCredentials();
        if (c.HasExplicitCredentials)
            copy.UseExplicit(c.Username, c.Password);
        else
            copy.UseIntegrated();
        return copy;
    }

    private void SetBusy(bool busy)
    {
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        IsEnabled = !busy;
    }

    private static bool IsSameGroup(string memberName, string groupName)
    {
        var slash = memberName.LastIndexOf('\\');
        var shortName = slash >= 0 ? memberName[(slash + 1)..] : memberName;
        return string.Equals(shortName, groupName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LocalProbeSnapshot
    {
        public string ComputerName { get; init; } = string.Empty;
        public string RemoteHost { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
        public IReadOnlyList<string> MembersNow { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MembersAfterLocal { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MigrateLeaves { get; init; } = Array.Empty<string>();
        public string? Error { get; init; }

        public string Display =>
            Error is null
                ? ComputerName
                : $"{ComputerName} · ошибка";
    }
}
