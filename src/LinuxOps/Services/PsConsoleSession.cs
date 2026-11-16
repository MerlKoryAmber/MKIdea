using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;

namespace LinuxOps.Services;

/// <summary>
/// Консоль вкладки PowerShell: LinuxOps.PsHost --console (настоящая консоль PowerShell с PSReadLine) в ConPTY,
/// вывод — в эмулятор xterm (тот же, что у SSH Terminal). Как терминал VS Code.
/// </summary>
public sealed class PsConsoleSession : IDisposable
{
    private readonly object _gate = new();
    private readonly TerminalScreen _screen = new();
    private Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private ConPty? _pty;
    private NamedPipeServerStream? _suggest;
    private int _cols = 120;
    private int _rows = 30;
    private long _shown = -1;
    private int _paint;

    public TerminalFrame Frame { get; private set; } = TerminalFrame.Empty;
    public bool Running => _pty is not null;
    public bool AppCursorKeys { get { lock (_gate) return _screen.AppCursorKeys; } }
    public bool Win32Input { get { lock (_gate) return _screen.Win32Input; } }
    public (int Mode, bool Sgr) Mouse { get { lock (_gate) return (_screen.MouseMode, _screen.MouseSgr); } }
    public string Status { get; private set; } = "консоль не запущена";

    /// <summary>Есть что перерисовать (UI-поток).</summary>
    public event Action? OutputChanged;

    /// <summary>Запуск, завершение, ошибка (UI-поток).</summary>
    public event Action? StatusChanged;

    /// <summary>Подсказки для набранной строки (suggest) или «закрыть список» (hide) — UI-поток.</summary>
    public event Action<PsMsg>? Suggestions;

    public void Start()
    {
        if (_pty is not null) return;
        var exe = PsEngineClient.HostExe;
        if (!File.Exists(exe))
        {
            SetStatus("Движок PowerShell не найден: " + exe + ". Папка pwsh должна лежать рядом с LinuxOps.exe.");
            return;
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Канал подсказок: предиктор консоли шлёт дополнения набранной строки, список рисуем сами (как VS Code).
            var pipeName = "LinuxOps-ps-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var pty = ConPty.Start($"\"{exe}\" --console --suggest {pipeName}", home, _cols, _rows);
            _pty = pty;
            _suggest = server;
            _ = ReadSuggestions(server, pty);
            lock (_gate) _utf8 = new UTF8Encoding(false).GetDecoder();
            var thread = new Thread(() => ReadLoop(pty)) { IsBackground = true, Name = "ps-console" };
            thread.Start();
            // Канал вывода ConPTY не закрывается сам, когда процесс вышел (exit) — следим за процессом.
            var watch = new Thread(() =>
            {
                try { pty.Process.WaitForExit(); }
                catch { /* уже нет */ }
                Thread.Sleep(300); // дочитать последний вывод
                Ended(pty);
            }) { IsBackground = true, Name = "ps-console-exit" };
            watch.Start();
            SetStatus("PowerShell · консоль");
        }
        catch (Exception ex)
        {
            _pty = null;
            try { _suggest?.Dispose(); } catch { }
            _suggest = null;
            SetStatus("Не удалось запустить консоль PowerShell: " + ex.Message);
        }
    }

    /// <summary>Убить консоль (вместе с запущенными в ней программами) и начать заново: переменные и модули сбрасываются.</summary>
    public void Restart()
    {
        var old = _pty;
        _pty = null;
        old?.Dispose();
        CloseSuggest();
        lock (_gate)
        {
            _screen.Clear();
            _shown = -1;
        }

        Flush();
        Start();
    }

    public void Type(string data)
    {
        if (data.Length == 0) return;
        if (_pty is null)
        {
            // Консоль завершилась (exit) — Enter запускает новую, как «подключиться снова» в SSH Terminal.
            if (data.Contains('\r')) Restart();
            return;
        }

        _pty.Write(data);
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Clamp(cols, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        if (cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        lock (_gate) _screen.Resize(cols, rows);
        _pty?.Resize(cols, rows);
        Flush();
    }

    private void ReadLoop(ConPty pty)
    {
        var buf = new byte[16384];
        var chars = new char[16384 + 4];
        try
        {
            while (true)
            {
                var n = pty.Output.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                lock (_gate)
                {
                    if (!ReferenceEquals(pty, _pty)) return; // уже перезапущена
                    var c = _utf8.GetChars(buf, 0, n, chars, 0);
                    _screen.Feed(chars.AsSpan(0, c));
                }

                Publish();
            }
        }
        catch (IOException) { /* консоль закрыта */ }
        catch (ObjectDisposedException) { }

        Ended(pty);
    }

    private async Task ReadSuggestions(NamedPipeServerStream server, ConPty pty)
    {
        try
        {
            await server.WaitForConnectionAsync();
            using var reader = new StreamReader(server, new UTF8Encoding(false));
            while (await reader.ReadLineAsync() is { } line)
            {
                PsMsg? msg;
                try { msg = PsMsg.Parse(line); }
                catch { continue; }
                if (msg is null) continue;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(pty, _pty)) Suggestions?.Invoke(msg);
                });
            }
        }
        catch { /* консоль закрыта */ }
    }

    private void CloseSuggest()
    {
        var s = _suggest;
        _suggest = null;
        try { s?.Dispose(); } catch { }
        Suggestions?.Invoke(new PsMsg { T = "hide" });
    }

    /// <summary>Консоль завершилась (exit, сбой, закрыт канал) — один раз на запуск.</summary>
    private void Ended(ConPty pty)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(pty, _pty)) return;
            _pty = null;
            pty.Dispose();
            CloseSuggest();
            lock (_gate) _screen.Feed("\u001b[0m\r\n[PowerShell завершился — Enter: запустить снова]\r\n".AsSpan());
            Flush();
            SetStatus("PowerShell завершился — Enter в консоли или «Перезапустить» запустят новый");
        });
    }

    private void Publish()
    {
        if (Interlocked.CompareExchange(ref _paint, 1, 0) != 0) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _paint, 0);
            Flush();
        });
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_screen.Version == _shown) return;
            Frame = _screen.Snapshot();
            _shown = Frame.Version;
        }

        OutputChanged?.Invoke();
    }

    private void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        var p = _pty;
        _pty = null;
        p?.Dispose();
        try { _suggest?.Dispose(); } catch { }
    }
}
