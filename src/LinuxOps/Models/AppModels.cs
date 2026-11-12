using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace LinuxOps.Models;

/// <summary>Ресурс Inventory: хост Linux или vCenter. Общее для дерева с папками.</summary>
public interface IInventoryItem : INotifyPropertyChanged
{
    string Title { get; }
    string Subtitle { get; }
    string Display { get; }
    string IconLetter { get; }
    Brush IconBrush { get; }
    string IconTip { get; }
    string Status { get; }
    string StatusColor { get; }
    bool IsSelected { get; set; }
    /// <summary>Id папки (<see cref="HostFolder.Id"/>), пусто — корень.</summary>
    string FolderId { get; set; }
}

/// <summary>Папка Inventory. Вложенность — через <see cref="ParentId"/>; свёрнутость общая для всех вкладок.</summary>
public sealed class HostFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
}

public sealed class HostConfig : IInventoryItem
{
    private bool _isSelected = true;
    private string _status = "—";
    private string _distroHint = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    /// <summary>PrivateKey | Password | Key</summary>
    public string Auth { get; set; } = "PrivateKey";
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    /// <summary>Выполнять действия с привилегией sudo/root через sudo. Для root не используется.</summary>
    public bool UseSudo { get; set; } = true;
    public string FolderId { get; set; } = string.Empty;

    public string DistroHint
    {
        get => _distroHint;
        set
        {
            _distroHint = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DistroBadge));
            OnPropertyChanged(nameof(DistroLetter));
            OnPropertyChanged(nameof(DistroColorBrush));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>Online / Warning / — (UI; не в JSON).</summary>
    [JsonIgnore]
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColor)); }
    }

    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? Host : Name;
    [JsonIgnore] public string Subtitle => $"{Username}@{Host}" + (Port > 0 && Port != 22 ? $":{Port}" : "");
    [JsonIgnore] public string DistroBadge => DistroLabel.FromHint(DistroHint);
    [JsonIgnore] public string DistroLetter => DistroLabel.Letter(DistroHint);
    [JsonIgnore]
    public Brush DistroColorBrush
    {
        get
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(DistroLabel.ColorHex(DistroHint))!;
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
    }
    [JsonIgnore]
    public string StatusColor => Status switch
    {
        "Online" => "#2F9E6F",
        "Warning" => "#C4852C",
        "Offline" => "#C45C5C",
        _ => "#8A9AAB"
    };

    [JsonIgnore] public string Display => $"{Title} · {Subtitle}";
    [JsonIgnore] public string IconLetter => DistroLetter;
    [JsonIgnore] public Brush IconBrush => DistroColorBrush;
    [JsonIgnore] public string IconTip => DistroBadge;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name != nameof(DistroHint)) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconLetter)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconTip)));
    }
}

public sealed class AppDefaults
{
    public List<HostConfig> Hosts { get; set; } = new();
    /// <summary>Id действий, отмеченных в план в прошлой сессии.</summary>
    public List<string> SelectedActionIds { get; set; } = new();
    /// <summary>Программа для F4. Пусто — Notepad++ из комплекта, без него Блокнот.</summary>
    public string F4Editor { get; set; } = "";
    /// <summary>Тема: light, dark или system (как в Windows).</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Профили vCenter для PowerShell-действий. Пароль не хранится — спрашивается при подключении.</summary>
    public List<VCenterProfile> VCenters { get; set; } = new();
    /// <summary>Папки Inventory: хосты и vCenter ссылаются на них через FolderId.</summary>
    public List<HostFolder> Folders { get; set; } = new();
}

/// <summary>vCenter в Inventory. Пароль не хранится — спрашивается при подключении и живёт до закрытия программы.</summary>
public sealed class VCenterProfile : IInventoryItem
{
    private static readonly Brush VcBrush = Frozen("#3B7DD8");
    private bool _isSelected = true;
    private string _status = "—";

    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    /// <summary>Самоподписанный сертификат vCenter — не проверять.</summary>
    public bool IgnoreCert { get; set; }
    public string FolderId { get; set; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>Online / Offline — доступность порта 443 после «Опросить» (UI; не в JSON).</summary>
    [JsonIgnore]
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusColor)); }
    }

    [JsonIgnore] public string Title => string.IsNullOrWhiteSpace(Name) ? Server : Name;
    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(User) ? Server : $"{User} · {Server}";
    [JsonIgnore] public string Display => $"vCenter {Title} · {Server}";
    [JsonIgnore] public string IconLetter => "vC";
    [JsonIgnore] public Brush IconBrush => VcBrush;
    [JsonIgnore] public string IconTip => "vCenter";
    [JsonIgnore]
    public string StatusColor => Status switch
    {
        "Online" => "#2F9E6F",
        "Offline" => "#C45C5C",
        _ => "#8A9AAB"
    };

    public override string ToString() => Title;

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Параметр PowerShell-действия: спрашивается перед запуском, в скрипте — переменная $Name.</summary>
public sealed class ActionParam
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Default { get; set; } = string.Empty;
}

public sealed class ActionDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool BuiltIn { get; set; }
    public bool Destructive { get; set; }
    /// <summary>rhel, ubuntu, debian, alma, suse, any</summary>
    public List<string> Distros { get; set; } = new() { "any" };
    public string Script { get; set; } = string.Empty;
    public string Privilege { get; set; } = "sudo/root";
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>bash — по SSH на хостах (по умолчанию, и для старых файлов без поля); powershell — в движке PowerShell против vCenter.</summary>
    public string Kind { get; set; } = "bash";
    public List<ActionParam> Parameters { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPowerShell => string.Equals(Kind, "powershell", StringComparison.OrdinalIgnoreCase);
}

public sealed class DistroInfo
{
    public string Id { get; init; } = "unknown";
    public string Like { get; init; } = string.Empty;
    public string PrettyName { get; init; } = string.Empty;
    public string Family { get; init; } = "unknown";
}

public sealed class ActionResult
{
    public string HostName { get; set; } = string.Empty;
    public string ActionId { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public bool Skipped { get; set; }
    public bool DryRun { get; set; }
    public bool Ok => !Skipped && !DryRun && ExitCode == 0;
    public string Summary => DryRun ? "DRY-RUN" : Skipped ? "SKIP" : Ok ? "OK" : $"FAIL exit={ExitCode}";
}

public sealed class ResultRow : INotifyPropertyChanged
{
    private string _preview = "";

    public string Status { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>У строки RUN меняется по ходу: последняя строка вывода и время.</summary>
    public string Preview
    {
        get => _preview;
        set
        {
            if (_preview == value) return;
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Detail { get; init; } = "";
    public bool IsOk { get; init; }
    /// <summary>Разобранный аудит пользователей (list-users) — для табличного вида.</summary>
    public LinuxOps.Services.UserAuditReport? Audit { get; init; }
    /// <summary>Разобранная проверка прокси (proxy-check) — для табличного вида.</summary>
    public LinuxOps.Services.ProxyReport? Proxy { get; init; }
}

public sealed class ActionItemView : INotifyPropertyChanged
{
    private bool _isSelected;

    public required ActionDefinition Definition { get; init; }
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public string Title => Definition.Name;
    public string Description => string.IsNullOrWhiteSpace(Definition.Description)
        ? Definition.Id
        : Definition.Description;
    public string Badge => Definition.BuiltIn ? "Встроено" : "Своё";
    public ObservableCollection<string> DistroTags { get; } = new();

    public static ActionItemView From(ActionDefinition def, bool selected)
    {
        var v = new ActionItemView { Definition = def, IsSelected = selected };
        if (def.IsPowerShell) v.DistroTags.Add("PowerShell · vSphere");
        else
            foreach (var t in DistroLabel.TagsForAction(def.Distros))
                v.DistroTags.Add(t);
        return v;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class DistroLabel
{
    public static string FromHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return "—";
        var h = hint.Trim().ToLowerInvariant();
        return h switch
        {
            "ubuntu" => "Ubuntu",
            "debian" => "Debian",
            "rhel" or "redhat" => "RHEL",
            "centos" => "CentOS",
            "rocky" => "Rocky",
            "alma" or "almalinux" => "Alma",
            "fedora" => "Fedora",
            "suse" or "sles" or "opensuse" => "SUSE",
            "any" => "все",
            _ => char.ToUpperInvariant(hint[0]) + hint[1..]
        };
    }

    public static string Letter(string? hint)
    {
        var label = FromHint(hint);
        return label switch
        {
            "Ubuntu" => "U",
            "Debian" => "D",
            "RHEL" => "R",
            "CentOS" => "C",
            "Alma" => "A",
            "SUSE" => "S",
            "Rocky" => "R",
            "Fedora" => "F",
            "—" or "все" => "?",
            _ => label.Length > 0 ? label[..1].ToUpperInvariant() : "?"
        };
    }

    public static string ColorHex(string? hint) => FromHint(hint) switch
    {
        "Ubuntu" => "#E95420",
        "Debian" => "#A80030",
        "RHEL" => "#EE0000",
        "CentOS" => "#262577",
        "Alma" => "#0F5C8C",
        "SUSE" => "#73BA25",
        "Rocky" => "#10B981",
        "Fedora" => "#294172",
        _ => "#8A9AAB"
    };

    public static IReadOnlyList<string> TagsForAction(IEnumerable<string> distros)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in distros)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // как на макете: «any» = основные семейства, не слово «все»
            if (raw.Equals("any", StringComparison.OrdinalIgnoreCase))
                return new[] { "Ubuntu", "Debian", "RHEL", "Alma" };

            var label = FromHint(raw);
            // свернуть синонимы rhel-семейства в RHEL для бейджа, кроме явных Alma/CentOS если указаны
            if (raw is "centos" or "fedora" or "rocky")
                label = "RHEL";
            set.Add(label);
        }

        if (set.Count == 0) return new[] { "все" };

        // стабильный порядок как на макете
        var order = new[] { "Ubuntu", "Debian", "RHEL", "CentOS", "Alma", "Rocky", "Fedora", "SUSE", "все" };
        return order.Where(set.Contains).Concat(set.Except(order)).ToList();
    }
}
