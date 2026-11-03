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
            return new PasswordAuthenticationMethod(host.Username, host.Password);
        }

        var keyPath = Environment.ExpandEnvironmentVariables(host.PrivateKeyPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException($"Хост {host.Display}: ключ не найден: {keyPath}");

        return new PrivateKeyAuthenticationMethod(host.Username, new PrivateKeyFile(keyPath));
    }
}
