using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Services;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class SshFilesView : UserControl
{
    private SshFilesViewModel? _vm;

    public SshFilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SshFilesViewModel old)
            {
                old.TerminalWrite -= OnTerm;
                old.ClearInput -= OnClearInput;
                old.FocusInput -= OnFocusInput;
            }

            if (e.NewValue is not SshFilesViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.TerminalWrite += OnTerm;
            vm.ClearInput += OnClearInput;
            vm.FocusInput += OnFocusInput;
            vm.Sudo = IsSudo();
        };
    }

    private void OnTerm(string text, bool replace)
    {
        if (replace) SshTermOut.Text = text;
        else SshTermOut.AppendText(text);
        SshTermOut.CaretIndex = SshTermOut.Text.Length;
        SshTermOut.ScrollToEnd();
    }

    private void OnClearInput() => SshTermInput.Clear();

    private void OnFocusInput()
    {
        SshTermInput.Focus();
        Keyboard.Focus(SshTermInput);
    }

    private bool IsSudo() =>
        SshPrivCombo.SelectedItem is ComboBoxItem item &&
        string.Equals(item.Content?.ToString(), "sudo", StringComparison.OrdinalIgnoreCase);

    private void PushSelection()
    {
        if (_vm is null) return;
        _vm.SelectedLocal = LocalFilesList.SelectedItem as FileEntry;
        _vm.SelectedRemote = RemoteFilesList.SelectedItem as FileEntry;
        _vm.Sudo = IsSudo();
    }

    private void SshConnect_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.Connect();
    }

    private void SshRefresh_Click(object sender, RoutedEventArgs e) => _vm?.Refresh();
    private void LocalUp_Click(object sender, RoutedEventArgs e) => _vm?.LocalUp();
    private void RemoteUp_Click(object sender, RoutedEventArgs e) => _vm?.RemoteUp();

    private void SshPrivCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _vm is null) return;
        _vm.Sudo = IsSudo();
    }

    private void LocalFilesList_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.ActiveLocal = true;
    }

    private void RemoteFilesList_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.ActiveLocal = false;
    }

    private void LocalPathBox_LostFocus(object sender, RoutedEventArgs e) => ApplyLocalPath();

    private void LocalPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyLocalPath();
    }

    private void ApplyLocalPath() => _vm?.ApplyTypedPath(LocalPathBox.Text);

    private void LocalFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        PushSelection();
        _vm?.OpenLocal(LocalFilesList.SelectedItem as FileEntry);
    }

    private void RemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        PushSelection();
        _vm?.OpenRemote(RemoteFilesList.SelectedItem as FileEntry);
    }

    private void SshUpload_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.Upload();
    }

    private void SshDownload_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.Download();
    }

    private void SshDelete_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.DeleteRemote();
    }

    private void SshTermRun_Click(object sender, RoutedEventArgs e) => RunTerminal();

    private void SshTermInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunTerminal();
    }

    private void RunTerminal()
    {
        PushSelection();
        _vm?.RunTerminal(SshTermInput.Text);
    }

    private void PanelSsh_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } tb &&
            tb != SshTermInput && e.Key is not (>= Key.F1 and <= Key.F10))
            return;
        if (Keyboard.FocusedElement == SshTermInput && e.Key is not (>= Key.F1 and <= Key.F10))
            return;

        switch (e.Key)
        {
            case Key.F3: SshF3_Click(sender, e); e.Handled = true; break;
            case Key.F4: SshF4_Click(sender, e); e.Handled = true; break;
            case Key.F5: SshF5_Click(sender, e); e.Handled = true; break;
            case Key.F6: SshF6_Click(sender, e); e.Handled = true; break;
            case Key.F7: SshF7_Click(sender, e); e.Handled = true; break;
            case Key.F8: SshF8_Click(sender, e); e.Handled = true; break;
            case Key.F9: SshTermFocus_Click(sender, e); e.Handled = true; break;
            case Key.F10: SshF10_Click(sender, e); e.Handled = true; break;
        }
    }

    private void SshF3_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.ViewFile();
    }

    private void SshF4_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.EditFile();
    }

    private void SshF5_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.CopyActive();
    }

    private void SshF6_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.RenameActive();
    }

    private void SshF7_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.MkDir();
    }

    private void SshF8_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.DeleteActive();
    }

    private void SshTermFocus_Click(object sender, RoutedEventArgs e) => OnFocusInput();

    private void SshF10_Click(object sender, RoutedEventArgs e) => _vm?.Disconnect();
}
