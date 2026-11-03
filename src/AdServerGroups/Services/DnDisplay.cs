namespace AdServerGroups.Services;

public static class DnDisplay
{
    /// <summary>
    /// OU=Prod,OU=Servers,OU=Infra,DC=Merl,DC=loc → merl.loc · Infra · Servers · Prod
    /// </summary>
    public static string ToFriendly(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
            return string.Empty;

        var names = new List<string>();
        var domainParts = new List<string>();

        foreach (var raw in dn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Trim();
            if (part.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
                names.Add(part[3..]);
            else if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                names.Add(part[3..]);
            else if (part.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                domainParts.Add(part[3..]);
        }

        names.Reverse();
        var domain = domainParts.Count > 0
            ? string.Join('.', domainParts)
            : null;

        if (domain is null)
            return string.Join(" · ", names);

        if (names.Count == 0)
            return domain;

        return domain + " · " + string.Join(" · ", names);
    }

    /// <summary>Родительский контейнер компьютера без CN=Имя.</summary>
    public static string ParentFriendly(string? objectDn)
    {
        if (string.IsNullOrWhiteSpace(objectDn))
            return string.Empty;

        var idx = objectDn.IndexOf(',');
        return idx > 0 ? ToFriendly(objectDn[(idx + 1)..]) : ToFriendly(objectDn);
    }
}
