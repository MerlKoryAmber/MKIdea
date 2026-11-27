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

    /// <summary>
    /// Кодировка файла на диске такая, что PowerShell 7 сам его не прочтёт (UTF-16 без BOM, ANSI) — F5 запускает копию в UTF-8.
    /// После «Сохранить» файл уже в UTF-8 с BOM.
    /// </summary>
    public string? UnreadableEncoding { get; set; }
    [ObservableProperty] private string? filePath;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private string title;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private bool dirty;
    public string Header => Dirty ? Title + " *" : Title;
}

/// <summary>
/// Вкладка PowerShell как в VS Code: скрипты сверху, настоящая консоль PowerShell снизу (PsConsoleSession — ConPTY + PSReadLine),
/// F5/F8 выполняют в этой консоли. Подсказки редактора — от отдельного движка (PsEngineClient): знает модули и сам скрипт,
/// но не переменные, заданные в консоли.
/// </summary>
public sealed partial class PowerShellViewModel : ObservableObject, IDisposable
{
    private readonly PsEngineClient _completer;

    public PowerShellViewModel()
    {
        Console = new PsConsoleSession();
        Console.StatusChanged += () => Status = Console.Status;
        _completer = new PsEngineClient(Application.Current.Dispatcher);
        _completer.Exited += _ => { /* перезапустится при следующей подсказке */ };
        Scripts.Add(new PsScript());
        SelectedScript = Scripts[0];
    }

    public Func<Window?>? Owner { get; set; }
    public ObservableCollection<PsScript> Scripts { get; } = new();
    [ObservableProperty] private PsScript? selectedScript;
    public PsConsoleSession Console { get; }
    [ObservableProperty] private string status = "консоль не запущена";

    /// <summary>Вкладка открыта: консоль и движок подсказок.</summary>
    public void EnsureStarted()
    {
        Console.Start();
        EnsureCompleter();
    }

    private void EnsureCompleter()
    {
        if (_completer.IsRunning) return;
        try { _completer.Start(); }
        catch { /* без подсказок */ }
    }

    public Task<PsMsg?> Complete(string text, int caret)
    {
        EnsureCompleter();
        return _completer.Complete(text, caret);
    }

    /// <summary>Перезапуск консоли: переменные, модули и подключения сбрасываются.</summary>
    public void Restart() => Console.Restart();

    /// <summary>
    /// F5: команда для консоли. Сохранённый файл — dot-source самого файла ($PSScriptRoot верный), «Без имени» — временной копии.
    /// Перед этим вид сохраняет изменённый файл (как VS Code).
    /// </summary>
    public string? RunCommand(PsScript s)
    {
        if (s.Document.TextLength == 0) return null;
        var path = s.FilePath;
        if (path is null || s.UnreadableEncoding is not null)
        {
            var dir = Path.Combine(Path.GetTempPath(), "LinuxOps", "PowerShell");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, s.Title);
            File.WriteAllText(path, s.Document.Text, new System.Text.UTF8Encoding(true));
        }

        return ". '" + path.Replace("'", "''") + "'";
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

        // Кодировку определяем сами: File.ReadAllText считает файл без BOM UTF-8, и UTF-16 без BOM
        // превращался в текст с «\0» между символами — в консоли «#» переставал быть комментарием (замечание владельца).
        var read = ScriptFile.Read(path);
        var script = new PsScript(path, read.Text) { Dirty = false, UnreadableEncoding = read.PwshReadable ? null : read.EncodingName };
        if (!read.PwshReadable)
            Status = $"{script.Title}: файл в кодировке {read.EncodingName} — PowerShell сам его так не прочтёт; F5 запускает копию в UTF-8, «Сохранить» перезапишет файл в UTF-8 с BOM";
        // Пустая нетронутая вкладка «Без имени» заменяется открытым файлом.
        if (SelectedScript is { FilePath: null, Dirty: false, Document.TextLength: 0 } blank) Scripts.Remove(blank);
        Scripts.Add(script);
        SelectedScript = script;
    }

    public void SaveScript(PsScript s, string path)
    {
        File.WriteAllText(path, s.Document.Text, new System.Text.UTF8Encoding(true)); // BOM — Windows PowerShell 5.1 тоже прочтёт кириллицу
        s.FilePath = path;
        s.UnreadableEncoding = null;
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

    public void Dispose()
    {
        Console.Dispose();
        _completer.Dispose();
    }
}
