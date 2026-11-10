using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ICSharpCode.AvalonEdit.Document;
using LinuxOps.Services;

namespace LinuxOps.ViewModels;

/// <summary>Скрипт во вкладке редактора.</summary>
public sealed partial class PsScript : ObservableObject
{
    private static int _untitled;

    public PsScript(string? path = null, string text = "")
    {
        FilePath = path;
        Document = new TextDocument(text);
        Document.TextChanged += (_, _) => Dirty = true;
        Title = path is null ? $"Без имени{++_untitled}.ps1" : Path.GetFileName(path);
    }

    public TextDocument Document { get; }
    [ObservableProperty] private string? filePath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private string title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private bool dirty;
    public string Header => Dirty ? Title + " *" : Title;
}

/// <summary>Отрезок консоли с цветом: kind — out/err/warn/verbose/debug/host/echo, color — ConsoleColor Write-Host.</summary>
public readonly record struct PsSpan(int Start, int Length, string Kind, string? Color);

/// <summary>Вкладка PowerShell: скрипты, консоль, движок (один сеанс на вкладку — переменные и модули живут между запусками).</summary>
public sealed partial class PowerShellViewModel : ObservableObject, IDisposable
{
    private const int ConsoleLimit = 2_000_000;
    private readonly PsEngineClient _engine;
    private readonly List<string> _history = new();
    private int _historyPos;

    public PowerShellViewModel()
    {
        _engine = new PsEngineClient(Application.Current.Dispatcher);
        _engine.Ready += () =>
        {
            Status = $"PowerShell {_engine.Version} · готов";
            UpdatePrompt();
        };
        _engine.Output += (kind, text, color, newLine) => Append(text + (newLine ? "\n" : ""), kind, color);
        _engine.Progress += text => Progress = text;
        _engine.Prompt += m => _engine.Reply(m.Id, PsPrompts.Ask(Owner?.Invoke(), m));
        _engine.Done += (_, ok, note) =>
        {
            Busy = false;
            Progress = "";
            Status = note is { Length: > 0 } ? note : ok ? "готово" : "завершено с ошибками";
            UpdatePrompt();
        };
        _engine.Exited += reason =>
        {
            Busy = false;
            Status = reason + " — «Перезапустить движок»";
            Append(reason + "\n", "err", null);
        };
        Scripts.Add(new PsScript());
        SelectedScript = Scripts[0];
    }

    public Func<Window?>? Owner { get; set; }
    public ObservableCollection<PsScript> Scripts { get; } = new();
    [ObservableProperty] private PsScript? selectedScript;
    public TextDocument ConsoleDoc { get; } = new();
    public List<PsSpan> Spans { get; } = new();

    /// <summary>Консоль дописана — вид прокручивает вниз и перерисовывает цвета.</summary>
    public event Action? ConsoleChanged;

    [ObservableProperty] private bool busy;
    [ObservableProperty] private string status = "движок не запущен";
    [ObservableProperty] private string progress = "";
    [ObservableProperty] private string promptText = "PS>";

    public void EnsureEngine()
    {
        if (_engine.IsRunning) return;
        try
        {
            Status = "запуск PowerShell…";
            _engine.Start();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            Append(ex.Message + "\n", "err", null);
        }
    }

    public void SetWidth(int columns) => _engine.SetWidth(columns);

    private void UpdatePrompt() => PromptText = $"PS {_engine.Location}>";

    // ---------- Выполнение ----------

    /// <summary>F5: весь скрипт текущей вкладки.</summary>
    public void RunScript()
    {
        if (SelectedScript is not { } s || s.Document.TextLength == 0) return;
        var label = s.FilePath ?? s.Title;
        Execute(s.Document.Text, $"{PromptText} ▶ {label}");
    }

    /// <summary>F8: выделенное или текущая строка.</summary>
    public void RunSelection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Execute(text, PromptText + " " + text.TrimEnd().Replace("\n", "\n>> "));
    }

    /// <summary>Строка консоли.</summary>
    public void RunLine(string line)
    {
        if (Busy) return;
        if (!string.IsNullOrWhiteSpace(line) && (_history.Count == 0 || _history[^1] != line)) _history.Add(line);
        _historyPos = _history.Count;
        Execute(line, PromptText + " " + line);
    }

    private void Execute(string script, string echo)
    {
        EnsureEngine();
        if (Busy || !_engine.IsRunning) return;
        Append(echo + "\n", "echo", null);
        if (_engine.Run(script) == 0) return;
        Busy = true;
        Status = "выполняется… (Ctrl+Break — стоп)";
    }

    public void Stop()
    {
        if (!Busy) return;
        Status = "остановка…";
        _engine.Stop();
    }

    /// <summary>Убить процесс и начать заново: переменные, модули и подключения сбрасываются.</summary>
    public void Restart()
    {
        _engine.Kill();
        Busy = false;
        Progress = "";
        Append("— движок перезапущен: переменные, модули и подключения сброшены —\n", "verbose", null);
        EnsureEngine();
    }

    public string? HistoryStep(int delta)
    {
        if (_history.Count == 0) return null;
        _historyPos = Math.Clamp(_historyPos + delta, 0, _history.Count);
        return _historyPos < _history.Count ? _history[_historyPos] : "";
    }

    // ---------- Консоль ----------

    public void ClearConsole()
    {
        ConsoleDoc.Text = "";
        Spans.Clear();
        ConsoleChanged?.Invoke();
    }

    private void Append(string text, string kind, string? color)
    {
        if (text.Length == 0) return;
        if (ConsoleDoc.TextLength + text.Length > ConsoleLimit) Trim();
        var start = ConsoleDoc.TextLength;
        ConsoleDoc.Insert(start, text);
        if (kind != "out" || color is not null) Spans.Add(new PsSpan(start, text.Length, kind, color));
        ConsoleChanged?.Invoke();
    }

    /// <summary>Длинный вывод: срезаем первую половину по границе строки.</summary>
    private void Trim()
    {
        var cut = ConsoleDoc.TextLength / 2;
        var line = ConsoleDoc.GetLineByOffset(cut);
        cut = line.EndOffset + line.DelimiterLength;
        ConsoleDoc.Remove(0, cut);
        var kept = Spans.Where(s => s.Start + s.Length > cut)
            .Select(s => new PsSpan(Math.Max(0, s.Start - cut), s.Length - Math.Max(0, cut - s.Start), s.Kind, s.Color))
            .ToList();
        Spans.Clear();
        Spans.AddRange(kept);
    }

    // ---------- Файлы ----------

    public void NewScript()
    {
        var s = new PsScript();
        Scripts.Add(s);
        SelectedScript = s;
    }

    public void OpenScript(string path)
    {
        if (Scripts.FirstOrDefault(s => string.Equals(s.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            SelectedScript = open;
            return;
        }

        var script = new PsScript(path, File.ReadAllText(path)) { Dirty = false };
        // Пустая нетронутая вкладка «Без имени» заменяется открытым файлом.
        if (SelectedScript is { FilePath: null, Dirty: false, Document.TextLength: 0 } blank) Scripts.Remove(blank);
        Scripts.Add(script);
        SelectedScript = script;
    }

    public void SaveScript(PsScript s, string path)
    {
        File.WriteAllText(path, s.Document.Text, new System.Text.UTF8Encoding(true)); // BOM — Windows PowerShell 5.1 тоже прочтёт кириллицу
        s.FilePath = path;
        s.Title = Path.GetFileName(path);
        s.Dirty = false;
    }

    public void CloseScript(PsScript s)
    {
        var i = Scripts.IndexOf(s);
        Scripts.Remove(s);
        if (Scripts.Count == 0) Scripts.Add(new PsScript());
        SelectedScript = Scripts[Math.Clamp(i, 0, Scripts.Count - 1)];
    }

    public void Dispose() => _engine.Dispose();
}
