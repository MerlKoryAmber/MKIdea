using System.Globalization;
using System.Text;

namespace LinuxOps.Web.Services.Guac;

/// <summary>
/// Протокол Guacamole: инструкция — элементы «длина.значение» через запятую, в конце «;».
/// Длина — в символах Юникода (кодовых точках), не в байтах и не в UTF-16.
/// </summary>
public static class GuacProtocol
{
    public static string Encode(string opcode, params string[] args)
    {
        var sb = new StringBuilder();
        Append(sb, opcode);
        foreach (var a in args) { sb.Append(','); Append(sb, a ?? ""); }
        return sb.Append(';').ToString();
    }

    private static void Append(StringBuilder sb, string v) =>
        sb.Append(CodePoints(v).ToString(CultureInfo.InvariantCulture)).Append('.').Append(v);

    private static int CodePoints(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++) { if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; n++; }
        return n;
    }

    /// <summary>
    /// Найти в буфере конец первой целой инструкции (индекс после «;»), не разбирая значения.
    /// -1 — инструкция ещё не пришла целиком. Бросает FormatException на мусоре.
    /// </summary>
    public static int CompleteLength(StringBuilder buf, int start = 0)
    {
        var i = start;
        while (true)
        {
            var dot = -1;
            for (var j = i; j < buf.Length; j++)
            {
                var c = buf[j];
                if (c == '.') { dot = j; break; }
                if (c < '0' || c > '9') throw new FormatException("guac: длина элемента не число");
                if (j - i > 9) throw new FormatException("guac: слишком длинный элемент");
            }
            if (dot < 0) return -1;
            var len = int.Parse(buf.ToString(i, dot - i), CultureInfo.InvariantCulture);
            // пройти len кодовых точек
            var p = dot + 1;
            for (var k = 0; k < len; k++)
            {
                if (p >= buf.Length) return -1;
                p += char.IsHighSurrogate(buf[p]) ? 2 : 1;
            }
            if (p >= buf.Length) return -1;
            var term = buf[p];
            if (term == ';') return p + 1;
            if (term != ',') throw new FormatException("guac: нет разделителя после элемента");
            i = p + 1;
        }
    }

    /// <summary>Разобрать одну целую инструкцию на элементы (opcode первым).</summary>
    public static List<string> Parse(string instr)
    {
        var list = new List<string>();
        var i = 0;
        while (i < instr.Length)
        {
            var dot = instr.IndexOf('.', i);
            var len = int.Parse(instr.AsSpan(i, dot - i), CultureInfo.InvariantCulture);
            var p = dot + 1;
            var s = p;
            for (var k = 0; k < len; k++) p += char.IsHighSurrogate(instr[p]) ? 2 : 1;
            list.Add(instr[s..p]);
            if (instr[p] == ';') break;
            i = p + 1;
        }
        return list;
    }
}
