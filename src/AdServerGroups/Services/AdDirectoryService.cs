using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using AdServerGroups.Models;

namespace AdServerGroups.Services;

public sealed class AdDirectoryService : IDisposable
{
    private readonly AppSettings _settings;
    private readonly SessionCredentials _credentials;

    public AdDirectoryService(AppSettings settings, SessionCredentials credentials)
    {
        _settings = settings;
        _credentials = credentials;
    }

    public string DomainDns => _settings.DomainDns;
    public string RootDn => BuildRootDn(_settings.DomainDns);

    public void TestConnection()
    {
        using var root = Bind(RootDn);
        _ = root.NativeObject;
    }

    public OuNode LoadOuTree(string? startDn = null)
    {
        var dn = string.IsNullOrWhiteSpace(startDn) ? RootDn : startDn;
        using var entry = Bind(dn);
        var root = ToOuNode(entry);
        LoadChildren(entry, root, depth: 0, maxDepth: 8);
        return root;
    }

    public IReadOnlyList<AdComputerItem> FindWindowsServers(string containerDn)
    {
        using var searchRoot = Bind(containerDn);
        using var searcher = new DirectorySearcher(searchRoot)
        {
            Filter = "(&(objectCategory=computer)(objectClass=computer))",
            SearchScope = _settings.IncludeNestedContainers ? SearchScope.Subtree : SearchScope.OneLevel,
            PageSize = 500
        };
        searcher.PropertiesToLoad.AddRange(new[]
        {
            "cn", "distinguishedName", "operatingSystem", "dNSHostName"
        });

        var needle = _settings.OperatingSystemContains ?? string.Empty;
        var list = new List<AdComputerItem>();

        using var results = searcher.FindAll();
        foreach (SearchResult result in results)
        {
            var os = GetString(result, "operatingSystem");
            if (!string.IsNullOrWhiteSpace(needle) &&
                os.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var dn = GetString(result, "distinguishedName");
            list.Add(new AdComputerItem
            {
                Name = GetString(result, "cn"),
                DistinguishedName = dn,
                Location = DnDisplay.ParentFriendly(dn),
                OperatingSystem = os,
                DnsHostName = GetString(result, "dNSHostName"),
                IsSelected = false
            });
        }

        return list.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool GroupExists(string groupName)
    {
        using var ctx = CreatePrincipalContext();
        using var group = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName);
        return group is not null;
    }

    public void EnsureGroup(string groupName, string description)
    {
        using var ctx = CreatePrincipalContext(ContextType.Domain, _settings.GroupsOuDn);
        using var existing = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName);
        if (existing is not null)
        {
            // Обновляем описание, если группа уже была создана со старым шаблоном (LA).
            if (!string.IsNullOrWhiteSpace(description) &&
                !string.Equals(existing.Description, description, StringComparison.Ordinal))
            {
                existing.Description = description;
                existing.Save();
            }

            return;
        }

        using var group = new GroupPrincipal(ctx)
        {
            Name = groupName,
            SamAccountName = groupName,
            Description = description,
            IsSecurityGroup = true,
            GroupScope = GroupScope.Local
        };
        group.Save();
    }

    public void AddMembersToGroup(string groupName, IEnumerable<string> memberSamOrDns)
    {
        using var ctx = CreatePrincipalContext();
        using var group = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName)
            ?? throw new InvalidOperationException($"Группа {groupName} не найдена.");

        // Principal нельзя Dispose до group.Save() — иначе «Cannot access a disposed object (UserPrincipal)».
        var held = new List<Principal>();
        try
        {
            var changed = false;
            foreach (var memberId in memberSamOrDns.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Только точный sAMAccountName — иначе ADSI показывает «Multiple Names Found».
                var sam = StripDomain(memberId);
                if (string.IsNullOrWhiteSpace(sam) || sam.EndsWith('$'))
                    continue;

                var principal = Principal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
                if (principal is null)
                    continue;

                held.Add(principal);

                if (IsMemberBySid(group, principal))
                    continue;

                group.Members.Add(principal);
                changed = true;
            }

            if (changed)
                group.Save();
        }
        finally
        {
            foreach (var p in held)
                p.Dispose();
        }
    }

    public bool IsMemberOfGroup(string groupName, string memberSamOrDns)
    {
        using var ctx = CreatePrincipalContext();
        using var group = GroupPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, groupName);
        if (group is null)
            return false;

        var sam = StripDomain(memberSamOrDns);
        if (string.IsNullOrWhiteSpace(sam))
            return false;

        using var principal = Principal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);
        if (principal is null)
            return false;

        return IsMemberBySid(group, principal);
    }

    private static bool IsMemberBySid(GroupPrincipal group, Principal principal)
    {
        var sid = principal.Sid?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            return false;

        foreach (var member in group.GetMembers())
        {
            try
            {
                if (string.Equals(member.Sid?.Value, sid, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            finally
            {
                member.Dispose();
            }
        }

        return false;
    }

    private static string StripDomain(string name)
    {
        var s = (name ?? string.Empty).Trim();
        var slash = s.LastIndexOf('\\');
        return slash >= 0 && slash < s.Length - 1 ? s[(slash + 1)..] : s;
    }

    /// <summary>
    /// Распаковка члена Administrators для переноса в целевую группу.
    /// Группы разворачиваются рекурсивно; защищённые — пропускаются целиком.
    /// </summary>
    public IReadOnlyList<MigrationLeaf> ExpandForMigration(
        string memberName,
        Func<string, bool> isProtected,
        int maxDepth = 8)
    {
        var leaves = new List<MigrationLeaf>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExpandCore(memberName, sourceGroup: null, depth: 0, isProtected, maxDepth, visited, leaves);
        return leaves
            .GroupBy(l => l.Member, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(l => l.Member, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ExpandCore(
        string memberName,
        string? sourceGroup,
        int depth,
        Func<string, bool> isProtected,
        int maxDepth,
        HashSet<string> visited,
        List<MigrationLeaf> leaves)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            return;
        if (!visited.Add(memberName))
            return;
        if (isProtected(memberName))
            return;

        using var ctx = CreatePrincipalContext();
        // Точный sAMAccountName — без диалога «Multiple Names Found».
        var sam = StripDomain(memberName);
        using var principal = string.IsNullOrWhiteSpace(sam)
            ? null
            : Principal.FindByIdentity(ctx, IdentityType.SamAccountName, sam);

        if (principal is null)
        {
            // Локальный или неразрешённый — переносим как есть (если AD его не знает, Add позже просто пропустит/упадёт).
            if (!sam.EndsWith('$'))
                leaves.Add(new MigrationLeaf(memberName, sourceGroup));
            return;
        }

        if (principal is ComputerPrincipal)
            return; // компьютерные учётки (*$) в целевую группу не переносим

        if (principal is UserPrincipal)
        {
            leaves.Add(new MigrationLeaf(FormatPrincipal(principal), sourceGroup));
            return;
        }

        if (principal is GroupPrincipal group)
        {
            if (!_settings.ExpandNestedGroupsOnMigrate)
            {
                leaves.Add(new MigrationLeaf(FormatPrincipal(principal), sourceGroup));
                return;
            }

            if (depth >= maxDepth)
                return;

            var unpackedFrom = FormatPrincipal(principal);
            foreach (var child in group.GetMembers())
            {
                try
                {
                    ExpandCore(
                        FormatPrincipal(child),
                        sourceGroup: unpackedFrom,
                        depth: depth + 1,
                        isProtected,
                        maxDepth,
                        visited,
                        leaves);
                }
                finally
                {
                    child.Dispose();
                }
            }
        }
    }

    private static string FormatPrincipal(Principal principal)
    {
        if (!string.IsNullOrWhiteSpace(principal.SamAccountName) &&
            !string.IsNullOrWhiteSpace(principal.Context?.Name))
        {
            // Context.Name для домена часто DNS; для отображения/поиска хватает sAMAccountName.
            return principal.SamAccountName!;
        }

        return principal.SamAccountName
               ?? principal.Name
               ?? principal.DistinguishedName
               ?? string.Empty;
    }

    public DirectoryEntry Bind(string dn)
    {
        var path = $"LDAP://{_settings.LdapHost}/{dn}";
        if (!_credentials.HasExplicitCredentials)
            return new DirectoryEntry(path); // текущий Windows-токен

        return new DirectoryEntry(path, _credentials.Username, _credentials.Password);
    }

    public PrincipalContext CreatePrincipalContext(ContextType type = ContextType.Domain, string? container = null)
    {
        // Явный DC: DNS SRV у рабочей станции может смотреть не в AD.
        var server = string.IsNullOrWhiteSpace(_settings.LdapHost)
            ? _settings.DomainDns
            : _settings.LdapHost;
        var ctxContainer = container ?? RootDn;

        if (!_credentials.HasExplicitCredentials)
            return new PrincipalContext(type, server, ctxContainer);

        return new PrincipalContext(
            type,
            server,
            ctxContainer,
            ContextOptions.SimpleBind,
            _credentials.Username,
            _credentials.Password);
    }

    private void LoadChildren(DirectoryEntry parent, OuNode node, int depth, int maxDepth)
    {
        if (depth >= maxDepth)
            return;

        foreach (DirectoryEntry child in parent.Children)
        {
            using (child)
            {
                if (!string.Equals(child.SchemaClassName, "organizationalUnit", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(child.SchemaClassName, "container", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var childNode = ToOuNode(child);
                node.Children.Add(childNode);

                if (string.Equals(child.SchemaClassName, "organizationalUnit", StringComparison.OrdinalIgnoreCase))
                {
                    LoadChildren(child, childNode, depth + 1, maxDepth);
                }
            }
        }
    }

    private OuNode ToOuNode(DirectoryEntry entry)
    {
        var dn = entry.Properties["distinguishedName"]?.Count > 0
            ? entry.Properties["distinguishedName"][0]?.ToString() ?? entry.Path
            : entry.Path;

        string? name = null;
        if (string.Equals(entry.SchemaClassName, "domainDNS", StringComparison.OrdinalIgnoreCase))
            name = _settings.DomainDns;
        else if (entry.Properties["ou"]?.Count > 0)
            name = entry.Properties["ou"][0]?.ToString();
        else if (entry.Properties["cn"]?.Count > 0)
            name = entry.Properties["cn"][0]?.ToString();
        else if (!string.IsNullOrWhiteSpace(dn))
            name = DnDisplay.ToFriendly(dn);

        return new OuNode
        {
            Name = name ?? dn,
            DistinguishedName = dn
        };
    }

    private static string GetString(SearchResult result, string property)
    {
        if (!result.Properties.Contains(property) || result.Properties[property].Count == 0)
            return string.Empty;
        return result.Properties[property][0]?.ToString() ?? string.Empty;
    }

    private static string BuildRootDn(string domainDns)
    {
        var parts = domainDns.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(",", parts.Select(p => $"DC={p}"));
    }

    public void Dispose()
    {
        // nothing pooled yet
    }
}
