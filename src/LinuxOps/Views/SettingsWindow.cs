using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps.Views;

/// <summary>Настройки: тема (применяется сразу) и программа для F4.</summary>
public static class SettingsWindow
{
    public static void Show(Window owner)
    {
        var window = new Window
        {
            Title = "Настройки",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Title("Оформление"));
        var themes = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var initial = ThemeManager.Mode;
        foreach (var (mode, caption) in new[]
                 {
                     (ThemeManager.Light, "Дневная"),
                     (ThemeManager.Dark, "Ночная"),
                     (ThemeManager.System, "Как в Windows")
                 })
        {
            var rb = new RadioButton
            {
                Content = caption,
                GroupName = "theme",
                IsChecked = initial == mode,
                Margin = new Thickness(0, 0, 0, 8),
                Tag = mode
            };
            rb.Checked += (_, _) => ThemeManager.Apply(mode);
            themes.Children.Add(rb);
        }

        root.Children.Add(themes);

        root.Children.Add(Title("Редактор для F4 (SSH Files)"));
        var editorRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var browse = new Button { Content = "Обзор…", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var editor = new TextBox { Text = DefaultsStore.F4Editor, Height = 30 };
        editorRow.Children.Add(browse);
        editorRow.Children.Add(editor);
        root.Children.Add(editorRow);
        var hint = new TextBlock
        {
            Text = "Например notepad.exe или C:\\Program Files\\Notepad++\\notepad++.exe",
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 18),
            TextWrapping = TextWrapping.Wrap
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(hint);
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe|Все файлы|*.*", Title = "Редактор для F4" };
            try
            {
                var current = editor.Text.Trim().Trim('"');
                if (Path.IsPathRooted(current) && Directory.Exists(Path.GetDirectoryName(current)))
                    dlg.InitialDirectory = Path.GetDirectoryName(current);
            }
            catch
            {
                // путь из настройки может быть любым — тогда просто открываем по умолчанию
            }

            if (dlg.ShowDialog(window) == true) editor.Text = dlg.FileName;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Готово", MinWidth = 100, IsDefault = true };
        ok.SetResourceReference(FrameworkElement.StyleProperty, "BtnPrimary");
        var cancel = new Button { Content = "Отмена", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "Btn");
        ok.Click += (_, _) =>
        {
            DefaultsStore.F4Editor = editor.Text;
            window.DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        window.Content = root;

        // «Отмена» возвращает тему, с которой окно открыли.
        if (window.ShowDialog() != true && ThemeManager.Mode != initial)
            ThemeManager.Apply(initial);
    }

    private static TextBlock Title(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 10) };
        t.SetResourceReference(FrameworkElement.StyleProperty, "SectionTitle");
        return t;
    }
}
