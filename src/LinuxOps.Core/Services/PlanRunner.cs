using LinuxOps.Models;

namespace LinuxOps.Services;

public sealed class PlanProgress
{
    public string? Status { get; init; }
    public ResultRow? Row { get; init; }
    public HostConfig? Host { get; init; }
    public string? HostStatus { get; init; }
    public string? DistroHint { get; init; }
    /// <summary>Кусок живого вывода для строки RUN с этим заголовком.</summary>
    public string? Live { get; init; }
    public string? LiveKey { get; init; }
}

public sealed class PlanRunResult
{
    public bool Canceled { get; init; }
    public List<HistoryEntry> History { get; init; } = new();
}

/// <summary>Запуск плана по хостам. UI не трогает: строки и статус хоста — через <see cref="IProgress{T}"/>.</summary>
public sealed class PlanRunner
{
    public async Task<PlanRunResult> RunAsync(
        IReadOnlyList<HostConfig> hosts,
        IReadOnlyList<ActionDefinition> actions,
        bool apply,
        IProgress<PlanProgress> progress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? args = null)
    {
        var mode = apply ? "apply" : "dry-run";
        var history = new List<HistoryEntry>();
        var canceled = false;
        var finished = 0;

        progress.Report(new PlanProgress
        {
            Status = $"хост 0 из {hosts.Count}",
            Row = MakeRow(
                "INFO",
                apply
                    ? $"Применить: хостов {hosts.Count}, действий {actions.Count}"
                    : $"Dry-run: хостов {hosts.Count}, действий {actions.Count}",
                apply ? "меняющие действия выполняются на хостах" : "меняющие действия — только план, на хостах ничего не меняется",
                "",
                ok: true)
        });

        var gate = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        try
        {
            foreach (var host in hosts)
            {
                await gate.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        RunHost(host, hosts.Count, actions, apply, mode, history, progress, cancellationToken, args);
                    }
                    finally
                    {
                        var n = Interlocked.Increment(ref finished);
                        progress.Report(new PlanProgress { Status = $"хост {n} из {hosts.Count}" });
                        gate.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (IsCancel(ex))
        {
            canceled = true;
            if (tasks.Count > 0)
            {
                try { await Task.WhenAll(tasks); }
                catch (Exception waitEx) when (IsCancel(waitEx)) { }
            }
        }

        return new PlanRunResult
        {
            Canceled = canceled,
            History = history
        };
    }

    private static void RunHost(
        HostConfig host,
        int hostTotal,
        IReadOnlyList<ActionDefinition> actions,
        bool apply,
        string mode,
        List<HistoryEntry> history,
        IProgress<PlanProgress> progress,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? args)
    {
        try
        {
            using var ssh = SshRunner.Connect(host);
            var distro = ssh.DetectDistro();
            progress.Report(new PlanProgress
            {
                Status = $"хост · {host.Name} из {hostTotal}",
                Host = host,
                HostStatus = "Online",
                DistroHint = distro.Id,
                Row = MakeRow(
                    "OK",
                    $"Pre-flight · {host.Name}",
                    $"SSH OK · {distro.PrettyName} ({distro.Family})",
                    $"host={host.Host}\nuser={host.Username}\nid={distro.Id}\nfamily={distro.Family}\n{distro.PrettyName}",
                    ok: true)
            });

            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new PlanProgress { Status = $"{host.Name} · {action.Name}" });

                if (!ActionCatalog.Supports(action, distro))
                {
                    var skip = MakeRow("SKIP", $"{host.Name} · {action.Name}",
                        $"не для family={distro.Family}",
                        $"Действие {action.Id} не поддерживает {distro.Family}.",
                        ok: true);
                    progress.Report(new PlanProgress { Row = skip, Host = host });
                    Remember(history, ToHistory(mode, host.Name, action.Name, skip));
                    continue;
                }

                string script;
                try
                {
                    script = ActionCatalog.ResolveScript(action, distro);
                }
                catch (Exception ex)
                {
                    var scriptFail = MakeRow("FAIL", $"{host.Name} · {action.Name}",
                        ex.Message, ex.ToString(), ok: false);
                    progress.Report(new PlanProgress { Row = scriptFail, Host = host });
                    Remember(history, ToHistory(mode, host.Name, action.Name, scriptFail));
                    continue;
                }

                if (action.Destructive && !apply)
                {
                    if (BuiltinActions.TrySimulate(action.Id, distro, out var simulated))
                    {
                        var simTimeout = action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 180;
                        var simKey = Started(progress, host.Name, action.Name, "симуляция");
                        var simEnv = Env(action, args, dry: BuiltinActions.UsesDryFlag(action.Id));
                        ActionResult sim;
                        var simTunnel = Tunnel.OpenIfAsked(ssh, action, simEnv);
                        try
                        {
                            sim = ssh.Run(action.Id, simulated, simTimeout, SshRunner.NeedsSudo(host, action), host, simEnv, cancellationToken: cancellationToken,
                                onOutput: chunk => progress.Report(new PlanProgress { Live = chunk, LiveKey = simKey }));
                        }
                        finally
                        {
                            simTunnel?.Dispose();
                        }
                        sim.HostName = host.Name;
                        sim.ActionName = action.Name;
                        var simRow = FromActionResult(host.Name, action.Name, sim, simTunnel, isPreview: true);
                        simRow = MakeRow("DRY-RUN", simRow.Title, simRow.Preview, simRow.Detail, sim.Ok, simRow.PackageUpdate);
                        progress.Report(new PlanProgress { Row = simRow, Host = host });
                        Remember(history, ToHistory(mode, host.Name, action.Name, simRow));
                    }
                    else
                    {
                        var dry = MakeRow("DRY-RUN", $"{host.Name} · {action.Name}",
                            "скрипт не выполнялся",
                            script, ok: true);
                        progress.Report(new PlanProgress { Row = dry, Host = host });
                        Remember(history, ToHistory(mode, host.Name, action.Name, dry));
                    }

                    continue;
                }

                var timeout = action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 180;
                var sudo = SshRunner.NeedsSudo(host, action);
                var key = Started(progress, host.Name, action.Name, apply && action.Destructive ? "применяется" : "выполняется");
                var env = Env(action, args, dry: false);

                // Галка «через этот ПК»: прокси на ПК + обратный проброс SSH на время команды.
                ActionResult result;
                var tunnel = Tunnel.OpenIfAsked(ssh, action, env);
                try
                {
                    result = ssh.Run(action.Id, script, timeout, sudo, host, env, cancellationToken: cancellationToken,
                        onOutput: chunk => progress.Report(new PlanProgress { Live = chunk, LiveKey = key }));
                }
                finally
                {
                    tunnel?.Dispose();
                }
                result.HostName = host.Name;
                result.ActionName = action.Name;
                var row = FromActionResult(host.Name, action.Name, result, tunnel);
                row = WithCve(row, host.Name, action.Name, env, progress, key, cancellationToken);
                progress.Report(new PlanProgress { Row = row, Host = host });
                Remember(history, ToHistory(mode, host.Name, action.Name, row));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fail = MakeRow("FAIL", $"Pre-flight · {host.Display}", ex.Message, ex.ToString(), ok: false);
            progress.Report(new PlanProgress
            {
                Host = host,
                HostStatus = "Offline",
                Row = fail
            });
            Remember(history, ToHistory(mode, host.Name, "pre-flight", fail));
        }
    }

    /// <summary>
    /// Параметры действия — переменными окружения (значения через base64, без подстановок в shell).
    /// Значение из окна запуска (ключ «id:Имя»), иначе по умолчанию. dry — LINUXOPS_DRY=1: показать, не менять.
    /// </summary>
    internal static Dictionary<string, string>? Env(ActionDefinition action, IReadOnlyDictionary<string, string>? args, bool dry)
    {
        if (action.Parameters.Count == 0 && !dry) return null;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in action.Parameters)
            env[p.Name] = args is not null && args.TryGetValue(action.Id + ":" + p.Name, out var v) ? v : p.Default;
        if (dry) env["LINUXOPS_DRY"] = "1";
        return env;
    }

    /// <summary>Строка RUN: действие запущено. Итоговая строка с тем же заголовком её заменит.</summary>
    private static string Started(IProgress<PlanProgress> progress, string host, string action, string what)
    {
        var title = $"{host} · {action}";
        progress.Report(new PlanProgress { Row = MakeRow("RUN", title, what + "…", "", ok: true), Status = title });
        return title;
    }

    private static void Remember(List<HistoryEntry> history, HistoryEntry entry)
    {
        lock (history) history.Add(entry);
    }

    private static bool IsCancel(Exception ex)
        => ex is OperationCanceledException
           || (ex is AggregateException ae && ae.InnerExceptions.All(e => e is OperationCanceledException));

    public static int Count(IEnumerable<ResultRow> rows, string status)
        => rows.Count(r => r.Status == status);

    public static ResultRow MakeRow(string status, string title, string preview, string detail, bool ok, PackageUpdateReport? update = null)
        => new()
        {
            Status = status,
            Title = title,
            Preview = preview,
            Detail = detail,
            IsOk = ok || status is "SKIP" or "DRY-RUN" or "INFO" or "WARN" or "RUN",
            PackageUpdate = update
        };

    internal static HistoryEntry ToHistory(string mode, string host, string action, ResultRow row) => new()
    {
        AtUtc = DateTime.UtcNow,
        Mode = mode,
        Host = host,
        Action = action,
        Status = row.Status,
        Preview = row.Preview,
        Detail = string.IsNullOrWhiteSpace(row.Detail) ? row.Preview : row.Detail
    };

    /// <summary>
    /// Туннель «через этот ПК» для «Версий ПО» и «Обновить отмеченное»: прокси на 127.0.0.1 ПК и обратный проброс SSH,
    /// адрес — в UPD_PROXY. Не поднялся — Note, команда идёт обычным путём. Журнал снимается при закрытии.
    /// </summary>
    internal sealed class Tunnel : IDisposable
    {
        private PcProxy? _proxy;
        private IDisposable? _forward;
        public string Note { get; private set; } = "";
        public int Connections { get; private set; } = -1;
        public string Log { get; private set; } = "";

        public static Tunnel? OpenIfAsked(SshRunner ssh, ActionDefinition action, Dictionary<string, string>? env)
        {
            if (env is null || !(action.Id is VersionsCheck.ActionId or PackageUpdate.ActionId)
                || !env.TryGetValue(VersionsCheck.ViaPcParam, out var via) || !ActionParam.IsYes(via))
                return null;
            var t = new Tunnel();
            try
            {
                t._proxy = PcProxy.Start();
                t._forward = ssh.OpenRemoteForward((uint)t._proxy.Port, out var remotePort);
                env["UPD_PROXY"] = $"http://127.0.0.1:{remotePort}";
            }
            catch (Exception ex)
            {
                t.Note = ex.Message;
                t._proxy?.Dispose();
                t._proxy = null;
            }
            return t;
        }

        public void Dispose()
        {
            _forward?.Dispose();
            _forward = null;
            if (_proxy is null) return;
            Connections = _proxy.Connections;
            Log = _proxy.Log;
            _proxy.Dispose();
            _proxy = null;
        }
    }

    private static ResultRow VersionsRow(string host, string actionName, VersionReport versions) => new()
    {
        // Обновление безопасности, CVE, ветка без поддержки или просто доступное обновление — внимание, не сбой.
        Status = versions.BadCount + versions.WarnCount > 0 ? "WARN" : "OK",
        Title = $"{host} · {actionName}",
        Preview = versions.Summary,
        Detail = VersionsCheck.Format(host, versions),
        IsOk = true,
        Versions = versions
    };

    /// <summary>«Версии ПО» → сверка CVE с ПК (галка UPD_CVE, по умолчанию да); строка пересобирается.</summary>
    private static ResultRow WithCve(ResultRow row, string host, string actionName, Dictionary<string, string>? env,
        IProgress<PlanProgress> progress, string key, CancellationToken ct)
    {
        if (row.Versions is not { } vr) return row;
        if (env is not null && env.TryGetValue(CveCheck.Param, out var on) && !ActionParam.IsYes(on))
        {
            vr.CveOff = true;
            return VersionsRow(host, actionName, vr);
        }
        if (vr.Items.Count == 0) return row;
        // та же строка RUN (по ключу): новая строка RUN с тем же заголовком осталась бы висеть над итогом
        progress.Report(new PlanProgress { Live = $"\nсверка CVE с базами уязвимостей (с {RunPlace.OfHere})…\n", LiveKey = key, Status = $"{row.Title} · сверка CVE" });
        CveCheck.Run(vr, ct);
        return VersionsRow(host, actionName, vr);
    }

    private static ResultRow FromActionResult(string host, string actionName, ActionResult r, Tunnel? tunnel = null, bool isPreview = false)
    {
        if (string.Equals(r.ActionId, PackageUpdate.ActionId, StringComparison.OrdinalIgnoreCase)
            && PackageUpdate.Parse(r.StdOut, isPreview) is { } upd)
        {
            upd.Host = host;
            upd.TunnelNote = tunnel?.Note ?? "";
            upd.TunnelLog = tunnel?.Log ?? "";
            var updDetail = upd.Format() + (upd.TunnelNote.Length > 0 ? $"\nТуннель через {RunPlace.Here} не поднят: {upd.TunnelNote}\n" : "")
                         + (upd.TunnelLog.Length > 0 ? "\n" + upd.TunnelLog + "\n" : "")
                         + "\n--- вывод ---\n" + r.StdOut + (r.StdErr.Length > 0 ? "\n" + r.StdErr : "");
            var ok = upd.Error.Length == 0 && upd.ExitCode == 0;
            return new ResultRow
            {
                Status = ok ? (upd.RestartServices.Count > 0 ? "WARN" : "OK") : "FAIL",
                Title = $"{host} · {actionName}",
                Preview = upd.Summary,
                Detail = updDetail,
                IsOk = ok,
                PackageUpdate = upd
            };
        }

        if (string.Equals(r.ActionId, PackageUpdate.RestartId, StringComparison.OrdinalIgnoreCase))
        {
            var rs = PackageUpdate.ParseRestart(r.StdOut);
            rs.Host = host;
            return new ResultRow
            {
                Status = rs.Failed == 0 && r.Ok ? "OK" : "WARN",
                Title = $"{host} · {actionName}",
                Preview = rs.Summary,
                Detail = rs.Format() + (r.StdErr.Length > 0 ? "\n" + r.StdErr : ""),
                IsOk = r.Ok
            };
        }

        if (r.Ok && string.Equals(r.ActionId, UserAudit.ActionId, StringComparison.OrdinalIgnoreCase)
            && UserAudit.Parse(r.StdOut) is { } audit)
        {
            audit.Host = host;
            return new ResultRow
            {
                Status = "OK",
                Title = $"{host} · {actionName}",
                Preview = audit.Summary,
                Detail = UserAudit.Format(host, audit),
                IsOk = true,
                Audit = audit
            };
        }

        if (r.Ok && string.Equals(r.ActionId, ProxyCheck.ActionId, StringComparison.OrdinalIgnoreCase)
            && ProxyCheck.Parse(r.StdOut) is { } proxy)
        {
            proxy.Host = host;
            return new ResultRow
            {
                // Репозиторий недоступен — не сбой проверки, но внимание.
                Status = proxy.RepoOk == false ? "WARN" : "OK",
                Title = $"{host} · {actionName}",
                Preview = proxy.Summary,
                Detail = ProxyCheck.Format(host, proxy),
                IsOk = true,
                Proxy = proxy
            };
        }

        if (r.Ok && string.Equals(r.ActionId, VersionsCheck.ActionId, StringComparison.OrdinalIgnoreCase)
            && VersionsCheck.Parse(r.StdOut) is { } versions)
        {
            versions.Host = host;
            if (tunnel is not null)
            {
                versions.TunnelNote = tunnel.Note;
                versions.TunnelConnections = tunnel.Connections;
                versions.TunnelLog = tunnel.Log;
            }
            return VersionsRow(host, actionName, versions);
        }

        var preview = string.Equals(r.ActionId, "check-updates", StringComparison.OrdinalIgnoreCase)
            ? SummarizeUpdates(r.StdOut + "\n" + r.StdErr)
            : string.Equals(r.ActionId, "install-updates", StringComparison.OrdinalIgnoreCase) && SummarizeInstall(r.StdOut + "\n" + r.StdErr) is { } installed
                ? installed
                : LastNonEmptyLine(r.StdOut) ?? FirstNonEmptyLine(r.StdErr) ?? r.Summary;
        if (preview.Length > 120) preview = preview[..117] + "…";

        var detail = new System.Text.StringBuilder();
        detail.AppendLine($"host: {host}");
        detail.AppendLine($"action: {actionName} ({r.ActionId})");
        detail.AppendLine($"exit: {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.StdOut))
        {
            detail.AppendLine();
            detail.AppendLine("--- stdout ---");
            detail.AppendLine(TrimBody(r.StdOut));
        }

        if (!string.IsNullOrWhiteSpace(r.StdErr))
        {
            detail.AppendLine();
            detail.AppendLine("--- stderr ---");
            detail.AppendLine(TrimBody(r.StdErr));
        }

        return MakeRow(r.Ok ? "OK" : "FAIL", $"{host} · {actionName}", preview, detail.ToString().TrimEnd(), r.Ok);
    }

    /// <summary>Последняя непустая строка stdout — обычно итог скрипта.</summary>
    private static string? LastNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return SshRunner.NormalizeLf(text).Split((char)10).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
    }

    private static string? FirstNonEmptyLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var line in SshRunner.NormalizeLf(text).Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return t;
        }

        return null;
    }

    /// <summary>Итог «Установить обновления» (dnf / apt / zypper): сколько пакетов, или «нечего обновлять». null — не распознано.</summary>
    public static string? SummarizeInstall(string text)
    {
        var t = SshRunner.NormalizeLf(text);
        var parts = new List<string>();
        foreach (var (word, ru) in new[] { ("Upgrade", "обновление"), ("Install", "установка"), ("Remove", "удаление"), ("Downgrade", "откат") })
        {
            var m = System.Text.RegularExpressions.Regex.Match(t, @"^\s*" + word + @"\s+(\d+)\s+Packages?", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (m.Success) parts.Add($"{ru}: {m.Groups[1].Value}");
        }

        var apt = System.Text.RegularExpressions.Regex.Match(t, @"(\d+) upgraded, (\d+) newly installed, (\d+) to remove");
        if (apt.Success)
            parts.Add($"обновление: {apt.Groups[1].Value}, установка: {apt.Groups[2].Value}, удаление: {apt.Groups[3].Value}");
        var zyp = System.Text.RegularExpressions.Regex.Match(t, @"(\d+) packages? to upgrade");
        if (zyp.Success) parts.Add($"обновление: {zyp.Groups[1].Value}");
        if (parts.Count > 0)
        {
            var done = t.Contains("Complete!", StringComparison.Ordinal) ? " · готово" : "";
            return "пакеты — " + string.Join(", ", parts) + done;
        }

        if (t.Contains("Nothing to do", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(t, @"\b0 upgraded, 0 newly installed"))
            return "нечего обновлять";
        return null;
    }

    public static string SummarizeUpdates(string text)
    {
        var lines = SshRunner.NormalizeLf(text).Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        var security = 0;
        foreach (var line in lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s+Security", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) security += int.Parse(m.Groups[1].Value);
        }

        var packages = 0;
        foreach (var line in lines)
        {
            if (line.Contains("upgradable", StringComparison.OrdinalIgnoreCase))
            {
                packages++;
                continue;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[a-zA-Z]\s+\|")
                && !line.Contains("Repository", StringComparison.OrdinalIgnoreCase))
            {
                packages++;
                continue;
            }

            if (line.StartsWith('|') || line.StartsWith("S |", StringComparison.Ordinal))
                continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^\S+\.\S+\s+\S+\s+\S+")
                && !line.StartsWith("Last metadata", StringComparison.OrdinalIgnoreCase))
                packages++;
        }

        if (packages == 0 && security == 0)
        {
            if (lines.Count == 0 || lines.All(l => l.StartsWith("Last metadata", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Listing", StringComparison.OrdinalIgnoreCase)))
                return "0 обновлений";
            return "0 обновлений";
        }

        var n = packages > 0 ? packages : security;
        return security > 0 ? $"{n} обновления ({security} security)" : $"{n} обновления";
    }

    private static string TrimBody(string text, int maxLines = 80)
    {
        var lines = SshRunner.NormalizeLf(text).Split('\n');
        if (lines.Length <= maxLines) return text.TrimEnd();
        return string.Join('\n', lines.Take(maxLines)) + $"\n… ещё {lines.Length - maxLines} строк";
    }
}
