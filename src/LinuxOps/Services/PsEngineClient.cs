using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;

namespace LinuxOps.Services;

/// <summary>
/// Движок PowerShell — процесс pwsh\LinuxOps.PsHost.exe, связь по анонимным каналам (PsProtocol).
/// События приходят в UI-поток. Один запуск за раз; «Стоп» — мягко, «Перезапуск» — убить процесс.
/// </summary>
public sealed class PsEngineClient : IDisposable
{
    private readonly Dispatcher _ui;
    private Process? _proc;
    private StreamWriter? _writer;
    private int _nextId;
    private int _runId;

    public PsEngineClient(Dispatcher ui) => _ui = ui;

    public static string HostExe => Path.Combine(AppContext.BaseDirectory, "pwsh", "LinuxOps.PsHost.exe");

    /// <summary>Папка модулей для офлайн-установки (PowerCLI) — рядом с LinuxOps.exe.</summary>
    public static string UserModules => Path.Combine(AppContext.BaseDirectory, "Modules");

    public bool IsRunning => _proc is { HasExited: false };
    public bool Busy => _runId != 0;
    public string Location { get; private set; } = "";
    public string Version { get; private set; } = "";

    public event Action<string, string, string?, bool>? Output; // kind, text, color, newLine
    public event Action<PsMsg>? Prompt;
    public event Action<string>? Progress;
    public event Action<int, bool, string?>? Done; // id, ok, note
    public event Action? Ready;
    public event Action<string>? Exited;

    /// <summary>Запуск процесса; готовность — событием Ready.</summary>
    public void Start()
    {
        if (IsRunning) return;
        if (!File.Exists(HostExe))
            throw new FileNotFoundException("Движок PowerShell не найден: " + HostExe + ". Папка pwsh должна лежать рядом с LinuxOps.exe.");

        var toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var psi = new ProcessStartInfo(HostExe, $"{toHost.GetClientHandleAsString()} {fromHost.GetClientHandleAsString()}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(HostExe)!
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить движок PowerShell.");
        toHost.DisposeLocalCopyOfClientHandle();
        fromHost.DisposeLocalCopyOfClientHandle();
        _proc = proc;
        _writer = new StreamWriter(toHost, new UTF8Encoding(false)) { AutoFlush = true };
        _runId = 0;
        var reader = new StreamReader(fromHost, new UTF8Encoding(false));
        var thread = new Thread(() => ReadLoop(proc, reader)) { IsBackground = true, Name = "ps-client" };
        thread.Start();
    }

    private void ReadLoop(Process proc, StreamReader reader)
    {
        try
        {
            while (reader.ReadLine() is { } line)
            {
                PsMsg? m;
                try { m = PsMsg.Parse(line); }
                catch { continue; }
                if (m is not null) _ui.BeginInvoke(() => Handle(m));
            }
        }
        catch (IOException) { /* процесс убит */ }

        _ui.BeginInvoke(() =>
        {
            if (!ReferenceEquals(proc, _proc)) return; // уже перезапущен
            var id = _runId;
            _runId = 0;
            _proc = null;
            _writer = null;
            if (id != 0) Done?.Invoke(id, false, "движок завершился");
            Exited?.Invoke("движок PowerShell завершился");
        });
    }

    private void Handle(PsMsg m)
    {
        switch (m.T)
        {
            case "ready":
                Location = m.Location ?? "";
                Version = m.Text ?? "";
                Ready?.Invoke();
                break;
            case "out":
                Output?.Invoke(m.Kind ?? "out", m.Text ?? "", m.Color, !m.NoNewLine);
                break;
            case "prompt":
                Prompt?.Invoke(m);
                break;
            case "progress":
                Progress?.Invoke(m.Text ?? "");
                break;
            case "done":
                if (m.Location is { Length: > 0 }) Location = m.Location;
                if (m.Id == _runId) _runId = 0;
                Done?.Invoke(m.Id, m.Ok, m.Text);
                break;
        }
    }

    private void Send(PsMsg m)
    {
        try { _writer?.WriteLine(m.ToLine()); }
        catch (IOException) { /* процесс завершился — придёт Exited */ }
    }

    /// <summary>Выполнить скрипт в общем сеансе; 0 — движок занят или не запущен.</summary>
    public int Run(string script)
    {
        if (!IsRunning || Busy) return 0;
        _runId = ++_nextId;
        Send(new PsMsg { T = "run", Id = _runId, Text = script });
        return _runId;
    }

    public void Stop() => Send(new PsMsg { T = "stop" });

    public void Reply(int promptId, IReadOnlyList<string>? values) =>
        Send(new PsMsg { T = "reply", Id = promptId, Ok = values is not null, Values = values?.ToList() });

    public void SetWidth(int columns)
    {
        if (columns >= 40) Send(new PsMsg { T = "width", Width = columns });
    }

    /// <summary>Убить процесс (зависший вызов, сброс сеанса). Запускать заново — Start().</summary>
    public void Kill()
    {
        var p = _proc;
        _proc = null;
        _writer = null;
        _runId = 0;
        try { p?.Kill(entireProcessTree: true); }
        catch { /* уже завершён */ }
    }

    public void Dispose()
    {
        Send(new PsMsg { T = "exit" });
        var p = _proc;
        _proc = null;
        if (p is null) return;
        if (!p.WaitForExit(1500))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* уже завершён */ }
        }
    }
}
