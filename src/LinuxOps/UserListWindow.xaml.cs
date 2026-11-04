using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using LinuxOps.Models;
using LinuxOps.Services;
using Microsoft.Win32;

namespace LinuxOps;

public partial class UserListWindow : Window
{
    private readonly IReadOnlyList<HostConfig> _hosts;
    private readonly ObservableCollection<UserCard> _cards = new();
    private UserCard? _editing;
    private string _keptPassword = "";

    public IReadOnlyList<AddUserRequest>? ToApply { get; private set; }

    public UserListWindow(IReadOnlyList<HostConfig> hosts)
    {
        InitializeComponent();
        _hosts = hosts;
        Cards.ItemsSource = _cards;
        Reload();
    }

    private void Reload()
    {
        _cards.Clear();
        foreach (var user in UserListStore.Load())
            _cards.Add(new UserCard(user, Persist));
    }

    private void Persist()
    {
        UserListStore.Save(_cards.Select(c => c.Source));
    }

    private void Show(UIElement pane)
    {
        ListPane.Visibility = pane == ListPane ? Visibility.Visible : Visibility.Collapsed;
        EditPane.Visibility = pane == EditPane ? Visibility.Visible : Visibility.Collapsed;
        ImportPane.Visibility = pane == ImportPane ? Visibility.Visible : Visibility.Collapsed;
        ConfirmPane.Visibility = pane == ConfirmPane ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => OpenEdit(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Cards.SelectedItem is not UserCard card)
        {
            AppDialog.Info(this, "Выберите карточку.");
            return;
        }

        OpenEdit(card);
    }

    private void OpenEdit(UserCard? card)
    {
        _editing = card;
        _keptPassword = card?.Source.Password ?? "";
        LoginBox.Text = card?.Source.Login ?? "";
        SudoBox.IsChecked = card?.Source.Sudo == true;
        EnabledBox.IsChecked = card?.Source.Enabled ?? true;
        KeysBox.Text = card?.Source.Keys ?? "";
        CommentBox.Text = card?.Source.Comment ?? "";
        PassBox.Password = "";
        ClearPassBox.IsChecked = false;
        EditError.Text = "";
        ShowFingerprint();
        Show(EditPane);
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

    private void Keys_LostFocus(object sender, RoutedEventArgs e) => ShowFingerprint();

    private void ShowFingerprint()
    {
        FpText.Text = string.Join("\n", UserProvisioning.KeyFingerprints(KeysBox.Text));
    }

    private void EditCancel_Click(object sender, RoutedEventArgs e) => Show(ListPane);

    private void EditSave_Click(object sender, RoutedEventArgs e)
    {
        var password = ClearPassBox.IsChecked == true ? "" : string.IsNullOrEmpty(PassBox.Password) ? _keptPassword : PassBox.Password;
        var err = UserProvisioning.Validate(LoginBox.Text, KeysBox.Text, password, out _);
        if (err is not null)
        {
            EditError.Text = err;
            return;
        }

        var login = LoginBox.Text.Trim();
        if (_cards.Any(c => c != _editing && string.Equals(c.Source.Login, login, StringComparison.Ordinal)))
        {
            EditError.Text = "Такой логин уже есть в списке.";
            return;
        }

        var user = _editing?.Source ?? new BaseUser();
        user.Login = login;
        user.Sudo = SudoBox.IsChecked == true;
        user.Enabled = EnabledBox.IsChecked == true;
        user.Keys = KeysBox.Text.Replace("\r\n", "\n").Trim();
        user.Password = password;
        user.Comment = CommentBox.Text.Trim();
        if (_editing is null)
            _cards.Add(new UserCard(user, Persist));
        else
            _editing.Refresh();
        Persist();
        Show(ListPane);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Cards.SelectedItem is not UserCard card)
        {
            AppDialog.Info(this, "Выберите карточку.");
            return;
        }

        if (!AppDialog.Confirm(this, $"Убрать {card.Login} из списка? На хостах учётка не удалится.")) return;
        _cards.Remove(card);
        Persist();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        HostBox.ItemsSource = _hosts;
        HostBox.SelectedItem = _hosts.FirstOrDefault(h => h.IsSelected) ?? _hosts.FirstOrDefault();
        ImportList.ItemsSource = null;
        ImportError.Text = "";
        Show(ImportPane);
    }

    private void ImportBack_Click(object sender, RoutedEventArgs e) => Show(ListPane);

    private async void ReadHost_Click(object sender, RoutedEventArgs e)
    {
        if (HostBox.SelectedItem is not HostConfig host)
        {
            ImportError.Text = "Выберите хост.";
            return;
        }

        ImportError.Text = "читаю…";
        try
        {
            var found = await Task.Run(() => UserProvisioning.ReadFromHost(host));
            ImportList.ItemsSource = found.Select(c => new ImportRow(c)).ToList();
            ImportError.Text = found.Count == 0 ? "Подходящих учёток нет." : "";
        }
        catch (Exception ex)
        {
            ImportError.Text = ex.Message;
        }
    }

    private void ImportAdd_Click(object sender, RoutedEventArgs e)
    {
        if (ImportList.ItemsSource is not IEnumerable<ImportRow> rows) return;
        foreach (var row in rows.Where(r => r.Selected))
        {
            var existing = _cards.FirstOrDefault(c => string.Equals(c.Source.Login, row.Login, StringComparison.Ordinal));
            if (existing is null)
            {
                _cards.Add(new UserCard(new BaseUser
                {
                    Login = row.Candidate.Login,
                    Sudo = row.Candidate.Sudo,
                    Keys = row.Candidate.Keys,
                    Comment = row.Candidate.Comment,
                    Enabled = true
                }, Persist));
            }
            else
            {
                existing.Source.Sudo = row.Candidate.Sudo;
                existing.Source.Keys = row.Candidate.Keys;
                if (string.IsNullOrWhiteSpace(existing.Source.Comment))
                    existing.Source.Comment = row.Candidate.Comment;
                existing.Refresh();
            }
        }

        Persist();
        Show(ListPane);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var hosts = _hosts.Where(h => h.IsSelected).ToList();
        if (hosts.Count == 0)
        {
            AppDialog.Info(this, "Отметьте хост на панели «Хосты».");
            return;
        }

        var enabled = _cards.Where(c => c.Enabled).ToList();
        if (enabled.Count == 0)
        {
            AppDialog.Info(this, "Нет включённых пользователей.");
            return;
        }

        ConfirmList.ItemsSource = enabled.Select(c => new ConfirmRow(c)).ToList();
        ConfirmText.Text = $"{enabled.Count} пользователей на {hosts.Count} хостов. Снимите галочку, если кого-то применять не нужно. Ничего не удаляется.";
        Show(ConfirmPane);
    }

    private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => Show(ListPane);

    private void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmList.ItemsSource is not IEnumerable<ConfirmRow> rows) return;
        var chosen = rows.Where(r => r.Selected).ToList();
        if (chosen.Count == 0) return;
        ToApply = chosen.Select(r => r.Card.ToRequest()).ToList();
        DialogResult = true;
    }
}

public sealed class UserCard : System.ComponentModel.INotifyPropertyChanged
{
    private readonly Action _persist;
    public BaseUser Source { get; }
    public UserCard(BaseUser source, Action persist)
    {
        Source = source;
        _persist = persist;
    }

    public string Login => Source.Login;
    public string SudoText => Source.Sudo ? " · sudo" : " · без sudo";
    public string Comment => Source.Comment;
    public string KeysText
    {
        get
        {
            var fp = UserProvisioning.KeyFingerprints(Source.Keys);
            return fp.Count == 0 ? "ключей нет" : $"ключей {fp.Count}: {string.Join(", ", fp)}";
        }
    }

    public bool Enabled
    {
        get => Source.Enabled;
        set
        {
            Source.Enabled = value;
            _persist();
            OnPropertyChanged(nameof(Enabled));
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Login));
        OnPropertyChanged(nameof(SudoText));
        OnPropertyChanged(nameof(Comment));
        OnPropertyChanged(nameof(KeysText));
        OnPropertyChanged(nameof(Enabled));
    }

    public AddUserRequest ToRequest()
    {
        UserProvisioning.Validate(Source.Login, Source.Keys, Source.Password, out var fp);
        return new AddUserRequest
        {
            Login = Source.Login,
            Sudo = Source.Sudo,
            Keys = Source.Keys,
            Password = Source.Password,
            Fingerprint = fp
        };
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class ImportRow
{
    public ImportRow(UserProvisioning.HostUserCandidate candidate) => Candidate = candidate;
    public UserProvisioning.HostUserCandidate Candidate { get; }
    public string Login => Candidate.Login;
    public string Detail => (Candidate.Sudo ? "sudo · " : "") + (string.IsNullOrEmpty(Candidate.Fingerprints) ? "без ключа" : Candidate.Fingerprints);
    public bool Selected { get; set; } = true;
}

public sealed class ConfirmRow
{
    public ConfirmRow(UserCard card) => Card = card;
    public UserCard Card { get; }
    public string Login => Card.Login;
    public bool Selected { get; set; } = true;
}
