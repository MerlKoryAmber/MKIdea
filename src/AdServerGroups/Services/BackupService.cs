using System.IO;
using System.Text;
using System.Text.Json;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public sealed class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed record ServerBackup(string Computer, string DnsHostName, string DistinguishedName, IReadOnlyList<string> Administrators);
    public sealed record ServerBackupError(string Computer, string Error);

    public string CreateLocalAdminsBackup(
        IEnumerable<AdComputerItem> computers,
        LocalAdminService local,
        string outputPath)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var entries = new List<ServerBackup>();
        var errors = new List<ServerBackupError>();

        foreach (var computer in computers.Where(c => c.IsSelected))
        {
            try
            {
                var members = local.GetAdministrators(computer.RemoteHost);
                entries.Add(new ServerBackup(
                    computer.Name,
                    computer.DnsHostName,
                    computer.DistinguishedName,
                    members));
            }
            catch (Exception ex)
            {
                errors.Add(new ServerBackupError(computer.Name, ex.Message));
            }
        }

        var payload = new
        {
            app = "AD Server Groups",
            kind = "local-administrators-backup",
            createdAt = stamp,
            servers = entries.Select(e => new
            {
                computer = e.Computer,
                dnsHostName = e.DnsHostName,
                distinguishedName = e.DistinguishedName,
                capturedAt = stamp,
                administrators = e.Administrators
            }),
            errors = errors.Select(e => new { computer = e.Computer, error = e.Error })
        };

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);

        var txtPath = Path.ChangeExtension(outputPath, ".txt");
        var sb = new StringBuilder();
        sb.AppendLine("AD Server Groups — бэкап локальных Administrators");
        sb.AppendLine($"Создан: {stamp}");
        sb.AppendLine();

        foreach (var e in entries)
        {
            sb.AppendLine($"=== {e.Computer} ===");
            foreach (var m in e.Administrators)
                sb.AppendLine($"  {m}");
            sb.AppendLine();
        }

        if (errors.Count > 0)
        {
            sb.AppendLine("=== Ошибки ===");
            foreach (var err in errors)
                sb.AppendLine($"  {err.Computer}: {err.Error}");
        }

        File.WriteAllText(txtPath, sb.ToString(), Encoding.UTF8);
        return outputPath;
    }
}
