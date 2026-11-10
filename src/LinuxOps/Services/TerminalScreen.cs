using System.Text;

namespace LinuxOps.Services;

[Flags]
public enum CellFlags : byte
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Inverse = 16,
    Hidden = 32,
    Strike = 64
}

/// <summary>
/// Ячейка экрана. Цвет: -1 — по умолчанию, 0..255 — палитра xterm, <see cref="Rgb"/> — 24-битный.
/// </summary>
public readonly record struct Cell(char Ch, int Fg, int Bg, CellFlags Flags)
{
    public const int Default = -1;
    public const int RgbFlag = 0x1000000;
    public static readonly Cell Blank = new(' ', Default, Default, CellFlags.None);
    public static int Rgb(int r, int g, int b) => RgbFlag | ((r & 255) << 16) | ((g & 255) << 8) | (b & 255);
}

/// <summary>Неизменяемый снимок экрана для отрисовки.</summary>
public sealed class TerminalFrame
{
    private string? _text;

    public TerminalFrame(Cell[][] lines, int cursorLine, int cursorCol, bool cursorVisible, int cols, int rows, bool altScreen, long version)
    {
        Lines = lines;
        CursorLine = cursorLine;
        CursorCol = cursorCol;
        CursorVisible = cursorVisible;
        Cols = cols;
        Rows = rows;
        AltScreen = altScreen;
        Version = version;
    }

    public static readonly TerminalFrame Empty = new(Array.Empty<Cell[]>(), 0, 0, false, 120, 40, false, -1);

    public Cell[][] Lines { get; }
    public int CursorLine { get; }
    public int CursorCol { get; }
    public bool CursorVisible { get; }
    public int Cols { get; }
    public int Rows { get; }
    public bool AltScreen { get; }
    public long Version { get; }

    public bool HasText => Lines.Any(l => l.Any(c => c.Ch != ' '));

    public string Text => _text ??= string.Join("\n", Lines.Select(l => LineText(l)));

    public static string LineText(Cell[] line, int from = 0, int to = int.MaxValue)
    {
        to = Math.Min(to, line.Length);
        if (from >= to) return "";
        var sb = new StringBuilder(to - from);
        for (var i = from; i < to; i++) sb.Append(line[i].Ch);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Строка экрана; снимок кэшируется, пока строку не меняли.</summary>
internal sealed class TermLine
{
    private readonly List<Cell> _cells = new();
    private Cell[]? _frozen;

    public int Length => _cells.Count;

    public Cell this[int i]
    {
        get => _cells[i];
        set
        {
            _cells[i] = value;
            _frozen = null;
        }
    }

    public Cell[] Freeze() => _frozen ??= _cells.ToArray();

    public void Put(int col, Cell cell)
    {
        if (col < _cells.Count) _cells[col] = cell;
        else
        {
            while (_cells.Count < col) _cells.Add(Cell.Blank);
            _cells.Add(cell);
        }

        _frozen = null;
    }

    public void Truncate(int length)
    {
        if (length >= _cells.Count) return;
        _cells.RemoveRange(length, _cells.Count - length);
        _frozen = null;
    }

    public void Clear()
    {
        if (_cells.Count == 0) return;
        _cells.Clear();
        _frozen = null;
    }

    /// <summary>Заполнить [from, to) пустыми ячейками с фоном blank.</summary>
    public void Fill(int from, int to, Cell blank)
    {
        if (from >= to) return;
        while (_cells.Count < from) _cells.Add(Cell.Blank);
        for (var x = from; x < to; x++)
        {
            if (x < _cells.Count) _cells[x] = blank;
            else _cells.Add(blank);
        }

        _frozen = null;
    }

    public void Remove(int at, int n)
    {
        if (at >= _cells.Count) return;
        _cells.RemoveRange(at, Math.Min(n, _cells.Count - at));
        _frozen = null;
    }

    public void Insert(int at, int n, Cell blank, int maxCols)
    {
        if (at >= _cells.Count) return;
        _cells.InsertRange(at, Enumerable.Repeat(blank, n));
        if (_cells.Count > maxCols) _cells.RemoveRange(maxCols, _cells.Count - maxCols);
        _frozen = null;
    }

    public string Text()
    {
        var sb = new StringBuilder(_cells.Count);
        foreach (var c in _cells) sb.Append(c.Ch);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Экран xterm: перенос по ширине, прокрутка, альтернативный экран (vim, less, mc),
/// область прокрутки, вставка и удаление символов и строк, цвета SGR (16, 256, 24 бит) и атрибуты.
/// </summary>
public sealed class TerminalScreen
{
    private const int MaxScrollback = 1500;

    /// <summary>Псевдографика DEC для символов ` … ~ (рамки, когда программа включает ESC ( 0).</summary>
    private const string DecGraphics = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private List<TermLine> _lines = new() { new() };
    private int _row;
    private int _col;
    private int _cols = 120;
    private int _rows = 40;
    private int _marginTop;
    private int _marginBottom = -1; // -1 — до низа экрана
    private int _savedRow;
    private int _savedCol;
    private Cell _savedPen = Cell.Blank;
    private Cell _pen = Cell.Blank; // текущие цвет и атрибуты; Ch не используется
    private long _version;
    private readonly StringBuilder _carry = new(); // начало escape-последовательности с конца прошлого куска
    private char _last = ' ';  // для CSI b (повтор)
    private bool _g0Graphics;   // ESC ( 0 — псевдографика DEC (рамки ncurses)
    private bool _g1Graphics;
    private bool _shiftOut;     // SO выбрал G1

    private List<TermLine>? _mainLines;
    private int _mainRow;
    private int _mainCol;

    public int Columns => _cols;
    public int Rows => _rows;
    public bool AltScreen => _mainLines is not null;
    /// <summary>DECCKM: стрелки как ESC O A (vim, less, mc включают этот режим).</summary>
    public bool AppCursorKeys { get; private set; }
    /// <summary>Отчёты мыши, которые включила программа: 0 — нет, 1000 — нажатия, 1002 — и перетаскивание, 1003 — любое движение.</summary>
    public int MouseMode { get; private set; }
    /// <summary>Формат SGR (1006): координаты без ограничения 223.</summary>
    public bool MouseSgr { get; private set; }
    /// <summary>DECTCEM: программа может спрятать курсор.</summary>
    public bool CursorVisible { get; private set; } = true;
    public long Version => _version;

    public string Text => string.Join("\n", _lines.Select(l => l.Text()));

    /// <summary>Последняя строка с текстом (для распознавания запроса пароля).</summary>
    public string CurrentLine => _row < _lines.Count ? _lines[_row].Text() : "";

    private int Top => AltScreen ? 0 : Math.Max(0, _lines.Count - _rows);
    private int ScreenRow => _row - Top;
    private int BottomMargin => _marginBottom < 0 || _marginBottom >= _rows ? _rows - 1 : _marginBottom;

    /// <summary>Пустая ячейка с текущим фоном (как xterm с bce: mc и htop красят фон стиранием).</summary>
    private Cell BlankCell => _pen.Bg == Cell.Default && !_pen.Flags.HasFlag(CellFlags.Inverse)
        ? Cell.Blank
        : new Cell(' ', _pen.Fg, _pen.Bg, _pen.Flags & CellFlags.Inverse);

    private bool BlankIsDefault => _pen.Bg == Cell.Default && !_pen.Flags.HasFlag(CellFlags.Inverse);

    public TerminalFrame Snapshot()
    {
        var lines = new Cell[_lines.Count][];
        for (var i = 0; i < lines.Length; i++) lines[i] = _lines[i].Freeze();
        return new TerminalFrame(lines, _row, Math.Min(_col, _cols - 1), CursorVisible, _cols, _rows, AltScreen, _version);
    }

    public void Resize(int cols, int rows)
    {
        _cols = Math.Clamp(cols, 20, 500);
        _rows = Math.Clamp(rows, 5, 200);
        _marginTop = 0;
        _marginBottom = -1;
        if (AltScreen)
        {
            while (_lines.Count < _rows) _lines.Add(new TermLine());
            while (_lines.Count > _rows) _lines.RemoveAt(_lines.Count - 1);
            _row = Math.Min(_row, _rows - 1);
        }

        _col = Math.Min(_col, _cols - 1);
        _version++;
    }

    public void Clear()
    {
        _lines = new List<TermLine> { new() };
        _mainLines = null;
        _row = 0;
        _col = 0;
        _marginTop = 0;
        _marginBottom = -1;
        _pen = Cell.Blank;
        _g0Graphics = _g1Graphics = _shiftOut = false;
        AppCursorKeys = false;
        MouseMode = 0;
        MouseSgr = false;
        CursorVisible = true;
        _version++;
    }

    public void Feed(ReadOnlySpan<char> chunk)
    {
        _version++;
        if (_carry.Length > 0)
        {
            // Хвост прошлого куска («ESC [ 3» без конца) склеиваем с новым, иначе на экран вылезет «1;32m».
            var joined = _carry.Append(chunk).ToString();
            _carry.Clear();
            chunk = joined.AsSpan();
        }

        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            switch (c)
            {
                case '\u001b':
                    var end = ConsumeEscape(chunk, i);
                    if (end < 0)
                    {
                        if (chunk.Length - i < 4096) _carry.Append(chunk[i..]);
                        return;
                    }

                    i = end;
                    continue;
                case '\r':
                    _col = 0;
                    continue;
                case '\n':
                case '\v':
                case '\f':
                    LineFeed();
                    continue;
                case '\b':
                    if (_col >= _cols) _col = _cols - 1;
                    if (_col > 0) _col--;
                    continue;
                case '\t':
                    _col = Math.Min(_cols - 1, (_col / 8 + 1) * 8);
                    continue;
                case '\u000e':
                    _shiftOut = true;
                    continue;
                case '\u000f':
                    _shiftOut = false;
                    continue;
                case '\a':
                case '\0':
                case '\u007f':
                    continue;
            }

            if (c < ' ') continue;
            Put(c);
        }
    }

    private int ConsumeEscape(ReadOnlySpan<char> chunk, int i)
    {
        if (i + 1 >= chunk.Length) return -1;
        var next = chunk[i + 1];
        switch (next)
        {
            case '[': return ConsumeCsi(chunk, i + 2);
            case ']': return ConsumeOsc(chunk, i + 2);
            case '(' or ')' or '*' or '+' or '-' or '.' or '#' or '%':
                if (i + 2 >= chunk.Length) return -1;
                if (next == '(') _g0Graphics = chunk[i + 2] == '0';
                else if (next == ')') _g1Graphics = chunk[i + 2] == '0';
                return i + 2;
            case '7': SaveCursor(); return i + 1;
            case '8': RestoreCursor(); return i + 1;
            case 'M': ReverseIndex(); return i + 1;
            case 'D': LineFeed(); return i + 1;
            case 'E': _col = 0; LineFeed(); return i + 1;
            case 'c': Clear(); return i + 1;
            default: return i + 1; // ESC = , ESC > и прочее — без эффекта
        }
    }

    private static int ConsumeOsc(ReadOnlySpan<char> chunk, int i)
    {
        for (var p = i; p < chunk.Length; p++)
        {
            if (chunk[p] == '\a') return p;
            if (chunk[p] == '\u001b' && p + 1 < chunk.Length && chunk[p + 1] == '\\')
                return p + 1;
        }

        return -1; // не дошла до конца — допишется следующим куском
    }

    private int ConsumeCsi(ReadOnlySpan<char> chunk, int i)
    {
        var args = new List<int>();
        var num = -1;
        var priv = false;
        var intermediate = false;
        for (var p = i; p < chunk.Length; p++)
        {
            var c = chunk[p];
            if (c is >= '0' and <= '9')
            {
                num = (num < 0 ? 0 : num) * 10 + (c - '0');
                continue;
            }

            if (c == ';' || c == ':')
            {
                args.Add(num < 0 ? 0 : num);
                num = -1;
                continue;
            }

            if (c is '?' or '>' or '<' or '=')
            {
                priv = true;
                continue;
            }

            if (c is >= ' ' and <= '/')
            {
                intermediate = true; // ESC [ … SP q и т.п. — не наши команды
                continue;
            }

            if (c is >= '@' and <= '~')
            {
                if (num >= 0 || args.Count > 0) args.Add(num < 0 ? 0 : num);
                if (!intermediate) ApplyCsi(c, args, priv);
                return p;
            }
        }

        return -1;
    }

    private void ApplyCsi(char cmd, List<int> args, bool priv)
    {
        int N(int index, int fallback) => args.Count > index && args[index] > 0 ? args[index] : fallback;
        int A0() => args.Count > 0 ? args[0] : 0;

        if (priv)
        {
            if (cmd is 'h' or 'l')
            {
                var on = cmd == 'h';
                foreach (var mode in args)
                {
                    switch (mode)
                    {
                        case 1: AppCursorKeys = on; break;
                        case 25: CursorVisible = on; break;
                        case 9 or 1000 or 1002 or 1003:
                            MouseMode = on ? (mode == 9 ? 1000 : mode) : MouseMode == mode || (mode == 9 && MouseMode == 1000) ? 0 : MouseMode;
                            break;
                        case 1006: MouseSgr = on; break;
                        case 47 or 1047:
                            if (on) EnterAlt(); else LeaveAlt();
                            break;
                        case 1049:
                            if (on) { SaveCursor(); EnterAlt(); }
                            else { LeaveAlt(); RestoreCursor(); }
                            break;
                    }
                }
            }

            return;
        }

        switch (cmd)
        {
            case 'm': ApplySgr(args); break;
            case 'b': for (var k = 0; k < Math.Min(N(0, 1), _cols * _rows); k++) Put(_last); break;
            case 'A': MoveTo(ScreenRow - N(0, 1), _col); break;
            case 'B': case 'e': MoveTo(ScreenRow + N(0, 1), _col); break;
            case 'C': case 'a': MoveTo(ScreenRow, Math.Min(_col, _cols - 1) + N(0, 1)); break;
            case 'D': MoveTo(ScreenRow, Math.Min(_col, _cols - 1) - N(0, 1)); break;
            case 'E': MoveTo(ScreenRow + N(0, 1), 0); break;
            case 'F': MoveTo(ScreenRow - N(0, 1), 0); break;
            case 'G': case '`': MoveTo(ScreenRow, N(0, 1) - 1); break;
            case 'd': MoveTo(N(0, 1) - 1, _col); break;
            case 'H':
            case 'f':
                MoveTo(N(0, 1) - 1, N(1, 1) - 1);
                break;
            case 'J':
                switch (A0())
                {
                    case 0: EraseBelow(); break;
                    case 1: EraseAbove(); break;
                    case 2: EraseScreen(); break;
                    case 3: if (!AltScreen) { var keep = _lines.Skip(Top).ToList(); _row -= Top; _lines = keep; } break;
                }

                break;
            case 'K':
                {
                    var line = Line(_row);
                    var col = Math.Min(_col, _cols - 1);
                    switch (A0())
                    {
                        case 0: EraseLine(line, col, _cols); break;
                        case 1: line.Fill(0, col + 1, BlankCell); break;
                        case 2: EraseLine(line, 0, _cols); break;
                    }

                    break;
                }
            case 'X':
                {
                    var line = Line(_row);
                    var end = Math.Min(_col + N(0, 1), _cols);
                    if (BlankIsDefault) end = Math.Min(end, line.Length);
                    line.Fill(_col, end, BlankCell);
                    break;
                }
            case 'P':
                {
                    var line = Line(_row);
                    if (_col < line.Length)
                    {
                        line.Remove(_col, N(0, 1));
                        if (!BlankIsDefault) line.Fill(line.Length, _cols, BlankCell);
                    }

                    break;
                }
            case '@':
                {
                    var line = Line(_row);
                    line.Insert(_col, N(0, 1), BlankCell, _cols);
                    break;
                }
            case 'L': InsertLines(N(0, 1)); break;
            case 'M': DeleteLines(N(0, 1)); break;
            case 'S': for (var k = 0; k < N(0, 1); k++) ScrollUp(); break;
            case 'T': for (var k = 0; k < N(0, 1); k++) ScrollDown(); break;
            case 'r':
                _marginTop = Math.Max(0, N(0, 1) - 1);
                _marginBottom = args.Count > 1 && args[1] > 0 ? args[1] - 1 : -1;
                if (_marginBottom >= 0 && _marginBottom <= _marginTop) { _marginTop = 0; _marginBottom = -1; }
                MoveTo(0, 0);
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
        }
    }

    private void ApplySgr(List<int> args)
    {
        if (args.Count == 0)
        {
            _pen = Cell.Blank;
            return;
        }

        var fg = _pen.Fg;
        var bg = _pen.Bg;
        var flags = _pen.Flags;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case 0: fg = Cell.Default; bg = Cell.Default; flags = CellFlags.None; break;
                case 1: flags |= CellFlags.Bold; break;
                case 2: flags |= CellFlags.Dim; break;
                case 3: flags |= CellFlags.Italic; break;
                case 4: flags |= CellFlags.Underline; break;
                case 7: flags |= CellFlags.Inverse; break;
                case 8: flags |= CellFlags.Hidden; break;
                case 9: flags |= CellFlags.Strike; break;
                case 21: flags |= CellFlags.Underline; break;
                case 22: flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: flags &= ~CellFlags.Italic; break;
                case 24: flags &= ~CellFlags.Underline; break;
                case 27: flags &= ~CellFlags.Inverse; break;
                case 28: flags &= ~CellFlags.Hidden; break;
                case 29: flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: fg = a - 30; break;
                case 39: fg = Cell.Default; break;
                case >= 40 and <= 47: bg = a - 40; break;
                case 49: bg = Cell.Default; break;
                case >= 90 and <= 97: fg = a - 90 + 8; break;
                case >= 100 and <= 107: bg = a - 100 + 8; break;
                case 38 or 48:
                    {
                        var color = ExtendedColor(args, ref i);
                        if (color is { } c)
                        {
                            if (a == 38) fg = c; else bg = c;
                        }

                        break;
                    }
            }
        }

        _pen = new Cell(' ', fg, bg, flags);
    }

    /// <summary>38;5;n и 38;2;r;g;b (через ; или :).</summary>
    private static int? ExtendedColor(List<int> args, ref int i)
    {
        if (i + 1 >= args.Count) return null;
        var mode = args[i + 1];
        if (mode == 5 && i + 2 < args.Count)
        {
            var n = args[i + 2];
            i += 2;
            return Math.Clamp(n, 0, 255);
        }

        if (mode == 2 && i + 4 < args.Count)
        {
            var r = args[i + 2];
            var g = args[i + 3];
            var b = args[i + 4];
            i += 4;
            return Cell.Rgb(r, g, b);
        }

        i = args.Count; // непонятная форма — остаток не разбираем
        return null;
    }

    private void SaveCursor()
    {
        _savedRow = ScreenRow;
        _savedCol = _col;
        _savedPen = _pen;
    }

    private void RestoreCursor()
    {
        MoveTo(_savedRow, _savedCol);
        _pen = _savedPen;
    }

    private void EraseLine(TermLine line, int from, int to)
    {
        if (BlankIsDefault) line.Truncate(from);
        else
        {
            line.Fill(from, to, BlankCell);
            line.Truncate(to);
        }
    }

    private TermLine NewLine()
    {
        var line = new TermLine();
        if (!BlankIsDefault) line.Fill(0, _cols, BlankCell);
        return line;
    }

    private void EnterAlt()
    {
        if (AltScreen) return;
        _mainLines = _lines;
        _mainRow = _row;
        _mainCol = _col;
        _lines = Enumerable.Range(0, _rows).Select(_ => new TermLine()).ToList();
        _row = 0;
        _col = 0;
        _marginTop = 0;
        _marginBottom = -1;
    }

    private void LeaveAlt()
    {
        if (_mainLines is null) return;
        _lines = _mainLines;
        _mainLines = null;
        _row = _mainRow;
        _col = _mainCol;
        _marginTop = 0;
        _marginBottom = -1;
    }

    private void MoveTo(int screenRow, int col)
    {
        screenRow = Math.Clamp(screenRow, 0, _rows - 1);
        _row = Top + screenRow;
        EnsureRow(_row);
        _col = Math.Clamp(col, 0, _cols - 1);
    }

    private void Put(char c)
    {
        if (_col >= _cols)
        {
            _col = 0;
            LineFeed();
        }

        if ((_shiftOut ? _g1Graphics : _g0Graphics) && c is >= '`' and <= '~')
            c = DecGraphics[c - '`'];
        _last = c;
        Line(_row).Put(_col, _pen with { Ch = c });
        _col++;
    }

    private void LineFeed()
    {
        var screenRow = ScreenRow;
        if (screenRow == BottomMargin && (AltScreen || _marginTop > 0 || _marginBottom >= 0))
        {
            ScrollUp();
            return;
        }

        if (AltScreen)
        {
            if (screenRow < _rows - 1) _row++;
            return;
        }

        _row++;
        if (_row >= _lines.Count) _lines.Add(NewLine());
        EnsureRow(_row);
        while (_lines.Count > MaxScrollback)
        {
            _lines.RemoveAt(0);
            _row--;
        }
    }

    private void ReverseIndex()
    {
        if (ScreenRow == _marginTop) ScrollDown();
        else MoveTo(ScreenRow - 1, _col);
    }

    /// <summary>Сдвиг области прокрутки вверх на строку.</summary>
    private void ScrollUp()
    {
        var top = Top + _marginTop;
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        _lines.RemoveAt(top);
        _lines.Insert(bottom, NewLine());
    }

    private void ScrollDown()
    {
        var top = Top + _marginTop;
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        _lines.RemoveAt(bottom);
        _lines.Insert(top, NewLine());
    }

    private void InsertLines(int n)
    {
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        for (var k = 0; k < n && _row <= bottom; k++)
        {
            _lines.RemoveAt(bottom);
            _lines.Insert(_row, NewLine());
        }

        _col = 0;
    }

    private void DeleteLines(int n)
    {
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        for (var k = 0; k < n && _row <= bottom; k++)
        {
            _lines.RemoveAt(_row);
            _lines.Insert(bottom, NewLine());
        }

        _col = 0;
    }

    private void EraseBelow()
    {
        // Строки не удаляем, а чистим: иначе сдвинется верх экрана и курсор уедет в прокрутку.
        EraseLine(Line(_row), Math.Min(_col, _cols), _cols);
        for (var r = _row + 1; r < _lines.Count; r++) EraseLine(_lines[r], 0, _cols);
    }

    private void EraseAbove()
    {
        for (var r = Top; r < _row; r++) EraseLine(_lines[r], 0, _cols);
        var line = Line(_row);
        line.Fill(0, Math.Min(_col + 1, _cols), BlankCell);
    }

    private void EraseScreen()
    {
        if (AltScreen)
        {
            foreach (var l in _lines) EraseLine(l, 0, _cols);
            return;
        }

        // clear: экран пустой, прокрутка назад сохраняется. Экран дополняется до полной высоты,
        // чтобы его верх не сдвинулся.
        var top = Top;
        EnsureRow(top + _rows - 1);
        for (var r = top; r < _lines.Count; r++) EraseLine(_lines[r], 0, _cols);
    }

    private TermLine Line(int row)
    {
        EnsureRow(row);
        return _lines[row];
    }

    private void EnsureRow(int row)
    {
        while (_lines.Count <= row)
            _lines.Add(new TermLine());
    }
}
