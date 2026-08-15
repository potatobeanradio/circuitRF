using System.Linq;
using System.Numerics;

namespace CircuitRF.Harmonica;

/// <summary>Which of the two termination planes a quantity belongs to.</summary>
public enum TerminationSide { Source = 0, Load = 1 }

/// <summary>How the DUT's behaviour is supplied.</summary>
public enum DutKind
{
    /// <summary>One of the five native large-signal FET models on <c>FetModelBase</c>.</summary>
    NativeFet,
    /// <summary>An SDD carrying user-entered drain- (and gate-) current equations.</summary>
    Sdd,
    /// <summary>A compiled Verilog-A <c>.osdi</c> or a vendor-kit part, through a device worker.</summary>
    External,
    /// <summary>A two-terminal built-in, for teaching and for the degenerate cases.</summary>
    Diode,
}

/// <summary>
/// The DUT (harmonicarf.md §4.3). Exactly one, source always grounded.
///
/// <para><b>The source is grounded at the PACKAGE plane, not at the intrinsic one</b>, which is why
/// <c>Ls</c>/<c>Rs</c> in <see cref="LumpedPackage"/> are shared between the input and output loops
/// and therefore appear in the source-side intrinsic impedance (§4.5.3(a)).</para>
/// </summary>
public sealed record DutSpec
{
    public required DutKind Kind { get; init; }

    /// <summary>
    /// For <see cref="DutKind.NativeFet"/>: the component-type name (<c>FET_Angelov</c>,
    /// <c>FET_Curtice</c>, <c>FET_CurticeCubic</c>, <c>FET_Materka</c>, <c>FET_Statz</c>).
    /// For <see cref="DutKind.External"/>: the provider's type id.
    /// For <see cref="DutKind.Diode"/>: <c>Diode</c>.
    /// </summary>
    public string TypeName { get; init; } = "FET_Angelov";

    /// <summary>Provider name for <see cref="DutKind.External"/>; ignored otherwise.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// R-h9c-11 (R1C §6) — SDD2 vs SDD3, ignored for every other <see cref="Kind"/>. 2 (the default)
    /// is the existing gate/drain-vs-source pair convention (<c>_v1</c> = Vgs, <c>_v2</c> = Vds); 3
    /// adds a THIRD port pair, the source terminal against ground (<c>_v3</c> = Vs), so an equation
    /// can reference the source terminal's own current or voltage directly rather than only through
    /// the other two ports' shared reference. Both are still exactly the two-port intrinsic-plane
    /// case (§4.5): the gate and drain ports are unchanged, so <c>IntrinsicPortMap.TwoPort</c> is
    /// correct for either.
    /// </summary>
    public int SddPortCount { get; init; } = 2;

    /// <summary>
    /// The model's own parameters, verbatim. For an SDD these are the equation strings keyed
    /// <c>I[1,0]</c>, <c>I[2,0]</c>, … exactly as a <c>.cnl</c> spells them; for everything else the
    /// names the model declares. Values are written into the netlist as-is.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// R7B §3.4 — the SDD editor's verbatim text (variables, equations, comments, blank lines, in the
    /// order the user wrote them). The AUTHORITATIVE user-facing form; <see cref="Parameters"/> is
    /// DERIVED from it by <c>HarmonicaDutEditor.Build</c> whenever <see cref="Kind"/> is
    /// <see cref="DutKind.Sdd"/>. Null for every non-SDD kind, and for an SDD loaded from a
    /// <c>.charm</c> written before this field existed — <c>HarmonicaDutEditor</c>'s own constructor
    /// reconstructs something sensible to show in that case (<c>SddTextIo.Reconstruct</c>), rather
    /// than <c>CharmIo</c> writing a reconstruction back into a document nobody touched.
    /// </summary>
    public string? SddText { get; init; }

    /// <summary>Device multiplier — <c>m</c> in the netlist. 1 for a single device.</summary>
    public double Multiplicity { get; init; } = 1.0;

    /// <summary>
    /// For an external model, which declared node is the intrinsic drain / gate and which external
    /// pin is the source (§4.5.5). Null until the user has answered — the intrinsic panels must be
    /// drawn empty rather than with a plausible-looking wrong answer, so this is deliberately not
    /// defaulted.
    /// </summary>
    public IntrinsicMapping? IntrinsicMapping { get; init; }

    /// <summary>
    /// R7D — Cgs/Cdg/Cds across the DUT's own terminals, SDD only. A property of THIS device (hence
    /// hung off <see cref="DutSpec"/> rather than <see cref="LumpedPackage"/>, which is the extrinsic
    /// network outside it) and the reason the SDD-only rule reads naturally here. Default
    /// <see cref="DutCapacitances.None"/>, so every existing construction site keeps compiling and
    /// every existing document is unchanged.
    /// </summary>
    public DutCapacitances Capacitances { get; init; } = DutCapacitances.None;
}

/// <summary>
/// Names the intrinsic plane inside an external model. Nothing can guess which internal node is the
/// intrinsic drain, so this is user-supplied and persisted per model type (§4.5.5).
/// </summary>
public sealed record IntrinsicMapping(string GateNode, string DrainNode, string SourcePin);

/// <summary>R7D — one of the three DUT capacitances. Absent, linear, or a 1-D polynomial C(V).</summary>
public sealed record DutCapacitance
{
    /// <summary>Farads. Used when <see cref="Coefficients"/> is null. Zero means "no capacitor at all"
    /// — nothing is emitted into the netlist.</summary>
    public double Farads { get; init; }

    /// <summary>C0…Cn of C(V) = Σ Cₖ·Vᵏ, in raw SI (F, F/V, F/V², …) — the SAME spelling and the same
    /// units NonlinearC's own C0/C1/… parameters use. Null for a linear capacitor.</summary>
    public IReadOnlyList<double>? Coefficients { get; init; }

    public bool IsNonlinear => Coefficients is { Count: > 0 };
    public bool IsAbsent    => !IsNonlinear && Farads == 0.0;

    public static readonly DutCapacitance None = new();

    /// <summary>R7D §2.2 — this capacitor's own contribution to <see cref="CircuitModel.StructuralKey"/>,
    /// stable and readable (<c>"Cgs=1.2E-12"</c> / <c>"Cgs=[1.2E-12,3E-14]"</c>), via
    /// <see cref="HarmonicaNetlist.Num"/> so it never picks up a culture separator.</summary>
    public string StructuralKeyPart(string name) => IsNonlinear
        ? $"{name}=[{string.Join(",", Coefficients!.Select(HarmonicaNetlist.Num))}]"
        : $"{name}={HarmonicaNetlist.Num(Farads)}";
}

/// <summary>R7D — the DUT's three parasitic capacitances (Cgs/Cdg/Cds), SDD only (§1).</summary>
public sealed record DutCapacitances
{
    public DutCapacitance Cgs { get; init; } = DutCapacitance.None;
    public DutCapacitance Cdg { get; init; } = DutCapacitance.None;
    public DutCapacitance Cds { get; init; } = DutCapacitance.None;

    /// <summary>R8C §3 — ohms, in SERIES with Cgs between the gate terminal and the source
    /// terminal. 0 (the default) emits nothing at all, so an existing document is bit-identical.</summary>
    public double RgsOhms { get; init; }

    public static readonly DutCapacitances None = new();

    // R8C §3.1 — deliberately does NOT consult RgsOhms: a non-zero rgs with an absent Cgs is an open
    // branch and emits nothing (HarmonicaNetlist's own Cgs/rgs block), so it must not count as "not
    // the identity" — a document with rgs set but no Cgs is still structurally untouched.
    public bool IsIdentity => Cgs.IsAbsent && Cdg.IsAbsent && Cds.IsAbsent;
}

/// <summary>Which of the three DUT capacitances a quantity is about — used by the readout strip's
/// linearized-value computation (R7D §3.3) to say which port pair's bias voltage to read.</summary>
public enum DutCapacitanceKind { Cgs, Cdg, Cds }

/// <summary>
/// The canonical, fixed-topology extrinsic network (§4.1). Any value may be zero. It is deliberately
/// NOT an arbitrary sub-schematic — an arbitrary network is what circuitRF itself is for.
/// </summary>
public sealed record LumpedPackage
{
    public double Rg { get; init; }
    public double Lg { get; init; }
    public double Rd { get; init; }
    public double Ld { get; init; }
    public double Rs { get; init; }
    public double Ls { get; init; }
    public double Cpg { get; init; }
    public double Cpd { get; init; }
    public double CgdExt { get; init; }

    public static readonly LumpedPackage None = new();

    public bool IsIdentity =>
        Rg == 0 && Lg == 0 && Rd == 0 && Ld == 0 && Rs == 0 && Ls == 0 &&
        Cpg == 0 && Cpd == 0 && CgdExt == 0;

    /// <summary>
    /// Whether any element couples the input and output loops — a shared source lead, or an external
    /// gate-drain feedback capacitance. This is exactly the condition under which
    /// <c>Z_S,intr</c> departs from the passive source network (§4.5.3(a)), so Tier 1's fixture is
    /// defined by it rather than by "no package".
    /// </summary>
    public bool CouplesInputAndOutput => Rs != 0 || Ls != 0 || CgdExt != 0;
}

/// <summary>
/// The embedding stack (§4.1). Cascade order is fixed, outside in:
/// <c>s2p → s4p/s6p → lumped → DUT</c>. Every element is optional.
/// </summary>
public sealed record EmbeddingStack
{
    /// <summary>Two-port at the input, port 1 facing the tuner and port 2 facing inward.</summary>
    public string? S2pInFile { get; init; }

    /// <summary>Two-port at the output, port 1 facing the tuner and port 2 facing inward.</summary>
    public string? S2pOutFile { get; init; }

    /// <summary>
    /// One block embedding the whole DUT. Ports 1,2 face outward; 3,4 (s4p) face the DUT.
    /// </summary>
    public string? S4pFile { get; init; }

    public LumpedPackage Package { get; init; } = LumpedPackage.None;

    public static readonly EmbeddingStack None = new();

    public bool HasTouchstone => S2pInFile is not null || S2pOutFile is not null || S4pFile is not null;

    public IEnumerable<string> TouchstoneFiles
    {
        get
        {
            if (S2pInFile  is not null) yield return S2pInFile;
            if (S2pOutFile is not null) yield return S2pOutFile;
            if (S4pFile    is not null) yield return S4pFile;
        }
    }
}

/// <summary>
/// The two termination planes' per-band impedances (§4.2). Band 0 is DC and is never a marker;
/// bands 1…K carry the markers.
///
/// <para><b>A band with no marker is 1e-6 Ω</b> — ohms, a near-short — for every band from 2 to K
/// (D9). Bands 1 (source and load) are always present.</para>
/// </summary>
public sealed class TerminationSet
{
    /// <summary>The value an unmarked band 2…K is terminated at (D9). Ohms, near-short.</summary>
    public const double UnmarkedBandOhms = 1e-6;

    private readonly Dictionary<int, Complex>[] _z =
        [new Dictionary<int, Complex>(), new Dictionary<int, Complex>()];

    public TerminationSet(int harmonicCount)
    {
        HarmonicCount = harmonicCount;
        // S1 and L1 are always present; the rest default to the near-short until marked.
        Set(TerminationSide.Source, 1, new Complex(50, 0));
        Set(TerminationSide.Load,   1, new Complex(50, 0));
    }

    public int HarmonicCount { get; }

    /// <summary>The bands that carry a marker, on one side. Band 1 is always among them.</summary>
    public IReadOnlyCollection<int> MarkedBands(TerminationSide side) => _z[(int)side].Keys;

    public void Set(TerminationSide side, int band, Complex z)
    {
        if (band < 1 || band > HarmonicCount)
            throw new ArgumentOutOfRangeException(nameof(band),
                $"band {band} is outside 1…{HarmonicCount}");
        _z[(int)side][band] = z;
    }

    /// <summary>Removes a marker. Band 1 cannot be removed — it is the fundamental.</summary>
    public void Remove(TerminationSide side, int band)
    {
        if (band == 1)
            throw new InvalidOperationException("the fundamental termination is always present");
        _z[(int)side].Remove(band);
    }

    /// <summary>
    /// The impedance presented at band <paramref name="band"/> — the marker's value if there is one,
    /// otherwise the unmarked near-short.
    /// </summary>
    public Complex Z(TerminationSide side, int band)
        => _z[(int)side].TryGetValue(band, out var z) ? z : new Complex(UnmarkedBandOhms, 0);

    public bool IsMarked(TerminationSide side, int band) => _z[(int)side].ContainsKey(band);

    public TerminationSet Clone()
    {
        var copy = new TerminationSet(HarmonicCount);
        for (int s = 0; s < 2; s++)
        {
            copy._z[s].Clear();
            foreach (var (k, v) in _z[s]) copy._z[s][k] = v;
        }
        return copy;
    }
}

/// <summary>
/// Ideal bias (§4.4): a perfect choke and a perfect DC block. The RF terminations never see DC and
/// band 0 is a hard short to the supply.
///
/// <para>Entry is either <see cref="Vgs"/> directly or <see cref="Idq"/>, in which case Vgs is
/// solved by a 1-D secant on the DC solve at the stated Vds.</para>
/// </summary>
public sealed record BiasSpec
{
    public double  Vds { get; init; } = 28.0;
    public double? Vgs { get; init; } = -1.5;
    /// <summary>Quiescent drain current, amps. When set, <see cref="Vgs"/> is solved for it.</summary>
    public double? Idq { get; init; }

    public bool IsCurrentBias => Idq.HasValue;
}

/// <summary>Solver knobs that persist with the <c>.charm</c> (§8.2).</summary>
public sealed record HarmonicaSettings
{
    /// <summary>Harmonic order. D8: the default is 5.</summary>
    public int    HarmonicCount { get; init; } = 5;
    public double FrequencyHz   { get; init; } = 2e9;
    public int    FftOverSample { get; init; } = 1;
    public double Tol           { get; init; } = 1e-8;
    public int    MaxIter       { get; init; } = 100;
    public int    GuardHarmonic { get; init; }
    public double Lambda        { get; init; } = 1.0;

    /// <summary>Compression target in dB, the level the contour grid is taken at.</summary>
    public double CompressionDb { get; init; } = 3.0;

    /// <summary>
    /// R-h9b-6 — the Smith-chart Γ-normalisation reference impedance, Ω. Default 50, matching the
    /// hardcoded value every chart used before this setting existed.
    ///
    /// <para><b>Not structural.</b> Γ is a display and grid parameterisation; the terminations the
    /// engine reads are impedances, so a Z₀ change moves no circuit and must NOT touch
    /// <see cref="CircuitModel.StructuralKey"/> (that would rebuild the context and reset the frame
    /// ladder for a value nobody asked to re-solve for). It DOES change the Γ grid, so a change still
    /// re-solves through the ordinary value-input path.</para>
    /// </summary>
    /// <summary>brief-harmonicarf-r6a §5.2 — owner request: default raised from 50 to 80 Ω, matching
    /// the current DUT's R_opt. Default only — an existing <c>.charm</c> carries its own Z0 and opens
    /// unchanged (see <c>CharmIo</c>'s own absent-means-default rule).</summary>
    public double Z0 { get; init; } = 80.0;

    // ── R-h9b-12 — the DCIV Sweeps dialog's override of DcivFamily.DefaultKey ────────────────
    //
    // All six or none: the dialog writes them together (DcivFamily.OverrideOf treats a partially-set
    // group as absent), so a half-written override can never silently take effect. DrainPort is NOT
    // here — R-h9b-12 says the dialog must not offer it, and DefaultKey's own DrainPort: 1 is what a
    // resolved override still uses.
    public double? DcivVgsMin   { get; init; }
    public double? DcivVgsMax   { get; init; }
    public int?    DcivVgsSteps { get; init; }
    public double? DcivVdsMin   { get; init; }
    public double? DcivVdsMax   { get; init; }
    public int?    DcivVdsSteps { get; init; }

    /// <summary>
    /// R-h9b-13 — how many time samples the loadline is drawn at. A DISPLAY resolution, not a solve
    /// parameter: the spectrum carries every harmonic 0…K, so re-evaluating it at any sample count is
    /// exact rather than interpolated (§7.3), and this never changes <see cref="FftOverSample"/> or
    /// the HB solve's own time grid. Default 64, per the owner's "try 64 for now".
    /// </summary>
    public int LoadlineSamples { get; init; } = 64;

    /// <summary>Clamp for <see cref="LoadlineSamples"/> — sane at both ends: below this a locus
    /// stops reading as a smooth loop, above it the per-frame device-evaluation cost (one
    /// <c>dut.Evaluate</c> per sample) buys nothing a user can see.</summary>
    public const int LoadlineSamplesMin = 8, LoadlineSamplesMax = 2048;

    /// <summary>
    /// R-h9r2-18 — the EXPLICIT power sweep's own Stop, dBm, AND (unchanged from before this brief)
    /// <c>PinSearch.Run</c>'s hard bracket ceiling. One number for both, deliberately: a document
    /// whose panel swept to a different ceiling than its grid's bracket would show a sweep reaching
    /// further than the grid ever searches, with no way for the user to see why. Default raised from
    /// 30 to 50 dBm (the owner's own "−10 dBm to 50 dBm") — the direct consequence is that grid points
    /// which used to report a <c>PinMax</c> hole at 30 dBm now keep searching to 50, so a document may
    /// show fewer holes at a higher solve cost than before this default moved.
    /// </summary>
    public double PinMaxDbm { get; init; } = 50.0;

    /// <summary>Where the Pin search starts, dBm — also the explicit sweep's own Start.</summary>
    public double PinStartDbm { get; init; } = -10.0;

    /// <summary>R-h9r2-18 — the explicit power sweep's own step, dB. <c>PinSearch.Run</c>'s bracket
    /// never reads this — its doubling strides are what keep a grid point to ~4.6 solves, and nothing
    /// about this setting may touch that.</summary>
    public double PinStepDbm { get; init; } = 1.0;

    /// <summary>R-h9r2-18's own hard ceiling on the explicit sweep's point count, refused by the Power
    /// Sweep dialog by name (never silently clamped) rather than a per-document setting.</summary>
    public const int MaxSweepPoints = 1001;

    /// <summary>
    /// R9C §3 — the Pin ladder step, in dB, that each CONTOUR GRID point's drive-up walks. Separate from
    /// <see cref="PinStepDbm"/> (the power-sweep PANEL's own step, default 1 dB) because the grid pays it
    /// once per Γ point: measured on the shipped default's 37-point grid, 1 dB is 1370 solves and 2 dB is
    /// 736, and the two agree to 0.03 dB in Pin and 0.002 dB in Pout.
    ///
    /// <para><b>Do not raise this past 3 dB.</b> Measured, not assumed: at 4 dB the same grid grew 2
    /// holes and at 6 dB more — a large Pin jump breaks the HB warm start, which is the identical
    /// mechanism that made PinSearch.Run's doubling stride fail (§0.2). Clamped on read for that reason.</para>
    /// </summary>
    public double ContourLadderStepDbm { get; init; } = 2.0;

    /// <summary>Clamp for <see cref="ContourLadderStepDbm"/> — see its own remarks for why the top end
    /// is not just a suggestion.</summary>
    public const double ContourLadderStepDbmMin = 0.5, ContourLadderStepDbmMax = 3.0;

    /// <summary>
    /// brief-harmonicarf-r4 §1 — R-h9r2-19 is superseded for the compression case: the explicit
    /// power sweep now stops once compression reaches <see cref="CompressionDb"/> +
    /// <see cref="SweepOverdriveDb"/>, rather than always running to <see cref="PinMaxDbm"/>. This
    /// margin, dB, is what is left to search PAST the target before the ladder stops — 0 satisfies the
    /// owner's literal instruction ("stop once compression exceeds the P-xdB setting") and is the
    /// default; a positive value keeps a few rungs of the saturation region (PAE typically peaks a few
    /// dB past P3dB) on the panel at the cost of a few extra solves. Never applies when the sweep does
    /// not cross the target at all — that path still runs the full range unchanged (R-h9r2-19's
    /// original guarantee, kept for the non-crossing case).
    /// </summary>
    public double SweepOverdriveDb { get; init; }

    /// <summary>
    /// R-h9r2-18a — whether the tickle (the small-signal reference every compression measurement is
    /// taken against) is solved at all. Default true. OFF means <c>gMax</c> seeds from the first
    /// solved sweep/grid point instead and <c>SmallSignalGainDb</c> is null rather than fabricated —
    /// see <c>PinSearch</c>'s own remarks. Read by BOTH drive-ups (<c>PinSearch.Run</c> and
    /// <c>PinSearch.Sweep</c>), or the panel's compression cursor and the contour grid's compression
    /// criterion would measure against two different references.
    /// </summary>
    public bool TickleEnabled { get; init; } = true;

    /// <summary>
    /// R-h9r2-18a — the tickle's own drive level, dBm available, as an ABSOLUTE figure replacing the
    /// old <c>PinStartDbm − 30 dB</c> relative offset. Default −50 dBm, the owner's own number (at the
    /// default Start of −10 dBm, the effective tickle moves from −40 to −50 dBm against the prior
    /// behaviour). Deliberately absolute: the tickle no longer follows <see cref="PinStartDbm"/> when
    /// Start moves, which is the whole point of naming a level rather than an offset. Must stay below
    /// <see cref="PinStartDbm"/> — validated by the Power Sweep dialog, never here.
    /// </summary>
    public double TickleDbm { get; init; } = -50.0;

    /// <summary>
    /// R9A §8 — on by default (was off, R-h9r2-17a). When true, the explicit power sweep
    /// (<c>PinSearch.Sweep</c>) takes ONE extra real HB solve at the interpolated compression Pin, and
    /// every figure at compression — scalar and spectrum alike — comes from that one solved state
    /// instead of the default interpolation. Never touches the contour grid's own
    /// <c>PinSearch.Run</c>, whose secant is already exact and needs no such option.
    /// </summary>
    public bool ExactCompressionSolve { get; init; } = true;     // R9A §8

    /// <summary>Whether the DUT's charge terms are evaluated — the strip's compute-charge toggle.</summary>
    public bool ComputeCharge { get; init; } = true;

    // ── brief-harmonicarf-r6a §3 — the contour surface's own RBF kernel knobs ──────────────

    /// <summary>Which <see cref="RfCore.Loadpull.RbfKernel"/> <c>ContourGrid.Fit</c> factorizes with.
    /// Default matches <c>Rbf2D</c>'s own default, so an untouched document behaves exactly as
    /// before this setting existed.</summary>
    public RfCore.Loadpull.RbfKernel ContourKernel { get; init; } = RfCore.Loadpull.RbfKernel.Multiquadric;

    /// <summary>The RBF smoothing term (scipy convention: subtracted from the kernel matrix diagonal).
    /// R8A §5 — 0.1, owner-set; was <c>Rbf2D</c>'s own default (1e-3).</summary>
    public double ContourSmooth { get; init; } = 0.1;

    /// <summary>
    /// The RBF shape parameter ε. R8A §5 — 0.5, owner-set; <c>ContourEpsilon</c> is no longer
    /// null-means-auto BY DEFAULT for harmonicaRF (it was, until this brief — see
    /// <c>HarmonicaAdvancedSettingsView</c>'s own epsilon box, whose blank-for-auto behaviour
    /// survives as an OPT-IN a user can still reach by clearing the box). <c>null</c> still means
    /// <c>Rbf2D</c>'s own scipy-style auto epsilon when it occurs.
    /// </summary>
    public double? ContourEpsilon { get; init; } = 0.5;

    /// <summary>
    /// The bias choke, henries. One henry is the ideal-bias value (§4.4) and the default.
    ///
    /// <para><b>It is a knob because ideal bias has a measured numerical cost, and the cost falls on
    /// the REFERENCE rather than on harmonicaRF.</b> One henry at 2 GHz is 12.6 GΩ, so a netlist that
    /// stamps the termination stamps a 12.6 GΩ reactance in parallel with tens of ohms — and forming
    /// that parallel combination numerically annihilates the small reactive part of the answer: the
    /// product term is ~1e22 and the term being added to it is ~50, eight decades below double
    /// precision's reach. harmonicaRF's own closure never forms that product (it adds admittances),
    /// so it keeps the term. Measured on the Tier 2 fixture: with an ideal choke the two routes agree
    /// to ~1e-4 and the CLOSURE is the accurate one; at 1 µH — still 12.6 kΩ, still an open next to
    /// any termination — they agree to ~1e-13.</para>
    ///
    /// <para>Nothing in the product needs to change this. It exists so a comparison against a stamped
    /// netlist can be made on a fixture where the stamped netlist is itself accurate.</para>
    /// </summary>
    public double BiasChokeHenries { get; init; } = 1.0;

    /// <summary>
    /// The DC block, farads. One farad is the ideal-bias value (§4.4) and the default.
    ///
    /// <para>Same story as <see cref="BiasChokeHenries"/>, from the other end: one farad at 2 GHz is
    /// 1.26e10 S, so a netlist that STAMPS the block puts that next to a termination's ~0.04 S and
    /// spends eleven digits on the condition number. harmonicaRF never stamps it — the block is part
    /// of the closed-form termination admittance — so its own answer is exact. Measured: the two
    /// routes agree to ~1e-5 with a 1 F block and to ~1e-13 with a 1 nF one, which at 2 GHz is still
    /// 0.08 Ω and still a short.</para>
    /// </summary>
    public double DcBlockFarads { get; init; } = 1.0;

    // ── brief-harmonicarf-r6e §2.1 — persisted axis limits + autoscale, one mechanism, three plots ──
    //
    // Absent (null) means "never set", never zero — the same convention DcivVgsMin already follows.
    // Autoscale defaults to FALSE on all three: "the axes are never changed while the user drags
    // markers" is the owner's own wording for why. §2.2/§2.3 (HarmonicaViewModel.CaptureAxisWindows)
    // is what keeps a stored limit in sync with what AutoScale would have computed, so the numbers
    // are never stale the moment autoscale is turned back on.

    /// <summary>The DCIV / loadline panel's own stored window.</summary>
    public double? DcivXMin { get; init; }
    public double? DcivXMax { get; init; }
    public double? DcivYMin { get; init; }
    public double? DcivYMax { get; init; }
    public bool    DcivAutoscale { get; init; }

    /// <summary>The power-sweep panel's own stored window — X, left Y (gain) and right Y (efficiency).</summary>
    public double? PowerSweepXMin { get; init; }
    public double? PowerSweepXMax { get; init; }
    public double? PowerSweepYMin { get; init; }
    public double? PowerSweepYMax { get; init; }
    public double? PowerSweepY2Min { get; init; }
    public double? PowerSweepY2Max { get; init; }
    public bool    PowerSweepAutoscale { get; init; }

    /// <summary>
    /// The SAME panel slot's Time Domain view (§4) — a DIFFERENT quantity (time / volts / amps rather
    /// than power / dB / %), so it gets its own stored window rather than sharing the power-sweep one;
    /// switching modes must not corrupt the other mode's axes.
    /// </summary>
    public double? TimeDomainXMin { get; init; }
    public double? TimeDomainXMax { get; init; }
    public double? TimeDomainYMin { get; init; }
    public double? TimeDomainYMax { get; init; }
    public double? TimeDomainY2Min { get; init; }
    public double? TimeDomainY2Max { get; init; }
    public bool    TimeDomainAutoscale { get; init; }
}

/// <summary>
/// Everything harmonicaRF solves, in one value object (§4). Split into the part whose change forces
/// a netlist rebuild and the part that does not — see <see cref="StructuralKey"/>.
/// </summary>
public sealed record CircuitModel
{
    public required DutSpec           Dut         { get; init; }
    public EmbeddingStack             Embedding   { get; init; } = EmbeddingStack.None;
    public BiasSpec                   Bias        { get; init; } = new();
    public HarmonicaSettings          Settings    { get; init; } = new();

    /// <summary>Drive level at which a single operating point is evaluated, dBm available.</summary>
    public double PavlDbm { get; init; } = 0.0;

    /// <summary>
    /// Everything that forces a netlist rebuild when it changes (R-hrf-5): the DUT, the embedding
    /// stack, the harmonic count and the frequency. A change to a TERMINATION, the drive or the bias
    /// is a value change and is applied by mutating models in place.
    ///
    /// <para>Bias is in here even though §6.1 calls it a value change: the supplies are ordinary
    /// <c>Vdc</c> instances whose value is resolved at elaboration, so moving one needs either a
    /// rebuild or a mutable handle. <see cref="HarmonicaContext"/> keeps the handle and mutates it,
    /// so bias is deliberately NOT part of this key.</para>
    /// </summary>
    public string StructuralKey => string.Join("|",
        Dut.Kind, Dut.TypeName, Dut.Provider ?? "", Dut.SddPortCount,
        string.Join(",", Dut.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                                       .Select(p => $"{p.Key}={p.Value}")),
        Dut.Multiplicity,
        Embedding.S2pInFile ?? "", Embedding.S2pOutFile ?? "", Embedding.S4pFile ?? "",
        Embedding.Package,
        Settings.HarmonicCount, Settings.FrequencyHz, Settings.FftOverSample,
        Settings.ComputeCharge,
        // R7D §2.2 — a capacitance is a netlist ELEMENT, not a value like Vgs; leaving it out would
        // mean editing one changes nothing until some unrelated structural edit happens to rebuild.
        Dut.Capacitances.Cgs.StructuralKeyPart("Cgs"),
        Dut.Capacitances.Cdg.StructuralKeyPart("Cdg"),
        Dut.Capacitances.Cds.StructuralKeyPart("Cds"),
        // R8C §3.1 — a netlist element (HarmonicaNetlist's own RGS resistor), same rule as the
        // capacitances above: leaving it out would mean editing rgs changes nothing until some
        // unrelated structural edit happens to rebuild.
        "Rgs=" + HarmonicaNetlist.Num(Dut.Capacitances.RgsOhms));

    /// <summary>R8C §5.2 — whether an intrinsic glyph may be dragged. True only when the intrinsic
    /// plane is separated from each terminal by a LINEAR, UNILATERAL two-port, which is exactly the
    /// condition under which <see cref="IntrinsicAbcd"/>'s ABCD inversion is exact rather than
    /// approximate.</summary>
    public static bool IntrinsicDragAllowed(CircuitModel m, out string reason)
    {
        ArgumentNullException.ThrowIfNull(m);

        if (m.Dut.Kind != DutKind.Sdd)
        {
            reason = "Intrinsic dragging needs an SDD DUT — a native FET carries gate charge inside " +
                     "its own CapModel and an external model carries parasitics we cannot see, so no " +
                     "ABCD chain can be written for either.";
            return false;
        }

        var caps = m.Dut.Capacitances;
        if (caps.Cgs.IsNonlinear || caps.Cdg.IsNonlinear || caps.Cds.IsNonlinear)
        {
            reason = "A nonlinear capacitor makes the embedding a conversion matrix, not a 2×2 ABCD " +
                     "— harmonics couple and a per-band inversion would be wrong, not merely inaccurate.";
            return false;
        }

        if (!caps.Cdg.IsAbsent)
        {
            reason = "Cdg is the DUT's own gate–drain feedback path; with it the input and output " +
                     "halves are one four-port and cannot be inverted side by side.";
            return false;
        }

        // R8C §5.2 — this predicate already exists, and is already documented as "exactly the
        // condition under which Z_S,intr departs from the passive source network": a shared source
        // lead (Rs/Ls) or an external gate-drain feedback cap (CgdExt) closes a path between the input
        // and output loops. Mutual inductance between input and output is not representable in this
        // model — LumpedPackage carries no coupling coefficient — and Ls (the shared source lead) is
        // the one representable input–output inductive path, already covered here.
        if (m.Embedding.Package.CouplesInputAndOutput)
        {
            reason = "The package couples the input and output loops (a shared source lead or an " +
                     "external gate-drain feedback capacitance), so the two sides cannot be inverted " +
                     "independently.";
            return false;
        }

        if (!m.Settings.ComputeCharge)
        {
            // A drag is a no-op with charge off (the glyph already coincides with its marker), not an
            // error — allowed, but the caller is told so it is not a surprise.
            reason = "Charge is off, so the intrinsic glyph already coincides with its marker — a " +
                     "drag will move nothing.";
            return true;
        }

        reason = "";
        return true;
    }
}
