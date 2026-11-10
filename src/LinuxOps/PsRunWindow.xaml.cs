using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;
using LinuxOps.Services;

namespace LinuxOps;

/// <summary>Перед запуском PowerShell-действий: какой vCenter и значения параметров (последние — запоминаются до закрытия).</summary>
public partial class PsRunWindow : Window
{
    private static readonly Dictionary<string, string> LastValues = new(StringComparer.OrdinalIgnoreCase);
    private static string? _lastServer;
    private readonly List<(string Key, TextBox Box)> _fields = new();

    public VCenterProfile? Profile { get; private set; }
    public Dictionary<string, string> Args { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PsRunWindow(IReadOnlyList<ActionDefinition> actions)
    {
        InitializeComponent();
        LoadProfiles();
        foreach (var a in actions.Where(a => a.Parameters.Count > 0))
        {
            ParamsPanel.Children.Add(new TextBlock { Text = a.Name, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var p in a.Parameters)
            {
                var key = a.Id + ":" + p.Name;
                ParamsPanel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
                var box = new TextBox { Text = LastValues.TryGetValue(key, out var v) ? v : p.Default };
                System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PsParam_" + p.Name);
                ParamsPanel.Children.Add(box);
                ParamsPanel.Children.Add(new TextBlock
                {
                    Text = $"В скрипте — ${p.Name}. По умолчанию: {p.Default}",
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 10)
                });
                _fields.Add((key, box));
            }
        }
    }

    private void LoadProfiles()
    {
        var list = DefaultsStore.Load().VCenters;
        VcBox.ItemsSource = list;
        VcBox.SelectedItem = list.FirstOrDefault(p => p.Server == _lastServer) ?? list.FirstOrDefault();
        VcHint.Text = list.Count == 0
            ? "Профилей vCenter нет — нажмите «Профили…» и добавьте."
            : "Против какого vCenter выполнить. Пароль спросится при первом подключении.";
    }

    private void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (VsphereWindow.Open(this)) LoadProfiles();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (VcBox.SelectedItem is not VCenterProfile vc)
        {
            AppDialog.Info(this, "Выберите vCenter или добавьте профиль («Профили…»).", "PowerShell-действия");
            return;
        }

        Profile = vc;
        _lastServer = vc.Server;
        foreach (var (key, box) in _fields)
        {
            Args[key] = box.Text.Trim();
            LastValues[key] = box.Text.Trim();
        }

        DialogResult = true;
    }
}
