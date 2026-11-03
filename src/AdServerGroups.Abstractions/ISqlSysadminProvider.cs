namespace AdServerGroups.Abstractions;

/// <summary>
/// Опциональный провайдер из папки Providers рядом с exe.
/// Если DLL нет — вкладка SQL в GUI скрыта.
/// </summary>
public interface ISqlSysadminProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Поиск SQL на хосте (службы/инстансы). Без изменения sysadmin.</summary>
    SqlDiscoveryResult Discover(string computerName, string? dnsHostName);

    /// <summary>Проверка/план: Windows-группа как login + sysadmin (dry-run или apply).</summary>
    IReadOnlyList<SqlPlanLine> BuildPlan(
        SqlTarget target,
        string windowsGroupSam,
        string domainDns,
        bool apply);

    /// <summary>
    /// Явные члены роли sysadmin с типом Windows login/group (имена как в SQL, обычно DOMAIN\sam).
    /// Без изменений на сервере. SQL auth / certificate и т.п. не включаются.
    /// </summary>
    IReadOnlyList<string> ListWindowsSysadminPrincipals(SqlTarget target);
}

public sealed class SqlDiscoveryResult
{
    public string ComputerName { get; init; } = string.Empty;
    public bool SqlFound { get; init; }
    public IReadOnlyList<string> Instances { get; init; } = Array.Empty<string>();
    public string Access { get; init; } = "—"; // OK / Access denied / ошибка
    public string Detail { get; init; } = string.Empty;
}

public sealed class SqlTarget
{
    public string ComputerName { get; init; } = string.Empty;
    public string DnsHostName { get; init; } = string.Empty;
    public string Instance { get; init; } = "MSSQLSERVER";
}

public sealed class SqlPlanLine
{
    public string Kind { get; init; } = "INFO"; // ENSURE_LOGIN / ADD_SYSADMIN / SKIP / ERR
    public string Text { get; init; } = string.Empty;
    public bool IsSkip => Kind is "SKIP" or "INFO";
}
