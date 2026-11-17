using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;

namespace LinuxOps;

/// <summary>Что выбрано при подключении RDP. Пароль — только в памяти, до закрытия вкладки сессии.</summary>
public sealed class RdpOptions
{
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
    /// <summary>rcg | restricted | password — как <see cref="WindowsHost.Auth"/>.</summary>
    public string Auth { get; init; } = "rcg";
    public bool AdminConsole { get; init; }
    public bool RedirectDrives { get; init; }
    public bool RedirectPrinters { get; init; }
}

/// <summary>Окно подключения RDP: спрашивается при каждом подключении. Логин и галочки — из хоста.</summary>
public partial class RdpConnectWindow : Window
{
    public RdpConnectWindow(WindowsHost host)
    {
        InitializeComponent();
        HostText.Text = $"{host.Title} · {host.Host}" + (host.Port != 3389 ? $":{host.Port}" : "");
        UserBox.Text = host.User;
        AdminBox.IsChecked = host.AdminConsole;
        DrivesBox.IsChecked = host.RedirectDrives;
        PrintersBox.IsChecked = host.RedirectPrinters;
        RcgBox.IsChecked = host.Auth == "rcg";
        RestrictedBox.IsChecked = host.Auth == "restricted";
        UpdateHint();
        TestCredential.Apply(UserBox, PassBox);
        Loaded += (_, _) =>
        {
            if (UserBox.Text.Length == 0) UserBox.Focus();
            else PassBox.Focus();
        };
    }

    public RdpOptions Result { get; private set; } = new();

    private string Auth => WindowsHost.AuthFrom(RcgBox.IsChecked == true, RestrictedBox.IsChecked == true);

    // Remote Credential Guard и Restricted Admin вместе не бывают: отметили одну — другая снимается.
    private void Rcg_Checked(object sender, RoutedEventArgs e)
    {
        RestrictedBox.IsChecked = false;
        UpdateHint();
    }

    private void Restricted_Checked(object sender, RoutedEventArgs e)
    {
        RcgBox.IsChecked = false;
        UpdateHint();
    }

    private void Auth_Changed(object sender, RoutedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (PassHint is null || RcgBox is null || RestrictedBox is null) return;
        PassHint.Text = Auth == "password"
            ? "Пароль уходит на сервер (CredSSP), как в обычном mstsc."
            : "Пусто — текущая учётка Windows (только если вы вошли в Windows доменной учёткой). Пароль на сервер не уходит: вход через Kerberos.";
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        if (Auth == "password" && (user.Length == 0 || PassBox.Password.Length == 0))
        {
            AppDialog.Info(this, "Для обычного входа нужны учётная запись и пароль.", "Подключение RDP");
            (user.Length == 0 ? (Control)UserBox : PassBox).Focus();
            return;
        }

        if (PassBox.Password.Length > 0 && user.Length == 0)
        {
            AppDialog.Info(this, "Укажите учётную запись для этого пароля.", "Подключение RDP");
            UserBox.Focus();
            return;
        }

        Result = new RdpOptions
        {
            User = user,
            Password = PassBox.Password,
            Auth = Auth,
            AdminConsole = AdminBox.IsChecked == true,
            RedirectDrives = DrivesBox.IsChecked == true,
            RedirectPrinters = PrintersBox.IsChecked == true
        };
        DialogResult = true;
    }
}
