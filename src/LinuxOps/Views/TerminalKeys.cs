using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace LinuxOps.Views;

/// <summary>Клавиши → байты для терминала: VT (ssh, xterm) и win32-input-mode (ConPTY — консоль Windows, PSReadLine).</summary>
public static class TerminalKeys
{
    /// <summary>Клавиша в последовательность xterm; null — не наша (обычный символ придёт через TextInput).</summary>
    public static string? Vt(Key key, ModifierKeys mods, bool appCursor)
    {
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        string? data = key switch
        {
            Key.Enter => "\r",
            Key.Space when ctrl => "\0",
            Key.Space => " ",
            Key.Back => alt ? "\u001b\u007f" : "\u007f",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Escape => "\u001b",
            Key.Delete => "\u001b[3~",
            Key.Insert => "\u001b[2~",
            Key.Up => appCursor ? "\u001bOA" : ctrl ? "\u001b[1;5A" : "\u001b[A",
            Key.Down => appCursor ? "\u001bOB" : ctrl ? "\u001b[1;5B" : "\u001b[B",
            Key.Right => appCursor ? "\u001bOC" : ctrl ? "\u001b[1;5C" : "\u001b[C",
            Key.Left => appCursor ? "\u001bOD" : ctrl ? "\u001b[1;5D" : "\u001b[D",
            Key.Home => appCursor ? "\u001bOH" : "\u001b[H",
            Key.End => appCursor ? "\u001bOF" : "\u001b[F",
            Key.PageUp => "\u001b[5~",
            Key.PageDown => "\u001b[6~",
            Key.F1 => "\u001bOP",
            Key.F2 => "\u001bOQ",
            Key.F3 => "\u001bOR",
            Key.F4 => "\u001bOS",
            Key.F5 => "\u001b[15~",
            Key.F6 => "\u001b[17~",
            Key.F7 => "\u001b[18~",
            Key.F8 => "\u001b[19~",
            Key.F9 => "\u001b[20~",
            Key.F10 => "\u001b[21~",
            Key.F11 => "\u001b[23~",
            Key.F12 => "\u001b[24~",
            _ => null
        };

        if (data is null && ctrl && !alt && key is >= Key.A and <= Key.Z)
            data = ((char)(key - Key.A + 1)).ToString();
        if (data is null && ctrl && !alt)
        {
            data = key switch
            {
                Key.OemOpenBrackets => "\u001b",
                Key.Oem5 => "\u001c",
                Key.OemCloseBrackets => "\u001d",
                Key.D6 => "\u001e",
                Key.OemMinus => "\u001f",
                _ => null
            };
        }

        if (data is null && alt && !ctrl && (key is >= Key.A and <= Key.Z || key is >= Key.D0 and <= Key.D9))
        {
            var ch = key is >= Key.A and <= Key.Z
                ? (char)((shift ? 'A' : 'a') + (key - Key.A))
                : (char)('0' + (key - Key.D0));
            data = "\u001b" + ch;
        }

        return data;
    }

    // ---------- win32-input-mode: ESC [ Vk ; Sc ; Uc ; Kd ; Cs ; Rc _ ----------

    private const int RightAlt = 0x1, LeftAlt = 0x2, LeftCtrl = 0x8, ShiftPressed = 0x10, Enhanced = 0x100;

    /// <summary>
    /// Клавиша целиком (нажатие и отпускание). null — пусть придёт символом через TextInput (буквы, цифры, знаки без Ctrl/Alt —
    /// раскладку знает только Windows). Модификаторы сами по себе не шлём.
    /// </summary>
    public static string? Win32(Key key, ModifierKeys mods)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
            return null;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        var alt = mods.HasFlag(ModifierKeys.Alt);
        var shift = mods.HasFlag(ModifierKeys.Shift);
        var vk = KeyInterop.VirtualKeyFromKey(key);
        var special = key is Key.Enter or Key.Tab or Key.Back or Key.Escape or Key.Space or Key.Delete or Key.Insert
            or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown
            or (>= Key.F1 and <= Key.F24) or Key.Apps;
        if (!special && !ctrl && !alt) return null;
        // AltGr (Ctrl+Alt) печатает символы в некоторых раскладках — их тоже отдаём TextInput.
        if (!special && ctrl && alt) return null;

        char uc = key switch
        {
            Key.Enter => '\r',
            Key.Tab => '\t',
            Key.Back => '\b',
            Key.Escape => '\u001b',
            Key.Space => ctrl ? '\0' : ' ',
            _ => '\0'
        };
        if (uc == '\0' && ctrl && !alt && key is >= Key.A and <= Key.Z) uc = (char)(key - Key.A + 1);
        if (uc == '\0' && alt && !ctrl && key is >= Key.A and <= Key.Z) uc = (char)((shift ? 'A' : 'a') + (key - Key.A));
        if (uc == '\0' && alt && !ctrl && key is >= Key.D0 and <= Key.D9) uc = (char)('0' + (key - Key.D0));

        var cs = (ctrl ? LeftCtrl : 0) | (alt ? LeftAlt : 0) | (shift ? ShiftPressed : 0);
        if (key is Key.Delete or Key.Insert or Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End
            or Key.PageUp or Key.PageDown) cs |= Enhanced;
        return Pair(vk, uc, cs);
    }

    /// <summary>Напечатанный символ (любой раскладки, IME) — как нажатие без кода клавиши.</summary>
    public static string Win32Text(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            var vk = 0;
            var cs = 0;
            // Перевод строки при вставке — Enter, иначе PSReadLine не увидит конец строки.
            if (c is '\r' or '\n') { vk = 0x0D; }
            else if (c == '\t') { vk = 0x09; }
            var r = VkKeyScanW(c);
            if (vk == 0 && r != -1 && (r & 0xFF) != 0xFF)
            {
                vk = r & 0xFF;
                if ((r & 0x100) != 0) cs |= ShiftPressed;
            }

            sb.Append(Pair(vk, c == '\n' ? '\r' : c, cs));
        }

        return sb.ToString();
    }

    private static string Pair(int vk, char uc, int cs)
    {
        var sc = vk == 0 ? 0 : (int)MapVirtualKeyW((uint)vk, 0);
        return $"\u001b[{vk};{sc};{(int)uc};1;{cs};1_\u001b[{vk};{sc};{(int)uc};0;{cs};1_";
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint code, uint mapType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);
}
