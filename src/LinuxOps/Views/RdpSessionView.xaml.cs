using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>
/// RDP-сессия во вкладке «Подключения» на встроенном mstscax. Каждое подключение — новый экземпляр клиента
/// (настройки входа меняются только до Connect). Размер рабочего стола следует за вкладкой.
/// </summary>
public partial class RdpSessionView : UserControl, IDisposable
{
    private readonly DispatcherTimer _resize;
    private RdpControl? _rdp;
    private bool _userDisconnect;
    private bool _disposed;

    public RdpSessionView(RdpSessionViewModel vm)
    {
        InitializeComponent();
        Vm = vm;
        DataContext = vm;
        Focusable = true;
        _resize = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            ResizeDesktop();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Vm.Connected && !Vm.Connecting)
            {
                e.Handled = true;
                Connect();
            }
        };
    }

    public RdpSessionViewModel Vm { get; }

    /// <summary>Подключиться (заново) с <see cref="RdpSessionViewModel.Options"/>.</summary>
    public void Connect()
    {
        if (_disposed) return;
        DisposeClient();
        _userDisconnect = false;
        Vm.Connecting = true;
        Vm.Status = $"Подключение к {Vm.Host.Host}…";
        Placeholder.Visibility = Visibility.Collapsed;
        Desk.Visibility = Visibility.Visible;
        var rdp = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };
        _rdp = rdp;
        rdp.Sink.Connecting += () => Vm.Status = $"Подключение к {Vm.Host.Host}…";
        rdp.Sink.LoginComplete += () =>
        {
            Vm.Connecting = false;
            Vm.Connected = true;
            Vm.Status = $"{Vm.Host.Title} · {(Vm.Options.User.Length > 0 ? Vm.Options.User : "текущая учётка Windows")} · {Vm.ModeText}";
            FocusSession();
        };
        rdp.Sink.Disconnected += reason => OnDisconnected(rdp, reason);
        rdp.Sink.FatalError += code => Vm.Status = $"Ошибка клиента RDP (код {code})";
        // Из полного экрана клиент возвращается с разрешением экрана и не перерисовывается во вкладке —
        // вернуть размер вкладки и перерисовать.
        rdp.Sink.FullScreenChanged += full =>
        {
            if (full) return;
            var t = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!ReferenceEquals(rdp, _rdp) || rdp.IsDisposed) return;
                ResizeDesktop();
                rdp.Invalidate(true);
                rdp.Update();
                FocusSession();
            };
            t.Start();
        };
        Desk.Child = rdp;
        // Окно клиента создаётся, когда WindowsFormsHost в дереве и видим; настройки — после этого.
        Dispatcher.BeginInvoke(() => Start(rdp), DispatcherPriority.Loaded);
    }

    private void Start(RdpControl rdp)
    {
        if (!ReferenceEquals(rdp, _rdp)) return;
        try
        {
            rdp.CreateControl();
            Configure(rdp);
            rdp.Client.Connect();
        }
        catch (Exception ex)
        {
            Fail("Не удалось начать подключение: " + ex.Message);
        }
    }

    private void Configure(RdpControl rdp)
    {
        var o = Vm.Options;
        var c = rdp.Client;
        c.Server = Vm.Host.Host;
        var (domain, user) = SplitUser(o.User);
        if (user.Length > 0) c.UserName = user;
        if (domain.Length > 0) c.Domain = domain;
        c.ColorDepth = 32;
        c.FullScreenTitle = $"{Vm.Host.Title} — LinuxOps";
        var (w, h) = DesktopSize();
        c.DesktopWidth = w;
        c.DesktopHeight = h;

        var a = c.AdvancedSettings9;
        a.RDPPort = Vm.Host.Port;
        a.EnableCredSspSupport = true;
        // 2 — сервер не подтвердил подлинность: предупредить и спросить (как mstsc).
        a.AuthenticationLevel = 2;
        a.RedirectClipboard = true;
        a.RedirectDrives = o.RedirectDrives;
        a.RedirectPrinters = o.RedirectPrinters;
        a.ConnectToAdministerServer = o.AdminConsole;
        a.EnableAutoReconnect = true;
        a.MaxReconnectAttempts = 20;
        a.SmartSizing = false;
        a.BitmapPersistence = 1;
        if (o.Password.Length > 0) a.ClearTextPassword = o.Password;

        switch (o.Auth)
        {
            case "rcg":
                rdp.SetExtended("RedirectedAuthentication", true);
                rdp.SetExtended("DisableCredentialsDelegation", true);
                break;
            case "restricted":
                rdp.SetExtended("RestrictedLogon", true);
                rdp.SetExtended("DisableCredentialsDelegation", true);
                break;
        }

        try
        {
            rdp.SetExtended("DesktopScaleFactor", (uint)ScalePercent());
            rdp.SetExtended("DeviceScaleFactor", 100u);
        }
        catch
        {
            // старый клиент без масштаба — рабочий стол в 100 %
        }
    }

    /// <summary>Причины отключения mstscax «не достучались»: DNS, тайм-аут, сокет. Вход тут ни при чём.</summary>
    private static readonly HashSet<int> NetworkReasons = new() { 260, 264, 516, 520, 772, 1028, 1032, 2308 };

    private void OnDisconnected(RdpControl rdp, int reason)
    {
        if (!ReferenceEquals(rdp, _rdp)) return;
        var wasConnected = Vm.Connected;
        Vm.Connected = false;
        Vm.Connecting = false;
        string text;
        if (_userDisconnect || reason == 1) text = "Отключено";
        else
        {
            string desc;
            try
            {
                desc = (string)rdp.Client.GetErrorDescription((uint)reason, (uint)(int)rdp.Client.ExtendedDisconnectReason);
            }
            catch
            {
                desc = "";
            }

            desc = desc.Replace("\r", " ").Replace("\n", " ").Trim();
            var network = NetworkReasons.Contains(reason);
            text = reason switch
            {
                2 => "Сеанс на сервере завершён",
                3 => "Сервер разорвал подключение",
                260 => $"Имя {Vm.Host.Host} не найдено (DNS)",
                264 => $"{Vm.Host.Host}: время ожидания подключения истекло",
                516 or 520 => $"{Vm.Host.Host}:{Vm.Host.Port} не отвечает: удалённый рабочий стол на сервере выключен, хост выключен или порт закрыт брандмауэром",
                2055 => "Неверная учётная запись или пароль",
                _ => desc.Length > 0 ? desc : $"Подключение не удалось (код {reason})"
            };
            // Подсказка про Kerberos — только когда до сервера достучались, а вход не прошёл.
            if (!wasConnected && !network && Vm.Options.Auth != "password" && reason != 2 && reason != 3)
                text += $" · {Models.WindowsHost.AuthLabel(Vm.Options.Auth)} работает только через Kerberos: нужен доступ к контроллеру домена и вход по имени хоста.";
        }

        Fail(text);
    }

    private void Fail(string text)
    {
        Vm.Connected = false;
        Vm.Connecting = false;
        Vm.Status = text;
        Desk.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
    }

    public void Disconnect()
    {
        if (_rdp is null || !Vm.Connected) return;
        _userDisconnect = true;
        try
        {
            _rdp.Client.Disconnect();
        }
        catch
        {
            Fail("Отключено");
        }
    }

    public void FocusSession()
    {
        if (Vm.Connected && _rdp is { IsDisposed: false } rdp)
            Dispatcher.BeginInvoke(() =>
            {
                Desk.Focus();
                rdp.Focus();
            }, DispatcherPriority.Input);
        else Focus();
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Connected &&
            !AppDialog.Confirm(Window.GetWindow(this)!, $"Сессия {Vm.Host.Title} активна. Переподключить?", "Подключения"))
            return;
        Connect();
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Disconnect();

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_rdp is not null && Vm.Connected) _rdp.Client.FullScreen = true;
        }
        catch (Exception ex)
        {
            AppDialog.Info(Window.GetWindow(this)!, ex.Message, "Полный экран");
        }
    }

    /// <summary>Тот же хост в mstsc: временный .rdp без пароля, режим входа — ключом командной строки.</summary>
    private void Mstsc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var o = Vm.Options;
            var dir = Path.Combine(Path.GetTempPath(), "LinuxOps", "rdp");
            Directory.CreateDirectory(dir);
            var safe = string.Concat(Vm.Host.Title.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var file = Path.Combine(dir, safe + ".rdp");
            var sb = new StringBuilder();
            sb.AppendLine($"full address:s:{Vm.Host.Host}:{Vm.Host.Port}");
            if (o.User.Length > 0) sb.AppendLine($"username:s:{o.User}");
            sb.AppendLine("screen mode id:i:1");
            sb.AppendLine("redirectclipboard:i:1");
            sb.AppendLine($"redirectprinters:i:{(o.RedirectPrinters ? 1 : 0)}");
            sb.AppendLine(o.RedirectDrives ? "drivestoredirect:s:*" : "drivestoredirect:s:");
            sb.AppendLine("enablecredsspsupport:i:1");
            sb.AppendLine("authentication level:i:2");
            File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
            var args = $"\"{file}\"" + (o.AdminConsole ? " /admin" : "") + o.Auth switch
            {
                "rcg" => " /remoteGuard",
                "restricted" => " /restrictedAdmin",
                _ => ""
            };
            Process.Start(new ProcessStartInfo("mstsc.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppDialog.Info(Window.GetWindow(this)!, ex.Message, "mstsc");
        }
    }

    private void Desk_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!Vm.Connected) return;
        _resize.Stop();
        _resize.Start();
    }

    /// <summary>Рабочий стол — по размеру вкладки (динамическое разрешение, как у mstsc при изменении окна).</summary>
    private void ResizeDesktop()
    {
        if (_rdp is null || !Vm.Connected) return;
        var (w, h) = DesktopSize();
        try
        {
            _rdp.Client.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, (uint)ScalePercent(), 100u);
        }
        catch
        {
            // сервер без динамического разрешения — останется прежний размер
        }
    }

    private (int W, int H) DesktopSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Round(Math.Max(Desk.ActualWidth, 200) * dpi.DpiScaleX);
        var h = (int)Math.Round(Math.Max(Desk.ActualHeight, 200) * dpi.DpiScaleY);
        // Клиент принимает чётную ширину от 200 до 8192.
        w = Math.Clamp(w & ~1, 200, 8192);
        h = Math.Clamp(h & ~1, 200, 8192);
        return (w, h);
    }

    private int ScalePercent()
    {
        var p = (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
        return Math.Clamp(p, 100, 500);
    }

    /// <summary>ДОМЕН\логин → (ДОМЕН, логин); логин@домен и просто логин — целиком в UserName.</summary>
    private static (string Domain, string User) SplitUser(string login)
    {
        var i = login.IndexOf('\\');
        return i > 0 ? (login[..i], login[(i + 1)..]) : ("", login);
    }

    private void DisposeClient()
    {
        var rdp = _rdp;
        _rdp = null;
        if (rdp is null) return;
        Desk.Child = null;
        try
        {
            if (!rdp.IsDisposed && (int)rdp.Client.Connected != 0) rdp.Client.Disconnect();
        }
        catch
        {
            // уже отключён
        }

        try
        {
            rdp.Dispose();
        }
        catch
        {
            // ActiveX мог разрушиться раньше
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resize.Stop();
        DisposeClient();
        Desk.Dispose();
    }
}
