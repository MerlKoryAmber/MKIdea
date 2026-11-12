using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LinuxOps.Models;

namespace LinuxOps.ViewModels;

/// <summary>Узел дерева Inventory: папка или ресурс. Строится заново при каждом изменении структуры.</summary>
public abstract class TreeNode : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isDropTarget;

    public FolderNode? Parent { get; init; }

    /// <summary>Выделение строки в дереве (не галочка).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>Подсветка папки, на которую сейчас бросают при перетаскивании.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; OnPropertyChanged(); }
    }

    /// <summary>Имя для UI Automation и экранных дикторов.</summary>
    public abstract string AutomationName { get; }

    /// <summary>Раскрыт ли узел (у ресурса всегда нет — детей у него нет).</summary>
    public virtual bool IsExpanded { get => false; set { } }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FolderNode : TreeNode
{
    private readonly bool _forceExpand;
    private readonly Action _persist;

    public FolderNode(HostFolder folder, bool forceExpand, Action persist)
    {
        Folder = folder;
        _forceExpand = forceExpand;
        _persist = persist;
    }

    public HostFolder Folder { get; }
    public ObservableCollection<TreeNode> Children { get; } = new();
    public string Title => Folder.Name;
    public string CountText => Leaves().Count().ToString();
    public override string AutomationName => "Папка " + Folder.Name;

    /// <summary>Свёрнута или нет — хранится в папке (общее для всех вкладок). При поиске всё раскрыто, без записи.</summary>
    public override bool IsExpanded
    {
        get => _forceExpand || Folder.IsExpanded;
        set
        {
            if (_forceExpand || Folder.IsExpanded == value) return;
            Folder.IsExpanded = value;
            OnPropertyChanged();
            _persist();
        }
    }

    /// <summary>Галочка папки: все ресурсы внутри отмечены — да, ни одного — нет, иначе — частично.</summary>
    public bool? IsChecked
    {
        get
        {
            var leaves = Leaves().ToList();
            if (leaves.Count == 0) return false;
            var n = leaves.Count(l => l.Item.IsSelected);
            return n == 0 ? false : n == leaves.Count ? true : null;
        }
        set
        {
            var v = value == true;
            foreach (var l in Leaves()) l.Item.IsSelected = v;
            RaiseChecked();
        }
    }

    public IEnumerable<ItemNode> Leaves()
    {
        foreach (var c in Children)
        {
            if (c is ItemNode i) yield return i;
            else if (c is FolderNode f)
                foreach (var x in f.Leaves()) yield return x;
        }
    }

    public void RaiseChecked()
    {
        OnPropertyChanged(nameof(IsChecked));
        Parent?.RaiseChecked();
    }
}

public sealed class ItemNode : TreeNode
{
    public ItemNode(IInventoryItem item) => Item = item;
    public IInventoryItem Item { get; }
    public override string AutomationName => Item.Title;
}
