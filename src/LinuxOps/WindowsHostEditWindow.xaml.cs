using System.Net;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;

namespace LinuxOps;

/// <summary>Хост Windows (RDP) в Inventory: имя, адрес, учётка, режим входа, что подключать по умолчанию. Пароль не хранится.</summary>
public partial class WindowsHostEditWindow : Window
{
    private readonly WindowsHost? _existing;

    public WindowsHost Result { get; private set; } = new();

    public WindowsHostEditWindow(WindowsHost? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        var auth = existing?.Auth ?? "rcg";
        RcgBox.IsChecked = auth == "rcg";
        RestrictedBox.IsChecked = auth == "restricted";
        if (existing is null) Title = "Новый хост Windows";
        else
        {
            NameBox.Text = existing.Name;
            HostBox.Text = existing.Host;
            PortBox.Text = existing.Port.ToString();
            UserBox.Text = existing.User;
            AdminBox.IsChecked = existing.AdminConsole;
            DrivesBox.IsChecked = existing.RedirectDrives;
            PrintersBox.IsChecked = existing.RedirectPrinters;
        }

        Loaded += (_, _) => (existing is null ? HostBox : NameBox).Focus();
    }

    // Remote Credential Guard и Restricted Admin вместе не бывают: отметили одну — другая снимается.
    private void Rcg_Checked(object sender, RoutedEventArgs e) => RestrictedBox.IsChecked = false;

    private void Restricted_Checked(object sender, RoutedEventArgs e) => RcgBox.IsChecked = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0 || host.Contains(' ') || host.Contains('/') || host.Contains('\\'))
        {
            AppDialog.Info(this, "Укажите адрес: имя или IP, без пробелов и косых черт.", "Хост Windows");
            HostBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            AppDialog.Info(this, "Порт — число от 1 до 65535 (обычно 3389).", "Хост Windows");
            PortBox.Focus();
            return;
        }

        var auth = WindowsHost.AuthFrom(RcgBox.IsChecked == true, RestrictedBox.IsChecked == true);
        if (auth != "password" && IPAddress.TryParse(host, out _) &&
            !AppDialog.Confirm(this, $"{WindowsHost.AuthLabel(auth)} работает только через Kerberos, по имени хоста. По IP подключение не пройдёт. Сохранить с IP всё равно?", "Хост Windows"))
            return;

        Result = new WindowsHost
        {
            Name = NameBox.Text.Trim(),
            Host = host,
            Port = port,
            User = UserBox.Text.Trim(),
            Auth = auth,
            AdminConsole = AdminBox.IsChecked == true,
            RedirectDrives = DrivesBox.IsChecked == true,
            RedirectPrinters = PrintersBox.IsChecked == true,
            FolderId = _existing?.FolderId ?? "",
            IsSelected = _existing?.IsSelected ?? true
        };
        DialogResult = true;
    }
}
