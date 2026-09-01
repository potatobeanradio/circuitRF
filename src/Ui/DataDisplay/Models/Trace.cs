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
using CircuitRF.Ui.DataDisplay.ViewModels;

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

    /// <summary>
    /// Network metrics derived from an S-matrix. Appended-to only — the numeric values are
    /// persisted in `.cdd` (TraceConfig.Derived), so existing members must keep their ordinals.
    /// </summary>
    public enum DerivedParameters
    {
        None, SourceStabilityCircle, LoadStabilityCircle, MaxGain, Mu, MuPrime,
        // brief-stability-passivity-touchstone.md — K/|Δ| complete the standard stability set;
        // Passivity is the one metric here that is NOT 2-port-limited (R-stb-6).
        K, DeltaMag, Passivity,
        // 2026-08-19 — group delay, −dφ/dω on the unwrapped S21 phase. APPENDED, like everything
        // else here: the ordinal is persisted in `.cdd`.
        GroupDelay,
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
            DerivedParameters.K                     => "Rollett K",
            DerivedParameters.DeltaMag              => "|Δ|",
            DerivedParameters.Passivity             => "Passivity, σmax",
            DerivedParameters.GroupDelay            => "Group Delay (ns)",
            _                                       => ""
        };

        /// <summary>
        /// True for metrics that are scalars versus frequency → rectangular plots. Stability
        /// circles are loci in the Γ plane → Smith/Polar. The two do not mix in one plot, which is
        /// what R-stb-5's offer-what-fits gating is built on.
        /// </summary>
        public static bool IsScalarVsFrequency(this DerivedParameters d) =>
            d is DerivedParameters.Mu or DerivedParameters.MuPrime or DerivedParameters.K
              or DerivedParameters.DeltaMag or DerivedParameters.MaxGain
              or DerivedParameters.Passivity or DerivedParameters.GroupDelay;

        /// <summary>True for the Γ-plane loci (Smith/Polar only).</summary>
        public static bool IsCircleLocus(this DerivedParameters d) =>
            d is DerivedParameters.SourceStabilityCircle or DerivedParameters.LoadStabilityCircle;

        /// <summary>
        /// True when the metric is a 2-port formula and therefore needs an ordered port pair.
        /// Passivity is defined for any N and defaults to the whole network (R-stb-6).
        /// </summary>
        public static bool NeedsPortPair(this DerivedParameters d) =>
            d != DerivedParameters.None && d != DerivedParameters.Passivity;

        /// <summary>
        /// True when the metric is a derivative along the SWEEP rather than a function of one matrix
        /// — so it needs the frequency axis and cannot go through <c>NetworkMetrics.TwoPortMetric</c>.
        /// Group delay is the only one today; <see cref="ToNetworkMetric"/> refuses it for the same
        /// reason <c>NetworkMetric</c> has no member for it.
        /// </summary>
        public static bool IsSweepDerivative(this DerivedParameters d) =>
            d is DerivedParameters.GroupDelay;

        /// <summary>Maps to the RfCore metric enum; throws for non-scalar/None members.</summary>
        public static RfCore.Data.NetworkMetric ToNetworkMetric(this DerivedParameters d) => d switch
        {
            DerivedParameters.Mu        => RfCore.Data.NetworkMetric.Mu,
            DerivedParameters.MuPrime   => RfCore.Data.NetworkMetric.MuPrime,
            DerivedParameters.K         => RfCore.Data.NetworkMetric.K,
            DerivedParameters.DeltaMag  => RfCore.Data.NetworkMetric.DeltaMag,
            DerivedParameters.MaxGain   => RfCore.Data.NetworkMetric.MaxGain,
            DerivedParameters.Passivity => RfCore.Data.NetworkMetric.Passivity,
            _ => throw new ArgumentOutOfRangeException(nameof(d), $"{d} has no NetworkMetric."),
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

        /// <summary>
        /// True when the user has explicitly checked "Override" on the trace card's Z0 control.
        /// This is the single gate on ALL reference-impedance renormalization of displayed data:
        /// <list type="bullet">
        /// <item>OFF — the trace renders the source's OWN data with no renormalization whatsoever,
        /// even when the source's ports carry different (or complex) references. <see cref="Z0"/>
        /// is then merely a mirror of the source's port-1 reference, shown read-only.</item>
        /// <item>ON — every port is renormalized to the uniform user <see cref="Z0"/>, starting from
        /// the source's true per-port references (<see cref="SourceZ0PerPort"/>), so a non-uniform
        /// source is accounted for correctly.</item>
        /// </list>
        /// Introduced by brief-dd-z0-nonuniform-override: without it, a trace on a non-uniform
        /// source silently renormalized every port to the port-1 reference even with Override off,
        /// turning a genuine −20 dB match into a −5 dB one. Derived quantities (µ, µ', |Δ|, MaxGain)
        /// are NOT affected — those are only defined at a uniform real reference, so their internal
        /// renormalization is mathematics, not a display choice, and stays unconditional.
        /// </summary>
        public bool Z0OverrideEnabled { get; set; }

        /// <summary>
        /// The TRUE per-port references of the source. <see cref="SourceZ0PerPort"/> is stamped from
        /// the source's own Z0 cube (Phase 7.2f); <c>Data.Z0</c> is a single value because SNP is
        /// uniform-only by design, and <c>DataSetBuilder.ToSnp</c> flattens a non-uniform cube to
        /// port 1 — so for a simulated S cube the stamped array is the only faithful record.
        /// One copy, used by every derived/metric path (they must all agree on the references, or
        /// the plotted curve and its readouts disagree).
        /// </summary>
        internal Complex[] SourceZ0PerPortResolved(int nPorts) =>
            SourceZ0PerPort is { } perPort && perPort.Length >= nPorts
                ? perPort.Take(nPorts).ToArray()
                : RFNetwork.Z0Array(Data.Z0, nPorts);

        /// <summary>
        /// The reference impedance a DERIVED Γ-plane locus (a load/source stability circle) lives in.
        /// <c>BuildDerivedPath</c> hands the circle routines the 2-port from
        /// <c>NetworkMetrics.TwoPortUniformReal</c>, which renormalizes BOTH ports to
        /// <c>Re(z0[InputPort−1])</c> — so the plotted Γ is in that uniform REAL reference and
        /// nothing else. Mirrors that target exactly; changing one without the other silently
        /// desynchronizes the drawn circle from every readout taken on it.
        /// <para>Deliberately ignores <see cref="Z0OverrideEnabled"/>/<see cref="Z0"/>: the Z0 box is
        /// not even shown for a derived trace, and <c>BuildDerivedPath</c> does not consult it, so
        /// honouring it here would report an impedance the drawn circle does not have.</para>
        /// </summary>
        private Complex DerivedGammaReferenceZ0
        {
            get
            {
                int nPorts = Data.Ports;
                int inIdx  = Math.Clamp(InputPort - 1, 0, Math.Max(0, nPorts - 1));
                return new Complex(SourceZ0PerPortResolved(nPorts)[inIdx].Real, 0.0);
            }
        }

        /// <summary>The per-port reference impedances the DISPLAYED data is referenced to — the
        /// single authority behind the rule above. Override off ⇒ the source's own references;
        /// override on ⇒ the user's uniform <see cref="Z0"/> on every port.</summary>
        internal Complex[] DisplayZ0PerPort(int nPorts)
        {
            if (Z0OverrideEnabled) return RFNetwork.Z0Array(_z0, nPorts);
            if (SourceZ0PerPort is { } src && src.Length >= nPorts)
                return src.Length == nPorts ? src : src.Take(nPorts).ToArray();
            return RFNetwork.Z0Array(Data.Z0, nPorts);
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
                if (!IsDerived)
                {
                    // Circle geometry belongs to the derived mode and nothing else clears it — only
                    // BuildDerivedPath/BuildMatrixPath do, and BuildCubePath never runs them. Left
                    // behind, it keeps rendering (TraceRenderer.BuildPath branches on
                    // IsStabilityCircle) after the trace has become something else.
                    StabilityCircleCentres.Clear();
                    StabilityCircleRadii.Clear();
                    StabilityCircleStableInside.Clear();
                    return;
                }

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
                    // Keep the marker's FREQUENCY and move it the shortest way onto that
                    // frequency's circle. Two things were wrong here before:
                    //   • `f == m.Freq - 1e-6` is an exact float comparison against a SHIFTED
                    //     value, so it never matched and every marker fell through to the LAST
                    //     frequency — the readout then described a frequency the marker was not at.
                    //   • The position was zeroed first, so "nearest point on the circle" was always
                    //     measured from the origin and the marker teleported to an arbitrary point
                    //     instead of staying beside where the user had put it.
                    int fi = NearestFrequencyIndex(m.Freq);
                    if (fi < 0) continue;
                    m.Freq = Data.Frequencies[fi];
                    SnapMarkerToStabilityCircle(m, fi);
                }
            }
        }

        public bool IsDerived => Derived != DerivedParameters.None;

        /// <summary>True for a derived metric that is a real scalar versus frequency (µ, µ′, K,
        /// |Δ|, MaxGain, passivity, group delay) — i.e. every derived trace except a Γ-plane locus.</summary>
        public bool IsDerivedScalar => IsDerived && Derived.IsScalarVsFrequency();

        /// <summary>
        /// True when <see cref="YAxis"/> names the COMPLEX value itself rather than a real scalar.
        /// </summary>
        /// <remarks>
        /// <c>YAxis == Complex</c> alone is not that question. A scalar derived metric stores its
        /// display choice in <see cref="YAxis"/> too (Max Gain uses <c>Db</c> for 10·log10 and
        /// <c>Complex</c>/<c>Mag</c> for the linear ratio — see <see cref="MaxGainIsLog"/>), and its
        /// value is a real number in every one of those states. Readout, hit-test and impedance
        /// paths that branch on "is this a complex trace" must ask THIS, or a linear Max Gain trace
        /// gets formatted as "g + j0" and offered an impedance it has no reflection coefficient for.
        /// A stability circle IS a Γ-plane locus and is deliberately still complex here.
        /// </remarks>
        public bool YAxisIsComplexValue => YAxis == DependentVarFormat.Complex && !IsDerivedScalar;

        // ---- Display transform for a derived scalar ---------------------
        //
        //  Until 2026-08-30 the transform combo was, for a derived trace, "a unit annotation
        //  wearing a transform's clothes" (DataDisplay/RESOLVED.md) — it changed the label and
        //  nothing else, and for MaxGain it labelled a 10·log10 quantity "dB20". MaxGain now
        //  genuinely has two forms, so the combo selects between them:
        //
        //    dB10  → 10·10log10(MAG)   (the default, and what every pre-existing .cdd restores)
        //    Mag   → the linear power ratio
        //    None  → the linear power ratio, unlabelled
        //
        //  The choice is stored in YAxis rather than in Transform because Transform is not
        //  round-tripped for a network/derived trace (DataDisplayViewModel restores YAxis and
        //  Derived only), so an old file would silently reload as linear.

        /// <summary>
        /// True when a Max Gain trace plots 10·log10(MAG/MSG) rather than the linear power ratio.
        /// Meaningless — and always false — for any other derived metric.
        /// </summary>
        public bool MaxGainIsLog =>
            Derived == DerivedParameters.MaxGain && YAxis == DependentVarFormat.Db;

        /// <summary>
        /// The transform this trace's Y value actually is, in the display language the axis label,
        /// the marker readouts and the trace card's transform combo all share. Cube traces already
        /// carry it directly; a network trace's <see cref="YAxis"/> maps onto it; and Max Gain is
        /// the one quantity whose dB is 10·log10, so it reads dB10 and never dB20.
        /// </summary>
        public CubeTransform DisplayTransform
        {
            get
            {
                if (IsCubeBound) return Transform;
                if (Derived == DerivedParameters.MaxGain)
                    return YAxis switch
                    {
                        DependentVarFormat.Db  => CubeTransform.dB10,
                        DependentVarFormat.Mag => CubeTransform.Mag,
                        _                      => CubeTransform.None,
                    };
                return YAxis switch
                {
                    DependentVarFormat.Db        => CubeTransform.dB20,
                    DependentVarFormat.Mag       => CubeTransform.Mag,
                    DependentVarFormat.Phase     => CubeTransform.Phase,
                    DependentVarFormat.Real      => CubeTransform.Real,
                    DependentVarFormat.Imaginary => CubeTransform.Imag,
                    _                            => CubeTransform.None,
                };
            }
        }

        /// <summary>
        /// The transforms a trace of this kind may be given — everything else is keyed out and
        /// disabled in the trace card's combo. Only Max Gain narrows the list today: it is a real,
        /// positive power gain, so Real/Imag/Phase/Conj say nothing about it and dB20/dB would be a
        /// lie about the arithmetic. <c>null</c> means "no per-trace restriction".
        /// </summary>
        public static readonly CubeTransform[] MaxGainTransforms =
            { CubeTransform.None, CubeTransform.dB10, CubeTransform.Mag };

        /// <summary>
        /// Applies a <see cref="DisplayTransform"/> selection back onto the trace's own state.
        /// Returns false when the transform is not one this trace accepts, in which case nothing
        /// is written.
        /// </summary>
        public bool SetDisplayTransform(CubeTransform t)
        {
            if (Derived == DerivedParameters.MaxGain)
            {
                if (Array.IndexOf(MaxGainTransforms, t) < 0) return false;
                YAxis = t switch
                {
                    CubeTransform.dB10 => DependentVarFormat.Db,
                    CubeTransform.Mag  => DependentVarFormat.Mag,
                    _                  => DependentVarFormat.Complex,
                };
                Transform = t;
                return true;
            }

            Transform = t;
            YAxis = t switch
            {
                CubeTransform.dB20  => DependentVarFormat.Db,
                CubeTransform.Mag   => DependentVarFormat.Mag,
                CubeTransform.Phase => DependentVarFormat.Phase,
                CubeTransform.Real  => DependentVarFormat.Real,
                CubeTransform.Imag  => DependentVarFormat.Imaginary,
                _                   => DependentVarFormat.Complex,
            };
            return true;
        }

        // ---- Ordered port selection for network metrics (R-stb-3/3a) ----
        //
        //  1-based, and ORDERED — port roles are not symmetric. μ is the LOAD stability factor and
        //  μ′ the SOURCE stability factor, so swapping input and output swaps which is which;
        //  (1,2) and (2,1) are different selections, never an unordered pair. Two independent
        //  selectors rather than an enumerated pair list, because the pair count grows as N(N−1)/2
        //  (28 for an 8-port, 190 for a 20-port) and would not scale.

        /// <summary>1-based input port for 2-port network metrics. Default 1.</summary>
        public int InputPort { get; set; } = 1;

        /// <summary>1-based output port for 2-port network metrics. Default 2.</summary>
        public int OutputPort { get; set; } = 2;

        /// <summary>
        /// For Passivity only: measure the WHOLE network rather than the extracted (input, output)
        /// pair. Default true — whole-network passivity is the meaningful default (R-stb-6), and a
        /// sub-matrix can test passive while the full network is not.
        /// </summary>
        public bool PassivityWholeNetwork { get; set; } = true;

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
        /// "&lt;name&gt;.npy" (flat results/, R-res-1) = a specific sim run or user-named baseline;
        /// rooted path = explicit Touchstone file.</summary>
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

        private string? _cubeName;
        public string? CubeName
        {
            get => _cubeName;
            set { _cubeName = value; if (value is not null) DropDerivedForCubeBinding(); }
        }

        public AxisSlice[]?  Slice          { get; set; }
        public CubeTransform Transform      { get; set; } = CubeTransform.None;
        public string?       InvalidSpecText { get; set; }

        /// <summary>Full element-wise expression string (e.g. <c>mag(V[:, 0, 0]) + mag(V[:, 0, 1])</c>).
        /// When non-null, the owner resolves via <c>TraceExpression</c> instead of the single-slice path.
        /// Supersedes CubeName/Slice/Transform for value production.</summary>
        private string? _expression;
        public string? Expression
        {
            get => _expression;
            set { _expression = value; if (value is not null) DropDerivedForCubeBinding(); }
        }

        /// <summary>
        /// A cube binding and a derived network metric are mutually exclusive, and <b>cube wins</b> —
        /// <see cref="BuildPath"/> tests <see cref="IsCubeBound"/> before <see cref="IsDerived"/>, so
        /// a trace holding both BUILDS a cube path while <see cref="IsStabilityCircle"/> stays true,
        /// and the renderer (which keys off that flag) draws the stale circles instead of the curve.
        /// That is exactly the "picked Stability Circles, then picked S(1,1) again, and S(1,1) never
        /// appeared" bug, with the In/Out port selectors left on the card for the same reason
        /// (<c>TraceRowViewModel.ShowPortSelectors</c> reads <see cref="Derived"/>).
        /// <para>Enforced HERE, on the two setters that make a trace cube-bound, rather than at each
        /// call site — the picker, a typed spec, and a <c>.cdd</c> load all pass through them, and a
        /// <c>.cdd</c> saved before this fix carries the broken combination on disk.</para>
        /// </summary>
        private void DropDerivedForCubeBinding()
        {
            if (_derived == DerivedParameters.None) return;
            Derived = DerivedParameters.None;   // via the setter, so the circle geometry is dropped too
        }

        /// <summary>
        /// "Plot versus" — the X side of a <c>Y vs X</c> trace: an ordinary trace spec whose values
        /// become this trace's X coordinates instead of the Y cube's swept axis (PA work plots gain
        /// against Pout, not against the swept Pin). Null ⇒ X comes from the cube axis, as always.
        /// <para>Held as its OWN field rather than folded into <see cref="Expression"/> on purpose:
        /// the Y side keeps its <see cref="CubeName"/>/<see cref="Slice"/> identity, so the card's
        /// axis-role editor, the family path, and the pinned-axis labels all keep working exactly as
        /// they do for a non-versus trace. Versus is an X-source attribute, not a new kind of
        /// expression.</para>
        /// </summary>
        public string? XSpec { get; set; }

        /// <summary>Data source the <see cref="XSpec"/> resolves against. Null ⇒ the same source as
        /// the Y side (<see cref="SourcePath"/>) — the ordinary case. Set only for a cross-source X
        /// (measured Pout against simulated Gain, say), where the point-count gate is what keeps the
        /// pairing honest.</summary>
        public string? XSourcePath { get; set; }

        /// <summary>True when this trace names its own X data (<see cref="XSpec"/>).</summary>
        public bool IsVersus => !string.IsNullOrWhiteSpace(XSpec);

        /// <summary>Display-only alias of the X side's source, stamped by the owner when the X data
        /// comes from a DIFFERENT file than the Y data. It is rendered as an <c>alias::</c> prefix in
        /// the shorthand (and accepted back on typed input) so a cross-source trace reads as one.
        /// Never used for resolution — <see cref="XSourcePath"/> is, because an alias is renamable
        /// and a path is not.</summary>
        public string? XSourceAlias { get; set; }

        /// <summary>The X spec as it is SHOWN: alias-qualified when the X source differs.</summary>
        private string XSpecDisplay => string.IsNullOrEmpty(XSourceAlias)
            ? XSpec ?? ""
            : $"{XSourceAlias}::{XSpec}";

        /// <summary>
        /// Re-attaches the X source's alias to an already-authored spec string. The alias is stamped
        /// by the OWNER when the trace resolves, which is necessarily after the picker wrote the
        /// expression text — without this, a cross-source trace would author "Gain vs Pout" and then
        /// never show which file that Pout came from.
        /// </summary>
        private string WithXSourceAlias(string spec)
        {
            if (!IsVersus || string.IsNullOrEmpty(XSourceAlias)) return spec;
            if (!VersusSpec.TrySplit(spec, out var ySide, out var xSide, out _)) return spec;
            return xSide.Contains("::", StringComparison.Ordinal)
                ? spec
                : VersusSpec.Join(ySide, XSpecDisplay);
        }

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

            /// <summary>Per-curve X values — set only for a "plot versus" family, where each curve's
            /// X data genuinely differs (Pout at 2.0 GHz is not Pout at 2.4 GHz). Null ⇒ the curve
            /// shares the trace-level X array, which is every ordinary family.</summary>
            public double[]?  RawX       { get; init; }
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

        // How each PINNED axis should READ in a label — the WHOLE token, not just the value:
        // "VDS=3.5 V" for a swept axis (its own value and unit, never the bare index), or "IDS" for
        // a labelled axis (the label names the quantity, so the axis name in front of it says
        // nothing). Owner-supplied for the same reason as the spectral pair above: the axis values
        // live on the cube and a Trace deliberately never holds one. Absent for an axis the owner
        // could not resolve, which falls back to the raw index.
        private IReadOnlyDictionary<string, string>? _pinnedAxisDisplay;

        /// <summary>
        /// Owner-supplied label token per pinned axis name, e.g. <c>{"VDS": "VDS=3.5 V", "branch": "IDS"}</c>.
        /// Derived state: reset by <see cref="SetCubeData"/>/<see cref="SetFamilyData"/> exactly like
        /// <see cref="SetPinnedSpectral"/>, and re-applied by the owner immediately afterwards.
        /// </summary>
        public void SetPinnedAxisDisplay(IReadOnlyDictionary<string, string>? map)
            => _pinnedAxisDisplay = map;

        /// <summary>The resolved display text for a pinned axis, or null when unresolved.</summary>
        public string? PinnedAxisDisplay(string axisName)
            => _pinnedAxisDisplay is { } m && m.TryGetValue(axisName, out var v) ? v : null;

        // Per-X fundamental (Hz) injected by the owner before SetCubeData/SetFamilyData.
        // Non-null only for single-tone HB spectrum traces; null for all other trace types.
        private double[]? _f0ByX;
        public void SetSpectrumFundamentals(double[]? f0ByX) => _f0ByX = f0ByX;

        private bool _cubeIsScalar;
        private PlotType _lastPlotType = PlotType.Rect;
        public  bool CubeIsScalar => _cubeIsScalar;

        // True when the values were produced by a multi-cube TraceExpression (e.g. "10*log10(Pout_W*1000)"):
        // the expression text already encodes any transform, so a REAL result is the final value and the
        // transform combo must NOT be re-applied during rendering (else a chosen dB/Mag double-transforms it).
        // A COMPLEX expression result still needs the combo to reduce to a scalar on Rect, so it is unaffected.
        private bool _transformBaked;
        public  bool TransformBaked => _transformBaked;

        /// <summary>True when the transform combo has no effect on this trace: a REAL multi-cube expression
        /// result, whose transform is already encoded in the expression text. (A complex expression result
        /// still needs the combo for scalar reduction, so it is NOT inert.) The combo is disabled + forced
        /// to None in that case so the rendered value always equals the expression.</summary>
        public bool TransformIsInert => _transformBaked && _cubeComplexValues is null && _cubeRealValues is not null;

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
        /// The trace's quantity as a READOUT writes it — marker info box, marker editor, the
        /// info box's own trace menu — in the same display language the plot's Y-axis label uses:
        /// <c>S(1,1) dB20</c>, never <c>dB(S(1,1))</c>. Optionally prefixed with the source-file
        /// stem, exactly as <see cref="Description"/> does.
        /// </summary>
        /// <remarks>
        /// <b>Owner, 2026-08-21:</b> <i>"the MarkerInfoBox and popup window are not respecting the
        /// y-label used for s-parameters … I don't want these two text renderings to drift,
        /// 'S(1,1) dB20' is the correct."</i>
        ///
        /// <para>The axis label moved onto <see cref="TraceLabeler"/>'s language on 2026-08-20; the
        /// marker readouts still read <see cref="ShortDescription"/>, so one plot showed a trace
        /// labelled two ways. Both halves now come through
        /// <see cref="TraceLabeler.QuantityFor"/> — the same single transform-suffix table — which
        /// is the only arrangement in which they cannot drift apart again.</para>
        ///
        /// <para><see cref="Description"/>/<see cref="ShortDescription"/> are deliberately left
        /// alone: they are the trace's own description, and <c>BuildPickerYExpression</c> reads
        /// <see cref="ShortDescription"/> as an EXPRESSION fallback, where a trailing " dB20"
        /// suffix would not parse.</para>
        /// </remarks>
        public string ReadoutDescription(bool showFilePrefix)
        {
            string prefix = showFilePrefix && SourcePath != null
                ? System.IO.Path.GetFileNameWithoutExtension(SourcePath) + ".."
                : "";
            return prefix + TraceLabeler.QuantityFor(this);
        }

        /// <summary>
        /// The file this trace's data came from: <see cref="SourcePath"/> when set, otherwise the
        /// bound network's own <c>FilePath</c>.
        ///
        /// <para>One definition, because the two used to disagree: a trace with a null SourcePath
        /// but a perfectly well-known <c>Data.FilePath</c> contributed NO source component to
        /// <see cref="TraceLabeler.ComputeMinimalLabels"/>, so it alone lost its alias prefix while
        /// every sibling on the plot kept theirs — the label convention silently not applying to
        /// one trace.</para>
        /// </summary>
        public string? EffectiveSourcePath => SourcePath ?? Data?.FilePath;

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
                else if (Expression is not null)        baseLabel = WithXSourceAlias(Expression);
                else if (!IsCubeBound || Slice is null) baseLabel = ShortDescription;
                else                                    baseLabel = BuildPickerExpression();
                if (ScalarOnNonTableInvalid && !baseLabel.Contains("<invalid")) baseLabel += " <invalid>";
                return baseLabel;
            }
        }

        /// <summary>
        /// Y-axis label for this trace on a Rect plot: always the caller-supplied minimal label
        /// (<see cref="TraceLabeler.ComputeMinimalLabels"/>, which already handles quantity
        /// formatting for BOTH network and cube-bound traces via its own CubeShorthand-equivalent
        /// logic, and — the point of this parameter — the source alias), with soft suffixes:
        ///   • " &lt;invalid: complex on scalar plot type&gt;" when a cube-bound value can't render
        ///     on Rect (complex value, scalar plot type),
        ///   • " dimension mismatch" when this trace's cube X-axis differs from the plot's X-axis.
        /// A prior version of this method recomputed its OWN label for cube-bound traces
        /// (CubeShorthand + a raw file-STEM prefix, never the user's alias) instead of using the
        /// supplied one — that divergence is exactly why switching a trace's data source used to
        /// leave the Rect Y-axis label showing the wrong (un-aliased, or stale) text even after the
        /// underlying label-strip mechanism was fixed to recompute correctly.
        /// </summary>
        public string RectYLabel(string networkFallback, bool dimensionMismatch)
        {
            if (IsContourTrace) return "";
            string baseLabel = networkFallback;
            if (IsCubeBound && RectValueInvalid && !baseLabel.Contains("<invalid"))
                baseLabel += " <invalid: complex on scalar plot type>";
            if (dimensionMismatch) baseLabel += " dimension mismatch";
            if (IsZ0ReReferenced) baseLabel += " @ Z0=" + ComplexStringHelper.Format(_z0) + "Ω";
            return baseLabel;
        }

        /// <summary>
        /// True when this trace's displayed reference <see cref="Z0"/> has been changed away from
        /// the SOURCE's own reference — brief-dd-z0-renormalization.md §4. Compared against the
        /// source, never a literal 50 Ω, so a native-75 Ω source displayed at 75 Ω shows no token.
        /// A genuinely non-uniform/complex ("unusual") source has no single native value to compare
        /// against — displaying it at ANY uniform reference is already a re-reference, so the token
        /// always shows in that case. Restricted to the same trace kinds that actually expose the Z0
        /// field to the user (network: non-derived MatrixType.S; cube: a network-param S/Z/Y element)
        /// — everywhere else <see cref="Z0"/> is an inert default the user never touched.
        /// <para>Gated on <see cref="Z0OverrideEnabled"/>: with Override off nothing is
        /// renormalized at all, so the data IS at the source's own reference and the token would be
        /// a lie — including for an "unusual" (non-uniform) source, which is now rendered raw.</para>
        /// </summary>
        private bool IsZ0ReReferenced
        {
            get
            {
                if (!Z0OverrideEnabled) return false;
                if (!IsCubeBound)
                {
                    if (Derived != DerivedParameters.None || MatrixType != MatrixType.S) return false;
                    return SourceZ0IsUnusual || _z0 != Data.Z0;
                }
                if (SourceZ0PerPort is not { Length: > 0 } perPort) return false;
                return SourceZ0IsUnusual || _z0 != perPort[0];
            }
        }

        /// <summary>Computes the function-call shorthand from CubeName/Slice/Transform only,
        /// ignoring Expression.  Used by the owner to sync Expression after picker edits.</summary>
        internal string BuildPickerExpression()
        {
            string body = BuildPickerYExpression();
            return IsVersus ? VersusSpec.Join(body, XSpecDisplay) : body;
        }

        /// <summary>The Y side alone — the shorthand as it was before "plot versus" existed.
        /// <see cref="BuildPickerExpression"/> appends the <c>vs X</c> half on top of this.</summary>
        private string BuildPickerYExpression()
        {
            if (CubeName is null || Slice is null)
            {
                // The fallback description can itself be a "Y vs X" string (it reads Expression),
                // so take only its Y half — otherwise re-appending the X side would compound into
                // "Gain vs Pout vs Gain" on the next edit.
                string d = ShortDescription;
                return VersusSpec.TrySplit(d, out var yHalf, out _, out _) ? yHalf : d;
            }
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
                // A narrowed X must re-emit as "a..b", the same end-exclusive spelling
                // SliceTokenParser reads back. Emitting a bare ":" here was the other half of the
                // dropped-range bug: the shortcut above already excludes IsNarrowedRange, but this
                // mapping widened the range anyway, so anything that regenerates the spec text —
                // an S/Z/Y toggle, a signal reselection — silently threw the narrowing away
                // mid-session, with no edit by the user.
                s.IsNarrowedRange                  ? $"{s.RangeStart}..{s.RangeEndExclusive}"
                : s.Role == AxisRole.KeepAsX       ? ":"
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
                return MaxGainIsLog
                    ? $"{prefix}dB10({Derived.Description()})"
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
            Z0OverrideEnabled = src.Z0OverrideEnabled;
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
            XSpec           = src.XSpec;
            XSourcePath     = src.XSourcePath;
            XSourceAlias    = src.XSourceAlias;
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
            _pinnedAxisDisplay    = src._pinnedAxisDisplay;
            _cubeIsScalar      = src._cubeIsScalar;
            _transformBaked    = src._transformBaked;
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
            if      (IsFamily)    BuildFamilyPath(plotType, freqUnit);
            else if (IsCubeBound) BuildCubePath(plotType, freqUnit);
            else if (IsDerived)   BuildDerivedPath(plotType, freqUnit);
            else                  BuildMatrixPath(plotType, freqUnit);
        }

        // ---- Plot-type remap (Phase brief-dd-plot-type-integrity §1) ----

        /// <summary>
        /// Single source of truth for what a plot-type change does to a CUBE-BOUND trace's
        /// Transform/Expression. Called by <see cref="Plot.SetPlotType"/> for every trace, right
        /// before <see cref="BuildPath"/>. A valid trace must stay valid across every plot-type
        /// change — this transforms only as much as the new plot type requires, and never blanks or
        /// deletes a trace that can still be made to render.
        ///
        /// <para>No-op for: a non-cube trace (network/derived plot-type behaviour is unchanged and
        /// stays in <see cref="Plot.SetPlotType"/> — it predates cube binding and has its own
        /// well-established rules); a contour trace (already plot-type aware via
        /// <c>TraceRowViewModel.RebuildContour</c>); Table on EITHER side (a Table renders complex
        /// and scalar cells alike — no transform change, either direction); a REAL multi-cube
        /// expression result (<see cref="TransformIsInert"/> — the transform lives in the baked
        /// expression text, never in <see cref="Transform"/>); real (non-complex) underlying data
        /// (nothing to remap to — kept as-is so the reverse switch restores a perfectly good
        /// trace); and a Rect→Rect-equivalent transition (Smith↔Polar, both complex-plane).</para>
        ///
        /// <para>Only rewrites <see cref="Expression"/> for a genuine picker-authored trace
        /// (<see cref="CubeName"/> != null) — a user-typed multi-cube expression is never rewritten,
        /// only its <see cref="Transform"/> (see CLAUDE.md §"Transform combo must not corrupt a
        /// network trace").</para>
        /// </summary>
        internal void RemapForPlotType(PlotType oldType, PlotType newType)
        {
            if (oldType == newType) return;
            if (!IsCubeBound || IsContourTrace) return;
            if (oldType == PlotType.Table || newType == PlotType.Table) return;
            if (TransformIsInert) return;

            bool wasComplexPlane = oldType.IsComplex();
            bool isComplexPlane  = newType.IsComplex();
            if (wasComplexPlane == isComplexPlane) return;   // both Rect-side — nothing to remap

            if (!CubeDataIsComplex) return;   // real cube-bound data: keep as-is either direction

            if (isComplexPlane)
            {
                // → Smith/Polar: undo any scalar reduction so the complex value passes through.
                if (Transform is CubeTransform.None or CubeTransform.Conj) return;
                Transform = CubeTransform.None;
            }
            else
            {
                // → Rect: apply the same "first-add nicety" default a fresh complex trace gets
                // (TraceRowViewModel.DefaultTransformFor's single source of truth).
                if (Transform is not (CubeTransform.None or CubeTransform.Conj)) return;
                Transform = DefaultRectTransform(true, HasPortAxes, CubeName);
            }

            if (CubeName is not null) Expression = BuildPickerExpression();
        }

        /// <summary>True when the currently-bound cube data is complex — family-aware (a family
        /// trace's underlying arrays live in <see cref="FamilyCurve.RawComplex"/>/RawReal, not the
        /// single-slice <c>_cubeComplexValues</c> cache). Also drives <c>TraceRowViewModel</c>'s
        /// per-plot-type transform-list filtering (§4).</summary>
        internal bool CubeDataIsComplex => IsFamily
            ? FamilyCurves.Count > 0 && FamilyCurves[0].RawComplex is not null
            : _cubeComplexValues is not null;

        /// <summary>True when this trace's cube axes look like an S/Y/Z port matrix (both "i" and
        /// "j" axes present in <see cref="Slice"/>) — mirrors <c>TraceRowViewModel.IsParameterCube</c>'s
        /// <c>cube.Axes</c> check, from the Trace's own cached Slice (no DataCube at this layer).</summary>
        private bool HasPortAxes => Slice is not null
            && Array.Exists(Slice, s => s.AxisName == "i")
            && Array.Exists(Slice, s => s.AxisName == "j");

        /// <summary>True when this trace is a cube-bound network-parameter REFLECTION element
        /// (bare cube name "S", pinned i == pinned j) — the only cube case where a per-sample
        /// impedance readout is meaningful (brief-dd-z0-renormalization.md §5). Off-diagonal or
        /// non-S cube-bound traces have no impedance meaning.</summary>
        private bool IsCubeReflectionElement
        {
            get
            {
                if (!HasPortAxes || Slice is null || CubeName is null) return false;
                if (BareCubeName(CubeName) != "S") return false;
                AxisSlice? iSlice = null, jSlice = null;
                foreach (var s in Slice)
                {
                    if (s.AxisName == "i") iSlice = s;
                    else if (s.AxisName == "j") jSlice = s;
                }
                return iSlice is { Role: AxisRole.PinToIndex } i
                    && jSlice is { Role: AxisRole.PinToIndex } j
                    && i.Index == j.Index;
            }
        }

        /// <summary>
        /// The "first-add nicety" default Rect transform for a COMPLEX cube: dB20 for an S-parameter
        /// cube, Mag otherwise. Single source of truth — <c>TraceRowViewModel.DefaultTransformFor</c>
        /// (which seeds a freshly-added trace from an actual <c>RfCore.Data.DataCube</c>) calls this
        /// same table via cube-derived (isComplex, hasPortAxes) inputs; <see cref="RemapForPlotType"/>
        /// calls it from the Trace's own cached state.
        /// </summary>
        internal static CubeTransform DefaultRectTransform(bool isComplex, bool hasPortAxes, string? cubeName)
        {
            if (!isComplex)   return CubeTransform.None;
            if (!hasPortAxes) return CubeTransform.Mag;
            string bare = BareCubeName(cubeName);
            return cubeName is null || bare == "S" ? CubeTransform.dB20 : CubeTransform.Mag;
        }

        private static string BareCubeName(string? cubeName)
        {
            if (cubeName is null) return "";
            int dot = cubeName.LastIndexOf('.');
            return dot < 0 ? cubeName : cubeName[(dot + 1)..];
        }

        // ---- Cube-bound path (Phase 7.2c-a) ----------------------------

        /// <summary>
        /// Injects the 1-D slice arrays produced by the owner (PlotInspectorViewModel)
        /// and immediately rebuilds Points.  Trace never holds a DataSet reference.
        /// </summary>
        public void SetCubeData(double[] xValues, Complex[]? complexValues, double[]? realValues,
                                string xAxisName, string? xUnit,
                                PlotType plotType, FreqUnit freqUnit, string[]? xLabels = null,
                                bool transformBaked = false)
        {
            _cubeIsScalar      = false;
            _transformBaked    = transformBaked;
            SetPinnedSpectral(null, null, double.NaN);   // derived state — reset on data-set (the VM
                                                         // re-applies it for a single-curve pinned trace)
            SetPinnedAxisDisplay(null);                  // same contract: resolved from the cube, so it
                                                         // cannot outlive the data it was resolved from
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
            _transformBaked    = false;
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
            // Expression-baked real value: the transform is already in the expression text — render as-is.
            double yr = _transformBaked ? v : Transform switch
            {
                CubeTransform.dB20 => 20.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                CubeTransform.dB10 or CubeTransform.dB => 10.0 * Math.Log10(Math.Max(Math.Abs(v), 1e-300)),
                CubeTransform.Mag  => Math.Abs(v),
                _                  => v,
            };
            return double.IsFinite(yr) ? yr : (double?)null;
        }

        /// <summary>Injects N pre-sliced family curves (each a rank-1 X/value pair) and builds their Points.
        /// xValues are shared across curves (same X axis). Each curve carries its own complex/real values.
        /// <para><paramref name="perCurveX"/> is the "plot versus" case and the ONE exception to the
        /// shared-X rule: when supplied, curve k plots against its own X array (Gain-vs-Pout, one curve
        /// per RFfreq, where each curve's Pout differs). <paramref name="xValues"/> then serves as the
        /// trace-level anchor (first curve) for anything that reads a single X array.</para></summary>
        public void SetFamilyData(double[] xValues, string xAxisName, string? xUnit, string familyAxisName,
            IReadOnlyList<(double axisValue, string? axisLabel, Complex[]? cz, double[]? rv)> curves,
            PlotType plotType, FreqUnit freqUnit, string? familyAxisUnit = null,
            IReadOnlyList<double[]>? perCurveX = null)
        {
            _cubeIsScalar = false;
            _transformBaked = false;
            SetPinnedSpectral(null, null, double.NaN);   // a family trace shows the per-curve tag, not a
                                                         // pinned line — clear any stale pinned context
            SetPinnedAxisDisplay(null);                  // resolved from the cube — cannot outlive it
            _cubeXValues = xValues; _cubeXAxisName = xAxisName; _cubeXUnit = xUnit;
            _cubeComplexValues = null; _cubeRealValues = null;
            FamilyAxisName = familyAxisName;
            FamilyAxisUnit = familyAxisUnit;
            FamilyCurves.Clear();
            FamilyColumnWidths.Clear();
            Points.Clear();

            for (int k = 0; k < curves.Count; k++)
            {
                var (axisValue, axisLabel, cz, rv) = curves[k];
                FamilyCurves.Add(new FamilyCurve { AxisValue = axisValue, AxisLabel = axisLabel,
                                                     RawComplex = cz, RawReal = rv,
                                                     RawX = perCurveX is not null && k < perCurveX.Count
                                                            ? perCurveX[k] : null });
            }

            BuildFamilyPath(plotType, freqUnit);
        }

        /// <summary>
        /// Rebuilds every <see cref="FamilyCurve"/>'s <c>Points</c> for the given plot type from the
        /// already-cached <c>RawComplex</c>/<c>RawReal</c> arrays — no re-slice from the DataSet needed.
        /// Split out of <see cref="SetFamilyData"/> so a plain plot-type or freq-unit change (which only
        /// calls <see cref="BuildPath"/>, never re-touches the DataSet) can re-render a family trace
        /// too — previously <see cref="BuildPath"/> only dispatched to <see cref="BuildCubePath"/>, which
        /// no-ops for a family trace (its single-slice cube arrays are null by design), so a family
        /// trace's geometry silently went stale on a plot-type switch.
        /// </summary>
        private void BuildFamilyPath(PlotType plotType, FreqUnit freqUnit)
        {
            _lastPlotType = plotType;
            RectValueInvalid = false;
            if (_cubeXValues is null) return;

            bool isRect = plotType.IsRect();
            bool isHarmonicFamilyX = string.Equals(_cubeXAxisName, HarmonicAxisName, StringComparison.Ordinal) && _f0ByX is not null;
            double xScale = IsFreqUnit(_cubeXUnit) ? freqUnit.Scale() : 1.0;

            foreach (var fc in FamilyCurves)
            {
                fc.Points.Clear();
                bool isComplex = fc.RawComplex is not null;
                if (isRect && isComplex && (Transform == CubeTransform.None || Transform == CubeTransform.Conj))
                { RectValueInvalid = true; continue; }

                // "Plot versus" families carry their own X per curve; every other family shares one.
                double[] xs = fc.RawX ?? _cubeXValues;
                int valueCount = isComplex ? fc.RawComplex!.Length : fc.RawReal!.Length;
                int n = Math.Min(xs.Length, valueCount);

                for (int i = 0; i < n; i++)
                {
                    if (isRect)
                    {
                        double? y = RectY(isComplex ? fc.RawComplex![i] : (Complex?)null, isComplex ? (double?)null : fc.RawReal![i]);
                        if (y is double yy)
                        {
                            double xCoord = isHarmonicFamilyX
                                ? xs[i] * _f0ByX![Math.Min(i, _f0ByX.Length - 1)] * freqUnit.Scale()
                                : xs[i] * xScale;
                            fc.Points.Add(new Vector2((float)xCoord, (float)yy));
                        }
                    }
                    else if (isComplex)
                    {
                        var z = Transform == CubeTransform.Conj ? Complex.Conjugate(fc.RawComplex![i]) : fc.RawComplex![i];
                        fc.Points.Add(new Vector2((float)z.Real, (float)z.Imaginary));
                    }
                }
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
                    // Expression-baked real value: transform is already in the expression — render as-is.
                    y = _transformBaked ? v : Transform switch
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

            // Per-port (unusual) path: with Override ON, renormalize S from the per-port
            // SourceZ0PerPort to the trace's own (uniform) Z0 before extracting the element —
            // brief-dd-z0-renormalization.md §2. With Override OFF, S is displayed exactly as the
            // source holds it, each port at its own reference (brief-dd-z0-nonuniform-override).
            // Z/Y stay computed straight from the raw per-port source (reference-independent —
            // the same invariant §1 makes for the cube path, not a coincidence).
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                var targetZ0Array = Z0OverrideEnabled ? RFNetwork.Z0Array(_z0, Data.Ports) : null;
                for (int fi = 0; fi < Data.FrequencyCount; fi++)
                {
                    Complex raw;
                    switch (MatrixType)
                    {
                        case MatrixType.S:
                            raw = targetZ0Array is null
                                ? Data.Matrices[fi][Row, Col]
                                : RFNetwork.SToS(Data.Matrices[fi], sourceZ0, targetZ0Array)[Row, Col];
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
                        if (Z0OverrideEnabled && _z0 != Data.Z0)
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

            int nPorts = Data.Ports;
            // Passivity is defined for any N ≥ 1; every other metric here is a 2-port formula
            // (R-stb-6). No per-N branching beyond that one distinction — a 3-, 5- or 12-port
            // travels exactly the same path as a 2-port (R-stb-3b).
            int minPorts = Derived.NeedsPortPair() ? 2 : 1;
            if (nPorts < minPorts) return;

            Complex[] z0PerPort = SourceZ0PerPortResolved(nPorts);

            if (plotType.IsRect())
            {
                // R-stb-1/R-stb-2: renormalize per-port → uniform real, then call the SAME
                // per-matrix stability functions the SNP path uses. NetworkMetrics performs no
                // mathematics of its own — it is purely the extract-and-renormalize adapter.
                double[] yData;
                try
                {
                    // Passivity has its own two scopes and is NOT a 2-port metric, so it must not
                    // be routed through TwoPortMetric (which correctly refuses it).
                    yData = DerivedScalarArray(z0PerPort);
                }
                catch (ArgumentException) { return; }   // invalid port pair → empty, never a crash

                double[] xData = Data.Frequencies.Select(f => f * freqUnit.Scale()).ToArray();
                for (int i = 0; i < xData.Length && i < yData.Length; i++)
                    if (double.IsFinite(yData[i]))
                        Points.Add(new Vector2((float)xData[i], (float)yData[i]));
            }
            else
            {
                // Γ-plane loci. The circle routines take an SNP, so hand them the extracted,
                // already-uniform-real 2-port rather than the raw N-port.
                if (nPorts < 2) return;
                SNP snp;
                try
                {
                    var pair = RfCore.Data.NetworkMetrics.TwoPortUniformReal(
                        Data.Matrices, z0PerPort, InputPort, OutputPort);
                    snp = new SNP(Data.Frequencies, pair, MatrixType.S, Data.Format,
                                  new Complex(z0PerPort[InputPort - 1].Real, 0));
                }
                catch (ArgumentException) { return; }

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

        // Memoizes the full-sweep derived-metric array so a per-cell Table read (DataPointScalar
        // called once per row by TableRenderer.FormatTraceCell) does not recompute the whole sweep
        // per cell. Same authority as BuildDerivedPath — NetworkMetrics — just indexed at fi instead
        // of iterated into Points.
        private double[]? _derivedMetricCache;
        private DerivedParameters _derivedMetricCacheDerived;
        private int _derivedMetricCacheInPort;
        private int _derivedMetricCacheOutPort;
        private bool _derivedMetricCachePassivityScope;
        private bool _derivedMetricCacheMaxGainLog;
        private Mat<Complex>[]? _derivedMetricCacheMats;

        /// <summary>
        /// The derived scalar versus frequency, whatever kind of derived it is — the ONE place the
        /// three routes (whole-network passivity, the per-matrix 2-port metrics, and the sweep
        /// derivative that is group delay) are chosen between, so the plotted path and the
        /// Table/marker path cannot pick differently.
        /// </summary>
        /// <remarks>
        /// Group delay is returned in NANOSECONDS while <c>NetworkMetrics.GroupDelay</c> returns
        /// seconds. The conversion belongs here rather than in RfCore: SI seconds is the right thing
        /// for a library to hand back, and a rectangular axis running from 0 to 3e-9 is not something
        /// to make a user read. The unit is stated in the trace's own label ("Group Delay (ns)"), so
        /// nothing has to infer it.
        /// </remarks>
        private double[] DerivedScalarArray(Complex[] z0PerPort)
        {
            if (Derived == DerivedParameters.Passivity)
                return PassivityWholeNetwork
                    ? RfCore.Data.NetworkMetrics.PassivityFull(Data.Matrices, z0PerPort)
                    : RfCore.Data.NetworkMetrics.PassivityPair(
                          Data.Matrices, z0PerPort, InputPort, OutputPort);

            if (Derived == DerivedParameters.GroupDelay)
            {
                var tau = RfCore.Data.NetworkMetrics.GroupDelay(
                    Data.Matrices, z0PerPort, Data.Frequencies, InputPort, OutputPort);
                for (int i = 0; i < tau.Length; i++) tau[i] *= 1e9;
                return tau;
            }

            // Max Gain is the one metric with two display forms. Both come from RFNetwork, chosen
            // here rather than by the UI taking 10^(dB/10) of the other, so there is still exactly
            // one implementation of MAG/MSG (R-stb-1).
            var metric = Derived == DerivedParameters.MaxGain && !MaxGainIsLog
                ? RfCore.Data.NetworkMetric.MaxGainLinear
                : Derived.ToNetworkMetric();

            return RfCore.Data.NetworkMetrics.TwoPortMetric(
                Data.Matrices, z0PerPort, metric, InputPort, OutputPort);
        }

        private double[] GetDerivedMetricArray()
        {
            if (_derivedMetricCache != null
                && _derivedMetricCacheDerived == Derived
                && _derivedMetricCacheInPort == InputPort
                && _derivedMetricCacheOutPort == OutputPort
                && _derivedMetricCachePassivityScope == PassivityWholeNetwork
                && _derivedMetricCacheMaxGainLog == MaxGainIsLog
                && ReferenceEquals(_derivedMetricCacheMats, Data.Matrices))
            {
                return _derivedMetricCache;
            }

            int nPorts = Data.Ports;
            Complex[] z0PerPort = SourceZ0PerPortResolved(nPorts);

            double[] values = DerivedScalarArray(z0PerPort);

            _derivedMetricCache = values;
            _derivedMetricCacheDerived = Derived;
            _derivedMetricCacheInPort = InputPort;
            _derivedMetricCacheOutPort = OutputPort;
            _derivedMetricCachePassivityScope = PassivityWholeNetwork;
            _derivedMetricCacheMaxGainLog = MaxGainIsLog;
            _derivedMetricCacheMats = Data.Matrices;
            return values;
        }

        public Complex DataPoint(double freq, Complex? z0Override = null)
        {
            if (IsCubeBound) return new Complex(double.NaN, double.NaN);
            int fi = Array.FindIndex(Data.Frequencies, f => f == freq);
            if (fi < 0) return new Complex(double.NaN, double.NaN);

            if (IsDerived)
            {
                double[] metricValues;
                try
                {
                    metricValues = GetDerivedMetricArray();
                }
                catch (ArgumentException)
                {
                    return new Complex(double.NaN, double.NaN);   // bad port pair → NaN, never a crash
                }
                double v = fi < metricValues.Length ? metricValues[fi] : double.NaN;
                return new Complex(v, 0);
            }

            // Per-port (unusual) path: renormalize S to the trace's (or override's) Z0 before
            // extracting — §2, and only when Override is on. Z/Y are reference-independent, unchanged.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                var mat = Data.Matrices[fi];
                if (MatrixType == MatrixType.Z)
                    mat = RFNetwork.SToZ(mat, sourceZ0);
                else if (MatrixType == MatrixType.Y)
                    mat = RFNetwork.SToY(mat, sourceZ0);
                else if (Z0OverrideEnabled || z0Override is not null)
                    mat = RFNetwork.SToS(mat, sourceZ0, RFNetwork.Z0Array(z0Override ?? _z0, Data.Ports));
                return mat[Row, Col];
            }

            // Uniform/legacy path (unchanged).
            var z0  = z0Override ?? _z0;
            var matU = Data.Matrices[fi];

            if (MatrixType == MatrixType.S && (Z0OverrideEnabled || z0Override is not null) && z0 != Data.Z0)
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
            // A derived metric's DataPoint is already the final displayed number (matching
            // BuildDerivedPath, which plots the NetworkMetrics value with no further transform) —
            // e.g. MaxGain is already dB from RFNetwork.MaxGain. Re-running it through the YAxis
            // switch below would double-apply Db for MaxGain (whose YAxis defaults to Db).
            if (IsDerived) return d.Real;
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

                if (YAxisIsComplexValue)
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
            if (!IsStabilityCircle || freqIndex < 0 || freqIndex >= StabilityCircleCentres.Count)
                return null;
            var   C  = StabilityCircleCentres[freqIndex];
            float r  = MathF.Abs((float)StabilityCircleRadii[freqIndex]);
            float dx = queryWorld.X - C.X;
            float dy = queryWorld.Y - C.Y;
            float dc = MathF.Sqrt(dx * dx + dy * dy);
            // Query exactly at the centre — every perimeter point is equidistant, so "nearest" is
            // undefined. Pick the +real one rather than returning null and leaving the marker
            // wherever it happened to be (which, at the centre, is off the locus entirely).
            if (dc < 1e-9f) return new Vector2(C.X + r, C.Y);
            return new Vector2(C.X + dx / dc * r, C.Y + dy / dc * r);
        }

        /// <summary>Index of the frequency sample NEAREST <paramref name="freq"/>; −1 when the trace
        /// has no frequencies. Never an exact-equality match — a frequency that arrives from another
        /// trace mode (or from a cube axis) is not guaranteed to be bit-identical to a sample.</summary>
        private int NearestFrequencyIndex(double freq)
        {
            var f = Data.Frequencies;
            if (f is not { Length: > 0 }) return -1;
            int best = 0;
            double bestD = Math.Abs(f[0] - freq);
            for (int i = 1; i < f.Length; i++)
            {
                double d = Math.Abs(f[i] - freq);
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Writes every marker's frequency into <see cref="Marker.Freq"/>, resolved from whatever
        /// representation the trace currently uses — so the value survives a change of trace mode.
        /// <para>A CUBE-bound marker carries its frequency <b>implicitly, in its position</b>:
        /// <c>PlotControl.SnapMarkerToTrace</c> deliberately does not assign <c>Freq</c> for a cube
        /// trace (<c>CubeMarkerIndex</c> re-derives the sample from the position on every read), and
        /// markers are constructed with <c>freq: 0.0</c>. So a marker that has only ever lived on a
        /// cube trace has <c>Freq == 0</c> — invisible there, but it becomes the displayed frequency
        /// the moment the trace turns into a network/derived one, which is the "marker jumps to
        /// 0 Hz and reads NaN" report.</para>
        /// <para>Call this BEFORE clearing the cube binding — it reads the cube X values, which
        /// <see cref="SetCubeData"/> will replace.</para>
        /// </summary>
        public void CaptureMarkerFrequencies()
        {
            if (!IsCubeBound || Markers.Count == 0) return;
            if (_cubeXValues is not { Length: > 0 } xs) return;
            // Only a frequency X axis can answer this; a Pin/harmonic sweep has no frequency to
            // carry over, and inventing one from a sweep index would be worse than leaving it.
            if (!string.Equals(_cubeXAxisName, "freq", StringComparison.OrdinalIgnoreCase)) return;

            foreach (var m in Markers)
            {
                int idx = CubeMarkerIndex(m);
                if (idx >= 0 && idx < xs.Length) m.Freq = xs[idx];
            }
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
            IReadOnlyList<Trace>? plotTraces = null)
        {
            var lines = new List<(string, bool)> { (m.MarkerString, true) };

            var pts = CubeMarkerPoints(m);
            string desc = ReadoutDescription(showFilePrefix);

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
            // A "plot versus" family reads its X from the MARKED CURVE — each curve carries its own
            // X there, so the trace-level array (curve 0's) would report the wrong value on any
            // other curve.
            double[] xSource = IsFamily && curve >= 0 && curve < FamilyCurves.Count
                               && FamilyCurves[curve].RawX is { Length: > 0 } curveX
                ? curveX
                : _cubeXValues;
            int rawIdx = xIdx < xSource.Length ? xIdx : xSource.Length - 1;
            if (rawIdx < 0) rawIdx = 0;
            double xRaw = xSource[rawIdx];
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

            if (MarkerShowsImpedance(m))
                lines.Add((GetMarkerImpedanceString(m), false));

            // Multi-marker rows: the same X sample read on every other trace in the plot.
            // Cube traces are keyed by X-index, not frequency, so this uses the cube path.
            // When the other trace's X axis is incompatible (different length), the value is NaN.
            if (m.IsMulti && plotTraces != null)
                foreach (var other in plotTraces)
                    if (!Equals(other)) lines.Add((GetMultiMarkerLine(m, other), false));

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
                return $"  Δ{other.ReadoutDescription(false)}={valStr}";
            }

            string val = compatible
                ? other.FormatCubeCell(xIdx, m.FormatString, m.MaximumFractionDigits)
                : "NaN";
            if (string.IsNullOrEmpty(val)) val = "NaN";
            return $"{other.ReadoutDescription(false)}={val}";
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
            string desc = ReadoutDescription(showFilePrefix);
            if (CubeXValues is null || CubeXValues.Count == 0 || Points.Count == 0) return $"{desc}=NaN";
            int    idx = FindStemIndex(m);
            string val = FormatCubeCell(idx, m.FormatString, m.MaximumFractionDigits);
            return $"{desc}={val}";
        }

        /// <summary>
        /// "metric=value unit" for THIS contour trace, evaluated at a marker's coordinate.
        /// One formatter for the marker's own row, for the sibling-contour rows the info box adds
        /// beside it, and for the editor popup — a second copy is a second chance for the box and
        /// the popup to disagree about the same point.
        /// </summary>
        internal string ContourMetricLine(Marker m, Complex coord)
        {
            if (ContourData is not { } cd) return "";
            double val    = cd.EvaluateMetric?.Invoke(coord, m.ContourSnapped) ?? double.NaN;
            string metric = string.IsNullOrEmpty(cd.MetricName) ? "value" : cd.MetricName;
            string fmt    = $"{m.FormatString}{m.MaximumFractionDigits}";
            string valStr = double.IsFinite(val) ? val.ToString(fmt) : "NaN";
            string unit   = string.IsNullOrEmpty(cd.MetricUnitString) ? "" : $" {cd.MetricUnitString}";
            return $"{metric}={valStr}{unit}";
        }

        /// <summary>
        /// The physical termination (Ω) a contour marker's coordinate stands for.
        /// <para>On the Γ plane the coordinate is a reflection coefficient against THIS trace's own
        /// <see cref="Z0"/> — the reference <c>RebuildContour</c> fits the surface in — so
        /// Z = Z0·(1+Γ)/(1−Γ), the loadpull surface's own convention. That is deliberately NOT the
        /// power-wave form <c>FormatImpedance</c> uses for S-parameter readouts: reporting a
        /// termination the fitted surface does not agree with would be worse than reporting none.</para>
        /// <para>On a Rect (Z-plane) contour the coordinate already IS the impedance.</para>
        /// <param name="gammaPlane">Overrides which plane the coordinate is in. Callers that know the
        /// PLOT type should pass it: <c>ContourData.GammaPlane</c> is set by the fit and falls back to
        /// false when a fit fails, which on a Smith plot would read a Γ out as if it were ohms.</param>
        /// </summary>
        public Complex ContourImpedance(Complex coord, bool? gammaPlane = null)
        {
            if (ContourData is not { } cd) return coord;
            if (!(gammaPlane ?? cd.GammaPlane)) return coord;
            var z0 = Z0 == Complex.Zero ? new Complex(50, 0) : Z0;
            return RfHelpers.G2Z(coord) * z0;
        }

        /// <summary>The marker value line for the compact editor readout, by kind.</summary>
        public string GetEditorDataLine(Marker m, bool showFilePrefix)
        {
            if (IsContourTrace)
                return ContourMetricLine(m, new Complex(m.PositionStatic.X, m.PositionStatic.Y));
            if (IsHarmonicStem) return GetStemValString(m, showFilePrefix);
            if (IsCubeXMarker)
            {
                string desc = ReadoutDescription(showFilePrefix);
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

            if (IsDerived)
            {
                // Same authority and z0PerPort resolution as DataPoint's derived branch —
                // extraction via NetworkMetrics, never the raw N-port matrix.
                double[] metricValues;
                try
                {
                    metricValues = GetDerivedMetricArray();
                }
                catch (ArgumentException)
                {
                    return new Complex(double.NaN, double.NaN);   // bad port pair → NaN, never a crash
                }
                double v = fi < metricValues.Length ? metricValues[fi] : double.NaN;
                return new Complex(v, 0);
            }

            var mat = Data.Matrices[fi];

            // Per-port (unusual) path: renormalize S to the trace's Z0 before extracting — §2, and
            // only when Override is on.
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0)
            {
                if (MatrixType == MatrixType.Z)
                    mat = RFNetwork.SToZ(mat, sourceZ0);
                else if (MatrixType == MatrixType.Y)
                    mat = RFNetwork.SToY(mat, sourceZ0);
                else if (Z0OverrideEnabled)
                    mat = RFNetwork.SToS(mat, sourceZ0, RFNetwork.Z0Array(Z0, Data.Ports));

                return mat[Row, Col];
            }

            // Uniform/legacy path (unchanged apart from the Override gate: with Override off the
            // target reference IS the source's, so a type conversion still happens but no renorm).
            var targetZ0 = Z0OverrideEnabled ? Z0 : Data.Z0;
            if (Data.Type != MatrixType || targetZ0 != Data.Z0)
                mat = RFNetwork.Convert(mat, Data.Type, Data.Z0, MatrixType, targetZ0);

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
                // Expression-baked real value: transform is already in the expression — show as-is.
                double y = _transformBaked ? v : Transform switch
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
            string desc   = ReadoutDescription(showFilePrefix);

            if (YAxisIsComplexValue)
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
                return $"  Δ{other.ReadoutDescription(false)}={valStr}";
            }

            if (other.YAxisIsComplexValue)
            {
                var    dp     = other.GetMarkerDataPoint(m);
                string valStr = double.IsNaN(dp.Real) ? "NaN" : m.FormatComplex(dp);
                return $"{other.ReadoutDescription(false)}={valStr}";
            }

            double scalar = other.DataPointScalar(m.Freq);
            return $"{other.ReadoutDescription(false)}={other.FormatScalarValue(scalar, m)}";
        }

        public string MuString(Marker m)
        {
            if (IsCubeBound) return "";
            string fmt = $"{m.FormatString.ToString()}{m.MaximumFractionDigits}";
            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "Stability=NaN";

            RfCore.Data.NetworkMetric? metric = Derived switch
            {
                DerivedParameters.LoadStabilityCircle or DerivedParameters.Mu      => RfCore.Data.NetworkMetric.Mu,
                DerivedParameters.SourceStabilityCircle or DerivedParameters.MuPrime => RfCore.Data.NetworkMetric.MuPrime,
                _ => null,
            };
            if (metric is null) return "";
            string label = metric == RfCore.Data.NetworkMetric.Mu
                ? "Load Stability, µ=" : "Source Stability, µ'=";

            // Extract the ordered 2-port via NetworkMetrics — same authority and z0PerPort
            // resolution as the plotted path — rather than calling RFNetwork on the raw N-port SNP.
            int nPorts = Data.Ports;
            Complex[] z0PerPort = SourceZ0PerPortResolved(nPorts);
            try
            {
                double v = RfCore.Data.NetworkMetrics.TwoPortMetric(
                    Data.Matrices, z0PerPort, metric.Value, InputPort, OutputPort)[fi];
                return label + v.ToString(fmt);
            }
            catch (ArgumentException)
            {
                return label + "NaN";   // bad port pair → NaN, never a crash
            }
        }

        public bool MarkerShowsImpedance(Marker m) =>
            !m.IsMulti && (IsCubeBound
                ? IsCubeReflectionElement
                : Row == Col && YAxisIsComplexValue);

        /// <summary>Shared impedance formula — Γ (or S(i,i)) at a reference Z0, both the plain and
        /// normalized forms. One formatter for the network and cube-bound paths (brief-dd-z0-
        /// renormalization.md §5: "do not add a second impedance formatter").</summary>
        private static string FormatImpedance(Complex s, Complex z0, Marker m)
        {
            var Z  = z0 * (z0.Conjugate() / z0 + s) / (Complex.One - s);
            var Zn = Z / z0;
            return m.UseNormalizedImpedance
                ? $"impedance=Z0*({m.FormatComplex(Zn)})"
                : $"impedance={m.FormatComplex(Z)} Ω";
        }

        /// <summary>The 0-based port index a cube-bound reflection element (S(i,i)) reads; -1 when
        /// the trace is not one. The pinned "i" slice index IS the port index (the axis is 1-based
        /// port numbers, the slice holds the 0-based position).</summary>
        private int CubeReflectionPortIndex
        {
            get
            {
                if (!IsCubeReflectionElement || Slice is null) return -1;
                foreach (var s in Slice)
                    if (s.AxisName == "i") return s.Index;
                return -1;
            }
        }

        /// <summary>The reference impedance a marker readout must report against — the port's OWN
        /// reference with Override off (the data was never renormalized, so reporting it against a
        /// uniform Z0 would state the wrong impedance), the user's uniform Z0 with Override on.
        /// Either way this is "the impedance looking into the port with every other port terminated
        /// in the reference the displayed S is normalized to".</summary>
        private Complex MarkerReferenceZ0(int portIndex)
        {
            // A derived Γ-plane locus is not an S-element at a port — it lives in the uniform real
            // reference TwoPortUniformReal put it in, whatever the Z0 box or the per-port array say.
            if (IsDerived) return DerivedGammaReferenceZ0;
            if (Z0OverrideEnabled) return _z0;
            if (SourceZ0PerPort is { } src && portIndex >= 0 && portIndex < src.Length)
                return src[portIndex];
            return IsCubeBound ? _z0 : Data.Z0;
        }

        /// <summary>The reference impedance every marker readout on this trace is against — the
        /// port's own reference with Override off, the user's uniform <see cref="Z0"/> with it on.
        /// This is what the info box's "Z0=" line reports and what the VSWR locus is drawn in, so
        /// an S(2,2) trace on a 12 Ω port reads 12 Ω rather than the port-1 value <see cref="Z0"/>
        /// happens to mirror.</summary>
        public Complex MarkerZ0 => MarkerReferenceZ0(IsCubeBound ? CubeReflectionPortIndex : Row);

        public string GetMarkerImpedanceString(Marker m)
        {
            if (IsCubeBound)
            {
                if (!IsCubeReflectionElement || _cubeComplexValues is null) return "";
                int xIdx = _lastPlotType == PlotType.Table ? NearestCubeXIndex(m.Freq) : CubeMarkerIndex(m);
                if (xIdx < 0 || xIdx >= _cubeComplexValues.Length) return "impedance=NaN";
                // §1 already renormalized _cubeComplexValues upstream when Override is ON
                // (PlotInspectorViewModel.ResolveNetworkParamCube); with Override off they are the
                // source's own values, so the readout must use that port's own reference.
                return FormatImpedance(_cubeComplexValues[xIdx], MarkerReferenceZ0(CubeReflectionPortIndex), m);
            }

            int fi = Array.FindIndex(Data.Frequencies, f => f == m.Freq);
            if (fi < 0) return "impedance=NaN";

            // Derived (stability-circle) traces FIRST — the marker sits on a Γ-plane locus, so its
            // impedance comes from the marker POSITION at the locus's own uniform-real reference,
            // never from an S-matrix element. Both branches below read S[Row, Col] (= S11, since
            // Derived forces Row = Col = 0), which for a circle marker is an unrelated number that
            // does not even move when the marker does. The per-port branch in particular used to
            // swallow every derived trace on a non-uniform source.
            if (IsDerived)
                return FormatImpedance(new Complex(m.PositionStatic.X, m.PositionStatic.Y),
                                       DerivedGammaReferenceZ0, m);

            // Per-port (unusual) path: with Override ON, renormalize to the trace's Z0 before
            // reading out — §2. With Override off nothing is renormalized and the readout is against
            // the port's own reference (sourceZ0[Row]).
            if (SourceZ0IsUnusual && SourceZ0PerPort is { } sourceZ0 && Row < sourceZ0.Length)
            {
                if (!Z0OverrideEnabled)
                    return FormatImpedance(Data.Matrices[fi][Row, Col], sourceZ0[Row], m);
                Complex s = RFNetwork.SToS(Data.Matrices[fi], sourceZ0, RFNetwork.Z0Array(Z0, Data.Ports))[Row, Col];
                return FormatImpedance(s, Z0, m);
            }

            // Uniform/legacy path (unchanged apart from the Override gate). The derived case is
            // handled above, so the element read is unconditional here.
            var refZ0 = MarkerReferenceZ0(Row);
            Mat<Complex> temp = RFNetwork.Convert(Data.Matrices[fi], Data.Type, Data.Z0, MatrixType, refZ0);
            return FormatImpedance(temp[Row, Col], refZ0, m);
        }

        /// <param name="plotTraces">Every trace in the plot the info box belongs to, in the order they
        /// were placed — including the marker's own. Multi-markers read the others; a CONTOUR marker
        /// reads all of them, because comparing power against efficiency at one termination is the
        /// whole point of the readout. Null when the caller has no plot context (tests, design time),
        /// in which case only this trace is reported.</param>
        public List<(string Text, bool Bold)> BuildMarkerBoxLines(Marker m, FreqUnit freqUnit,
            bool showFilePrefix = true, IReadOnlyList<Trace>? plotTraces = null)
        {
            if (IsContourTrace && ContourData is { } cd)
            {
                var lines = new List<(string, bool)> { (m.MarkerString, true) };

                var coord = new Complex(m.PositionStatic.X, m.PositionStatic.Y);

                // One row per contour trace in the plot, in placement order: a loadpull marker is
                // placed to ask "what do I get at THIS termination", and the answer is every plotted
                // metric at once, not just the trace that happens to own the marker. Every contour in
                // one plot is fitted in that plot's own plane (Γ on Smith/Polar, Z on Rect), so the
                // marker's single coordinate evaluates all of their surfaces.
                var contours = plotTraces is null
                    ? new List<Trace>()
                    : plotTraces.Where(t => t.IsContourTrace).ToList();
                if (!contours.Contains(this)) contours.Insert(0, this);
                foreach (var ct in contours)
                    lines.Add((ct.ContourMetricLine(m, coord), false));

                // Impedance is always reported — the termination in ohms is what leaves the plot and
                // goes into a matching network. Γ follows it only on a Γ plane (Smith/Polar), where it
                // is a second reading of the same point; on a Rect contour the coordinate IS the
                // impedance and a Γ row would just repeat the row above.
                lines.Add(($"Z={m.FormatImpedanceComplex(ContourImpedance(coord))} Ω", false));
                if (cd.GammaPlane)
                    lines.Add(($"Γ={m.FormatComplex(coord)}", false));
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
                return BuildCubeMarkerBoxLines(m, freqUnit, showFilePrefix, plotTraces);

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

            if (m.IsMulti && plotTraces != null)
                foreach (var other in plotTraces)
                    if (!Equals(other)) standardLines.Add((GetMultiMarkerLine(m, other), false));

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
