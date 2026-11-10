using System.Management.Automation;
using System.Management.Automation.Runspaces;
using LinuxOps.Services;

namespace LinuxOps.PsHost;

/// <summary>Один runspace на весь сеанс (переменные, модули, подключения живут между запусками — как в ISE).</summary>
public sealed class PsEngine
{
    private readonly Channel _ch;
    private readonly Runspace _rs;
    private readonly object _lock = new();
    private PowerShell? _current;
    private volatile bool _stopRequested;

    public LoHost Host { get; }

    public PsEngine(Channel ch)
    {
        _ch = ch;
        // Папка Modules рядом с LinuxOps.exe (офлайн-установка PowerCLI) — первой в пути поиска.
        // Помощник лежит в pwsh\, его собственный pwsh\Modules — встроенные модули PowerShell ($PSHOME).
        var modules = UserModules;
        Directory.CreateDirectory(modules);
        var path = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        if (!path.Split(';').Contains(modules, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PSModulePath", modules + ";" + path);

        Host = new LoHost(ch);
        var iss = InitialSessionState.CreateDefault2();
        // Подписи модулей не проверяем: без интернета проверка отзыва сертификатов вешает Import-Module на минуты.
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        iss.ThreadOptions = PSThreadOptions.ReuseThread;
        _rs = RunspaceFactory.CreateRunspace(Host, iss);
        _rs.ApartmentState = ApartmentState.STA; // некоторые модули (диалоги, COM) требуют STA
        _rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = _rs;
        ps.AddScript("Set-Location ([Environment]::GetFolderPath('UserProfile'))").Invoke();
    }

    public static string UserModules => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Modules"));

    public string Version => PSVersionInfo.PSVersion.ToString();

    public string Location
    {
        get
        {
            try { return _rs.SessionStateProxy.Path.CurrentLocation.Path; }
            catch { return ""; }
        }
    }

    public void Start(int id, string script)
    {
        lock (_lock)
        {
            if (_current is not null)
            {
                _ch.Send(new PsMsg { T = "done", Id = id, Ok = false, Text = "движок занят", Location = Location });
                return;
            }

            _current = PowerShell.Create();
            _current.Runspace = _rs;
        }

        var ps = _current;
        var thread = new Thread(() => Run(id, ps, script)) { IsBackground = true, Name = "ps-run" };
        thread.Start();
    }

    private void Run(int id, PowerShell ps, string script)
    {
        var ok = false;
        string? note = null;
        try
        {
            Host.Errors = 0;
            ps.AddScript(script).AddCommand("Out-Default");
            // Только ошибки — в Out-Default; Write-Host, предупреждения, verbose идут в хост сами и сохраняют цвет.
            ps.Commands.Commands[0].MergeMyResults(PipelineResultTypes.Error, PipelineResultTypes.Output);
            ps.Invoke();
            ok = !ps.HadErrors && Host.Errors == 0;
        }
        catch (PipelineStoppedException)
        {
            note = "остановлено";
        }
        catch (RuntimeException ex)
        {
            Host.UI.WriteErrorLine(ex.ErrorRecord?.ToString() ?? ex.Message);
        }
        catch (Exception ex)
        {
            Host.UI.WriteErrorLine(ex.Message);
        }
        finally
        {
            // Остановленный конвейер не всегда бросает PipelineStoppedException — смотрим на флаг запроса.
            if (_stopRequested)
            {
                note = "остановлено";
                ok = false;
                _stopRequested = false;
                Host.UI.WriteLine("— остановлено —");
            }

            lock (_lock) _current = null;
            ps.Dispose();
            _ch.Send(new PsMsg { T = "done", Id = id, Ok = ok, Text = note, Location = Location });
        }
    }

    public void Stop()
    {
        PowerShell? ps;
        lock (_lock) ps = _current;
        if (ps is null) return;
        _stopRequested = true;
        try { ps.BeginStop(null, null); }
        catch { /* уже завершился */ }
    }
}
