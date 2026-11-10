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
        CancellationToken cancellationToken)
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
                    ? $"Apply: {hosts.Count} хост(ов), {actions.Count} действий"
                    : $"Plan (dry-run): {hosts.Count} хост(ов), {actions.Count} действий",
                apply ? "изменения будут применены для destructive" : "destructive только как план",
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
                        RunHost(host, hosts.Count, actions, apply, mode, history, progress, cancellationToken);
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
        CancellationToken cancellationToken)
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
                    progress.Report(new PlanProgress { Row = skip });
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
                    progress.Report(new PlanProgress { Row = scriptFail });
                    Remember(history, ToHistory(mode, host.Name, action.Name, scriptFail));
                    continue;
                }

                if (action.Destructive && !apply)
                {
                    if (BuiltinActions.TrySimulate(action.Id, distro, out var simulated))
                    {
                        var simTimeout = action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 180;
                        var simKey = Started(progress, host.Name, action.Name, "симуляция");
                        var sim = ssh.Run(action.Id, simulated, simTimeout, SshRunner.NeedsSudo(host, action), host, cancellationToken: cancellationToken,
                            onOutput: chunk => progress.Report(new PlanProgress { Live = chunk, LiveKey = simKey }));
                        sim.HostName = host.Name;
                        sim.ActionName = action.Name;
                        var simRow = FromActionResult(host.Name, action.Name, sim);
                        simRow = MakeRow("DRY-RUN", simRow.Title, simRow.Preview, simRow.Detail, sim.Ok);
                        progress.Report(new PlanProgress { Row = simRow });
                        Remember(history, ToHistory(mode, host.Name, action.Name, simRow));
                    }
                    else
                    {
                        var dry = MakeRow("DRY-RUN", $"{host.Name} · {action.Name}",
                            "скрипт не выполнялся",
                            script, ok: true);
                        progress.Report(new PlanProgress { Row = dry });
                        Remember(history, ToHistory(mode, host.Name, action.Name, dry));
                    }

                    continue;
                }

                var timeout = action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 180;
                var sudo = SshRunner.NeedsSudo(host, action);
                var key = Started(progress, host.Name, action.Name, apply && action.Destructive ? "применяется" : "выполняется");
                var result = ssh.Run(action.Id, script, timeout, sudo, host, cancellationToken: cancellationToken,
                    onOutput: chunk => progress.Report(new PlanProgress { Live = chunk, LiveKey = key }));
                result.HostName = host.Name;
                result.ActionName = action.Name;
                var row = FromActionResult(host.Name, action.Name, result);
                progress.Report(new PlanProgress { Row = row });
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

    public static ResultRow MakeRow(string status, string title, string preview, string detail, bool ok)
        => new()
        {
            Status = status,
            Title = title,
            Preview = preview,
            Detail = detail,
            IsOk = ok || status is "SKIP" or "DRY-RUN" or "INFO" or "WARN" or "RUN"
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

    private static ResultRow FromActionResult(string host, string actionName, ActionResult r)
    {
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
