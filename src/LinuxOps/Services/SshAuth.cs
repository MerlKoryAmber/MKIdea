using System.IO;
using LinuxOps.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

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

        return new PrivateKeyAuthenticationMethod(host.Username, KeyPassphrases.Load(host, keyPath));
    }

    /// <summary>Пароли зашифрованных ключей: спрашиваются при подключении, живут в памяти до закрытия приложения.</summary>
    private static class KeyPassphrases
    {
        private const int Attempts = 3;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AskGate = new();

        public static PrivateKeyFile Load(HostConfig host, string keyPath)
        {
            var path = Path.GetFullPath(keyPath);
            try
            {
                return new PrivateKeyFile(path);
            }
            catch (SshPassPhraseNullOrEmptyException)
            {
                // ключ зашифрован — нужен пароль
            }

            lock (AskGate)
            {
                string? error = null;
                for (var i = 0; i <= Attempts; i++)
                {
                    if (!Cache.TryGetValue(path, out var pass))
                    {
                        if (i == Attempts) break;
                        pass = Ask(host, path, error);
                        if (pass is null)
                            throw new InvalidOperationException($"Хост {host.Display}: ключ {Path.GetFileName(path)} зашифрован, пароль ключа не введён.");
                    }

                    try
                    {
                        var key = new PrivateKeyFile(path, pass);
                        Cache[path] = pass;
                        return key;
                    }
                    catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
                    {
                        Cache.TryRemove(path, out _);
                        error = "Неверный пароль ключа.";
                    }
                }

                throw new InvalidOperationException($"Хост {host.Display}: неверный пароль ключа {Path.GetFileName(path)}.");
            }
        }

        private static string? Ask(HostConfig host, string path, string? error)
        {
            var app = System.Windows.Application.Current;
            if (app is null) return null;
            string? entered = null;
            void Show()
            {
                var hint = (error is null ? "" : error + "\n")
                    + $"Ключ {Path.GetFileName(path)} для {host.Username}@{host.Host} зашифрован. "
                    + "Пароль хранится только в памяти до закрытия LinuxOps.";
                var dlg = new SudoPasswordWindow("Пароль ключа SSH", hint)
                {
                    Owner = app.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow
                };
                if (dlg.ShowDialog() == true)
                    entered = dlg.Password;
            }

            if (app.Dispatcher.CheckAccess()) Show();
            else app.Dispatcher.Invoke(Show);
            return string.IsNullOrEmpty(entered) ? null : entered;
        }
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
