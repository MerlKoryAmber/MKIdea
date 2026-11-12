using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LinuxOps.Models;
using LinuxOps.ViewModels;

namespace LinuxOps.Views;

/// <summary>
/// Дерево Inventory с папками: сворачивание, перетаскивание мышью (ресурсы и папки), галочки (Actions), поиск.
/// Одно на все вкладки; дерево строится заново при изменении структуры и при показе вкладки.
/// </summary>
public partial class ResourceTree : UserControl
{
    private const string DragFormat = "LinuxOps.TreeNode";

    public static readonly DependencyProperty ShowChecksProperty =
        DependencyProperty.Register(nameof(ShowChecks), typeof(bool), typeof(ResourceTree), new PropertyMetadata(false));

    private AppSession? _session;
    private readonly ObservableCollection<TreeNode> _roots = new();
    private readonly List<IInventoryItem> _watched = new();
    private readonly Dictionary<IInventoryItem, ItemNode> _nodes = new();
    private bool _silent;
    private Point _dragStart;
    private TreeNode? _dragNode;
    private FolderNode? _dropFolder;

    public ResourceTree()
    {
        InitializeComponent();
        Tree.ItemsSource = _roots;
        IsVisibleChanged += (_, e) =>
        {
            // Свёрнутость папок общая: другая вкладка могла её поменять.
            if (e.NewValue is true) Rebuild();
        };
    }

    /// <summary>Галочки у ресурсов и папок (Actions).</summary>
    public bool ShowChecks
    {
        get => (bool)GetValue(ShowChecksProperty);
        set => SetValue(ShowChecksProperty, value);
    }

    /// <summary>Только хосты Linux (SSH Files, Terminal): vCenter и папки без хостов не показываются.</summary>
    public bool LinuxOnly { get; set; }

    /// <summary>Поиск: что показывать. Пока задан — все папки раскрыты (без записи).</summary>
    public Func<IInventoryItem, bool>? Filter { get; set; }

    /// <summary>Текст, когда ресурсов нет совсем.</summary>
    public string EmptyMessage { get; set; } = "Ресурсов нет. Добавьте их на вкладке Inventory.";

    public event Action<TreeNode?>? SelectionChanged;
    /// <summary>Двойной щелчок или Enter на ресурсе.</summary>
    public event Action<IInventoryItem>? ItemActivated;
    /// <summary>Начали печатать буквы в дереве (HostListPanel переносит ввод в поиск).</summary>
    public event Action? TypingStarted;
    /// <summary>Дерево перестроено (счётчики и т.п.).</summary>
    public event Action? Rebuilt;

    public TreeNode? SelectedNode { get; private set; }
    public IInventoryItem? SelectedItem => (SelectedNode as ItemNode)?.Item;

    /// <summary>Куда класть новое: выделенная папка или папка выделенного ресурса.</summary>
    public HostFolder? TargetFolder => SelectedNode is FolderNode f ? f.Folder : SelectedNode?.Parent?.Folder;

    public int ShownCount => _nodes.Count;
    public int TotalCount { get; private set; }

    public void Bind(AppSession session)
    {
        _session = session;
        session.TreeChanged += Rebuild;
        session.Hosts.CollectionChanged += OnCollectionChanged;
        session.VCenters.CollectionChanged += OnCollectionChanged;
        session.Folders.CollectionChanged += OnCollectionChanged;
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private IEnumerable<IInventoryItem> AllItems()
        => _session is null ? Enumerable.Empty<IInventoryItem>() : LinuxOnly ? _session.Hosts : _session.Items;

    public void Rebuild()
    {
        if (_session is null) return;
        var keep = SelectedNode switch
        {
            ItemNode i => (object)i.Item,
            FolderNode f => f.Folder,
            _ => null
        };
        foreach (var w in _watched) w.PropertyChanged -= OnItemChanged;
        _watched.Clear();
        _nodes.Clear();

        var all = AllItems().ToList();
        TotalCount = all.Count;
        var items = Filter is { } filter ? all.Where(filter).ToList() : all;
        var filtering = Filter is not null;
        var hideEmpty = filtering || LinuxOnly;
        var known = _session.Folders.Select(f => f.Id).ToHashSet();

        _silent = true;
        try
        {
            _roots.Clear();
            foreach (var n in Level(null, "", items, known, filtering, hideEmpty, 0)) _roots.Add(n);
        }
        finally
        {
            _silent = false;
        }

        foreach (var i in _nodes.Keys)
        {
            i.PropertyChanged += OnItemChanged;
            _watched.Add(i);
        }

        SelectedNode = null;
        if (keep is not null && Find(keep) is { } again)
        {
            _silent = true;
            again.IsSelected = true;
            SelectedNode = again;
            _silent = false;
        }

        UpdateEmpty();
        Rebuilt?.Invoke();
    }

    private IEnumerable<TreeNode> Level(FolderNode? parent, string parentId, List<IInventoryItem> items, HashSet<string> known,
        bool filtering, bool hideEmpty, int depth)
    {
        if (depth > 32) yield break; // цикл в JSON, правленном руками
        foreach (var f in _session!.Folders.Where(f => f.ParentId == parentId).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new FolderNode(f, filtering, () => _session.Persist()) { Parent = parent };
            foreach (var c in Level(node, f.Id, items, known, filtering, hideEmpty, depth + 1)) node.Children.Add(c);
            if (hideEmpty && !node.Leaves().Any()) continue;
            yield return node;
        }

        foreach (var i in items.Where(i => (known.Contains(i.FolderId) ? i.FolderId : "") == parentId)
                     .OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var node = new ItemNode(i) { Parent = parent };
            _nodes[i] = node;
            yield return node;
        }
    }

    private TreeNode? Find(object key)
    {
        if (key is IInventoryItem item) return _nodes.GetValueOrDefault(item);
        return Folders(_roots).FirstOrDefault(f => ReferenceEquals(f.Folder, key));
    }

    private static IEnumerable<FolderNode> Folders(IEnumerable<TreeNode> nodes)
    {
        foreach (var f in nodes.OfType<FolderNode>())
        {
            yield return f;
            foreach (var x in Folders(f.Children)) yield return x;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IInventoryItem.IsSelected) && sender is IInventoryItem i && _nodes.TryGetValue(i, out var node))
            node.Parent?.RaiseChecked();
    }

    private void UpdateEmpty()
    {
        if (TotalCount == 0)
        {
            EmptyText.Text = EmptyMessage;
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (_nodes.Count == 0 && Filter is not null)
        {
            EmptyText.Text = "Ничего не найдено";
            EmptyText.Visibility = Visibility.Visible;
        }
        else EmptyText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Выделить ресурс без события выбора. Папки не раскрываются: свёрнутое остаётся свёрнутым.</summary>
    public void Select(IInventoryItem? item)
    {
        _silent = true;
        try
        {
            if (SelectedNode is not null) SelectedNode.IsSelected = false;
            SelectedNode = item is null ? null : _nodes.GetValueOrDefault(item);
            if (SelectedNode is not null)
            {
                SelectedNode.IsSelected = true;
                ContainerOf(SelectedNode)?.BringIntoView();
            }
        }
        finally
        {
            _silent = false;
        }
    }

    /// <summary>Первый видимый ресурс (для поиска: Enter / стрелка вниз).</summary>
    public IInventoryItem? FirstVisibleItem()
    {
        IEnumerable<ItemNode> Walk(IEnumerable<TreeNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is ItemNode i) yield return i;
                else if (n is FolderNode { IsExpanded: true } f)
                    foreach (var x in Walk(f.Children)) yield return x;
            }
        }

        return Walk(_roots).FirstOrDefault()?.Item;
    }

    public void FocusSelected()
    {
        var tvi = SelectedNode is null ? null : ContainerOf(SelectedNode);
        if (tvi is not null) tvi.Focus();
        else Tree.Focus();
    }

    /// <summary>Контейнер узла (если папки над ним раскрыты и он уже создан).</summary>
    private TreeViewItem? ContainerOf(TreeNode node)
    {
        var path = new Stack<TreeNode>();
        for (TreeNode? n = node; n is not null; n = n.Parent) path.Push(n);
        ItemsControl parent = Tree;
        TreeViewItem? tvi = null;
        foreach (var n in path)
        {
            parent.UpdateLayout();
            tvi = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
            if (tvi is null) return null;
            parent = tvi;
        }

        return tvi;
    }

    // ---------- Выбор, активация, клавиши ----------

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_silent) return;
        SelectedNode = e.NewValue as TreeNode;
        SelectionChanged?.Invoke(SelectedNode);
    }

    private static TreeViewItem? ItemAt(DependencyObject? d)
    {
        while (d is not null and not TreeViewItem and not TreeView)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not TreeViewItem)
        {
            if (d is T) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return false;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInside<ToggleButton>(e.OriginalSource as DependencyObject)) return;
        if (ItemAt(e.OriginalSource as DependencyObject)?.DataContext is ItemNode node)
        {
            ItemActivated?.Invoke(node.Item);
            e.Handled = true;
        }
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SelectedNode is ItemNode i:
                ItemActivated?.Invoke(i.Item);
                e.Handled = true;
                break;
            case Key.Space when ShowChecks && SelectedNode is ItemNode i:
                i.Item.IsSelected = !i.Item.IsSelected;
                e.Handled = true;
                break;
            case Key.Space when ShowChecks && SelectedNode is FolderNode f:
                f.IsChecked = f.IsChecked != true;
                e.Handled = true;
                break;
            case >= Key.A and <= Key.Z when Keyboard.Modifiers == ModifierKeys.None:
                TypingStarted?.Invoke();
                break;
        }
    }

    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Правая кнопка выделяет строку — меню относится к ней.
        if (ItemAt(e.OriginalSource as DependencyObject) is { } tvi)
        {
            tvi.IsSelected = true;
            tvi.Focus();
        }
    }

    // ---------- Перетаскивание ----------

    private void Tree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        _dragNode = IsInside<ToggleButton>(src) || IsInside<ScrollBar>(src) ? null : ItemAt(src)?.DataContext as TreeNode;
        _dragStart = e.GetPosition(Tree);
    }

    private void Tree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed || _session is null) return;
        var p = e.GetPosition(Tree);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var node = _dragNode;
        _dragNode = null;
        try
        {
            DragDrop.DoDragDrop(Tree, new DataObject(DragFormat, node), DragDropEffects.Move);
        }
        finally
        {
            SetDropTarget(null);
        }
    }

    /// <summary>Папка, куда упадёт: папка под мышью, папка ресурса под мышью или корень (null).</summary>
    private static FolderNode? DropFolderAt(DragEventArgs e)
        => ItemAt(e.OriginalSource as DependencyObject)?.DataContext switch
        {
            FolderNode f => f,
            ItemNode i => i.Parent,
            _ => null
        };

    private bool CanDrop(TreeNode node, FolderNode? target)
    {
        if (_session is null) return false;
        var targetId = target?.Folder.Id ?? "";
        return node switch
        {
            ItemNode i => i.Item.FolderId != targetId,
            FolderNode f => f.Folder.ParentId != targetId && !(targetId.Length > 0 && _session.IsInside(targetId, f.Folder.Id)),
            _ => false
        };
    }

    private void SetDropTarget(FolderNode? f)
    {
        if (ReferenceEquals(_dropFolder, f)) return;
        if (_dropFolder is not null) _dropFolder.IsDropTarget = false;
        _dropFolder = f;
        if (f is not null) f.IsDropTarget = true;
    }

    private void Tree_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DragFormat) is not TreeNode node)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var target = DropFolderAt(e);
        var ok = CanDrop(node, target);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? target : null);
        AutoScroll(e.GetPosition(Tree).Y);
    }

    private void AutoScroll(double y)
    {
        if (FindChild<ScrollViewer>(Tree) is not { } sv) return;
        if (y < 24) sv.ScrollToVerticalOffset(sv.VerticalOffset - 12);
        else if (y > Tree.ActualHeight - 24) sv.ScrollToVerticalOffset(sv.VerticalOffset + 12);
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } x) return x;
        }

        return null;
    }

    private void Tree_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void Tree_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        SetDropTarget(null);
        if (_session is null || e.Data.GetData(DragFormat) is not TreeNode node) return;
        var target = DropFolderAt(e);
        if (!CanDrop(node, target)) return;
        if (target is not null && !target.Folder.IsExpanded) target.Folder.IsExpanded = true; // видно, куда легло
        switch (node)
        {
            case ItemNode i:
                _session.Move(i.Item, target?.Folder.Id);
                Select(i.Item);
                break;
            case FolderNode f:
                _session.Move(f.Folder, target?.Folder.Id);
                break;
        }
    }
}
