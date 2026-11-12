using System.Windows;

namespace LinuxOps;

public partial class AppDialog : Window
{
    public AppDialog(string message, bool confirm = false, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(title))
            Title = title!;
        MessageText.Text = message;
        if (!confirm)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            OkBtn.Content = "OK";
            IconText.Text = "i";
        }
        else
        {
            OkBtn.Content = "Да";
            CancelBtn.Visibility = Visibility.Visible;
            IconText.Text = "?";
            IconBox.Background = (System.Windows.Media.Brush)FindResource("WarnBgBrush");
            IconText.Foreground = (System.Windows.Media.Brush)FindResource("WarnBrush");
        }
    }

    public static void Info(Window owner, string message, string? title = null)
        => new AppDialog(message, confirm: false, title) { Owner = owner }.ShowDialog();

    public static bool Confirm(Window owner, string message, string? title = null)
        => new AppDialog(message, confirm: true, title) { Owner = owner }.ShowDialog() == true;

    /// <summary>Несохранённые изменения: true — сохранить, false — не сохранять, null — отмена.</summary>
    /// <summary>Строка от пользователя (имя папки и т.п.): null — отмена или пусто.</summary>
    public static string? AskText(Window owner, string message, string initial = "", string? title = null)
    {
        var d = new AppDialog(message, confirm: true, title) { Owner = owner };
        d.OkBtn.Content = "OK";
        d.IconText.Text = "✎";
        d.IconBox.Background = (System.Windows.Media.Brush)d.FindResource("BadgeBgBrush");
        d.IconText.Foreground = (System.Windows.Media.Brush)d.FindResource("AccentDeepBrush");
        d.InputBox.Visibility = Visibility.Visible;
        d.InputBox.Text = initial;
        d.Loaded += (_, _) =>
        {
            d.InputBox.Focus();
            d.InputBox.SelectAll();
        };
        if (d.ShowDialog() != true) return null;
        var v = d.InputBox.Text.Trim();
        return v.Length == 0 ? null : v;
    }

    public static bool? AskSave(Window owner, string message, string? title = null)
    {
        var d = new AppDialog(message, confirm: true, title) { Owner = owner };
        d.OkBtn.Content = "Сохранить";
        d.NoBtn.Visibility = Visibility.Visible;
        if (d.ShowDialog() == true) return true;
        return d._no ? false : null;
    }

    private bool _no;

    private void No_Click(object sender, RoutedEventArgs e)
    {
        _no = true;
        DialogResult = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
