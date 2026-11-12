using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>
/// SSH Files: список хостов слева и вкладки, как в WinSCP. У каждой вкладки своё SFTP-соединение,
/// свои папки, правки F4 и передачи. Хотя бы одна вкладка есть всегда.
/// </summary>
public partial class FilesTabsView : UserControl
{
    private AppSession? _session;
    private SessionTab? _active;

    public FilesTabsView()
    {
        InitializeComponent();
        HostsPanel.OtherHeader = "Открыть в терминале";
        HostsPanel.HostSelected += host =>
        {
            // Пустая вкладка «запоминает» выбранный хост — для F9 и «Подключить».
            if (host is not null && ActiveVm is { Connected: false } vm) vm.SelectedHost = host;
        };
        HostsPanel.HostActivated += async host => await ConnectTo(host);
        HostsPanel.NewRequested += async host => await ConnectTo(host, newTab: true);
        HostsPanel.OtherRequested += host => OpenTerminalRequested?.Invoke(host, null);
        Strip.AddTip = "Новая вкладка (Ctrl+T)";
        Strip.Selected += Activate;
        Strip.CloseRequested += tab => Close(tab);
        Strip.DuplicateRequested += async tab =>
        {
            if (((SshFilesViewModel)tab.Vm).ConnectedHost is { } h) await ConnectTo(h, newTab: true);
            else NewTab();
        };
        Strip.AddRequested += () => NewTab();
    }

    /// <summary>Терминал на хосте (меню хоста, F9). Путь — папка, куда перейти.</summary>
    public event Action<HostConfig, string?>? OpenTerminalRequested;

    private SshFilesViewModel? ActiveVm => _active?.Vm as SshFilesViewModel;

    public int Count => Strip.Tabs.Count;

    public void Init(AppSession session)
    {
        _session = session;
        HostsPanel.Bind(session);
        HostsPanel.Select(session.Hosts.FirstOrDefault(h => h.IsSelected) ?? session.Hosts.FirstOrDefault());
        NewTab();
    }

    /// <summary>Первый заход на вкладку: локальная панель — «Документы».</summary>
    public void EnterDocuments()
    {
        ActiveVm?.EnterDocuments();
        if (ActiveVm?.SelectedHost is { } h) HostsPanel.Select(h);
    }

    /// <summary>
    /// Открыть хост. Есть вкладка с ним — перейти. Текущая вкладка пустая — подключить в ней.
    /// Иначе (или newTab) — новая вкладка.
    /// </summary>
    public async Task ConnectTo(HostConfig host, bool newTab = false)
    {
        if (_session is null) return;
        HostsPanel.Select(host);
        SessionTab? tab = null;
        if (!newTab)
        {
            tab = Strip.Tabs.FirstOrDefault(t => ReferenceEquals(((SshFilesViewModel)t.Vm).ConnectedHost, host));
            if (tab is not null)
            {
                Activate(tab);
                return;
            }

            if (ActiveVm is { Connected: false, Transferring: false }) tab = _active;
        }

        tab ??= NewTab();
        Activate(tab);
        await ((SshFilesView)tab.View).ConnectTo(host);
    }

    private SessionTab NewTab()
    {
        var vm = new SshFilesViewModel(_session!);
        if (ActiveVm is { } current) vm.LocalPath = current.LocalPath;
        vm.SelectedHost = HostsPanel.SelectedHost;
        vm.Init();
        var view = new SshFilesView { DataContext = vm, Visibility = Visibility.Collapsed };
        var tab = new SessionTab(view, vm);
        view.ConnectRequested += async () => await OnConnectRequested(tab);
        vm.OpenTerminalRequested += (h, path) => OpenTerminalRequested?.Invoke(h, path);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SshFilesViewModel.Connected) or nameof(SshFilesViewModel.ConnectedHost)
                or nameof(SshFilesViewModel.Transferring) or nameof(SshFilesViewModel.TransferStatus)
                or nameof(SshFilesViewModel.RemotePath))
                Sync(tab);
        };
        Pages.Children.Add(view);
        Strip.Tabs.Add(tab);
        Sync(tab);
        Activate(tab);
        return tab;
    }

    /// <summary>Кнопка «Подключить» на вкладке: хост из списка слева (или запомненный вкладкой).</summary>
    private async Task OnConnectRequested(SessionTab tab)
    {
        var vm = (SshFilesViewModel)tab.Vm;
        var host = HostsPanel.SelectedHost ?? vm.SelectedHost;
        if (host is null)
        {
            AppDialog.Info(Window.GetWindow(this)!, "Выберите хост в списке слева.", "SSH Files");
            return;
        }

        if (vm.Connected)
        {
            if (!ReferenceEquals(vm.ConnectedHost, host)) await ConnectTo(host);
            return;
        }

        await ((SshFilesView)tab.View).ConnectTo(host);
    }

    private void Activate(SessionTab tab)
    {
        if (ReferenceEquals(_active, tab)) return;
        if (_active is not null)
        {
            _active.IsActive = false;
            _active.View.Visibility = Visibility.Collapsed;
        }

        _active = tab;
        tab.IsActive = true;
        tab.View.Visibility = Visibility.Visible;
        var vm = (SshFilesViewModel)tab.Vm;
        if ((vm.ConnectedHost ?? vm.SelectedHost) is { } h) HostsPanel.Select(h);
        Strip.BringIntoView(tab);
    }

    /// <summary>Закрыть вкладку (правки F4 и передачи — с вопросом). Последнюю заменяет пустая.</summary>
    public bool Close(SessionTab tab)
    {
        var vm = (SshFilesViewModel)tab.Vm;
        if (!vm.Close()) return false;
        var index = Strip.Tabs.IndexOf(tab);
        vm.Dispose();
        Strip.Tabs.Remove(tab);
        Pages.Children.Remove(tab.View);
        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            if (Strip.Tabs.Count > 0) Activate(Strip.Tabs[Math.Min(index, Strip.Tabs.Count - 1)]);
        }

        if (Strip.Tabs.Count == 0)
        {
            // Пустая вкладка сохраняет папку закрытой.
            var keep = vm.LocalPath;
            var fresh = NewTab();
            var freshVm = (SshFilesViewModel)fresh.Vm;
            freshVm.LocalPath = keep;
            freshVm.ReloadLocal();
        }

        Retitle();
        return true;
    }

    public void DisposeAll()
    {
        foreach (var tab in Strip.Tabs.ToList()) ((SshFilesViewModel)tab.Vm).Dispose();
    }

    private void Sync(SessionTab tab)
    {
        var vm = (SshFilesViewModel)tab.Vm;
        tab.Host = vm.ConnectedHost;
        tab.Connected = vm.Connected;
        tab.Busy = vm.Transferring || vm.TransferStatus.StartsWith("подключение", StringComparison.Ordinal);
        tab.Tip = vm.ConnectedHost is { } h
            ? $"{h.Display}\n{vm.RemotePath}" + (vm.Transferring ? "\n" + vm.TransferStatus : "")
            : "Не подключено — дважды щёлкните хост слева";
        Retitle();
    }

    private void Retitle()
    {
        foreach (var group in Strip.Tabs.GroupBy(t => t.Host))
        {
            var n = 0;
            foreach (var tab in group)
            {
                n++;
                if (tab.Host is null)
                {
                    tab.Title = "Новая вкладка";
                    continue;
                }

                tab.Title = n == 1 ? tab.Host.Title : $"{tab.Host.Title} ({n})";
            }
        }
    }

    private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var newTab = key == Key.T && mods == ModifierKeys.Control;
        if (!newTab && !SshTerminalView.IsTabShortcut(key, mods)) return;
        e.Handled = true;
        if (key == Key.Tab)
        {
            if (Strip.Tabs.Count < 2 || _active is null) return;
            var step = mods.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            var i = (Strip.Tabs.IndexOf(_active) + step + Strip.Tabs.Count) % Strip.Tabs.Count;
            Activate(Strip.Tabs[i]);
        }
        else if (key == Key.W)
        {
            if (_active is not null) Close(_active);
        }
        else if (key == Key.T)
        {
            if (mods.HasFlag(ModifierKeys.Shift) && ActiveVm?.ConnectedHost is { } h) await ConnectTo(h, newTab: true);
            else NewTab();
        }
    }
}
