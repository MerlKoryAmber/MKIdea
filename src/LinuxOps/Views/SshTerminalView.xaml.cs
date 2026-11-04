using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class SshTerminalView : UserControl
{
    private readonly DispatcherTimer _resize;
    private SshTerminalViewModel? _vm;

    public SshTerminalView()
    {
        InitializeComponent();
        System.Windows.Automation.AutomationProperties.SetAutomationId(Out, "TermOut");
        Out.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Out_PreviewKeyDown), handledEventsToo: true);
        Out.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(Out_PreviewTextInput), handledEventsToo: true);
        _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            PushSize();
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SshTerminalViewModel old)
            {
                old.OutputChanged -= OnOutput;
                old.PropertyChanged -= OnVmChanged;
            }

            if (e.NewValue is not SshTerminalViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.OutputChanged += OnOutput;
            vm.PropertyChanged += OnVmChanged;
            UpdatePlaceholder();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm?.Connected == true) FocusTerminal();
            }, DispatcherPriority.Input);
        };
    }

    /// <summary>Кнопка «SSH Files»: этот хост в файловом менеджере.</summary>
    public event Action<HostConfig>? OpenFilesRequested;

    public void FocusTerminal()
    {
        Out.Focus();
        Keyboard.Focus(Out);
    }

    /// <summary>Сочетания вкладок (Ctrl+Tab, Ctrl+Shift+T/W) — не в оболочку, их ловит TerminalTabsView.</summary>
    public static bool IsTabShortcut(Key key, ModifierKeys mods)
    {
        if (!mods.HasFlag(ModifierKeys.Control)) return false;
        if (key == Key.Tab) return true;
        return mods.HasFlag(ModifierKeys.Shift) && key is Key.T or Key.W;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SshTerminalViewModel.Connected))
            UpdatePlaceholder();
    }

    private void UpdatePlaceholder()
    {
        Placeholder.Visibility = _vm is { Connected: false } && string.IsNullOrEmpty(_vm.Output)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReconnectBtn.IsEnabled = _vm is { Connected: false };
    }

    private void OnOutput()
    {
        if (_vm is null) return;
        Out.Text = _vm.Output;
        Out.CaretIndex = Math.Min(_vm.CursorIndex, Out.Text.Length);
        Out.ScrollToEnd();
        UpdatePlaceholder();
    }

    private void Out_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resize.Stop();
        _resize.Start();
    }

    private void PushSize()
    {
        if (_vm is null || Out.ActualWidth < 50 || Out.ActualHeight < 50) return;
        var typeface = new Typeface(Out.FontFamily, Out.FontStyle, Out.FontWeight, Out.FontStretch);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = new FormattedText(new string('M', 10), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, Out.FontSize, Brushes.White, dpi);
        var charWidth = probe.WidthIncludingTrailingWhitespace / 10;
        var lineHeight = Out.FontFamily.LineSpacing * Out.FontSize;
        // Поля TextBox, рамка и место под вертикальную прокрутку.
        var width = Out.ActualWidth - Out.Padding.Left - Out.Padding.Right - 4 - SystemParameters.VerticalScrollBarWidth;
        var height = Out.ActualHeight - Out.Padding.Top - Out.Padding.Bottom - 4;
        var cols = (int)Math.Floor(width / charWidth);
        var rows = (int)Math.Floor(height / lineHeight);
        _vm.Resize(cols, rows, width, height);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.Connect(_vm.SelectedHost, confirmSwitch: false);
        if (_vm.Connected) FocusTerminal();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _vm?.Disconnect();

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedHost is { } host) OpenFilesRequested?.Invoke(host);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _vm?.Clear();
        if (_vm?.Connected == true)
        {
            _vm.Type("\u000c"); // Ctrl+L: оболочка заново нарисует приглашение
            FocusTerminal();
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Out.SelectionLength > 0) Clipboard.SetText(Out.SelectedText);
    }

    private void Paste_Click(object sender, RoutedEventArgs e) => Paste();

    private void Paste()
    {
        if (_vm is null || !_vm.Connected || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
        _vm.Type(text);
    }

    private void Out_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is null || !_vm.Connected || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Any(char.IsControl)) return;
        _vm.Type(e.Text);
        e.Handled = true;
    }

    private void Out_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsTabShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) return;
        if (_vm is null || !_vm.Connected) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);

        // Копирование и вставка в стиле терминалов Windows.
        if (ctrl && shift && key == Key.C) { Copy_Click(sender, e); e.Handled = true; return; }
        if (ctrl && shift && key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && !shift && key == Key.C && Out.SelectionLength > 0) return; // обычное копирование выделенного
        if (ctrl && !shift && key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && key == Key.Insert) { Copy_Click(sender, e); e.Handled = true; return; }
        if (shift && key == Key.Insert) { Paste(); e.Handled = true; return; }
        // Shift+PageUp/PageDown — прокрутка истории на экране, в оболочку не уходит.
        if (shift && key is Key.PageUp or Key.PageDown) return;

        var app = _vm.AppCursorKeys;
        string? data = key switch
        {
            Key.Enter => "\r",
            Key.Space when ctrl => "\0",
            Key.Space => " ",
            Key.Back => alt ? "\u001b\u007f" : "\u007f",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Escape => "\u001b",
            Key.Delete => "\u001b[3~",
            Key.Insert => "\u001b[2~",
            Key.Up => app ? "\u001bOA" : ctrl ? "\u001b[1;5A" : "\u001b[A",
            Key.Down => app ? "\u001bOB" : ctrl ? "\u001b[1;5B" : "\u001b[B",
            Key.Right => app ? "\u001bOC" : ctrl ? "\u001b[1;5C" : "\u001b[C",
            Key.Left => app ? "\u001bOD" : ctrl ? "\u001b[1;5D" : "\u001b[D",
            Key.Home => app ? "\u001bOH" : "\u001b[H",
            Key.End => app ? "\u001bOF" : "\u001b[F",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
            _ => null
        };

        if (data is null && ctrl && !alt && key is >= Key.A and <= Key.Z)
            data = ((char)(key - Key.A + 1)).ToString();
        if (data is null && ctrl && !alt)
        {
            data = key switch
            {
                Key.OemOpenBrackets => "\u001b",
                Key.Oem5 => "\u001c",
                Key.OemCloseBrackets => "\u001d",
                Key.D6 => "\u001e",
                Key.OemMinus => "\u001f",
                _ => null
            };
        }

        if (data is null && alt && !ctrl && (key is >= Key.A and <= Key.Z || key is >= Key.D0 and <= Key.D9))
        {
            var ch = key is >= Key.A and <= Key.Z
                ? (char)((shift ? 'A' : 'a') + (key - Key.A))
                : (char)('0' + (key - Key.D0));
            data = "\u001b" + ch;
        }

        if (data is null) return;
        _vm.Type(data);
        e.Handled = true;
    }
}
