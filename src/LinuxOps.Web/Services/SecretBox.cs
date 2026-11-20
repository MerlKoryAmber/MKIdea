using System.Security.Cryptography;
using System.Text;

namespace LinuxOps.Web.Services;

/// <summary>
/// Секреты (пароли хостов, закрытые ключи SSH) — AES-256-GCM. Мастер-ключ — отдельный файл вне каталога данных
/// (копия базы без него бесполезна). Нет файла — создаётся при первом запуске, права 600.
/// В браузер секреты не отдаются: расшифровка — только на сервере перед подключением.
/// </summary>
public sealed class SecretBox
{
    private readonly byte[] _key;

    public SecretBox()
    {
        var path = WebPaths.KeyFile;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(path);
        if (_key.Length != 32) throw new InvalidOperationException($"Мастер-ключ {path}: нужно 32 байта, а не {_key.Length}.");
    }

    /// <summary>«v1:» + base64(nonce 12 | tag 16 | шифротекст). Пусто → пусто.</summary>
    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var gcm = new AesGcm(_key, 16)) gcm.Encrypt(nonce, data, cipher, tag);
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith("v1:", StringComparison.Ordinal)) throw new InvalidOperationException("Секрет в неизвестном формате.");
        var raw = Convert.FromBase64String(stored[3..]);
        var plain = new byte[raw.Length - 28];
        using (var gcm = new AesGcm(_key, 16)) gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
