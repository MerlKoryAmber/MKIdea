using System.IO;
using System.Text;
using LinuxOps.Models;
using Renci.SshNet;

namespace LinuxOps.Services;

public sealed class SshRunner : IDisposable
{
    private readonly SshClient _client;
    private bool _disposed;

    private SshRunner(SshClient client) => _client = client;

    public static SshRunner Connect(HostConfig host, TimeSpan? timeout = null)
    {
        var client = new SshClient(SshAuth.Build(host, timeout));
        client.Connect();
        return new SshRunner(client);
    }

    /// <summary>
    /// Выполняет скрипт через bash -s (base64), чтобы CRLF из Windows и кавычки не ломали shell.
    /// </summary>
    public ActionResult Run(string actionId, string command, int timeoutSeconds = 180,
        bool sudo = false, string? sudoPassword = null)
    {
        var script = NormalizeLf(command);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        // LC_ALL=C — стабильный вывод; pipefail не везде, поэтому без него
        // sudo -S ест stdin → пароль отдельно, скрипт — внутри bash -c
        string wrapper;
        if (!sudo)
            wrapper = $"echo '{b64}' | base64 -d | bash -s";
        else if (!string.IsNullOrEmpty(sudoPassword))
        {
            var passB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sudoPassword));
            wrapper =
                $"echo '{passB64}' | base64 -d | sudo -S -p '' bash -c \"echo '{b64}' | base64 -d | bash -s\"";
        }
        else
            wrapper = $"echo '{b64}' | base64 -d | sudo -n bash -s";

        using var cmd = _client.CreateCommand(wrapper);
        cmd.CommandTimeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
        var stdout = cmd.Execute() ?? string.Empty;
        var stderr = cmd.Error ?? string.Empty;
        var code = cmd.ExitStatus ?? -1;

        // dnf check-update: exit 100 = есть обновления — для нас это OK с пометкой
        if (string.Equals(actionId, "check-updates", StringComparison.OrdinalIgnoreCase) && code == 100)
            code = 0;

        return new ActionResult
        {
            ActionId = actionId,
            ExitCode = code,
            StdOut = stdout.TrimEnd(),
            StdErr = stderr.TrimEnd()
        };
    }

    public DistroInfo DetectDistro()
    {
        var r = Run("detect-distro", "cat /etc/os-release 2>/dev/null || true");
        var map = ParseOsRelease(r.StdOut);
        map.TryGetValue("ID", out var id);
        map.TryGetValue("ID_LIKE", out var like);
        map.TryGetValue("PRETTY_NAME", out var pretty);
        id ??= "unknown";
        like ??= string.Empty;
        pretty ??= id;

        var family = "unknown";
        var blob = $"{id} {like}".ToLowerInvariant();
        if (blob.Contains("rhel") || blob.Contains("centos") || blob.Contains("fedora") ||
            blob.Contains("rocky") || blob.Contains("alma") || blob.Contains("oracle"))
            family = "rhel";
        else if (blob.Contains("debian") || blob.Contains("ubuntu"))
            family = "debian";
        else if (blob.Contains("suse") || blob.Contains("sles") || blob.Contains("opensuse"))
            family = "suse";

        return new DistroInfo
        {
            Id = id.Trim('"'),
            Like = like.Trim('"'),
            PrettyName = pretty.Trim('"'),
            Family = family
        };
    }

    public static string NormalizeLf(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static Dictionary<string, string> ParseOsRelease(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in NormalizeLf(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            d[line[..i]] = line[(i + 1)..].Trim().Trim('"');
        }

        return d;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client.IsConnected) _client.Disconnect();
        _client.Dispose();
    }
}
