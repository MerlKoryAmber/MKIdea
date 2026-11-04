using System.IO;
using System.Windows;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

public partial class AddUserWindow : Window
{
    public AddUserRequest? Request { get; private set; }

    public AddUserWindow(IReadOnlyList<HostConfig> hosts)
    {
        InitializeComponent();
        HostsText.Text = string.Join(", ", hosts.Select(h => h.Title));
    }

    private void PickKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Ключи (*.pub)|*.pub|Все|*.*",
            InitialDirectory = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.ssh")
        };
        if (dlg.ShowDialog(this) != true) return;
        KeysBox.Text = File.ReadAllText(dlg.FileName);
        ShowFingerprint();
    }

    private void KeysBox_LostFocus(object sender, RoutedEventArgs e) => ShowFingerprint();

    private void ShowFingerprint()
    {
        var err = UserProvisioning.Validate(LoginBox.Text, KeysBox.Text, PassBox.Password, out var fp);
        FpText.Text = string.IsNullOrEmpty(fp) ? "" : fp;
        if (err is not null && !string.IsNullOrWhiteSpace(KeysBox.Text))
            ErrorText.Text = err;
        else if (string.IsNullOrWhiteSpace(KeysBox.Text))
            ErrorText.Text = "";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var err = UserProvisioning.Validate(LoginBox.Text, KeysBox.Text, PassBox.Password, out var fp);
        if (err is not null)
        {
            ErrorText.Text = err;
            FpText.Text = fp;
            return;
        }

        Request = new AddUserRequest
        {
            Login = LoginBox.Text.Trim(),
            Sudo = SudoBox.IsChecked == true,
            Keys = KeysBox.Text.Replace("\r\n", "\n").Trim(),
            Password = PassBox.Password,
            Fingerprint = fp
        };
        DialogResult = true;
    }
}
