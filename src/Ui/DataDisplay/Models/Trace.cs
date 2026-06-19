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
        public PrecisionFormat FormatString          { get; set; } = PrecisionFormat.F;
        public int             MaximumFractionDigits { get; set; } = 3;

        /// <summary>Optional URL of the source file (for display / reload).</summary>
        public string? SourcePath { get; set; }

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

        /// <summary>One curve of a family trace: its iterated-axis value (for the legend) + its points.</summary>
        public sealed class FamilyCurve
        {
            public double  AxisValue { get; init; }
            public string? AxisLabel { get; init; }
            public List<Vector2> Points { get; } = new();
        }

        /// <summary>N curves when IsFamily; empty otherwise. Derived (never serialized) — rebuilt on load.</summary>
        public List<FamilyCurve> FamilyCurves { get; } = new();

        /// <summary>Name of the iterated (family) axis — the legend title.</summary>
        public string? FamilyAxisName { get; set; }

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

        private bool _cubeIsScalar;
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
            SourcePath        = src.SourcePath;
            ColumnWidth       = src.ColumnWidth;
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
            _cubeIsScalar      = src._cubeIsScalar;
            // Per-port Z0 (Phase 7.2f).
            SourceZ0PerPort   = src.SourceZ0PerPort;
            SourceZ0IsUnusual = src.SourceZ0IsUnusual;
            // Family axis name (Phase 7.3b).
            FamilyAxisName = src.FamilyAxisName;
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
                                PlotType plotType, FreqUnit freqUnit)
        {
            _cubeIsScalar      = false;
            _cubeXValues       = xValues;
            _cubeComplexValues = complexValues;
            _cubeRealValues    = realValues;
            _cubeXAxisName     = xAxisName;
            _cubeXUnit         = xUnit;
            BuildCubePath(plotType, freqUnit);
        }

        /// <summary>Binds a scalar (rank-0) cube value. Renders as one Table cell; on any non-Table plot type
        /// the trace produces no geometry and flags ScalarOnNonTableInvalid for a soft label.</summary>
        public void SetScalarCubeData(Complex? complexValue, double? realValue,
                                      PlotType plotType, FreqUnit freqUnit)
        {
            _cubeIsScalar      = true;
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
            PlotType plotType, FreqUnit freqUnit)
        {
            _cubeIsScalar = false;
            _cubeXValues = xValues; _cubeXAxisName = xAxisName; _cubeXUnit = xUnit;
            _cubeComplexValues = null; _cubeRealValues = null;
            FamilyAxisName = familyAxisName;
            FamilyCurves.Clear();
            Points.Clear();
            RectValueInvalid = false;

            bool isRect = plotType.IsRect();
            double xScale = IsFreqUnit(xUnit) ? freqUnit.Scale() : 1.0;

            foreach (var (axisValue, axisLabel, cz, rv) in curves)
            {
                var fc = new FamilyCurve { AxisValue = axisValue, AxisLabel = axisLabel };
                int n = xValues.Length;
                bool isComplex = cz is not null;
                if (isRect && isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
                { RectValueInvalid = true; FamilyCurves.Add(fc); continue; }

                for (int i = 0; i < n; i++)
                {
                    if (isRect)
                    {
                        double? y = RectY(isComplex ? cz![i] : (Complex?)null, isComplex ? (double?)null : rv![i]);
                        if (y is double yy) fc.Points.Add(new Vector2((float)(xValues[i] * xScale), (float)yy));
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

            double xScale = IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;
            for (int i = 0; i < n; i++)
            {
                double x = _cubeXValues[i] * xScale;
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
            if (IsCubeBound) return Vector2.Zero;
            if (IsStabilityCircle) return m.PositionStatic;
            int fi = Array.FindIndex(Data.Frequencies, f => f >= m.Freq - 1e-6);
            if (fi < 0) fi = Data.Frequencies.Length - 1;
            if (fi >= 0 && fi < Points.Count) return Points[fi];
            return Vector2.Zero;
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
                    CubeTransform.None  => FormatCubeMA(z, f),
                    CubeTransform.Conj  => FormatCubeMA(Complex.Conjugate(z), f),
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

        private static string FormatCubeMA(Complex c, string fmt)
            => $"{c.Magnitude.ToString(fmt)}∠{(c.Phase * 180.0 / Math.PI):F1}°";

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
            var lines = new List<(string, bool)>
            {
                (m.MarkerString,                        true),
                (m.FreqString,                          false),
                (GetMarkerValString(m, showFilePrefix), false)
            };
            if (MarkerShowsImpedance(m))
                lines.Add((GetMarkerImpedanceString(m), false));
            if (IsStabilityCircle)
                lines.Add((MuString(m), false));

            if (m.IsMulti && otherTraces != null)
                foreach (var other in otherTraces)
                    lines.Add((GetMultiMarkerLine(m, other), false));

            return lines;
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
