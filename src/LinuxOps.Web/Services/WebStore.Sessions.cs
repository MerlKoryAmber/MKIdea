namespace LinuxOps.Web.Services;

/// <summary>Сеанс SSH или RDP через guacd: кто, куда, когда; файл записи — в каталоге записей (WebPaths.RecordingsDir).</summary>
public sealed record SessionRow(string Id, string Start, string End, string Who, string Protocol, string Host, string Status);

public sealed partial class WebStore
{
    private bool _sessionsReady;

    private void EnsureSessions()
    {
        if (_sessionsReady) return;
        using var c = Open();
        Exec(c, "CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, start TEXT NOT NULL, end TEXT NOT NULL DEFAULT '', who TEXT NOT NULL, protocol TEXT NOT NULL, host TEXT NOT NULL, status TEXT NOT NULL DEFAULT '')");
        // служба остановилась посреди сеанса — такие сеансы уже не идут
        Exec(c, "UPDATE sessions SET status='прерван (перезапуск службы)' || CASE WHEN instr(status, ' · ') > 0 THEN substr(status, instr(status, ' · ')) ELSE '' END WHERE status='идёт' OR status LIKE 'идёт · %'");
        _sessionsReady = true;
    }

    public void SessionStart(string id, string who, string protocol, string host)
    {
        EnsureSessions();
        using var c = Open();
        Exec(c, "INSERT INTO sessions(id, start, who, protocol, host, status) VALUES($id, $s, $w, $p, $h, 'идёт')",
            ("$id", id), ("$s", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$w", who), ("$p", protocol), ("$h", host));
    }

    public void SessionEnd(string id, string status)
    {
        EnsureSessions();
        using var c = Open();
        // пометки, сделанные во время сеанса («без записи: …»), остаются после итогового статуса
        Exec(c, "UPDATE sessions SET end=$e, status = $st || CASE WHEN instr(status, ' · ') > 0 THEN substr(status, instr(status, ' · ')) ELSE '' END WHERE id=$id",
            ("$id", id), ("$e", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$st", status));
    }

    /// <summary>Пометка к статусу сеанса (запись удалена и т. п.): «статус · пометка».</summary>
    public void SessionNote(string id, string note)
    {
        EnsureSessions();
        using var c = Open();
        Exec(c, "UPDATE sessions SET status = CASE WHEN status='' THEN $n ELSE status || ' · ' || $n END WHERE id=$id", ("$id", id), ("$n", note));
    }

    public List<SessionRow> LoadSessions(int limit = 500)
    {
        EnsureSessions();
        var list = new List<SessionRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, start, end, who, protocol, host, status FROM sessions ORDER BY start DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new SessionRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    public SessionRow? FindSession(string id) => LoadSessions(100000).FirstOrDefault(s => s.Id == id);
}
