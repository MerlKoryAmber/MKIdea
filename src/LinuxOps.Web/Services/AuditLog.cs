using System.Diagnostics;

namespace LinuxOps.Web.Services;

/// <summary>
/// Аудит: база (только дописывание) + строка в audit.log + на Linux копия в syslog (logger -t linuxops-web),
/// чтобы админ сервиса не мог стереть след. На стенде без входа вместо пользователя — адрес клиента.
/// </summary>
public sealed class AuditLog(WebStore store)
{
    private static readonly object FileGate = new();

    public void Write(string who, string what, string detail = "")
    {
        var at = DateTime.UtcNow;
        try { store.AppendAudit(at, who, what, detail); } catch (Exception ex) { LinuxOps.Services.CrashLog.Write("Аудит: база", ex); }
        var line = $"{at:O}\t{who}\t{what}" + (detail.Length > 0 ? "\t" + detail.Replace('\n', ' ').Replace('\r', ' ') : "");
        try { lock (FileGate) File.AppendAllText(WebPaths.AuditFile, line + Environment.NewLine); } catch { }
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new ProcessStartInfo("logger") { UseShellExecute = false };
                psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("linuxops-web"); psi.ArgumentList.Add("--");
                psi.ArgumentList.Add($"{who} {what}" + (detail.Length > 0 ? " — " + detail.Replace('\n', ' ') : ""));
                Process.Start(psi)?.Dispose();
            }
            catch { }
        }
    }
}
