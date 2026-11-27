using System.IO;
using System.Text;

namespace LinuxOps.Services;

/// <summary>
/// Чтение текстовых файлов с Windows (скрипты .ps1, ключи): BOM, UTF-16 без BOM (так пишут некоторые редакторы и выгрузки),
/// UTF-8 без BOM, иначе — ANSI-кодировка системы (1251). File.ReadAllText без BOM считает всё UTF-8:
/// UTF-16 без BOM превращается в текст с «\0» между символами, ANSI-кириллица — в «�».
/// </summary>
public static class ScriptFile
{
    static ScriptFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Текст и кодировка; <c>PwshReadable</c> — PowerShell 7 прочтёт файл сам (BOM или чистый UTF-8/ASCII).</summary>
    public sealed record Result(string Text, string EncodingName, bool PwshReadable);

    public static Result Read(string path) => Decode(File.ReadAllBytes(path));

    public static Result Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return new(Strip(new UTF8Encoding(false).GetString(b, 3, b.Length - 3)), "UTF-8 с BOM", true);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return new(Strip(Encoding.Unicode.GetString(b, 2, b.Length - 2)), "UTF-16 LE с BOM", true);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return new(Strip(Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)), "UTF-16 BE с BOM", true);

        switch (Utf16Guess(b))
        {
            case 1: return new(Strip(Encoding.Unicode.GetString(b, 0, b.Length & ~1)), "UTF-16 LE без BOM", false);
            case 2: return new(Strip(Encoding.BigEndianUnicode.GetString(b, 0, b.Length & ~1)), "UTF-16 BE без BOM", false);
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b);
            var ascii = b.All(x => x < 0x80);
            // pwsh 7 читает файл без BOM как UTF-8 — ASCII и UTF-8 он прочтёт верно
            return new(Strip(text), ascii ? "ASCII" : "UTF-8 без BOM", true);
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return new(Strip(ansi.GetString(b)), $"ANSI ({ansi.WebName})", false);
        }
    }

    /// <summary>0 — не UTF-16; 1 — LE (нули в нечётных байтах: латиница, цифры, знаки); 2 — BE.</summary>
    private static int Utf16Guess(byte[] b)
    {
        var n = Math.Min(b.Length & ~1, 8192);
        if (n < 4) return 0;
        int evenZero = 0, oddZero = 0;
        for (var i = 0; i < n; i += 2)
        {
            if (b[i] == 0) evenZero++;
            if (b[i + 1] == 0) oddZero++;
        }

        var pairs = n / 2;
        // Кириллица в UTF-16 — 0x04 в старшем байте, не ноль, поэтому порог умеренный; в обычном тексте нулевых байтов нет вовсе.
        if (oddZero * 4 >= pairs && evenZero * 20 < pairs) return 1;
        if (evenZero * 4 >= pairs && oddZero * 20 < pairs) return 2;
        return 0;
    }

    /// <summary>Нулевые символы в скрипте не нужны никогда, а в консоли ломают ввод.</summary>
    private static string Strip(string s) => s.IndexOf('\0') < 0 ? s : s.Replace("\0", "");
}
