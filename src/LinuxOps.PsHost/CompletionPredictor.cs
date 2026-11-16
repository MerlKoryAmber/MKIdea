using System.IO.Pipes;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Management.Automation.Subsystem;
using System.Management.Automation.Subsystem.Prediction;
using System.Text;
using LinuxOps.Services;

namespace LinuxOps.PsHost;

/// <summary>
/// Подсказки в консоли по мере набора, как в терминале VS Code. PSReadLine сообщает сюда каждую правку строки ввода;
/// дополнения PowerShell (команды, параметры, пути, значения) уходят по каналу в LinuxOps, а она рисует список у курсора.
/// Сам PSReadLine показывает только серый хвост из истории (InlineView) — отсюда ему ничего не возвращается.
/// Свой runspace: знает модули и папку консоли, но не её переменные.
/// </summary>
public sealed class CompletionPredictor : ICommandPredictor
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private Runspace? _rs;
    private StreamWriter? _pipe;
    private string? _pending;
    private int _seq;

    public Guid Id { get; } = new("7d1a6f40-6a5e-4d0b-9e57-3b3c4c2f9a11");
    public string Name => "LinuxOps";
    public string Description => "Дополнения PowerShell для набранной строки (список в LinuxOps)";
    public Dictionary<string, string>? FunctionsToDefine => null;

    /// <param name="pipeName">Канал LinuxOps для списка подсказок; null — консоль запущена не из LinuxOps.</param>
    public static void Register(string? pipeName)
    {
        var p = new CompletionPredictor();
        SubsystemManager.RegisterSubsystem(SubsystemKind.CommandPredictor, p);
        if (pipeName is null) return;
        new Thread(() => p.Work(pipeName)) { IsBackground = true, Name = "suggest" }.Start();
    }

    private void Work(string pipeName)
    {
        try
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(5000);
            _pipe = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }
        catch
        {
            return; // без списка: серый хвост из истории остаётся
        }

        // Прогрев: первое дополнение собирает кэш команд (секунды) — сразу, а не на первой подсказке.
        var iss = InitialSessionState.CreateDefault2();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        _rs = RunspaceFactory.CreateRunspace(iss);
        _rs.Open();
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = _rs;
            CommandCompletion.CompleteInput("Get-Ch", 6, null, ps);
        }
        catch { /* не страшно */ }

        while (true)
        {
            _wake.WaitOne();
            string? input;
            int seq;
            lock (_gate)
            {
                input = _pending;
                _pending = null;
                seq = _seq;
            }

            if (input is null) continue;
            var msg = Compute(input, seq);
            lock (_gate)
            {
                if (seq != _seq) continue; // строка уже другая — посчитаем свежую
                Send(msg);
            }
        }
    }

    private PsMsg Compute(string input, int seq)
    {
        var hide = new PsMsg { T = "hide", Id = seq };
        // Список — пока набирается слово (как в VS Code): после пробела, «(», «|», «=», кавычки — нет.
        var last = input[^1];
        if (!(char.IsLetterOrDigit(last) || last is '-' or '_' or '$' or '.' or ':' or '\\' or '/' or '[')) return hide;
        try
        {
            SyncLocation(_rs!);
            using var ps = PowerShell.Create();
            ps.Runspace = _rs;
            var r = CommandCompletion.CompleteInput(input, input.Length, null, ps);
            if (r.CompletionMatches.Count == 0) return hide;
            var typed = input.Substring(r.ReplacementIndex, Math.Min(r.ReplacementLength, input.Length - r.ReplacementIndex));
            if (r.CompletionMatches.Count == 1 && string.Equals(r.CompletionMatches[0].CompletionText, typed, StringComparison.OrdinalIgnoreCase))
                return hide;
            return new PsMsg
            {
                T = "suggest",
                Id = seq,
                Text = input,
                Start = r.ReplacementIndex,
                Length = r.ReplacementLength,
                Completions = r.CompletionMatches.Take(200).Select(c => new PsCompletion
                {
                    Text = c.CompletionText,
                    List = c.ListItemText,
                    Type = c.ResultType.ToString(),
                    Tip = c.ToolTip == c.ListItemText ? null : c.ToolTip
                }).ToList()
            };
        }
        catch
        {
            return hide;
        }
    }

    private void Send(PsMsg msg)
    {
        try { _pipe?.WriteLine(msg.ToLine()); }
        catch { _pipe = null; } // LinuxOps закрыла канал
    }

    public SuggestionPackage GetSuggestion(PredictionClient client, PredictionContext context, CancellationToken cancellationToken)
    {
        if (_pipe is null) return default;
        var input = context.InputAst.Extent.Text;
        lock (_gate)
        {
            _seq++;
            if (string.IsNullOrWhiteSpace(input))
            {
                _pending = null;
                Send(new PsMsg { T = "hide", Id = _seq });
                return default;
            }

            _pending = input;
        }

        _wake.Set();
        return default;
    }

    /// <summary>Текущая папка консоли: её записывает функция prompt (см. ConsoleMode) — пути подсказываются от неё.</summary>
    public static string? Location { get; set; }

    private string? _applied;

    private void SyncLocation(Runspace rs)
    {
        var loc = Location;
        if (loc is null || loc == _applied) return;
        _applied = loc;
        try
        {
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            ps.AddCommand("Set-Location").AddParameter("LiteralPath", loc).Invoke();
        }
        catch { /* папки нет — не страшно */ }
    }

    /// <summary>Строка ушла на выполнение — список закрыть.</summary>
    public void OnCommandLineAccepted(PredictionClient client, IReadOnlyList<string> history)
    {
        lock (_gate)
        {
            _seq++;
            _pending = null;
            Send(new PsMsg { T = "hide", Id = _seq });
        }
    }

    public bool CanAcceptFeedback(PredictionClient client, PredictorFeedbackKind feedback) => feedback == PredictorFeedbackKind.CommandLineAccepted;
    public void OnCommandLineExecuted(PredictionClient client, string commandLine, bool success) { }
    public void OnSuggestionDisplayed(PredictionClient client, uint session, int countOrIndex) { }
    public void OnSuggestionAccepted(PredictionClient client, uint session, string acceptedSuggestion) { }
}
