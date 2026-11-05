using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

/// <summary>
/// «Закрыть доступ» / «Открыть доступ». Сначала «Проверить» (только чтение), потом кнопка действия —
/// она выполняется только на хостах, где проверка нашла что делать. Удаление — после ввода логина.
/// </summary>
public partial class AccessWindow : Window
{
    private readonly IReadOnlyList<HostConfig> _hosts;
    private bool _unlock;
    private bool _checking;
    private AccessRequest? _checked;
    private List<HostConfig> _applicable = new();

    public AccessWindow(IReadOnlyList<HostConfig> hosts, IEnumerable<string> knownLogins, bool unlock = false)
    {
        InitializeComponent();
        _hosts = hosts;
        HostsText.Text = "Хосты: " + string.Join(", ", hosts.Select(h => h.Title));
        foreach (var login in knownLogins.Distinct().OrderBy(l => l)) LoginBox.Items.Add(login);
        LoginBox.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(Input_Changed));
        LoginBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(Invalidate);
        SetUnlock(unlock);
        Loaded += (_, _) => LoginBox.Focus();
    }

    public AccessRequest? Request { get; private set; }
    public IReadOnlyList<HostConfig> Targets => _applicable;

    private AccessMode Mode => _unlock ? AccessMode.Unlock
        : ModeKey.IsChecked == true ? AccessMode.RevokeKey
        : ModeDelete.IsChecked == true ? AccessMode.Delete
        : AccessMode.Lock;

    private string Login => (LoginBox.Text ?? "").Trim();

    private void SetUnlock(bool unlock)
    {
        _unlock = unlock;
        Title = unlock ? "Открыть доступ" : "Закрыть доступ";
        ModesPanel.Visibility = unlock ? Visibility.Collapsed : Visibility.Visible;
        UnlockHint.Visibility = unlock ? Visibility.Visible : Visibility.Collapsed;
        SwitchBtn.Content = unlock ? "← Закрыть доступ" : "Открыть доступ…";
        OkBtn.Content = unlock ? "Открыть доступ" : "Закрыть доступ";
        Invalidate();
    }

    private void Switch_Click(object sender, RoutedEventArgs e) => SetUnlock(!_unlock);

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || KeyPanel is null) return;
        KeyPanel.Visibility = ModeKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OkBtn.Content = ModeDelete.IsChecked == true ? "Удалить учётную запись" : ModeKey.IsChecked == true ? "Отозвать ключ" : "Закрыть доступ";
        Invalidate();
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => Invalidate();

    /// <summary>Любое изменение ввода — проверка устарела.</summary>
    private void Invalidate()
    {
        if (!IsInitialized || OkBtn is null) return;
        _checked = null;
        _applicable.Clear();
        PreviewList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ConfirmBox.Text = "";
        ErrorText.Text = "";
        if (KeyPanel.Visibility == Visibility.Visible)
        {
            var prints = UserProvisioning.KeyFingerprints(KeyBox.Text);
            FpText.Text = string.IsNullOrWhiteSpace(KeyBox.Text) ? "" :
                prints.Count == 0 ? "Не похоже на открытый ключ" : string.Join("\n", prints);
        }

        UpdateOk();
    }

    private void UpdateOk()
    {
        var ready = _checked is not null && _applicable.Count > 0 && !_checking;
        if (ready && Mode == AccessMode.Delete)
            ready = string.Equals(ConfirmBox.Text.Trim(), _checked!.Login, StringComparison.Ordinal);
        OkBtn.IsEnabled = ready;
    }

    private void PickKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Ключи (*.pub)|*.pub|Все|*.*",
            InitialDirectory = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.ssh")
        };
        if (dlg.ShowDialog(this) != true) return;
        KeyBox.Text = File.ReadAllText(dlg.FileName).Trim();
    }

    private AccessRequest? BuildRequest()
    {
        if (UserAccess.ValidateLogin(Login) is { } err)
        {
            ErrorText.Text = err;
            return null;
        }

        var blobs = "";
        if (Mode == AccessMode.RevokeKey)
        {
            var list = UserAccess.KeyBlobs(KeyBox.Text);
            if (list.Count == 0)
            {
                ErrorText.Text = "Вставьте открытый ключ, который нужно отозвать (строка ssh-ed25519 … / ssh-rsa …).";
                return null;
            }

            blobs = string.Join("\n", list);
        }

        return new AccessRequest { Login = Login, Mode = Mode, KeyBlobs = blobs };
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        Invalidate();
        var request = BuildRequest();
        if (request is null) return;
        _checking = true;
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = "Проверка…";
        try
        {
            var states = await UserAccess.CheckAsync(_hosts, request, CancellationToken.None);
            // Пока шла проверка, ввод могли поменять — тогда результат не наш.
            var now = BuildRequest();
            if (now is null || now.Login != request.Login || now.Mode != request.Mode || now.KeyBlobs != request.KeyBlobs) return;
            var rows = states.Select(s =>
            {
                var (applicable, plan) = UserAccess.Plan(s, request.Mode);
                return new PreviewRow(s.Host, s.Host.Title, UserAccess.Now(s), plan, applicable);
            }).ToList();
            PreviewList.ItemsSource = rows;
            EmptyText.Visibility = Visibility.Collapsed;
            _checked = request;
            _applicable = rows.Where(r => r.Applicable).Select(r => r.Config).ToList();
            if (_applicable.Count == 0)
                ErrorText.Text = "Делать нечего: ни на одном отмеченном хосте нет подходящей учётной записи.";
            else if (request.Mode == AccessMode.Delete)
            {
                ConfirmText.Text = $"Удаление не отменить. Хостов: {_applicable.Count}. Чтобы продолжить, введите логин {request.Login}:";
                ConfirmPanel.Visibility = Visibility.Visible;
                ConfirmBox.Focus();
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
        }
        finally
        {
            _checking = false;
            CheckBtn.IsEnabled = true;
            CheckBtn.Content = "Проверить";
            UpdateOk();
        }
    }

    private void Confirm_Changed(object sender, TextChangedEventArgs e) => UpdateOk();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_checked is null || _applicable.Count == 0) return;
        Request = _checked;
        DialogResult = true;
    }

    private sealed record PreviewRow(HostConfig Config, string Host, string Now, string Plan, bool Applicable);
}
