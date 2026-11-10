using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using LinuxOps.Services;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>Цвета консоли PowerShell: ошибки, предупреждения, verbose, эхо команд, цвета Write-Host.</summary>
public sealed class PsConsoleColorizer : DocumentColorizingTransformer
{
    private readonly List<PsSpan> _spans;

    public PsConsoleColorizer(List<PsSpan> spans) => _spans = spans;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (_spans.Count == 0) return;
        // Отрезки идут по возрастанию Start — ищем первый, который может задеть строку.
        int lo = 0, hi = _spans.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].Start + _spans[mid].Length <= line.Offset) lo = mid + 1;
            else hi = mid;
        }

        for (var i = lo; i < _spans.Count; i++)
        {
            var s = _spans[i];
            if (s.Start >= line.EndOffset) break;
            var start = Math.Max(s.Start, line.Offset);
            var end = Math.Min(s.Start + s.Length, line.EndOffset);
            if (end <= start) continue;
            var brush = BrushFor(s);
            if (brush is null) continue;
            ChangeLinePart(start, end, el =>
            {
                el.TextRunProperties.SetForegroundBrush(brush);
                if (s.Kind == "echo") el.TextRunProperties.SetTypeface(new Typeface(el.TextRunProperties.Typeface.FontFamily,
                    FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
            });
        }
    }

    // Фон консоли тёмный в обеих темах (ConsoleBrush) — палитра консоли постоянная, под тёмный фон.
    private static readonly Brush Err = Frozen("#F07A7A");
    private static readonly Brush Warn = Frozen("#E0B060");
    private static readonly Brush Muted = Frozen("#8C9DAE");
    private static readonly Brush Echo = Frozen("#3CC7B8");

    private static Brush Frozen(string rgb)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rgb));
        b.Freeze();
        return b;
    }

    private static Brush? BrushFor(PsSpan s) => s.Color is { } c ? ConsoleColorBrush(c) : s.Kind switch
    {
        "err" => Err,
        "warn" => Warn,
        "verbose" or "debug" => Muted,
        "echo" => Echo,
        _ => null
    };

    /// <summary>ConsoleColor Write-Host → цвет, читаемый на тёмном фоне консоли.</summary>
    private static Brush? ConsoleColorBrush(string name)
    {
        var rgb = name switch
        {
            "Black" => "#7F8FA0",
            "DarkBlue" => "#5C8DEB",
            "DarkGreen" => "#4FB06A",
            "DarkCyan" => "#3FB5B5",
            "DarkRed" => "#D9605F",
            "DarkMagenta" => "#C27AD6",
            "DarkYellow" => "#C9A44A",
            "Gray" => "#C8CFD6",
            "DarkGray" => "#8C9DAE",
            "Blue" => "#7AA7FF",
            "Green" => "#6BD08A",
            "Cyan" => "#5FD7D7",
            "Red" => "#F07A7A",
            "Magenta" => "#E08AF0",
            "Yellow" => "#F0D264",
            "White" => "#FFFFFF",
            _ => null
        };
        if (rgb is null) return null;
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(rgb));
        b.Freeze();
        return b;
    }

    /// <summary>Подсветка PowerShell под тему: штатные цвета AvalonEdit рассчитаны на светлый фон.</summary>
    public static IHighlightingDefinition? ScriptHighlighting()
    {
        // Готовое определение заморожено — берём исходный xshd из сборки AvalonEdit и перекрашиваем копию.
        using var stream = typeof(HighlightingManager).Assembly.GetManifestResourceStream("ICSharpCode.AvalonEdit.Highlighting.Resources.PowerShell.xshd");
        if (stream is null) return HighlightingManager.Instance.GetDefinition("PowerShell");
        using var reader = System.Xml.XmlReader.Create(stream);
        var xshd = ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.LoadXshd(reader);
        var dark = ThemeManager.IsDark;
        foreach (var c in xshd.Elements.OfType<ICSharpCode.AvalonEdit.Highlighting.Xshd.XshdColor>())
        {
            var rgb = c.Name switch
            {
                "Comment" => dark ? "#6A9955" : "#008000",
                "String" or "Char" => dark ? "#CE9178" : "#A31515",
                "Keywords" or "Keyword" => dark ? "#569CD6" : "#0000FF",
                "Command" or "Commands" => dark ? "#4EC9B0" : "#2B91AF",
                "Variable" or "Variables" => dark ? "#9CDCFE" : "#C1440E",
                "Operators" or "Operator" => dark ? "#C8CFD6" : "#5A5A5A",
                "Digits" or "Number" => dark ? "#B5CEA8" : "#800080",
                _ => null
            };
            if (rgb is not null) c.Foreground = new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(rgb));
        }

        return ICSharpCode.AvalonEdit.Highlighting.Xshd.HighlightingLoader.Load(xshd, HighlightingManager.Instance);
    }
}
