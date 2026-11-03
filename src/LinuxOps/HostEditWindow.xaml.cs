using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;

namespace LinuxOps;

public partial class HostEditWindow : Window
{
    public HostConfig Result { get; private set; } = new();

    public HostEditWindow(HostConfig? existing = null)
    {
        InitializeComponent();
        if (existing is null) return;

        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port > 0 ? existing.Port.ToString() : "22";
        UserBox.Text = existing.Username;
        KeyBox.Text = existing.PrivateKeyPath;
        AuthBox.SelectedIndex = string.Equals(existing.Auth, "Password", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Result.IsSelected = existing.IsSelected;
        if (!string.IsNullOrEmpty(existing.Password))
            PassBox.Password = existing.Password;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
        {
            AppDialog.Info(this, "Нужны Host и User.");
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0)
            port = 22;

        var auth = (AuthBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Key";
        Result = new HostConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text.Trim() : NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UserBox.Text.Trim(),
            Auth = auth,
            PrivateKeyPath = KeyBox.Text.Trim(),
            Password = PassBox.Password,
            IsSelected = Result.IsSelected
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
