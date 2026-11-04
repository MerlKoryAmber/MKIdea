using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxOps.Models;
using LinuxOps.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxOps.ViewModels;

public partial class SshTerminalViewModel : ObservableObject, IDisposable
{
    private readonly object _gate = new();
    private readonly TerminalScreen _screen = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _typed = new();
    private readonly object _write = new();
    private bool _typedKnown = true;
    private SshClient? _client;
    private ShellStream? _shell;
    private int _paint;
    private int _ask;
    private bool _promptShown;
    private string? _secret;
    private string _pending = "";

    public SshTerminalViewModel(AppSession session) => Hosts = session.Hosts;

    public ObservableCollection<HostConfig> Hosts { get; }

    public Func<Window?>? Owner { get; set; }

    public event Action? OutputChanged;

    [ObservableProperty] private HostConfig? selectedHost;
    [ObservableProperty] private string output = "";
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string status = "Не подключено";

    public void UseHost(HostConfig host) => SelectedHost = host;

    partial void OnSelectedHostChanged(HostConfig? value)
    {
        if (Connected)
            Disconnect();
    }

    public async Task Connect()
    {
        if (SelectedHost is not HostConfig host)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост.", "SSH Terminal");
            return;
        }

        Disconnect();
        Status = "подключение…";
        try
        {
            var (client, shell) = await Task.Run(() => Open(host));
            _client = client;
            _shell = shell;
            shell.DataReceived += OnData;
            shell.ErrorOccurred += OnError;
            Connected = true;
            Status = $"{host.Username}@{host.Host}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            AppDialog.Info(OwnerWin(), ex.Message, "SSH Terminal");
            Disconnect();
        }
    }

    private static (SshClient Client, ShellStream Shell) Open(HostConfig host)
    {
        var client = new SshClient(SshAuth.Build(host));
        SshAuth.AttachHostKey(client, host);
        client.Connect();
        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.VERASE] = 127 };
        var shell = client.CreateShellStream("xterm", 120, 40, 0, 0, 4096, modes);
        return (client, shell);
    }

    public void Disconnect()
    {
        var shell = _shell;
        var client = _client;
        _shell = null;
        _client = null;
        Connected = false;
        if (shell is not null)
        {
            shell.DataReceived -= OnData;
            shell.ErrorOccurred -= OnError;
            try { shell.Dispose(); } catch { /* канал уже закрыт */ }
        }

        if (client is not null)
        {
            try
            {
                if (client.IsConnected) client.Disconnect();
                client.Dispose();
            }
            catch { /* уже отключён */ }
        }

        if (Status.StartsWith("подключение", StringComparison.Ordinal))
            Status = "Не подключено";
        else if (shell is not null)
            Status = "Отключено";
    }

    public void Clear()
    {
        lock (_gate)
        {
            _screen.Clear();
            _pending = "";
        }

        Output = "";
        OutputChanged?.Invoke();
    }

    /// <summary>Символы уходят в PTY. Оболочка сама рисует их у приглашения.</summary>
    public void Type(string data)
    {
        if (data.Length == 0) return;
        if (data is "\r" or "\n")
        {
            if (_typedKnown && !_promptShown) Remember(_typed.ToString());
            _typed.Clear();
            _typedKnown = true;
            Send("\r");
            return;
        }

        if (data is "\u007f" or "\b")
        {
            if (_typedKnown && _typed.Length > 0) _typed.Length--;
            Send(data);
            return;
        }

        if (data[0] == '\u001b' || data.Any(char.IsControl))
        {
            _typed.Clear();
            _typedKnown = false;
            Send(data);
            return;
        }

        if (_typedKnown) _typed.Append(data);
        Send(data);
    }

    public void Dispose() => Disconnect();

    private void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (SelectedHost is not HostConfig host) return;
        if (_secret is not null && line.Contains(_secret, StringComparison.Ordinal)) return;
        HistoryStore.Append(new[]
        {
            new HistoryEntry
            {
                Mode = "terminal",
                Host = host.Display,
                Action = "ssh",
                Status = "OK",
                Preview = line.Length > 180 ? line[..180] : line
            }
        });
    }

    private void Send(string data)
    {
        var shell = _shell;
        if (shell is null || !Connected)
        {
            Status = "Нет подключения";
            return;
        }

        lock (_write)
        {
            try { shell.Write(data); }
            catch (Exception ex) { Status = ex.Message; }
        }
    }

    private void OnError(object? sender, ExceptionEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => Status = e.Exception.Message);
    }

    private void OnData(object? sender, ShellDataEventArgs e)
    {
        var bytes = e.Data ?? Array.Empty<byte>();
        if (bytes.Length == 0 && e.Line is not null)
            bytes = Encoding.UTF8.GetBytes(e.Line);
        if (bytes.Length == 0) return;

        string snap;
        bool ask;
        lock (_gate)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var n = _utf8.GetChars(bytes, 0, bytes.Length, chars, 0);
            _screen.Feed(chars.AsSpan(0, n));
            if (_secret is not null)
                _screen.Redact(_secret);
            snap = _screen.Text;
            var tail = _screen.Tail();
            ask = IsPasswordPrompt(tail);
            if (!ask) _promptShown = false;
            else if (_promptShown) ask = false;
            else _promptShown = true;
            _pending = snap;
        }

        Publish(ask);
    }

    private void Publish(bool ask)
    {
        var app = Application.Current;
        if (app is null) return;
        if (Interlocked.CompareExchange(ref _paint, 1, 0) != 0) return;
        app.Dispatcher.BeginInvoke(() => Flush(ask));
    }

    private void Flush(bool ask)
    {
        string snap;
        lock (_gate) snap = _pending;
        Output = snap;
        OutputChanged?.Invoke();
        if (ask) AskPassword();
        var more = false;
        lock (_gate) more = _pending != snap;
        if (more)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => Flush(false));
            return;
        }

        Interlocked.Exchange(ref _paint, 0);
        lock (_gate) more = _pending != Output;
        if (more && Interlocked.CompareExchange(ref _paint, 1, 0) == 0)
            Application.Current?.Dispatcher.BeginInvoke(() => Flush(false));
    }

    private void AskPassword()
    {
        if (Interlocked.Exchange(ref _ask, 1) == 1) return;
        var host = SelectedHost;
        if (host is null || _shell is null)
        {
            Interlocked.Exchange(ref _ask, 0);
            return;
        }

        var password = SshRunner.AskSudo(host);
        if (string.IsNullOrEmpty(password))
        {
            Interlocked.Exchange(ref _ask, 0);
            return;
        }

        _secret = password;
        _typed.Clear();
        _typedKnown = true;
        try { Send(password + "\r"); }
        catch { /* канал закрыт */ }
        _ = Task.Delay(1200).ContinueWith(_ => Interlocked.Exchange(ref _ask, 0));
    }

    public static bool IsPasswordPrompt(string tail)
    {
        var s = tail.Replace("\r", "").TrimEnd();
        return Regex.IsMatch(s, @"(?i)(password|пароль)\s*(for\s+\S+)?\s*:\s*$");
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}

public sealed class TerminalScreen
{
    private readonly List<StringBuilder> _lines = new() { new() };
    private int _row;
    private int _col;
    private const int MaxRows = 400;

    public string Text => string.Join("\n", _lines.Select(l => l.ToString()));

    public string Tail(int n = 200)
    {
        var text = Text;
        return text.Length <= n ? text : text[^n..];
    }

    public void Clear()
    {
        _lines.Clear();
        _lines.Add(new StringBuilder());
        _row = 0;
        _col = 0;
    }

    public void Redact(string secret)
    {
        if (secret.Length == 0) return;
        foreach (var line in _lines)
        {
            var s = line.ToString();
            if (!s.Contains(secret, StringComparison.Ordinal)) continue;
            line.Clear();
            line.Append(s.Replace(secret, "", StringComparison.Ordinal));
        }
    }

    public void Feed(ReadOnlySpan<char> chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            var c = chunk[i];
            if (c == '\u001b')
            {
                i = ConsumeEscape(chunk, i);
                continue;
            }

            if (c == '\r') { _col = 0; continue; }
            if (c == '\n') { NewLine(); continue; }
            if (c == '\b') { if (_col > 0) _col--; continue; }
            if (c == '\u007f') { EraseLeft(); continue; }
            if (c is '\a' or '\0') continue;
            Put(c);
        }
    }

    private int ConsumeEscape(ReadOnlySpan<char> chunk, int i)
    {
        if (i + 1 >= chunk.Length) return i;
        var next = chunk[i + 1];
        if (next == '[')
            return ConsumeCsi(chunk, i + 2);
        if (next == ']')
            return ConsumeOsc(chunk, i + 2);
        if (next is '(' or ')' or '*' or '+' or '-' or '.')
            return Math.Min(i + 2, chunk.Length - 1);
        return i + 1;
    }

    private int ConsumeOsc(ReadOnlySpan<char> chunk, int i)
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
        var p = i;
        for (; p < chunk.Length; p++)
        {
            var c = chunk[p];
            if (c is >= '0' and <= '9')
            {
                num = (num < 0 ? 0 : num) * 10 + (c - '0');
                continue;
            }

            if (c == ';')
            {
                args.Add(num < 0 ? 0 : num);
                num = -1;
                continue;
            }

            if (c is >= '@' and <= '~')
            {
                args.Add(num < 0 ? 0 : num);
                ApplyCsi(c, args);
                return p;
            }
        }

        return chunk.Length - 1;
    }

    private void ApplyCsi(char cmd, List<int> args)
    {
        int N(int index, int fallback) => args.Count > index && args[index] > 0 ? args[index] : fallback;
        switch (cmd)
        {
            case 'A': _row = Math.Max(0, _row - N(0, 1)); break;
            case 'B': _row += N(0, 1); EnsureRow(); break;
            case 'C': _col += N(0, 1); break;
            case 'D': _col = Math.Max(0, _col - N(0, 1)); break;
            case 'H':
            case 'f':
                _row = Math.Max(0, N(0, 1) - 1);
                _col = Math.Max(0, (args.Count > 1 ? args[1] : 1) - 1);
                if (args.Count > 1 && args[1] == 0) _col = 0;
                EnsureRow();
                break;
            case 'J':
                if (args.Count == 0 || args[0] is 0)
                    ClearFromCursor();
                else if (args[0] == 2)
                    Clear();
                break;
            case 'K':
                EnsureRow();
                if (_col < _lines[_row].Length)
                    _lines[_row].Length = _col;
                break;
        }
    }

    private void ClearFromCursor()
    {
        EnsureRow();
        if (_col < _lines[_row].Length)
            _lines[_row].Length = _col;
        while (_lines.Count > _row + 1)
            _lines.RemoveAt(_lines.Count - 1);
    }

    private void Put(char c)
    {
        EnsureRow();
        var line = _lines[_row];
        if (_col < line.Length) line[_col] = c;
        else
        {
            while (line.Length < _col) line.Append(' ');
            line.Append(c);
        }

        _col++;
    }

    private void EraseLeft()
    {
        if (_col <= 0) return;
        _col--;
        EnsureRow();
        var line = _lines[_row];
        if (_col < line.Length) line.Remove(_col, 1);
    }

    private void NewLine()
    {
        _row++;
        _col = 0;
        EnsureRow();
        while (_lines.Count > MaxRows)
        {
            _lines.RemoveAt(0);
            _row--;
        }
    }

    private void EnsureRow()
    {
        while (_lines.Count <= _row)
            _lines.Add(new StringBuilder());
    }
}
