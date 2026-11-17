using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>
/// «Подключения»: хосты Linux и Windows слева, вкладки сессий справа. Хост Linux — терминал SSH,
/// хост Windows — рабочий стол RDP. У каждой вкладки своё соединение.
/// </summary>
public partial class TerminalTabsView : UserControl
{
    private AppSession? _session;
    private SessionTab? _active;

    public TerminalTabsView()
    {
        InitializeComponent();
        HostsPanel.Scope = "Connect";
        HostsPanel.OtherHeader = "Открыть в SSH Files";
        HostsPanel.HostActivated += async host => await Open(host);
        HostsPanel.WindowsActivated += win => OpenRdp(win);
        HostsPanel.NewRequested += async item => await OpenNew(item);
        HostsPanel.OtherRequested += host => OpenFilesRequested?.Invoke(host);
        Strip.AddTip = "Новая сессия к выбранному хосту (Ctrl+Shift+T)";
        Strip.Selected += Activate;
        Strip.CloseRequested += tab => Close(tab);
        Strip.DuplicateRequested += async tab =>
        {
            if (tab.Resource is { } item) await OpenNew(item);
        };
        Strip.AddRequested += async () => await OpenSelected();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_active is not null && IsConnected(_active)) FocusView(_active);
                else HostsPanel.FocusSearch();
            }, DispatcherPriority.Input);
        };
        UpdateEmpty();
    }

    /// <summary>«Открыть в SSH Files» — из меню хоста или кнопки вкладки.</summary>
    public event Action<HostConfig>? OpenFilesRequested;

    public int Count => Strip.Tabs.Count;

    public void Init(AppSession session)
    {
        _session = session;
        HostsPanel.Bind(session);
        HostsPanel.Select(session.Hosts.FirstOrDefault(h => h.IsSelected) ?? session.Hosts.FirstOrDefault());
    }

    private Task OpenNew(IInventoryItem item) => item switch
    {
        HostConfig h => Open(h, newTab: true),
        WindowsHost w => OpenRdpTask(w),
        _ => Task.CompletedTask
    };

    private Task OpenRdpTask(WindowsHost host)
    {
        OpenRdp(host, newTab: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Открыть хост. Уже есть вкладка этого хоста — перейти на неё (и переподключить, если сессия закрыта).
    /// newTab — всегда новая сессия. cdPath — новая вкладка сразу в этой папке (F9 из SSH Files).
    /// </summary>
    public async Task Open(HostConfig host, bool newTab = false, string? cdPath = null)
    {
        if (_session is null) return;
        HostsPanel.Select(host);
        var withPath = !string.IsNullOrWhiteSpace(cdPath);
        if (!newTab && !withPath && Strip.Tabs.FirstOrDefault(t => ReferenceEquals(t.Host, host)) is { } existing)
        {
            Activate(existing);
            var vmOld = (SshTerminalViewModel)existing.Vm;
            if (!vmOld.Connected) await vmOld.Connect(host, confirmSwitch: false);
            if (vmOld.Connected) ((SshTerminalView)existing.View).FocusTerminal();
            return;
        }

        var vm = new SshTerminalViewModel(_session) { SelectedHost = host };
        var view = new SshTerminalView { DataContext = vm, Visibility = Visibility.Collapsed };
        view.OpenFilesRequested += h => OpenFilesRequested?.Invoke(h);
        var tab = new SessionTab(view, vm) { Host = host };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SshTerminalViewModel.Connected) or nameof(SshTerminalViewModel.Status))
                Sync(tab);
        };
        AddTab(tab);
        await vm.UseHost(host, cdPath);
        if (vm.Connected && ReferenceEquals(_active, tab)) view.FocusTerminal();
    }

    /// <summary>
    /// RDP к хосту Windows. Уже есть вкладка — перейти; если она отключена — переподключить с теми же учётными данными.
    /// Новая сессия — после окна подключения (пароль спрашивается каждый раз).
    /// </summary>
    public void OpenRdp(WindowsHost host, bool newTab = false)
    {
        if (_session is null) return;
        HostsPanel.Select(host);
        if (!newTab && Strip.Tabs.FirstOrDefault(t => ReferenceEquals(t.Rdp, host)) is { } existing)
        {
            Activate(existing);
            var old = (RdpSessionView)existing.View;
            if (!old.Vm.Connected && !old.Vm.Connecting) old.Connect();
            return;
        }

        var dlg = new RdpConnectWindow(host) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        var vm = new RdpSessionViewModel(host, dlg.Result);
        var view = new RdpSessionView(vm) { Visibility = Visibility.Collapsed };
        var tab = new SessionTab(view, vm) { Rdp = host };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RdpSessionViewModel.Connected) or nameof(RdpSessionViewModel.Status)
                or nameof(RdpSessionViewModel.Connecting))
                Sync(tab);
        };
        AddTab(tab);
        view.Connect();
    }

    private void AddTab(SessionTab tab)
    {
        Pages.Children.Add(tab.View);
        Strip.Tabs.Add(tab);
        Retitle();
        Sync(tab);
        Activate(tab);
        UpdateEmpty();
    }

    private async Task OpenSelected()
    {
        var item = HostsPanel.SelectedItem ?? _active?.Resource;
        if (item is null)
        {
            AppDialog.Info(Window.GetWindow(this)!, "Выберите хост в списке слева.", "Подключения");
            return;
        }

        await OpenNew(item);
    }

    private void Activate(SessionTab tab)
    {
        if (ReferenceEquals(_active, tab))
        {
            Focus(tab);
            return;
        }

        if (_active is not null)
        {
            _active.IsActive = false;
            _active.View.Visibility = Visibility.Collapsed;
        }

        _active = tab;
        tab.IsActive = true;
        tab.View.Visibility = Visibility.Visible;
        if (tab.Resource is { } item) HostsPanel.Select(item);
        Strip.BringIntoView(tab);
        Focus(tab);
    }

    private void Focus(SessionTab tab)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_active, tab) && IsConnected(tab)) FocusView(tab);
        }, DispatcherPriority.Input);
    }

    private static bool IsConnected(SessionTab tab) => tab.Vm switch
    {
        SshTerminalViewModel s => s.Connected,
        RdpSessionViewModel r => r.Connected,
        _ => false
    };

    private static void FocusView(SessionTab tab)
    {
        if (tab.View is SshTerminalView ssh) ssh.FocusTerminal();
        else if (tab.View is RdpSessionView rdp) rdp.FocusSession();
    }

    /// <summary>Закрыть вкладку. Живую сессию — после вопроса. false — пользователь передумал.</summary>
    public bool Close(SessionTab tab, bool ask = true)
    {
        if (ask && IsConnected(tab) &&
            !AppDialog.Confirm(Window.GetWindow(this)!, $"Закрыть сессию {tab.Title}?", "Подключения"))
            return false;
        var index = Strip.Tabs.IndexOf(tab);
        DisposeTab(tab);
        Strip.Tabs.Remove(tab);
        Pages.Children.Remove(tab.View);
        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            if (Strip.Tabs.Count > 0) Activate(Strip.Tabs[Math.Min(index, Strip.Tabs.Count - 1)]);
        }

        Retitle();
        UpdateEmpty();
        return true;
    }

    private static void DisposeTab(SessionTab tab)
    {
        if (tab.Vm is SshTerminalViewModel ssh) ssh.Dispose();
        if (tab.View is RdpSessionView rdp) rdp.Dispose();
    }

    public void DisposeAll()
    {
        foreach (var tab in Strip.Tabs.ToList()) DisposeTab(tab);
    }

    private static void Sync(SessionTab tab)
    {
        switch (tab.Vm)
        {
            case SshTerminalViewModel vm:
                tab.Connected = vm.Connected;
                tab.Busy = !vm.Connected && vm.Status.EndsWith('…');
                tab.Tip = vm.Status;
                break;
            case RdpSessionViewModel rdp:
                tab.Connected = rdp.Connected;
                tab.Busy = rdp.Connecting;
                tab.Tip = rdp.Status;
                break;
        }
    }

    /// <summary>Подписи: имя хоста, а у второй и следующих сессий того же хоста — номер.</summary>
    private void Retitle()
    {
        foreach (var group in Strip.Tabs.GroupBy(t => (object?)t.Resource))
        {
            var n = 0;
            foreach (var tab in group)
            {
                n++;
                var name = tab.Resource?.Title ?? "сессия";
                tab.Title = n == 1 ? name : $"{name} ({n})";
            }
        }
    }

    private void UpdateEmpty() => Empty.Visibility = Strip.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (!SshTerminalView.IsTabShortcut(key, mods)) return;
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
            if (_active?.Resource is { } item) await OpenNew(item);
            else await OpenSelected();
        }
    }
}
