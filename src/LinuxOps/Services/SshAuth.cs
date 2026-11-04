using System.IO;
using LinuxOps.Models;
using Renci.SshNet;

namespace LinuxOps.Services;

public static class SshAuth
{
    public static ConnectionInfo Build(HostConfig host, TimeSpan? timeout = null)
    {
        var info = new ConnectionInfo(
            host.Host,
            host.Port <= 0 ? 22 : host.Port,
            host.Username,
            BuildAuth(host))
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        return info;
    }

    public static AuthenticationMethod BuildAuth(HostConfig host)
    {
        if (string.Equals(host.Auth, "Password", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(host.Password))
                throw new InvalidOperationException($"Хост {host.Display}: не задан пароль.");
            if (host.Password.StartsWith(DefaultsStore.PasswordPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Хост {host.Display}: пароль не расшифровался (DPAPI, другой пользователь Windows).");
            return new PasswordAuthenticationMethod(host.Username, host.Password);
        }

        var keyPath = Environment.ExpandEnvironmentVariables(host.PrivateKeyPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException($"Хост {host.Display}: ключ не найден: {keyPath}");

        return new PrivateKeyAuthenticationMethod(host.Username, new PrivateKeyFile(keyPath));
    }

    public static void AttachHostKey(BaseClient client, HostConfig host)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            var port = host.Port <= 0 ? 22 : host.Port;
            var known = KnownHostsStore.Find(host.Host, port);
            if (known is null)
            {
                var trust = AskKey($"{host.Display}\nОтпечаток {fp}\nДоверять?", changed: false);
                e.CanTrust = trust;
                if (trust) KnownHostsStore.Remember(host.Host, port, fp);
                return;
            }

            if (string.Equals(known, fp, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }

            var accept = AskKey(
                $"{host.Display}\nКлюч хоста изменился.\nБыло: {known}\nСтало: {fp}\nПодключение отклонено, пока явно не подтвердите.",
                changed: true);
            e.CanTrust = accept;
            if (accept) KnownHostsStore.Remember(host.Host, port, fp);
        };
    }

    private static bool AskKey(string message, bool changed)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return false;
        if (app.Dispatcher.CheckAccess())
            return Show();
        return app.Dispatcher.Invoke(Show);

        bool Show()
        {
            var dlg = new HostKeyWindow(message, changed) { Owner = app.MainWindow };
            return dlg.ShowDialog() == true;
        }
    }
}
