using LinuxOps.Models;

namespace LinuxOps.Services;

public sealed class PlanProgress
{
    public string? Status { get; init; }
    public ResultRow? Row { get; init; }
    public HostConfig? Host { get; init; }
    public string? HostStatus { get; init; }
    public string? DistroHint { get; init; }
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

        progress.Report(new PlanProgress
        {
            Row = MakeRow(
                "INFO",
                apply
                    ? $"Apply: {hosts.Count} хост(ов), {actions.Count} действий"
                    : $"Plan (dry-run): {hosts.Count} хост(ов), {actions.Count} действий",
                apply ? "изменения будут применены для destructive" : "destructive только как план",
                "",
                ok: true)
        });

        try
        {
            await Task.Run(() =>
            {
                for (var i = 0; i < hosts.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var host = hosts[i];
                    progress.Report(new PlanProgress { Status = $"хост {i + 1}/{hosts.Count} · {host.Name}" });

                    try
                    {
                        using var ssh = SshRunner.Connect(host);
                        var distro = ssh.DetectDistro();
                        progress.Report(new PlanProgress
                        {
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
                            progress.Report(new PlanProgress
                            {
                                Status = $"хост {i + 1}/{hosts.Count} · {action.Name}"
                            });

                            if (!ActionCatalog.Supports(action, distro))
                            {
                                var skip = MakeRow("SKIP", $"{host.Name} · {action.Name}",
                                    $"не для family={distro.Family}",
                                    $"Действие {action.Id} не поддерживает {distro.Family}.",
                                    ok: true);
                                progress.Report(new PlanProgress { Row = skip });
                                history.Add(ToHistory(mode, host.Name, action.Name, skip));
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
                                history.Add(ToHistory(mode, host.Name, action.Name, scriptFail));
                                continue;
                            }

                            if (action.Destructive && !apply)
                            {
                                var dry = MakeRow("DRY-RUN", $"{host.Name} · {action.Name}",
                                    "скрипт не выполнен",
                                    script, ok: true);
                                progress.Report(new PlanProgress { Row = dry });
                                history.Add(ToHistory(mode, host.Name, action.Name, dry));
                                continue;
                            }

                            var timeout = action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 180;
                            var result = ssh.Run(action.Id, script, timeout);
                            result.HostName = host.Name;
                            result.ActionName = action.Name;
                            var row = FromActionResult(host.Name, action.Name, result);
                            progress.Report(new PlanProgress { Row = row });
                            history.Add(ToHistory(mode, host.Name, action.Name, row));
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
                        history.Add(ToHistory(mode, host.Name, "pre-flight", fail));
                    }
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        return new PlanRunResult
        {
            Canceled = canceled,
            History = history
        };
    }

    public static int Count(IEnumerable<ResultRow> rows, string status)
        => rows.Count(r => r.Status == status);

    public static ResultRow MakeRow(string status, string title, string preview, string detail, bool ok)
        => new()
        {
            Status = status,
            Title = title,
            Preview = preview,
            Detail = detail,
            IsOk = ok || status is "SKIP" or "DRY-RUN" or "INFO" or "WARN"
        };

    private static HistoryEntry ToHistory(string mode, string host, string action, ResultRow row) => new()
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
        var preview = FirstNonEmptyLine(r.StdOut) ?? FirstNonEmptyLine(r.StdErr) ?? r.Summary;
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

    private static string TrimBody(string text, int maxLines = 80)
    {
        var lines = SshRunner.NormalizeLf(text).Split('\n');
        if (lines.Length <= maxLines) return text.TrimEnd();
        return string.Join('\n', lines.Take(maxLines)) + $"\n… ещё {lines.Length - maxLines} строк";
    }
}
