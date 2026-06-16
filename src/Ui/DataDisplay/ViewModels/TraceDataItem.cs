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
    /// True when the source file is missing or the row/col is out of range
    /// for the currently loaded file.  The item is still selectable (it
    /// represents the current, unresolvable trace state) but is shown in
    /// a warning style (red italic).
    /// </summary>
    public bool IsBroken { get; }

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

        (Label, IsEnabled) = derived switch
        {
            // Stability circles: valid on Smith/Polar (rendered as circles) and
            // Table (rendered as per-frequency Inside/Outside); disabled on Rect.
            DerivedParameters.SourceStabilityCircle =>
                ($"{prefix}Source Stability Circles", isComplex || isTable),

            DerivedParameters.LoadStabilityCircle =>
                ($"{prefix}Load Stability Circles",   isComplex || isTable),

            // Scalar stability / gain: valid on Rect and Table; disabled on Smith/Polar.
            DerivedParameters.MuPrime =>
                ($"{prefix}Source Stability µ'", !isComplex),

            DerivedParameters.Mu =>
                ($"{prefix}Load Stability µ",    !isComplex),

            DerivedParameters.MaxGain =>
                ($"{prefix}MaxGain",             !isComplex),

            _ => ($"{prefix}?", false)
        };
    }
}
