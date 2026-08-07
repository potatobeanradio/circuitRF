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
    /// The model's own parameters, verbatim. For an SDD these are the equation strings keyed
    /// <c>I[1,0]</c>, <c>I[2,0]</c>, … exactly as a <c>.cnl</c> spells them; for everything else the
    /// names the model declares. Values are written into the netlist as-is.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Device multiplier — <c>m</c> in the netlist. 1 for a single device.</summary>
    public double Multiplicity { get; init; } = 1.0;

    /// <summary>
    /// For an external model, which declared node is the intrinsic drain / gate and which external
    /// pin is the source (§4.5.5). Null until the user has answered — the intrinsic panels must be
    /// drawn empty rather than with a plausible-looking wrong answer, so this is deliberately not
    /// defaulted.
    /// </summary>
    public IntrinsicMapping? IntrinsicMapping { get; init; }
}

/// <summary>
/// Names the intrinsic plane inside an external model. Nothing can guess which internal node is the
/// intrinsic drain, so this is user-supplied and persisted per model type (§4.5.5).
/// </summary>
public sealed record IntrinsicMapping(string GateNode, string DrainNode, string SourcePin);

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

    /// <summary>Available-power ceiling for the Pin search, dBm (R-hrf-7's hard stop).</summary>
    public double PinMaxDbm { get; init; } = 30.0;

    /// <summary>Where the Pin search starts, dBm.</summary>
    public double PinStartDbm { get; init; } = -10.0;

    /// <summary>Whether the DUT's charge terms are evaluated — the strip's compute-charge toggle.</summary>
    public bool ComputeCharge { get; init; } = true;

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
        Dut.Kind, Dut.TypeName, Dut.Provider ?? "",
        string.Join(",", Dut.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                                       .Select(p => $"{p.Key}={p.Value}")),
        Dut.Multiplicity,
        Embedding.S2pInFile ?? "", Embedding.S2pOutFile ?? "", Embedding.S4pFile ?? "",
        Embedding.Package,
        Settings.HarmonicCount, Settings.FrequencyHz, Settings.FftOverSample,
        Settings.ComputeCharge);
}
