using System.Security.Cryptography;
using System.Text;

namespace LinuxOps.Web.Services.Auth;

/// <summary>
/// TOTP по RFC 6238 (HMAC-SHA1, 6 цифр, шаг 30 с) — как у Google / Microsoft Authenticator и FreeOTP.
/// Свой, без MK 2FA: локальный админ — аварийный вход, когда AD или MK 2FA недоступны.
/// </summary>
public static class Totp
{
    private const int Digits = 6, StepSeconds = 30;
    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Новый секрет: 20 случайных байт в Base32 (32 символа).</summary>
    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    public static string Code(string secretB32, long step)
    {
        var key = FromBase32(secretB32);
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(step & 0xFF); step >>= 8; }
        var hash = HMACSHA1.HashData(key, msg);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6");
    }

    public static long StepNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>
    /// Проверить код с допуском ±1 шаг (часы телефона). Возвращает шаг совпадения или -1.
    /// Шаг ≤ lastUsed не принимается — один код нельзя использовать дважды.
    /// </summary>
    public static long Verify(string secretB32, string code, long lastUsedStep)
    {
        code = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (code.Length != Digits || string.IsNullOrEmpty(secretB32)) return -1;
        var now = StepNow();
        for (var s = now - 1; s <= now + 1; s++)
        {
            if (s <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secretB32, s)), Encoding.ASCII.GetBytes(code)))
                return s;
        }
        return -1;
    }

    /// <summary>Ссылка otpauth:// для приложения-аутентификатора.</summary>
    public static string Uri(string secretB32, string account, string issuer = "LinuxOps Web") =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={secretB32}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    /// <summary>QR-код ссылки otpauth:// — PNG в data:URI (QRCoder, без System.Drawing). Чёрное на белом с полем — читается в любой теме.</summary>
    public static string QrDataUri(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new QRCoder.PngByteQRCode(data).GetGraphic(5));
    }

    /// <summary>Секрет группами по 4 — так его проще ввести вручную.</summary>
    public static string Pretty(string secretB32) =>
        string.Join(' ', Enumerable.Range(0, (secretB32.Length + 3) / 4).Select(i => secretB32.Substring(i * 4, Math.Min(4, secretB32.Length - i * 4))));

    public static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(B32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(B32[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string s)
    {
        s = s.Replace(" ", "").TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(s.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in s)
        {
            var v = B32.IndexOf(ch);
            if (v < 0) throw new FormatException("Base32");
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
