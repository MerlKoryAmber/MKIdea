using LinuxOps.Services;
using LinuxOps.Web.Services.Auth;
using LinuxOps.Web.Services.Guac;
using Renci.SshNet;

namespace LinuxOps.Web.Services;

/// <summary>Сеансы через guacd (Ф3): терминал SSH. Права — на хост; секреты — в пропуск сервиса, браузеру только номер.</summary>
public sealed partial class WebSession
{
    /// <summary>
    /// Открыть терминал SSH: право «Терминал» на хост, проверка ключа хоста (как в Actions — спросит, если новый),
    /// пропуск для туннеля. null — отказ (причина — в Notice).
    /// </summary>
    public async Task<string?> OpenTerminal(StoredHost h, GuacTickets tickets)
    {
        if (h.IsWindows) return null;
        if (!AllowedOn(Perm.Terminal, h, "терминал")) return null;
        var cfg = _store.ForConnect(h);
        WebPrompts.Channel.Value = Prompts;
        string hostKey;
        try
        {
            hostKey = await Task.Run(() => ProbeHostKey(cfg));
        }
        catch (Exception ex)
        {
            _audit.Write(Who, "терминал: не открыт", $"{h.Config.Title}: {ex.Message}");
            ShowNotice($"{h.Config.Title}: {ex.Message}");
            return null;
        }

        var p = new Dictionary<string, string>
        {
            ["hostname"] = cfg.Host,
            ["port"] = (cfg.Port <= 0 ? 22 : cfg.Port).ToString(),
            ["username"] = cfg.Username,
            ["host-key"] = hostKey,
            ["terminal-type"] = "xterm-256color",
            ["font-name"] = "monospace",
            ["font-size"] = "12",
            ["scrollback"] = "3000",
            ["color-scheme"] = "gray-black",
            ["recording-path"] = WebPaths.RecordingsDir,
            ["create-recording-path"] = "true",
            ["typescript-path"] = WebPaths.RecordingsDir,
            ["create-typescript-path"] = "true",
            ["server-alive-interval"] = "30"
        };
        if (cfg.Auth == "Password") p["password"] = cfg.Password;
        else p["private-key"] = cfg.PrivateKeyText.Replace("\r\n", "\n");

        var t = new GuacTicket { Login = User.Login, Who = Who, Protocol = "ssh", HostId = h.Id, HostTitle = h.Config.Title, Params = p };
        // имя записи = номер сеанса: по нему запись находится в журнале сеансов
        p["recording-name"] = t.Id + ".guac";
        p["typescript-name"] = t.Id + ".txt";
        RecordKeys(p);
        return tickets.Issue(t);
    }

    /// <summary>Учётка для RDP по умолчанию: вошли через AD — свой логин (домен подставится сам).</summary>
    public string RdpDefaultLogin => User.Kind == "ad" ? User.Login : "";

    /// <summary>
    /// Открыть RDP: право «RDP» на хост; вход — учётка пользователя (DOMAIN\user, user@domain или user — тогда домен из настроек AD).
    /// Пароль живёт только в пропуске (60 с, в памяти) и уходит в guacd; нигде не сохраняется. null — отказ.
    /// </summary>
    public string? OpenRdp(StoredHost h, string login, string password, GuacTickets tickets)
    {
        if (!h.IsWindows) return null;
        if (!AllowedOn(Perm.Rdp, h, "RDP")) return null;
        login = login.Trim();
        if (login.Length == 0 || password.Length == 0) { ShowNotice("Введите учётную запись и пароль."); return null; }
        string user = login, domain = "";
        var slash = login.IndexOf('\\');
        if (slash > 0) { domain = login[..slash]; user = login[(slash + 1)..]; }
        else if (!login.Contains('@')) domain = _store.LoadAuth().AdDomain;

        var p = new Dictionary<string, string>
        {
            ["hostname"] = h.Config.Host,
            ["port"] = (h.Config.Port is <= 0 ? 3389 : h.Config.Port).ToString(),
            ["username"] = user,
            ["password"] = password,
            ["domain"] = domain,
            // NLA: учётка проверяется до сеанса, отказ приходит понятной ошибкой; сертификат RDP в домене обычно самоподписанный
            ["security"] = "nla",
            ["ignore-cert"] = "true",
            ["resize-method"] = "display-update",
            ["disable-audio"] = "true",
            // без GFX (H.264) — обычная графика RDP: проверена с guacamole-common-js 1.5 в браузере
            ["disable-gfx"] = "true",
            ["enable-wallpaper"] = "false",
            ["enable-theming"] = "true",
            ["enable-font-smoothing"] = "true",
            ["recording-path"] = WebPaths.RecordingsDir,
            ["create-recording-path"] = "true"
        };
        var t = new GuacTicket { Login = User.Login, Who = Who, Protocol = "rdp", HostId = h.Id, HostTitle = h.Config.Title, Params = p };
        p["recording-name"] = t.Id + ".guac";
        RecordKeys(p);
        _audit.Write(Who, "RDP: вход", $"{h.Config.Title}: учётка {(domain.Length > 0 ? domain + "\\" : "")}{user}");
        return tickets.Issue(t);
    }

    /// <summary>Нажатия клавиш — в запись, если включено в настройках (пароли, набранные внутри сеанса, тоже попадут).</summary>
    private void RecordKeys(Dictionary<string, string> p)
    {
        if (_store.GetSetting("rec.keys") == "1") p["recording-include-keys"] = "true";
    }

    /// <summary>«Проверить порт» в окне «Хост Windows»: TCP до RDP за 3 с.</summary>
    public async Task<(bool Ok, string Text)> TestRdpPort(string host, int port)
    {
        if (!Can(Perm.HostsEdit)) return (false, "Нет права менять хосты.");
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, cts.Token);
            return (true, $"Порт {port} открыт.");
        }
        catch (OperationCanceledException) { return (false, $"{host}:{port} не ответил за 3 с."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Порядок типов ключа хоста, как их выбирает libssh2 в guacd (сначала ECDSA, потом Ed25519, потом RSA).</summary>
    private static readonly string[] Libssh2KeyOrder =
        ["ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521", "ssh-ed25519", "ssh-rsa"];

    /// <summary>
    /// Подключиться по SSH, проверить ключ хоста (KnownHostsStore; новый — вопрос в окне) и вход;
    /// вернуть ключ для guacd в формате known_hosts («хост тип base64»): guacd сверит его сам.
    /// guacd принимает одну строку и договаривается о типе ключа по-своему — поэтому берём ключ того типа,
    /// который выберет libssh2, прочитав его по уже проверенному соединению.
    /// </summary>
    private static string ProbeHostKey(LinuxOps.Models.HostConfig cfg)
    {
        string? type = null, key = null;
        var port = cfg.Port <= 0 ? 22 : cfg.Port;
        var who = port == 22 ? cfg.Host : $"[{cfg.Host}]:{port}";
        using var client = new SshClient(SshAuth.Build(cfg, TimeSpan.FromSeconds(60)));
        SshAuth.AttachHostKey(client, cfg);
        client.HostKeyReceived += (_, e) => { type = e.HostKeyName; key = Convert.ToBase64String(e.HostKey); };
        try { client.Connect(); }
        catch (Renci.SshNet.Common.SshConnectionException ex) when (ex.Message.Contains("Key exchange negotiation failed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ключ хоста не подтверждён — подключение отклонено");
        }
        catch (Renci.SshNet.Common.SshAuthenticationException)
        {
            throw new InvalidOperationException($"сервер не принял учётку {cfg.Username} — проверьте пароль или ключ в Inventory");
        }
        catch (Renci.SshNet.Common.SshOperationTimeoutException)
        {
            throw new InvalidOperationException("хост не ответил вовремя");
        }
        if (type is null || key is null) throw new InvalidOperationException("хост не прислал ключ");
        // алгоритм подписи RSA (rsa-sha2-*) — в known_hosts тип ключа ssh-rsa
        if (type.StartsWith("rsa-sha2-", StringComparison.Ordinal)) type = "ssh-rsa";

        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cmd = client.RunCommand("cat /etc/ssh/ssh_host_*_key.pub 2>/dev/null");
            foreach (var pub in (cmd.Result ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = pub.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 2 && Libssh2KeyOrder.Contains(f[0])) read.TryAdd(f[0], f[1]);
            }
        }
        catch (Renci.SshNet.Common.SshException) { }
        client.Disconnect();
        // прочитанным файлам верим, только если среди них есть проверенный ключ (значит, это ключи этого sshd)
        var keys = read.GetValueOrDefault(type) == key ? read : new Dictionary<string, string> { [type] = key };
        var pick = Libssh2KeyOrder.FirstOrDefault(keys.ContainsKey) ?? type;
        return $"{who} {pick} {keys[pick]}";
    }
}
