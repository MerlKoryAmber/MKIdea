using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LinuxOps.Services;

namespace LinuxOps.Views;

/// <summary>
/// Отрисовка экрана терминала по ячейкам: цвета и атрибуты, курсор, выделение мышью, прокрутка истории.
/// Символы ставятся строго в сетку (GlyphRun с шагом ячейки), так что рамки mc и htop не «плывут».
/// </summary>
public sealed class TerminalCanvas : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TerminalCanvas),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TerminalCanvas),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(TerminalCanvas),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(TerminalCanvas),
        new FrameworkPropertyMetadata(new FontFamily("Consolas"), FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TerminalCanvas)d).ResetMetrics()));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(TerminalCanvas),
        new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((TerminalCanvas)d).ResetMetrics()));

    /// <summary>Палитра xterm: 16 цветов в духе Windows Terminal, дальше куб 6×6×6 и 24 серых.</summary>
    private static readonly Color[] Palette = BuildPalette();

    private readonly Dictionary<Color, SolidColorBrush> _brushes = new();
    private TerminalFrame _frame = TerminalFrame.Empty;
    private int _offset;            // первая видимая строка кадра
    private bool _follow = true;    // держать низ экрана
    private ScrollBar? _bar;
    private bool _barSync;

    private GlyphTypeface? _regular;
    private GlyphTypeface? _bold;
    private double _cw = 7.2;
    private double _lh = 16;
    private double _baseline = 12;
    private double _ppd = 1;
    private bool _metricsReady;

    private long _selA = -1;        // выделение: строка * Stride + колонка, [начало; конец)
    private long _selB = -1;
    private bool _dragging;
    private const long Stride = 1 << 16;

    public TerminalCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush SelectionBrush { get => (Brush)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public Thickness Padding { get; set; } = new(8, 6, 8, 6);

    public double CharWidth { get { EnsureMetrics(); return _cw; } }
    public double LineHeight { get { EnsureMetrics(); return _lh; } }

    public string Text => _frame.Text;

    /// <summary>Сколько строк помещается по высоте.</summary>
    public int VisibleRows => Math.Max(1, (int)Math.Floor((ActualHeight - Padding.Top - Padding.Bottom) / LineHeight));

    public int VisibleCols => Math.Max(1, (int)Math.Floor((ActualWidth - Padding.Left - Padding.Right) / CharWidth));

    public bool HasSelection => _selA >= 0 && _selA != _selB;

    /// <summary>Вертикальная полоса прокрутки рядом (своя у каждой сессии).</summary>
    public ScrollBar? ScrollBar
    {
        get => _bar;
        set
        {
            if (_bar is not null) _bar.ValueChanged -= Bar_ValueChanged;
            _bar = value;
            if (_bar is not null) _bar.ValueChanged += Bar_ValueChanged;
            SyncBar();
        }
    }

    public void SetFrame(TerminalFrame frame)
    {
        if (frame.AltScreen != _frame.AltScreen || frame.Lines.Length < _frame.Lines.Length && frame.AltScreen)
            ClearSelection();
        _frame = frame;
        if (_follow) _offset = MaxOffset;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        SyncBar();
        InvalidateVisual();
    }

    public void ScrollToBottom()
    {
        _follow = true;
        _offset = MaxOffset;
        SyncBar();
        InvalidateVisual();
    }

    public void ScrollLines(int delta)
    {
        _offset = Math.Clamp(_offset + delta, 0, MaxOffset);
        _follow = _offset >= MaxOffset;
        SyncBar();
        InvalidateVisual();
    }

    public void ScrollPage(int direction) => ScrollLines(direction * Math.Max(1, VisibleRows - 1));

    public void ClearSelection()
    {
        if (_selA < 0) return;
        _selA = _selB = -1;
        InvalidateVisual();
    }

    public string SelectedText
    {
        get
        {
            if (!HasSelection) return "";
            var (s, e) = Ordered();
            var sb = new StringBuilder();
            for (var li = (int)(s / Stride); li <= (int)(e / Stride) && li < _frame.Lines.Length; li++)
            {
                var from = li == (int)(s / Stride) ? (int)(s % Stride) : 0;
                var to = li == (int)(e / Stride) ? (int)(e % Stride) : int.MaxValue;
                if (li > (int)(s / Stride)) sb.Append("\r\n");
                sb.Append(TerminalFrame.LineText(_frame.Lines[li], from, to));
            }

            return sb.ToString();
        }
    }

    private int MaxOffset => Math.Max(0, _frame.Lines.Length - VisibleRows);

    private void SyncBar()
    {
        if (_bar is null) return;
        _barSync = true;
        _bar.Minimum = 0;
        _bar.Maximum = MaxOffset;
        _bar.ViewportSize = VisibleRows;
        _bar.LargeChange = Math.Max(1, VisibleRows - 1);
        _bar.SmallChange = 1;
        _bar.Value = _offset;
        _bar.Visibility = MaxOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
        _barSync = false;
    }

    private void Bar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_barSync) return;
        _offset = Math.Clamp((int)Math.Round(e.NewValue), 0, MaxOffset);
        _follow = _offset >= MaxOffset;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_follow) _offset = MaxOffset;
        _offset = Math.Clamp(_offset, 0, MaxOffset);
        SyncBar();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ResetMetrics();
    }

    private void ResetMetrics()
    {
        _metricsReady = false;
        InvalidateVisual();
    }

    private void EnsureMetrics()
    {
        if (_metricsReady) return;
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _regular = Resolve(FontWeights.Normal);
        _bold = Resolve(FontWeights.Bold) ?? _regular;
        var size = FontSize;
        if (_regular is not null && _regular.CharacterToGlyphMap.TryGetValue('M', out var g))
        {
            _cw = _regular.AdvanceWidths[g] * size;
            _lh = Snap(Math.Ceiling(_regular.Height * size * _ppd) / _ppd);
            _baseline = Snap(_regular.Baseline * size + (_lh - _regular.Height * size) / 2);
        }
        else
        {
            _cw = size * 0.6;
            _lh = Snap(size * 1.25);
            _baseline = Snap(size);
        }

        _metricsReady = true;
    }

    private double Snap(double v) => Math.Round(v * _ppd) / _ppd;

    /// <summary>Первый установленный шрифт из списка «Cascadia Mono, Consolas, …».</summary>
    private GlyphTypeface? Resolve(FontWeight weight)
    {
        foreach (var name in FontFamily.Source.Split(','))
        {
            var tf = new Typeface(new FontFamily(name.Trim()), FontStyles.Normal, weight, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out var gt) &&
                gt.FamilyNames.Values.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return gt;
        }

        return new Typeface(FontFamily, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var any) ? any : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsureMetrics();
        var bgDefault = (Background as SolidColorBrush)?.Color ?? Colors.Black;
        var fgDefault = (Foreground as SolidColorBrush)?.Color ?? Colors.LightGray;
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var lines = _frame.Lines;
        var rows = VisibleRows + 1;
        var (selS, selE) = HasSelection ? Ordered() : (-1L, -1L);
        var focused = IsKeyboardFocused;
        var x0 = Padding.Left;

        for (var r = 0; r < rows; r++)
        {
            var li = _offset + r;
            if (li >= lines.Length) break;
            var cells = lines[li];
            var y = Padding.Top + r * _lh;
            if (y > ActualHeight) break;

            // Фон: подряд идущие ячейки одного цвета — один прямоугольник.
            var runStart = 0;
            Color? runBg = null;
            for (var x = 0; x <= cells.Length; x++)
            {
                Color? bg = null;
                if (x < cells.Length)
                {
                    var (_, b) = CellColors(cells[x], fgDefault, bgDefault);
                    bg = b == bgDefault ? null : b;
                }

                if (x < cells.Length && bg == runBg) continue;
                if (runBg is { } fill && x > runStart)
                    dc.DrawRectangle(BrushOf(fill), null, new Rect(x0 + runStart * _cw, y, (x - runStart) * _cw + 0.5, _lh));
                runStart = x;
                runBg = bg;
            }

            // Выделение.
            if (selS >= 0)
            {
                var lineS = (long)li * Stride;
                var lineE = lineS + Stride;
                if (selS < lineE && selE > lineS)
                {
                    var a = selS > lineS ? (int)(selS - lineS) : 0;
                    var b = selE < lineE ? (int)(selE - lineS) : Math.Max(cells.Length, a) + 1;
                    if (b > a)
                        dc.DrawRectangle(SelectionBrush, null, new Rect(x0 + a * _cw, y, (b - a) * _cw, _lh));
                }
            }

            DrawText(dc, cells, x0, y, fgDefault, bgDefault);
        }

        // Курсор: блок в фокусе, рамка без фокуса. Прячется, если программа попросила (?25l) или история прокручена.
        if (_frame.CursorVisible && _frame.Lines.Length > 0)
        {
            var r = _frame.CursorLine - _offset;
            if (r >= 0 && r < rows)
            {
                var rect = new Rect(x0 + _frame.CursorCol * _cw, Padding.Top + r * _lh, _cw, _lh);
                if (focused)
                {
                    dc.DrawRectangle(BrushOf(fgDefault), null, rect);
                    var line = _frame.CursorLine < lines.Length ? lines[_frame.CursorLine] : Array.Empty<Cell>();
                    if (_frame.CursorCol < line.Length && line[_frame.CursorCol].Ch != ' ')
                    {
                        var c = line[_frame.CursorCol];
                        DrawGlyphs(dc, new[] { c.Ch }, 0, 1, c.Flags.HasFlag(CellFlags.Bold), rect.X, rect.Y, bgDefault);
                    }
                }
                else
                {
                    rect.Inflate(-0.5, -0.5);
                    dc.DrawRectangle(null, new Pen(BrushOf(fgDefault), 1), rect);
                }
            }
        }
    }

    private void DrawText(DrawingContext dc, Cell[] cells, double x0, double y, Color fgDefault, Color bgDefault)
    {
        var x = 0;
        var buf = new char[cells.Length];
        while (x < cells.Length)
        {
            var c = cells[x];
            var (fg, _) = CellColors(c, fgDefault, bgDefault);
            var bold = c.Flags.HasFlag(CellFlags.Bold);
            var under = c.Flags.HasFlag(CellFlags.Underline);
            var strike = c.Flags.HasFlag(CellFlags.Strike);
            var hidden = c.Flags.HasFlag(CellFlags.Hidden);
            var start = x;
            var n = 0;
            while (x < cells.Length)
            {
                var d = cells[x];
                if (d.Flags != c.Flags || CellColors(d, fgDefault, bgDefault).Fg != fg) break;
                buf[n++] = d.Ch;
                x++;
            }

            if (hidden) continue;
            var left = x0 + start * _cw;
            if (HasInk(buf, n))
                DrawGlyphs(dc, buf, 0, n, bold, left, y, fg);
            if (under)
                dc.DrawRectangle(BrushOf(fg), null, new Rect(left, y + _baseline + 1, n * _cw, 1));
            if (strike)
                dc.DrawRectangle(BrushOf(fg), null, new Rect(left, y + Math.Round(_lh / 2), n * _cw, 1));
        }
    }

    private static bool HasInk(char[] buf, int n)
    {
        for (var i = 0; i < n; i++)
            if (buf[i] != ' ') return true;
        return false;
    }

    /// <summary>Символы строго по сетке; то, чего нет в шрифте, — отдельно через подстановку шрифтов.</summary>
    private void DrawGlyphs(DrawingContext dc, char[] chars, int from, int count, bool bold, double left, double top, Color color)
    {
        var face = bold ? _bold : _regular;
        var brush = BrushOf(color);
        if (face is null)
        {
            DrawFallback(dc, new string(chars, from, count), left, top, brush, bold);
            return;
        }

        var glyphs = new List<ushort>(count);
        var advances = new List<double>(count);
        var runLeft = left;
        var size = FontSize;

        void FlushRun()
        {
            if (glyphs.Count == 0) return;
            var run = new GlyphRun(face, 0, false, size, (float)_ppd, glyphs.ToArray(),
                new Point(runLeft, top + _baseline), advances.ToArray(), null, null, null, null, null, null);
            dc.DrawGlyphRun(brush, run);
            glyphs.Clear();
            advances.Clear();
        }

        for (var i = 0; i < count; i++)
        {
            var ch = chars[from + i];
            var cellLeft = left + i * _cw;
            if (ch == ' ')
            {
                FlushRun();
                runLeft = cellLeft + _cw;
                continue;
            }

            if (face.CharacterToGlyphMap.TryGetValue(ch, out var glyph))
            {
                if (glyphs.Count == 0) runLeft = cellLeft;
                glyphs.Add(glyph);
                advances.Add(_cw);
                continue;
            }

            FlushRun();
            runLeft = cellLeft + _cw;
            DrawFallback(dc, ch.ToString(), cellLeft, top, brush, bold);
        }

        FlushRun();
    }

    private void DrawFallback(DrawingContext dc, string text, double left, double top, Brush brush, bool bold)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            FontSize, brush, _ppd);
        // Шире ячейки (эмодзи, иероглифы) — сжимаем, чтобы не залезать на соседей.
        var scale = ft.WidthIncludingTrailingWhitespace > _cw * text.Length * 1.05
            ? _cw * text.Length / ft.WidthIncludingTrailingWhitespace
            : 1;
        if (scale < 1) dc.PushTransform(new ScaleTransform(scale, 1, left, top));
        dc.DrawText(ft, new Point(left, top + (_lh - ft.Height) / 2));
        if (scale < 1) dc.Pop();
    }

    /// <summary>Итоговые цвета ячейки с учётом жирного (яркие 8–15), тусклого и инверсии.</summary>
    private static (Color Fg, Color Bg) CellColors(Cell c, Color fgDefault, Color bgDefault)
    {
        var fgIndex = c.Fg;
        if (c.Flags.HasFlag(CellFlags.Bold) && fgIndex is >= 0 and < 8) fgIndex += 8;
        var fg = Resolve(fgIndex, fgDefault);
        var bg = Resolve(c.Bg, bgDefault);
        if (c.Flags.HasFlag(CellFlags.Dim)) fg = Mix(fg, bg, 0.45);
        return c.Flags.HasFlag(CellFlags.Inverse) ? (bg, fg) : (fg, bg);
    }

    private static Color Resolve(int color, Color fallback)
    {
        if (color < 0) return fallback;
        if ((color & Cell.RgbFlag) != 0)
            return Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        return color < Palette.Length ? Palette[color] : fallback;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private SolidColorBrush BrushOf(Color color)
    {
        if (_brushes.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush(color);
        brush.Freeze();
        if (_brushes.Count > 512) _brushes.Clear();
        _brushes[color] = brush;
        return brush;
    }

    private static Color[] BuildPalette()
    {
        var p = new Color[256];
        string[] basic =
        {
            "#1E2630", "#E05561", "#3FB950", "#D7A23B", "#1F4DB8", "#B067D8", "#2FB8C4", "#C9D1D9",
            "#6E7681", "#FF7B85", "#6EE787", "#F2CC60", "#79A8FF", "#D2A8FF", "#6FE0E8", "#F5F7FA"
        };
        for (var i = 0; i < 16; i++) p[i] = (Color)ColorConverter.ConvertFromString(basic[i]);
        byte[] steps = { 0, 95, 135, 175, 215, 255 };
        for (var i = 0; i < 216; i++)
            p[16 + i] = Color.FromRgb(steps[i / 36], steps[i / 6 % 6], steps[i % 6]);
        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            p[232 + i] = Color.FromRgb(v, v, v);
        }

        return p;
    }

    // ── Мышь: выделение, колесо ─────────────────────────────────────────

    private long HitCell(Point p, bool nearestEdge)
    {
        var row = (int)Math.Floor((p.Y - Padding.Top) / _lh);
        var li = Math.Clamp(_offset + row, 0, Math.Max(0, _frame.Lines.Length - 1));
        var colF = (p.X - Padding.Left) / _cw;
        var col = (int)(nearestEdge ? Math.Round(colF) : Math.Floor(colF));
        col = Math.Clamp(col, 0, Math.Max(_frame.Cols, 1));
        return li * Stride + col;
    }

    private (long, long) Ordered() => _selA <= _selB ? (_selA, _selB) : (_selB, _selA);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Keyboard.Focus(this);
        EnsureMetrics();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            SelectWord(HitCell(p, false));
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 3)
        {
            var li = HitCell(p, false) / Stride;
            _selA = li * Stride;
            _selB = li * Stride + Math.Max(_frame.Cols, 1);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hit = HitCell(p, true);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selA >= 0) _selB = hit;
        else _selA = _selB = hit;
        _dragging = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        if (p.Y < 0) ScrollLines(-1);
        else if (p.Y > ActualHeight) ScrollLines(1);
        _selB = HitCell(p, true);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        if (_selA == _selB) ClearSelection();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollLines(-e.Delta / 40);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private void SelectWord(long at)
    {
        var li = (int)(at / Stride);
        var col = (int)(at % Stride);
        if (li >= _frame.Lines.Length) return;
        var cells = _frame.Lines[li];
        static bool Word(char c) => !char.IsWhiteSpace(c) && "│┃║─━═┌┐└┘├┤┬┴┼\"'()[]{}<>|;,".IndexOf(c) < 0;
        if (col >= cells.Length || !Word(cells[col].Ch)) return;
        var a = col;
        var b = col;
        while (a > 0 && Word(cells[a - 1].Ch)) a--;
        while (b < cells.Length && Word(cells[b].Ch)) b++;
        _selA = li * Stride + a;
        _selB = li * Stride + b;
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Для автотестов и экранных дикторов: текст экрана как значение.</summary>
    private sealed class Peer : FrameworkElementAutomationPeer, IValueProvider
    {
        public Peer(TerminalCanvas owner) : base(owner) { }

        private TerminalCanvas Canvas => (TerminalCanvas)Owner;

        protected override string GetClassNameCore() => nameof(TerminalCanvas);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override bool IsKeyboardFocusableCore() => true;
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        public bool IsReadOnly => true;
        public string Value => Canvas.Text;
        public void SetValue(string value) => throw new ElementNotEnabledException();
    }
}
