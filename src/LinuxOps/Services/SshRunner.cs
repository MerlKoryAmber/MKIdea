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
        SshAuth.AttachHostKey(client, host);
        client.Connect();
        return new SshRunner(client);
    }

    /// <summary>
    /// Скрипт уходит в stdin <c>bash -s</c>. Пароль sudo — первой строкой stdin, не в argv.
    /// </summary>
    public ActionResult Run(string actionId, string command, int timeoutSeconds = 180,
        bool sudo = false, HostConfig? host = null, IReadOnlyDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        var script = env is { Count: > 0 } ? EncodeEnv(env) + NormalizeLf(command) : NormalizeLf(command);
        if (!script.EndsWith('\n'))
            script += "\n";

        string? sudoPassword = null;
        if (sudo)
        {
            var probe = Exec(actionId, "export LC_ALL=C LANG=C; sudo -n true", null, Math.Min(30, timeoutSeconds), cancellationToken);
            var probeHint = FriendlySudo(probe.StdErr, probe.StdOut);
            if (probe.ExitCode == 0)
                sudoPassword = null;
            else if (probeHint is not null && probeHint.Contains("sudoers", StringComparison.Ordinal))
                return WithHint(probe, probeHint);
            else if (probeHint is not null && probeHint.Contains("пароль", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null && string.Equals(host.Auth, "Password", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(host.Password))
                    sudoPassword = host.Password;
                else
                    sudoPassword = host is null ? null : SudoMemory.Get(host) ?? SudoMemory.Ask(host);
                if (string.IsNullOrEmpty(sudoPassword))
                {
                    return new ActionResult
                    {
                        ActionId = actionId,
                        ExitCode = 1,
                        StdErr = "Нужен пароль sudo. Окно закрыли без ввода."
                    };
                }
            }
            else
                return probeHint is null ? probe : WithHint(probe, probeHint);
        }

        var remote = sudo
            ? (string.IsNullOrEmpty(sudoPassword)
                ? "export LC_ALL=C LANG=C; sudo -n bash -s"
                : "export LC_ALL=C LANG=C; sudo -S -p '' bash -s")
            : "export LC_ALL=C LANG=C; bash -s";
        var stdin = string.IsNullOrEmpty(sudoPassword) ? script : sudoPassword + "\n" + script;
        var result = Exec(actionId, remote, stdin, timeoutSeconds, cancellationToken);
        var hint = FriendlySudo(result.StdErr, result.StdOut);
        if (hint is not null && host is not null && hint.Contains("пароль", StringComparison.OrdinalIgnoreCase))
            SudoMemory.Forget(host);
        return hint is null ? result : WithHint(result, hint);
    }

    /// <summary>Значения только в stdin (base64), не в argv.</summary>
    public static string EncodeEnv(IReadOnlyDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in env)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Z][A-Z0-9_]{0,40}$"))
                throw new ArgumentException($"Имя переменной недопустимо: {key}");
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
            sb.Append(key).Append("=$(printf '%s' '").Append(b64).Append("' | base64 -d)\nexport ").Append(key).Append('\n');
        }

        return sb.ToString();
    }

    public static bool NeedsSudo(HostConfig host, ActionDefinition action)
    {
        var priv = action.Privilege ?? string.Empty;
        var wants = priv.Contains("sudo", StringComparison.OrdinalIgnoreCase)
                    || priv.Contains("root", StringComparison.OrdinalIgnoreCase);
        if (!wants || !host.UseSudo) return false;
        return !string.Equals(host.Username, "root", StringComparison.OrdinalIgnoreCase);
    }

    private ActionResult Exec(string actionId, string remoteCommand, string? stdin, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var cmd = _client.CreateCommand(remoteCommand);
        var timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds));
        cmd.CommandTimeout = timeout;
        var task = cmd.ExecuteAsync(cancellationToken);
        var bytes = Encoding.UTF8.GetBytes(stdin ?? string.Empty);
        using (var input = cmd.CreateInputStream())
        {
            if (bytes.Length > 0)
                input.Write(bytes, 0, bytes.Length);
        }

        try
        {
            if (!task.Wait(timeout, cancellationToken))
            {
                TryCancel(cmd);
                return new ActionResult
                {
                    ActionId = actionId,
                    ExitCode = -1,
                    StdErr = $"Таймаут {timeoutSeconds} с."
                };
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            TryCancel(cmd);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryCancel(cmd);
            throw;
        }

        var code = cmd.ExitStatus ?? -1;
        if (string.Equals(actionId, "check-updates", StringComparison.OrdinalIgnoreCase) && code == 100)
            code = 0;

        return new ActionResult
        {
            ActionId = actionId,
            ExitCode = code,
            StdOut = (cmd.Result ?? string.Empty).TrimEnd(),
            StdErr = (cmd.Error ?? string.Empty).TrimEnd()
        };
    }

    private static void TryCancel(Renci.SshNet.SshCommand cmd)
    {
        try { cmd.CancelAsync(true, 2000); }
        catch { /* команда уже завершилась */ }
    }

    private static ActionResult WithHint(ActionResult result, string hint) => new()
    {
        ActionId = result.ActionId,
        ExitCode = result.ExitCode,
        StdOut = result.StdOut,
        StdErr = hint + "\n" + result.StdErr
    };

    private static string? FriendlySudo(string stderr, string stdout)
    {
        var blob = stderr + "\n" + stdout;
        if (blob.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("not in sudoers", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("not allowed to run sudo", StringComparison.OrdinalIgnoreCase))
            return "Пользователь не в sudoers — sudo отклонён.";
        if (blob.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("incorrect password", StringComparison.OrdinalIgnoreCase))
            return "Неверный пароль sudo.";
        if (blob.Contains("a password is required", StringComparison.OrdinalIgnoreCase)
            || blob.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
            return "sudo просит пароль.";
        return null;
    }

    public static string? AskSudo(HostConfig host) => SudoMemory.Ask(host);

    /// <summary>Пароль sudo, который уже вводили в этой сессии (или null).</summary>
    public static string? CachedSudo(HostConfig host) => SudoMemory.Get(host);

    public static void ForgetSudo(HostConfig host) => SudoMemory.Forget(host);

    public static bool IsRoot(HostConfig host) => string.Equals(host.Username, "root", StringComparison.OrdinalIgnoreCase);

    /// <summary>Команда с правами root: под root — напрямую, иначе через sudo.</summary>
    public ActionResult RunAsRoot(string actionId, string command, int timeoutSeconds, HostConfig host,
        IReadOnlyDictionary<string, string>? env = null)
        => Run(actionId, command, timeoutSeconds, sudo: !IsRoot(host), host, env);

    private static class SudoMemory
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AskGate = new();

        public static string? Get(HostConfig host)
            => Cache.TryGetValue(Key(host), out var value) ? value : null;

        public static void Forget(HostConfig host) => Cache.TryRemove(Key(host), out _);

        public static string? Ask(HostConfig host)
        {
            var cached = Get(host);
            if (!string.IsNullOrEmpty(cached)) return cached;

            lock (AskGate)
            {
                cached = Get(host);
                if (!string.IsNullOrEmpty(cached)) return cached;

                string? entered = null;
                var app = System.Windows.Application.Current;
                if (app is null) return null;
                void Show()
                {
                    var dlg = new SudoPasswordWindow(host) { Owner = app.MainWindow };
                    if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.Password))
                        entered = dlg.Password;
                }

                if (app.Dispatcher.CheckAccess()) Show();
                else app.Dispatcher.Invoke(Show);
                if (!string.IsNullOrEmpty(entered))
                    Cache[Key(host)] = entered;
                return entered;
            }
        }

        private static string Key(HostConfig host) => $"{host.Username}@{host.Host}:{host.Port}";
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
