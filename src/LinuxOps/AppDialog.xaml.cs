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

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
