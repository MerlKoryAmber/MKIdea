using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinuxOps.Services;
using LinuxOps.ViewModels;
using Microsoft.Win32;

namespace LinuxOps.Views;

public partial class PowerShellView : UserControl
{
    private readonly DispatcherTimer _width;
    private PowerShellViewModel? _vm;
    private bool _scrollQueued;

    public PowerShellView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = PsConsoleColorizer.ScriptHighlighting();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        ThemeManager.Changed += () => Editor.SyntaxHighlighting = PsConsoleColorizer.ScriptHighlighting();
        PreviewKeyDown += OnPreviewKeyDown;

        _width = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _width.Tick += (_, _) =>
        {
            _width.Stop();
            PushWidth();
        };

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PowerShellViewModel old)
            {
                old.PropertyChanged -= OnVmChanged;
                old.ConsoleChanged -= OnConsoleChanged;
            }

            if (e.NewValue is not PowerShellViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.PropertyChanged += OnVmChanged;
            vm.ConsoleChanged += OnConsoleChanged;
            ConsoleOut.Document = vm.ConsoleDoc;
            ConsoleOut.TextArea.TextView.LineTransformers.Add(new PsConsoleColorizer(vm.Spans));
            ShowScript();
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _vm is null) return;
            _vm.EnsureEngine();
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PowerShellViewModel.SelectedScript)) ShowScript();
        if (e.PropertyName == nameof(PowerShellViewModel.Busy) && _vm is { Busy: false } && Input.IsKeyboardFocusWithin == false
            && ConsoleOut.IsKeyboardFocusWithin) Input.Focus();
    }

    private void ShowScript()
    {
        if (_vm?.SelectedScript is { } s && !ReferenceEquals(Editor.Document, s.Document)) Editor.Document = s.Document;
    }

    /// <summary>Вывод идёт кусками — прокрутка и перерисовка не чаще одного раза за цикл диспетчера.</summary>
    private void OnConsoleChanged()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollQueued = false;
            ConsoleOut.TextArea.TextView.Redraw();
            ConsoleOut.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    // ---------- Ширина консоли → BufferSize хоста (Format-Table укладывается по ширине) ----------

    private void ConsoleOut_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _width.Stop();
        _width.Start();
    }

    private void PushWidth()
    {
        if (_vm is null || ConsoleOut.ActualWidth < 100) return;
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(ConsoleOut.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            ConsoleOut.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var cols = (int)((ConsoleOut.ActualWidth - SystemParameters.VerticalScrollBarWidth - 12) / (ft.WidthIncludingTrailingWhitespace / 10));
        _vm.SetWidth(cols);
    }

    // ---------- Клавиши ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.F5: _vm.RunScript(); break;
            case Key.F8: RunSelection(); break;
            case Key.Cancel: _vm.Stop(); break; // Ctrl+Break
            case Key.N when ctrl: _vm.NewScript(); break;
            case Key.O when ctrl: Open_Click(sender, e); break;
            case Key.S when ctrl: Save(_vm.SelectedScript, saveAs: shift); break;
            default: return;
        }

        e.Handled = true;
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                if (_vm.Busy) break;
                var line = Input.Text;
                Input.Clear();
                _vm.RunLine(line);
                break;
            case Key.Up or Key.Down:
                if (_vm.HistoryStep(e.Key == Key.Up ? -1 : 1) is { } h)
                {
                    Input.Text = h;
                    Input.CaretIndex = h.Length;
                }

                break;
            case Key.Escape:
                Input.Clear();
                break;
            default: return;
        }

        e.Handled = true;
    }

    private void RunSelection()
    {
        if (_vm is null) return;
        var text = Editor.SelectionLength > 0
            ? Editor.SelectedText
            : Editor.Document.GetText(Editor.Document.GetLineByOffset(Editor.CaretOffset));
        _vm.RunSelection(text);
    }

    // ---------- Кнопки ----------

    private void Run_Click(object sender, RoutedEventArgs e) => _vm?.RunScript();
    private void RunSelection_Click(object sender, RoutedEventArgs e) => RunSelection();
    private void Stop_Click(object sender, RoutedEventArgs e) => _vm?.Stop();
    private void ClearConsole_Click(object sender, RoutedEventArgs e) => _vm?.ClearConsole();
    private void New_Click(object sender, RoutedEventArgs e) => _vm?.NewScript();
    private void Vsphere_Click(object sender, RoutedEventArgs e) => VsphereWindow.Open(Window.GetWindow(this));
    private void Save_Click(object sender, RoutedEventArgs e) => Save(_vm?.SelectedScript, saveAs: false);

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!AppDialog.Confirm(Window.GetWindow(this)!, "Перезапустить движок PowerShell?\nПеременные, загруженные модули и подключения (vCenter и др.) будут сброшены.", "PowerShell"))
            return;
        _vm.Restart();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Скрипты PowerShell (*.ps1;*.psm1;*.psd1)|*.ps1;*.psm1;*.psd1|Все файлы (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true || _vm is null) return;
        foreach (var f in dlg.FileNames)
        {
            try { _vm.OpenScript(f); }
            catch (Exception ex) { AppDialog.Info(Window.GetWindow(this)!, $"Не удалось открыть {f}:\n{ex.Message}", "PowerShell"); }
        }
    }

    private bool Save(PsScript? s, bool saveAs)
    {
        if (_vm is null || s is null) return false;
        var path = s.FilePath;
        if (saveAs || path is null)
        {
            var dlg = new SaveFileDialog { Filter = "Скрипт PowerShell (*.ps1)|*.ps1|Все файлы (*.*)|*.*", FileName = s.Title, DefaultExt = ".ps1" };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return false;
            path = dlg.FileName;
        }

        try
        {
            _vm.SaveScript(s, path);
            return true;
        }
        catch (Exception ex)
        {
            AppDialog.Info(Window.GetWindow(this)!, $"Не удалось сохранить {path}:\n{ex.Message}", "PowerShell");
            return false;
        }
    }

    private void CloseScript_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as FrameworkElement)?.DataContext is not PsScript s) return;
        if (s.Dirty && !ConfirmDiscard(s)) return;
        _vm.CloseScript(s);
    }

    /// <summary>Несохранённый скрипт: Да — сохранить, Нет — выбросить, Отмена — оставить.</summary>
    private bool ConfirmDiscard(PsScript s)
    {
        return AppDialog.AskSave(Window.GetWindow(this)!, $"Сохранить изменения в «{s.Title}»?", "PowerShell") switch
        {
            true => Save(s, saveAs: false),
            false => true,
            null => false
        };
    }

    /// <summary>Закрытие программы: спросить про несохранённые скрипты. false — закрытие отменено.</summary>
    public bool ConfirmCloseAll()
    {
        if (_vm is null) return true;
        foreach (var s in _vm.Scripts.Where(s => s.Dirty).ToList())
        {
            _vm.SelectedScript = s;
            if (!ConfirmDiscard(s)) return false;
        }

        return true;
    }
}
