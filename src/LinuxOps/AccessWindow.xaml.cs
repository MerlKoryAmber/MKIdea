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

    private readonly IReadOnlyList<UserAuditReport> _audits;
    private string? _infoLogin;

    public AccessWindow(IReadOnlyList<HostConfig> hosts, IEnumerable<string> knownLogins, IReadOnlyList<UserAuditReport>? audits = null, bool unlock = false)
    {
        InitializeComponent();
        _hosts = hosts;
        _audits = audits ?? Array.Empty<UserAuditReport>();
        HostsText.Text = "Хосты: " + string.Join(", ", hosts.Select(h => h.Title));
        // Учётки из аудита (без root и служебных) плюс базовые пользователи.
        var audited = _audits.SelectMany(a => a.Users).Where(u => u.Uid >= 1000).Select(u => u.Login);
        foreach (var login in knownLogins.Concat(audited).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal)) LoginBox.Items.Add(login);
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
        if (!string.Equals(_infoLogin, Login, StringComparison.Ordinal))
            ShowAudit();
        if (KeyPanel.Visibility == Visibility.Visible)
        {
            var prints = UserProvisioning.KeyFingerprints(KeyBox.Text);
            FpText.Text = string.IsNullOrWhiteSpace(KeyBox.Text) ? "" :
                prints.Count == 0 ? "Не похоже на открытый ключ" : string.Join("\n", prints);
        }

        UpdateOk();
    }

    /// <summary>Что известно об учётке из последнего аудита: по хостам и ключи для «Отозвать ключ».</summary>
    private void ShowAudit()
    {
        _infoLogin = Login;
        var keys = new Dictionary<string, KeyPick>(StringComparer.Ordinal);
        var lines = new List<string>();
        if (Login.Length > 0 && _audits.Count > 0)
        {
            foreach (var host in _hosts)
            {
                var report = _audits.FirstOrDefault(a => string.Equals(a.Host, host.Name, StringComparison.OrdinalIgnoreCase));
                if (report is null)
                {
                    lines.Add($"{host.Title}: аудита не было");
                    continue;
                }

                var user = report.Users.FirstOrDefault(u => u.Login == Login);
                if (user is null)
                {
                    lines.Add($"{host.Title}: учётки нет");
                    continue;
                }

                var parts = new List<string>();
                if (user.HasSudo) parts.Add(user.SudoText);
                if (user.IsLocked) parts.Add(user.LockText);
                parts.Add(user.KeysTitle);
                lines.Add($"{host.Title}: " + string.Join(" · ", parts));
                foreach (var k in user.Keys.Where(k => k.Blob.Length > 0))
                {
                    if (!keys.TryGetValue(k.Blob, out var pick))
                        keys[k.Blob] = pick = new KeyPick(k.Type, k.Fingerprint, k.Comment, k.Blob);
                    pick.Hosts.Add(host.Title);
                }
            }
        }

        LoginInfo.Text = lines.Count > 0 ? "По аудиту — " + string.Join("; ", lines) : "";
        LoginInfo.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AuditKeysList.ItemsSource = keys.Values.ToList();
        AuditKeysPanel.Visibility = keys.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AuditKey_Changed(object sender, RoutedEventArgs e)
    {
        if (AuditKeysList.ItemsSource is not IEnumerable<KeyPick> picks) return;
        KeyBox.Text = string.Join("\n", picks.Where(p => p.Picked).Select(p => p.Blob + (p.Comment.Length > 0 ? " " + p.Comment : "")));
    }

    private sealed class KeyPick(string type, string fingerprint, string comment, string blob)
    {
        public string Type { get; } = type;
        public string Fingerprint { get; } = fingerprint;
        public string Comment { get; } = comment;
        public string Blob { get; } = blob;
        public List<string> Hosts { get; } = new();
        public bool Picked { get; set; }
        public string Note => (Comment.Length > 0 ? Comment + " · " : "") + string.Join(", ", Hosts);
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
