using System.Windows;
using System.Windows.Controls;

namespace LinuxOps.Services;

/// <summary>Запросы ввода из PowerShell (Read-Host, Get-Credential, обязательные параметры, выбор) — окнами LinuxOps.</summary>
public static class PsPrompts
{
    /// <summary>Значения полей по порядку (для выбора — номер варианта); null — отмена.</summary>
    public static List<string>? Ask(Window? owner, PsMsg m) =>
        m.Kind == "choice" ? AskChoice(owner, m) : AskFields(owner, m);

    private static Window NewWindow(Window? owner, string? caption) => new()
    {
        Title = string.IsNullOrWhiteSpace(caption) ? "PowerShell" : caption,
        Width = 440,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        Owner = owner,
        ShowInTaskbar = owner is null
    };

    private static TextBlock Text(string text, double bottom) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom)
    };

    private static List<string>? AskFields(Window? owner, PsMsg m)
    {
        var w = NewWindow(owner, m.Caption);
        var panel = new StackPanel { Margin = new Thickness(18) };
        if (!string.IsNullOrWhiteSpace(m.Message)) panel.Children.Add(Text(m.Message!, 12));

        var inputs = new List<Control>();
        foreach (var f in m.Fields ?? new List<PsField>())
        {
            panel.Children.Add(Text(f.Label, 4));
            Control input = f.Secret
                ? new PasswordBox()
                : new TextBox { Text = f.Value ?? "" };
            input.Margin = new Thickness(0, 0, 0, 10);
            inputs.Add(input);
            panel.Children.Add(input);
        }

        List<string>? result = null;
        var ok = new Button { Content = "OK", Width = 100, IsDefault = true, Style = (Style)Application.Current.FindResource("BtnPrimary") };
        ok.Click += (_, _) =>
        {
            result = inputs.Select(i => i is PasswordBox p ? p.Password : ((TextBox)i).Text).ToList();
            w.DialogResult = true;
        };
        panel.Children.Add(Buttons(ok));
        w.Content = panel;
        w.Loaded += (_, _) =>
        {
            // Сразу в первое пустое поле (для Get-Credential с именем — в пароль).
            var first = inputs.FirstOrDefault(i => i is PasswordBox || i is TextBox { Text.Length: 0 }) ?? inputs.FirstOrDefault();
            first?.Focus();
        };
        return w.ShowDialog() == true ? result : null;
    }

    private static List<string>? AskChoice(Window? owner, PsMsg m)
    {
        var w = NewWindow(owner, m.Caption);
        var panel = new StackPanel { Margin = new Thickness(18) };
        if (!string.IsNullOrWhiteSpace(m.Message)) panel.Children.Add(Text(m.Message!, 14));

        string? picked = null;
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var choices = m.Choices ?? new List<string>();
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = choices[i],
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = i == m.Default,
                Style = (Style)Application.Current.FindResource(i == m.Default ? "BtnPrimary" : "Btn")
            };
            b.Click += (_, _) =>
            {
                picked = index.ToString();
                w.DialogResult = true;
            };
            row.Children.Add(b);
        }

        panel.Children.Add(row);
        w.Content = panel;
        return w.ShowDialog() == true && picked is not null ? new List<string> { picked } : null;
    }

    private static StackPanel Buttons(Button ok)
    {
        var cancel = new Button { Content = "Отмена", Width = 100, IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("Btn") };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(cancel);
        row.Children.Add(ok);
        return row;
    }
}
