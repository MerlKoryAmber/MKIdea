using System.Windows;

namespace AdServerGroups.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string message, string? confirmButtonText = null)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        if (!string.IsNullOrWhiteSpace(confirmButtonText))
            YesButton.Content = confirmButtonText;
    }

    public static bool Ask(Window owner, string title, string message, string? confirmButtonText = null)
    {
        var wnd = new ConfirmWindow(title, message, confirmButtonText) { Owner = owner };
        return wnd.ShowDialog() == true;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
