using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.Properties;

/// <summary>
/// Code-behind for the wire inspector. Tag-keyed dispatchers, exactly like the layout shape
/// inspector's — three handlers cover every static field and three cover every coordinate row,
/// rather than a pair per field.
///
/// <para>Commit is LostFocus + Enter, never per keystroke: the convention every typed dimension field
/// in this application follows, and the one that stops a half-typed "40" committing as "4".</para>
/// </summary>
public partial class WBondWirePropertiesView : UserControl
{
    public WBondWirePropertiesView() => InitializeComponent();

    private WBondWirePropertiesViewModel? Vm => DataContext as WBondWirePropertiesViewModel;

    private WBondWirePropertiesViewModel? _boundVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Unsubscribe first. This fires again every time the panel is re-hosted or its context is
        // re-assigned, and a handler added per attach is a handler RUN per attach.
        if (_boundVm is not null) _boundVm.PropertyChanged -= OnVmPropertyChanged;

        _boundVm = Vm;
        if (_boundVm is not null) _boundVm.PropertyChanged += OnVmPropertyChanged;

        SyncGroupSelection();
    }

    /// <summary>Keeps the group combo showing the wire's real group as the selection moves.</summary>
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WBondWirePropertiesViewModel.GroupName)
                           or nameof(WBondWirePropertiesViewModel.AvailableGroups))
            SyncGroupSelection();
    }

    // ---------------------------------------------------------------- static fields

    private void OnFieldGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string key }) Vm?.SetFocusedField(key);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } box) return;

        // Clear focus BEFORE committing, so this field's own reformat is not suppressed by the
        // refresh guard that exists to protect a field being typed into.
        Vm?.SetFocusedField(null);
        CommitStaticField(key, box.Text ?? "");
    }

    /// <summary>One dispatcher, so a field's commit cannot differ between LostFocus and Enter.</summary>
    private void CommitStaticField(string key, string text)
    {
        switch (key)
        {
            case "Diameter":   Vm?.CommitDiameter(text); break;
            case "LoopHeight": Vm?.CommitLoopHeight(text); break;
            case "Span":       Vm?.CommitSpan(text); break;
        }
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string key } box) return;

        if (e.Key == Key.Enter)
        {
            Vm?.SetFocusedField(null);
            CommitStaticField(key, box.Text ?? "");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm?.SetFocusedField(null);
            Vm?.Refresh();
            e.Handled = true;
        }
    }

    private void OnMaterialChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string name }) Vm?.CommitMaterial(name);
    }

    /// <summary>
    /// Group picker. The trailing "New Group Name…" entry is resolved to a real name by prompting;
    /// a cancelled prompt puts the combo back where it was.
    ///
    /// <para><b>Guarded against re-entry</b> — putting the selection back is itself a selection
    /// change, and without the guard a cancelled prompt would re-open itself forever. The guard is
    /// held across the commit too, because a commit that creates a group REPLACES this combo's item
    /// list, and a rebuilt list re-raises the selection.</para>
    /// </summary>
    private async void OnGroupChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGroup || Vm is null) return;
        if (sender is not ComboBox { SelectedItem: string picked }) return;

        if (picked != WBondWirePropertiesViewModel.NewGroupSentinel)
        {
            _updatingGroup = true;
            try { Vm.CommitGroup(picked); }
            finally { _updatingGroup = false; }

            SyncGroupSelection();
            return;
        }

        string? name = TopLevel.GetTopLevel(this) is Window owner
            ? await new InputNameDialog("New Group", "Group name:").ShowDialog<string?>(owner)
            : null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            _updatingGroup = true;
            try { Vm.CommitGroup(name.Trim()); }
            finally { _updatingGroup = false; }
        }

        // Whether the prompt was answered or cancelled, the combo must stop showing the sentinel.
        SyncGroupSelection();
    }

    /// <summary>
    /// "Group Wires As…" — the same dialog the layout view's wire context menu opens, on the same
    /// shared command (<see cref="WBondGroupCommand"/>), so the two routes cannot behave differently.
    /// </summary>
    private async void OnGroupWiresClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Editor is not { } editor) return;

        await WBondGroupCommand.RunAsync(TopLevel.GetTopLevel(this) as Window, editor);

        // The move re-points the selection, which raises the panel's own refresh — but the group
        // combo tracks the wire rather than the panel, so it is put back explicitly.
        SyncGroupSelection();
    }

    /// <summary>
    /// Puts the combo's ITEMS and then its selection where the view model says, without re-entering
    /// the handler.
    ///
    /// <para><b>Both, in that order, and that is the whole of the blank-combo fix</b> (owner,
    /// 2026-08-17: <i>"sometimes the Group combobox item is empty when I click on a wire"</i>).
    /// <c>ItemsSource</c> used to be bound in XAML, and a binding is attached when the DataContext
    /// reaches the ComboBox — after this view's own <c>PropertyChanged</c> handler, which is
    /// subscribed in <c>OnDataContextChanged</c>. So on an <c>AvailableGroups</c> change this ran
    /// FIRST, set a <c>SelectedItem</c> the combo's old item list did not contain — which Avalonia
    /// resolves to no selection at all — and the binding then rebuilt the items with nothing selected
    /// and nothing left to put it back.</para>
    ///
    /// <para>The list is assigned only when its CONTENTS differ, because assigning it re-raises the
    /// combo's selection; the view model already hands back a stable reference for exactly that
    /// reason (see <c>WBondWirePropertiesViewModel.SyncList</c>).</para>
    /// </summary>
    private void SyncGroupSelection()
    {
        if (Vm is null) return;

        _updatingGroup = true;

        if (!ReferenceEquals(GroupCombo.ItemsSource, Vm.AvailableGroups))
            GroupCombo.ItemsSource = Vm.AvailableGroups;

        GroupCombo.SelectedItem = Vm.GroupName;
        _updatingGroup = false;
    }

    private bool _updatingGroup;

    // ---------------------------------------------------------------- coordinate rows

    private void OnVertexGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string axis, DataContext: WireVertexRowViewModel row }) return;
        Vm?.SetFocusedField(KeyFor(row, axis));
    }

    private void OnVertexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string axis, DataContext: WireVertexRowViewModel row } box) return;

        Vm?.SetFocusedField(null);
        row.Commit(char.ToLowerInvariant(axis[0]), box.Text ?? "");
    }

    private void OnVertexKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { Tag: string axis, DataContext: WireVertexRowViewModel row } box) return;

        if (e.Key == Key.Enter)
        {
            Vm?.SetFocusedField(null);
            row.Commit(char.ToLowerInvariant(axis[0]), box.Text ?? "");
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm?.SetFocusedField(null);
            row.Revert();
            e.Handled = true;
        }
    }

    private static string KeyFor(WireVertexRowViewModel row, string axis) => axis switch
    {
        "X" => row.FieldKeyX,
        "Y" => row.FieldKeyY,
        _ => row.FieldKeyZ,
    };
}
