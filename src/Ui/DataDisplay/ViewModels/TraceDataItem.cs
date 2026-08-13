// ================================================================
//  TraceDataItem.cs  —  one selectable item in the trace data ComboBox
// ================================================================

using System.IO;
using RfCore;
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

        // R-stb-5, expressed once from the metric's own kind rather than re-listed per member, so a
        // metric added to DerivedParameters later cannot be forgotten here.
        bool enabled = derived.IsCircleLocus() ? (isComplex || isTable)
                     : derived.IsScalarVsFrequency() ? !isComplex
                     : false;

        DisabledReason = enabled ? null
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
            _                                       => "?",
        };
        IsEnabled = enabled;
    }
}
