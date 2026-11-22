using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace LinuxOps.Web.Services.Guac;

/// <summary>
/// WebSocket-туннель браузер ↔ guacd (как WebSocketTunnel из guacamole-client).
/// Браузер приходит с одноразовым пропуском (GuacTickets); сервис сам делает рукопожатие с guacd (select → args → connect),
/// подставляя параметры из пропуска, и дальше пересылает инструкции в обе стороны. Сеанс — в журнал сеансов и аудит.
/// </summary>
public static partial class GuacTunnel
{
    /// <summary>Версия протокола клиента в браузере (guacamole-common-js 1.5).</summary>
    private const string ClientVersion = "VERSION_1_5_0";

    public static void MapGuac(this WebApplication app)
    {
        app.Map("/guac/tunnel", Handle).RequireAuthorization();
        app.MapGet("/guac/recording/{id}", Recording).RequireAuthorization();
    }

    /// <summary>Текст отказа по пределу сеансов — один и тот же в окне и в туннеле.</summary>
    public static string LimitText(int limit) => $"Открыто сеансов: {limit} — это предел. Закройте ненужный и подключитесь снова.";

    private static async Task Handle(HttpContext ctx, GuacTickets tickets, WebStore store, AuditLog audit, GuacLive live)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        var q = ctx.Request.Query;
        var ticket = tickets.Take(q["t"].ToString(), ctx.User.Identity?.Name ?? "");
        if (ticket is null) { ctx.Response.StatusCode = 403; return; }
        // просмотр вживую: подключение к идущему сеансу только смотреть (пропуск выдаёт WatchSession после проверки права)
        LiveSession? target = null;
        if (ticket.WatchId is { } wid && (target = live.Find(wid)) is null) { ctx.Response.StatusCode = 404; return; }
        var watching = target is not null;
        LiveSession? mine = null;
        int Num(string k, int def, int min, int max) => int.TryParse(q[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
        var w = Num("w", 1024, 200, 8192);
        var h = Num("h", 768, 150, 8192);
        var dpi = Num("dpi", 96, 48, 400);
        var tz = q["tz"].ToString();

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync("guacamole");
        var sessionId = ticket.Id;
        var started = DateTime.UtcNow;
        var status = "закрыт";
        var counted = false;   // зритель учтён в 👁 (WatcherIn) — снять в finally
        // предел одновременных сеансов на человека (⚙ → «Сеансы и записи»): место занимается до подключения к guacd
        if (!watching)
        {
            var limit = GuacLive.Limit(store);
            if (!live.TryReserve(ticket.Login, limit))
            {
                audit.Write(ticket.Who, "сеанс: отказ — предел", $"{ticket.HostTitle}: уже {limit} одновременно");
                try
                {
                    await WsSend(ws, GuacProtocol.Encode("", sessionId), ctx.RequestAborted);
                    await WsSend(ws, GuacProtocol.Encode("error", LimitText(limit), "519"), ctx.RequestAborted);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch { }
                return;
            }
        }
        TcpClient? tcp = null;
        try
        {
            tcp = new TcpClient { NoDelay = true };
            var (gh, gp) = SplitHostPort(WebPaths.Guacd);
            await tcp.ConnectAsync(gh, gp, ctx.RequestAborted);
            var stream = tcp.GetStream();
            var reader = new GuacReader(stream);

            async Task Send(string s) { var b = Encoding.UTF8.GetBytes(s); await stream.WriteAsync(b, ctx.RequestAborted); }

            await Send(GuacProtocol.Encode("select", target?.ConnectionId ?? ticket.Protocol));
            var args = await reader.ReadInstructionAsync(ctx.RequestAborted);
            var argv = args is null ? null : GuacProtocol.Parse(args);
            if (argv is null || argv[0] != "args") throw new IOException("guacd: нет ответа args (" + (argv?[0] ?? "соединение закрыто") + ")");

            await Send(GuacProtocol.Encode("size", w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture), dpi.ToString(CultureInfo.InvariantCulture)));
            await Send(GuacProtocol.Encode("audio"));
            await Send(GuacProtocol.Encode("video"));
            await Send(GuacProtocol.Encode("image", "image/png", "image/jpeg", "image/webp"));
            if (tz.Length is > 0 and < 64) await Send(GuacProtocol.Encode("timezone", tz));
            var values = argv.Skip(1).Select(name =>
                name.StartsWith("VERSION_", StringComparison.Ordinal) ? ClientVersion
                : watching ? (name == "read-only" ? "true" : "")   // зритель: только смотреть, никаких параметров сеанса
                : ticket.Params.GetValueOrDefault(name, "")).ToArray();
            await Send(GuacProtocol.Encode("connect", values));

            var ready = await reader.ReadInstructionAsync(ctx.RequestAborted);
            var rv = ready is null ? null : GuacProtocol.Parse(ready);
            if (rv is null || rv[0] != "ready")
            {
                // guacd отказал сразу (например, ошибка параметров) — отдать ошибку браузеру как есть
                if (ready is not null) await WsSend(ws, ready, ctx.RequestAborted);
                throw new IOException("guacd: " + (rv is null ? "соединение закрыто" : string.Join(" ", rv)));
            }

            if (target is not null)
            {
                target.WatcherIn();
                counted = true;
                // владелец мог уйти, пока шло рукопожатие: тогда зритель держал бы соединение guacd один
                if (target.Ended.IsCancellationRequested || live.Find(target.Id) is null) throw new IOException("сеанс уже закончился");
                audit.Write(ticket.Who, "сеанс: просмотр вживую", $"{target.HostTitle}, {target.Who}, сеанс {target.Id}");
            }
            else
            {
                store.SessionStart(sessionId, ticket.Who, ticket.Protocol, ticket.HostTitle);
                audit.Write(ticket.Who, (ticket.Protocol == "rdp" ? "RDP" : "терминал") + ": открыт", $"{ticket.HostTitle}, сеанс {sessionId}");
                if (ticket.NoRecording is { } noRec)
                {
                    store.SessionNote(sessionId, "без записи: " + noRec);
                    audit.Write(ticket.Who, "сеанс без записи", $"{ticket.HostTitle}, сеанс {sessionId}: {noRec}");
                }
                // ready несёт номер соединения у guacd — по нему подключается зритель
                mine = new LiveSession { Id = sessionId, Who = ticket.Who, Login = ticket.Login, Protocol = ticket.Protocol, HostTitle = ticket.HostTitle, ConnectionId = rv.Count > 1 ? rv[1] : "", IsPowerShell = ticket.HostId == "powershell" };
                live.Add(mine);
            }
            // первая инструкция туннеля — служебная, с его номером (так ждёт Guacamole.WebSocketTunnel)
            await WsSend(ws, GuacProtocol.Encode("", sessionId), ctx.RequestAborted);

            // сеанс закрывают: из браузера, со стороны guacd или админом (live.Close → Kill)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            // закрыт админом: браузеру — ошибка «сеанс закрыт» (клиент сам отключится и покажет её), затем обрыв;
            // соединение с guacd закрывается в finally — guacd завершает сеанс сам
            using var killReg = mine?.Kill.Token.Register(() => _ = Task.Run(async () =>
            {
                try { await WsSend(ws, GuacProtocol.Encode("error", "Сеанс закрыт администратором", "523"), CancellationToken.None); }
                catch { }
                finally { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
            }));
            // зритель: владелец ушёл или сеанс закрыли — отключиться (guacd держит соединение, пока подключён хоть кто-то)
            using var endedReg = target?.Ended.Token.Register(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
            var toBrowser = Task.Run(async () =>
            {
                var batch = new StringBuilder();
                while (!cts.IsCancellationRequested)
                {
                    var instr = await reader.ReadInstructionAsync(cts.Token);
                    if (instr is null) break;
                    // ошибка от guacd (не пустил к хосту и т. п.) — в статус сеанса
                    if (instr.StartsWith("5.error,", StringComparison.Ordinal) && GuacProtocol.Parse(instr) is { Count: > 1 } err) status = "ошибка: " + err[1];
                    batch.Append(instr);
                    // склеиваем то, что уже пришло, в одно сообщение — меньше кадров WebSocket
                    while (reader.HasBuffered && batch.Length < 32768 && await reader.TryReadBufferedAsync() is { } more) batch.Append(more);
                    await WsSend(ws, batch.ToString(), cts.Token);
                    batch.Clear();
                }
            }, cts.Token);
            var toGuacd = Task.Run(async () =>
            {
                var buf = new byte[16384];
                var msg = new MemoryStream();
                while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    msg.Write(buf, 0, r.Count);
                    // сообщение без конца (кусками) — не копить бесконечно: как и у потока guacd, предел 4 МБ
                    if (msg.Length > 4 * 1024 * 1024) throw new FormatException("guac: слишком длинное сообщение браузера");
                    if (!r.EndOfMessage) continue;
                    var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                    msg.SetLength(0);
                    // служебные инструкции туннеля (opcode "") в guacd не идут; ping — эхом обратно
                    if (text.StartsWith("0.,", StringComparison.Ordinal))
                    {
                        var parts = GuacProtocol.Parse(text);
                        if (parts.Count > 1 && parts[1] == "ping") await WsSend(ws, text, cts.Token);
                        continue;
                    }
                    // зритель: к guacd — только служебное (sync, ack, nop, disconnect; size — нет: не менять размер чужого терминала), ни клавиш, ни мыши, ни буфера
                    if (watching) text = WatcherOnly(text);
                    if (text.Length == 0) continue;
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(bytes, cts.Token);
                }
            }, cts.Token);
            await Task.WhenAny(toBrowser, toGuacd);
            cts.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or FormatException)
        {
            status = "ошибка: " + ex.Message;
            audit.Write(ticket.Who, watching ? "сеанс: просмотр — ошибка" : (ticket.Protocol == "rdp" ? "RDP" : "терминал") + ": ошибка", $"{ticket.HostTitle}: {ex.Message}");
        }
        finally
        {
            tcp?.Dispose();
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            if (!watching) live.Release(ticket.Login);
            if (counted && target is not null)
            {
                target.WatcherOut();
                audit.Write(ticket.Who, "сеанс: просмотр окончен", $"{target.HostTitle}, {target.Who}, {(DateTime.UtcNow - started).TotalMinutes:0.#} мин, сеанс {target.Id}");
            }
            if (mine is not null)
            {
                live.Remove(sessionId);
                if (mine.ClosedBy is { } by) status = "закрыт администратором: " + by;
                // зрители отключаются вслед за владельцем (Ended не освобождаем: на него ещё могут подписываться)
                try { mine.Ended.Cancel(); } catch (ObjectDisposedException) { }
                mine.Kill.Dispose();
            }
            if (target is null && store.FindSession(sessionId) is not null)
            {
                store.SessionEnd(sessionId, status);
                var mins = (DateTime.UtcNow - started).TotalMinutes;
                audit.Write(ticket.Who, (ticket.Protocol == "rdp" ? "RDP" : "терминал") + ": закрыт", $"{ticket.HostTitle}, {mins:0.#} мин, сеанс {sessionId}" + (status == "закрыт" ? "" : ", " + status));
            }
        }
    }

    /// <summary>
    /// Запись сеанса: kind=screen — поток Guacamole для проигрывания, kind=text — текст терминала (typescript) скачать.
    /// Только с правом «аудит» (назначение на «всё»); каждое открытие — в аудит.
    /// </summary>
    private static IResult Recording(HttpContext ctx, string id, string? kind, Auth.LoginService login, WebStore store, AuditLog audit)
    {
        var p = ctx.User;
        var user = login.Resolve(p.Identity?.Name ?? "", p.FindFirst("kind")?.Value ?? "", p.FindFirst("display")?.Value ?? "", p.FindAll("grp").Select(c => c.Value));
        if (user is null || !user.Can(Auth.Perm.AuditView)) return Results.StatusCode(403);
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) || store.FindSession(id) is not { } s) return Results.NotFound();
        var text = kind == "text";
        // имена задаёт OpenTerminal: экран — <id>.guac, текст терминала — <id>.txt (+ .txt.timing)
        // в буфере или уже в хранилище (SMB, NFS, диск)
        if (RecordingStorage.Find(id + (text ? ".txt" : ".guac")) is not { } file) return Results.NotFound();
        // паузы для «пропускать простой» — часть того же просмотра, в аудит отдельно не пишется
        if (kind == "gaps") return Results.Json(Gaps(file, 5000));
        // как во всём аудите: логин (адрес)
        var who = $"{user.Login} ({Auth.AuthEndpoints.ClientIp(ctx)})";
        audit.Write(who, text ? "запись: скачан текст" : "запись: просмотр", $"{s.Host}, {s.Who}, {s.Start}, сеанс {id}");
        if (text)
        {
            // typescript — сырой поток терминала: убрать управляющие последовательности, чтобы читался как текст
            var raw = ReadShared(file);
            var clean = AnsiRx().Replace(raw.Replace("\r\n", "\n"), "");
            // строка с возвратом каретки — видна только последняя перерисовка; Backspace стирает символ
            clean = string.Join("\n", clean.Split('\n').Select(Redraw));
            return Results.File(Encoding.UTF8.GetBytes(clean), "text/plain; charset=utf-8", $"{s.Host}-{s.Start.Replace(':', '-').Replace(' ', '_')}.txt");
        }
        return Results.File(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), "application/octet-stream");
    }

    private static readonly HashSet<string> WatcherOps = new(StringComparer.Ordinal) { "sync", "ack", "nop", "disconnect" };

    /// <summary>Из сообщения зрителя — только служебные инструкции; клавиши, мышь, буфер и потоки отбрасываются.</summary>
    private static string WatcherOnly(string text)
    {
        var sb = new StringBuilder(text);
        var res = new StringBuilder();
        var pos = 0;
        while (pos < sb.Length)
        {
            var n = GuacProtocol.CompleteLength(sb, pos);
            if (n < 0) break;
            var instr = sb.ToString(pos, n - pos);
            pos = n;
            if (GuacProtocol.Parse(instr) is { Count: > 0 } p && WatcherOps.Contains(p[0])) res.Append(instr);
        }
        return res.ToString();
    }

    /// <summary>
    /// Простой в записи для «пропускать простой»: отрезки длиннее minGapMs, где на экране ничего не менялось —
    /// [начало, конец] в мс от первого sync. guacd шлёт sync и в простое (раз в несколько секунд), поэтому простой —
    /// это кадры, в которых между sync нет ничего, кроме sync и nop. Читается потоком, по целым инструкциям.
    /// </summary>
    private static List<long[]> Gaps(string file, long minGapMs)
    {
        var gaps = new List<long[]>();
        long first = -1, lastActive = -1;
        var dirty = false;
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var buf = new char[65536];
        var sb = new StringBuilder();
        int read;
        try
        {
            while ((read = sr.Read(buf, 0, buf.Length)) > 0)
            {
                sb.Append(buf, 0, read);
                var pos = 0;
                while (pos < sb.Length)
                {
                    var end = GuacProtocol.CompleteLength(sb, pos);
                    if (end < 0) break;
                    var (op, arg) = OpAndFirstArg(sb, pos);
                    if (op == "sync")
                    {
                        if (long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
                        {
                            if (first < 0) { first = ts; lastActive = ts; }
                            if (dirty)
                            {
                                if (ts - lastActive > minGapMs) gaps.Add([lastActive - first, ts - first]);
                                lastActive = ts;
                                dirty = false;
                            }
                        }
                    }
                    else if (op != "nop") dirty = true;
                    pos = end;
                }
                sb.Remove(0, pos);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException) { }   // испорченный хвост записи — что нашли, то и отдаём
        return gaps;
    }

    /// <summary>Опкод и первый аргумент инструкции, начинающейся с pos (инструкция целая — проверил CompleteLength).</summary>
    private static (string Op, string Arg) OpAndFirstArg(StringBuilder sb, int pos)
    {
        (string Value, int Next) Elem(int i)
        {
            var dot = i;
            while (sb[dot] != '.') dot++;
            var len = int.Parse(sb.ToString(i, dot - i), CultureInfo.InvariantCulture);
            // короткие элементы (опкод, отметка времени) — без суррогатных пар
            return (sb.ToString(dot + 1, len), dot + 1 + len);
        }
        var (op, next) = Elem(pos);
        if (op != "sync" || sb[next] != ',') return (op, "");
        return (op, Elem(next + 1).Value);
    }

    private static string Redraw(string line)
    {
        if (line.Contains('\r')) line = line[(line.LastIndexOf('\r') + 1)..];
        if (!line.Contains('\b')) return line;
        var sb = new StringBuilder();
        foreach (var ch in line)
            if (ch == '\b') { if (sb.Length > 0) sb.Length--; }
            else sb.Append(ch);
        return sb.ToString();
    }

    private static string ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>Управляющие последовательности терминала: CSI, OSC, прочие ESC и служебные символы (кроме \n, \r, \t, BS).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[()][0-9A-Za-z]|\x1B[@-_=>]|[\x00-\x07\x0B\x0C\x0E-\x1A\x1C-\x1F\x7F]")]
    private static partial System.Text.RegularExpressions.Regex AnsiRx();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendGates = new();

    /// <summary>WebSocket не терпит две отправки сразу (поток guacd и эхо ping) — по очереди.</summary>
    private static async Task WsSend(WebSocket ws, string text, CancellationToken ct)
    {
        var gate = SendGates.GetValue(ws, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally { gate.Release(); }
    }

    private static (string Host, int Port) SplitHostPort(string s)
    {
        var i = s.LastIndexOf(':');
        return i > 0 && int.TryParse(s[(i + 1)..], out var p) ? (s[..i].Trim('[', ']'), p) : (s, 4822);
    }

    /// <summary>Чтение инструкций из потока guacd: UTF-8 → символы, режем по целым инструкциям.</summary>
    private sealed class GuacReader(Stream stream)
    {
        private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
        private readonly byte[] _bytes = new byte[32768];
        private readonly char[] _chars = new char[32768 + 4];
        private readonly StringBuilder _buf = new();

        public bool HasBuffered => _buf.Length > 0;

        /// <summary>Целая инструкция из уже прочитанного, без чтения из сети.</summary>
        public Task<string?> TryReadBufferedAsync()
        {
            var n = GuacProtocol.CompleteLength(_buf);
            if (n < 0) return Task.FromResult<string?>(null);
            var s = _buf.ToString(0, n);
            _buf.Remove(0, n);
            return Task.FromResult<string?>(s);
        }

        public async Task<string?> ReadInstructionAsync(CancellationToken ct)
        {
            while (true)
            {
                var n = GuacProtocol.CompleteLength(_buf);
                if (n >= 0)
                {
                    var s = _buf.ToString(0, n);
                    _buf.Remove(0, n);
                    return s;
                }
                if (_buf.Length > 4 * 1024 * 1024) throw new FormatException("guac: слишком длинная инструкция");
                var read = await stream.ReadAsync(_bytes, ct);
                if (read == 0) return null;
                var chars = _utf8.GetChars(_bytes, 0, read, _chars, 0);
                _buf.Append(_chars, 0, chars);
            }
        }
    }
}
