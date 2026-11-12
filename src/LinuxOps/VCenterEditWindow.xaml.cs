using System.Windows;
using LinuxOps.Models;

namespace LinuxOps;

/// <summary>vCenter в Inventory: имя, адрес, пользователь, проверка сертификата. Пароль не хранится.</summary>
public partial class VCenterEditWindow : Window
{
    private readonly VCenterProfile? _existing;

    public VCenterProfile Result { get; private set; } = new();

    public VCenterEditWindow(VCenterProfile? existing = null)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is null)
        {
            Title = "Новый vCenter";
            UserBox.Text = "administrator@vsphere.local";
        }
        else
        {
            NameBox.Text = existing.Name;
            ServerBox.Text = existing.Server;
            UserBox.Text = existing.User;
            CertBox.IsChecked = existing.IgnoreCert;
        }

        Loaded += (_, _) => (existing is null ? ServerBox : NameBox).Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        if (server.Length == 0 || server.Contains(' ') || server.Contains('/'))
        {
            AppDialog.Info(this, "Укажите адрес vCenter: имя или IP, без https:// и пробелов.", "vCenter");
            ServerBox.Focus();
            return;
        }

        Result = new VCenterProfile
        {
            Name = NameBox.Text.Trim(),
            Server = server,
            User = UserBox.Text.Trim(),
            IgnoreCert = CertBox.IsChecked == true,
            FolderId = _existing?.FolderId ?? "",
            IsSelected = _existing?.IsSelected ?? true
        };
        DialogResult = true;
    }
}
