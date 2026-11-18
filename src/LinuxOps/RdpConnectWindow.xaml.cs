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
        TestCredential.Apply(UserBox, PassBox);
        _loaded = true;
        UpdateMode();
    }

    private bool _loaded;

    /// <summary>Учётка, под которой запущена программа: ДОМЕН\логин.</summary>
    public static string CurrentAccount => $@"{Environment.UserDomainName}\{Environment.UserName}";

    /// <summary>Вошли в Windows локальной учёткой (домен = имя этого ПК) — Kerberos к серверу не будет.</summary>
    private static bool LocalAccount => string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public RdpOptions Result { get; private set; } = new();

    private string Auth => WindowsHost.AuthFrom(RcgBox.IsChecked == true, RestrictedBox.IsChecked == true);

    // Remote Credential Guard и Restricted Admin вместе не бывают: отметили одну — другая снимается.
    private void Rcg_Checked(object sender, RoutedEventArgs e)
    {
        RestrictedBox.IsChecked = false;
        UpdateMode();
    }

    private void Restricted_Checked(object sender, RoutedEventArgs e)
    {
        RcgBox.IsChecked = false;
        UpdateMode();
    }

    private void Auth_Changed(object sender, RoutedEventArgs e) => UpdateMode();

    /// <summary>
    /// RCG и Restricted Admin входят на сервер текущей учёткой Windows через Kerberos — логин и пароль не нужны,
    /// поля скрываются. Обычный вход — логин и пароль.
    /// </summary>
    private void UpdateMode()
    {
        if (!_loaded) return;
        var sso = Auth != "password";
        CredPanel.Visibility = sso ? Visibility.Collapsed : Visibility.Visible;
        SsoPanel.Visibility = sso ? Visibility.Visible : Visibility.Collapsed;
        if (sso)
        {
            SsoText.Text = $"Вход вашей учёткой Windows: {CurrentAccount}. Пароль не нужен и на сервер не уходит " +
                           (Auth == "rcg" ? "(Kerberos, учётные данные остаются на этом ПК)." : "(Kerberos, на сервере — сеанс без учётных данных).");
            SsoWarn.Text = LocalAccount
                ? "Вы вошли в Windows локальной учёткой — так войти не получится. Снимите галочку, чтобы войти по паролю."
                : "";
            SsoWarn.Visibility = SsoWarn.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (sso) ConnectBtn.Focus();
            else if (UserBox.Text.Length == 0) UserBox.Focus();
            else PassBox.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var sso = Auth != "password";
        var user = sso ? "" : UserBox.Text.Trim();
        if (!sso && (user.Length == 0 || PassBox.Password.Length == 0))
        {
            AppDialog.Info(this, "Для входа по паролю нужны учётная запись и пароль.", "Подключение RDP");
            (user.Length == 0 ? (Control)UserBox : PassBox).Focus();
            return;
        }

        Result = new RdpOptions
        {
            User = user,
            Password = sso ? "" : PassBox.Password,
            Auth = Auth,
            AdminConsole = AdminBox.IsChecked == true,
            RedirectDrives = DrivesBox.IsChecked == true,
            RedirectPrinters = PrintersBox.IsChecked == true
        };
        DialogResult = true;
    }
}
