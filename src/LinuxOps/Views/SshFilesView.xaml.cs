using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinuxOps.Models;
using LinuxOps.Services;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class SshFilesView : UserControl
{
    private const string RemoteDragFormat = "LinuxOpsRemote";
    private const string LocalDragFormat = "LinuxOpsLocal";

    private SshFilesViewModel? _vm;
    private Point _dragStart;
    private List<FileEntry> _dragRemote = new();

    public SshFilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SshFilesViewModel old)
            {
                old.ShowText -= ShowText;
                old.PropertyChanged -= OnVmChanged;
                old.SelectRequested -= OnSelectRequested;
            }

            if (e.NewValue is not SshFilesViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.ShowText += ShowText;
            vm.PropertyChanged += OnVmChanged;
            vm.SelectRequested += OnSelectRequested;
            vm.Sudo = IsSudo();
            // Счётчик «папок N, файлов M» — после каждой смены списка, одним проходом.
            vm.LocalFiles.CollectionChanged += (_, _) => QueueInfo();
            vm.RemoteFiles.CollectionChanged += (_, _) => QueueInfo();
            RefreshKeys();
            UpdateHeaders();
        };
    }

    private bool _infoQueued;

    private void QueueInfo()
    {
        if (_infoQueued) return;
        _infoQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _infoQueued = false;
            UpdateInfo();
        }, DispatcherPriority.Background);
    }

    /// <summary>Кнопка «Подключить»: решает вкладочный контейнер (какой хост и в какой вкладке).</summary>
    public event Action? ConnectRequested;

    /// <summary>Подключить эту вкладку к хосту.</summary>
    public async Task ConnectTo(HostConfig host)
    {
        if (_vm is null) return;
        PushSudo();
        await _vm.Connect(host);
        if (_vm.Connected) FocusList(RemoteFilesList);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SshFilesViewModel.Connected):
            case nameof(SshFilesViewModel.ActiveLocal):
                Dispatcher.BeginInvoke(RefreshKeys);
                break;
            case nameof(SshFilesViewModel.SelectedHost):
                Dispatcher.BeginInvoke(RefreshKeys);
                break;
            case nameof(SshFilesViewModel.LocalSort):
            case nameof(SshFilesViewModel.LocalSortDesc):
            case nameof(SshFilesViewModel.RemoteSort):
            case nameof(SshFilesViewModel.RemoteSortDesc):
                UpdateHeaders();
                break;
        }
    }

    private void OnSelectRequested(bool local, string name)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var list = local ? LocalFilesList : RemoteFilesList;
            var item = list.Items.Cast<FileEntry>().FirstOrDefault(f => f.Name == name);
            if (item is null) return;
            list.SelectedItem = item;
            list.ScrollIntoView(item);
            FocusList(list, item);
        }, DispatcherPriority.Background);
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
        KeyF5.Content = local ? "F5 На сервер →" : "F5 ← На компьютер";
        RemotePlaceholder.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        LocalBorder.SetResourceReference(Border.BorderBrushProperty, local ? "AccentBrush" : "LineBrush");
        RemoteBorder.SetResourceReference(Border.BorderBrushProperty, local ? "LineBrush" : "AccentBrush");
        UpdateInfo();
    }

    private void UpdateHeaders()
    {
        if (_vm is null) return;
        SetHeaders(LocalFilesList, _vm.LocalSort, _vm.LocalSortDesc);
        SetHeaders(RemoteFilesList, _vm.RemoteSort, _vm.RemoteSortDesc);
    }

    private static void SetHeaders(ListView list, string key, bool desc)
    {
        if (list.View is not GridView grid) return;
        foreach (var col in grid.Columns)
        {
            if (col.Header is not GridViewColumnHeader h || h.Tag is not string tag) continue;
            var baseText = (h.Content?.ToString() ?? "").TrimEnd(' ', '▲', '▼');
            h.Content = tag == key ? baseText + (desc ? " ▼" : " ▲") : baseText;
        }
    }

    private void UpdateInfo()
    {
        LocalInfo.Text = Summary(LocalFilesList);
        RemoteInfo.Text = _vm?.Connected == true ? Summary(RemoteFilesList) : "";
    }

    private static string Summary(ListView list)
    {
        var all = list.Items.Cast<FileEntry>().Where(f => !f.IsParentNav).ToList();
        var sel = list.SelectedItems.Cast<FileEntry>().Where(f => !f.IsParentNav).ToList();
        var dirs = all.Count(f => f.IsDirectory);
        var text = $"папок {dirs}, файлов {all.Count - dirs}";
        if (sel.Count > 0)
            text += $" · выбрано {sel.Count}, {FileEntry.FormatSize(sel.Where(f => !f.IsDirectory).Sum(f => f.Size))}";
        return text;
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
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8)
        };
        var win = new Window
        {
            Title = "Просмотр — " + title,
            Width = 860,
            Height = 620,
            Owner = Window.GetWindow(this),
            Content = box,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        win.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.F3 or Key.F10) win.Close();
        };
        win.Show();
        box.Focus();
    }

    private bool IsSudo() =>
        SshPrivCombo.SelectedItem is ComboBoxItem item &&
        string.Equals(item.Content?.ToString(), "sudo", StringComparison.OrdinalIgnoreCase);

    private void PushSudo()
    {
        if (_vm is not null) _vm.Sudo = IsSudo();
    }

    private void PushSelection()
    {
        if (_vm is null) return;
        _vm.SelectedLocal = LocalFilesList.SelectedItem as FileEntry;
        _vm.SelectedRemote = RemoteFilesList.SelectedItem as FileEntry;
        _vm.Sudo = IsSudo();
    }

    private List<FileEntry> Selected(bool local) =>
        (local ? LocalFilesList : RemoteFilesList).SelectedItems.Cast<FileEntry>().Where(f => !f.IsParentNav).ToList();

    private List<FileEntry> ActiveSelection() => Selected(_vm?.ActiveLocal ?? true);

    private static void FocusList(ListView list, FileEntry? item = null)
    {
        item ??= list.SelectedItem as FileEntry ?? list.Items.Cast<FileEntry>().FirstOrDefault();
        if (item is null)
        {
            list.Focus();
            return;
        }

        if (list.SelectedItem is null) list.SelectedItem = item;
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem lvi) lvi.Focus();
        else list.Focus();
    }

    // ---------- Кнопки панели инструментов ----------

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectRequested?.Invoke();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.Refresh();
        UpdateInfo();
    }

    private void SshPrivCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _vm is null) return;
        _vm.Sudo = IsSudo();
        _vm.LastOperation = _vm.Sudo
            ? "режим sudo: если своих прав не хватит, операция выполнится от root"
            : "режим user: всё от имени пользователя SSH";
    }

    private void PickEditor_Click(object sender, RoutedEventArgs e)
    {
        var next = TextPrompt.Ask(Window.GetWindow(this), "Редактор для F4",
            "Программа (например notepad.exe или C:\\Program Files\\Notepad++\\notepad++.exe):", DefaultsStore.F4Editor);
        if (!string.IsNullOrWhiteSpace(next))
            DefaultsStore.F4Editor = next;
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _vm?.CancelTransfer();

    // ---------- Навигация ----------

    private void LocalUp_Click(object sender, RoutedEventArgs e) => _vm?.LocalUp();

    private void LocalHome_Click(object sender, RoutedEventArgs e) => _vm?.LocalHome();

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteUp();
    }

    private async void RemoteHome_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteHome();
    }

    private async void RemoteRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RemoteRoot();
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
        FocusList(RemoteFilesList);
    }

    private void LocalPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm is null) return;
        e.Handled = true;
        _vm.ApplyTypedPath(LocalPathBox.Text);
        FocusList(LocalFilesList);
    }

    private void LocalFilesList_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.ActiveLocal = true;
    }

    private void RemoteFilesList_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.ActiveLocal = false;
    }

    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInfo();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || e.OriginalSource is not GridViewColumnHeader { Tag: string key }) return;
        _vm.SortBy(ReferenceEquals(sender, LocalFilesList), key);
    }

    private void LocalFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsOnItem(LocalFilesList, e)) return;
        PushSelection();
        _vm?.OpenLocal(LocalFilesList.SelectedItem as FileEntry);
    }

    private async void RemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsOnItem(RemoteFilesList, e)) return;
        PushSelection();
        if (_vm is not null) await _vm.OpenRemote(RemoteFilesList.SelectedItem as FileEntry);
    }

    private static bool IsOnItem(ListView list, MouseButtonEventArgs e)
        => e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListViewItem;

    // ---------- Клавиши ----------

    private void PanelSsh_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Visibility != Visibility.Visible || _vm is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var inText = Keyboard.FocusedElement is TextBox { IsReadOnly: false };
        var inList = Keyboard.FocusedElement is DependencyObject fe &&
                     (IsInside(fe, LocalFilesList) || IsInside(fe, RemoteFilesList));

        // F-клавиши работают везде, кроме поля ввода с Shift/Ctrl.
        switch (key)
        {
            case Key.F1: SshF1_Click(sender, e); e.Handled = true; return;
            case Key.F2 when !inText: SshF6_Click(sender, e); e.Handled = true; return;
            case Key.F3: SshF3_Click(sender, e); e.Handled = true; return;
            case Key.F4 when mods == ModifierKeys.Shift: NewFile_Click(sender, e); e.Handled = true; return;
            case Key.F4: SshF4_Click(sender, e); e.Handled = true; return;
            case Key.F5: SshF5_Click(sender, e); e.Handled = true; return;
            case Key.F6: SshF6_Click(sender, e); e.Handled = true; return;
            case Key.F7: SshF7_Click(sender, e); e.Handled = true; return;
            case Key.F8: SshF8_Click(sender, e); e.Handled = true; return;
            case Key.F9: SshF9_Click(sender, e); e.Handled = true; return;
            case Key.F10: SshF10_Click(sender, e); e.Handled = true; return;
            case Key.R when mods == ModifierKeys.Control: Refresh_Click(sender, e); e.Handled = true; return;
            case Key.H when mods == ModifierKeys.Control: _vm.ShowHidden = !_vm.ShowHidden; e.Handled = true; return;
        }

        if (inText || !inList) return;
        var local = _vm.ActiveLocal;
        switch (key)
        {
            case Key.Enter when mods == ModifierKeys.Alt:
                Properties_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Enter:
                MenuOpen_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Back:
                if (local) _vm.LocalUp();
                else _ = _vm.RemoteUp();
                e.Handled = true;
                break;
            case Key.Delete:
                SshF8_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Tab:
                FocusList(local ? RemoteFilesList : LocalFilesList);
                e.Handled = true;
                break;
            case Key.Left when mods == ModifierKeys.Alt && !local:
                RemoteBack_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Right when mods == ModifierKeys.Alt && !local:
                RemoteForward_Click(sender, e);
                e.Handled = true;
                break;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var d = element; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }

    /// <summary>Пункт контекстного меню — делает активной панель, на которой открыто меню.</summary>
    private void TakeMenuPanel(object sender)
    {
        if (_vm is null || sender is not MenuItem mi) return;
        for (DependencyObject? d = mi; d is not null; d = LogicalTreeHelper.GetParent(d))
        {
            if (d is ContextMenu { Tag: string tag })
            {
                _vm.ActiveLocal = tag == "local";
                return;
            }
        }
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is null) return;
        if (_vm.ActiveLocal) _vm.OpenLocal(LocalFilesList.SelectedItem as FileEntry);
        else await _vm.OpenRemote(RemoteFilesList.SelectedItem as FileEntry);
    }

    private void SshF1_Click(object sender, RoutedEventArgs e)
    {
        AppDialog.Info(Window.GetWindow(this)!,
            "Хост слева: двойной щелчок или Enter — подключиться. Начните печатать — поиск.\n\n" +
            "Enter — открыть папку / править файл\nBackspace — вверх\nTab — другая панель\n" +
            "F3 — просмотр\nF4 — правка (Shift+F4 — новый файл)\nF5 — копировать в другую панель (папки целиком)\n" +
            "F2, F6 — переименовать или перенести (можно ввести путь)\nF7 — новая папка\nF8, Del — удалить\n" +
            "F9 — терминал в этой папке\nF10 — отключиться\nAlt+Enter — свойства и права\n" +
            "Ctrl+R — обновить\nCtrl+H — скрытые файлы\nAlt+← / Alt+→ — назад / вперёд\n\n" +
            "Перетаскивание: между панелями и из проводника Windows.\n" +
            "Права: sudo — если своих прав не хватает, операция выполняется от root.",
            "Клавиши");
    }

    private async void SshF3_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.ViewFile();
    }

    private async void SshF4_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.EditFile();
    }

    private async void NewFile_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.NewFile();
    }

    private async void SshF5_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is null) return;
        await _vm.CopyActive(Selected(true), Selected(false));
        UpdateInfo();
    }

    private async void SshF6_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.RenameActive(ActiveSelection());
    }

    private async void SshF7_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.MkDir();
    }

    private async void SshF8_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.DeleteActive(ActiveSelection());
        UpdateInfo();
    }

    private void SshF9_Click(object sender, RoutedEventArgs e)
    {
        PushSelection();
        _vm?.OpenTerminal();
    }

    private void SshF10_Click(object sender, RoutedEventArgs e)
    {
        _vm?.Disconnect();
        RefreshKeys();
    }

    private async void Properties_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        PushSelection();
        if (_vm is not null) await _vm.Properties(ActiveSelection());
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        TakeMenuPanel(sender);
        _vm?.CopyPath(ActiveSelection());
    }

    private async void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RemoteEditSession session } && _vm is not null)
        {
            PushSudo();
            await _vm.SaveEdit(session);
        }
    }

    private async void CloseEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RemoteEditSession session } && _vm is not null)
            await _vm.CloseEdit(session);
    }

    // ---------- Перетаскивание ----------

    private void Drag_Down(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void LocalDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !DragDistance(e)) return;
        if (!IsOnItemMove(LocalFilesList, e)) return;
        var files = Selected(true).Select(f => f.FullPath).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, files);
        data.SetData(LocalDragFormat, files);
        DragDrop.DoDragDrop(LocalFilesList, data, DragDropEffects.Copy);
    }

    private void RemoteDrag_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !DragDistance(e)) return;
        if (!IsOnItemMove(RemoteFilesList, e)) return;
        _dragRemote = Selected(false);
        if (_dragRemote.Count == 0) return;
        DragDrop.DoDragDrop(RemoteFilesList, new DataObject(RemoteDragFormat, "1"), DragDropEffects.Copy);
    }

    private bool DragDistance(MouseEventArgs e)
    {
        var d = e.GetPosition(null) - _dragStart;
        return Math.Abs(d.X) >= SystemParameters.MinimumHorizontalDragDistance * 2 ||
               Math.Abs(d.Y) >= SystemParameters.MinimumVerticalDragDistance * 2;
    }

    private static bool IsOnItemMove(ListView list, MouseEventArgs e)
        => e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(list, d) is ListViewItem;

    private void Local_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(RemoteDragFormat) ||
                    (e.Data.GetDataPresent(DataFormats.FileDrop) && !e.Data.GetDataPresent(LocalDragFormat))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Local_Drop(object sender, DragEventArgs e)
    {
        if (_vm is null) return;
        if (e.Data.GetDataPresent(RemoteDragFormat))
        {
            var entries = _dragRemote;
            _dragRemote = new();
            await _vm.DownloadEntries(entries);
            return;
        }

        if (e.Data.GetDataPresent(LocalDragFormat)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            try
            {
                foreach (var src in dropped)
                {
                    var dest = Path.Combine(_vm.LocalPath, Path.GetFileName(src.TrimEnd('\\')));
                    if (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(src))
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyFile(src, dest, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs);
                    else if (Directory.Exists(src))
                        Microsoft.VisualBasic.FileIO.FileSystem.CopyDirectory(src, dest, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs);
                }
            }
            catch (OperationCanceledException) { /* отменили в окне Windows */ }
            catch (Exception ex) { AppDialog.Info(Window.GetWindow(this)!, ex.Message, "Копирование"); }

            _vm.ReloadLocal();
        }
    }

    private void Remote_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _vm?.Connected == true && !e.Data.GetDataPresent(RemoteDragFormat) &&
                    (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(LocalDragFormat))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Remote_Drop(object sender, DragEventArgs e)
    {
        if (_vm is null || e.Data.GetDataPresent(RemoteDragFormat)) return;
        string[]? files = null;
        if (e.Data.GetData(LocalDragFormat) is string[] local) files = local;
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) files = dropped;
        if (files is not { Length: > 0 }) return;

        // Брошено на папку — копируем в неё.
        string? into = null;
        if (e.OriginalSource is DependencyObject d &&
            ItemsControl.ContainerFromElement(RemoteFilesList, d) is ListViewItem { DataContext: FileEntry { IsDirectory: true, IsParentNav: false } target })
            into = target.FullPath;
        PushSudo();
        await _vm.UploadPaths(files, into);
    }
}
