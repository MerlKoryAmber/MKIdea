using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Services;

namespace LinuxOps.Views;

public enum OverwriteChoice { Overwrite, Skip, OverwriteAll, SkipAll, Cancel }

public sealed class PropertiesResult
{
    public int? Mode { get; init; }
    public string? Owner { get; init; }
    public bool Recursive { get; init; }
}

/// <summary>Диалоги файлового менеджера: «файл уже есть» и «свойства / права».</summary>
public static class FileDialogs
{
    public static OverwriteChoice AskOverwrite(Window owner, string target, string existing, string incoming, bool many)
    {
        var choice = OverwriteChoice.Cancel;
        var window = NewWindow(owner, "Файл уже есть", 640);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = target, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Уже есть:  " + existing, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "Новый:       " + incoming, Margin = new Thickness(0, 0, 0, 16) });
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string caption, OverwriteChoice value, bool isDefault = false, bool isCancel = false)
        {
            var b = new Button { Content = caption, MinWidth = 96, Margin = new Thickness(8, 4, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = isDefault, IsCancel = isCancel };
            b.Click += (_, _) => { choice = value; window.DialogResult = true; };
            row.Children.Add(b);
        }

        Add("Заменить", OverwriteChoice.Overwrite, isDefault: true);
        Add("Пропустить", OverwriteChoice.Skip);
        if (many)
        {
            Add("Заменить все", OverwriteChoice.OverwriteAll);
            Add("Пропустить все", OverwriteChoice.SkipAll);
        }

        Add("Отмена", OverwriteChoice.Cancel, isCancel: true);
        panel.Children.Add(row);
        window.Content = panel;
        window.ShowDialog();
        return choice;
    }

    /// <summary>Права (rwx и восьмерично), владелец:группа, рекурсивно для папок.</summary>
    public static PropertiesResult? AskProperties(Window owner, string title, int mode, string ownerGroup, bool hasDirectory, bool sudo)
    {
        PropertiesResult? result = null;
        var window = NewWindow(owner, "Свойства", 460);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        for (var i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        for (var i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] heads = { "Чтение", "Запись", "Выполнение" };
        string[] who = { "Владелец", "Группа", "Остальные" };
        var boxes = new CheckBox[9];
        for (var c = 0; c < 3; c++)
        {
            var h = new TextBlock { Text = heads[c], Margin = new Thickness(0, 0, 0, 6), Foreground = System.Windows.Media.Brushes.DimGray };
            Grid.SetColumn(h, c + 1);
            grid.Children.Add(h);
        }

        for (var r = 0; r < 3; r++)
        {
            var label = new TextBlock { Text = who[r], Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (var c = 0; c < 3; c++)
            {
                var cb = new CheckBox { Margin = new Thickness(0, 4, 0, 4) };
                Grid.SetRow(cb, r + 1);
                Grid.SetColumn(cb, c + 1);
                grid.Children.Add(cb);
                boxes[r * 3 + c] = cb;
            }
        }

        var special = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var suid = new CheckBox { Content = "setuid", Margin = new Thickness(0, 0, 16, 0) };
        var sgid = new CheckBox { Content = "setgid", Margin = new Thickness(0, 0, 16, 0) };
        var sticky = new CheckBox { Content = "sticky" };
        special.Children.Add(suid);
        special.Children.Add(sgid);
        special.Children.Add(sticky);
        Grid.SetRow(special, 4);
        Grid.SetColumnSpan(special, 4);
        grid.Children.Add(special);
        root.Children.Add(grid);

        var octalRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        octalRow.Children.Add(new TextBlock { Text = "Восьмерично:", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        var octal = new TextBox { Width = 80, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        octalRow.Children.Add(octal);
        root.Children.Add(octalRow);

        var ownerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        ownerRow.Children.Add(new TextBlock { Text = "Владелец:группа", Width = 110, VerticalAlignment = VerticalAlignment.Center });
        var ownerBox = new TextBox { Text = ownerGroup, Height = 26, VerticalContentAlignment = VerticalAlignment.Center };
        ownerRow.Children.Add(ownerBox);
        root.Children.Add(ownerRow);

        var recursive = new CheckBox { Content = "Применить ко всему внутри папок", Visibility = hasDirectory ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(recursive);
        if (!sudo)
        {
            root.Children.Add(new TextBlock
            {
                Text = "Сменить владельца обычно может только root. Для чужих файлов выберите «Права: sudo».",
                TextWrapping = TextWrapping.Wrap,
                Foreground = System.Windows.Media.Brushes.DimGray,
                FontSize = 11.5,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        var sync = false;
        int[] bits = { 0x100, 0x80, 0x40, 0x20, 0x10, 0x8, 0x4, 0x2, 0x1 };
        int FromBoxes()
        {
            var m = 0;
            for (var i = 0; i < 9; i++) if (boxes[i].IsChecked == true) m |= bits[i];
            if (suid.IsChecked == true) m |= 0x800;
            if (sgid.IsChecked == true) m |= 0x400;
            if (sticky.IsChecked == true) m |= 0x200;
            return m;
        }

        void ToBoxes(int m)
        {
            for (var i = 0; i < 9; i++) boxes[i].IsChecked = (m & bits[i]) != 0;
            suid.IsChecked = (m & 0x800) != 0;
            sgid.IsChecked = (m & 0x400) != 0;
            sticky.IsChecked = (m & 0x200) != 0;
        }

        var known = mode >= 0;
        sync = true;
        ToBoxes(Math.Max(0, mode));
        octal.Text = known ? Rights.ToOctal(mode) : "";
        sync = false;
        foreach (var cb in boxes.Concat(new[] { suid, sgid, sticky }))
        {
            cb.Checked += (_, _) => { if (!sync) { sync = true; octal.Text = Rights.ToOctal(FromBoxes()); sync = false; } };
            cb.Unchecked += (_, _) => { if (!sync) { sync = true; octal.Text = Rights.ToOctal(FromBoxes()); sync = false; } };
        }

        octal.TextChanged += (_, _) =>
        {
            if (sync) return;
            if (TryOctal(octal.Text, out var m)) { sync = true; ToBoxes(m); sync = false; }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Применить", MinWidth = 96, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsCancel = true };
        ok.Click += (_, _) =>
        {
            int? newMode = null;
            if (octal.Text.Trim().Length > 0)
            {
                if (!TryOctal(octal.Text, out var m))
                {
                    AppDialog.Info(window, "Права — 3 или 4 восьмеричные цифры, например 644 или 0755.", "Свойства");
                    return;
                }

                if (!known || m != mode || recursive.IsChecked == true) newMode = m;
            }

            var newOwner = ownerBox.Text.Trim();
            result = new PropertiesResult
            {
                Mode = newMode,
                Owner = newOwner.Length > 0 && newOwner != ownerGroup ? newOwner : null,
                Recursive = recursive.IsChecked == true
            };
            window.DialogResult = true;
        };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        root.Children.Add(row);
        window.Content = root;
        window.ShowDialog();
        return result;
    }

    private static bool TryOctal(string text, out int mode)
    {
        mode = 0;
        var t = text.Trim();
        if (t.Length is < 3 or > 4 || t.Any(ch => ch is < '0' or > '7')) return false;
        mode = Convert.ToInt32(t, 8);
        return true;
    }

    private static Window NewWindow(Window owner, string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = owner,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false
    };
}
