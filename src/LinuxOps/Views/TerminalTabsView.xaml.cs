using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>SSH Terminal: список хостов слева и вкладки сессий. У каждой вкладки своё соединение и свой экран.</summary>
public partial class TerminalTabsView : UserControl
{
    private AppSession? _session;
    private SessionTab? _active;

    public TerminalTabsView()
    {
        InitializeComponent();
        HostsPanel.OtherHeader = "Открыть в SSH Files";
        HostsPanel.HostActivated += async host => await Open(host);
        HostsPanel.NewRequested += async host => await Open(host, newTab: true);
        HostsPanel.OtherRequested += host => OpenFilesRequested?.Invoke(host);
        Strip.AddTip = "Новая сессия к выбранному хосту (Ctrl+Shift+T)";
        Strip.Selected += Activate;
        Strip.CloseRequested += tab => Close(tab);
        Strip.DuplicateRequested += async tab =>
        {
            if (tab.Host is { } h) await Open(h, newTab: true);
        };
        Strip.AddRequested += async () => await OpenSelected();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_active?.View is SshTerminalView v && ((SshTerminalViewModel)_active.Vm).Connected) v.FocusTerminal();
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
        HostsPanel.Bind(session.Hosts);
        HostsPanel.Select(session.Hosts.FirstOrDefault(h => h.IsSelected) ?? session.Hosts.FirstOrDefault());
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
        Pages.Children.Add(view);
        Strip.Tabs.Add(tab);
        Retitle();
        Sync(tab);
        Activate(tab);
        UpdateEmpty();
        await vm.UseHost(host, cdPath);
        if (vm.Connected && ReferenceEquals(_active, tab)) view.FocusTerminal();
    }

    private async Task OpenSelected()
    {
        var host = HostsPanel.SelectedHost ?? _active?.Host;
        if (host is null)
        {
            AppDialog.Info(Window.GetWindow(this)!, "Выберите хост в списке слева.", "SSH Terminal");
            return;
        }

        await Open(host, newTab: true);
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
        if (tab.Host is not null) HostsPanel.Select(tab.Host);
        Strip.BringIntoView(tab);
        Focus(tab);
    }

    private void Focus(SessionTab tab)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_active, tab) && ((SshTerminalViewModel)tab.Vm).Connected)
                ((SshTerminalView)tab.View).FocusTerminal();
        }, DispatcherPriority.Input);
    }

    /// <summary>Закрыть вкладку. Живую сессию — после вопроса. false — пользователь передумал.</summary>
    public bool Close(SessionTab tab, bool ask = true)
    {
        var vm = (SshTerminalViewModel)tab.Vm;
        if (ask && vm.Connected &&
            !AppDialog.Confirm(Window.GetWindow(this)!, $"Закрыть сессию {tab.Title}?", "SSH Terminal"))
            return false;
        var index = Strip.Tabs.IndexOf(tab);
        vm.Dispose();
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

    public void DisposeAll()
    {
        foreach (var tab in Strip.Tabs.ToList()) ((SshTerminalViewModel)tab.Vm).Dispose();
    }

    private void Sync(SessionTab tab)
    {
        var vm = (SshTerminalViewModel)tab.Vm;
        tab.Connected = vm.Connected;
        tab.Busy = !vm.Connected && vm.Status.EndsWith('…');
        tab.Tip = vm.Status;
    }

    /// <summary>Подписи: имя хоста, а у второй и следующих сессий того же хоста — номер.</summary>
    private void Retitle()
    {
        foreach (var group in Strip.Tabs.GroupBy(t => t.Host))
        {
            var n = 0;
            foreach (var tab in group)
            {
                n++;
                var name = tab.Host?.Title ?? "сессия";
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
            if (_active?.Host is { } h) await Open(h, newTab: true);
            else await OpenSelected();
        }
    }
}
