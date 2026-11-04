using System.Windows;
using LinuxOps.Models;

namespace LinuxOps;

public partial class SudoPasswordWindow : Window
{
    public string Password { get; private set; } = string.Empty;

    public SudoPasswordWindow(HostConfig host)
    {
        InitializeComponent();
        HintText.Text = $"sudo на {host.Username}@{host.Host}. Пароль только в памяти этой сессии, на диск не пишется.";
        PassBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PassBox.Password;
        DialogResult = true;
    }
}
