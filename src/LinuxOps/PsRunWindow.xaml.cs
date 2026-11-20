using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;

namespace LinuxOps;

/// <summary>Перед запуском PowerShell-действий: значения параметров (последние — запоминаются до закрытия).</summary>
public partial class PsRunWindow : Window
{
    private static readonly Dictionary<string, string> LastValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Key, TextBox Box)> _fields = new();
    private readonly List<(string Key, CheckBox Box)> _checks = new();

    public Dictionary<string, string> Args { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PsRunWindow(IReadOnlyList<ActionDefinition> actions)
    {
        InitializeComponent();
        foreach (var a in actions.Where(a => a.Parameters.Count > 0))
        {
            ParamsPanel.Children.Add(new TextBlock { Text = a.Name, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 6) });
            foreach (var p in a.Parameters)
            {
                var key = a.Id + ":" + p.Name;
                var value = LastValues.TryGetValue(key, out var last) ? last : p.Default;
                if (p.Kind == "bool")
                {
                    var check = new CheckBox { Content = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label, IsChecked = ActionParam.IsYes(value), FontWeight = FontWeights.SemiBold };
                    System.Windows.Automation.AutomationProperties.SetAutomationId(check, "PsParam_" + p.Name);
                    ParamsPanel.Children.Add(check);
                    ParamsPanel.Children.Add(new TextBlock
                    {
                        Text = p.Hint.Length > 0 ? p.Hint : $"В скрипте — ${p.Name} (да / нет)",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 10)
                    });
                    _checks.Add((key, check));
                    continue;
                }
                ParamsPanel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(p.Label) ? p.Name : p.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
                var box = new TextBox { Text = LastValues.TryGetValue(key, out var v) ? v : p.Default };
                System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PsParam_" + p.Name);
                ParamsPanel.Children.Add(box);
                ParamsPanel.Children.Add(new TextBlock
                {
                    Text = $"В скрипте — ${p.Name}. По умолчанию: {(p.Default.Length > 0 ? p.Default : "пусто")}",
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 10)
                });
                _fields.Add((key, box));
            }
        }

        Loaded += (_, _) =>
        {
            if (_fields.Count > 0) _fields[0].Box.Focus();
            else _checks.FirstOrDefault().Box?.Focus();
        };
    }


    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (key, box) in _fields)
        {
            Args[key] = box.Text.Trim();
            LastValues[key] = box.Text.Trim();
        }
        foreach (var (key, box) in _checks)
        {
            Args[key] = LastValues[key] = box.IsChecked == true ? "да" : "нет";
        }

        DialogResult = true;
    }
}
