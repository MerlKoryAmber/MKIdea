using System.Windows;
using LinuxOps.Models;

namespace LinuxOps;

/// <summary>Запрос секрета на время сессии: пароль sudo или пароль ключа SSH.</summary>
public partial class SudoPasswordWindow : Window
{
    public string Password { get; private set; } = string.Empty;

    public SudoPasswordWindow(HostConfig host)
        : this("Пароль sudo", $"sudo на {host.Username}@{host.Host}. Пароль только в памяти этой сессии, на диск не пишется.")
    {
    }

    public SudoPasswordWindow(string title, string hint)
    {
        InitializeComponent();
        Title = title;
        HintText.Text = hint;
        PassBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Password = PassBox.Password;
        DialogResult = true;
    }
}
