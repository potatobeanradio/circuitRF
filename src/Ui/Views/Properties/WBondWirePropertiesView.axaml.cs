using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private WBondWirePropertiesViewModel? Vm => DataContext as WBondWirePropertiesViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (Vm is not null) Vm.PropertyChanged += OnVmPropertyChanged;
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
    /// change, and without the guard a cancelled prompt would re-open itself forever.</para>
    /// </summary>
    private async void OnGroupChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingGroup || Vm is null) return;
        if (sender is not ComboBox { SelectedItem: string picked } combo) return;

        if (picked != WBondWirePropertiesViewModel.NewGroupSentinel)
        {
            Vm.CommitGroup(picked);
            SyncGroupSelection();
            return;
        }

        string? name = TopLevel.GetTopLevel(this) is Window owner
            ? await new InputNameDialog("New Group", "Group name:").ShowDialog<string?>(owner)
            : null;

        if (!string.IsNullOrWhiteSpace(name)) Vm.CommitGroup(name.Trim());

        // Whether the prompt was answered or cancelled, the combo must stop showing the sentinel.
        SyncGroupSelection();
    }

    /// <summary>Puts the combo back on the wire's ACTUAL group, without re-entering the handler.</summary>
    private void SyncGroupSelection()
    {
        if (Vm is null) return;

        _updatingGroup = true;
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
