using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using LinuxOps.Services;

namespace LinuxOps.Views;

/// <summary>
/// Редактор скриптов как в VS Code: подсказки по мере набора и по Ctrl+Space (команды, параметры, переменные, пути, члены),
/// поиск Ctrl+F (F3 — дальше), автоотступ после «{», отмена по словам, а не по буквам.
/// </summary>
public sealed class PsEditorAssist
{
    private readonly TextEditor _editor;
    private readonly Func<string, int, Task<PsMsg?>> _complete;
    private readonly DispatcherTimer _typing;
    private CompletionWindow? _window;
    private int _request;

    private PsEditorAssist(TextEditor editor, Func<string, int, Task<PsMsg?>> complete)
    {
        _editor = editor;
        _complete = complete;
        _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            _ = Show(auto: true);
        };

        var area = editor.TextArea;
        area.IndentationStrategy = new BraceIndentation();
        area.TextEntering += OnTextEntering;
        area.TextEntered += OnTextEntered;
        area.PreviewKeyDown += OnKeyDown;
        area.Caret.PositionChanged += (_, _) => EndWord();
        // Любая команда (Ctrl+Z, Ctrl+Y, вырезать, вставить) и любая клавиша — при закрытой группе отмены, иначе AvalonEdit падает.
        CommandManager.AddPreviewExecutedHandler(area, (_, _) => CloseGroup());
        area.PreviewKeyDown += (_, _) => CloseGroup();
        var search = SearchPanel.Install(area);
        search.Localization = new RuSearch();
    }

    public static PsEditorAssist Attach(TextEditor editor, Func<string, int, Task<PsMsg?>> complete) => new(editor, complete);

    public bool PopupOpen => _window is not null;

    // ---------- Отмена по словам ----------

    private bool _inWord;
    private bool _groupOpen;
    private int _wordEnd = -1;

    /// <summary>Группа, открытая в TextEntering, закрывается в TextEntered; если ввод перехватили (окно подсказок) — здесь.</summary>
    private void CloseGroup()
    {
        if (!_groupOpen) return;
        _groupOpen = false;
        try { _editor.Document.UndoStack.EndUndoGroup(); }
        catch (InvalidOperationException) { /* уже закрыта */ }
    }

    private void OnTextEntering(object sender, TextCompositionEventArgs e)
    {
        var c = e.Text.Length == 1 ? e.Text[0] : '\0';
        var word = char.IsLetterOrDigit(c) || c is '_' or '-';
        var doc = _editor.Document;
        CloseGroup();
        // Список открыт, а набирается не слово (пробел, скобка, кавычка, «=») — закрыть, не вставляя: Enter после «{» переносит строку.
        if (_window is not null && !(char.IsLetterOrDigit(c) || c is '_' or '-' or '$' or '.' or ':' or '\\'))
            _window.Close();
        if (word && _inWord && _editor.CaretOffset == _wordEnd)
        {
            doc.UndoStack.StartContinuedUndoGroup("word");
            _groupOpen = true;
        }
        else
        {
            EndWord();
            if (word)
            {
                doc.UndoStack.StartUndoGroup("word");
                _groupOpen = true;
                _inWord = true;
            }
        }
    }

    private void EndWord()
    {
        // Курсор сдвигает и сама вставка символа (группа ещё открыта) — это не уход из слова.
        if (!_inWord || _groupOpen || _editor.CaretOffset == _wordEnd) return;
        _inWord = false;
        _wordEnd = -1;
    }

    private void OnTextEntered(object sender, TextCompositionEventArgs e)
    {
        if (_groupOpen)
        {
            CloseGroup();
            _wordEnd = _editor.CaretOffset;
        }

        var c = e.Text.Length == 1 ? e.Text[0] : '\0';
        // «}» первым символом строки — на уровень «{».
        if (c == '}') Outdent();

        if (_window is not null) return;
        if (c is '-' or '$' or '.' or ':' or '\\' or '[')
        {
            _typing.Stop();
            _ = Show(auto: true);
        }
        else if (char.IsLetter(c))
        {
            _typing.Stop();
            _typing.Start();
        }
        else
        {
            _typing.Stop();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _ = Show(auto: false);
        }
    }

    private void Outdent()
    {
        var doc = _editor.Document;
        var line = doc.GetLineByOffset(_editor.CaretOffset);
        var text = doc.GetText(line);
        if (text.Trim() != "}") return;
        var indent = text.Length - text.TrimStart().Length;
        if (indent == 0) return;
        var remove = Math.Min(4, indent);
        doc.Remove(line.Offset, remove);
    }

    // ---------- Подсказки ----------

    private async Task Show(bool auto)
    {
        var doc = _editor.Document;
        var caret = _editor.CaretOffset;
        // Сами — только пока набирается слово (буква, «-», «$», «.», «:», «\», «[» перед курсором): после «{», «)», пробела
        // список не нужен, а Enter должен переносить строку. Ctrl+Space — всегда.
        if (auto && (caret == 0 || !(char.IsLetter(doc.GetCharAt(caret - 1)) || doc.GetCharAt(caret - 1) is '-' or '$' or '.' or ':' or '\\' or '[' or '_')))
            return;
        var id = ++_request;
        var text = doc.Text;
        PsMsg? r;
        try { r = await _complete(text, caret); }
        catch { return; }
        // Пока ждали — курсор ушёл или текст поменялся не набором: не показываем устаревшее.
        if (id != _request || r?.Completions is not { Count: > 0 } list || _window is not null) return;
        if (_editor.CaretOffset < r.Start || _editor.CaretOffset > doc.TextLength) return;
        if (!ReferenceEquals(doc, _editor.Document)) return;
        var typed = doc.GetText(r.Start, Math.Max(0, _editor.CaretOffset - r.Start));
        // Ответ пришёл, а набор ушёл дальше слова («$name) {») — не показываем, иначе Enter вставит подсказку вместо переноса.
        if (auto && !System.Text.RegularExpressions.Regex.IsMatch(typed, @"^[\w\-\$\.:\\\[\]]*$")) return;
        if (auto && list.Count == 1 && string.Equals(list[0].Text, typed, StringComparison.OrdinalIgnoreCase)) return;

        var w = new CompletionWindow(_editor.TextArea)
        {
            StartOffset = r.Start,
            EndOffset = _editor.CaretOffset,
            CloseWhenCaretAtBeginning = true,
            CloseAutomatically = true,
            Width = 420,
            MaxHeight = 320,
            Background = (Brush)Application.Current.FindResource("SurfaceBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush")
        };
        w.CompletionList.ListBox.Background = w.Background;
        w.CompletionList.ListBox.Foreground = w.Foreground;
        w.CompletionList.ListBox.BorderThickness = new Thickness(0);
        foreach (var c in list) w.CompletionList.CompletionData.Add(new Item(c));
        w.CompletionList.SelectItem(typed);
        if (auto && w.CompletionList.SelectedItem is null && typed.Length > 0) return; // ничего не подходит — молчим
        w.Closed += (_, _) => { if (ReferenceEquals(_window, w)) _window = null; };
        _window = w;
        w.Show();
    }

    /// <summary>Пункт списка: значок типа + текст; описание — синтаксис команды или тип переменной.</summary>
    private sealed class Item : ICompletionData
    {
        private readonly PsCompletion _c;

        public Item(PsCompletion c) => _c = c;

        public ImageSource? Image => null;
        // По Text список фильтрует набранное: «-Pa» должно совпасть с «-Path», а не с «Path».
        public string Text => _c.Text;

        public object Content => ItemContent(_c);

        public object? Description => string.IsNullOrWhiteSpace(_c.Tip) ? null : _c.Tip!.Trim();
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, _c.Text);
    }

    /// <summary>Строка списка подсказок: значок типа + текст. Общая для редактора и консоли.</summary>
    public static FrameworkElement ItemContent(PsCompletion c)
    {
        var shown = c.List.Length > 0 ? c.List : c.Text;
        var (glyph, color) = c.Type switch
        {
            "Command" => ("ƒ", "#3CC7B8"),
            "Parameter" or "ParameterName" => ("-", "#D7A84A"),
            "Variable" => ("$", "#7FB2F0"),
            "Property" => ("▪", "#B48EDA"),
            "Method" => ("◆", "#B48EDA"),
            "ProviderContainer" => ("▸", "#8FA3B5"),
            "ProviderItem" => ("·", "#8FA3B5"),
            "Type" or "Namespace" => ("T", "#6CC08B"),
            "Keyword" => ("k", "#E07A7A"),
            _ => ("•", "#8FA3B5")
        };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock
        {
            Text = glyph, Width = 16, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!
        });
        p.Children.Add(new TextBlock { Text = shown, Margin = new Thickness(4, 0, 0, 0) });
        return p;
    }

    // ---------- Отступы ----------

    /// <summary>Новая строка — отступ предыдущей; после «{», «(», «@(», «@{» — на 4 пробела глубже.</summary>
    private sealed class BraceIndentation : IIndentationStrategy
    {
        public void IndentLine(TextDocument document, DocumentLine line)
        {
            var prev = line.PreviousLine;
            if (prev is null) return;
            var prevText = document.GetText(prev);
            var indent = prevText[..(prevText.Length - prevText.TrimStart().Length)];
            var t = prevText.TrimEnd();
            if (t.EndsWith('{') || t.EndsWith('(')) indent += "    ";
            var cur = document.GetText(line);
            var curIndent = cur.Length - cur.TrimStart().Length;
            document.Replace(line.Offset, curIndent, indent);
        }

        public void IndentLines(TextDocument document, int beginLine, int endLine) { }
    }

    private sealed class RuSearch : ICSharpCode.AvalonEdit.Search.Localization
    {
        public override string MatchCaseText => "С учётом регистра";
        public override string MatchWholeWordsText => "Слово целиком";
        public override string UseRegexText => "Регулярное выражение";
        public override string FindNextText => "Найти далее (F3)";
        public override string FindPreviousText => "Найти назад (Shift+F3)";
        public override string ErrorText => "Ошибка: ";
        public override string NoMatchesFoundText => "Не найдено";
    }
}
