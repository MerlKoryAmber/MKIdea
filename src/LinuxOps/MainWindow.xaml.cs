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
        _history = new HistoryViewModel();
        Actions.DataContext = _actions;
        Inventory.DataContext = inventory;
        History.DataContext = _history;
        Ssh.Init(session);
        Terminal.Init(session);
        // F9 с папкой — новая вкладка терминала в этой папке; без папки — вкладка хоста (своя или новая).
        Ssh.OpenTerminalRequested += async (host, path) =>
        {
            ShowMode("terminal");
            await Terminal.Open(host, cdPath: path);
        };
        Terminal.OpenFilesRequested += async host =>
        {
            ShowMode("ssh");
            await Ssh.ConnectTo(host);
        };
        _actions.Load();
        FooterVersionText.Text = $"v{GetVersion()}";
        ShowMode("actions");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeManager.SetTitleBar(this);
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Views.SettingsWindow.Show(this);

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
            Ssh.EnterDocuments();
            _sshDocsOnce = true;
        }

        if (mode == "history")
            _history.Reload();
    }

    protected override void OnClosed(EventArgs e)
    {
        Ssh.DisposeAll();
        Terminal.DisposeAll();
        base.OnClosed(e);
    }

    private static string GetVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
