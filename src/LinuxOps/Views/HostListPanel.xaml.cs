using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>
/// Хосты слева (SSH Files, «Подключения»): дерево с папками, поиск, двойной щелчок или Enter — подключение.
/// В «Подключениях» рядом с хостами Linux — хосты Windows (RDP).
/// </summary>
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
        List.ItemActivated += Activate;
        List.TypingStarted += FocusSearch;
        List.Rebuilt += UpdateCount;
        // Меню — только для хоста, у папки его нет. «Открыть в SSH Files» — только у Linux.
        TreeHost.ContextMenuOpening += (_, e) =>
        {
            e.Handled = SelectedItem is not (HostConfig or WindowsHost);
            MenuOther.Visibility = SelectedItem is HostConfig && OtherHeader.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    /// <summary>Хост выбран щелчком (без подключения).</summary>
    public event Action<HostConfig?>? HostSelected;

    /// <summary>Двойной щелчок, Enter или «Подключить» на хосте Linux.</summary>
    public event Action<HostConfig>? HostActivated;

    /// <summary>То же на хосте Windows.</summary>
    public event Action<WindowsHost>? WindowsActivated;

    /// <summary>«Новая вкладка с этим хостом» — ещё одна сессия, даже если хост уже открыт (Linux или Windows).</summary>
    public event Action<IInventoryItem>? NewRequested;

    /// <summary>Второй пункт меню (например «Открыть в SSH Files»), только для Linux.</summary>
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

    /// <summary>Набор ресурсов дерева (<see cref="ResourceTree.Scope"/>): Linux или Connect. Задавать до <see cref="Bind"/>.</summary>
    public string Scope
    {
        get => List.Scope;
        set
        {
            List.Scope = value;
            if (value == "Connect") SearchHint.Text = "Поиск: имя, адрес, пользователь, Windows";
        }
    }

    public HostConfig? SelectedHost => List.SelectedItem as HostConfig;

    /// <summary>Выделенный хост Linux или Windows.</summary>
    public IInventoryItem? SelectedItem => List.SelectedItem is HostConfig or WindowsHost ? List.SelectedItem : null;

    public void Bind(AppSession session) => List.Bind(session);

    /// <summary>Выделить хост без события выбора.</summary>
    public void Select(IInventoryItem? item)
    {
        _silent = true;
        try
        {
            List.Select(item);
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

    private void Activate(IInventoryItem item)
    {
        if (item is HostConfig h) HostActivated?.Invoke(h);
        else if (item is WindowsHost w) WindowsActivated?.Invoke(w);
    }

    private bool Match(IInventoryItem item)
    {
        string[] fields = item switch
        {
            HostConfig h => new[] { h.Title, h.Host, h.Username, h.DistroBadge },
            WindowsHost w => new[] { w.Title, w.Host, w.User, "Windows", "RDP" },
            _ => Array.Empty<string>()
        };
        if (fields.Length == 0) return false;
        foreach (var part in Search.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!fields.Any(f => f.Contains(part, StringComparison.OrdinalIgnoreCase)))
                return false;

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
        if (SelectedItem is null && List.ShownCount == 1)
        {
            var only = List.FirstVisibleItem();
            Select(only);
            HostSelected?.Invoke(only as HostConfig);
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
                if (SelectedItem is null && List.FirstVisibleItem() is { } first)
                {
                    Select(first);
                    HostSelected?.Invoke(first as HostConfig);
                }

                List.FocusSelected();
                e.Handled = true;
                break;
            case Key.Enter:
                var item = SelectedItem ?? List.FirstVisibleItem();
                if (item is HostConfig or WindowsHost)
                {
                    Select(item);
                    HostSelected?.Invoke(item as HostConfig);
                    Activate(item);
                }

                e.Handled = true;
                break;
        }
    }

    private void MenuConnect_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is { } item) Activate(item);
    }

    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is { } item) NewRequested?.Invoke(item);
    }

    private void MenuOther_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHost is { } host) OtherRequested?.Invoke(host);
    }
}
