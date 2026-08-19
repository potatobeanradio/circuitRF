// ================================================================
//  TraceRowViewModel.Versus.cs  —  the "vs X" half of the trace card.
//
//  "Plot versus" gives a trace its own X data: Gain vs Pout, not Gain
//  vs the swept Pin. The card models it as ONE extra choice — which
//  quantity is X — because the X side's axis roles are not free: they
//  mirror the Y side's by axis NAME (same swept axis, same family), so
//  a Gain-vs-Pout family over RFfreq needs no second axis editor. The
//  only rows this picker grows are Fix selectors for axes the Y side
//  does not have.
//
//  The X side may live in a DIFFERENT loaded file (measured Pout
//  against simulated Gain); the point-count gate in VersusResolver is
//  what keeps that pairing honest.
// ================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RfCore;
using RfCore.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class TraceRowViewModel
{
    private bool _suppressVersusCallback;

    // ---- Visibility -------------------------------------------------------

    /// <summary>The vs row is offered for cube-bound traces on the two plot types that have an X
    /// axis at all. Smith/Polar are excluded by the same rule the resolver enforces.</summary>
    public bool ShowVersusRow => IsCubeBoundTrace && IsRectOrTablePlot && !ShowEmptyQuantity;

    /// <summary>The X-source combo appears only once a second dataset is loaded — mirroring the Y
    /// side's own Source selector, so a single-dataset display is untouched by this feature.</summary>
    public bool XSourceSelectorVisible => VersusEnabled && _parent.LibraryEntries.Count > 1;

    // ---- Picker state -----------------------------------------------------

    [ObservableProperty] private bool _versusEnabled;

    public ObservableCollection<PickerSourceItem> XSourceEntries { get; } = new();
    [ObservableProperty] private PickerSourceItem? _selectedXSourceItem;

    public ObservableCollection<string> XGroups { get; } = new();
    [ObservableProperty] private string? _selectedXGroup;

    public ObservableCollection<TraceDataItem> XSignals { get; } = new();
    [ObservableProperty] private TraceDataItem? _selectedXSignal;

    /// <summary>Fix rows for the X quantity's OWN axes — the ones the Y side does not have. Shared
    /// axes are stated by <see cref="XRoleSummary"/> instead of being duplicated as controls.</summary>
    public ObservableCollection<XAxisPinRowViewModel> XAxisPins { get; } = new();

    // ---- X-side transform --------------------------------------------------
    //
    //  The X axis must be REAL, and a perfectly ordinary X quantity is complex (HB1.V). The Y side's
    //  transform combo cannot serve: it transforms Y. So the vs row carries its own, and it is written
    //  INTO the X spec text (mag(HB1.V[…])) rather than into a new persisted field — the spec parser
    //  already reads that form, so a typed spec and a picked one land in exactly the same place.

    public ObservableCollection<CubeTransformItem> XTransformItems { get; } = new();

    private CubeTransformItem? _selectedXTransformItem;
    public CubeTransformItem? SelectedXTransformItem
    {
        get => _selectedXTransformItem;
        set
        {
            if (ReferenceEquals(_selectedXTransformItem, value)) return;
            _selectedXTransformItem = value;
            OnPropertyChanged();
            if (!_suppressVersusCallback && value is { Enabled: true }) ApplyXSpec();
        }
    }

    private string _xRoleSummary = "";
    /// <summary>What the X side inherits from the Y side, in words — the card's way of saying that
    /// the swept axis and the family are shared rather than picked twice.</summary>
    public string XRoleSummary { get => _xRoleSummary; private set { _xRoleSummary = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasXRoleSummary)); } }
    public bool HasXRoleSummary => VersusEnabled && _xRoleSummary.Length > 0;

    // ---- Callbacks --------------------------------------------------------

    partial void OnVersusEnabledChanged(bool value)
    {
        if (_suppressVersusCallback) return;

        if (!value)
        {
            _trace.XSpec       = null;
            _trace.XSourcePath = null;
            _trace.XSourceAlias = null;
            _trace.Expression  = _trace.BuildPickerExpression();
            ClearXPicker();
            _parent.RebuildAndNotify();
            NotifyVersusUi();
            return;
        }

        RebuildXPicker();
        ApplyXSpec();
        NotifyVersusUi();
    }

    partial void OnSelectedXSourceItemChanged(PickerSourceItem? value)
    {
        if (_suppressVersusCallback || value?.Entry is null) return;
        _trace.XSourcePath =
            string.Equals(value.Entry.FilePath, _trace.SourcePath, StringComparison.OrdinalIgnoreCase)
                ? null                       // same file as Y → keep following the Y source
                : value.Entry.FilePath;
        RebuildXPicker();
        ApplyXSpec();
    }

    partial void OnSelectedXGroupChanged(string? value)
    {
        if (_suppressVersusCallback) return;
        _suppressVersusCallback = true;
        FilterXSignalsToGroup(value);
        _suppressVersusCallback = false;
        SelectedXSignal = XSignals.FirstOrDefault();
    }

    partial void OnSelectedXSignalChanged(TraceDataItem? value)
    {
        if (_suppressVersusCallback || value is null) return;
        // Picking a DIFFERENT X quantity resets its transform, the same way the Y side's does on a
        // signal switch: a real quantity gets None (Pout is already dBm — there is nothing to reduce),
        // a complex one gets Mag (the only way it becomes a real axis). Carrying the old transform
        // across left "mag" sitting on a real quantity purely because the PREVIOUS one was complex.
        RebuildXRows(resetTransform: true);
        ApplyXSpec();
    }

    /// <summary>Called by an <see cref="XAxisPinRowViewModel"/> when its Fix value changes.</summary>
    internal void OnXPinChanged()
    {
        if (_suppressVersusCallback) return;
        ApplyXSpec();
    }

    // ---- Sync from the model ----------------------------------------------

    /// <summary>Re-points the whole vs row at whatever the trace currently holds. Called from
    /// RefreshDescription, so a typed spec, a .cdd load, and an undo all land on the same UI.</summary>
    internal void SyncVersusFromTrace()
    {
        _suppressVersusCallback = true;
        try
        {
            VersusEnabled = _trace.IsVersus;
            if (_trace.IsVersus) RebuildXPicker();
            else                 ClearXPicker();
        }
        finally { _suppressVersusCallback = false; }
        NotifyVersusUi();
    }

    /// <summary>
    /// Tears down the whole vs picker — <b>including the cache the content-diffing rebuild keys off</b>.
    ///
    /// <para>This is the bug that made unticking and re-ticking "vs X" come up with an EMPTY group and
    /// item combo: the visible collections were cleared here while <c>_allXSignals</c>/<c>_xSignalEntry</c>
    /// were left populated, so the next <see cref="SyncXSignalList"/> compared the stale cache against
    /// the identical wanted list, concluded "nothing changed", and returned without refilling anything.
    /// A cache and the thing it describes have to be cleared together — which is exactly why they are
    /// cleared in one method now rather than at each call site.</para>
    /// </summary>
    private void ClearXPicker()
    {
        XSourceEntries.Clear();
        XGroups.Clear();
        XSignals.Clear();
        XAxisPins.Clear();
        XTransformItems.Clear();
        _allXSignals.Clear();
        _xSignalEntry = null;
        _selectedXTransformItem = null;
        XRoleSummary = "";
    }

    private void NotifyVersusUi()
    {
        OnPropertyChanged(nameof(ShowVersusRow));
        OnPropertyChanged(nameof(XSourceSelectorVisible));
        OnPropertyChanged(nameof(HasXRoleSummary));
        OnPropertyChanged(nameof(SpecShorthand));
        OnPropertyChanged(nameof(SelectedXTransformItem));
    }

    // ---- Building the picker ----------------------------------------------

    /// <summary>The dataset the X side reads from: its own source when one is set, else the Y side's.</summary>
    private DataSourceEntryViewModel? XEntry()
    {
        string? path = _trace.XSourcePath ?? _trace.SourcePath;
        if (path is null) return null;
        return _parent.LibraryEntries.FirstOrDefault(e =>
            string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Re-points the whole vs picker at the trace's current state.
    ///
    /// <para><b>Every collection here is rebuilt ONLY when its content actually changed.</b> This runs
    /// on every card refresh — i.e. after every edit anywhere on the card — and clearing an
    /// ItemsSource a ComboBox is bound to drops that ComboBox's selection to null: picking a GROUP in
    /// the vs row was blanking the SOURCE combo, because the source list was being torn down and
    /// rebuilt from brand-new item objects in the refresh that followed. (Same failure the
    /// <c>src/Ui/CLAUDE.md</c> ComboBox note describes: a stable item list is part of the contract,
    /// not an optimisation.)</para>
    /// </summary>
    private void RebuildXPicker()
    {
        var entry = XEntry();

        bool saved = _suppressVersusCallback;
        _suppressVersusCallback = true;
        try
        {
            SyncXSourceEntries(entry);
            SyncXSignalList(entry);

            // Re-select whatever the trace's XSpec names, else default to a sensible first item.
            string? wanted = _trace.XSpec is { } xs ? CubeNameOf(xs) : null;
            var match = wanted is null
                ? DefaultXItem()
                : _allXSignals.FirstOrDefault(s => string.Equals(s.CubeName, wanted, StringComparison.Ordinal))
                  ?? DefaultXItem();

            string? group = match?.Group ?? XGroups.FirstOrDefault();
            if (!string.Equals(SelectedXGroup, group, StringComparison.Ordinal)) SelectedXGroup = group;
            FilterXSignalsToGroup(group);
            var wantSignal = match ?? XSignals.FirstOrDefault();
            if (!ReferenceEquals(SelectedXSignal, wantSignal)) SelectedXSignal = wantSignal;

            RebuildXRows();
        }
        finally { _suppressVersusCallback = saved; }

        XRoleSummary = BuildRoleSummary();
    }

    /// <summary>Rebuilds the source list only when the loaded set changed; always re-points the
    /// selection.</summary>
    private void SyncXSourceEntries(DataSourceEntryViewModel? entry)
    {
        var wanted = _parent.LibraryEntries.Count > 1
            ? _parent.LibraryEntries.ToList()
            : new List<DataSourceEntryViewModel>();

        bool same = XSourceEntries.Count == wanted.Count;
        if (same)
            for (int i = 0; i < wanted.Count; i++)
                if (!ReferenceEquals(XSourceEntries[i].Entry, wanted[i])) { same = false; break; }

        if (!same)
        {
            XSourceEntries.Clear();
            foreach (var e in wanted) XSourceEntries.Add(new PickerSourceItem(e));
        }

        var match = XSourceEntries.FirstOrDefault(i => ReferenceEquals(i.Entry, entry));
        if (!ReferenceEquals(SelectedXSourceItem, match)) SelectedXSourceItem = match;
    }

    /// <summary>Rebuilds the quantity list only when the source's cubes changed.</summary>
    private void SyncXSignalList(DataSourceEntryViewModel? entry)
    {
        var wanted = entry?.Data is { } ds
            ? EnumerateCubeItems(entry, ds).ToList()
            : new List<TraceDataItem>();

        bool same = _allXSignals.Count == wanted.Count
                    && ReferenceEquals(_xSignalEntry, entry);
        if (same)
            for (int i = 0; i < wanted.Count; i++)
                if (!string.Equals(_allXSignals[i].CubeName, wanted[i].CubeName, StringComparison.Ordinal)
                 || !string.Equals(_allXSignals[i].Group,    wanted[i].Group,    StringComparison.Ordinal))
                { same = false; break; }
        if (same) return;

        _xSignalEntry = entry;
        _allXSignals.Clear();
        XGroups.Clear();
        foreach (var item in wanted)
        {
            _allXSignals.Add(item);
            if (!XGroups.Contains(item.Group)) XGroups.Add(item.Group);
        }
    }

    private DataSourceEntryViewModel? _xSignalEntry;

    private readonly List<TraceDataItem> _allXSignals = new();

    private void FilterXSignalsToGroup(string? group)
    {
        var wanted = group is null
            ? new List<TraceDataItem>()
            : _allXSignals.Where(s => s.Group == group).ToList();

        // Same items, same order → leave the collection (and the ComboBox's selection) alone.
        bool same = XSignals.Count == wanted.Count;
        if (same)
            for (int i = 0; i < wanted.Count; i++)
                if (!ReferenceEquals(XSignals[i], wanted[i])) { same = false; break; }
        if (same) return;

        XSignals.Clear();
        foreach (var s in wanted) XSignals.Add(s);
    }

    /// <summary>
    /// The X quantity to start from. Never the Y quantity itself (Gain against Gain is not a plot), and
    /// preferably a SIBLING of it — a measurement beside the measurement being plotted, which in PA work
    /// is the whole point (Gain vs Pout). Falling back to "the first cube in the file" landed on a raw
    /// complex HB voltage, so the very first thing the user saw was an X that needed a transform before
    /// it could be plotted at all.
    /// </summary>
    private TraceDataItem? DefaultXItem()
    {
        bool NotY(TraceDataItem s) =>
            !string.Equals(s.CubeName, _trace.CubeName, StringComparison.Ordinal);

        string? yGroup = _allXSignals
            .FirstOrDefault(s => string.Equals(s.CubeName, _trace.CubeName, StringComparison.Ordinal))?.Group;

        bool IsReal(TraceDataItem s) =>
            XEntry()?.Data is { } ds && ds.Contains(s.CubeName!) && ds[s.CubeName!].DataKind == DataKind.Real;

        return (yGroup is not null
                   ? _allXSignals.FirstOrDefault(s => s.Group == yGroup && NotY(s) && IsReal(s))
                     ?? _allXSignals.FirstOrDefault(s => s.Group == yGroup && NotY(s))
                   : null)
            ?? _allXSignals.FirstOrDefault(s => NotY(s) && IsReal(s))
            ?? _allXSignals.FirstOrDefault(NotY)
            ?? _allXSignals.FirstOrDefault();
    }

    /// <summary>The plottable cubes of one dataset, as picker items. Deliberately a simpler list
    /// than the Y side's: no network metrics, no V/I placeholders, no S(i,j) element explosion —
    /// an X axis is a quantity, and anything more exotic is typed into the spec box.</summary>
    private static IEnumerable<TraceDataItem> EnumerateCubeItems(DataSourceEntryViewModel entry, DataSet ds)
    {
        foreach (var group in ds.Groups)
        {
            string groupDisplay = group == DataSet.DefaultGroup      ? "Signals"
                                : group == DataSet.MeasurementsGroup ? "Measurements"
                                :                                      group;

            foreach (var (bareName, cube) in ds.CubesIn(group))
            {
                if (bareName is "Z0" or "ToneFreqs" or "MetaMixOrder") continue;
                if (bareName.StartsWith("__", StringComparison.Ordinal)) continue;
                if (bareName.EndsWith("Converged", StringComparison.Ordinal)) continue;
                if (bareName.EndsWith("Residual", StringComparison.Ordinal)) continue;
                if (cube.Rank == 0) continue;                       // a scalar cannot be an X axis
                if (bareName is "S" or "Z" or "Y"
                    && NetworkMetrics.IsNetworkParamCubeSpec(ds, group == DataSet.DefaultGroup ? bareName : $"{group}.{bareName}"))
                    continue;                                       // matrix cubes: typed, not picked

                string qualified = (group == DataSet.DefaultGroup || group == DataSet.MeasurementsGroup)
                    ? bareName
                    : $"{group}.{bareName}";

                yield return new TraceDataItem(entry, qualified, BuildDefaultSlice(cube), bareName, true)
                             { Group = groupDisplay };
            }
        }
    }

    /// <summary>
    /// Builds the Fix rows — one per X-cube axis that the Y side does NOT have. A shared axis gets no
    /// row at all: its role and its pinned value are the trace's own, already edited by the axis rows
    /// above, and putting a second copy here made one piece of state look like two.
    /// </summary>
    private void RebuildXRows(bool resetTransform = false)
    {
        var previous = XAxisPins.ToDictionary(r => r.AxisName, r => r.PinIndex, StringComparer.Ordinal);
        XAxisPins.Clear();

        if (SelectedXSignal is not { CubeName: { } name } || XEntry()?.Data is not { } ds || !ds.Contains(name))
            return;

        var cube   = ds[name];
        var ySlice = _trace.Slice;

        // Seed from the SPEC when it carries explicit tokens (a .cdd load, or typed text): the spec
        // is the truth, and re-deriving the rows from stale row state would silently rewrite a
        // saved pin back to index 0 on the next edit.
        var fromSpec = PinnedIndicesFromSpec(_trace.XSpec, cube);

        foreach (var axis in cube.Axes)
        {
            bool shared = ySlice is not null && Array.Exists(ySlice, sl =>
                string.Equals(sl.AxisName, axis.Name, StringComparison.Ordinal));
            if (shared) continue;                                          // stated, not duplicated
            if (ySlice is null && axis.Name == DefaultXAxisName(cube)) continue;   // it IS the X axis

            bool hasLabels  = axis.Labels is { Length: > 0 };
            bool axisIsFreq = IsFreqUnit(axis.Unit);
            var opts = new List<string>(axis.Length);
            for (int k = 0; k < axis.Length; k++)
            {
                if (hasLabels && k < axis.Labels!.Length) opts.Add(axis.Labels[k]);
                else if (axisIsFreq)
                    opts.Add($"{(axis.Values[k] * _parent.FreqUnit.Scale()):G4} {_parent.FreqUnit.Description()}");
                else opts.Add(axis.Values[k].ToString("G3"));
            }

            int idx = fromSpec.TryGetValue(axis.Name, out int fs) ? fs
                    : previous.TryGetValue(axis.Name, out int p) ? p
                    : 0;
            string? displayUnit = axisIsFreq ? _parent.FreqUnit.Description() : axis.Unit;

            XAxisPins.Add(new XAxisPinRowViewModel(this, axis.Name, displayUnit, opts, idx, hasLabels));
        }

        RebuildXTransformItems(cube, resetTransform);
    }

    /// <summary>
    /// The X-side transform list. Conj is absent (its result is complex) and, for a complex X cube,
    /// so is None — the X axis must be real, and offering the two choices that cannot produce one
    /// would only route the user to an error message.
    /// </summary>
    private void RebuildXTransformItems(DataCube cube, bool resetTransform = false)
    {
        bool complex = cube.DataKind == DataKind.Complex;
        XTransformItems.Clear();
        XTransformItems.Add(new CubeTransformItem(CubeTransform.None, enabled: !complex));
        foreach (var t in new[] { CubeTransform.dB20, CubeTransform.dB10, CubeTransform.dB,
                                  CubeTransform.Mag,  CubeTransform.Phase,
                                  CubeTransform.Real, CubeTransform.Imag })
            XTransformItems.Add(new CubeTransformItem(t));

        // On a signal CHANGE the transform is re-derived from the new quantity alone; otherwise it is
        // whatever the spec already says (so a user's own choice survives every refresh).
        var want = resetTransform
            ? (complex ? CubeTransform.Mag : CubeTransform.None)
            : XTransform();
        // A complex X with no usable transform lands on Mag rather than on an error: it is the only
        // choice that makes the trace renderable, and the combo shows what it picked.
        if (complex && want is CubeTransform.None or CubeTransform.Conj) want = CubeTransform.Mag;
        if (!complex && want is CubeTransform.Conj) want = CubeTransform.None;

        bool saved = _suppressVersusCallback;
        _suppressVersusCallback = true;
        try
        {
            _selectedXTransformItem = XTransformItems.FirstOrDefault(i => i.Transform == want)
                                   ?? XTransformItems[0];
            OnPropertyChanged(nameof(SelectedXTransformItem));
        }
        finally { _suppressVersusCallback = saved; }
    }

    /// <summary>
    /// The X side's transform. The SPEC leads and the combo is only the fallback, so typing a spec
    /// without a transform genuinely clears it — reading the combo first would silently re-apply the
    /// last picked transform on the next edit.
    /// </summary>
    private CubeTransform XTransform()
    {
        if (_trace.XSpec is { Length: > 0 } spec && XEntry()?.Data is { } ds
            && CubeTraceSpecParser.TryParse(spec, ds, out _, out _, out var t, out _))
            return t;
        return _selectedXTransformItem?.Transform ?? CubeTransform.None;
    }

    /// <summary>Pinned index per axis name, read out of a bracketed X spec (a transform wrapper is
    /// harmless — the bracket body is found either way). Empty for a bare spec, which pins nothing of
    /// its own because it inherits.</summary>
    private static Dictionary<string, int> PinnedIndicesFromSpec(string? spec, DataCube cube)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (spec is null) return map;
        int open  = spec.IndexOf('[');
        int close = spec.LastIndexOf(']');
        if (open < 0 || close <= open) return map;

        var tokens = SliceTokenParser.SplitTokens(spec[(open + 1)..close]);
        if (tokens.Length != cube.Rank) return map;
        for (int d = 0; d < tokens.Length; d++)
        {
            var axis = cube.Axes[d];
            var t = SliceTokenParser.Parse(tokens[d], axis.Length, axis.Labels, axis.Name, out _);
            if (t.Kind == SliceTokenParser.Kind.PinIndex) map[axis.Name] = t.Index;
        }
        return map;
    }

    private static string? DefaultXAxisName(DataCube cube)
    {
        int d = DefaultXAxis(cube);
        return d >= 0 ? cube.Axes[d].Name : null;
    }

    // ---- Writing the spec --------------------------------------------------

    /// <summary>
    /// Writes the X spec the picker currently describes onto the trace, then rebuilds.
    /// A BARE cube name is emitted whenever the Y side answers for every axis — that is the form that
    /// INHERITS roles, and the form that survives a re-run whose sweep changed length. An X cube with
    /// axes of its own gets the explicit bracketed form; a transform wraps either as a function call
    /// (<c>mag(HB1.V[~, :, "Vout", 2])</c>), which is exactly what the spec parser reads back.
    /// </summary>
    private void ApplyXSpec()
    {
        if (!VersusEnabled) return;
        if (SelectedXSignal is not { CubeName: { } name })
        {
            _trace.XSpec = null;
            _trace.Expression = _trace.BuildPickerExpression();
            _parent.RebuildAndNotify();
            return;
        }

        _trace.XSpec      = ComposeXSpec(name);
        _trace.Expression = _trace.BuildPickerExpression();
        XRoleSummary      = BuildRoleSummary();
        _parent.RebuildAndNotify();
        OnPropertyChanged(nameof(SpecShorthand));
    }

    /// <summary>The X spec the picker currently describes — pure, so the Y side can re-derive it
    /// without re-entering the rebuild.</summary>
    private string ComposeXSpec(string cubeName)
    {
        var transform = _selectedXTransformItem is { Enabled: true } item
            ? item.Transform : CubeTransform.None;

        // No axes of its own → the BARE form, which is the one that inherits the Y side's roles by
        // name (and the one that survives a re-run whose sweep changed length).
        string body = XAxisPins.Count == 0 ? cubeName : BuildExplicitXSpec(cubeName);

        return transform == CubeTransform.None
            ? body
            : $"{XTransformFunctionName(transform)}({body})";
    }

    /// <summary>
    /// Re-derives the X spec after the Y SIDE's roles changed — called from the Y-side flush, before
    /// it rebuilds.
    ///
    /// <para>Without this an EXPLICIT X spec keeps the roles it was written with: press Fam on RFfreq
    /// and the Y half becomes <c>Gain[:, ~]</c> while the X half still says <c>…[:, 0, …]</c>, which
    /// the resolver then correctly refuses ("both sides must iterate the same family axis"). The X
    /// rows are rebuilt first, since they are what the spec is composed from and they read the Y
    /// slice.</para>
    /// </summary>
    internal void RegenerateXSpecForYRoleChange()
    {
        if (!_trace.IsVersus) return;
        if (SelectedXSignal is not { CubeName: { } name }) return;

        bool saved = _suppressVersusCallback;
        _suppressVersusCallback = true;
        try
        {
            RebuildXRows();                    // re-read the NEW inherited roles
            _trace.XSpec = ComposeXSpec(name);
        }
        finally { _suppressVersusCallback = saved; }
        XRoleSummary = BuildRoleSummary();
    }

    /// <summary>The exact spelling the expression engine expects — its function switch is
    /// case-sensitive, so "db20" is an unknown function while "dB20" is not.</summary>
    private static string XTransformFunctionName(CubeTransform t) => t switch
    {
        CubeTransform.dB20  => "dB20",
        CubeTransform.dB10  => "dB10",
        CubeTransform.dB    => "dB",
        CubeTransform.Mag   => "mag",
        CubeTransform.Phase => "phase",
        CubeTransform.Real  => "real",
        CubeTransform.Imag  => "imag",
        _                   => t.ToString().ToLowerInvariant(),
    };

    private string BuildExplicitXSpec(string cubeName)
    {
        if (XEntry()?.Data is not { } ds || !ds.Contains(cubeName)) return cubeName;
        var cube   = ds[cubeName];
        var ySlice = _trace.Slice;

        var tokens = new List<string>(cube.Rank);
        foreach (var axis in cube.Axes)
        {
            // A shared axis takes the Y side's own role and pin — there is no X-side row for it.
            AxisSlice? y = null;
            if (ySlice is not null)
                foreach (var sl in ySlice)
                    if (string.Equals(sl.AxisName, axis.Name, StringComparison.Ordinal)) { y = sl; break; }

            if (y is { } ys)
            {
                tokens.Add(ys.Role switch
                {
                    AxisRole.KeepAsX       => ":",
                    AxisRole.FamilyIterate => "~",
                    _ => !string.IsNullOrEmpty(ys.Label) ? $"\"{ys.Label}\"" : ys.Index.ToString(),
                });
                continue;
            }

            var row = XAxisPins.FirstOrDefault(r =>
                string.Equals(r.AxisName, axis.Name, StringComparison.Ordinal));
            tokens.Add(row?.Token ?? (axis.Name == DefaultXAxisName(cube) ? ":" : "0"));
        }
        return $"{cubeName}[{string.Join(", ", tokens)}]";
    }

    /// <summary>
    /// What the X side takes from the Y side, in words — naming each SHARED axis and what it is doing,
    /// including the value of a pinned one. This carries the whole job the duplicated role rows used
    /// to do: the user needs to KNOW that the family and the swept axis apply to X too (and at which
    /// frequency), not to set them twice.
    /// </summary>
    private string BuildRoleSummary()
    {
        if (_trace.Slice is not { Length: > 0 } slice) return "X side is sliced as typed.";

        // Only axes the X quantity actually has are shared with it.
        var xAxisNames = new HashSet<string>(StringComparer.Ordinal);
        if (SelectedXSignal is { CubeName: { } name } && XEntry()?.Data is { } ds && ds.Contains(name))
            foreach (var ax in ds[name].Axes) xAxisNames.Add(ax.Name);

        var parts = new List<string>();
        foreach (var sl in slice)
        {
            if (xAxisNames.Count > 0 && !xAxisNames.Contains(sl.AxisName)) continue;
            parts.Add(sl.Role switch
            {
                AxisRole.KeepAsX       => $"{sl.AxisName} = X",
                AxisRole.FamilyIterate => $"{sl.AxisName} = family",
                _                      => $"{sl.AxisName} = fixed at {PinnedDisplay(sl)}",
            });
        }
        return parts.Count == 0 ? "" : "Shares the axes above: " + string.Join(", ", parts);
    }

    /// <summary>How a pinned shared axis reads — the Y row's own displayed option when there is one
    /// (so "2 GHz", not "0"), else the slice's label or index.</summary>
    private string PinnedDisplay(AxisSlice sl)
    {
        var yRow = AxisRoles.FirstOrDefault(r =>
            string.Equals(r.AxisName, sl.AxisName, StringComparison.Ordinal));
        if (yRow is not null && yRow.PinOptions.Count > 0)
            return yRow.PinOptions[Math.Clamp(yRow.PinIndex, 0, yRow.PinOptions.Count - 1)];
        return !string.IsNullOrEmpty(sl.Label) ? sl.Label : sl.Index.ToString();
    }

    /// <summary>Cube name out of an X spec — bare, bracketed, or wrapped in a transform call
    /// (<c>mag(HB1.V[…])</c>). The parser answers when it can; the textual fallback covers a spec
    /// whose cube is not in the current source (a stale binding still has to name its cube).</summary>
    private string CubeNameOf(string spec)
    {
        if (XEntry()?.Data is { } ds
            && CubeTraceSpecParser.TryParse(spec, ds, out var parsed, out _, out _, out _))
            return parsed;

        string t = spec.Trim();
        int paren = t.IndexOf('(');
        if (paren > 0 && t.EndsWith(")", StringComparison.Ordinal)) t = t[(paren + 1)..^1].Trim();
        int b = t.IndexOf('[');
        return (b < 0 ? t : t[..b]).Trim();
    }
}
