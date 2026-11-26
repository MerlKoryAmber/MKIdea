using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace LinuxOps.Services;

/// <summary>Пара ed25519 в формате OpenSSH. Приватный живёт в памяти вызывающего; файл ssh-keygen удаляется сразу.</summary>
public sealed record SshKeyPair(string PrivateOpenSsh, string PublicLine, string Blob, string Fingerprint);

public static class SshKeyGen
{
    /// <summary>
    /// Новая пара. <paramref name="workDir"/> — каталог службы (не целевой хост): внутри короткий временный файл, затем удаление.
    /// Комментарий — одна строка без кавычек. Открытый ключ сверяется с тем, что записал ssh-keygen.
    /// </summary>
    public static SshKeyPair Create(string workDir, string comment)
    {
        if (string.IsNullOrWhiteSpace(comment) || comment.Any(c => c is '\r' or '\n' or '\'' or '"'))
            throw new ArgumentException("Комментарий ключа — одна строка без кавычек.");
        var sshKeygen = FindSshKeygen();
        Directory.CreateDirectory(workDir);
        var dir = Path.Combine(workDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(dir, "id_ed25519");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = sshKeygen,
                Arguments = $"-q -t ed25519 -N \"\" -C \"{comment}\" -f \"{path}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Не запустился ssh-keygen.");
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
                throw new InvalidOperationException("ssh-keygen не завершился.");
            }
            if (p.ExitCode != 0 || !File.Exists(path) || !File.Exists(path + ".pub"))
                throw new InvalidOperationException("ssh-keygen не создал ключ." + (string.IsNullOrWhiteSpace(err) ? "" : " " + err.Trim()));

            var priv = File.ReadAllText(path).Replace("\r\n", "\n");
            var pub = File.ReadAllText(path + ".pub").Trim().Replace("\r\n", "\n");
            var blob = PublicBlob(priv);
            var fromFile = BlobOfLine(pub);
            if (!string.Equals(blob, fromFile, StringComparison.Ordinal))
                throw new InvalidOperationException("Открытый ключ не совпал с закрытым — пара не используется.");
            return new SshKeyPair(priv, pub, blob, Fingerprint(blob));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* файл уже прочитан; каталог службы закрыт 0700 */ }
        }
    }

    /// <summary>Base64-тело строки authorized_keys для закрытого ключа (без пароля или с уже известным паролем).</summary>
    public static string PublicBlob(string privateOpenSsh, string? passphrase = null)
    {
        var bytes = Encoding.UTF8.GetBytes(privateOpenSsh.Replace("\r\n", "\n"));
        using var stream = new MemoryStream(bytes);
        using var file = passphrase is null ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        return PublicBlob(file);
    }

    public static string PublicBlob(PrivateKeyFile file)
    {
        var alg = file.HostKeyAlgorithms.FirstOrDefault(a => a.Name == "ssh-ed25519") ?? file.HostKeyAlgorithms.First();
        var data = alg.Data;
        var wire = LooksLikeWire(data) ? data : Concat(SshString(alg.Name), SshString(data));
        return Convert.ToBase64String(wire);
    }

    public static string Fingerprint(string blob)
    {
        var hash = SHA256.HashData(Convert.FromBase64String(blob));
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>Строка открытого ключа: тип, тело, комментарий. Чужое в команду на хост не попадает.</summary>
    public static bool IsPublicLine(string line)
    {
        var p = line.Split(' ');
        return p.Length >= 3 && p[0] == "ssh-ed25519" && IsBlob(p[1]) && string.Join(' ', p.Skip(2)).All(c => c is >= ' ' and not '\'');
    }

    public static string BlobOfLine(string line)
    {
        var p = line.Split(' ');
        if (p.Length < 2 || p[0] != "ssh-ed25519" || !IsBlob(p[1]))
            throw new InvalidOperationException("Не открытый ключ ed25519.");
        return p[1];
    }

    public static bool IsBlob(string blob)
        => blob.Length is > 20 and < 2000 && blob.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '+' or '/' or '=');

    private static bool LooksLikeWire(byte[] data) => data.Length > 19 && data[0] == 0 && data[1] == 0 && data[2] == 0;

    private static byte[] SshString(string text) => SshString(Encoding.ASCII.GetBytes(text));

    private static byte[] SshString(byte[] data)
    {
        var r = new byte[4 + data.Length];
        r[0] = (byte)(data.Length >> 24);
        r[1] = (byte)(data.Length >> 16);
        r[2] = (byte)(data.Length >> 8);
        r[3] = (byte)data.Length;
        data.CopyTo(r, 4);
        return r;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }

    private static string FindSshKeygen()
    {
        var fromPath = OperatingSystem.IsWindows() ? "ssh-keygen.exe" : "ssh-keygen";
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), fromPath);
            if (File.Exists(candidate)) return candidate;
        }
        var wellKnown = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\OpenSSH\ssh-keygen.exe"
            : "/usr/bin/ssh-keygen";
        if (File.Exists(wellKnown)) return wellKnown;
        throw new InvalidOperationException("На сервере нет ssh-keygen.");
    }
}
