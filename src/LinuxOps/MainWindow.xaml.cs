using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LinuxOps.ViewModels;

namespace LinuxOps;

public partial class MainWindow : Window
{
    private readonly ActionsViewModel _actions;
    private readonly SshFilesViewModel _ssh;
    private readonly SshTerminalViewModel _terminal;
    private readonly HistoryViewModel _history;
    private bool _modeSwitching;
    private bool _sshDocsOnce;

    public MainWindow()
    {
        InitializeComponent();
        var session = new AppSession();
        session.Reload();
        _actions = new ActionsViewModel(session);
        var inventory = new InventoryViewModel(session);
        _ssh = new SshFilesViewModel(session);
        _terminal = new SshTerminalViewModel(session);
        _history = new HistoryViewModel();
        Actions.DataContext = _actions;
        Inventory.DataContext = inventory;
        Ssh.DataContext = _ssh;
        Terminal.DataContext = _terminal;
        History.DataContext = _history;
        _ssh.OpenTerminalRequested += host =>
        {
            if (host is not null) _terminal.UseHost(host);
            ShowMode("terminal");
        };
        _actions.Load();
        _ssh.Init();
        FooterVersionText.Text = $"v{GetVersion()}";
        ShowMode("actions");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void ModeChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_modeSwitching || sender is not ToggleButton chip || chip.Tag is not string mode)
            return;
        ShowMode(mode);
    }

    private void ModeChip_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_modeSwitching) return;
        if (ChipInventory.IsChecked == true || ChipActions.IsChecked == true ||
            ChipSsh.IsChecked == true || ChipTerminal.IsChecked == true || ChipHistory.IsChecked == true)
            return;
        _modeSwitching = true;
        ((ToggleButton)sender).IsChecked = true;
        _modeSwitching = false;
    }

    private void ShowMode(string mode)
    {
        _modeSwitching = true;
        ChipInventory.IsChecked = mode == "inventory";
        ChipActions.IsChecked = mode == "actions";
        ChipSsh.IsChecked = mode == "ssh";
        ChipTerminal.IsChecked = mode == "terminal";
        ChipHistory.IsChecked = mode == "history";
        _modeSwitching = false;

        Inventory.Visibility = mode == "inventory" ? Visibility.Visible : Visibility.Collapsed;
        Actions.Visibility = mode == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Ssh.Visibility = mode == "ssh" ? Visibility.Visible : Visibility.Collapsed;
        Terminal.Visibility = mode == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        History.Visibility = mode == "history" ? Visibility.Visible : Visibility.Collapsed;
        var actions = mode == "actions";
        FooterBar.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        ActionsFooter.Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        DefaultsPathText.Text = actions ? "Actions · отметьте хосты и действия, затем Dry-run" : "";

        if (mode == "ssh" && !_sshDocsOnce)
        {
            _ssh.EnterDocuments();
            _sshDocsOnce = true;
        }

        if (mode == "history")
            _history.Reload();
    }

    protected override void OnClosed(EventArgs e)
    {
        _ssh.Dispose();
        _terminal.Dispose();
        base.OnClosed(e);
    }

    private static string GetVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
