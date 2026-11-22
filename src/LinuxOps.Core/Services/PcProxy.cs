using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace LinuxOps.Services;

/// <summary>
/// Мини-прокси на 127.0.0.1 этого ПК — для проверки обновлений через туннель SSH (галка у «Версий ПО», по умолчанию
/// выключена). Живёт только на время проверки. Пускает только на порты 80 и 443 (CONNECT и обычный HTTP).
/// Дальше — как браузер: системный прокси Windows / PAC; на запрос входа прокси — Negotiate / NTLM текущей учёткой
/// Windows. Прокси с логином и паролем (Basic) не поддерживаются — пароли программа не хранит и не спрашивает.
/// </summary>
public sealed class PcProxy : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Socket, byte> _open = new();
    private readonly ConcurrentDictionary<string, int> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWebProxy? _upstreamOverride;
    private readonly PcProxy? _inner;
    private int _connections;
    private int _failures;
    private string _lastError = "";

    public int Port { get; }

    private PcProxy(IWebProxy? upstreamOverride, PcProxy? inner)
    {
        _upstreamOverride = upstreamOverride;
        _inner = inner;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public static PcProxy Start()
    {
#if DEBUG
        // Проверка цепочки «прокси → прокси» без корпоративного прокси: второй такой же прокси как вышестоящий.
        // Адрес — вышестоящий прокси вместо системного (например, недоступный — проверка отката на кэш).
        var test = Environment.GetEnvironmentVariable("LINUXOPS_UPSTREAM_PROXY");
        if (test is "chain")
        {
            var inner = new PcProxy(null, null);
            return new PcProxy(new WebProxy($"http://127.0.0.1:{inner.Port}"), inner);
        }
        if (!string.IsNullOrWhiteSpace(test)) return new PcProxy(new WebProxy(test), null);
#endif
        return new PcProxy(null, null);
    }

    /// <summary>Сводка для отчёта: сколько соединений и куда.</summary>
    public string Log
    {
        get
        {
            var targets = _targets.OrderByDescending(t => t.Value).Select(t => t.Value > 1 ? $"{t.Key} ×{t.Value}" : t.Key).ToList();
            var sb = new StringBuilder($"Соединений через {RunPlace.Here}: {_connections}");
            if (_failures > 0) sb.Append($", неудачных {_failures} (последняя ошибка: {_lastError})");
            if (targets.Count > 0) sb.Append($". Куда: {string.Join(", ", targets.Take(20))}{(targets.Count > 20 ? $" и ещё {targets.Count - 20}" : "")}");
            return sb.Append('.').ToString();
        }
    }

    public int Connections => _connections;

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptSocketAsync(_cts.Token); }
            catch { break; }
            _open[s] = 0;
            _ = Task.Run(async () =>
            {
                try { await Handle(s); }
                catch (Exception ex) { Fail(ex.Message); }
                finally { Close(s); }
            });
        }
    }

    private void Fail(string message)
    {
        if (_cts.IsCancellationRequested) return;
        Interlocked.Increment(ref _failures);
        _lastError = message;
    }

    private void Close(Socket s)
    {
        _open.TryRemove(s, out _);
        try { s.Shutdown(SocketShutdown.Both); } catch { }
        s.Dispose();
    }

    private async Task Handle(Socket clientSocket)
    {
        var ct = _cts.Token;
        await using var client = new NetworkStream(clientSocket, ownsSocket: false);
        var (head, extra) = await ReadHead(client, ct);
        if (head is null) return;
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) { await Reply(client, "400 Bad Request", ct); return; }

        var connect = first[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase);
        string host;
        int port;
        string path = "";
        if (connect)
        {
            if (!TrySplit(first[1], out host, out port)) { await Reply(client, "400 Bad Request", ct); return; }
        }
        else
        {
            if (!Uri.TryCreate(first[1], UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp)
            {
                await Reply(client, "400 Bad Request", ct);
                return;
            }
            host = u.IdnHost;
            port = u.Port;
            path = u.PathAndQuery;
        }

        if (port is not (80 or 443))
        {
            await Reply(client, "403 Forbidden", ct);
            return;
        }

        Interlocked.Increment(ref _connections);
        _targets.AddOrUpdate($"{host}:{port}", 1, (_, n) => n + 1);

        var dest = new Uri($"{(port == 443 ? "https" : "http")}://{host}:{port}/");
        var proxy = _upstreamOverride ?? HttpClient.DefaultProxy;
        Uri? up = null;
        try
        {
            if (!proxy.IsBypassed(dest)) up = proxy.GetProxy(dest);
        }
        catch { up = null; }
        if (up is not null && (up == dest || string.Equals(up.Host, host, StringComparison.OrdinalIgnoreCase) && up.Port == port)) up = null;

        // Запрос к цели: для туннеля — CONNECT, для HTTP — сам запрос, «Connection: close» (один запрос на соединение).
        var headers = lines.Skip(1).Where(l => l.Length > 0 && !IsHopHeader(l)).ToList();
        string Request(bool viaProxy, string? auth)
        {
            var sb = new StringBuilder();
            if (connect)
            {
                sb.Append($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
            }
            else
            {
                sb.Append($"{first[0]} {(viaProxy ? first[1] : path)} HTTP/1.1\r\n");
                foreach (var h in headers) sb.Append(h).Append("\r\n");
                sb.Append("Connection: close\r\n");
            }
            if (viaProxy) sb.Append("Proxy-Connection: keep-alive\r\n");
            if (auth is not null) sb.Append("Proxy-Authorization: ").Append(auth).Append("\r\n");
            return sb.Append("\r\n").ToString();
        }

        try
        {
            await Forward(client, connect, host, port, extra, up, Request, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex.Message);
            try { await Reply(client, "502 Bad Gateway", ct); } catch { }
        }
    }

    private async Task Forward(NetworkStream client, bool connect, string host, int port, byte[] extra, Uri? up,
        Func<bool, string?, string> Request, CancellationToken ct)
    {
        if (up is null)
        {
            using var server = await Dial(host, port, ct);
            await using var ss = new NetworkStream(server, ownsSocket: false);
            if (connect) await Write(client, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
            else await Write(ss, Request(false, null), ct);
            if (extra.Length > 0) await ss.WriteAsync(extra, ct);
            await Pipe(client, ss, ct);
            return;
        }

        var (upSocket, upStream, respHead, rest) = await ViaProxy(up, Request, ct);
        using (upSocket)
        await using (upStream)
        {
            var status = StatusOf(respHead);
            if (connect && status == 200)
            {
                await Write(client, "HTTP/1.1 200 Connection established\r\n\r\n", ct);
                if (rest.Length > 0) await client.WriteAsync(rest, ct);
            }
            else
            {
                if (status is 403 or 407 or 502 or 503 or 504 || connect) Fail($"прокси {up.Host}: {respHead.Split("\r\n")[0]}");
                await Write(client, respHead + "\r\n\r\n", ct);
                if (rest.Length > 0) await client.WriteAsync(rest, ct);
                if (connect) return;
            }
            if (extra.Length > 0) await upStream.WriteAsync(extra, ct);
            await Pipe(client, upStream, ct);
        }
    }

    /// <summary>Запрос через вышестоящий прокси; на 407 — Negotiate / NTLM текущей учёткой Windows (до 3 шагов).</summary>
    private async Task<(Socket, NetworkStream, string, byte[])> ViaProxy(Uri up, Func<bool, string?, string> request, CancellationToken ct)
    {
        var socket = await Dial(up.Host, up.Port, ct);
        var stream = new NetworkStream(socket, ownsSocket: false);
        NegotiateAuthentication? auth = null;
        string? package = null;
        string? header = null;
        try
        {
            for (var step = 0; ; step++)
            {
                await Write(stream, request(true, header), ct);
                var (head, rest) = await ReadHead(stream, ct);
                if (head is null) throw new IOException($"прокси {up.Host} закрыл соединение");
                if (StatusOf(head) != 407 || step >= 3) return (socket, stream, head, rest);

                var challenges = HeaderValues(head, "Proxy-Authenticate");
                if (package is null)
                {
                    package = challenges.Any(c => c.StartsWith("Negotiate", StringComparison.OrdinalIgnoreCase)) ? "Negotiate"
                        : challenges.Any(c => c.StartsWith("NTLM", StringComparison.OrdinalIgnoreCase)) ? "NTLM" : null;
                    if (package is null) return (socket, stream, head, rest); // Basic и прочее — не поддерживается
                    auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
                    {
                        Package = package,
                        Credential = CredentialCache.DefaultNetworkCredentials,
                        TargetName = "HTTP/" + up.Host
                    });
                }

                var token = challenges.Select(c => c.Split(' ', 2)).FirstOrDefault(p => p[0].Equals(package, StringComparison.OrdinalIgnoreCase) && p.Length > 1)?[1].Trim();
                byte[] incoming = [];
                if (token is not null)
                {
                    try { incoming = Convert.FromBase64String(token); } catch { }
                }
                else if (step > 0)
                {
                    return (socket, stream, head, rest); // учётку не приняли
                }
                var blob = auth!.GetOutgoingBlob(incoming, out var code);
                if (blob is null || code is not (NegotiateAuthenticationStatusCode.Completed or NegotiateAuthenticationStatusCode.ContinueNeeded))
                    return (socket, stream, head, rest);
                header = $"{package} {Convert.ToBase64String(blob)}";

                // Тело ответа 407 дочитать, чтобы продолжить на том же соединении (NTLM требует того же соединения).
                if (!await Drain(stream, head, rest, ct))
                {
                    await stream.DisposeAsync();
                    socket.Dispose();
                    socket = await Dial(up.Host, up.Port, ct);
                    stream = new NetworkStream(socket, ownsSocket: false);
                }
            }
        }
        catch
        {
            await stream.DisposeAsync();
            socket.Dispose();
            throw;
        }
        finally
        {
            auth?.Dispose();
        }
    }

    private static async Task<bool> Drain(Stream s, string head, byte[] rest, CancellationToken ct)
    {
        if (HeaderValues(head, "Connection").Any(v => v.Equals("close", StringComparison.OrdinalIgnoreCase)) ||
            HeaderValues(head, "Proxy-Connection").Any(v => v.Equals("close", StringComparison.OrdinalIgnoreCase)))
            return false;
        var cl = HeaderValues(head, "Content-Length").FirstOrDefault();
        if (cl is null || !long.TryParse(cl, out var left)) return false;
        left -= rest.Length;
        var buf = new byte[8192];
        while (left > 0)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct);
            if (n <= 0) return false;
            left -= n;
        }
        return true;
    }

    private async Task<Socket> Dial(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout);
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(host, port, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            s.Dispose();
            throw new IOException($"{host}:{port} — нет ответа за {ConnectTimeout.TotalSeconds:0} с");
        }
        catch
        {
            s.Dispose();
            throw;
        }
        _open[s] = 0;
        return s;
    }

    private static async Task Pipe(Stream a, Stream b, CancellationToken ct)
    {
        var t1 = a.CopyToAsync(b, ct);
        var t2 = b.CopyToAsync(a, ct);
        try { await Task.WhenAny(t1, t2); } catch { }
    }

    private static Task Write(Stream s, string text, CancellationToken ct) => s.WriteAsync(Encoding.Latin1.GetBytes(text), ct).AsTask();

    private static Task Reply(Stream s, string status, CancellationToken ct) =>
        Write(s, $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);

    /// <summary>Заголовок HTTP до пустой строки (не больше 64 КБ) и уже прочитанные за ним байты.</summary>
    private static async Task<(string? Head, byte[] Tail)> ReadHead(Stream s, CancellationToken ct)
    {
        var buf = new byte[65536];
        var len = 0;
        while (len < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n <= 0) return (null, []);
            len += n;
            var end = IndexOfEnd(buf, len);
            if (end >= 0)
                return (Encoding.Latin1.GetString(buf, 0, end), buf.AsSpan(end + 4, len - end - 4).ToArray());
        }
        return (null, []);
    }

    private static int IndexOfEnd(byte[] b, int len)
    {
        for (var i = 0; i + 3 < len; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private static int StatusOf(string head)
    {
        var p = head.Split("\r\n")[0].Split(' ');
        return p.Length > 1 && int.TryParse(p[1], out var code) ? code : 0;
    }

    private static List<string> HeaderValues(string head, string name) =>
        head.Split("\r\n").Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .ToList();

    private static bool IsHopHeader(string line)
    {
        var name = line.Split(':', 2)[0].Trim();
        return name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySplit(string target, out string host, out int port)
    {
        host = "";
        port = 0;
        var i = target.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(target[(i + 1)..], out port)) return false;
        host = target[..i].Trim('[', ']');
        return host.Length > 0;
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        foreach (var s in _open.Keys) Close(s);
        _inner?.Dispose();
    }
}
