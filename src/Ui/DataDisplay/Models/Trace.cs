// ================================================================
//  Trace.cs  —  Trace model  (pure data + logic, no drawing)
//
//  Ported from splotRF/src/Models/Trace.cs — namespace renamed to
//  CircuitRF.Ui.DataDisplay.  Trace.Data stays as SNP (DataSet/
//  DataCube retarget is Phase 7.2).
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Avalonia;
using NumFlat;
using RfCore;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Ui.DataDisplay
{
    // ============================================================
    //  DependentVarFormat
    // ============================================================

    public enum DependentVarFormat
    {
        Complex, Db, Mag, Phase, Real, Imaginary
    }

    public static class DependentVarFormatExtensions
    {
        public static string Description(this DependentVarFormat f) => f switch
        {
            DependentVarFormat.Complex   => "complex",
            DependentVarFormat.Db        => "dB",
            DependentVarFormat.Mag       => "mag",
            DependentVarFormat.Phase     => "phase",
            DependentVarFormat.Real      => "real",
            DependentVarFormat.Imaginary => "imaginary",
            _                            => f.ToString()
        };
    }

    // ============================================================
    //  DerivedParameters
    // ============================================================

    public enum DerivedParameters
    {
        None, SourceStabilityCircle, LoadStabilityCircle, MaxGain, Mu, MuPrime,
    }

    public static class DerivedParametersExtensions
    {
        public static string Description(this DerivedParameters d) => d switch
        {
            DerivedParameters.SourceStabilityCircle => "Source Stability Circles",
            DerivedParameters.LoadStabilityCircle   => "Load Stability Circles",
            DerivedParameters.MuPrime               => "Source Stability, µ'",
            DerivedParameters.Mu                    => "Load Stability, µ",
            DerivedParameters.MaxGain               => "Max Gain",
            _                                       => ""
        };
    }

    // ============================================================
    //  CubeTransform / AxisRole / AxisSlice  (Phase 7.2c-a)
    // ============================================================

    /// <summary>Element-wise transform applied to a cube-bound trace value.</summary>
    public enum CubeTransform { None, dB20, dB10, dB, Mag, Phase, Real, Imag, Conj }

    /// <summary>How a DataCube axis is consumed when building a 1-D trace.</summary>
    public enum AxisRole { PinToIndex, KeepAsX, FamilyIterate }

    /// <summary>Per-axis slice directive for a cube-bound trace (one entry per cube axis, in axis order).
    /// For a kept sub-range (KeepAsX + RangeEndExclusive >= 0) the axis is sliced to [RangeStart, RangeEndExclusive)
    /// (end-exclusive). RangeEndExclusive &lt; 0 means the whole axis (":"/All).</summary>
    public readonly record struct AxisSlice(
        string AxisName, AxisRole Role, int Index,
        int RangeStart = 0, int RangeEndExclusive = -1,   // RangeEndExclusive < 0 ⇒ whole axis
        string Label = "")                                  // net-name label for quoted shorthand; "" ⇒ emit index
    {
        public bool IsNarrowedRange => Role == AxisRole.KeepAsX && RangeEndExclusive >= 0;
    }

    // ============================================================
    //  Trace
    // ============================================================

    public class Trace
    {
        // ---- Identity ---------------------------------------------------

        public Guid Id { get; set; } = Guid.NewGuid();

        // ---- Data source ------------------------------------------------

        private SNP _data = new SNP(new double[] { 1e9 }, 2);
        public SNP Data
        {
            get => _data;
            set => _data = value;
        }

        // ---- Matrix selectors -------------------------------------------

        private int _row;
        public int Row
        {
            get => _row;
            set { if (value >= 0 && value < Data.Ports) _row = value; }
        }

        private int _col;
        public int Col
        {
            get => _col;
            set { if (value >= 0 && value < Data.Ports) _col = value; }
        }

        // ---- Reference impedance ----------------------------------------

        private Complex _z0 = new Complex(50, 0);
        public Complex Z0
        {
            get => _z0;
            set => _z0 = value;
        }

        // ---- Matrix type ------------------------------------------------

        private MatrixType _matrixType = MatrixType.S;
        public MatrixType MatrixType
        {
            get => _matrixType;
            set => _matrixType = value;
        }

        // ---- Y-axis format ----------------------------------------------

        private DependentVarFormat _yAxis = DependentVarFormat.Complex;
        public DependentVarFormat YAxis
        {
            get => _yAxis;
            set => _yAxis = value;
        }

        // ---- Derived parameter ------------------------------------------

        private DerivedParameters _derived = DerivedParameters.None;
        public DerivedParameters Derived
        {
            get => _derived;
            set
            {
                _derived = value;
                if (IsDerived)
                {
                    _row        = 0;
                    _col        = 0;
                    _matrixType = MatrixType.S;
                    _yAxis      = IsStabilityCircle
                        ? DependentVarFormat.Complex
                        : value == DerivedParameters.MaxGain
                            ? DependentVarFormat.Db
                            : DependentVarFormat.Mag;
                    if (StabilityCircleCentres.Count == 0 && Markers.Count > 0) BuildDerivedPath(PlotType.Smith, FreqUnit.GHz);
                    foreach (var m in Markers)
                    {
                        m.PositionStatic = new System.Numerics.Vector2(0, 0);
                        int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq - 1e-6);
                        if (fi < 0) fi = Data.Frequencies.Length - 1;
                        SnapMarkerToStabilityCircle(m, fi);
                    }
                }
            }
        }

        public bool IsDerived => Derived != DerivedParameters.None;

        public bool IsStabilityCircle =>
            Derived == DerivedParameters.LoadStabilityCircle ||
            Derived == DerivedParameters.SourceStabilityCircle;

        // ---- Secondary axis ---------------------------------------------

        private bool _useSecondaryAxis;
        public bool UseSecondaryAxis
        {
            get => _useSecondaryAxis;
            set => _useSecondaryAxis = value;
        }

        // ---- Display / serialisation properties -------------------------

        public MatrixFormat    MatrixFormat          { get; set; } = MatrixFormat.MA;
        public TraceProperties Properties            { get; set; } = new TraceProperties();
        public double          ColumnWidth           { get; set; } = 115;
        /// <summary>Per-X-group logical column width (0 = fall back to plot.ColumnWidth).</summary>
        public double          XColumnWidth          { get; set; } = 0;
        /// <summary>Per-family-curve column widths (key = FamilyCurveIndex). Empty = fall back to ColumnWidth.</summary>
        public Dictionary<int, double> FamilyColumnWidths { get; } = new();
        public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.F;
        public int             MaximumFractionDigits { get; set; } = 3;

        /// <summary>Logical reference persisted in .cdd. "run.npy" (or null) = selected datasource sentinel;
        /// "schemName/run.npy" = a specific sim run; rooted path = explicit Touchstone file.</summary>
        public string? SourceRef  { get; set; }

        /// <summary>Resolved absolute path for the source file (runtime only, not persisted directly).</summary>
        public string? SourcePath { get; set; }

        // ---- Contour trace (Phase 7.4d) ---------------------------------

        /// <summary>
        /// When non-null, this trace renders loadpull contours (iso-lines + fill).
        /// Overrides all SNP / cube-bound behaviour in the renderers.
        /// </summary>
        public ContourData? ContourData { get; set; }

        public bool IsContourTrace => ContourData != null;

        /// <summary>When non-null, this trace is a summary-table column (Phase 7.5). Mutually exclusive
        /// with ContourData; only meaningful on a Table plot.</summary>
        public SummaryColumnData? SummaryColumn { get; set; }

        public bool IsSummaryColumn => SummaryColumn != null;

        // ---- Cube-native binding (Phase 7.2c-a) -------------------------
        //
        //  Null CubeName ⇒ this trace uses the legacy SNP/matrix path.
        //  The owner (PlotInspectorViewModel) resolves SourcePath+CubeName+Slice
        //  to numeric arrays and injects them via SetCubeData; Trace never
        //  holds a DataSet reference.

        public string?       CubeName       { get; set; }
        public AxisSlice[]?  Slice          { get; set; }
        public CubeTransform Transform      { get; set; } = CubeTransform.None;
        public string?       InvalidSpecText { get; set; }

        /// <summary>Full element-wise expression string (e.g. <c>mag(V[:, 0, 0]) + mag(V[:, 0, 1])</c>).
        /// When non-null, the owner resolves via <c>TraceExpression</c> instead of the single-slice path.
        /// Supersedes CubeName/Slice/Transform for value production.</summary>
        public string?       Expression      { get; set; }

        /// <summary>Set by the owner when TraceExpression evaluation fails; cleared on success.</summary>
        public string?       ExpressionError { get; set; }

        /// <summary>True when the last BuildPath produced a Rect plot but the cube value is complex with no
        /// scalar transform (None/Conj) — Rect can only plot scalars. Drives a soft "&lt;invalid&gt;" Y-axis label.</summary>
        public bool RectValueInvalid { get; private set; }

        public bool          IsCubeBound => CubeName is not null || Expression is not null;

        // ── Performance guardrail (Phase 7.3) ────────────────────────────────────
        // Max curves a single family trace renders. Single source of truth — clamp +
        // one Message past it. Raise/lower here for perf testing.
        public const int MaxFamilyCurves = 101;

        /// <summary>Axis name emitted by HbEngine for the single-tone spectral axis.
        /// Matched case-sensitively against CubeXAxisName to drive stem rendering.</summary>
        public const string HarmonicAxisName = "harmonic";

        /// <summary>Axis name emitted by HbEngine for the two-tone spectral axis. Its VALUES are the
        /// signed mixing-product frequencies (k₁f₁+k₂f₂, can be negative), so a stem plot over it is a
        /// spectrum directly — no order→frequency reconstruction needed.</summary>
        public const string MixIndexAxisName = "mixIndex";

        /// <summary>True when this trace's X-axis is harmonic index (HB spectrum) — drives stem rendering.
        /// Single-curve only: a harmonic-X <em>family</em> keeps its geometry in FamilyCurves (not Points),
        /// so it is handled by the generic cube-X marker path instead (which is family-aware).</summary>
        public bool IsHarmonicStem => IsCubeBound
            && !IsFamily
            && string.Equals(_cubeXAxisName, HarmonicAxisName, StringComparison.Ordinal);

        /// <summary>True when this trace's X-axis is the two-tone mixIndex spectral axis. Drives stem
        /// rendering (a connected line would zig-zag, since mix products are stored in lattice order,
        /// not frequency order). Markers use the generic cube-X path (the axis values are the physical
        /// frequencies), so — unlike <see cref="IsHarmonicStem"/> — this is NOT excluded from
        /// <see cref="IsCubeXMarker"/>.</summary>
        public bool IsMixIndexStem => IsCubeBound
            && !IsFamily
            && string.Equals(_cubeXAxisName, MixIndexAxisName, StringComparison.Ordinal);

        /// <summary>One curve of a family trace: its iterated-axis value (for the legend) + its points.</summary>
        public sealed class FamilyCurve
        {
            public double     AxisValue  { get; init; }
            public string?    AxisLabel  { get; init; }
            public List<Vector2> Points  { get; } = new();
            // Raw values (not transformed) — used by TableRenderer for cell formatting.
            public Complex[]? RawComplex { get; init; }
            public double[]?  RawReal    { get; init; }
        }

        /// <summary>N curves when IsFamily; empty otherwise. Derived (never serialized) — rebuilt on load.</summary>
        public List<FamilyCurve> FamilyCurves { get; } = new();

        /// <summary>Name of the iterated (family) axis — the legend title.</summary>
        public string? FamilyAxisName { get; set; }

        /// <summary>Unit of the iterated (family) axis (e.g. "Hz" when the family axis is a frequency,
        /// such as the HB "harmonic" axis whose coordinate values are physical frequencies). Null when
        /// the axis is unitless. Used to unit-scale the family-curve value in a marker InfoBox.</summary>
        public string? FamilyAxisUnit { get; set; }

        /// <summary>True when the slice marks an axis FamilyIterate.</summary>
        public bool IsFamily => Slice is not null && Array.Exists(Slice, s => s.Role == AxisRole.FamilyIterate);

        // ---- Per-port source reference impedance (Phase 7.2f) -----------
        //
        //  Set by the owner when it binds/refreshes a scattering trace.
        //  When SourceZ0IsUnusual, the matrix path uses SourceZ0PerPort
        //  directly and the user Z0 box is disabled (no renorm offered).

        /// <summary>Per-port source reference impedance (index k = port k+1), from the source
        /// 'Z0' cube.  Null ⇒ uniform source (use Data.Z0).  When non-null AND
        /// SourceZ0IsUnusual, compute uses these values directly (no renorm).</summary>
        public Complex[]? SourceZ0PerPort { get; set; }

        /// <summary>True when the source reference is non-uniform-across-ports OR complex
        /// (set by the owner from DataSetBuilder.ClassifyZ0).  Drives compute path + textbox
        /// gating.</summary>
        public bool SourceZ0IsUnusual { get; set; }

        // Cache filled by SetCubeData; cleared on each call.
        private double[]?  _cubeXValues;
        private Complex[]? _cubeComplexValues;
        private double[]?  _cubeRealValues;
        private string     _cubeXAxisName = "";
        private string?    _cubeXUnit;
        // Per-X axis labels (e.g. two-tone "(k1,k2)" mix-product tags) — used by the marker readout.
        private string[]?  _cubeXLabels;

        // When a spectral axis ("harmonic"/"mixIndex") is PINNED (not the X axis) — e.g. the user plots
        // one harmonic/product vs a Pin sweep — this carries the pinned line's tag + frequency so the
        // marker box still reports which spectral line is shown. Null name = no pinned spectral axis.
        private string? _pinnedSpectralName;
        private string? _pinnedSpectralLabel;
        private double  _pinnedSpectralFreqHz = double.NaN;

        /// <summary>Owner-supplied: identifies a pinned spectral line ("harmonic"/"mixIndex"), its tag
        /// (order or "(k1,k2)"), and its frequency (Hz; NaN to omit the freq row), for the marker box.</summary>
        public void SetPinnedSpectral(string? axisName, string? label, double freqHz)
        {
            _pinnedSpectralName   = axisName;
            _pinnedSpectralLabel  = label;
            _pinnedSpectralFreqHz = freqHz;
        }

        // Per-X fundamental (Hz) injected by the owner before SetCubeData/SetFamilyData.
        // Non-null only for single-tone HB spectrum traces; null for all other trace types.
        private double[]? _f0ByX;
        public void SetSpectrumFundamentals(double[]? f0ByX) => _f0ByX = f0ByX;

        private bool _cubeIsScalar;
        private PlotType _lastPlotType = PlotType.Rect;
        public  bool CubeIsScalar => _cubeIsScalar;

        /// <summary>True when a scalar (rank-0) cube is bound while the plot type is not Table. Scalars render
        /// only on a Table; elsewhere the trace draws nothing and its label shows a soft "&lt;invalid&gt;".</summary>
        public bool ScalarOnNonTableInvalid { get; private set; }

        // ---- Cube data read accessors (for TableRenderer — no recompute) -----

        public IReadOnlyList<double>?  CubeXValues   => _cubeXValues;
        public IReadOnlyList<Complex>? CubeComplex   => _cubeComplexValues;
        public IReadOnlyList<double>?  CubeReal      => _cubeRealValues;
        public string                  CubeXAxisName => _cubeXAxisName;
        public string?                 CubeXUnit     => _cubeXUnit;

        // ---- Markers ----------------------------------------------------

        public List<Marker> Markers { get; } = new();

        // ---- Pre-built geometry (world coordinates) ---------------------

        public List<Vector2> Points                     { get; private set; } = new();
        public List<Vector2> StabilityCircleCentres     { get; private set; } = new();
        public List<double>  StabilityCircleRadii       { get; private set; } = new();
        public List<bool>    StabilityCircleStableInside { get; private set; } = new();

        // ---- Frequency range --------------------------------------------

        public double MinFreq => IsCubeBound
            ? (_cubeXValues?.Length > 0 ? _cubeXValues[0]  : double.NaN)
            : (Data.Frequencies.Length > 0 ? Data.Frequencies.Min() : double.NaN);
        public double MaxFreq => IsCubeBound
            ? (_cubeXValues?.Length > 0 ? _cubeXValues[^1] : double.NaN)
            : (Data.Frequencies.Length > 0 ? Data.Frequencies.Max() : double.NaN);

        // ---- Description string -----------------------------------------

        /// <summary>Full description including the source-file prefix.</summary>
        public string Description => DescriptionFor(includePrefix: true);

        /// <summary>Short description with no source-file prefix.</summary>
        public string ShortDescription => DescriptionFor(includePrefix: false);

        /// <summary>
        /// DataCube-shorthand label for use as a Table column header, e.g. <c>V[0, 1, :]</c>.
        /// Pinned axes show their integer index; the kept (X) axis shows ':'.
        /// Transform prefix is prepended when non-None (e.g. <c>dB20 V[0, 1, :]</c>).
        /// Falls back to <see cref="ShortDescription"/> for non-cube traces.
        /// Note: uses index-form for pinned tokens (the documented fallback).
        /// </summary>
        public string CubeShorthand
        {
            get
            {
                string baseLabel;
                if (InvalidSpecText is not null)        baseLabel = $"{InvalidSpecText} <invalid>";
                else if (Expression is not null)        baseLabel = Expression;
                else if (!IsCubeBound || Slice is null) baseLabel = ShortDescription;
                else                                    baseLabel = BuildPickerExpression();
                if (ScalarOnNonTableInvalid && !baseLabel.Contains("<invalid")) baseLabel += " <invalid>";
                return baseLabel;
            }
        }

        /// <summary>
        /// Y-axis label for this trace on a Rect plot: the cube shorthand (net-name form, e.g.
        /// mag(V[:, "Vout2", 2])), optionally source-prefixed, with soft suffixes:
        ///   • " &lt;invalid&gt;" when the value can't render (parse error OR complex-on-Rect),
        ///   • " dimension mismatch" when this trace's cube X-axis differs from the plot's X-axis.
        /// Network (SNP) traces fall back to the supplied minimal label.
        /// </summary>
        public string RectYLabel(string networkFallback, bool showFilePrefix, bool dimensionMismatch)
        {
            if (IsContourTrace) return "";
            string baseLabel;
            if (IsCubeBound)
            {
                baseLabel = CubeShorthand;
                if (showFilePrefix && SourcePath != null)
                    baseLabel = System.IO.Path.GetFileNameWithoutExtension(SourcePath) + ".." + baseLabel;
                if (RectValueInvalid && !baseLabel.Contains("<invalid"))
                    baseLabel += " <invalid: complex on scalar plot type>";
            }
            else
            {
                baseLabel = networkFallback;
            }
            if (dimensionMismatch) baseLabel += " dimension mismatch";
            return baseLabel;
        }

        /// <summary>Computes the function-call shorthand from CubeName/Slice/Transform only,
        /// ignoring Expression.  Used by the owner to sync Expression after picker edits.</summary>
        internal string BuildPickerExpression()
        {
            if (CubeName is null || Slice is null) return ShortDescription;
            if (Slice.Length == 0)   // scalar (rank-0) cube — no axes to slice
                return Transform == CubeTransform.None
                    ? CubeName
                    : $"{TransformFunctionName(Transform)}({CubeName})";
            // A single whole-axis X (e.g. "PDC[:]") reads better bare.
            if (Slice.Length == 1 && Slice[0].Role == AxisRole.KeepAsX && !Slice[0].IsNarrowedRange)
                return Transform == CubeTransform.None
                    ? CubeName
                    : $"{TransformFunctionName(Transform)}({CubeName})";
            var parts = Slice.Select(s =>
                s.Role == AxisRole.KeepAsX         ? ":"
                : s.Role == AxisRole.FamilyIterate ? "~"
                : (s.AxisName is "i" or "j")       ? (s.Index + 1).ToString()   // 1-based port number (S[:, 2, 1] = S21)
                : !string.IsNullOrEmpty(s.Label)   ? $"\"{s.Label}\""
                :                                    s.Index.ToString());
            var inner = string.Join(", ", parts);
            if (Transform == CubeTransform.None)
                return $"{CubeName}[{inner}]";
            return $"{TransformFunctionName(Transform)}({CubeName}[{inner}])";
        }

        /// <summary>Maps a CubeTransform to the exact expression-engine function name.
        /// Case matters: the evaluator's function switch is case-sensitive and expects
        /// "dB"/"dB20"/"dB10" (capital B) — lower-casing the enum name (e.g. "db20")
        /// produces an UnknownFunction error. mag/phase/real/imag/conj are already lowercase.</summary>
        private static string TransformFunctionName(CubeTransform t) => t switch
        {
            CubeTransform.dB20  => "dB20",
            CubeTransform.dB10  => "dB10",
            CubeTransform.dB    => "dB",
            CubeTransform.Mag   => "mag",
            CubeTransform.Phase => "phase",
            CubeTransform.Real  => "real",
            CubeTransform.Imag  => "imag",
            CubeTransform.Conj  => "conj",
            _                   => t.ToString().ToLowerInvariant(),
        };

        private string DescriptionFor(bool includePrefix)
        {
            string prefix = includePrefix && SourcePath != null
                ? System.IO.Path.GetFileNameWithoutExtension(SourcePath) + ".."
                : "";

            // Contour: a loadpull contour trace has no S-parameter element, so it must not fall
            // through to the S(row+1,col+1) branch (which would mislabel it e.g. "dB(S(1,1))").
            // Use the contour's own human-readable title (e.g. "P-3dB Pout (dBm)").
            if (IsContourTrace && ContourData is { } cd)
                return $"{prefix}{cd.TitleString()}";

            // Cube-bound: minimal label.
            if (IsCubeBound)
            {
                var lbl = $"{prefix}{Expression ?? CubeName ?? ""}";
                if (ScalarOnNonTableInvalid) lbl += " <invalid>";
                return lbl;
            }

            if (IsDerived)
                return Derived == DerivedParameters.MaxGain && YAxis == DependentVarFormat.Db
                    ? $"{prefix}dB({Derived.Description()})"
                    : $"{prefix}{Derived.Description()}";

            string el = $"({Row + 1},{Col + 1})";
            return YAxis switch
            {
                DependentVarFormat.Db        => $"{prefix}dB({MatrixType}{el})",
                DependentVarFormat.Mag       => $"{prefix}mag({MatrixType}{el})",
                DependentVarFormat.Phase     => $"{prefix}phase({MatrixType}{el})",
                DependentVarFormat.Complex   => $"{prefix}{MatrixType}{el}",
                DependentVarFormat.Real      => $"{prefix}real({MatrixType}{el})",
                DependentVarFormat.Imaginary => $"{prefix}imag({MatrixType}{el})",
                _                            => $"{prefix}{MatrixType}{el}"
            };
        }

        // ---- Constructors -----------------------------------------------

        public Trace(
            SNP                data,
            MatrixType         matrixType,
            int                row,
            int                col,
            DependentVarFormat yAxis,
            bool               secondaryAxis = false,
            TraceProperties?   properties    = null)
        {
            _data             = data;
            _matrixType       = matrixType;
            _yAxis            = yAxis;
            _row              = row;
            _col              = col;
            _useSecondaryAxis = secondaryAxis;
            Properties        = properties ?? new TraceProperties();
            _z0               = new Complex(data.Z0.Real, data.Z0.Imaginary);
        }

        public Trace(Trace src, int incrementColorBy = 0, bool includeMarkers = true)
        {
            _data             = src.Data;
            _matrixType       = src.MatrixType;
            _yAxis            = src.YAxis;
            _row              = src.Row;
            _col              = src.Col;
            _useSecondaryAxis = src.UseSecondaryAxis;
            _derived          = src.Derived;
            Properties        = new TraceProperties(src.Properties, incrementColorBy);
            _z0               = src.Z0;
            SourceRef         = src.SourceRef;
            SourcePath        = src.SourcePath;
            ColumnWidth       = src.ColumnWidth;
            XColumnWidth      = src.XColumnWidth;
            foreach (var kvp in src.FamilyColumnWidths)
                FamilyColumnWidths[kvp.Key] = kvp.Value;
            // Cube-bound identity fields (Phase 7.2c-a).
            CubeName        = src.CubeName;
            Slice           = src.Slice;   // AxisSlice[] is immutable; sharing is safe.
            Transform       = src.Transform;
            Expression      = src.Expression;
            ExpressionError = src.ExpressionError;
            _cubeXValues       = src._cubeXValues;
            _cubeComplexValues = src._cubeComplexValues;
            _cubeRealValues    = src._cubeRealValues;
            _cubeXAxisName     = src._cubeXAxisName;
            _cubeXUnit         = src._cubeXUnit;
            _cubeXLabels       = src._cubeXLabels;
            _pinnedSpectralName   = src._pinnedSpectralName;
            _pinnedSpectralLabel  = src._pinnedSpectralLabel;
            _pinnedSpectralFreqHz = src._pinnedSpectralFreqHz;
            _cubeIsScalar      = src._cubeIsScalar;
            _lastPlotType      = src._lastPlotType;
            _f0ByX             = src._f0ByX;
            // Per-port Z0 (Phase 7.2f).
            SourceZ0PerPort   = src.SourceZ0PerPort;
            SourceZ0IsUnusual = src.SourceZ0IsUnusual;
            // Family axis name (Phase 7.3b).
            FamilyAxisName = src.FamilyAxisName;
            FamilyAxisUnit = src.FamilyAxisUnit;
            // Contour traces: deep-copy authoring fields so paste gets an independent
            // ContourData that re-fits independently.  Grid/Levels/caches are left null
            // and repopulated when the pasted trace's VM calls RebuildContour.
            ContourData = src.ContourData?.Clone();
            SummaryColumn = src.SummaryColumn?.Clone();
            if (includeMarkers)
                foreach (var m in src.Markers)
                    Markers.Add(new Marker(m));
        }

        // ---- Path building ----------------------------------------------

        public void BuildPath(PlotType plotType, FreqUnit freqUnit)
        {
            if      (IsCubeBound) BuildCubePath(plotType, freqUnit);
            else if (IsDerived)   BuildDerivedPath(plotType, freqUnit);
            else                  BuildMatrixPath(plotType, freqUnit);
        }

        // ---- Cube-bound path (Phase 7.2c-a) ----------------------------

        /// <summary>
        /// Injects the 1-D slice arrays produced by the owner (PlotInspectorViewModel)
        /// and immediately rebuilds Points.  Trace never holds a DataSet reference.
        /// </summary>
        public void SetCubeData(double[] xValues, Complex[]? complexValues, double[]? realValues,
                                string xAxisName, string? xUnit,
                                PlotType plotType, FreqUnit freqUnit, string[]? xLabels = null)
        {
            _cubeIsScalar      = false;
            SetPinnedSpectral(null, null, double.NaN);   // derived state — reset on data-set (the VM
                                                         // re-applies it for a single-curve pinned trace)
            // Two-tone spectrum is single-sided: each mixing product is shown at its ABSOLUTE
            // frequency |k1·f1+k2·f2| (negative-frequency reps fold onto the positive side, matching
            // single-tone). The "(k1,k2)" label still identifies the product. Magnitudes are unchanged
            // (conjugate reps), and the retained upper-half-plane reps don't collide after folding.
            _cubeXValues = string.Equals(xAxisName, MixIndexAxisName, StringComparison.Ordinal)
                ? Array.ConvertAll(xValues, Math.Abs)
                : xValues;
            _cubeComplexValues = complexValues;
            _cubeRealValues    = realValues;
            _cubeXAxisName     = xAxisName;
            _cubeXUnit         = xUnit;
            _cubeXLabels       = xLabels;
            BuildCubePath(plotType, freqUnit);
        }

        /// <summary>Binds a scalar (rank-0) cube value. Renders as one Table cell; on any non-Table plot type
        /// the trace produces no geometry and flags ScalarOnNonTableInvalid for a soft label.</summary>
        public void SetScalarCubeData(Complex? complexValue, double? realValue,
                                      PlotType plotType, FreqUnit freqUnit)
        {
            _cubeIsScalar      = true;
            SetPinnedSpectral(null, null, double.NaN);                           // reset derived state
            _cubeXValues       = new[] { 0.0 };                                  // synthetic 1-row anchor
            _cubeComplexValues = complexValue is Complex c ? new[] { c } : null;
            _cubeRealValues    = realValue   is double  r ? new[] { r } : null;
            _cubeXAxisName     = "";
            _cubeXUnit         = null;
            BuildCubePath(plotType, freqUnit);
        }

        private static bool IsFreqUnit(string? unit) => unit is "Hz" or "kHz" or "MHz" or "GHz";

        // Rect scalar Y from one sample (null → skip point).
        private double? RectY(Complex? cz, double? rv)
        {
            if (cz is Complex z)
            {
                double y = Transform switch
                {
                    CubeTransform.dB20  => 20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                    CubeTransform.dB10 or CubeTransform.dB => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                    CubeTransform.Mag   => z.Magnitude,
                    CubeTransform.Phase => z.Phase * 180.0 / Math.PI,
                    CubeTransform.Real  => z.Real,
                    CubeTransform.Imag  => z.Imaginary,
                    _                   => z.Magnitude,
                };
                return double.IsFinite(y) ? y : (double?)null;
            }
            double v = rv!.Value;
            double yr = Transform switch
            {
                CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                CubeTransform.dB10 or CubeTransform.dB => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                CubeTransform.Mag  => Math.Abs(v),
                _                  => v,
            };
            return double.IsFinite(yr) ? yr : (double?)null;
        }

        /// <summary>Injects N pre-sliced family curves (each a rank-1 X/value pair) and builds their Points.
        /// xValues are shared across curves (same X axis). Each curve carries its own complex/real values.</summary>
        public void SetFamilyData(double[] xValues, string xAxisName, string? xUnit, string familyAxisName,
            IReadOnlyList<(double axisValue, string? axisLabel, Complex[]? cz, double[]? rv)> curves,
            PlotType plotType, FreqUnit freqUnit, string? familyAxisUnit = null)
        {
            _cubeIsScalar = false;
            SetPinnedSpectral(null, null, double.NaN);   // a family trace shows the per-curve tag, not a
                                                         // pinned line — clear any stale pinned context
            _lastPlotType = plotType;
            _cubeXValues = xValues; _cubeXAxisName = xAxisName; _cubeXUnit = xUnit;
            _cubeComplexValues = null; _cubeRealValues = null;
            FamilyAxisName = familyAxisName;
            FamilyAxisUnit = familyAxisUnit;
            FamilyCurves.Clear();
            FamilyColumnWidths.Clear();
            Points.Clear();
            RectValueInvalid = false;

            bool isRect = plotType.IsRect();
            bool isHarmonicFamilyX = string.Equals(xAxisName, HarmonicAxisName, StringComparison.Ordinal) && _f0ByX is not null;
            double xScale = IsFreqUnit(xUnit) ? freqUnit.Scale() : 1.0;

            foreach (var (axisValue, axisLabel, cz, rv) in curves)
            {
                var fc = new FamilyCurve { AxisValue = axisValue, AxisLabel = axisLabel,
                                           RawComplex = cz, RawReal = rv };
                int n = xValues.Length;
                bool isComplex = cz is not null;
                if (isRect && isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
                { RectValueInvalid = true; FamilyCurves.Add(fc); continue; }

                for (int i = 0; i < n; i++)
                {
                    if (isRect)
                    {
                        double? y = RectY(isComplex ? cz![i] : (Complex?)null, isComplex ? (double?)null : rv![i]);
                        if (y is double yy)
                        {
                            double xCoord = isHarmonicFamilyX
                                ? xValues[i] * _f0ByX![Math.Min(i, _f0ByX.Length - 1)] * freqUnit.Scale()
                                : xValues[i] * xScale;
                            fc.Points.Add(new Vector2((float)xCoord, (float)yy));
                        }
                    }
                    else if (isComplex)
                    {
                        var z = Transform == CubeTransform.Conj ? Complex.Conjugate(cz![i]) : cz![i];
                        fc.Points.Add(new Vector2((float)z.Real, (float)z.Imaginary));
                    }
                }
                FamilyCurves.Add(fc);
            }
        }

        private void BuildCubePath(PlotType plotType, FreqUnit freqUnit)
        {
            _lastPlotType = plotType;
            Points.Clear();
            RectValueInvalid = false;
            ScalarOnNonTableInvalid = false;
            if (_cubeIsScalar)
            {
                // Scalars render only on a Table (which reads CubeXValues/FormatCubeCell, not Points).
                // Rect/Smith/Polar have nothing meaningful to draw → no points + soft <invalid> label.
                ScalarOnNonTableInvalid = plotType != PlotType.Table;
                return;   // Points already cleared above.
            }
            if (_cubeXValues is null) return;
            if (_cubeComplexValues is null && _cubeRealValues is null) return;

            int  n         = _cubeXValues.Length;
            bool isComplex = _cubeComplexValues is not null;

            if (!plotType.IsRect())
            {
                // Smith / Polar: require a Complex cube; Real yields no points.
                if (!isComplex) return;
                for (int i = 0; i < n; i++)
                {
                    var z = Transform == CubeTransform.Conj
                        ? Complex.Conjugate(_cubeComplexValues![i])
                        : _cubeComplexValues![i];
                    Points.Add(new Vector2((float)z.Real, (float)z.Imaginary));
                }
                return;
            }

            // Rectangular — Rect needs a scalar. A complex cube with a non-scalar transform is invalid.
            if (isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
            {
                RectValueInvalid = true;
                return;
            }

            bool isHarmonicX = _cubeXAxisName == HarmonicAxisName && _f0ByX is not null;
            double xScale = IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;
            for (int i = 0; i < n; i++)
            {
                double x = isHarmonicX
                    ? _cubeXValues[i] * _f0ByX![Math.Min(i, _f0ByX.Length - 1)] * freqUnit.Scale()
                    : _cubeXValues[i] * xScale;
                double y;

                if (isComplex)
                {
                    var z = _cubeComplexValues![i];
                    y = Transform switch
                    {
                        CubeTransform.dB20  => 20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                        CubeTransform.dB10  => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                        CubeTransform.dB    => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                        CubeTransform.Mag   => z.Magnitude,
                        CubeTransform.Phase => z.Phase * 180.0 / Math.PI,
                        CubeTransform.Real  => z.Real,
                        CubeTransform.Imag  => z.Imaginary,
                        _                   => z.Magnitude,
                    };
                }
                else
                {
                    double v = _cubeRealValues![i];
                    y = Transform switch
                    {
                        CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                        CubeTransform.dB10 => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                        CubeTransform.dB   => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                        CubeTransform.Mag  => Math.Abs(v),
                        _                  => v,
                    };
                }

                if (!double.IsFinite(y)) continue;
                Points.Add(new Vector2((float)x, (float)y));
            }
        }

        private void BuildMatrixPath(PlotType plotType, FreqUnit freqUnit)
        {
            Points.Clear();
            StabilityCircleCentres.Clear();
            StabilityCircleRadii.Clear();

            if (Row >= Data.Ports || Col >= Data.Ports) return;

            // Per-port (unusual) path: use SourceZ0PerPort; no user renorm.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                for (int fi = 0; fi < Data.FrequencyCount; fi++)
                {
                    Complex raw;
                    switch (MatrixType)
                    {
                        case MatrixType.S:
                            raw = Data.Matrices[fi][Row, Col];   // stored as-is — renorm disabled
                            break;
                        case MatrixType.Z:
                            raw = RFNetwork.SToZ(Data.Matrices[fi], sourceZ0)[Row, Col];
                            break;
                        default: // Y
                            raw = RFNetwork.SToY(Data.Matrices[fi], sourceZ0)[Row, Col];
                            break;
                    }

                    float x, y;
                    if (plotType.IsRect())
                    {
                        x = (float)(Data.Frequencies[fi] * freqUnit.Scale());
                        y = (float)(YAxis switch
                        {
                            DependentVarFormat.Mag       => raw.Magnitude,
                            DependentVarFormat.Phase     => raw.Phase * 180.0 / Math.PI,
                            DependentVarFormat.Real      => raw.Real,
                            DependentVarFormat.Imaginary => raw.Imaginary,
                            DependentVarFormat.Db        => 20.0 * Math.Log10(Math.Max(raw.Magnitude, 1e-300)),
                            _                            => raw.Magnitude
                        });
                    }
                    else
                    {
                        x = (float)raw.Real;
                        y = (float)raw.Imaginary;
                    }
                    if (!float.IsFinite(y)) continue;
                    Points.Add(new Vector2(x, y));
                }
                return;
            }

            // Uniform/legacy path (unchanged).
            var z0Array = RFNetwork.Z0Array(_z0, Data.Ports);

            for (int fi = 0; fi < Data.FrequencyCount; fi++)
            {
                Complex raw;

                switch (MatrixType)
                {
                    case MatrixType.S:
                    {
                        var mat = Data.Matrices[fi];
                        if (_z0 != Data.Z0)
                            mat = RFNetwork.SToS(mat, Data.Z0, z0Array);
                        raw = mat[Row, Col];
                        break;
                    }
                    case MatrixType.Z:
                        raw = RFNetwork.SToZ(Data.Matrices[fi], Data.Z0)[Row, Col];
                        break;
                    default: // Y
                        raw = RFNetwork.SToY(Data.Matrices[fi], Data.Z0)[Row, Col];
                        break;
                }

                float x, y;

                if (plotType.IsRect())
                {
                    x = (float)(Data.Frequencies[fi] * freqUnit.Scale());
                    y = (float)(YAxis switch
                    {
                        DependentVarFormat.Mag       => raw.Magnitude,
                        DependentVarFormat.Phase     => raw.Phase * 180.0 / Math.PI,
                        DependentVarFormat.Real      => raw.Real,
                        DependentVarFormat.Imaginary => raw.Imaginary,
                        DependentVarFormat.Db        => 20.0 * Math.Log10(Math.Max(raw.Magnitude, 1e-300)),
                        _                            => raw.Magnitude
                    });
                }
                else // Smith / Polar
                {
                    x = (float)raw.Real;
                    y = (float)raw.Imaginary;
                }

                if (!float.IsFinite(y)) continue;
                Points.Add(new Vector2(x, y));
            }
        }

        private void BuildDerivedPath(PlotType plotType, FreqUnit freqUnit)
        {
            Points.Clear();
            StabilityCircleCentres.Clear();
            StabilityCircleRadii.Clear();
            StabilityCircleStableInside.Clear();

            if (Data.Ports != 2) return;

            SNP snp;
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0 && sourceZ0.Length >= 1)
            {
                // Renorm stored matrices to uniform-real so NormalizedS2Port inside
                // stability routines gets an honest starting point.
                int n = Data.Ports;
                var z0Real = new Complex(sourceZ0[0].Real, 0);
                var z0RealArray = RFNetwork.Z0Array(z0Real, n);
                var renormedMats = Data.Matrices
                    .Select(m => RFNetwork.SToS(m, sourceZ0, z0RealArray))
                    .ToArray();
                snp = new SNP(Data.Frequencies, renormedMats,
                              MatrixType.S, Data.Format, z0Real);
            }
            else
            {
                snp = new SNP(Data.Frequencies, Data.Matrices,
                              MatrixType.S, Data.Format, Data.Z0);
            }

            if (plotType.IsRect())
            {
                double[] xData = Data.Frequencies
                    .Select(f => f * freqUnit.Scale()).ToArray();

                double[] yData = Derived switch
                {
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(snp),
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(snp),
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(snp),
                    _                         => Array.Empty<double>()
                };

                for (int i = 0; i < xData.Length && i < yData.Length; i++)
                    if (double.IsFinite(yData[i]))
                        Points.Add(new Vector2((float)xData[i], (float)yData[i]));
            }
            else
            {
                if (Derived == DerivedParameters.LoadStabilityCircle)
                {
                    var (CL, rL) = RFNetwork.StabilityCirclesLoad(snp);
                    bool[] inside = RFNetwork.StableRegionInsideLoad(snp);
                    for (int i = 0; i < CL.Length; i++)
                    {
                        StabilityCircleCentres.Add(new Vector2((float)CL[i].Real, (float)CL[i].Imaginary));
                        StabilityCircleRadii.Add(rL[i]);
                        StabilityCircleStableInside.Add(inside[i]);
                    }
                }
                else if (Derived == DerivedParameters.SourceStabilityCircle)
                {
                    var (CS, rS) = RFNetwork.StabilityCirclesSource(snp);
                    bool[] inside = RFNetwork.StableRegionInsideSource(snp);
                    for (int i = 0; i < CS.Length; i++)
                    {
                        StabilityCircleCentres.Add(new Vector2((float)CS[i].Real, (float)CS[i].Imaginary));
                        StabilityCircleRadii.Add(rS[i]);
                        StabilityCircleStableInside.Add(inside[i]);
                    }
                }
            }
        }

        // ---- Bounding rect in world coords (used by autoscale) ----------

        public Rect PathBoundingRect()
        {
            // Contour traces have no Points — return the grid extent so AutoscaleCore frames it.
            if (IsContourTrace)
            {
                var grid = ContourData?.Grid;
                if (grid == null || grid.XSpace.Length == 0 || grid.YSpace.Length == 0)
                    return default;
                double minX = grid.XSpace[0],                     maxX = grid.XSpace[grid.XSpace.Length - 1];
                double minY = grid.YSpace[0],                     maxY = grid.YSpace[grid.YSpace.Length - 1];
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }

            if (IsFamily)
            {
                bool any = false; float minX = 0, minY = 0, maxX = 0, maxY = 0;
                foreach (var c in FamilyCurves)
                    foreach (var p in c.Points)
                    {
                        if (!any) { minX = maxX = p.X; minY = maxY = p.Y; any = true; }
                        else { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
                    }
                return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : default;
            }
            if (Points.Count == 0) return default;
            float aX = Points.Min(p => p.X), bX = Points.Max(p => p.X);
            float aY = Points.Min(p => p.Y), bY = Points.Max(p => p.Y);
            return new Rect(aX, aY, bX - aX, bY - aY);
        }

        // ---- Data retrieval ---------------------------------------------

        public Complex DataPoint(double freq, Complex? z0Override = null)
        {
            if (IsCubeBound) return new Complex(double.NaN, double.NaN);
            int fi = Array.FindIndex(Data.Frequencies, f => f == freq);
            if (fi < 0) return new Complex(double.NaN, double.NaN);

            if (IsDerived)
            {
                SNP snp;
                if (SourceZ0IsUnusual && SourceZ0PerPort is { } srcZ0d && srcZ0d.Length >= 1)
                {
                    int n = Data.Ports;
                    var z0Real = new Complex(srcZ0d[0].Real, 0);
                    var renormedMats = Data.Matrices
                        .Select(m => RFNetwork.SToS(m, srcZ0d, RFNetwork.Z0Array(z0Real, n)))
                        .ToArray();
                    snp = new SNP(Data.Frequencies, renormedMats,
                                  MatrixType.S, Data.Format, z0Real);
                }
                else
                {
                    snp = new SNP(Data.Frequencies, Data.Matrices,
                                  MatrixType.S, Data.Format, Data.Z0);
                }
                double v = Derived switch
                {
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(snp)[fi],
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(snp)[fi],
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(snp)[fi],
                    _                         => double.NaN
                };
                return new Complex(v, 0);
            }

            // Per-port (unusual) path: no user renorm.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                var mat = Data.Matrices[fi];
                if (MatrixType == MatrixType.Z)
                    mat = RFNetwork.SToZ(mat, sourceZ0);
                else if (MatrixType == MatrixType.Y)
                    mat = RFNetwork.SToY(mat, sourceZ0);
                // S: return stored matrix as-is (renorm disabled)
                return mat[Row, Col];
            }

            // Uniform/legacy path (unchanged).
            var z0  = z0Override ?? _z0;
            var matU = Data.Matrices[fi];

            if (MatrixType == MatrixType.S && z0 != Data.Z0)
                matU = RFNetwork.SToS(matU, Data.Z0, z0);
            else if (MatrixType == MatrixType.Z)
                matU = RFNetwork.SToZ(Data.Matrices[fi], Data.Z0);
            else if (MatrixType == MatrixType.Y)
                matU = RFNetwork.SToY(Data.Matrices[fi], Data.Z0);

            return matU[Row, Col];
        }

        public double DataPointScalar(double freq, Complex? z0Override = null)
        {
            var d = DataPoint(freq, z0Override);
            return YAxis switch
            {
                DependentVarFormat.Db        => 20.0 * Math.Log10(Math.Max(d.Magnitude, 1e-300)),
                DependentVarFormat.Imaginary => d.Imaginary,
                DependentVarFormat.Mag       => d.Magnitude,
                DependentVarFormat.Phase     => d.Phase * 180.0 / Math.PI,
                DependentVarFormat.Real      => d.Real,
                _                           => double.NaN
            };
        }

        // ---- Nearest-point search ----------------------------------------

        public (int FreqIndex, double Distance, Vector2 NearestPoint)?
            FindNearestTraceData(Vector2 queryPt)
        {
            // Family cube trace: geometry is in FamilyCurves[].Points, not Points.
            // Search across all curves; FreqIndex returns the X-array index of the hit.
            if (IsFamily)
            {
                double bestF = double.PositiveInfinity;
                int    bestI = -1;
                Vector2 bestP = default;
                bool complexPlane = YAxis == DependentVarFormat.Complex;
                for (int c = 0; c < FamilyCurves.Count; c++)
                {
                    var cps = FamilyCurves[c].Points;
                    for (int i = 0; i < cps.Count; i++)
                    {
                        double d = complexPlane ? Dist(queryPt, cps[i]) : Math.Abs(queryPt.X - cps[i].X);
                        if (d < bestF) { bestF = d; bestI = i; bestP = cps[i]; }
                    }
                }
                return bestI < 0 ? null : (bestI, bestF, bestP);
            }

            if (IsStabilityCircle)
            {
                double  best    = double.PositiveInfinity;
                int     bestIdx = -1;
                Vector2 bestPt  = default;

                for (int i = 0; i < StabilityCircleCentres.Count; i++)
                {
                    float rPx = Math.Abs((float)StabilityCircleRadii[i]);
                    float dx  = queryPt.X - StabilityCircleCentres[i].X;
                    float dy  = queryPt.Y - StabilityCircleCentres[i].Y;
                    float dc  = MathF.Sqrt(dx * dx + dy * dy);
                    if (dc < 1e-6f) continue;

                    var   nearPt = new Vector2(StabilityCircleCentres[i].X + dx / dc * rPx,
                                               StabilityCircleCentres[i].Y + dy / dc * rPx);
                    double dist = Dist(queryPt, nearPt);
                    if (dist < best)
                    {
                        best    = dist;
                        bestIdx = i;
                        bestPt  = nearPt;
                    }
                }
                return bestIdx < 0 ? null : (bestIdx, best, bestPt);
            }
            else
            {
                if (Points.Count == 0) return null;
                double best    = double.PositiveInfinity;
                int    bestIdx = -1;

                if (YAxis == DependentVarFormat.Complex)
                {
                    for (int i = 0; i < Points.Count; i++)
                    {
                        double d = Dist(queryPt, Points[i]);
                        if (d < best) { best = d; bestIdx = i; }
                    }
                }
                else
                {
                    for (int i = 0; i < Points.Count; i++)
                    {
                        double d = Math.Abs(queryPt.X - Points[i].X);
                        if (d < best) { best = d; bestIdx = i; }
                    }
                }
                return (bestIdx, best, Points[bestIdx]);
            }
        }

        // ---- Stability-circle: nearest perimeter point ------------------

        public Vector2? FindNearestPointOnStabilityCircle(Vector2 queryWorld, int freqIndex)
        {
            if (!IsStabilityCircle || freqIndex >= StabilityCircleCentres.Count)
                return null;
            var   C  = StabilityCircleCentres[freqIndex];
            float r  = MathF.Abs((float)StabilityCircleRadii[freqIndex]);
            float dx = queryWorld.X - C.X;
            float dy = queryWorld.Y - C.Y;
            float dc = MathF.Sqrt(dx * dx + dy * dy);
            if (dc < 1e-9f) return null;
            return new Vector2(C.X + dx / dc * r, C.Y + dy / dc * r);
        }

        // ---- Safe bulk data assignment ----------------------------------

        public void SetData(SNP data, int row, int col, DerivedParameters derived,
                            PlotType plotType, FreqUnit freqUnit)
        {
            if (row >= data.Ports || col >= data.Ports) return;
            _row     = 0; _col = 0;
            _data    = data;
            _row     = row; _col = col;
            _derived = data.Ports != 2 ? DerivedParameters.None : derived;
            BuildPath(plotType, freqUnit);
        }

        // ---- Copy data as text ------------------------------------------

        public string CopyDataString(FreqUnit freqUnit, double[]? freq = null, string fmt = "G12")
        {
            var allFreq = freq ?? Data.Frequencies;
            var sb = new StringBuilder();
            sb.AppendLine($"freq ({freqUnit})\t{Description}");
            foreach (var f in allFreq)
            {
                sb.Append((f * freqUnit.Scale()).ToString(fmt));
                sb.Append('\t');
                sb.AppendLine(DataPoint(f).ToString());
            }
            return sb.ToString();
        }

        // ---- Marker-data methods ----------------------------------------

        public Vector2 GetMarkerDataLocation(Marker m)
        {
            if (IsContourTrace)    return m.PositionStatic;   // contour markers positioned by world Γ/Z
            if (IsHarmonicStem)    return StemPointFor(m);
            if (IsCubeXMarker)     return CubeMarkerPointFor(m);
            if (IsCubeBound)       return Vector2.Zero;
            if (IsStabilityCircle) return m.PositionStatic;
            int fi = Array.FindIndex(Data.Frequencies, f => f >= m.Freq - 1e-6);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            if (fi >= 0 && fi < Points.Count) return Points[fi];
            return Vector2.Zero;
        }

        // ---- Generic cube-bound marker (non-harmonic X axis: Pin sweep etc.) ----
        //
        //  Covers a cube-bound Rect trace whose X axis is a swept variable (NOT the
        //  harmonic stem axis) — both single-curve (Polyline) and family (Spectrum).
        //  The marker stores the snapped display-X in PositionStatic.X and, for a
        //  family, the bound curve index in PositionStatic.Y (rounded). Lookups compare
        //  against Points.X / FamilyCurves[c].Points.X (display units), matching the stem
        //  convention so there is no Hz-vs-display unit mismatch.

        /// <summary>True for a cube-bound trace whose X axis is a generic swept variable
        /// (not the harmonic stem axis, not a contour). Single-curve or family.</summary>
        public bool IsCubeXMarker => IsCubeBound && !IsContourTrace && !IsHarmonicStem;

        /// <summary>True when the last <see cref="BuildPath"/> was on a Smith or Polar (complex 2-D) plot.
        /// Drives 2-D Euclidean snapping and resolution for single-curve cube markers.</summary>
        public bool IsComplexPlanePlot => _lastPlotType is PlotType.Smith or PlotType.Polar;

        /// <summary>The Points list backing a generic cube marker — the bound family curve
        /// when IsFamily, else the trace's own Points. Empty list when unavailable.</summary>
        private IReadOnlyList<Vector2> CubeMarkerPoints(Marker m)
        {
            if (IsFamily)
            {
                int c = CubeMarkerCurveIndex(m);
                if (c >= 0 && c < FamilyCurves.Count) return FamilyCurves[c].Points;
                return Array.Empty<Vector2>();
            }
            return Points;
        }

        /// <summary>Bound family-curve index stored in PositionStatic.Y (clamped to range).
        /// Returns 0 for a non-family trace.</summary>
        public int CubeMarkerCurveIndex(Marker m)
        {
            if (!IsFamily || FamilyCurves.Count == 0) return 0;
            int c = (int)MathF.Round(m.PositionStatic.Y);
            return Math.Clamp(c, 0, FamilyCurves.Count - 1);
        }

        /// <summary>Index into the bound curve's Points nearest to the stored marker position.
        /// On Rect (and families), matches by X-only. On Smith/Polar single-curve, matches by
        /// 2-D Euclidean distance to the stored (Re, Im) world point in PositionStatic.</summary>
        private int CubeMarkerIndex(Marker m)
        {
            var pts = CubeMarkerPoints(m);
            int idx = 0; float bestD = float.PositiveInfinity;
            if (IsComplexPlanePlot && !IsFamily)
            {
                var target = new Vector2(m.PositionStatic.X, m.PositionStatic.Y);
                for (int i = 0; i < pts.Count; i++)
                {
                    float d = (float)Dist(target, pts[i]);
                    if (d < bestD) { bestD = d; idx = i; }
                }
            }
            else
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    float d = Math.Abs(pts[i].X - m.PositionStatic.X);
                    if (d < bestD) { bestD = d; idx = i; }
                }
            }
            return idx;
        }

        private Vector2 CubeMarkerPointFor(Marker m)
        {
            var pts = CubeMarkerPoints(m);
            if (pts.Count == 0) return Vector2.Zero;
            return pts[CubeMarkerIndex(m)];
        }

        /// <summary>Index of the cube X sample nearest to <paramref name="x"/> (a raw cube X value).
        /// Used for Table markers, whose position is the row's X value (Marker.Freq), not a pixel/Points
        /// coordinate. Returns 0 when there are no X values.</summary>
        private int NearestCubeXIndex(double x)
        {
            if (_cubeXValues is null || _cubeXValues.Length == 0) return 0;
            int idx = 0; double best = double.PositiveInfinity;
            for (int i = 0; i < _cubeXValues.Length; i++)
            {
                double d = Math.Abs(_cubeXValues[i] - x);
                if (d < best) { best = d; idx = i; }
            }
            return idx;
        }

        /// <summary>Snaps a world point to the nearest sample of a generic cube trace and returns
        /// the values to store on the marker: snapped display position, the X to keep in
        /// PositionStatic.X, and the bound family-curve index (0 when not a family).
        /// For a family, the nearest sample is searched across ALL curves so the marker binds to
        /// whichever curve the cursor is closest to. Returns null when no geometry is available.</summary>
        public (Vector2 Pos, float CubeX, int CurveIndex)? SnapToCubeMarker(Vector2 worldPt)
        {
            if (!IsCubeXMarker) return null;

            if (IsFamily)
            {
                int    bestC = -1, bestI = -1;
                double bestD = double.PositiveInfinity;
                for (int c = 0; c < FamilyCurves.Count; c++)
                {
                    var cps = FamilyCurves[c].Points;
                    for (int i = 0; i < cps.Count; i++)
                    {
                        double d = Dist(worldPt, cps[i]);
                        if (d < bestD) { bestD = d; bestC = c; bestI = i; }
                    }
                }
                if (bestC < 0) return null;
                var p = FamilyCurves[bestC].Points[bestI];
                return (p, p.X, bestC);
            }

            if (Points.Count == 0) return null;
            int best = 0; float bd = float.PositiveInfinity;
            if (IsComplexPlanePlot)
            {
                for (int i = 0; i < Points.Count; i++)
                {
                    float d = (float)Dist(worldPt, Points[i]);
                    if (d < bd) { bd = d; best = i; }
                }
            }
            else
            {
                for (int i = 0; i < Points.Count; i++)
                {
                    float d = Math.Abs(Points[i].X - worldPt.X);
                    if (d < bd) { bd = d; best = i; }
                }
            }
            return (Points[best], Points[best].X, 0);
        }

        /// <summary>InfoBox lines for a generic cube-bound marker (X = swept variable).
        /// Row 1: marker name. For a family, the iterated-axis identity rows: when that axis is
        /// frequency-like (e.g. the HB "harmonic" axis, whose values are physical frequencies) it is
        /// shown as a unit-scaled "freq=…" row plus an integer "harmonic=…" row (consistent with the
        /// harmonic-stem InfoBox); otherwise a single "&lt;axis&gt;=&lt;value&gt;" row. Then the X-axis
        /// row (swept variable name + value + unit), then the cube value.</summary>
        private List<(string, bool)> BuildCubeMarkerBoxLines(Marker m, FreqUnit freqUnit, bool showFilePrefix,
            IReadOnlyList<Trace>? otherTraces = null)
        {
            var lines = new List<(string, bool)> { (m.MarkerString, true) };

            var pts = CubeMarkerPoints(m);
            string desc = showFilePrefix ? Description : ShortDescription;

            // NaN only when there is genuinely no data. A Table real-valued cube builds NO Points
            // (BuildCubePath skips the Rect/Smith geometry), yet still has _cubeXValues — so an empty
            // Points list alone must NOT force NaN, or every Table marker reads NaN.
            if (_cubeXValues is null || _cubeXValues.Length == 0)
            {
                lines.Add(($"{desc}=NaN", false));
                return lines;
            }

            // On a Table the marker stores its X in Marker.Freq (PlotControl sets it from the row's
            // XValues), not PositionStatic.X — and Points may be empty — so resolve the index against
            // _cubeXValues directly. Rect/Smith/Polar use the Points-based CubeMarkerIndex.
            int xIdx = _lastPlotType == PlotType.Table ? NearestCubeXIndex(m.Freq) : CubeMarkerIndex(m);
            int curve = CubeMarkerCurveIndex(m);

            // Family: identify the bound curve via its iterated-axis value.
            if (IsFamily && curve >= 0 && curve < FamilyCurves.Count)
            {
                var fc = FamilyCurves[curve];

                bool familyIsHarmonic = string.Equals(FamilyAxisName, HarmonicAxisName, StringComparison.Ordinal);
                bool familyIsMixIndex = string.Equals(FamilyAxisName, MixIndexAxisName, StringComparison.Ordinal);
                if (familyIsHarmonic)
                {
                    // HB "harmonic" family axis: integer orders, with frequency reconstructed from _f0ByX.
                    int order = (int)Math.Round(fc.AxisValue);
                    lines.Add(($"harmonic={order}", false));
                    if (_f0ByX is not null)
                    {
                        double freqHz = HbSpectrum.HarmonicFreqHz(order, _f0ByX[Math.Min(xIdx, _f0ByX.Length - 1)]);
                        lines.Add(($"freq={freqHz * freqUnit.Scale():G6} {freqUnit.Description()}", false));
                    }
                }
                else if (familyIsMixIndex)
                {
                    // Two-tone "mixIndex" family axis: the (k1,k2) tag identifies the product (the axis
                    // value IS a frequency, but the user reads it by tag) — then the folded |frequency|.
                    // This is the bug fix: previously the freq-unit path below showed "mixIndex=<f> GHz".
                    string tag = !string.IsNullOrEmpty(fc.AxisLabel) ? fc.AxisLabel : $"{fc.AxisValue:G6}";
                    lines.Add(($"mixIndex={tag}", false));
                    lines.Add(($"freq={Math.Abs(fc.AxisValue) * freqUnit.Scale():G6} {freqUnit.Description()}", false));
                }
                else
                {
                    // Any other family axis — including a sweep over a frequency variable (e.g. RFfreq).
                    // Show the swept variable's NAME; scale by the plot's freq unit when the axis
                    // carries a frequency unit, else append the axis's own unit.
                    string axisName = string.IsNullOrEmpty(FamilyAxisName) ? "curve" : FamilyAxisName;
                    if (IsFreqUnit(FamilyAxisUnit))
                    {
                        double scaled = fc.AxisValue * freqUnit.Scale();
                        lines.Add(($"{axisName}={scaled:G6} {freqUnit.Description()}", false));
                    }
                    else
                    {
                        string axisVal = !string.IsNullOrEmpty(fc.AxisLabel)
                            ? fc.AxisLabel
                            : fc.AxisValue.ToString($"{m.FormatString}{m.MaximumFractionDigits}");
                        string unit = string.IsNullOrEmpty(FamilyAxisUnit) ? "" : $" {FamilyAxisUnit}";
                        lines.Add(($"{axisName}={axisVal}{unit}", false));
                    }
                }
            }

            // X-axis row: the swept variable name + value + unit (never "freq" unless the
            // axis really is a frequency).
            int rawIdx = xIdx < _cubeXValues.Length ? xIdx : _cubeXValues.Length - 1;
            double xRaw = _cubeXValues[rawIdx];
            bool xIsHarmonicAxis = string.Equals(_cubeXAxisName, HarmonicAxisName, StringComparison.Ordinal);
            if (xIsHarmonicAxis)
            {
                // HB "harmonic" X axis: integer orders, with frequency reconstructed from _f0ByX.
                int order = (int)Math.Round(xRaw);
                if (_f0ByX is not null)
                {
                    double freqHz = HbSpectrum.HarmonicFreqHz(order, _f0ByX[Math.Min(rawIdx, _f0ByX.Length - 1)]);
                    lines.Add(($"freq={freqHz * freqUnit.Scale():G6} {freqUnit.Description()}", false));
                }
                lines.Add(($"harmonic={order}", false));
            }
            else if (string.Equals(_cubeXAxisName, MixIndexAxisName, StringComparison.Ordinal))
            {
                // Two-tone mixIndex: row 1 = the (k1,k2) mix-product tag, row 2 = its frequency
                // (already folded to the absolute, single-sided value in _cubeXValues).
                string tag = _cubeXLabels is not null && rawIdx < _cubeXLabels.Length
                    ? _cubeXLabels[rawIdx] : "(?,?)";
                lines.Add(($"mixIndex={tag}", false));
                lines.Add(($"freq={xRaw * freqUnit.Scale():G6} {freqUnit.Description()}", false));
            }
            else if (IsFreqUnit(_cubeXUnit))
            {
                // Frequency-valued X axis (e.g. an ordinary frequency sweep): show variable name + scaled freq.
                double scaledX = xRaw * freqUnit.Scale();
                string xLabel = string.IsNullOrEmpty(_cubeXAxisName) ? "freq" : _cubeXAxisName;
                lines.Add(($"{xLabel}={scaledX:G6} {freqUnit.Description()}", false));
            }
            else
            {
                string xName = string.IsNullOrEmpty(_cubeXAxisName) ? "x" : _cubeXAxisName;
                string xUnit = string.IsNullOrEmpty(_cubeXUnit) ? "" : $" {_cubeXUnit}";
                lines.Add(($"{xName}={xRaw:G6}{xUnit}", false));
            }

            // Pinned spectral line: when the harmonic/mixIndex axis is PINNED (X is the sweep), still
            // surface which line this trace shows + its frequency — the same two rows the spectral-X
            // marker box gives, so a pinned-line plot reads the same as a spectral-axis-X plot.
            if (_pinnedSpectralName is not null)
            {
                lines.Add(($"{_pinnedSpectralName}={_pinnedSpectralLabel}", false));
                if (!double.IsNaN(_pinnedSpectralFreqHz))
                    lines.Add(($"freq={_pinnedSpectralFreqHz * freqUnit.Scale():G6} {freqUnit.Description()}", false));
            }

            // Value row.
            string val = IsFamily
                ? FormatFamilyCellForMarker(curve, xIdx, m)
                : FormatCubeCellForMarker(xIdx, m);
            if (string.IsNullOrEmpty(val)) val = "NaN";
            lines.Add(($"{desc}={val}", false));

            // Multi-marker rows: the same X sample read on every other trace in the plot.
            // Cube traces are keyed by X-index, not frequency, so this uses the cube path.
            // When the other trace's X axis is incompatible (different length), the value is NaN.
            if (m.IsMulti && otherTraces != null)
                foreach (var other in otherTraces)
                    lines.Add((GetMultiMarkerLine(m, other), false));

            return lines;
        }

        /// <summary>Transformed scalar value of THIS cube trace at X-index <paramref name="i"/> (single-curve
        /// path). Returns NaN when out of range, when the cube is complex with a non-scalar transform,
        /// or when this is a family trace (use the family overload). Mirrors FormatCubeCell's numeric path.</summary>
        private double CubeScalarAt(int i)
        {
            if (_cubeXValues is null || i < 0 || i >= _cubeXValues.Length) return double.NaN;
            if (_cubeComplexValues is not null)
            {
                var z = _cubeComplexValues[i];
                return Transform switch
                {
                    CubeTransform.dB20  => 20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                    CubeTransform.dB10 or CubeTransform.dB
                                        => 10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300)),
                    CubeTransform.Mag   => z.Magnitude,
                    CubeTransform.Phase => z.Phase * 180.0 / Math.PI,
                    CubeTransform.Real  => z.Real,
                    CubeTransform.Imag  => z.Imaginary,
                    _                   => double.NaN,   // None/Conj: complex, not a scalar
                };
            }
            if (_cubeRealValues is not null)
            {
                double v = _cubeRealValues[i];
                return Transform switch
                {
                    CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.dB10 or CubeTransform.dB
                                       => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.Mag  => Math.Abs(v),
                    _                  => v,
                };
            }
            return double.NaN;
        }

        /// <summary>One multi-marker row for a cube-X owner marker: reads <paramref name="other"/> at the
        /// same X-index. Only single-curve cube traces with a matching X-axis length are read; anything else
        /// (network trace, family, mismatched X axis) yields NaN, which the user has accepted for
        /// incompatible axes. Honors delta mode when both values are finite scalars.</summary>
        private string GetCubeMultiMarkerLine(Marker m, Trace other)
        {
            int xIdx = CubeMarkerIndex(m);
            bool compatible =
                other.IsCubeXMarker && !other.IsFamily &&
                other._cubeXValues is not null && _cubeXValues is not null &&
                other._cubeXValues.Length == _cubeXValues.Length;

            if (m.IsDelta)
            {
                double own   = CubeScalarAt(xIdx);
                double oth   = compatible ? other.CubeScalarAt(xIdx) : double.NaN;
                double delta = oth - own;
                string valStr = double.IsFinite(delta) ? delta.ToString($"{m.FormatString}{m.MaximumFractionDigits}") : "NaN";
                return $"  Δ{other.ShortDescription}={valStr}";
            }

            string val = compatible
                ? other.FormatCubeCell(xIdx, m.FormatString, m.MaximumFractionDigits)
                : "NaN";
            if (string.IsNullOrEmpty(val)) val = "NaN";
            return $"{other.ShortDescription}={val}";
        }

        private Vector2 StemPointFor(Marker m)
        {
            if (Points.Count == 0) return Vector2.Zero;
            float targetX = m.PositionStatic.X;
            int best = 0; float bestD = float.PositiveInfinity;
            for (int i = 0; i < Points.Count; i++)
            {
                float d = Math.Abs(Points[i].X - targetX);
                if (d < bestD) { bestD = d; best = i; }
            }
            return Points[best];
        }

        /// <summary>For a harmonic-stem trace, snaps a world point to the nearest stem and
        /// returns (snapped Points position, harmonic X-value to store in Marker.PositionStatic.X).
        /// Returns null when not a stem trace or no points.</summary>
        public (Vector2 Pos, float HarmonicX)? SnapToStem(Vector2 worldPt)
        {
            if (!IsHarmonicStem || Points.Count == 0) return null;
            int best = 0; float bestD = float.PositiveInfinity;
            for (int i = 0; i < Points.Count; i++)
            {
                float d = Math.Abs(Points[i].X - worldPt.X);
                if (d < bestD) { bestD = d; best = i; }
            }
            return (Points[best], Points[best].X);
        }

        // Finds the index in Points whose X (display freq units) is nearest to PositionStatic.X.
        // CubeXValues stores raw Hz values; Points.X stores Hz * freqUnit.Scale(). Using Points
        // avoids the unit mismatch that occurs when comparing Hz directly to PositionStatic.X.
        private int FindStemIndex(Marker m)
        {
            int idx = 0; float bestD = float.PositiveInfinity;
            for (int i = 0; i < Points.Count; i++)
            {
                float d = Math.Abs(Points[i].X - m.PositionStatic.X);
                if (d < bestD) { bestD = d; idx = i; }
            }
            return idx;
        }

        /// <summary>Integer harmonic order for the InfoBox of a stem marker. Single-tone only.</summary>
        public string GetStemOrderString(Marker m)
        {
            // TODO multitone (mixIndex): format (k1,k2) pair
            return $"harmonic={FindStemIndex(m)}";
        }

        /// <summary>Physical frequency row for the InfoBox of a stem marker (reconstructed from _f0ByX).</summary>
        public string? GetStemFreqString(Marker m)
        {
            if (Points.Count == 0 || _f0ByX is null) return null;
            int stemIdx = FindStemIndex(m);
            double freqHz = HbSpectrum.HarmonicFreqHz(stemIdx, _f0ByX[Math.Min(stemIdx, _f0ByX.Length - 1)]);
            return $"freq={freqHz * m.FreqUnits.Scale():G6} {m.FreqUnits.Description()}";
        }

        /// <summary>Marker value string for a harmonic-stem marker.</summary>
        public string GetStemValString(Marker m, bool showFilePrefix)
        {
            string desc = showFilePrefix ? Description : ShortDescription;
            if (CubeXValues is null || CubeXValues.Count == 0 || Points.Count == 0) return $"{desc}=NaN";
            int    idx = FindStemIndex(m);
            string val = FormatCubeCell(idx, m.FormatString, m.MaximumFractionDigits);
            return $"{desc}={val}";
        }

        /// <summary>The marker value line for the compact editor readout, by kind.</summary>
        public string GetEditorDataLine(Marker m, bool showFilePrefix)
        {
            if (IsContourTrace && ContourData is { } cd)
            {
                var coord   = new Complex(m.PositionStatic.X, m.PositionStatic.Y);
                double val  = cd.EvaluateMetric?.Invoke(coord, m.ContourSnapped) ?? double.NaN;
                string metric = string.IsNullOrEmpty(cd.MetricName) ? "value" : cd.MetricName;
                string fmt    = $"{m.FormatString}{m.MaximumFractionDigits}";
                string valStr = double.IsFinite(val) ? val.ToString(fmt) : "NaN";
                string unit   = string.IsNullOrEmpty(cd.MetricUnitString) ? "" : $" {cd.MetricUnitString}";
                return $"{metric}={valStr}{unit}";
            }
            if (IsHarmonicStem) return GetStemValString(m, showFilePrefix);
            if (IsCubeXMarker)
            {
                string desc = showFilePrefix ? Description : ShortDescription;
                var pts = CubeMarkerPoints(m);
                if (pts.Count == 0 || _cubeXValues is null || _cubeXValues.Length == 0)
                    return $"{desc}=NaN";
                int xIdx = CubeMarkerIndex(m);
                string val = IsFamily
                    ? FormatFamilyCellForMarker(CubeMarkerCurveIndex(m), xIdx, m)
                    : FormatCubeCellForMarker(xIdx, m);
                if (string.IsNullOrEmpty(val)) val = "NaN";
                return $"{desc}={val}";
            }
            return GetMarkerValString(m, showFilePrefix);
        }

        /// <summary>Resolves a world Γ/Z point to the position a contour marker should take,
        /// honoring the marker's mode: Mode 1 (free) returns the point unchanged; Mode 2 (snapped)
        /// returns the nearest measured grid-node coordinate. No-op fallback when no fit yet.</summary>
        public Vector2 ResolveContourMarkerPosition(Marker m, Vector2 worldPt)
        {
            if (!IsContourTrace) return worldPt;
            if (m.ContourSnapped && ContourData?.NearestNode is { } snap)
            {
                var c = snap(new Complex(worldPt.X, worldPt.Y));
                return new Vector2((float)c.Real, (float)c.Imaginary);
            }
            return worldPt;
        }

        public Complex GetMarkerDataPoint(Marker m)
        {
            if (IsCubeBound)       return new Complex(double.NaN, double.NaN);
            if (IsStabilityCircle) return new Complex(m.PositionStatic.X, m.PositionStatic.Y);

            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return new Complex(double.NaN, double.NaN);

            var mat = Data.Matrices[fi];

            // Per-port (unusual) path: convert using SourceZ0PerPort; no user renorm.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                if (IsDerived)
                {
                    // Renorm to uniform-real so scalar stability methods are valid.
                    int n = Data.Ports;
                    var z0Real = new Complex(sourceZ0[0].Real, 0);
                    mat = RFNetwork.SToS(mat, sourceZ0, RFNetwork.Z0Array(z0Real, n));
                    double v = Derived switch
                    {
                        DerivedParameters.Mu      => RFNetwork.StabilityMu(mat),
                        DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(mat),
                        DerivedParameters.MaxGain => RFNetwork.MaxGain(mat),
                        _                         => double.NaN
                    };
                    return new Complex(v, 0);
                }

                // Non-derived: convert type if needed using per-port reference.
                if (MatrixType == MatrixType.Z)
                    mat = RFNetwork.SToZ(mat, sourceZ0);
                else if (MatrixType == MatrixType.Y)
                    mat = RFNetwork.SToY(mat, sourceZ0);
                // S: return stored as-is

                return mat[Row, Col];
            }

            // Uniform/legacy path (unchanged).
            if (Data.Type != MatrixType || Z0 != Data.Z0)
                mat = RFNetwork.Convert(mat, Data.Type, Data.Z0, MatrixType, Z0);

            if (IsDerived)
            {
                double v = Derived switch
                {
                    DerivedParameters.Mu      => RFNetwork.StabilityMu(mat),
                    DerivedParameters.MuPrime => RFNetwork.StabilityMuPrime(mat),
                    DerivedParameters.MaxGain => RFNetwork.MaxGain(mat),
                    _                         => double.NaN
                };
                return new Complex(v, 0);
            }

            return mat[Row, Col];
        }

        public string FormatScalarValue(double val, Marker m)
        {
            if (!double.IsFinite(val)) return "NaN";
            string fmt = $"{m.FormatString}{m.MaximumFractionDigits}";
            return YAxis switch
            {
                DependentVarFormat.Db    => $"{val.ToString(fmt)} dB",
                DependentVarFormat.Phase => $"{val.ToString(fmt)}°",
                _                        => val.ToString(fmt),
            };
        }

        /// <summary>
        /// Formats the cube value at X index <paramref name="i"/> for the Table renderer
        /// (post-Transform, same transform logic as <see cref="BuildPath"/>).
        /// Returns "NaN" when out of range or cube data is absent.
        /// Complex with Transform=None uses mag∠deg (MA) format.
        /// </summary>
        public string FormatCubeCell(int i, PrecisionFormat fmt, int fracDigits)
        {
            if (InvalidSpecText is not null) return "";
            if (!IsCubeBound || _cubeXValues is null || i < 0 || i >= _cubeXValues.Length)
                return "NaN";
            string f = $"{fmt}{fracDigits}";

            if (_cubeComplexValues is not null)
            {
                var z = _cubeComplexValues[i];
                return Transform switch
                {
                    // No scalar transform → complex value shown in the user's Number Format (MA/RI/DB).
                    CubeTransform.None  => FormatCubeComplex(z, f),
                    CubeTransform.Conj  => FormatCubeComplex(Complex.Conjugate(z), f),
                    CubeTransform.dB20  => (20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToString(f),
                    CubeTransform.dB10 or CubeTransform.dB
                                        => (10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToString(f),
                    CubeTransform.Mag   => z.Magnitude.ToString(f),
                    CubeTransform.Phase => (z.Phase * 180.0 / Math.PI).ToString(f),
                    CubeTransform.Real  => z.Real.ToString(f),
                    CubeTransform.Imag  => z.Imaginary.ToString(f),
                    _                   => z.Magnitude.ToString(f),
                };
            }

            if (_cubeRealValues is not null)
            {
                double v = _cubeRealValues[i];
                double y = Transform switch
                {
                    CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.dB10 or CubeTransform.dB
                                       => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.Mag  => Math.Abs(v),
                    _                  => v,
                };
                return y.ToString(f);
            }

            return "NaN";
        }

        /// <summary>
        /// Formats family curve <paramref name="curveIndex"/> at X-array position <paramref name="xIndex"/>
        /// for the Table renderer.  Returns "" for out-of-range or absent data (never throws).
        /// </summary>
        public string FormatFamilyCell(int curveIndex, int xIndex, PrecisionFormat fmt, int fracDigits)
        {
            if (curveIndex < 0 || curveIndex >= FamilyCurves.Count) return "";
            var fc = FamilyCurves[curveIndex];
            if (_cubeXValues is null || xIndex < 0 || xIndex >= _cubeXValues.Length) return "";
            string f = $"{fmt}{fracDigits}";

            if (fc.RawComplex is { } cz)
            {
                if (xIndex >= cz.Length) return "";
                var z = cz[xIndex];
                return Transform switch
                {
                    // No scalar transform → complex value shown in the user's Number Format (MA/RI/DB).
                    CubeTransform.None  => FormatCubeComplex(z, f),
                    CubeTransform.Conj  => FormatCubeComplex(Complex.Conjugate(z), f),
                    CubeTransform.dB20  => (20.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToString(f),
                    CubeTransform.dB10 or CubeTransform.dB
                                        => (10.0 * Math.Log10(Math.Max(z.Magnitude, 1e-300))).ToString(f),
                    CubeTransform.Mag   => z.Magnitude.ToString(f),
                    CubeTransform.Phase => (z.Phase * 180.0 / Math.PI).ToString(f),
                    CubeTransform.Real  => z.Real.ToString(f),
                    CubeTransform.Imag  => z.Imaginary.ToString(f),
                    _                   => z.Magnitude.ToString(f),
                };
            }

            if (fc.RawReal is { } rv)
            {
                if (xIndex >= rv.Length) return "";
                double v = rv[xIndex];
                double y = Transform switch
                {
                    CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.dB10 or CubeTransform.dB
                                       => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                    CubeTransform.Mag  => Math.Abs(v),
                    _                  => v,
                };
                return y.ToString(f);
            }

            return "";
        }

        private static string FormatCubeMA(Complex c, string fmt)
            => $"{c.Magnitude.ToString(fmt)}∠{(c.Phase * 180.0 / Math.PI):F1}°";

        private static string FormatCubeRI(Complex c, string fmt)
            => $"{c.Real.ToString(fmt)}{(c.Imaginary >= 0 ? "+" : "-")}j{Math.Abs(c.Imaginary).ToString(fmt)}";

        private static string FormatCubeDB(Complex c, string fmt)
            => $"{(20.0 * Math.Log10(Math.Max(c.Magnitude, 1e-300))).ToString(fmt)}∠{(c.Phase * 180.0 / Math.PI):F1}°";

        /// <summary>Formats a complex cube value in the trace's Number Format (<see cref="MatrixFormat"/>):
        /// MA (Mag∠Angle), RI (Real±jImag), or DB (dB∠Angle). Used for Table cells with no scalar transform.</summary>
        private string FormatCubeComplex(Complex c, string fmt) => MatrixFormat switch
        {
            MatrixFormat.RI => FormatCubeRI(c, fmt),
            MatrixFormat.DB => FormatCubeDB(c, fmt),
            _               => FormatCubeMA(c, fmt),
        };

        /// <summary>
        /// Marker-aware cube cell formatter: identical to <see cref="FormatCubeCell"/> except that a
        /// COMPLEX value with no scalar transform (None/Conj) is formatted through the marker's own
        /// <see cref="Marker.FormatComplex"/> so the marker's MatrixFormat (MA/RI/DB) is honored on
        /// Smith/Polar plots. (FormatCubeCell hardcodes MA for the Table renderer, which has no marker.)
        /// </summary>
        public string FormatCubeCellForMarker(int i, Marker m)
        {
            if (InvalidSpecText is not null) return "";
            if (!IsCubeBound || _cubeXValues is null || i < 0 || i >= _cubeXValues.Length)
                return "NaN";

            if (_cubeComplexValues is not null &&
                (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
            {
                var z = Transform == CubeTransform.Conj
                    ? Complex.Conjugate(_cubeComplexValues[i])
                    : _cubeComplexValues[i];
                return m.FormatComplex(z);
            }

            // Scalar transforms (and real cubes) format identically to the table path.
            return FormatCubeCell(i, m.FormatString, m.MaximumFractionDigits);
        }

        /// <summary>Marker-aware family cell formatter — see <see cref="FormatCubeCellForMarker"/>.</summary>
        public string FormatFamilyCellForMarker(int curveIndex, int xIndex, Marker m)
        {
            if (curveIndex < 0 || curveIndex >= FamilyCurves.Count) return "";
            var fc = FamilyCurves[curveIndex];
            if (_cubeXValues is null || xIndex < 0 || xIndex >= _cubeXValues.Length) return "";

            if (fc.RawComplex is { } cz && xIndex < cz.Length &&
                (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
            {
                var z = Transform == CubeTransform.Conj ? Complex.Conjugate(cz[xIndex]) : cz[xIndex];
                return m.FormatComplex(z);
            }

            return FormatFamilyCell(curveIndex, xIndex, m.FormatString, m.MaximumFractionDigits);
        }

        public string GetMarkerValString(Marker m, bool showFilePrefix = true)
        {
            string suffix = IsStabilityCircle ? " Γ" : "";
            string desc   = showFilePrefix ? Description : ShortDescription;

            if (YAxis == DependentVarFormat.Complex)
                return $"{desc}{suffix}={m.FormatComplex(GetMarkerDataPoint(m))}";

            double scalar = DataPointScalar(m.Freq);
            return $"{desc}={FormatScalarValue(scalar, m)}";
        }

        public string GetMultiMarkerLine(Marker m, Trace other)
        {
            // Cube-X owner (HB measurement vs a swept axis): keyed by X-index, not frequency.
            if (IsCubeXMarker)
                return GetCubeMultiMarkerLine(m, other);

            if (m.IsDelta)
            {
                double ownVal   = DataPointScalar(m.Freq);
                double otherVal = other.DataPointScalar(m.Freq);
                double delta    = otherVal - ownVal;
                string valStr   = double.IsFinite(delta)
                    ? other.FormatScalarValue(delta, m)
                    : "NaN";
                return $"  Δ{other.ShortDescription}={valStr}";
            }

            if (other.YAxis == DependentVarFormat.Complex)
            {
                var    dp     = other.GetMarkerDataPoint(m);
                string valStr = double.IsNaN(dp.Real) ? "NaN" : m.FormatComplex(dp);
                return $"{other.ShortDescription}={valStr}";
            }

            double scalar = other.DataPointScalar(m.Freq);
            return $"{other.ShortDescription}={other.FormatScalarValue(scalar, m)}";
        }

        public string MuString(Marker m)
        {
            if (IsCubeBound) return "";
            string fmt = $"{m.FormatString.ToString()}{m.MaximumFractionDigits}";
            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "Stability=NaN";
            switch (Derived)
            {
                case DerivedParameters.LoadStabilityCircle or DerivedParameters.Mu:
                    var val = RFNetwork.StabilityMu(Data);
                    return "Load Stability, µ=" + val[fi].ToString(fmt);
                case DerivedParameters.SourceStabilityCircle or DerivedParameters.MuPrime:
                    val = RFNetwork.StabilityMuPrime(Data);
                    return "Source Stability, µ'=" + val[fi].ToString(fmt);
            }
            return "";
        }

        public bool MarkerShowsImpedance(Marker m) =>
            !IsCubeBound && !m.IsMulti && Row == Col && YAxis == DependentVarFormat.Complex;

        public string GetMarkerImpedanceString(Marker m)
        {
            if (IsCubeBound) return "";
            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "impedance=NaN";

            // Per-port (unusual) path: use SourceZ0PerPort[Row] as the port reference.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0 && Row < sourceZ0.Length)
            {
                Complex s = Data.Matrices[fi][Row, Col];  // stored S referenced to sourceZ0[Row]
                var portZ0 = sourceZ0[Row];
                var Z  = portZ0 * (portZ0.Conjugate() / portZ0 + s) / (Complex.One - s);
                var Zn = Z / portZ0;
                return m.UseNormalizedImpedance
                    ? $"impedance=Z0*({m.FormatComplex(Zn)})"
                    : $"impedance={m.FormatComplex(Z)} Ω";
            }

            // Uniform/legacy path (unchanged).
            Complex sv = new Complex(m.PositionStatic.X, m.PositionStatic.Y);

            if (!IsDerived)
            {
                Mat<Complex> temp = RFNetwork.Convert(Data.Matrices[fi], Data.Type, Data.Z0, MatrixType, Z0);
                sv = temp[Row, Col];
            }
            var Zv  = Z0 * (Z0.Conjugate() / Z0 + sv) / (Complex.One - sv);
            var Znv = Zv / Z0;

            return m.UseNormalizedImpedance
                ? $"impedance=Z0*({m.FormatComplex(Znv)})"
                : $"impedance={m.FormatComplex(Zv)} Ω";
        }

        public List<(string Text, bool Bold)> BuildMarkerBoxLines(Marker m, FreqUnit freqUnit,
            bool showFilePrefix = true, IReadOnlyList<Trace>? otherTraces = null)
        {
            if (IsContourTrace && ContourData is { } cd)
            {
                var lines = new List<(string, bool)> { (m.MarkerString, true) };

                var coord = new Complex(m.PositionStatic.X, m.PositionStatic.Y);
                double val = cd.EvaluateMetric?.Invoke(coord, m.ContourSnapped) ?? double.NaN;

                string metric = string.IsNullOrEmpty(cd.MetricName) ? "value" : cd.MetricName;
                string fmt    = $"{m.FormatString}{m.MaximumFractionDigits}";
                string valStr = double.IsFinite(val) ? val.ToString(fmt) : "NaN";
                string unit   = string.IsNullOrEmpty(cd.MetricUnitString) ? "" : $" {cd.MetricUnitString}";
                lines.Add(($"{metric}={valStr}{unit}", false));

                string coordLbl = cd.GammaPlane ? "Γ" : "Z";
                // Impedance readout carries an Ω unit; the reflection-coefficient (Γ) readout is unitless.
                string coordUnit = cd.GammaPlane ? "" : " Ω";
                lines.Add(($"{coordLbl}={m.FormatComplex(coord)}{coordUnit}", false));
                return lines;
            }

            if (IsHarmonicStem)
            {
                var lines = new List<(string, bool)> { (m.MarkerString, true) };
                var fline = GetStemFreqString(m);
                if (!string.IsNullOrEmpty(fline)) lines.Add((fline, false));
                lines.Add((GetStemOrderString(m), false));
                lines.Add((GetStemValString(m, showFilePrefix), false));
                return lines;
            }

            if (IsCubeXMarker)
                return BuildCubeMarkerBoxLines(m, freqUnit, showFilePrefix, otherTraces);

            var standardLines = new List<(string, bool)>
            {
                (m.MarkerString,                        true),
                (m.FreqString,                          false),
                (GetMarkerValString(m, showFilePrefix), false)
            };
            if (MarkerShowsImpedance(m))
                standardLines.Add((GetMarkerImpedanceString(m), false));
            if (IsStabilityCircle)
                standardLines.Add((MuString(m), false));

            if (m.IsMulti && otherTraces != null)
                foreach (var other in otherTraces)
                    standardLines.Add((GetMultiMarkerLine(m, other), false));

            return standardLines;
        }

        public void SetMarkerFreq(Marker m, double newFreq)
        {
            if (IsCubeBound || IsFamily) return;
            int fi = Array.FindIndex(Data.Frequencies, f => f >= newFreq - 1e-6);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            m.Freq = Data.Frequencies[fi];
            SnapMarkerToStabilityCircle(m, fi);
        }

        public void IncrementMarkerFreq(Marker m)
        {
            if (IsCubeBound || IsFamily) return;
            int fi = Array.FindIndex(Data.Frequencies, f => f > m.Freq);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            m.Freq = Data.Frequencies[fi];
            m.PositionStatic = new Vector2(0, 0);
            SnapMarkerToStabilityCircle(m, fi);
        }

        public void DecrementMarkerFreq(Marker m)
        {
            if (IsCubeBound || IsFamily) return;
            int fi = Array.FindLastIndex(Data.Frequencies, f => f < m.Freq);
            if (fi < 0) fi = 0;
            m.Freq = Data.Frequencies[fi];
            m.PositionStatic = new Vector2(0, 0);
            SnapMarkerToStabilityCircle(m, fi);
        }

        /// <summary>
        /// Moves a Rect-plot marker to the next x-axis sample: <paramref name="direction"/> &gt; 0 steps to
        /// the next HIGHER x (Up/Right arrow), &lt; 0 to the next lower (Down/Left). Stepping is done in
        /// ascending display-x order, so spectral axes (harmonic, mixIndex) step in <em>frequency</em> — the
        /// products are stored in lattice order, not sorted. Network/SNP traces step along the frequency axis.
        /// Returns true if the marker actually moved (false at an axis end, or for contour / Smith / Polar).
        /// </summary>
        public bool StepMarkerAlongX(Marker m, int direction)
        {
            if (direction == 0 || IsContourTrace || IsComplexPlanePlot) return false;

            // ── Cube-bound traces: normal sweep X, mixIndex spectra, harmonic stems, and families ──
            if (IsCubeBound)
            {
                if (!IsCubeXMarker && !IsHarmonicStem) return false;   // scalar/contour cube — no x to step
                var pts = CubeMarkerPoints(m);                          // the marker's bound curve's samples
                if (pts.Count < 2) return false;

                // Rank the samples by ascending display-x. Points[i].X is the marker-space x for both
                // cube-X and stem traces (SnapToStem/SnapToCubeMarker both store Points[i].X), so a
                // mixIndex spectrum — whose values are folded freqs in lattice order — steps by frequency.
                var order = new int[pts.Count];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(order, (a, b) => pts[a].X.CompareTo(pts[b].X));

                // Current rank = the sample nearest the marker's stored x.
                int curRank = 0; float best = float.PositiveInfinity;
                for (int r = 0; r < order.Length; r++)
                {
                    float d = Math.Abs(pts[order[r]].X - m.PositionStatic.X);
                    if (d < best) { best = d; curRank = r; }
                }

                int nextRank = curRank + direction;
                if (nextRank < 0 || nextRank >= order.Length) return false;   // at an end — no wrap

                // target is an exact sample on the bound curve, so store its X directly (matches the
                // index lookups in CubeMarkerIndex / FindStemIndex) and keep the family curve index.
                var target = pts[order[nextRank]];
                m.PositionStatic = new Vector2(target.X, IsFamily ? m.PositionStatic.Y : 0f);
                return true;
            }

            // ── Network/SNP traces: step along the (ascending) frequency axis ──
            if (Data is null || Data.Frequencies.Length < 2) return false;
            double before = m.Freq;
            if (direction > 0) IncrementMarkerFreq(m); else DecrementMarkerFreq(m);
            return m.Freq != before;
        }

        public void SnapMarkerToStabilityCircle(Marker m, int freqIndex)
        {
            if (!IsStabilityCircle) return;
            var nearest = FindNearestPointOnStabilityCircle(m.PositionStatic, freqIndex);
            if (nearest.HasValue) m.PositionStatic = nearest.Value;
        }

        // ---- Equality ---------------------------------------------------

        public override bool Equals(object? obj) => obj is Trace t && t.Id == Id;
        public override int  GetHashCode()        => Id.GetHashCode();

        public bool SameElement(Trace other) =>
            Row == other.Row && Col == other.Col && Derived == other.Derived;

        // ---- Private helpers --------------------------------------------

        private static double Dist(Vector2 a, Vector2 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
