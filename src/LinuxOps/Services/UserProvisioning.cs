using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LinuxOps.Models;

namespace LinuxOps.Services;

public sealed class AddUserRequest
{
    public required string Login { get; init; }
    public required bool Sudo { get; init; }
    public required string Keys { get; init; }
    public required string Password { get; init; }
    public required string Fingerprint { get; init; }
}

public static class UserProvisioning
{
    private static readonly Regex LoginRx = new(@"^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyRx = new(
        @"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+|sk-ssh-ed25519@openssh\.com|sk-ecdsa-sha2-\S+)\s+([A-Za-z0-9+/=]+)(\s+.*)?$",
        RegexOptions.CultureInvariant);

    public static string? Validate(string login, string keys, string password, out string fingerprint)
    {
        fingerprint = "";
        login = login.Trim();
        if (!LoginRx.IsMatch(login))
            return "Логин: латиница, с цифры нельзя, до 32 символов (a-z 0-9 _ -).";

        var raw = keys ?? "";
        if (raw.Contains("-----BEGIN", StringComparison.Ordinal))
            return "Это закрытый ключ. Нужен открытый (одна строка ssh-ed25519 …).";

        var lines = raw.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prints = new List<string>();
        foreach (var line in lines)
        {
            var m = KeyRx.Match(line);
            if (!m.Success)
                return "Ключ не похож на ssh-ed25519 / ssh-rsa / ecdsa-sha2 / sk-…";
            try
            {
                var blob = Convert.FromBase64String(m.Groups[2].Value);
                var hash = SHA256.HashData(blob);
                prints.Add("SHA256:" + Convert.ToBase64String(hash).TrimEnd('='));
            }
            catch (FormatException)
            {
                return "В ключе битая base64.";
            }
        }

        if (lines.Length == 0 && string.IsNullOrEmpty(password))
            return "Нужен открытый ключ или пароль.";

        fingerprint = string.Join("\n", prints);
        return null;
    }

    public static string HumanLine(string host, string stdout)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = line.IndexOf('=');
            if (i > 0) map[line[..i]] = line[(i + 1)..];
        }

        var parts = new List<string>();
        parts.Add(map.TryGetValue("created", out var created) && created == "yes" ? "создан" : "уже был");
        if (map.TryGetValue("sudo", out var sudo))
        {
            if (sudo == "added") parts.Add("sudo добавлен");
            else if (sudo == "already") parts.Add("sudo уже есть");
        }

        if (map.TryGetValue("key", out var key))
        {
            if (key == "added") parts.Add("ключ добавлен");
            else if (key == "already") parts.Add("ключ уже есть");
        }

        if (map.TryGetValue("password", out var pass) && pass == "set") parts.Add("пароль задан");
        return $"{host} — {string.Join(", ", parts)}";
    }

    public static async Task ApplyAsync(
        IReadOnlyList<HostConfig> hosts,
        AddUserRequest request,
        IProgress<PlanProgress> progress,
        CancellationToken cancellationToken)
    {
        var gate = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        var n = 0;
        foreach (var host in hosts)
        {
            await gate.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyOne(host, request, progress, cancellationToken);
                }
                finally
                {
                    var done = Interlocked.Increment(ref n);
                    progress.Report(new PlanProgress { Status = $"хост {done} из {hosts.Count}" });
                    gate.Release();
                }
            }));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is OperationCanceledException
                                   || (ex is AggregateException ae && ae.InnerExceptions.All(e => e is OperationCanceledException)))
        {
            throw new OperationCanceledException();
        }
    }

    private static void ApplyOne(HostConfig host, AddUserRequest request, IProgress<PlanProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            using var ssh = SshRunner.Connect(host);
            var distro = ssh.DetectDistro();
            var suse = distro.Family == "suse";
            var action = new ActionDefinition { Id = "add-user", Privilege = "sudo/root", TimeoutSeconds = 120 };
            var env = new Dictionary<string, string>
            {
                ["LINUXOPS_USER"] = request.Login,
                ["LINUXOPS_GROUP"] = suse ? "" : distro.Family == "debian" ? "sudo" : "wheel",
                ["LINUXOPS_SUDO_MODE"] = suse ? "file" : "group",
                ["LINUXOPS_SUDO"] = request.Sudo ? "yes" : "no",
                ["LINUXOPS_KEYS"] = request.Keys.Replace("\r\n", "\n").Trim(),
                ["LINUXOPS_PASSWORD"] = request.Password
            };
            var result = ssh.Run("add-user", Script, 120, SshRunner.NeedsSudo(host, action), host, env, cancellationToken);
            var line = result.ExitCode == 0
                ? HumanLine(host.Title, result.StdOut)
                : First(result.StdErr) ?? First(result.StdOut) ?? $"exit {result.ExitCode}";
            var detail = (result.StdOut + "\n" + result.StdErr).Trim();
            progress.Report(new PlanProgress
            {
                Row = PlanRunner.MakeRow(result.ExitCode == 0 ? "OK" : "FAIL", host.Title, line, detail, result.ExitCode == 0)
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new PlanProgress
            {
                Row = PlanRunner.MakeRow("FAIL", host.Title, ex.Message, ex.ToString(), ok: false)
            });
        }
    }

    private static string? First(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }

        return null;
    }

    private const string Script = """
        set -euo pipefail
        login="$LINUXOPS_USER"
        group="${LINUXOPS_GROUP:-}"
        mode="${LINUXOPS_SUDO_MODE:-group}"
        want_sudo="${LINUXOPS_SUDO:-no}"
        die() { echo "$1" >&2; exit 1; }
        created=no
        if ! id "$login" >/dev/null 2>&1; then
          useradd -m -s /bin/bash "$login" || die "useradd: не удалось создать $login"
          id "$login" >/dev/null 2>&1 || die "пользователь $login не появился после useradd"
          created=yes
        fi
        pg=$(id -gn "$login") || die "не прочитана основная группа $login"
        sudo_state=skip
        if [ "$want_sudo" = "yes" ]; then
          if [ "$mode" = "file" ]; then
            rule="$login ALL=(ALL) ALL"
            file="/etc/sudoers.d/linuxops-$login"
            if [ -f "$file" ] && grep -qxF -- "$rule" "$file"; then
              sudo_state=already
            else
              printf '%s\n' "$rule" > "$file" || die "не записан $file"
              chmod 440 "$file" || die "chmod $file"
              if command -v visudo >/dev/null 2>&1; then
                if ! visudo -c >/dev/null 2>&1; then
                  rm -f "$file"
                  die "sudoers отклонён проверкой visudo"
                fi
              fi
              sudo_state=added
            fi
          else
            if ! getent group "$group" >/dev/null 2>&1; then
              die "нет группы $group"
            fi
            if id -nG "$login" | tr ' ' '\n' | grep -qx "$group"; then
              sudo_state=already
            else
              usermod -aG "$group" "$login" || die "usermod: не добавлен в $group"
              id -nG "$login" | tr ' ' '\n' | grep -qx "$group" || die "группа $group не применилась"
              sudo_state=added
            fi
          fi
        fi
        key_state=skip
        if [ -n "${LINUXOPS_KEYS:-}" ]; then
          home=$(getent passwd "$login" | awk -F: '{print $6}')
          [ -n "$home" ] || die "нет домашнего каталога у $login"
          install -d -m 700 -o "$login" -g "$pg" "$home/.ssh" || die "не создан $home/.ssh"
          ak="$home/.ssh/authorized_keys"
          touch "$ak" || die "не создан authorized_keys"
          chown "$login:$pg" "$ak" || die "chown authorized_keys"
          chmod 600 "$ak" || die "chmod authorized_keys"
          key_state=already
          while IFS= read -r line || [ -n "$line" ]; do
            [ -z "$line" ] && continue
            if grep -qxF -- "$line" "$ak"; then
              continue
            fi
            printf '%s\n' "$line" >> "$ak" || die "не записан ключ"
            key_state=added
          done < <(printf '%s\n' "$LINUXOPS_KEYS")
          if command -v restorecon >/dev/null 2>&1; then
            restorecon -R "$home/.ssh" || die "restorecon .ssh"
          fi
        fi
        pass_state=skip
        if [ -n "${LINUXOPS_PASSWORD:-}" ]; then
          printf '%s:%s\n' "$login" "$LINUXOPS_PASSWORD" | chpasswd || die "не задан пароль"
          pass_state=set
        fi
        echo "created=$created"
        echo "sudo=$sudo_state"
        echo "key=$key_state"
        echo "password=$pass_state"
        """;
}
