using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>Хосты слева (SSH Files, Terminal): дерево с папками, поиск, двойной щелчок или Enter — подключение.</summary>
public partial class HostListPanel : UserControl
{
    private bool _silent;

    public HostListPanel()
    {
        InitializeComponent();
        List.SelectionChanged += _ =>
        {
            if (!_silent) HostSelected?.Invoke(SelectedHost);
        };
        List.ItemActivated += item =>
        {
            if (item is HostConfig h) HostActivated?.Invoke(h);
        };
        List.TypingStarted += FocusSearch;
        List.Rebuilt += UpdateCount;
        // Меню — только для хоста, у папки его нет.
        TreeHost.ContextMenuOpening += (_, e) => e.Handled = SelectedHost is null;
    }

    /// <summary>Хост выбран щелчком (без подключения).</summary>
    public event Action<HostConfig?>? HostSelected;

    /// <summary>Двойной щелчок, Enter или «Подключить».</summary>
    public event Action<HostConfig>? HostActivated;

    /// <summary>«Новая вкладка с этим хостом» — ещё одна сессия, даже если хост уже открыт.</summary>
    public event Action<HostConfig>? NewRequested;

    /// <summary>Второй пункт меню (например «Открыть в терминале»).</summary>
    public event Action<HostConfig>? OtherRequested;

    public string OtherHeader
    {
        get => MenuOther.Header?.ToString() ?? "";
        set
        {
            MenuOther.Header = value;
            MenuOther.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public HostConfig? SelectedHost => List.SelectedItem as HostConfig;

    public void Bind(AppSession session) => List.Bind(session);

    /// <summary>Выделить хост без события выбора.</summary>
    public void Select(HostConfig? host)
    {
        _silent = true;
        try
        {
            List.Select(host);
        }
        finally
        {
            _silent = false;
        }
    }

    public void FocusSearch()
    {
        Search.Focus();
        Keyboard.Focus(Search);
    }

    private bool Match(IInventoryItem item)
    {
        if (item is not HostConfig h) return false;
        foreach (var part in Search.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = h.Title.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.Host.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.Username.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.DistroBadge.Contains(part, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        return true;
    }

    private void UpdateCount()
    {
        var total = List.TotalCount;
        var shown = List.ShownCount;
        CountText.Text = shown == total ? total.ToString() : $"{shown} из {total}";
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        List.Filter = Search.Text.Trim().Length == 0 ? null : Match;
        List.Rebuild();
        if (SelectedHost is null && List.ShownCount == 1)
        {
            var only = List.FirstVisibleItem() as HostConfig;
            Select(only);
            HostSelected?.Invoke(only);
        }
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Search.Clear();
                e.Handled = true;
                break;
            case Key.Down:
                if (SelectedHost is null && List.FirstVisibleItem() is HostConfig first)
                {
                    Select(first);
                    HostSelected?.Invoke(first);
                }

                List.FocusSelected();
                e.Handled = true;
                break;
            case Key.Enter:
                var host = SelectedHost ?? List.FirstVisibleItem() as HostConfig;
                if (host is not null)
                {
                    Select(host);
                    HostSelected?.Invoke(host);
                    HostActivated?.Invoke(host);
                }

                e.Handled = true;
                break;
        }
    }

    private void MenuConnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHost is { } host) HostActivated?.Invoke(host);
    }

    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHost is { } host) NewRequested?.Invoke(host);
    }

    private void MenuOther_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHost is { } host) OtherRequested?.Invoke(host);
    }
}
