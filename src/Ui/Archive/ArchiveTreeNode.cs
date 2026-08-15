using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.Archive;

/// <summary>
/// One row of the Archive Workspace dialog's tree: a heading, or one file/kit the user can tick.
///
/// <para><b>Children are built on first expand, not up front.</b> A workspace can hold hundreds of
/// results files and a kit can hold tens of thousands, and a dialog that is only ever opened to tick
/// two boxes must not pay for rows nobody looks at. A collapsed heading therefore holds one
/// placeholder row, which is what makes the expander arrow appear at all, and swaps it for the real
/// rows the first time it opens.</para>
///
/// <para>Framework-free apart from the MVVM toolkit — no Avalonia — so the tri-state roll-up below
/// can be tested without a window, which is where this kind of logic actually goes wrong.</para>
/// </summary>
public sealed partial class ArchiveTreeNode : ObservableObject
{
    private static readonly ArchiveTreeNode Placeholder = new() { Title = "…" };

    private Func<IEnumerable<ArchiveTreeNode>>? _childFactory;
    private bool _populated;
    private bool _updatingFromParent;

    public string Title { get; init; } = "";

    /// <summary>Full path, shown as the row's tooltip. Empty for a heading.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The option this row ticks, or null for a heading.</summary>
    public ArchiveOption? Option { get; init; }

    public ArchiveTreeNode? Parent { get; private set; }

    public ObservableCollection<ArchiveTreeNode> Children { get; } = [];

    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Null = some children ticked and some not. Bound to a three-state CheckBox.</summary>
    [ObservableProperty] private bool? _isChecked;

    /// <summary>A heading whose children are not built yet still needs the expander arrow.</summary>
    public static ArchiveTreeNode Group(string title, Func<IEnumerable<ArchiveTreeNode>> children)
    {
        var node = new ArchiveTreeNode { Title = title, _childFactory = children };
        node.Children.Add(Placeholder);
        return node;
    }

    public static ArchiveTreeNode Leaf(ArchiveOption option) => new()
    {
        Title    = option.DisplayName,
        Detail   = option.Detail.Length > 0 ? option.Detail : option.SourcePath,
        Option   = option,
        SizeText = WorkspaceArchivePlan.FormatSize(option.SizeBytes),
        IsChecked = option.Selected,
    };

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _populated || _childFactory is null) return;

        _populated = true;
        Children.Clear();
        foreach (var child in _childFactory())
        {
            child.Parent = this;
            Children.Add(child);
        }

        // A heading the user ticked before opening it has to push that choice down to the rows it
        // was standing in for — otherwise expanding a ticked group shows unticked children.
        if (IsChecked is { } state) ApplyToChildren(state);
        else RefreshFromChildren();
    }

    partial void OnIsCheckedChanged(bool? value)
    {
        if (Option is not null && value is { } v) Option.Selected = v;

        if (!_updatingFromParent && value is { } state)
        {
            ApplyToChildren(state);
            // A group standing in for rows it has not built yet still has to decide for them, so the
            // choice is remembered on the group and applied by OnIsExpandedChanged above; the
            // unbuilt options are updated here directly.
            if (!_populated && _childFactory is not null)
                foreach (var child in _childFactory())
                    if (child.Option is not null) child.Option.Selected = state;
        }

        Parent?.RefreshFromChildren();
    }

    private void ApplyToChildren(bool state)
    {
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Placeholder)) continue;
            child._updatingFromParent = true;
            try { child.IsChecked = state; }
            finally { child._updatingFromParent = false; }
            child.ApplyToChildren(state);
        }
    }

    /// <summary>Rolls the children's state up: all on, all off, or the indeterminate middle.</summary>
    private void RefreshFromChildren()
    {
        var real = Children.Where(c => !ReferenceEquals(c, Placeholder)).ToList();
        if (real.Count == 0) return;

        bool? state = real.All(c => c.IsChecked == true) ? true
                    : real.All(c => c.IsChecked == false) ? false
                    : null;

        _updatingFromParent = true;
        try { IsChecked = state; }
        finally { _updatingFromParent = false; }

        Parent?.RefreshFromChildren();
    }

    /// <summary>Every node under this one, this one included — for tests and for size roll-ups.</summary>
    public IEnumerable<ArchiveTreeNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, Placeholder)) continue;
            foreach (var d in child.SelfAndDescendants()) yield return d;
        }
    }
}
