using System.IO;
using LinuxOps.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxOps.Services;

public static class SshAuth
{
    /// <summary>Пакет keepalive раз в 30 с — короче типичных таймаутов простоя файрволов (60–300 с).</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

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

        // Ключ текстом (LinuxOps.Web: хранится зашифрованным в базе сервиса, на диск не пишется)
        if (!string.IsNullOrWhiteSpace(host.PrivateKeyText))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(host.PrivateKeyText.Replace("\r\n", "\n"));
            return new PrivateKeyAuthenticationMethod(host.Username,
                KeyPassphrases.Load(host, $"ключ хоста {host.Title}", "mem:" + host.Username + "@" + host.Host,
                    pass => pass is null ? new PrivateKeyFile(new MemoryStream(bytes)) : new PrivateKeyFile(new MemoryStream(bytes), pass)));
        }

        var keyPath = Environment.ExpandEnvironmentVariables(host.PrivateKeyPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException($"Хост {host.Display}: ключ SSH не задан — укажите ключ или пароль в настройках хоста.");
        if (!File.Exists(keyPath))
            throw new InvalidOperationException($"Хост {host.Display}: ключ не найден: {keyPath}");

        var path = Path.GetFullPath(keyPath);
        return new PrivateKeyAuthenticationMethod(host.Username,
            KeyPassphrases.Load(host, path, path, pass => pass is null ? new PrivateKeyFile(path) : new PrivateKeyFile(path, pass)));
    }

    /// <summary>Пароли зашифрованных ключей: спрашиваются при подключении, живут в памяти до закрытия приложения.</summary>
    private static class KeyPassphrases
    {
        private const int Attempts = 3;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AskGate = new();

        /// <param name="path">Что показать в вопросе (путь к файлу или «ключ хоста …»).</param>
        /// <param name="cacheKey">Ключ кэша пароля.</param>
        /// <param name="open">Открыть ключ без пароля (null) или с паролем.</param>
        public static PrivateKeyFile Load(HostConfig host, string path, string cacheKey, Func<string?, PrivateKeyFile> open)
        {
            try
            {
                return open(null);
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
                    if (!Cache.TryGetValue(cacheKey, out var pass))
                    {
                        if (i == Attempts) break;
                        pass = Ask(host, path, error);
                        if (pass is null)
                            throw new InvalidOperationException($"Хост {host.Display}: ключ {Path.GetFileName(path)} зашифрован, пароль ключа не введён.");
                    }

                    try
                    {
                        var key = open(pass);
                        Cache[cacheKey] = pass;
                        return key;
                    }
                    catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
                    {
                        Cache.TryRemove(cacheKey, out _);
                        error = "Неверный пароль ключа.";
                    }
                }

                throw new InvalidOperationException($"Хост {host.Display}: неверный пароль ключа {Path.GetFileName(path)}.");
            }
        }

        private static string? Ask(HostConfig host, string path, string? error)
        {
            var entered = UserPrompts.Current?.KeyPassphrase(host, path, error);
            return string.IsNullOrEmpty(entered) ? null : entered;
        }
    }

    /// <summary>
    /// Проверка ключа хоста и keepalive. Без keepalive простаивающую сессию (терминал, SSH Files) рвут
    /// файрволы и NAT по таймауту — со стороны программы это выглядит как «сессия отвалилась».
    /// </summary>
    public static void AttachHostKey(BaseClient client, HostConfig host)
    {
        client.KeepAliveInterval = KeepAlive;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            var port = host.Port <= 0 ? 22 : host.Port;
            var known = KnownHostsStore.Find(host.Host, port);
            if (known is null)
            {
                var trust = AskKey(host, $"{host.Display}\nОтпечаток {fp}\nДоверять?", changed: false);
                e.CanTrust = trust;
                if (trust) KnownHostsStore.Remember(host.Host, port, fp);
                return;
            }

            if (string.Equals(known, fp, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }

            var accept = AskKey(host,
                $"{host.Display}\nКлюч хоста изменился.\nБыло: {known}\nСтало: {fp}\nПодключение отклонено, пока явно не подтвердите.",
                changed: true);
            e.CanTrust = accept;
            if (accept) KnownHostsStore.Remember(host.Host, port, fp);
        };
    }

    private static bool AskKey(HostConfig host, string message, bool changed)
        => UserPrompts.Current?.TrustHostKey(host, message, changed) ?? false;
}
