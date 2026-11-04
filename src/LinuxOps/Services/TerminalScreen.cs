using System.Text;

namespace LinuxOps.Services;

/// <summary>
/// Простой экран xterm: перенос по ширине, прокрутка, альтернативный экран (vim, less, mc),
/// область прокрутки, вставка и удаление символов и строк. Цвета отбрасываются.
/// </summary>
public sealed class TerminalScreen
{
    private const int MaxScrollback = 1500;

    private List<StringBuilder> _lines = new() { new() };
    private int _row;
    private int _col;
    private int _cols = 120;
    private int _rows = 40;
    private int _marginTop;
    private int _marginBottom = -1; // -1 — до низа экрана
    private int _savedRow;
    private int _savedCol;

    private List<StringBuilder>? _mainLines;
    private int _mainRow;
    private int _mainCol;

    public int Columns => _cols;
    public int Rows => _rows;
    public bool AltScreen => _mainLines is not null;
    /// <summary>DECCKM: стрелки как ESC O A (vim, less, mc включают этот режим).</summary>
    public bool AppCursorKeys { get; private set; }

    public string Text => string.Join("\n", _lines.Select(l => l.ToString().TrimEnd()));

    /// <summary>Позиция курсора в <see cref="Text"/>.</summary>
    public int CursorIndex
    {
        get
        {
            var index = 0;
            for (var i = 0; i < _row && i < _lines.Count; i++)
                index += _lines[i].ToString().TrimEnd().Length + 1;
            var line = _row < _lines.Count ? _lines[_row].ToString().TrimEnd() : "";
            return index + Math.Min(Math.Min(_col, _cols - 1), line.Length);
        }
    }

    /// <summary>Последняя строка с текстом (для распознавания запроса пароля).</summary>
    public string CurrentLine => _row < _lines.Count ? _lines[_row].ToString().TrimEnd() : "";

    private int Top => AltScreen ? 0 : Math.Max(0, _lines.Count - _rows);
    private int ScreenRow => _row - Top;
    private int BottomMargin => _marginBottom < 0 || _marginBottom >= _rows ? _rows - 1 : _marginBottom;

    public void Resize(int cols, int rows)
    {
        _cols = Math.Clamp(cols, 20, 500);
        _rows = Math.Clamp(rows, 5, 200);
        _marginTop = 0;
        _marginBottom = -1;
        if (AltScreen)
        {
            while (_lines.Count < _rows) _lines.Add(new StringBuilder());
            while (_lines.Count > _rows) _lines.RemoveAt(_lines.Count - 1);
            _row = Math.Min(_row, _rows - 1);
        }

        _col = Math.Min(_col, _cols - 1);
    }

    public void Clear()
    {
        _lines = new List<StringBuilder> { new() };
        _mainLines = null;
        _row = 0;
        _col = 0;
        _marginTop = 0;
        _marginBottom = -1;
        AppCursorKeys = false;
    }

    public void Feed(ReadOnlySpan<char> chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            switch (c)
            {
                case '\u001b':
                    i = ConsumeEscape(chunk, i);
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
                case '\a':
                case '\0':
                case '\u000e':
                case '\u000f':
                case '\u007f':
                    continue;
            }

            if (c < ' ') continue;
            Put(c);
        }
    }

    private int ConsumeEscape(ReadOnlySpan<char> chunk, int i)
    {
        if (i + 1 >= chunk.Length) return i;
        var next = chunk[i + 1];
        switch (next)
        {
            case '[': return ConsumeCsi(chunk, i + 2);
            case ']': return ConsumeOsc(chunk, i + 2);
            case '(' or ')' or '*' or '+' or '-' or '.' or '#' or '%':
                return Math.Min(i + 2, chunk.Length - 1);
            case '7': _savedRow = ScreenRow; _savedCol = _col; return i + 1;
            case '8': MoveTo(_savedRow, _savedCol); return i + 1;
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

        return chunk.Length - 1;
    }

    private int ConsumeCsi(ReadOnlySpan<char> chunk, int i)
    {
        var args = new List<int>();
        var num = -1;
        var priv = false;
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

            if (c is >= ' ' and <= '/') continue; // промежуточные байты
            if (c is >= '@' and <= '~')
            {
                if (num >= 0 || args.Count > 0) args.Add(num < 0 ? 0 : num);
                ApplyCsi(c, args, priv);
                return p;
            }
        }

        return chunk.Length - 1;
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
                        case 47 or 1047 or 1049:
                            if (on) EnterAlt(); else LeaveAlt();
                            break;
                    }
                }
            }

            return;
        }

        switch (cmd)
        {
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
                        case 0: if (col < line.Length) line.Length = col; break;
                        case 1: for (var x = 0; x <= col && x < line.Length; x++) line[x] = ' '; break;
                        case 2: line.Clear(); break;
                    }

                    break;
                }
            case 'X':
                {
                    var line = Line(_row);
                    for (var x = _col; x < _col + N(0, 1) && x < line.Length; x++) line[x] = ' ';
                    break;
                }
            case 'P':
                {
                    var line = Line(_row);
                    if (_col < line.Length) line.Remove(_col, Math.Min(N(0, 1), line.Length - _col));
                    break;
                }
            case '@':
                {
                    var line = Line(_row);
                    if (_col < line.Length)
                    {
                        line.Insert(_col, new string(' ', N(0, 1)));
                        if (line.Length > _cols) line.Length = _cols;
                    }

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
            case 's': _savedRow = ScreenRow; _savedCol = _col; break;
            case 'u': MoveTo(_savedRow, _savedCol); break;
        }
    }

    private void EnterAlt()
    {
        if (AltScreen) return;
        _mainLines = _lines;
        _mainRow = _row;
        _mainCol = _col;
        _lines = Enumerable.Range(0, _rows).Select(_ => new StringBuilder()).ToList();
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

        var line = Line(_row);
        if (_col < line.Length) line[_col] = c;
        else
        {
            if (line.Length < _col) line.Append(' ', _col - line.Length);
            line.Append(c);
        }

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
        _lines.Insert(bottom, new StringBuilder());
    }

    private void ScrollDown()
    {
        var top = Top + _marginTop;
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        _lines.RemoveAt(bottom);
        _lines.Insert(top, new StringBuilder());
    }

    private void InsertLines(int n)
    {
        var bottom = Top + BottomMargin;
        EnsureRow(bottom);
        for (var k = 0; k < n && _row <= bottom; k++)
        {
            _lines.RemoveAt(bottom);
            _lines.Insert(_row, new StringBuilder());
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
            _lines.Insert(bottom, new StringBuilder());
        }

        _col = 0;
    }

    private void EraseBelow()
    {
        // Строки не удаляем, а чистим: иначе сдвинется верх экрана и курсор уедет в прокрутку.
        var line = Line(_row);
        if (_col < line.Length) line.Length = _col;
        for (var r = _row + 1; r < _lines.Count; r++) _lines[r].Clear();
    }

    private void EraseAbove()
    {
        for (var r = Top; r < _row; r++) _lines[r].Clear();
        var line = Line(_row);
        for (var x = 0; x <= _col && x < line.Length; x++) line[x] = ' ';
    }

    private void EraseScreen()
    {
        if (AltScreen)
        {
            foreach (var l in _lines) l.Clear();
            return;
        }

        // clear: экран пустой, прокрутка назад сохраняется. Экран дополняется до полной высоты,
        // чтобы его верх не сдвинулся.
        var top = Top;
        for (var r = top; r < _lines.Count; r++) _lines[r].Clear();
        EnsureRow(top + _rows - 1);
    }

    private StringBuilder Line(int row)
    {
        EnsureRow(row);
        return _lines[row];
    }

    private void EnsureRow(int row)
    {
        while (_lines.Count <= row)
            _lines.Add(new StringBuilder());
    }
}
