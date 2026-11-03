using System.Management;
using System.Text.RegularExpressions;
using AdServerGroups.Abstractions;
using Microsoft.Data.SqlClient;

namespace AdServerGroups.SqlProvider;

/// <summary>
/// Точка входа для загрузчика: public class с конструктором без параметров, реализует ISqlSysadminProvider.
/// </summary>
public sealed class SqlSysadminProvider : ISqlSysadminProvider
{
    public string Id => "sql-sysadmin";
    public string DisplayName => "SQL Server sysadmin (Windows Auth)";

    public SqlDiscoveryResult Discover(string computerName, string? dnsHostName)
    {
        var host = string.IsNullOrWhiteSpace(dnsHostName) ? computerName : dnsHostName.Trim().TrimEnd('.');
        try
        {
            var instances = DiscoverInstancesWmi(host);
            if (instances.Count == 0)
            {
                return new SqlDiscoveryResult
                {
                    ComputerName = computerName,
                    SqlFound = false,
                    Access = "—",
                    Detail = "Службы MSSQL* не найдены"
                };
            }

            // Лёгкая проверка Windows Auth к первому инстансу (без изменений).
            var probe = ProbeSysadminAccess(host, instances[0]);
            return new SqlDiscoveryResult
            {
                ComputerName = computerName,
                SqlFound = true,
                Instances = instances,
                Access = probe.ok ? "OK" : "Access denied",
                Detail = probe.detail
            };
        }
        catch (Exception ex)
        {
            return new SqlDiscoveryResult
            {
                ComputerName = computerName,
                SqlFound = false,
                Access = "ошибка",
                Detail = ex.GetBaseException().Message
            };
        }
    }

    public IReadOnlyList<string> ListWindowsSysadminPrincipals(SqlTarget target)
    {
        var host = string.IsNullOrWhiteSpace(target.DnsHostName) ? target.ComputerName : target.DnsHostName;
        var server = BuildServerName(host, target.Instance);
        using var conn = Open(server);
        using var cmd = conn.CreateCommand();
        // U = Windows login, G = Windows group — только они переносятся в AD.
        cmd.CommandText =
            """
            SELECT pe.name
            FROM sys.server_role_members rm
            INNER JOIN sys.server_principals r ON rm.role_principal_id = r.principal_id
            INNER JOIN sys.server_principals pe ON rm.member_principal_id = pe.principal_id
            WHERE r.name = N'sysadmin'
              AND pe.type IN (N'U', N'G')
            ORDER BY pe.name
            """;
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.IsDBNull(0) ? null : r.GetString(0);
            if (!string.IsNullOrWhiteSpace(name))
                list.Add(name);
        }

        return list;
    }

    public IReadOnlyList<SqlPlanLine> BuildPlan(
        SqlTarget target,
        string windowsGroupSam,
        string domainDns,
        bool apply)
    {
        var lines = new List<SqlPlanLine>();
        var host = string.IsNullOrWhiteSpace(target.DnsHostName) ? target.ComputerName : target.DnsHostName;
        var server = BuildServerName(host, target.Instance);
        // SQL CREATE LOGIN FROM WINDOWS обычно ждёт NetBIOS\group (HCI\…), не DNS (hci.x\…).
        // domainDns сюда уже приходит как NetBIOS (или DNS — тогда берём первый лейбл).
        var domainPart = ToNetBiosDomain(domainDns);
        var login = $"{domainPart}\\{windowsGroupSam}";

        try
        {
            using var conn = Open(server);
            var existingLogin = FindExistingWindowsLogin(conn, domainPart, domainDns, windowsGroupSam);
            var loginName = existingLogin ?? login;
            var hasLogin = existingLogin is not null;
            var isSysadmin = hasLogin && IsSysadmin(conn, loginName);

            if (isSysadmin)
            {
                lines.Add(new SqlPlanLine
                {
                    Kind = "SKIP",
                    Text = $"[SQL] SKIP  {loginName} уже sysadmin на {server}"
                });
                return lines;
            }

            if (!hasLogin)
            {
                lines.Add(new SqlPlanLine
                {
                    Kind = "ENSURE_LOGIN",
                    Text = $"[SQL] ENSURE LOGIN [{login}] ON {server}"
                });
                if (apply)
                {
                    EnsureWindowsLogin(conn, login);
                    loginName = login;
                    lines[^1] = new SqlPlanLine { Kind = "ENSURE_LOGIN", Text = lines[^1].Text + "  OK" };
                }
            }
            else
            {
                lines.Add(new SqlPlanLine
                {
                    Kind = "INFO",
                    Text = $"[SQL] LOGIN [{loginName}] уже есть на {server}"
                });
            }

            lines.Add(new SqlPlanLine
            {
                Kind = "ADD_SYSADMIN",
                Text = $"[SQL] ADD    [{loginName}] → sysadmin @ {server}"
            });
            if (apply)
            {
                AddSysadmin(conn, loginName);
                lines[^1] = new SqlPlanLine { Kind = "ADD_SYSADMIN", Text = lines[^1].Text + "  OK" };
            }
        }
        catch (Exception ex)
        {
            lines.Add(new SqlPlanLine
            {
                Kind = "ERR",
                Text = $"[SQL] ERR   {server}: {ex.GetBaseException().Message}"
            });
        }

        return lines;
    }

    private static IReadOnlyList<string> DiscoverInstancesWmi(string host)
    {
        var options = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
            Timeout = TimeSpan.FromSeconds(8)
        };
        var scope = new ManagementScope($@"\\{host}\root\cimv2", options);
        scope.Connect();

        var q = new ObjectQuery(
            "SELECT Name, State FROM Win32_Service WHERE Name LIKE 'MSSQL%'");
        using var searcher = new ManagementObjectSearcher(scope, q);
        using var results = searcher.Get();

        var list = new List<string>();
        foreach (ManagementBaseObject row in results)
        {
            using (row)
            {
                var name = row["Name"]?.ToString() ?? string.Empty;
                if (string.Equals(name, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add("MSSQLSERVER");
                    continue;
                }

                // MSSQL$INSTANCE
                var m = Regex.Match(name, @"^MSSQL\$(?<inst>.+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                    list.Add(m.Groups["inst"].Value);
            }
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (bool ok, string detail) ProbeSysadminAccess(string host, string instance)
    {
        try
        {
            var server = BuildServerName(host, instance);
            using var conn = Open(server);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SUSER_SNAME(), IS_SRVROLEMEMBER('sysadmin')";
            using var r = cmd.ExecuteReader();
            r.Read();
            var who = r.GetString(0);
            var sa = Convert.ToInt32(r.GetValue(1)) == 1;
            return (true, $"Windows Auth OK как {who}; sysadmin={(sa ? "да" : "нет")}");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private static string BuildServerName(string host, string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) ||
            string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            return host;
        return $"{host}\\{instance}";
    }

    private static SqlConnection Open(string server)
    {
        var cs = new SqlConnectionStringBuilder
        {
            DataSource = server,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 8,
            ApplicationName = "AdServerGroups.SqlProvider"
        };
        var conn = new SqlConnection(cs.ConnectionString);
        conn.Open();
        return conn;
    }

    private static string ToNetBiosDomain(string domainDnsOrNetBios)
    {
        var s = (domainDnsOrNetBios ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(s))
            return s;
        if (!s.Contains('.'))
            return s;
        return s.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private static string? FindExistingWindowsLogin(
        SqlConnection conn,
        string netBios,
        string domainDns,
        string sam)
    {
        foreach (var candidate in new[]
                 {
                     $"{netBios}\\{sam}",
                     string.IsNullOrWhiteSpace(domainDns) ? null : $"{domainDns}\\{sam}"
                 })
        {
            if (candidate is null || !LoginExists(conn, candidate))
                continue;
            return candidate;
        }

        return null;
    }

    private static bool LoginExists(SqlConnection conn, string login)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sys.server_principals WHERE name = @n";
        cmd.Parameters.AddWithValue("@n", login);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool IsSysadmin(SqlConnection conn, string login)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT IS_SRVROLEMEMBER('sysadmin', @n)";
        cmd.Parameters.AddWithValue("@n", login);
        var v = cmd.ExecuteScalar();
        return v is not null and not DBNull && Convert.ToInt32(v) == 1;
    }

    private static void EnsureWindowsLogin(SqlConnection conn, string login)
    {
        using var cmd = conn.CreateCommand();
        // login already validated as DOMAIN\group from our templates
        cmd.CommandText = $"CREATE LOGIN [{login.Replace("]", "]]")}] FROM WINDOWS";
        cmd.ExecuteNonQuery();
    }

    private static void AddSysadmin(SqlConnection conn, string login)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER SERVER ROLE sysadmin ADD MEMBER [{login.Replace("]", "]]")}]";
        cmd.ExecuteNonQuery();
    }
}
