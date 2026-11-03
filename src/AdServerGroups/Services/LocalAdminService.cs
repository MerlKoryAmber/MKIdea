using System.Collections;
using System.DirectoryServices;
using System.Management;
using System.Text.RegularExpressions;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public sealed class LocalAdminService
{
    private readonly AppSettings _settings;
    private readonly SessionCredentials _credentials;

    private static readonly Regex WmiNameRegex = new(
        @"Domain=""(?<domain>[^""]+)"",Name=""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public LocalAdminService(AppSettings settings, SessionCredentials credentials)
    {
        _settings = settings;
        _credentials = credentials;
    }

    public IReadOnlyList<string> GetAdministrators(string computerName)
    {
        try
        {
            RemoteTimeout.EnsureReachable(computerName);
            return RemoteTimeout.Run(
                $"Чтение Administrators на {computerName}",
                () => GetAdministratorsCore(computerName));
        }
        catch (Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public void AddDomainGroup(string computerName, string groupSamAccountName)
    {
        try
        {
            RemoteTimeout.EnsureReachable(computerName);
            RemoteTimeout.Run(
                $"Добавление группы на {computerName}",
                () => AddDomainGroupCore(computerName, groupSamAccountName));
        }
        catch (Exception ex)
        {
            throw Wrap(ex);
        }
    }

    public void RemoveMember(string computerName, string memberName)
    {
        try
        {
            RemoteTimeout.EnsureReachable(computerName);
            RemoteTimeout.Run(
                $"Удаление члена на {computerName}",
                () => RemoveMemberCore(computerName, memberName));
        }
        catch (Exception ex)
        {
            throw Wrap(ex);
        }
    }

    private static Exception Wrap(Exception ex) =>
        new InvalidOperationException(RemoteErrorHints.Annotate(ex), ex);

    private IReadOnlyList<string> GetAdministratorsCore(string computerName)
    {
        // С workgroup-ПК WinNT://…Members часто даёт пустой список даже под Domain Admin.
        // WMI Win32_GroupUser с теми же учётками работает стабильно.
        Exception? wmiError = null;
        try
        {
            var viaWmi = GetAdministratorsWmi(computerName);
            if (viaWmi.Count > 0)
                return viaWmi;
        }
        catch (Exception ex)
        {
            wmiError = ex;
        }

        try
        {
            var viaWinNt = GetAdministratorsWinNt(computerName);
            if (viaWinNt.Count > 0)
                return viaWinNt;
        }
        catch (Exception ex)
        {
            if (wmiError is not null)
            {
                throw new InvalidOperationException(
                    $"WMI: {wmiError.Message}; WinNT: {ex.Message}", ex);
            }

            throw;
        }

        if (wmiError is not null)
            throw new InvalidOperationException($"Не удалось прочитать Administrators на {computerName}: {wmiError.Message}", wmiError);

        return Array.Empty<string>();
    }

    private IReadOnlyList<string> GetAdministratorsWmi(string computerName)
    {
        var scope = CreateWmiScope(computerName);
        scope.Connect();

        // PartComponent ссылается на локальных/доменных UserAccount или Group.
        var query = new ObjectQuery(
            "SELECT GroupComponent, PartComponent FROM Win32_GroupUser");
        using var searcher = new ManagementObjectSearcher(scope, query);
        using var results = searcher.Get();

        var members = new List<string>();
        foreach (ManagementBaseObject row in results)
        {
            using (row)
            {
                var groupPath = row["GroupComponent"]?.ToString() ?? string.Empty;
                if (!groupPath.Contains("Name=\"Administrators\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                var part = row["PartComponent"]?.ToString() ?? string.Empty;
                var display = FormatWmiPrincipal(part, computerName);
                if (!string.IsNullOrWhiteSpace(display))
                    members.Add(display);
            }
        }

        return members
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ManagementScope CreateWmiScope(string computerName)
    {
        var options = new ConnectionOptions
        {
            Impersonation = ImpersonationLevel.Impersonate,
            Authentication = AuthenticationLevel.PacketPrivacy,
            EnablePrivileges = true,
            Timeout = RemoteTimeout.DefaultOperation
        };

        if (_credentials.HasExplicitCredentials)
        {
            options.Username = _credentials.Username;
            options.Password = _credentials.Password;
        }

        return new ManagementScope($@"\\{computerName}\root\cimv2", options);
    }

    private static string FormatWmiPrincipal(string partComponent, string computerName)
    {
        var match = WmiNameRegex.Match(partComponent);
        if (!match.Success)
            return string.Empty;

        var domain = match.Groups["domain"].Value;
        var name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        // Локальные учётки/группы машины — без доменного префикса, как в GUI «Local Users and Groups».
        if (string.Equals(domain, computerName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(domain, StripDnsSuffix(computerName), StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return $"{domain}\\{name}";
    }

    private static string StripDnsSuffix(string host)
    {
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }

    private IReadOnlyList<string> GetAdministratorsWinNt(string computerName)
    {
        using var group = BindLocalGroup(computerName);
        var members = new List<string>();
        var raw = group.Invoke("Members") as IEnumerable
            ?? throw new InvalidOperationException($"Не удалось прочитать Administrators на {computerName}.");

        foreach (object? member in raw)
        {
            if (member is null) continue;
            using var entry = new DirectoryEntry(member);
            var name = entry.Name;
            if (string.IsNullOrWhiteSpace(name) && entry.Properties["Name"].Count > 0)
                name = entry.Properties["Name"][0]?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                members.Add(name!);
        }

        return members
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddDomainGroupCore(string computerName, string groupSamAccountName)
    {
        using var localAdmins = BindLocalGroup(computerName);
        var adsPath = $"WinNT://{_settings.DomainDns}/{groupSamAccountName}";
        try
        {
            localAdmins.Invoke("Add", adsPath);
        }
        catch (Exception ex) when (IsAlreadyMember(ex))
        {
            // already present
        }
    }

    private void RemoveMemberCore(string computerName, string memberName)
    {
        using var localAdmins = BindLocalGroup(computerName);
        var candidates = new[]
        {
            $"WinNT://{_settings.DomainDns}/{StripDomain(memberName)}",
            $"WinNT://{StripDnsSuffix(computerName)}/{StripDomain(memberName)}",
            $"WinNT://{computerName}/{StripDomain(memberName)}",
            memberName
        };

        Exception? last = null;
        foreach (var path in candidates)
        {
            try
            {
                localAdmins.Invoke("Remove", path);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException(
            $"Не удалось удалить '{memberName}' из Administrators на {computerName}.", last);
    }

    /// <summary>Всегда защищены, даже если выкинули из AlwaysKeepMembers в настройках.</summary>
    private static readonly string[] BuiltInKeep =
    {
        "Administrator",
        "Administrators",
        "Domain Admins",
        "Enterprise Admins",
        "Exchange Trusted Subsystem",
        "Organization Management",
        "Exchange Servers",
        "Exchange Windows Permissions",
    };

    public bool IsProtectedMember(string memberName)
    {
        var shortName = StripDomain(memberName);

        foreach (var item in BuiltInKeep)
        {
            if (NamesMatch(shortName, memberName, item))
                return true;
        }

        var keep = (_settings.AlwaysKeepMembers ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var item in keep)
        {
            if (NamesMatch(shortName, memberName, item))
                return true;
        }

        if (shortName.Contains("Exchange", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool NamesMatch(string shortName, string fullName, string keepItem)
    {
        var keepShort = StripDomain(keepItem);
        return string.Equals(shortName, keepShort, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullName, keepItem, StringComparison.OrdinalIgnoreCase);
    }

    private DirectoryEntry BindLocalGroup(string computerName)
    {
        var path = $"WinNT://{computerName}/Administrators,group";
        if (!_credentials.HasExplicitCredentials)
            return new DirectoryEntry(path);

        return new DirectoryEntry(path, _credentials.Username, _credentials.Password);
    }

    private static string StripDomain(string name)
    {
        var s = name.Trim();
        var slash = s.LastIndexOf('\\');
        if (slash >= 0 && slash < s.Length - 1)
            return s[(slash + 1)..];
        var at = s.IndexOf('@');
        if (at > 0)
            return s[..at];
        return s;
    }

    private static bool IsAlreadyMember(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("already a member", StringComparison.OrdinalIgnoreCase)
               || text.Contains("0x80070562", StringComparison.OrdinalIgnoreCase)
               || text.Contains("1378", StringComparison.OrdinalIgnoreCase);
    }
}
