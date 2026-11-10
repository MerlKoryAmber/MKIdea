using System.Text;
using System.Windows;
using LinuxOps.Models;

namespace LinuxOps.Services;

/// <summary>
/// PowerShell-действия Actions: свой движок (не сеанс вкладки PowerShell), подключение к vCenter держится
/// до закрытия программы. Пароль vCenter — в памяти процесса, на диск не пишется.
/// </summary>
public sealed class PsActionRunner : IDisposable
{
    private static readonly Dictionary<string, (string User, string Password)> Credentials = new(StringComparer.OrdinalIgnoreCase);
    private PsEngineClient? _engine;
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<(bool Ok, string? Note)>? _done;
    private readonly StringBuilder _out = new();

    public Func<Window?>? Owner { get; set; }

    private async Task<PsEngineClient> Engine()
    {
        if (_engine is { IsRunning: true } && _ready is { Task.IsCompletedSuccessfully: true }) return _engine;
        _engine?.Kill();
        var e = new PsEngineClient(Application.Current.Dispatcher);
        _ready = new TaskCompletionSource<bool>();
        e.Ready += () => _ready.TrySetResult(true);
        e.Exited += reason =>
        {
            _ready?.TrySetException(new InvalidOperationException(reason));
            _done?.TrySetResult((false, reason));
        };
        e.Output += (kind, text, _, newLine) =>
        {
            if (kind == "err") _out.Append("ОШИБКА: ");
            _out.Append(text);
            if (newLine) _out.Append('\n');
        };
        e.Prompt += OnPrompt;
        e.Done += (_, ok, note) => _done?.TrySetResult((ok, note));
        _engine = e;
        e.Start();
        if (await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(60))) != _ready.Task)
            throw new TimeoutException("Движок PowerShell не запустился за 60 с.");
        await _ready.Task;
        e.SetWidth(220);
        return e;
    }

    /// <summary>Учётные данные vCenter: из памяти, иначе окно (и запомнить).</summary>
    private void OnPrompt(PsMsg m)
    {
        var target = m.Text ?? "";
        if (m.Kind == "credential" && target.StartsWith("vcenter:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Credentials.TryGetValue(target, out var c))
            {
                var r = PsPrompts.Ask(Owner?.Invoke(), m);
                if (r is not { Count: 2 })
                {
                    _engine?.Reply(m.Id, null);
                    return;
                }

                c = (r[0], r[1]);
                Credentials[target] = c;
            }

            _engine?.Reply(m.Id, new[] { c.User, c.Password });
            return;
        }

        _engine?.Reply(m.Id, PsPrompts.Ask(Owner?.Invoke(), m));
    }

    public async Task<List<HistoryEntry>> RunAsync(IReadOnlyList<ActionDefinition> actions, VCenterProfile vc,
        IReadOnlyDictionary<string, string> args, bool apply, IProgress<PlanProgress> progress, CancellationToken ct)
    {
        var mode = apply ? "apply" : "dry-run";
        var history = new List<HistoryEntry>();
        var host = vc.Title;
        foreach (var action in actions)
        {
            ct.ThrowIfCancellationRequested();
            var title = $"{host} · {action.Name}";
            if (action.Destructive && !apply)
            {
                var plan = PlanRunner.MakeRow("DRY-RUN", title, "изменяющее действие — выполнится только в «Применить»", action.Script, ok: true);
                progress.Report(new PlanProgress { Row = plan });
                history.Add(PlanRunner.ToHistory(mode, host, action.Name, plan));
                continue;
            }

            progress.Report(new PlanProgress { Row = PlanRunner.MakeRow("RUN", title, "выполняется…", "", ok: true), Status = title });
            ResultRow row;
            try
            {
                var (ok, note, output) = await RunOne(action, vc, args, ct);
                var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                // FAIL — в превью причина (последняя ошибка), OK — итоговая строка скрипта.
                var last = (!ok ? lines.LastOrDefault(l => l.StartsWith("ОШИБКА:")) : null) ?? lines.LastOrDefault() ?? "";
                if (!ok && output.Contains("LINUXOPS-VC-CONNECT")) Credentials.Remove("vcenter:" + vc.Server.Trim());
                var preview = note is { Length: > 0 } ? note : last.Replace("LINUXOPS-VC-CONNECT: ", "").Replace("ОШИБКА: ", "");
                row = PlanRunner.MakeRow(ok ? "OK" : "FAIL", title, preview, output.Replace("LINUXOPS-VC-CONNECT: ", ""), ok);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                row = PlanRunner.MakeRow("FAIL", title, ex.Message, ex.ToString(), ok: false);
            }

            progress.Report(new PlanProgress { Row = row });
            history.Add(PlanRunner.ToHistory(mode, host, action.Name, row));
        }

        return history;
    }

    private async Task<(bool Ok, string? Note, string Output)> RunOne(ActionDefinition action, VCenterProfile vc,
        IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var engine = await Engine();
        _out.Clear();
        _done = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (engine.Run(BuildScript(action, vc, args)) == 0) throw new InvalidOperationException("Движок PowerShell занят.");
        var timeout = TimeSpan.FromSeconds(action.TimeoutSeconds > 0 ? action.TimeoutSeconds : 300);
        using var stop = ct.Register(() => engine.Stop());
        var finished = await Task.WhenAny(_done.Task, Task.Delay(timeout, CancellationToken.None));
        if (finished != _done.Task)
        {
            engine.Stop();
            if (await Task.WhenAny(_done.Task, Task.Delay(5000)) != _done.Task) engine.Kill(); // завис в нативном вызове
            return (false, $"таймаут {timeout.TotalSeconds:0} с", _out.ToString());
        }

        var (ok, note) = await _done.Task;
        ct.ThrowIfCancellationRequested();
        return (ok, note, _out.ToString());
    }

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Подключение к vCenter (если ещё нет), параметры — переменными, затем скрипт действия.</summary>
    public static string BuildScript(ActionDefinition action, VCenterProfile vc, IReadOnlyDictionary<string, string> args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("if (-not (Get-Module -ListAvailable -Name VMware.VimAutomation.Core)) { throw 'PowerCLI не установлен: вкладка PowerShell → «vSphere…» → «Скачать из интернета» или «Установить из ZIP».' }");
        // Баннер CEIP PowerCLI идёт через Write-Host — в результатах действия он не нужен.
        sb.AppendLine("Import-Module VMware.VimAutomation.Core *> $null");
        sb.AppendLine($"Set-PowerCLIConfiguration -Scope Session -ParticipateInCeip $false -InvalidCertificateAction {(vc.IgnoreCert ? "Ignore" : "Fail")} -Confirm:$false *> $null");
        sb.AppendLine($"$__srv = {Q(vc.Server.Trim())}");
        sb.AppendLine("$__vc = @($global:DefaultVIServers) | Where-Object { $_.Name -eq $__srv -and $_.IsConnected } | Select-Object -First 1");
        sb.AppendLine("if (-not $__vc) {");
        sb.AppendLine($"  $__cred = $Host.UI.PromptForCredential('vCenter ' + $__srv, 'Учётная запись vCenter', {Q(vc.User.Trim())}, 'vcenter:' + $__srv)");
        sb.AppendLine("  if (-not $__cred) { throw \"LINUXOPS-VC-CONNECT: вход в vCenter $__srv отменён\" }");
        sb.AppendLine("  try { $__vc = Connect-VIServer -Server $__srv -Credential $__cred -ErrorAction Stop }");
        // Сообщение PowerCLI: «дата<TAB>Connect-VIServer<TAB><TAB>суть» — оставляем суть.
        sb.AppendLine("  catch { $__m = @($_.Exception.Message -split \"`t\" | Where-Object { $_.Trim() }) | Select-Object -Last 1; throw \"LINUXOPS-VC-CONNECT: не удалось подключиться к $__srv — $__m\" }");
        sb.AppendLine("}");
        sb.AppendLine("$PSDefaultParameterValues['*:Server'] = $__vc");
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        foreach (var p in action.Parameters)
        {
            var value = args.TryGetValue(action.Id + ":" + p.Name, out var v) ? v : p.Default;
            sb.AppendLine($"${p.Name} = {Q(value)}");
        }

        sb.AppendLine("try {");
        sb.AppendLine(action.Script);
        sb.AppendLine("} finally { $PSDefaultParameterValues.Remove('*:Server') }");
        return sb.ToString();
    }

    public void Dispose() => _engine?.Dispose();
}
