using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using LinuxOps.Models;

namespace LinuxOps.Views;

/// <summary>Одна вкладка сессии: подпись, состояние и её окно.</summary>
public partial class SessionTab : ObservableObject
{
    public SessionTab(FrameworkElement view, object vm)
    {
        View = view;
        Vm = vm;
    }

    public FrameworkElement View { get; }
    public object Vm { get; }

    [ObservableProperty] private HostConfig? host;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string tip = "";
    [ObservableProperty] private bool connected;
    [ObservableProperty] private bool busy;
    [ObservableProperty] private bool isActive;
}

/// <summary>Полоса вкладок над терминалом и файлами: щелчок — перейти, × или средняя кнопка — закрыть, + — новая.</summary>
public partial class SessionTabStrip : UserControl
{
    public SessionTabStrip()
    {
        InitializeComponent();
        Items.ItemsSource = Tabs;
    }

    public ObservableCollection<SessionTab> Tabs { get; } = new();

    public event Action<SessionTab>? Selected;
    public event Action<SessionTab>? CloseRequested;
    public event Action<SessionTab>? DuplicateRequested;
    public event Action? AddRequested;

    public string AddTip
    {
        get => AddBtn.ToolTip?.ToString() ?? "";
        set => AddBtn.ToolTip = value;
    }

    public void BringIntoView(SessionTab tab)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Items.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement fe)
                fe.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static SessionTab? TabOf(object sender) => (sender as FrameworkElement)?.DataContext as SessionTab;

    private void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (TabOf(sender) is { } tab) Selected?.Invoke(tab);
    }

    private void Chip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || TabOf(sender) is not { } tab) return;
        e.Handled = true;
        CloseRequested?.Invoke(tab);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) CloseRequested?.Invoke(tab);
    }

    private void CloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } keep) return;
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
            CloseRequested?.Invoke(tab);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) DuplicateRequested?.Invoke(tab);
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddRequested?.Invoke();

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Колесо листает вкладки вбок.
        Scroll.ScrollToHorizontalOffset(Scroll.HorizontalOffset - e.Delta / 3.0);
        e.Handled = true;
    }
}
