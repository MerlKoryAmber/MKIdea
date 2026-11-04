using System.Windows;
using System.Windows.Media;

namespace LinuxOps;

public partial class HostKeyWindow : Window
{
    public HostKeyWindow(string message, bool changed)
    {
        InitializeComponent();
        Body.Text = message;
        if (!changed) return;
        Body.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x5C, 0x5C));
        TrustBtn.Content = "Всё равно доверять";
        TrustBtn.IsDefault = false;
    }

    private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
