using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

public partial class SshTerminalView : UserControl
{
    private SshTerminalViewModel? _vm;

    public SshTerminalView()
    {
        InitializeComponent();
        System.Windows.Automation.AutomationProperties.SetAutomationId(Out, "TermOut");
        Out.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(Out_PreviewKeyDown), handledEventsToo: true);
        Out.AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(Out_PreviewTextInput), handledEventsToo: true);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SshTerminalViewModel old)
                old.OutputChanged -= OnOutput;
            if (e.NewValue is not SshTerminalViewModel vm) return;
            _vm = vm;
            vm.Owner = () => Window.GetWindow(this);
            vm.OutputChanged += OnOutput;
        };
    }

    private void OnOutput()
    {
        if (_vm is null) return;
        Out.Text = _vm.Output;
        Out.CaretIndex = Out.Text.Length;
        Out.ScrollToEnd();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.Connect();
        if (_vm.Connected) Out.Focus();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => _vm?.Disconnect();

    private void Clear_Click(object sender, RoutedEventArgs e) => _vm?.Clear();

    private void Out_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_vm is null || !_vm.Connected || string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.Any(char.IsControl)) return;
        _vm.Type(e.Text);
        e.Handled = true;
    }

    private void Out_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || !_vm.Connected) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Out.SelectionLength > 0)
            return;
        var data = key switch
        {
            Key.Enter => "\r",
            Key.Space => " ",
            Key.Back => "\u007f",
            Key.Delete => "\u001b[3~",
            Key.Tab => "\t",
            Key.Up => "\u001b[A",
            Key.Down => "\u001b[B",
            Key.Left => "\u001b[D",
            Key.Right => "\u001b[C",
            Key.Home => "\u001b[H",
            Key.End => "\u001b[F",
            Key.C when Keyboard.Modifiers == ModifierKeys.Control => "\u0003",
            Key.D when Keyboard.Modifiers == ModifierKeys.Control => "\u0004",
            Key.V when Keyboard.Modifiers == ModifierKeys.Control => Clipboard.ContainsText() ? Clipboard.GetText() : null,
            _ => null
        };
        if (data is null) return;
        _vm.Type(data);
        e.Handled = true;
    }
}
