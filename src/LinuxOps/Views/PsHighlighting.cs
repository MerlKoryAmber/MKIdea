using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using LinuxOps.Services;

namespace LinuxOps.Views;

/// <summary>Подсветка скриптов PowerShell в редакторе под тему (консоль теперь рисует сам PowerShell — ConPTY).</summary>
public static class PsHighlighting
{
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
