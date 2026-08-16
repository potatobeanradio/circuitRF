using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.Ui.ViewModels;
using CircuitRF.WBond;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The Properties Inspector's wire context (wbond.md §6.9): everything about ONE selected wire —
/// its group, diameter, material, and an editable list of its own coordinates.
///
/// <para><b>Shown only for a single wire</b>, whole or partially selected. Two wires have no shared
/// coordinate list to edit, and blanking every differing field would leave a panel that shows almost
/// nothing — the toolbar and the profile menu are where multi-wire edits live.</para>
///
/// <para><b>It works from EITHER view.</b> The layout canvas and the profile canvas both write the
/// same <c>WBondViewModel.Selection</c>, so this panel follows a wire picked in either without
/// knowing which one did the picking.</para>
///
/// <para><b>Coordinates update live during a drag, and that needs no drag-override machinery here.</b>
/// Unlike the layout editor — where a drag previews through <c>Overlay.DragOverrides</c> and the model
/// is untouched until release — a wBond drag mutates the wire's points in place and raises
/// <c>ReadoutChanged</c> every frame. Refreshing on that event is the whole of it.</para>
///
/// <para>The row machinery deliberately mirrors <c>LayoutShapePropertiesViewModel</c>'s vertex list:
/// a <see cref="LazyIndexedList{T}"/> so a long wire materialises only the rows on screen, and rows
/// refreshed IN PLACE (never a rebuilt collection) while the point count is unchanged, so a drag
/// cannot thrash scroll position or steal focus from a field being typed into.</para>
///
/// <para><b>The picker lists are CACHED, and notified only when their contents change.</b> This is a
/// crash fix, not an optimisation. <c>ItemsSource</c> is bound to <see cref="AvailableGroups"/>, and a
/// ComboBox whose item list is replaced re-resolves — and re-raises — its selection. So a property
/// notification that hands out a freshly allocated list every time closes a cycle through the view:
/// <c>Refresh</c> → <c>AvailableGroups</c> changed → <c>ItemsSource</c> replaced → <c>SelectionChanged</c>
/// → <c>CommitGroup</c> → <c>Refresh</c>. Since <see cref="Refresh"/> runs on every selection change,
/// merely CLICKING A WIRE entered that cycle and overflowed the stack. Two things close it: the lists
/// below are stable references, and <see cref="CommitGroup"/> does nothing at all — not even a refresh —
/// when the wire is already in the named group.</para>
/// </summary>
public sealed partial class WBondWirePropertiesViewModel : ObservableObject
{
    private WBondViewModel? _vm;
    private int _wireIndex = -1;
    private int _pointCount = -1;
    private string? _focusedField;
    private LazyIndexedList<WireVertexRowViewModel>? _rowsBacking;

    [ObservableProperty] private bool _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a single wire.";

    [ObservableProperty] private string _groupName = "";
    [ObservableProperty] private string _wireSummary = "";

    [ObservableProperty] private string _loopHeightText = "";
    [ObservableProperty] private string? _loopHeightError;
    public bool HasLoopHeightError => LoopHeightError is not null;

    [ObservableProperty] private string _spanText = "";
    [ObservableProperty] private string? _spanError;
    public bool HasSpanError => SpanError is not null;

    [ObservableProperty] private string _diameterText = "";
    [ObservableProperty] private string? _diameterError;
    public bool HasDiameterError => DiameterError is not null;

    [ObservableProperty] private string _material = "";
    [ObservableProperty] private string _profileBinding = "";

    private string[] _materialsCache = [];
    private string[] _groupsCache = [];

    /// <summary>The materials a wire may be set to — a closed set, so a typo is not possible.</summary>
    public IReadOnlyList<string> Materials => _materialsCache;

    /// <summary>
    /// Chosen when the user wants a group that does not exist yet. The VIEW recognises it and prompts
    /// for a name — a combo cannot offer a value nobody has typed, and making the user create the
    /// group elsewhere first would be a detour.
    /// </summary>
    public const string NewGroupSentinel = "New Group Name…";

    /// <summary>Every existing group, plus the "new group" entry, for the group picker.</summary>
    public IReadOnlyList<string> AvailableGroups => _groupsCache;

    /// <summary>
    /// Re-reads a picker's item list, and raises its change notification ONLY if the contents differ.
    /// Handing the same reference back is what stops a bound ComboBox rebuilding — and re-raising its
    /// selection — on every refresh; see the class remarks for the crash that caused.
    /// </summary>
    private void SyncList(ref string[] cache, string propertyName, IEnumerable<string> live)
    {
        string[] next = _vm is null ? [] : [.. live];
        if (cache.AsSpan().SequenceEqual(next)) return;

        cache = next;
        OnPropertyChanged(propertyName);
    }

    private void SyncMaterialsList() =>
        SyncList(ref _materialsCache, nameof(Materials),
                 _vm?.Design.Materials.Select(m => m.Name) ?? []);

    private void SyncGroupsList() =>
        SyncList(ref _groupsCache, nameof(AvailableGroups),
                 (_vm?.Design.Arrays.Select(a => a.Name) ?? []).Append(NewGroupSentinel));

    /// <summary>One row per point. Never replaced while the point count is unchanged.</summary>
    public LazyIndexedList<WireVertexRowViewModel>? VertexRows { get; private set; }

    /// <summary>The unit every length in this panel is shown and parsed in.</summary>
    public WBondUnit Unit => _vm?.DisplayUnit ?? WBondUnit.Mil;

    internal WBondViewModel? Editor => _vm;

    // ---------------------------------------------------------------- context

    /// <summary>Binds to a document's editor, or clears the panel when passed null.</summary>
    public void SetContext(WBondViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm)) { Refresh(); return; }

        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.ReadoutChanged -= Refresh;
        }

        _vm = vm;

        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;

            // The live-during-drag channel: a wBond drag mutates points and raises this every frame.
            _vm.ReadoutChanged += Refresh;
        }

        SyncMaterialsList();
        Refresh();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WBondViewModel.Selection) or nameof(WBondViewModel.DisplayUnit))
            Refresh();
    }

    /// <summary>Re-reads everything from the model. Cheap, and safe to call per drag frame.</summary>
    public void Refresh()
    {
        // Groups can be created by an edit here, so the picker's own list is re-read each refresh —
        // it depends on the design, not on the selection, so it is read before the empty-state exits.
        SyncGroupsList();

        if (_vm is null) { SetEmpty("No wBond document."); return; }

        var touched = _vm.Selection.TouchedWires();
        if (touched.Count == 0) { SetEmpty("Select a single wire."); return; }
        if (touched.Count > 1) { SetEmpty($"{touched.Count} wires selected."); return; }

        int index = touched.First();
        var wire = _vm.Design.AllWires().ElementAtOrDefault(index);
        if (wire is null || wire.Points.Count < 2) { SetEmpty("Select a single wire."); return; }

        IsEmptyState = false;
        _wireIndex = index;

        GroupName = GroupOf(index) ?? "";
        Material = wire.Material;
        ProfileBinding = wire.ProfileBinding ?? "(free)";

        WireSummary = $"{wire.Points.Count} points";

        if (_focusedField != "LoopHeight") LoopHeightText = Format(wire.LoopHeightNm);
        if (_focusedField != "Span")
            SpanText = Format((long)Math.Round(wire.ChordLengthMetres() * WBondUnits.NmPerMetre));
        if (_focusedField != "Diameter") DiameterText = Format(wire.DiameterNm);

        RebuildOrRefreshRows(wire);
    }

    private void SetEmpty(string message)
    {
        IsEmptyState = true;
        EmptyMessage = message;
        _wireIndex = -1;
        _pointCount = -1;
        VertexRows = null;
        _rowsBacking = null;
        OnPropertyChanged(nameof(VertexRows));
    }

    /// <summary>The array a flat wire index belongs to.</summary>
    private string? GroupOf(int flatIndex)
    {
        int flat = 0;
        foreach (var array in _vm!.Design.Arrays)
        {
            foreach (var _ in array.Wires)
            {
                if (flat == flatIndex) return array.Name;
                flat++;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- rows

    /// <summary>
    /// Rebuilds the row collection only when the POINT COUNT changes; otherwise refreshes the rows
    /// already on screen in place.
    ///
    /// <para>Replacing the collection on every drag frame would reset scroll position and destroy the
    /// text box the user is typing into — the same reason the layout editor's vertex list is built
    /// this way.</para>
    /// </summary>
    private void RebuildOrRefreshRows(Wire wire)
    {
        if (wire.Points.Count != _pointCount || _rowsBacking is null)
        {
            _pointCount = wire.Points.Count;
            _rowsBacking = new LazyIndexedList<WireVertexRowViewModel>(
                _pointCount, i => new WireVertexRowViewModel(this, i));
            VertexRows = _rowsBacking;
            OnPropertyChanged(nameof(VertexRows));
            return;
        }

        foreach (int i in _rowsBacking.MaterializedIndices.ToList())
            _rowsBacking[i].RefreshFromWire();
    }

    /// <summary>Fills one row from the live wire, skipping whichever field has focus.</summary>
    internal void PopulateRow(WireVertexRowViewModel row)
    {
        if (CurrentWire() is not { } wire) return;
        if (row.PointIndex < 0 || row.PointIndex >= wire.Points.Count) return;

        var p = wire.Points[row.PointIndex];

        if (_focusedField != row.FieldKeyX) row.XText = Format(p.X);
        if (_focusedField != row.FieldKeyY) row.YText = Format(p.Y);
        if (_focusedField != row.FieldKeyZ) row.ZText = Format(p.Z);

        // The feet are what a wire lands on; naming them stops a user moving one by accident while
        // meaning to reshape the loop.
        row.RoleText = row.PointIndex == 0 ? "in"
                     : row.PointIndex == wire.Points.Count - 1 ? "out"
                     : "";
    }

    /// <summary>Commits one coordinate of one point.</summary>
    internal void CommitVertex(WireVertexRowViewModel row, char axis, string text)
    {
        if (_vm is null || CurrentWire() is not { } wire) return;
        if (row.PointIndex < 0 || row.PointIndex >= wire.Points.Count) return;

        if (!WBondUnits.TryParseLength(text, Unit, out long value))
        {
            row.Error = "Invalid value";
            return;
        }

        row.Error = null;

        var p = wire.Points[row.PointIndex];
        var updated = axis switch
        {
            'x' => new Point3(value, p.Y, p.Z),
            'y' => new Point3(p.X, value, p.Z),
            _ => new Point3(p.X, p.Y, value),
        };

        if (updated == p) return;   // no-change short-circuit: no undo entry for a re-typed value

        _vm.SetWirePoint(_wireIndex, row.PointIndex, updated);
        Refresh();
    }

    internal void RevertVertex(WireVertexRowViewModel row)
    {
        row.Error = null;
        PopulateRow(row);
    }

    // ---------------------------------------------------------------- fields

    public void SetFocusedField(string? key) => _focusedField = key;

    public void CommitDiameter(string text)
    {
        if (_vm is null || _wireIndex < 0) return;

        if (!WBondUnits.TryParseLength(text, Unit, out long nm) || nm <= 0)
        {
            DiameterError = "Not a positive length.";
            OnPropertyChanged(nameof(HasDiameterError));
            return;
        }

        DiameterError = null;
        OnPropertyChanged(nameof(HasDiameterError));

        _vm.SetWireDiameter(_wireIndex, nm);
        Refresh();
    }

    /// <summary>
    /// Sets this wire's loop height. <b>Detaches it from a shared profile</b> — see
    /// <c>WBondViewModel.SetWireLoopHeight</c> for why that is the honest outcome rather than
    /// silently editing the shape every other wire in the group follows.
    /// </summary>
    public void CommitLoopHeight(string text)
    {
        if (_vm is null || _wireIndex < 0) return;

        if (!WBondUnits.TryParseLength(text, Unit, out long nm) || nm <= 0)
        {
            LoopHeightError = "Not a positive length.";
            return;
        }

        // A loop height below the wire's own foot drop is unachievable by any shape — say so rather
        // than accepting the number and quietly producing a different one.
        if (CurrentWire() is { } wire && nm < wire.FootDropNm)
        {
            LoopHeightError =
                $"Below this wire's own foot drop ({Format(wire.FootDropNm)} {WBondUnits.Suffix(Unit)}); " +
                "a straight wire already measures that much.";
            return;
        }

        LoopHeightError = null;
        _vm.SetWireLoopHeight(_wireIndex, nm);
        Refresh();
    }

    /// <summary>Sets this wire's foot-to-foot span; the output foot moves, the input foot stays put.</summary>
    public void CommitSpan(string text)
    {
        if (_vm is null || _wireIndex < 0) return;

        if (!WBondUnits.TryParseLength(text, Unit, out long nm) || nm <= 0)
        {
            SpanError = "Not a positive length.";
            return;
        }

        SpanError = null;
        _vm.SetWireSpan(_wireIndex, nm);
        Refresh();
    }

    /// <summary>
    /// Moves this wire into <paramref name="groupName"/>, creating that group when the name is new.
    ///
    /// <para><b>A commit to the group the wire is already in does nothing — not even a refresh.</b>
    /// The combo re-raises <c>SelectionChanged</c> whenever its item list is rebuilt, so a refresh here
    /// would feed straight back into this method; the earlier "calling it again is harmless" comment
    /// was wrong, and that feedback path is what overflowed the stack on a plain wire click. Mirrors
    /// <see cref="CommitMaterial"/>'s guard.</para>
    /// </summary>
    public void CommitGroup(string? groupName)
    {
        if (_vm is null || _wireIndex < 0) return;
        if (string.IsNullOrWhiteSpace(groupName)) return;
        if (groupName == NewGroupSentinel) return;   // the view resolves this to a real name first

        if (string.Equals(GroupOf(_wireIndex), groupName.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        // MoveWireToGroup re-points the selection to the wire's new flat index, and that raises
        // Selection -> Refresh; refreshing again keeps the panel correct if the move was refused.
        _vm.MoveWireToGroup(_wireIndex, groupName);
        Refresh();
    }

    public void CommitMaterial(string? name)
    {
        if (_vm is null || _wireIndex < 0 || string.IsNullOrWhiteSpace(name)) return;
        if (CurrentWire()?.Material == name) return;

        _vm.SetWireMaterial(_wireIndex, name);
        Refresh();
    }

    private Wire? CurrentWire() =>
        _vm is null || _wireIndex < 0 ? null : _vm.Design.AllWires().ElementAtOrDefault(_wireIndex);

    internal string Format(long nm) =>
        WBondUnits.FromNm(nm, Unit).ToString("0.####", CultureInfo.InvariantCulture);

    partial void OnDiameterErrorChanged(string? value) => OnPropertyChanged(nameof(HasDiameterError));
    partial void OnLoopHeightErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoopHeightError));
    partial void OnSpanErrorChanged(string? value) => OnPropertyChanged(nameof(HasSpanError));
}

/// <summary>One point of the selected wire: X, Y and Z, each independently editable.</summary>
public sealed partial class WireVertexRowViewModel : ObservableObject
{
    private readonly WBondWirePropertiesViewModel _owner;

    public int PointIndex { get; }

    public string FieldKeyX { get; }
    public string FieldKeyY { get; }
    public string FieldKeyZ { get; }

    [ObservableProperty] private string _xText = "";
    [ObservableProperty] private string _yText = "";
    [ObservableProperty] private string _zText = "";
    [ObservableProperty] private string _roleText = "";
    [ObservableProperty] private string? _error;

    public bool HasError => Error is not null;

    internal WireVertexRowViewModel(WBondWirePropertiesViewModel owner, int pointIndex)
    {
        _owner = owner;
        PointIndex = pointIndex;

        FieldKeyX = $"WireX:{pointIndex}";
        FieldKeyY = $"WireY:{pointIndex}";
        FieldKeyZ = $"WireZ:{pointIndex}";

        RefreshFromWire();
    }

    internal void RefreshFromWire() => _owner.PopulateRow(this);

    public void Commit(char axis, string text) => _owner.CommitVertex(this, axis, text);

    public void Revert() => _owner.RevertVertex(this);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
