using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using LinuxOps.Models;

namespace LinuxOps.Views;

/// <summary>Список хостов слева: поиск, двойной щелчок или Enter — подключение.</summary>
public partial class HostListPanel : UserControl
{
    private ObservableCollection<HostConfig>? _hosts;
    private ListCollectionView? _view;
    private bool _silent;

    public HostListPanel()
    {
        InitializeComponent();
    }

    /// <summary>Хост выбран щелчком (без подключения).</summary>
    public event Action<HostConfig?>? HostSelected;

    /// <summary>Двойной щелчок, Enter или «Подключить».</summary>
    public event Action<HostConfig>? HostActivated;

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

    public void Bind(ObservableCollection<HostConfig> hosts)
    {
        if (_hosts is not null) _hosts.CollectionChanged -= OnHostsChanged;
        _hosts = hosts;
        _hosts.CollectionChanged += OnHostsChanged;
        _view = new ListCollectionView(hosts) { Filter = Match };
        _view.SortDescriptions.Add(new SortDescription(nameof(HostConfig.Title), ListSortDirection.Ascending));
        List.ItemsSource = _view;
        UpdateCount();
    }

    /// <summary>Выделить хост без события выбора.</summary>
    public void Select(HostConfig? host)
    {
        _silent = true;
        try
        {
            List.SelectedItem = host;
            if (host is not null) List.ScrollIntoView(host);
        }
        finally { _silent = false; }
    }

    public void FocusSearch()
    {
        Search.Focus();
        Keyboard.Focus(Search);
    }

    private bool Match(object item)
    {
        if (item is not HostConfig h) return false;
        var q = Search.Text.Trim();
        if (q.Length == 0) return true;
        foreach (var part in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hit = h.Title.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.Host.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.Username.Contains(part, StringComparison.OrdinalIgnoreCase)
                      || h.DistroBadge.Contains(part, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }

        return true;
    }

    private void OnHostsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        var total = _hosts?.Count ?? 0;
        var shown = _view?.Count ?? 0;
        CountText.Text = shown == total ? total.ToString() : $"{shown} из {total}";
        SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (total == 0)
        {
            EmptyText.Text = "Хостов нет. Добавьте их на вкладке Inventory.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (shown == 0)
        {
            EmptyText.Text = "Ничего не найдено";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
            EmptyText.Visibility = Visibility.Collapsed;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCount();
        if (List.SelectedItem is null && _view is { Count: 1 })
            List.SelectedIndex = 0;
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
                if (List.Items.Count > 0)
                {
                    if (List.SelectedIndex < 0) List.SelectedIndex = 0;
                    (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
                }

                e.Handled = true;
                break;
            case Key.Enter:
                var host = List.SelectedItem as HostConfig ?? (_view is { Count: > 0 } ? _view.GetItemAt(0) as HostConfig : null);
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

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_silent) return;
        HostSelected?.Invoke(List.SelectedItem as HostConfig);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(List, d) is ListBoxItem { DataContext: HostConfig host })
            HostActivated?.Invoke(host);
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is HostConfig host)
        {
            HostActivated?.Invoke(host);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && List.SelectedIndex == 0)
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (e.Key is >= Key.A and <= Key.Z && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Начали печатать в списке — переносим ввод в поиск.
            FocusSearch();
        }
    }

    private void MenuConnect_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is HostConfig host) HostActivated?.Invoke(host);
    }

    private void MenuOther_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is HostConfig host) OtherRequested?.Invoke(host);
    }
}
