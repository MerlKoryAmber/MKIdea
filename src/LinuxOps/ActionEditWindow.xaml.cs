using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps;

public partial class ActionEditWindow : Window
{
    private readonly bool _readOnly;
    private readonly bool _newId;
    public ActionDefinition Result { get; private set; } = new();

    public ActionEditWindow(ActionDefinition? existing = null, bool clone = false)
    {
        InitializeComponent();
        if (existing is null)
        {
            BannerText.Text = "Создание custom SSH-действия.";
            return;
        }

        _readOnly = existing.BuiltIn && !clone;
        NameBox.Text = clone ? existing.Name + " (custom)" : existing.Name;
        DescBox.Text = existing.Description;
        ScriptBox.Text = string.IsNullOrWhiteSpace(existing.Script)
            ? PreviewBuiltin(existing.Id)
            : existing.Script;
        TimeoutBox.Text = (existing.TimeoutSeconds > 0 ? existing.TimeoutSeconds : 60).ToString();
        DestructiveBox.IsChecked = existing.Destructive;
        PrivBox.SelectedIndex = string.Equals(existing.Privilege, "user", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

        SetDistros(existing.Distros);
        _newId = clone || string.IsNullOrWhiteSpace(existing.Id);
        Result.Id = clone
            ? existing.Id + "-custom"
            : existing.Id;

        if (_readOnly)
        {
            SaveBtn.IsEnabled = false;
            NameBox.IsReadOnly = DescBox.IsReadOnly = ScriptBox.IsReadOnly = TimeoutBox.IsReadOnly = true;
            DistroPanel.IsEnabled = PrivBox.IsEnabled = DestructiveBox.IsEnabled = false;
        }
    }

    private static string PreviewBuiltin(string id)
    {
        var sample = new DistroInfo { Id = "centos", Family = "rhel", Like = "rhel", PrettyName = "CentOS" };
        try { return BuiltinActions.ResolveScript(id, sample); }
        catch { return "set -e\nuname -a\n"; }
    }

    private void SetDistros(IEnumerable<string> distros)
    {
        var set = new HashSet<string>(distros, StringComparer.OrdinalIgnoreCase);
        DAny.IsChecked = set.Contains("any");
        DUbuntu.IsChecked = set.Contains("ubuntu");
        DDebian.IsChecked = set.Contains("debian");
        DRhel.IsChecked = set.Contains("rhel") || set.Contains("centos") || set.Contains("rocky") || set.Contains("fedora");
        DAlma.IsChecked = set.Contains("alma");
        DSuse.IsChecked = set.Contains("suse") || set.Contains("sles") || set.Contains("opensuse");
    }

    private List<string> CollectDistros()
    {
        if (DAny.IsChecked == true)
            return new List<string> { "any" };

        var list = new List<string>();
        if (DUbuntu.IsChecked == true) list.Add("ubuntu");
        if (DDebian.IsChecked == true) list.Add("debian");
        if (DRhel.IsChecked == true) list.Add("rhel");
        if (DAlma.IsChecked == true) list.Add("alma");
        if (DSuse.IsChecked == true) list.Add("suse");
        if (list.Count == 0) list.Add("any");
        return list;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly) return;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            AppDialog.Info(this, "Укажите имя.");
            return;
        }

        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 5)
            timeout = 60;

        var id = _newId || string.IsNullOrWhiteSpace(Result.Id)
            ? ActionCatalog.UniqueId(string.IsNullOrWhiteSpace(Result.Id) ? Slug(NameBox.Text) : Result.Id)
            : Result.Id;

        Result = new ActionDefinition
        {
            Id = id,
            Name = NameBox.Text.Trim(),
            Description = DescBox.Text.Trim(),
            Script = ScriptBox.Text,
            Distros = CollectDistros(),
            Privilege = (PrivBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "sudo/root",
            TimeoutSeconds = timeout,
            Destructive = DestructiveBox.IsChecked == true,
            BuiltIn = false
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string Slug(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (Cyrillic.TryGetValue(ch, out var lat)) sb.Append(lat);
            else sb.Append(ch);
        }

        var s = Regex.Replace(sb.ToString(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(s) ? "custom-action" : s;
    }

    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };
}
