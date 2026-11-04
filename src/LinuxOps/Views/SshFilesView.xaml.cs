using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Services;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class SshFilesView : UserControl
{
    private SshFilesViewModel? _vm;
    private Point _dragStart;

    public SshFilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not SshFilesViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.ShowText += ShowText;
            vm.Sudo = IsSudo();
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(SshFilesViewModel.Connected)
                    or nameof(SshFilesViewModel.ActiveLocal)
                    or nameof(SshFilesViewModel.SelectedHost))
                    Dispatcher.BeginInvoke(RefreshKeys);
            };
            RefreshKeys();
        };
    }

    private void RefreshKeys()
    {
        if (_vm is null) return;
        var on = _vm.Connected;
        var local = _vm.ActiveLocal;
        KeyF3.IsEnabled = local || on;
        KeyF4.IsEnabled = local || on;
        KeyF5.IsEnabled = on;
        KeyF6.IsEnabled = local || on;
        KeyF7.IsEnabled = local || on;
        KeyF8.IsEnabled = local || on;
        KeyF9.IsEnabled = _vm.SelectedHost is not null;
        KeyF10.IsEnabled = on;
    }

    private void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0)
        };
        var win = new Window
        {
            Title = title,
            Width = 760,
            Height = 560,
            Owner = Window.GetWindow(this),
            Content = box,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.Show();
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

    private async void SshConnect_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.Connect();
    }

    private async void SshRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.Refresh();
    }

    private void LocalUp_Click(object sender, RoutedEventArgs e) => _vm?.LocalUp();

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteUp();
    }

    private async void RemoteBack_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteBack();
    }

    private async void RemoteForward_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteForward();
    }

    private async void RemotePathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null) return;
        e.Handled = true;
        await _vm.GoRemote(RemotePathBox.Text);
    }

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

    private async void RemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.OpenRemote(RemoteFilesList.SelectedItem as FileEntry);
    }

    private async void SshUpload_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is null) return;
        await _vm.UploadPaths(LocalPaths());
    }

    private async void SshDownload_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is null) return;
        await _vm.DownloadEntries(RemoteEntries());
    }

    private async void SshDelete_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.DeleteRemote();
    }

    private void PanelSsh_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Visibility != Visibility.Visible) return;
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false } && e.Key is not (>= Key.F1 and <= Key.F10))
            return;

        switch (e.Key)
        {
            case Key.F1: SshF1_Click(sender, e); e.Handled = true; break;
            case Key.F3: SshF3_Click(sender, e); e.Handled = true; break;
            case Key.F4: SshF4_Click(sender, e); e.Handled = true; break;
            case Key.F5: SshF5_Click(sender, e); e.Handled = true; break;
            case Key.F6: SshF6_Click(sender, e); e.Handled = true; break;
            case Key.F7: SshF7_Click(sender, e); e.Handled = true; break;
            case Key.F8: SshF8_Click(sender, e); e.Handled = true; break;
            case Key.F9: SshF9_Click(sender, e); e.Handled = true; break;
            case Key.F10: SshF10_Click(sender, e); e.Handled = true; break;
        }
    }

    private void SshF1_Click(object sender, RoutedEventArgs e)
    {
        LinuxOps.AppDialog.Info(Window.GetWindow(this)!,
            "F1 Помощь\nF3 Просмотр\nF4 Правка\nF5 Копировать\nF6 Переименовать\nF7 Папка\nF8 Удалить\nF9 Терминал\nF10 Отключить",
            "Клавиши");
    }

    private async void SshF3_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.ViewFile();
    }

    private async void SshF4_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.EditFile();
    }

    private async void SshF5_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is null) return;
        if (_vm.ActiveLocal) await _vm.UploadPaths(LocalPaths());
        else await _vm.DownloadEntries(RemoteEntries());
    }

    private async void SshF6_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.RenameActive();
    }

    private async void SshF7_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.MkDir();
    }

    private async void SshF8_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        if (_vm is not null) await _vm.DeleteActive();
    }

    private List<string> LocalPaths() =>
        LocalFilesList.SelectedItems.Cast<FileEntry>()
            .Where(f => !f.IsDirectory && !f.IsParentNav)
            .Select(f => f.FullPath)
            .ToList();

    private List<FileEntry> RemoteEntries() =>
        RemoteFilesList.SelectedItems.Cast<FileEntry>().ToList();

    private void LocalDrag_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void RemoteDrag_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void LocalDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(null) - _dragStart).Length < 8) return;
        var files = LocalPaths().ToArray();
        if (files.Length == 0) return;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, files);
        data.SetData("LinuxOpsLocal", files);
        DragDrop.DoDragDrop(LocalFilesList, data, DragDropEffects.Copy);
    }

    private void RemoteDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if ((e.GetPosition(null) - _dragStart).Length < 8) return;
        var files = RemoteEntries().Where(f => !f.IsDirectory && !f.IsParentNav).Select(f => f.FullPath).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject("LinuxOpsRemote", files);
        DragDrop.DoDragDrop(RemoteFilesList, data, DragDropEffects.Copy);
    }

    private void Local_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("LinuxOpsRemote") || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Local_Drop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        if (e.Data.GetData("LinuxOpsRemote") is string[] remote && remote.Length > 0)
        {
            var entries = remote.Select(p => new FileEntry { Name = Path.GetFileName(p), FullPath = p }).ToList();
            await _vm.DownloadEntries(entries);
            return;
        }

        if (e.Data.GetDataPresent("LinuxOpsLocal")) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            foreach (var src in dropped.Where(File.Exists))
            {
                var dest = Path.Combine(_vm.LocalPath, Path.GetFileName(src));
                if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
                    File.Copy(src, dest, true);
            }

            _vm.ReloadLocal();
        }
    }

    private void Remote_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("LinuxOpsLocal")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Remote_Drop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        string[]? files = null;
        if (e.Data.GetData("LinuxOpsLocal") is string[] local) files = local;
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) files = dropped;
        if (files is { Length: > 0 })
            await _vm.UploadPaths(files);
    }

    private void SshF9_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.OpenTerminal();
    }

    private void PickEditor_Click(object sender, RoutedEventArgs e)
    {
        var next = TextPrompt.Ask(Window.GetWindow(this), "Редактор для F4", "Программа:", DefaultsStore.F4Editor);
        if (!string.IsNullOrWhiteSpace(next))
            DefaultsStore.F4Editor = next;
    }

    private void CloseEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RemoteEditSession session })
            _vm?.CloseEdit(session);
    }

    private void SshF10_Click(object sender, RoutedEventArgs e)
    {
        _vm?.Disconnect();
        RefreshKeys();
    }
}
