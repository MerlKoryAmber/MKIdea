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
    private static readonly Regex AnyPasswordPrompt = new(@"(?i)(password|passphrase|пароль)[^:\n]{0,40}:\s*$", RegexOptions.Compiled);
    private static readonly Regex SudoPrompt = new(@"^\[sudo\] (password for|пароль для) (?<user>[^:\s]+):\s*$", RegexOptions.Compiled);
    private static readonly Regex WrongSudo = new(@"(?i)sorry, try again|неверный пароль", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly TerminalScreen _screen = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _typed = new();
    private readonly object _write = new();
    private bool _typedKnown = true;
    private SshClient? _client;
    private ShellStream? _shell;
    private HostConfig? _shellHost;
    private int _paint;
    private int _ask;
    private bool _atPasswordPrompt;
    private bool _sudoPromptHandled;
    private bool _sentSudo;
    private long _shown = -1;
    private int _cols = 120;
    private int _rows = 40;
    private uint _pxWidth;
    private uint _pxHeight;

    public SshTerminalViewModel(AppSession session) => Hosts = session.Hosts;

    public ObservableCollection<HostConfig> Hosts { get; }

    public Func<Window?>? Owner { get; set; }

    public event Action? OutputChanged;

    [ObservableProperty] private HostConfig? selectedHost;
    [ObservableProperty] private bool connected;
    [ObservableProperty] private string status = "Не подключено";

    /// <summary>Последний снимок экрана для отрисовки.</summary>
    public TerminalFrame Frame { get; private set; } = TerminalFrame.Empty;
    public bool HasOutput => Frame.HasText;
    public bool AppCursorKeys { get { lock (_gate) return _screen.AppCursorKeys; } }
    public HostConfig? ConnectedHost => Connected ? _shellHost : null;

    /// <summary>Открыть терминал на хосте (F9 из SSH Files). cdPath — перейти в папку после входа.</summary>
    public async Task UseHost(HostConfig host, string? cdPath = null)
    {
        SelectedHost = host;
        if (!Connected || !ReferenceEquals(_shellHost, host))
            await Connect(host, confirmSwitch: false);
        if (Connected && !string.IsNullOrWhiteSpace(cdPath))
            Send("cd -- '" + cdPath.Replace("'", "'\\''") + "'\r");
    }

    /// <summary>Подключиться к хосту (двойной щелчок в списке).</summary>
    public async Task Connect(HostConfig? host = null, bool confirmSwitch = true)
    {
        host ??= SelectedHost;
        if (host is null)
        {
            AppDialog.Info(OwnerWin(), "Выберите хост в списке слева.", "SSH Terminal");
            return;
        }

        SelectedHost = host;
        if (Connected && ReferenceEquals(_shellHost, host)) return;
        if (Connected && confirmSwitch && _shellHost is not null &&
            !AppDialog.Confirm(OwnerWin(), $"Закрыть сессию {_shellHost.Title} и подключиться к {host.Title}?", "SSH Terminal"))
            return;

        Disconnect();
        Clear();
        Status = $"подключение к {host.Title}…";
        try
        {
            var cols = (uint)_cols;
            var rows = (uint)_rows;
            var (client, shell) = await Task.Run(() => Open(host, cols, rows));
            _client = client;
            _shell = shell;
            _shellHost = host;
            shell.DataReceived += OnData;
            shell.ErrorOccurred += OnError;
            shell.Closed += OnClosed;
            // Окно могло получить свой размер, пока шло подключение (новая вкладка) — сообщить PTY настоящий.
            if (cols != _cols || rows != _rows)
            {
                try { shell.ChangeWindowSize((uint)_cols, (uint)_rows, _pxWidth, _pxHeight); }
                catch { /* канал закрыт */ }
            }

            Connected = true;
            host.Status = "Online";
            Status = host.Display;
            OnPropertyChanged(nameof(ConnectedHost));
        }
        catch (Exception ex)
        {
            host.Status = "Offline";
            Status = "Ошибка: " + ex.Message;
            AppDialog.Info(OwnerWin(), ex.Message, "SSH Terminal");
            Disconnect();
        }
    }

    private static (SshClient Client, ShellStream Shell) Open(HostConfig host, uint cols, uint rows)
    {
        var client = new SshClient(SshAuth.Build(host));
        SshAuth.AttachHostKey(client, host);
        client.Connect();
        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.VERASE] = 127 };
        var shell = client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536, modes);
        return (client, shell);
    }

    public void Disconnect()
    {
        var shell = _shell;
        var client = _client;
        _shell = null;
        _client = null;
        var wasConnected = Connected;
        Connected = false;
        if (shell is not null)
        {
            shell.DataReceived -= OnData;
            shell.ErrorOccurred -= OnError;
            shell.Closed -= OnClosed;
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

        if (wasConnected) Status = "Отключено";
        OnPropertyChanged(nameof(ConnectedHost));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _screen.Clear();
            Frame = _screen.Snapshot();
            _shown = Frame.Version;
        }

        OutputChanged?.Invoke();
    }

    /// <summary>Новый размер окна терминала в символах и пикселях.</summary>
    public void Resize(int cols, int rows, double pxWidth, double pxHeight)
    {
        cols = Math.Clamp(cols, 20, 500);
        rows = Math.Clamp(rows, 5, 200);
        if (cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        _pxWidth = (uint)Math.Max(0, pxWidth);
        _pxHeight = (uint)Math.Max(0, pxHeight);
        lock (_gate) _screen.Resize(cols, rows);
        Flush();
        var shell = _shell;
        if (shell is null) return;
        try { shell.ChangeWindowSize((uint)cols, (uint)rows, _pxWidth, _pxHeight); }
        catch { /* канал закрыт */ }
    }

    /// <summary>Символы уходят в PTY. Оболочка сама рисует их у приглашения.</summary>
    public void Type(string data)
    {
        if (data.Length == 0) return;
        if (data is "\r" or "\n")
        {
            if (_typedKnown && !_atPasswordPrompt) Remember(_typed.ToString());
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
        if (_shellHost is not HostConfig host) return;
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
            Status = "Нет подключения. Дважды щёлкните хост слева.";
            return;
        }

        lock (_write)
        {
            try
            {
                shell.Write(data);
                shell.Flush();
            }
            catch (Exception ex) { Status = ex.Message; }
        }
    }

    private void OnError(object? sender, ExceptionEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() => Status = "Ошибка: " + e.Exception.Message);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!Connected) return;
            lock (_gate)
            {
                _screen.Feed("\u001b[0m\r\n[сессия закрыта]\r\n".AsSpan());
            }

            Flush();
            Disconnect();
            Status = "Сессия закрыта. Дважды щёлкните хост, чтобы подключиться снова.";
        });
    }

    private void OnData(object? sender, ShellDataEventArgs e)
    {
        var bytes = e.Data ?? Array.Empty<byte>();
        if (bytes.Length == 0 && e.Line is not null)
            bytes = Encoding.UTF8.GetBytes(e.Line);
        if (bytes.Length == 0) return;

        var ask = false;
        var wrong = false;
        lock (_gate)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var n = _utf8.GetChars(bytes, 0, bytes.Length, chars, 0);
            _screen.Feed(chars.AsSpan(0, n));
            var line = _screen.CurrentLine;
            _atPasswordPrompt = AnyPasswordPrompt.IsMatch(line);
            if (_sentSudo && WrongSudo.IsMatch(new string(chars, 0, n)))
                wrong = true;
            var sudo = SudoPrompt.Match(line);
            if (sudo.Success && _shellHost is not null &&
                string.Equals(sudo.Groups["user"].Value, _shellHost.Username, StringComparison.Ordinal))
            {
                if (!_sudoPromptHandled)
                {
                    _sudoPromptHandled = true;
                    ask = true;
                }
            }
            else if (!_atPasswordPrompt)
                _sudoPromptHandled = false;
        }

        if (wrong && _shellHost is not null)
        {
            SshRunner.ForgetSudo(_shellHost);
            _sentSudo = false;
        }

        Publish(ask);
    }

    private void Publish(bool ask)
    {
        var app = Application.Current;
        if (app is null) return;
        if (ask)
        {
            app.Dispatcher.BeginInvoke(() =>
            {
                Flush();
                AskPassword();
            });
            return;
        }

        if (Interlocked.CompareExchange(ref _paint, 1, 0) != 0) return;
        app.Dispatcher.BeginInvoke(() =>
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

    /// <summary>
    /// Только на «[sudo] password for &lt;логин хоста&gt;:». Сохранённый пароль уходит лишь после подтверждения.
    /// На запросы ssh, passwd, mysql и т.п. ничего не отправляется.
    /// </summary>
    private void AskPassword()
    {
        if (Interlocked.Exchange(ref _ask, 1) == 1) return;
        try
        {
            var host = _shellHost;
            if (host is null || _shell is null) return;
            string? password;
            var cached = SshRunner.CachedSudo(host);
            if (!string.IsNullOrEmpty(cached))
            {
                if (!AppDialog.Confirm(OwnerWin(),
                        $"{host.Title} просит пароль sudo для {host.Username}.\nОтправить пароль, введённый ранее в этой сессии?\n\nНет — введите пароль в терминале сами.",
                        "Пароль sudo"))
                    return;
                password = cached;
            }
            else
            {
                password = SshRunner.AskSudo(host);
            }

            if (string.IsNullOrEmpty(password)) return;
            _sentSudo = true;
            Send(password + "\r");
        }
        finally
        {
            Interlocked.Exchange(ref _ask, 0);
        }
    }

    private Window OwnerWin() => Owner?.Invoke() ?? Application.Current.MainWindow!;
}
