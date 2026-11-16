using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinuxOps.Services;
using LinuxOps.ViewModels;
using Microsoft.Win32;

namespace LinuxOps.Views;

public partial class PowerShellView : UserControl
{
    private PowerShellViewModel? _vm;

    public PowerShellView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = PsHighlighting.ScriptHighlighting();
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 4;
        Editor.Options.HighlightCurrentLine = true;
        ThemeManager.Changed += () => Editor.SyntaxHighlighting = PsHighlighting.ScriptHighlighting();
        PreviewKeyDown += OnPreviewKeyDown;

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PowerShellViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is not PowerShellViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.PropertyChanged += OnVmChanged;
            ConsolePane.Bind(vm.Console);
            PsEditorAssist.Attach(Editor, vm.Complete);
            ShowScript();
        };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _vm is null) return;
            _vm.EnsureStarted();
            Dispatcher.BeginInvoke(() => Editor.TextArea.Focus(), DispatcherPriority.Input);
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PowerShellViewModel.SelectedScript)) ShowScript();
    }

    private void ShowScript()
    {
        if (_vm?.SelectedScript is { } s && !ReferenceEquals(Editor.Document, s.Document)) Editor.Document = s.Document;
    }

    // ---------- Клавиши ----------

    /// <summary>
    /// F5/F8 и Ctrl+N/O/S — в редакторе и на панели. В консоли все клавиши уходят PowerShell
    /// (там F8 — поиск по истории, Ctrl+S — поиск вперёд), как в терминале VS Code.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || ConsolePane.IsConsoleFocused) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.F5: RunScript(); break;
            case Key.F8: RunSelection(); break;
            case Key.N when ctrl: _vm.NewScript(); break;
            case Key.O when ctrl: Open_Click(sender, e); break;
            case Key.S when ctrl: Save(_vm.SelectedScript, saveAs: shift); break;
            default: return;
        }

        e.Handled = true;
    }

    // ---------- Выполнение — в консоли ----------

    /// <summary>F5: изменённый файл сохраняется (как в VS Code), затем dot-source в консоли — переменные остаются в сеансе.</summary>
    private void RunScript()
    {
        if (_vm?.SelectedScript is not { } s) return;
        if (s.FilePath is not null && s.Dirty && !Save(s, saveAs: false)) return;
        string? cmd;
        try { cmd = _vm.RunCommand(s); }
        catch (Exception ex)
        {
            AppDialog.Info(Window.GetWindow(this)!, "Не удалось подготовить запуск:\n" + ex.Message, "PowerShell");
            return;
        }

        if (cmd is null) return;
        Execute(cmd);
    }

    /// <summary>F8: выделенное или текущая строка — набирается в консоли (несколько строк — одним вводом).</summary>
    private void RunSelection()
    {
        if (_vm is null) return;
        var text = Editor.SelectionLength > 0
            ? Editor.SelectedText
            : Editor.Document.GetText(Editor.Document.GetLineByOffset(Editor.CaretOffset));
        if (string.IsNullOrWhiteSpace(text)) return;
        Execute(text.Trim('\r', '\n'));
    }

    /// <summary>Esc очищает начатую в консоли строку (PSReadLine; первый Esc закрывает список подсказок), затем текст и Enter.</summary>
    private void Execute(string text)
    {
        _vm?.EnsureStarted();
        ConsolePane.SendKey(Key.Escape);
        ConsolePane.SendKey(Key.Escape);
        ConsolePane.SendLines(text, submit: true);
    }

    // ---------- Кнопки ----------

    private void Run_Click(object sender, RoutedEventArgs e) => RunScript();
    private void RunSelection_Click(object sender, RoutedEventArgs e) => RunSelection();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        ConsolePane.SendKey(Key.C, ModifierKeys.Control);
        ConsolePane.FocusConsole();
    }

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        ConsolePane.Clear();
        ConsolePane.FocusConsole();
    }

    private void New_Click(object sender, RoutedEventArgs e) => _vm?.NewScript();
    private void Vsphere_Click(object sender, RoutedEventArgs e) => VsphereWindow.Open(Window.GetWindow(this));
    private void Save_Click(object sender, RoutedEventArgs e) => Save(_vm?.SelectedScript, saveAs: false);

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (!AppDialog.Confirm(Window.GetWindow(this)!, "Перезапустить консоль PowerShell?\nПеременные, загруженные модули и подключения (vCenter и др.) будут сброшены, запущенные в ней программы — остановлены.", "PowerShell"))
            return;
        _vm.Restart();
        ConsolePane.FocusConsole();
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
