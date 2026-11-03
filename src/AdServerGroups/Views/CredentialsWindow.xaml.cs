using System.Windows;

namespace AdServerGroups.Views;

public partial class CredentialsWindow : Window
{
    public string Username => UsernameBox.Text.Trim();
    public string Password => PasswordBox.Password;

    public CredentialsWindow(string? suggestedUsername)
    {
        InitializeComponent();
        UsernameBox.Text = suggestedUsername ?? string.Empty;
        if (string.IsNullOrWhiteSpace(UsernameBox.Text))
            UsernameBox.Focus();
        else
            PasswordBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorText.Text = "Укажите учётную запись и пароль.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
