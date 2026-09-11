// ================================================================
//  TraceDataItem.cs  —  one selectable item in the trace data ComboBox
// ================================================================

using System.IO;
using RfCore;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed class TraceDataItem
{
    public DataSourceEntryViewModel Entry     { get; }
    public int               Row       { get; }
    public int               Col       { get; }
    public DerivedParameters Derived   { get; }
    public string            Label     { get; }
    public bool              IsEnabled { get; }

    /// <summary>
    /// Why this item cannot be picked on the current plot, or null when it can (R-stb-5).
    /// Scalars-versus-frequency belong on a rectangular plot and Γ-plane loci on Smith/Polar; the
    /// two do not mix in one plot, so the unavailable ones are offered DISABLED WITH A REASON
    /// rather than silently producing an empty trace.
    /// </summary>
    public string? DisabledReason { get; init; }

    /// <summary>Tooltip text: the disabled reason when present, else the label. A plain-string
    /// binding target — `TargetNullValue={Binding ...}` is not evaluated by Avalonia (the binding
    /// object itself becomes the fallback value, rendered via ToString()), so the fallback must be
    /// computed here rather than in the view.</summary>
    public string TooltipText => DisabledReason ?? Label;

    private readonly string? _pickerText;

    /// <summary>
    /// What the ComboBox DRAWS for this item — the label for every kind of item except a WSProbe
    /// metric, which carries a shortened wording of its own (<see cref="WspMetricInfo.Short"/>).
    ///
    /// <para><b>It is a separate property from <see cref="Label"/> on purpose.</b> The label is the
    /// item's identity: <see cref="TooltipText"/> falls back to it, and the documentation figures
    /// choose a metric by it. Shortening THAT would have changed what the tooltip says as well,
    /// which is the one place the full wording and its citation still have to appear — the picker
    /// is where a line that long stops being readable, not where the information stops being
    /// wanted.</para>
    /// </summary>
    public string PickerText => _pickerText ?? Label;

    /// <summary>
    /// True when the source file is missing or the row/col is out of range
    /// for the currently loaded file.  The item is still selectable (it
    /// represents the current, unresolvable trace state) but is shown in
    /// a warning style (red italic).
    /// </summary>
    public bool IsBroken { get; }

    /// <summary>A V/I placeholder for an analysis group whose cube is missing.</summary>
    public bool IsAbsent { get; init; }

    /// <summary>Picker group header this item belongs to (e.g. "HB1", "Measurements", "S-Parameters").</summary>
    public string Group { get; init; } = "";

    // ---- Cube-bound discriminator (Phase 7.2c-a) ---------------------------

    /// <summary>True for cube-bound items; false for matrix / derived items.</summary>
    public bool         IsCubeBound { get; }
    public string?      CubeName    { get; }
    public AxisSlice[]? Slice       { get; }

    // ---- Matrix element constructor ----------------------------------------

    public TraceDataItem(DataSourceEntryViewModel entry, MatrixType mt, int row, int col,
                         bool omitFilePrefix = false, bool isBroken = false)
    {
        Entry     = entry;
        Row       = row;
        Col       = col;
        Derived   = DerivedParameters.None;
        IsBroken  = isBroken;
        IsEnabled = true;

        string el = $"{mt}({row + 1},{col + 1})";
        Label = omitFilePrefix ? el : $"{Path.GetFileNameWithoutExtension(entry.DisplayName)}..{el}";
    }

    // ---- Cube-bound constructor (Phase 7.2c-a) -----------------------------

    public TraceDataItem(DataSourceEntryViewModel entry, string cubeName, AxisSlice[] slice,
                         string label, bool isEnabled = true)
    {
        Entry       = entry;
        Row         = 0;
        Col         = 0;
        Derived     = DerivedParameters.None;
        IsBroken    = false;
        IsCubeBound = true;
        CubeName    = cubeName;
        Slice       = slice;
        Label       = label;
        IsEnabled   = isEnabled;
    }

    // ---- WSProbe metric constructor (WSP-4 R-wsp4-5) -----------------------

    /// <summary>The WSProbe metric this item selects, or <see cref="WspMetric.None"/> for every
    /// other kind of item.</summary>
    public WspMetric WspMetric { get; init; } = WspMetric.None;

    /// <summary>
    /// One quantity of the reference document, over a run's <c>wsp</c> matrix.
    ///
    /// <para>It is a CUBE-BOUND item — its <see cref="CubeName"/> is the run's own <c>…wsp</c> cube
    /// and its <see cref="Slice"/> is authored against the metric's axes — so everything the picker,
    /// the axis-role editor and the persistence already do with a cube trace applies unchanged. The
    /// probe itself is NOT part of the item: it is chosen on the card's own WSProbe section, so the
    /// list is one entry per metric rather than one per (probe x metric).</para>
    ///
    /// <para>Gating is the metric's own (<see cref="WspMetrics.DisabledReasonOn"/>): a margin is a
    /// real scalar and belongs on a rectangular plot, a loop gain has no Smith grid, and each is
    /// offered DISABLED WITH A REASON on the wrong plot type rather than vanishing — the same rule
    /// the derived metrics already follow (R-wsp4-14g).</para>
    /// </summary>
    public TraceDataItem(DataSourceEntryViewModel entry, string wspCubeName, AxisSlice[] slice,
                         WspMetric metric, PlotType plotType)
    {
        Entry       = entry;
        Row         = 0;
        Col         = 0;
        Derived     = DerivedParameters.None;
        IsBroken    = false;
        IsCubeBound = true;
        CubeName    = wspCubeName;
        Slice       = slice;
        WspMetric   = metric;

        var info = WspMetrics.Info(metric);
        DisabledReason = WspMetrics.DisabledReasonOn(metric, plotType);
        IsEnabled      = DisabledReason is null;
        Label          = info is { } i ? $"{i.Name} — {i.Description}" : WspMetrics.Name(metric);
        _pickerText    = info is { } p ? $"{p.Name} — {p.Short}" : null;
    }

    // ---- Derived parameter constructor -------------------------------------

    public TraceDataItem(DataSourceEntryViewModel entry, DerivedParameters derived,
                         PlotType plotType, bool omitFilePrefix = false)
    {
        Entry   = entry;
        Row     = 0;
        Col     = 0;
        Derived = derived;
        IsBroken = false;

        bool isComplex = plotType is PlotType.Smith or PlotType.Polar;
        bool isTable   = plotType == PlotType.Table;
        string prefix  = omitFilePrefix ? string.Empty : $"{Path.GetFileNameWithoutExtension(entry.DisplayName)}..";

        // The 3D surface takes NONE of them, and it is asked first because it is the one answer that
        // does not depend on which kind of derived metric this is: a stability circle and a scalar
        // versus frequency are both functions of frequency, and a surface is a function of direction
        // (owner, 2026-09-11 — a 3D plot was offering a long list of quantities it could not draw).
        bool isSurface = plotType == PlotType.Surface3D;

        // R-stb-5, expressed once from the metric's own kind rather than re-listed per member, so a
        // metric added to DerivedParameters later cannot be forgotten here.
        bool enabled = isSurface ? false
                     : derived.IsCircleLocus() ? (isComplex || isTable)
                     : derived.IsScalarVsFrequency() ? !isComplex
                     : false;

        DisabledReason = enabled ? null
            : isSurface
                ? SurfaceResolve.NotOnASurfaceRefusal
            : derived.IsCircleLocus()
                ? "Stability circles are loci in the Γ plane — add them to a Smith or Polar plot."
                : "This is a scalar versus frequency — add it to a rectangular (or table) plot.";

        Label = $"{prefix}" + derived switch
        {
            DerivedParameters.SourceStabilityCircle => "Source Stability Circles",
            DerivedParameters.LoadStabilityCircle   => "Load Stability Circles",
            DerivedParameters.MuPrime               => "Source Stability µ'",
            DerivedParameters.Mu                    => "Load Stability µ",
            DerivedParameters.MaxGain               => "MaxGain",
            DerivedParameters.K                     => "Rollett K",
            DerivedParameters.DeltaMag              => "|Δ|",
            DerivedParameters.Passivity             => "Passivity σmax",
            DerivedParameters.GroupDelay            => "Group Delay (ns)",
            DerivedParameters.MagZ                  => "|Z| (Ω)",
            DerivedParameters.Esr                   => "ESR (Ω)",
            DerivedParameters.Reactance             => "Reactance X (Ω)",
            DerivedParameters.Ceff                  => "C effective (F)",
            DerivedParameters.Leff                  => "L effective (H)",
            DerivedParameters.QFactor               => "Q",
            _                                       => "?",
        };
        IsEnabled = enabled;
    }
}
