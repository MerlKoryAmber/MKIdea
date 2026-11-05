using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

public partial class HostEditWindow : Window
{
    private string _distroHint = string.Empty;
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
        _distroHint = existing.DistroHint ?? string.Empty;
        if (!string.IsNullOrEmpty(existing.Password))
            PassBox.Password = existing.Password;
        UseSudoBox.IsChecked = existing.UseSudo;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var host)) return;
        Result = host;
        DialogResult = true;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var dlg = new OpenFileDialog
        {
            Filter = "Ключи SSH|*.*|Все файлы|*.*",
            InitialDirectory = Directory.Exists(sshDir) ? sshDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dlg.ShowDialog(this) == true)
            KeyBox.Text = dlg.FileName;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var host)) return;
        // Подключение — в фоне: запросы ключа хоста и пароля ключа идут через Dispatcher и ждут UI-поток.
        var button = (Button)sender;
        button.IsEnabled = false;
        try
        {
            var d = await Task.Run(() =>
            {
                using var ssh = SshRunner.Connect(host);
                return ssh.DetectDistro();
            });
            AppDialog.Info(this, $"Подключено. {d.PrettyName} ({d.Id})", "Проверка");
        }
        catch (Exception ex)
        {
            AppDialog.Info(this, ex.Message, "Проверка");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private bool TryRead(out HostConfig host)
    {
        host = new HostConfig();
        if (string.IsNullOrWhiteSpace(HostBox.Text) || string.IsNullOrWhiteSpace(UserBox.Text))
        {
            AppDialog.Info(this, "Нужны Host и User.");
            return false;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port <= 0)
            port = 22;

        var auth = (AuthBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Key";
        host = new HostConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? HostBox.Text.Trim() : NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UserBox.Text.Trim(),
            Auth = auth,
            PrivateKeyPath = KeyBox.Text.Trim(),
            Password = PassBox.Password,
            UseSudo = UseSudoBox.IsChecked == true,
            DistroHint = _distroHint,
            IsSelected = Result.IsSelected
        };
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
