using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinuxOps.Services;

namespace LinuxOps.Views;

/// <summary>Консоль вкладки PowerShell: эмулятор xterm поверх ConPTY. Клавиши — win32-input-mode (ConPTY его включает сам).</summary>
public partial class PsConsoleView : UserControl
{
    private readonly DispatcherTimer _resize;
    private PsConsoleSession? _session;

    public PsConsoleView()
    {
        InitializeComponent();
        System.Windows.Automation.AutomationProperties.SetAutomationId(Out, "PsConsole");
        Out.ScrollBar = OutBar;
        Out.MouseTracking = () => _session?.Mouse ?? (0, false);
        Out.MouseInput += seq => _session?.Type(seq);
        Out.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Out_PreviewKeyDown), handledEventsToo: true);
        Out.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(Out_PreviewTextInput), handledEventsToo: true);
        // Список подсказок не должен висеть поверх других окон и вкладок.
        Out.LostKeyboardFocus += (_, _) => HideSuggest();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideSuggest(); };
        _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            PushSize();
        };
    }

    public void Bind(PsConsoleSession session)
    {
        if (_session is not null)
        {
            _session.OutputChanged -= OnOutput;
            _session.Suggestions -= OnSuggestions;
        }

        _session = session;
        session.OutputChanged += OnOutput;
        session.Suggestions += OnSuggestions;
        OnOutput();
        PushSize();
    }

    public bool IsConsoleFocused => Out.IsKeyboardFocusWithin;

    public void FocusConsole()
    {
        Out.Focus();
        Keyboard.Focus(Out);
    }

    private void OnOutput()
    {
        if (_session is not null) Out.SetFrame(_session.Frame);
    }

    private void Out_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resize.Stop();
        _resize.Start();
    }

    private void PushSize()
    {
        if (_session is null || Out.ActualWidth < 50 || Out.ActualHeight < 50) return;
        var width = Out.ActualWidth - Out.Padding.Left - Out.Padding.Right -
                    (OutBar.Visibility == Visibility.Visible ? 0 : SystemParameters.VerticalScrollBarWidth);
        var height = Out.ActualHeight - Out.Padding.Top - Out.Padding.Bottom;
        _session.Resize((int)Math.Floor(width / Out.CharWidth), (int)Math.Floor(height / Out.LineHeight));
    }

    // ---------- Ввод ----------

    /// <summary>Строка с клавиатуры или вставка: в win32-режиме — нажатиями (переводы строк — Enter), иначе — как есть.</summary>
    public void SendText(string text)
    {
        if (_session is null || text.Length == 0) return;
        Out.ClearSelection();
        Out.ScrollToBottom();
        _session.Type(_session.Win32Input ? TerminalKeys.Win32Text(text) : text);
    }

    /// <summary>
    /// Несколько строк одним вводом: между строками Shift+Enter (новая строка в PSReadLine), в конце Enter, если submit.
    /// Табуляции — пробелами, иначе Tab сработает как дополнение.
    /// </summary>
    public void SendLines(string text, bool submit)
    {
        if (_session is null) return;
        var lines = text.Replace("\0", "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
        var count = lines.Length;
        while (count > 1 && lines[count - 1].Length == 0) count--; // хвостовой перевод строки — не пустая строка ввода
        if (!_session.Win32Input)
        {
            SendText(string.Join("\r", lines.Take(count)) + (submit ? "\r" : ""));
            return;
        }

        Out.ClearSelection();
        Out.ScrollToBottom();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append(TerminalKeys.Win32Text(lines[i]));
            if (i < count - 1) sb.Append(TerminalKeys.Win32(Key.Enter, ModifierKeys.Shift));
        }

        if (submit) sb.Append(TerminalKeys.Win32(Key.Enter, ModifierKeys.None));
        _session.Type(sb.ToString());
    }

    /// <summary>Нажатие клавиши (Esc, Enter, Shift+Enter, Ctrl+C) — для F5/F8 и кнопки «Стоп».</summary>
    public void SendKey(Key key, ModifierKeys mods = ModifierKeys.None)
    {
        if (_session is null) return;
        Out.ClearSelection();
        Out.ScrollToBottom();
        var data = _session.Win32Input ? TerminalKeys.Win32(key, mods) : TerminalKeys.Vt(key, mods, _session.AppCursorKeys);
        if (data is not null) _session.Type(data);
    }

    private void Out_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Any(char.IsControl)) return;
        if (Suggest.IsOpen) SuggestTyped(e.Text);
        SendText(e.Text);
        e.Handled = true;
    }

    private void Out_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);

        // Копирование и вставка как в Windows Terminal: Ctrl+C при выделении — копировать, без — прервать команду.
        if (ctrl && shift && key == Key.C) { Copy(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.C && Out.HasSelection) { Copy(); Out.ClearSelection(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && key == Key.Insert) { Copy(); e.Handled = true; return; }
        if (shift && key == Key.Insert) { Paste(); e.Handled = true; return; }
        if (shift && key is Key.PageUp or Key.PageDown)
        {
            Out.ScrollPage(key == Key.PageUp ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (Suggest.IsOpen && SuggestKey(key, mods))
        {
            e.Handled = true;
            return;
        }

        // Консоль завершилась (exit) — Enter запускает новую, как «подключиться снова» в SSH Terminal.
        if (!_session.Running)
        {
            if (key == Key.Enter && mods == ModifierKeys.None) _session.Restart();
            e.Handled = true;
            return;
        }

        // Ctrl+Break — тоже прервать (Ctrl+C): «настоящий» Ctrl+Break в PowerShell открывает отладчик.
        if (key == Key.Cancel) { SendKey(Key.C, ModifierKeys.Control); e.Handled = true; return; }

        string? data;
        if (_session.Win32Input)
        {
            data = TerminalKeys.Win32(key, mods);
        }
        else
        {
            data = TerminalKeys.Vt(key, mods, _session.AppCursorKeys);
            // Обычные символы придут через TextInput.
            if (data == " ") data = null;
        }

        if (data is null) return;
        Out.ClearSelection();
        Out.ScrollToBottom();
        _session.Type(data);
        e.Handled = true;
    }

    // ---------- Подсказки (как в терминале VS Code) ----------

    private PsMsg? _sugg;
    private string _extra = ""; // набрано поверх списка, пока свежий не пришёл
    private bool _navigated; // ходили по списку стрелками — тогда и Enter вставляет (как в VS Code)

    private void OnSuggestions(PsMsg m)
    {
        if (m.T != "suggest" || m.Completions is not { Count: > 0 } list || m.Text is null || !Out.IsKeyboardFocusWithin)
        {
            HideSuggest();
            return;
        }

        var at = Out.CursorRect();
        if (at is null)
        {
            HideSuggest();
            return;
        }

        _sugg = m;
        _extra = "";
        _navigated = false;
        if (!Fill()) return;
        // Список — от начала дополняемого слова, под строкой (у нижнего края экрана WPF сам перенесёт его вверх).
        var typed = m.Text.Length - m.Start;
        var r = at.Value;
        var x = Math.Max(0, r.X - typed * Out.CharWidth - 20);
        Suggest.PlacementRectangle = new Rect(x, r.Y, r.Width, r.Height);
        SuggestList.SelectedIndex = 0;
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        Suggest.IsOpen = true;
    }

    /// <summary>Пункты, начинающиеся с набранного; false — ничего не подходит (список закрыт).</summary>
    private bool Fill()
    {
        if (_sugg?.Text is not { } input || _sugg.Completions is not { } all) return false;
        var typed = input[_sugg.Start..] + _extra;
        SuggestList.Items.Clear();
        foreach (var c in all)
        {
            if (_extra.Length > 0 && !c.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) &&
                !c.List.StartsWith(typed.TrimStart('-', '$'), StringComparison.OrdinalIgnoreCase)) continue;
            var item = new ListBoxItem { Content = PsEditorAssist.ItemContent(c), Tag = c };
            System.Windows.Automation.AutomationProperties.SetName(item, c.List.Length > 0 ? c.List : c.Text);
            SuggestList.Items.Add(item);
        }

        if (SuggestList.Items.Count == 0)
        {
            HideSuggest();
            return false;
        }

        SuggestList.SelectedIndex = 0;
        SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        return true;
    }

    /// <summary>Набор поверх открытого списка: буквы сужают его сразу, остальное — закрывает.</summary>
    private void SuggestTyped(string text)
    {
        if (text.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'))
        {
            _extra += text;
            Fill();
        }
        else
        {
            HideSuggest();
        }
    }

    private void HideSuggest()
    {
        _sugg = null;
        _extra = "";
        if (Suggest.IsOpen) Suggest.IsOpen = false;
    }

    /// <summary>Клавиши, пока открыт список: ↑↓ PgUp PgDn — выбор, Tab — вставить, Enter — вставить после ↑↓, иначе выполнить строку, Esc — закрыть. true — обработано.</summary>
    private bool SuggestKey(Key key, ModifierKeys mods)
    {
        if (mods != ModifierKeys.None)
        {
            if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Alt)) HideSuggest();
            return false;
        }

        var n = SuggestList.Items.Count;
        switch (key)
        {
            case Key.Down: Move(1); return true;
            case Key.Up: Move(-1); return true;
            case Key.PageDown: Move(8); return true;
            case Key.PageUp: Move(-8); return true;
            case Key.Tab:
                AcceptSuggest();
                return true;
            case Key.Enter:
                // Набрал команду целиком и нажал Enter — выполнить, а не вставить подсказку; после ↑↓ — вставить.
                if (!_navigated)
                {
                    HideSuggest();
                    return false;
                }

                AcceptSuggest();
                return true;
            case Key.Escape:
                HideSuggest();
                return true;
            case Key.Back:
                if (_extra.Length > 0)
                {
                    _extra = _extra[..^1];
                    Fill();
                }
                else
                {
                    HideSuggest();
                }

                return false;
            case Key.Left or Key.Right or Key.Home or Key.End:
                HideSuggest();
                return false;
            default:
                return false;
        }

        void Move(int d)
        {
            if (n == 0) return;
            _navigated = true;
            var i = SuggestList.SelectedIndex + d;
            SuggestList.SelectedIndex = d is 1 or -1 ? (i + n) % n : Math.Clamp(i, 0, n - 1);
            SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        }
    }

    /// <summary>Заменить набранное начало слова выбранным: Backspace по числу набранных символов и сам текст.</summary>
    private void AcceptSuggest()
    {
        if (_sugg?.Text is not { } input || SuggestList.SelectedItem is not ListBoxItem { Tag: PsCompletion c } || _session is null)
        {
            HideSuggest();
            return;
        }

        var typed = input[_sugg.Start..] + _extra;
        HideSuggest();
        string data;
        if (c.Text.StartsWith(typed, StringComparison.Ordinal))
        {
            data = c.Text[typed.Length..];
            data = _session.Win32Input ? TerminalKeys.Win32Text(data) : data;
        }
        else
        {
            var bs = _session.Win32Input ? TerminalKeys.Win32(Key.Back, ModifierKeys.None)! : "\x7f";
            data = string.Concat(Enumerable.Repeat(bs, typed.Length)) + (_session.Win32Input ? TerminalKeys.Win32Text(c.Text) : c.Text);
        }

        Out.ScrollToBottom();
        _session.Type(data);
    }

    private void SuggestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tip = (SuggestList.SelectedItem as ListBoxItem)?.Tag is PsCompletion { Tip: { } t } && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
        SuggestTip.Text = tip ?? "";
        SuggestTip.Visibility = tip is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SuggestList_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(SuggestList, d) is ListBoxItem item)
        {
            SuggestList.SelectedItem = item;
            AcceptSuggest();
            FocusConsole();
        }
    }

    private void Copy()
    {
        if (Out.HasSelection) Clipboard.SetText(Out.SelectedText);
    }

    private void Paste()
    {
        if (!Clipboard.ContainsText()) return;
        // Многострочная вставка — одним блоком, выполнение по Enter (как в Windows Terminal с PSReadLine).
        SendLines(Clipboard.GetText(), submit: false);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => Copy();
    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();

    /// <summary>Как Ctrl+L в PSReadLine: консоль очищает экран и историю прокрутки сама.</summary>
    public void Clear() => SendKey(Key.L, ModifierKeys.Control);

    private void Clear_Click(object sender, RoutedEventArgs e) => Clear();
}
