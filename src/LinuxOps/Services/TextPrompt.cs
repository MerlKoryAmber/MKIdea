using System.Windows;
using System.Windows.Controls;

namespace LinuxOps.Services;

public static class TextPrompt
{
    public static string? Ask(Window? owner, string title, string label, string initial)
    {
        var w = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner
        };
        var box = new TextBox
        {
            Text = initial,
            Margin = new Thickness(16, 8, 16, 8),
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var ok = new Button { Content = "OK", Width = 88, Height = 30, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "Отмена", Width = 88, Height = 30, IsCancel = true };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); w.DialogResult = true; };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 16)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new DockPanel();
        var labelTb = new TextBlock { Text = label, Margin = new Thickness(16, 16, 16, 4) };
        DockPanel.SetDock(labelTb, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(labelTb);
        root.Children.Add(buttons);
        root.Children.Add(box);
        w.Content = root;
        box.SelectAll();
        box.Focus();
        return w.ShowDialog() == true ? result : null;
    }
}
