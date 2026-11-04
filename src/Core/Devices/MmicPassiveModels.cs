using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

// The four MMIC passives (brief-agent-authoring-overview.md AA-1). Every one is LINEAR and is stamped
// as a small admittance matrix over its own terminals and node 0, the implicit reference — the same
// arrangement MLIN and VIA have. Every material number arrives as a resolved SI parameter: a schematic
// on a technology has them injected at extraction (MmicPassiveInjection, src/Design) and a bare .cnl
// line takes the defaults ComponentModelFactory states, which are the shipped GaAs process's.
//
// Every quantity is in MmicPassiveFormulas, with its source.

/// <summary>
/// <c>MIMCAP</c> — a metal-insulator-metal capacitor. Terminal 1 is the TOP plate, terminal 2 the
/// BOTTOM plate (the one lying on the substrate).
///
/// <para><b>Topology.</b> Terminal 1 — the plates' series resistance — the plate capacitance with its
/// dielectric loss in parallel — terminal 2, and from terminal 2 the bottom plate's capacitance through
/// the substrate to node 0. The top plate's own capacitance to ground is screened by the bottom plate
/// and is not modelled. Which plate faces the substrate matters for a shunt part: ground terminal 2 and
/// that parasitic is shorted out, ground terminal 1 and it hangs off the signal node.</para>
/// </summary>
public sealed class MimCapModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    public double W { get; }
    public double L { get; }
    public double EpsR { get; }
    public double Td { get; }
    public double TanD { get; }
    public double SigmaTop { get; }
    public double TTop { get; }
    public double SigmaBottom { get; }
    public double TBottom { get; }
    public double SubstrateH { get; }
    public double SubstrateEpsR { get; }

    /// <summary>The plate capacitance with fringing (Palmer), F.</summary>
    public double Capacitance { get; }

    /// <summary>The bottom plate's capacitance to node 0 through the substrate, F (parallel plate, no
    /// fringing).</summary>
    public double BottomPlateCapacitance { get; }

    private readonly LumpedValidityWarning _warning;

    public MimCapModel(double w, double l, double epsR, double td, double tanD,
                       double sigmaTop, double tTop, double sigmaBottom, double tBottom,
                       double substrateH, double substrateEpsR)
    {
        W = w; L = l; EpsR = epsR; Td = td; TanD = tanD;
        SigmaTop = sigmaTop; TTop = tTop; SigmaBottom = sigmaBottom; TBottom = tBottom;
        SubstrateH = substrateH; SubstrateEpsR = substrateEpsR;
        Capacitance = MmicPassiveFormulas.PalmerCapacitance(epsR, w, l, td);
        BottomPlateCapacitance = MmicPassiveFormulas.ParallelPlateCapacitance(substrateEpsR, w, l, substrateH);
        _warning = new LumpedValidityWarning("MIMCAP", "plate", Math.Max(w, l), epsR);
    }

    /// <summary>The equivalent series resistance at <paramref name="freqHz"/>, Ω.</summary>
    public double Esr(double freqHz)
        => MmicPassiveFormulas.MimPlateEsr(
            MmicPassiveFormulas.SheetResistance(freqHz, TTop, SigmaTop),
            MmicPassiveFormulas.SheetResistance(freqHz, TBottom, SigmaBottom), W, L);

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    public (Complex Y11, Complex Y12, Complex Y22) Admittance(double omega)
    {
        if (omega <= 0) return (Complex.Zero, Complex.Zero, Complex.Zero);
        double f = omega / (2 * Math.PI);
        Complex yc = new(omega * Capacitance * TanD, omega * Capacitance);
        Complex zs = Esr(f) + 1 / yc;
        Complex ys = 1 / zs;
        return (ys, -ys, ys + new Complex(0, omega * BottomPlateCapacitance));
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        var (y11, y12, y22) = Admittance(omega);
        MmicStamp.TwoPort(mna, c.Nodes[0], c.Nodes[1], y11, y12, y22);
    }
}

/// <summary>
/// <c>TFR</c> — a thin-film resistor, sheet resistance times squares, drawn straight or as a serpentine
/// of <see cref="Segments"/> runs. Terminals 1 and 2 are the two ends and are interchangeable.
///
/// <para><b>Topology.</b> A π: the resistance between the terminals, and half the film's capacitance
/// through the substrate to node 0 at each end.</para>
/// </summary>
public sealed class ThinFilmResistorModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    public double W { get; }
    public double L { get; }
    public int Segments { get; }
    public double Link { get; }
    public double Squares { get; }
    public double SheetResistance { get; }
    public double Resistance { get; }
    public double ShuntCapacitance { get; }
    private readonly LumpedValidityWarning _warning;

    public ThinFilmResistorModel(double w, double l, double sheetResistance, double substrateH, double substrateEpsR,
                                 int segments = 1, double link = 0)
    {
        W = w; L = l; SheetResistance = sheetResistance;
        Segments = Math.Max(segments, 1);
        Link = link;
        double length = MmicPassiveFormulas.SerpentineLength(w, l, Segments, link);
        Squares = MmicPassiveFormulas.SerpentineSquares(w, l, Segments, link);
        Resistance = Math.Max(sheetResistance * Squares, 1e-9);
        ShuntCapacitance = MmicPassiveFormulas.ParallelPlateCapacitance(substrateEpsR, w, length, substrateH);
        _warning = new LumpedValidityWarning("TFR", "film", length, (substrateEpsR + 1) / 2);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    public (Complex Y11, Complex Y12, Complex Y22) Admittance(double omega)
    {
        Complex g = 1 / Resistance;
        Complex half = new(0, omega * ShuntCapacitance / 2);
        return (g + half, -g, g + half);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        var (y11, y12, y22) = Admittance(omega);
        MmicStamp.TwoPort(mna, c.Nodes[0], c.Nodes[1], y11, y12, y22);
    }
}

/// <summary>
/// <c>SPIRAL</c> — a square spiral inductor, and <c>OSPIRAL</c>, the octagonal one. Terminal 1 is the
/// OUTER end, terminal 2 the INNER end, brought out over the turns on the bridge metal.
///
/// <para><b>Topology.</b> The usual π: the coil's <c>R(f) + jωL</c> between the terminals, with the
/// crossing's overlap capacitance across it, and half the trace's capacitance through the substrate
/// to node 0 at each end. The inductance is the partial-inductance sum over the drawn path and its
/// image in the ground plane (<see cref="PartialInductance"/>) — an ESTIMATE; an EM extraction of the
/// drawn coil is the reference.</para>
/// </summary>
public sealed class SpiralInductorModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;

    public double Turns { get; }
    public double W { get; }
    public double S { get; }
    public double Din { get; }
    public double Sigma { get; }
    public double T { get; }

    /// <summary>True for <c>OSPIRAL</c>: the same coil with its corners cut.</summary>
    public bool Octagonal { get; }

    /// <summary>The series inductance, H: the drawn current path's partial-inductance sum over the ground
    /// plane (<see cref="PartialInductance"/>). An estimate — see that type for what it leaves out.</summary>
    public double Inductance { get; }

    /// <summary>The modified Wheeler estimate of the coil alone in free space, H — kept for comparison.</summary>
    public double WheelerInductance { get; }

    /// <summary>The drawn coil's centreline length, m.</summary>
    public double TraceLength { get; }
    public double ShuntCapacitance { get; }
    public double CrossingCapacitance { get; }
    private readonly LumpedValidityWarning _warning;

    public SpiralInductorModel(double turns, double w, double s, double din, double sigma, double t,
                               double substrateH, double substrateEpsR, double bridgeH, double bridgeEpsR,
                               bool octagonal = false)
    {
        Turns = turns; W = w; S = s; Din = din; Sigma = sigma; T = t; Octagonal = octagonal;
        // The DRAWN part: the walk the layout generator draws, its escape and its landing pad, summed
        // segment by segment over the ground plane under the substrate (PartialInductance). Modified
        // Wheeler — the coil alone, in free space — is kept beside it for comparison.
        var path = SpiralWalk.Path(turns, w, s, din, bridgeH, octagonal);
        Inductance = PartialInductance.OfPath(path, w, t, substrateH);
        WheelerInductance = MmicPassiveFormulas.ModifiedWheelerInductance(turns, w, s, din, octagonal);
        // The coil's own drawn length, which is what the series R and the shunt C are of; the escape is on
        // the bridge metal and the pad is a lead, so neither is counted.
        TraceLength = 0;
        for (int i = 0; i < path.Count - 2; i++) TraceLength += path[i].Length;
        ShuntCapacitance = MmicPassiveFormulas.ParallelPlateCapacitance(substrateEpsR, TraceLength, w, substrateH);
        int crossings = octagonal ? OctagonalCrossings(turns) : Crossings(turns);
        CrossingCapacitance = crossings * MmicPassiveFormulas.ParallelPlateCapacitance(bridgeEpsR, w, w, bridgeH);
        _warning = new LumpedValidityWarning(octagonal ? "OSPIRAL" : "SPIRAL", "trace", TraceLength, (substrateEpsR + 1) / 2);
    }

    /// <summary>An octagonal coil drawn in whole SIDES: <paramref name="turns"/> rounded to the nearest
    /// eighth turn, at least one side. The layout generator draws exactly this many.</summary>
    public static int OctagonalSides(double turns) => Math.Max((int)Math.Round(8 * turns, MidpointRounding.AwayFromZero), 1);

    /// <summary>How many turns an octagonal coil's escape crosses: it leaves the middle of the innermost
    /// flat straight outward, crossing that flat's counterpart on every later lap — one per side numbered
    /// 8, 16, 24… that is drawn. The layout generator draws exactly this crossing.</summary>
    public static int OctagonalCrossings(double turns) => (OctagonalSides(turns) - 1) / 8;

    /// <summary>The coil drawn in whole SIDES: <paramref name="turns"/> rounded to the nearest quarter
    /// turn, at least one side. The layout generator draws exactly this many.</summary>
    public static int Sides(double turns) => Math.Max((int)Math.Round(4 * turns, MidpointRounding.AwayFromZero), 1);

    /// <summary>How many turns the inner end's escape crosses on its way out — the overlaps the
    /// crossing capacitance counts. The escape leaves the inner end across the coil's first side, so it
    /// crosses that side's counterpart on every later lap: one per side numbered 4, 8, 12… that is
    /// drawn. The layout generator draws exactly this crossing.</summary>
    public static int Crossings(double turns) => (Sides(turns) - 1) / 4;

    /// <summary>The coil's series resistance at <paramref name="freqHz"/>, Ω.</summary>
    public double Resistance(double freqHz)
    {
        double te = MmicPassiveFormulas.EffectiveThickness(freqHz, T, Sigma);
        return Sigma > 0 && te > 0 && W > 0 ? TraceLength / (W * Sigma * te) : 1e-9;
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    public (Complex Y11, Complex Y12, Complex Y22) Admittance(double omega)
    {
        double f = omega / (2 * Math.PI);
        Complex z = new(Math.Max(Resistance(f), 1e-9), omega * Inductance);
        Complex ys = 1 / z + new Complex(0, omega * CrossingCapacitance);
        Complex half = new(0, omega * ShuntCapacitance / 2);
        return (ys + half, -ys, ys + half);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        var (y11, y12, y22) = Admittance(omega);
        MmicStamp.TwoPort(mna, c.Nodes[0], c.Nodes[1], y11, y12, y22);
    }
}

/// <summary>
/// <c>AIRBRIDGE</c> — a span of the bridge metal crossing over another line: a FOUR-port. Terminals 1
/// and 2 are the bridge's two landings; terminals 3 and 4 are the two ends of the crossed line's
/// segment under the bridge (<see cref="CrossedLength"/> long, two bridge widths), so the line runs
/// through the part rather than ending at it.
///
/// <para><b>Topology.</b> Two Ts sharing their midpoints' coupling: half the span's <c>R(f) + jωL</c>
/// from each landing to the span's midpoint, half the crossed segment's from each of its ends to its
/// midpoint, and the overlap capacitance between the two midpoints. The midpoints are eliminated
/// exactly at every frequency (the part is linear). Tie 3 and 4 to ground for a bridge over a ground
/// strap.</para>
/// </summary>
public sealed class AirbridgeModel : ComponentModel, IReportsWarnings
{
    public override int PortCount => 4;
    public override ModelKind Kind => ModelKind.Linear;

    public double Span { get; }
    public double W { get; }
    public double Sigma { get; }
    public double T { get; }
    public double Inductance { get; }
    public double CrossingCapacitance { get; }

    /// <summary>The crossed segment: two bridge widths long (the bridge's footprint and half a width
    /// beyond each edge — what the layout generator draws), <see cref="CrossedWidth"/> wide.</summary>
    public double CrossedLength { get; }
    public double CrossedWidth { get; }
    public double CrossedSigma { get; }
    public double CrossedT { get; }
    public double CrossedInductance { get; }
    private readonly LumpedValidityWarning _warning;

    public AirbridgeModel(double span, double w, double crossedWidth, double sigma, double t,
                          double bridgeH, double bridgeEpsR, double crossedSigma, double crossedT)
    {
        Span = span; W = w; Sigma = sigma; T = t;
        Inductance = MmicPassiveFormulas.RibbonInductance(span, w, t);
        CrossingCapacitance = MmicPassiveFormulas.ParallelPlateCapacitance(bridgeEpsR, w, crossedWidth, bridgeH);
        CrossedLength = 2 * w; CrossedWidth = crossedWidth; CrossedSigma = crossedSigma; CrossedT = crossedT;
        CrossedInductance = MmicPassiveFormulas.RibbonInductance(CrossedLength, crossedWidth, crossedT);
        _warning = new LumpedValidityWarning("AIRBRIDGE", "span", span, 1.0);
    }

    public double Resistance(double freqHz) => StripResistance(freqHz, Span, W, T, Sigma);

    /// <summary>The crossed segment's series resistance at <paramref name="freqHz"/>, Ω.</summary>
    public double CrossedResistance(double freqHz) => StripResistance(freqHz, CrossedLength, CrossedWidth, CrossedT, CrossedSigma);

    private static double StripResistance(double freqHz, double length, double w, double t, double sigma)
    {
        double te = MmicPassiveFormulas.EffectiveThickness(freqHz, t, sigma);
        return sigma > 0 && te > 0 && w > 0 ? length / (w * sigma * te) : 1e-9;
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _warning.Drain();

    /// <summary>The 4×4 admittance over (1, 2, 3, 4), both midpoints eliminated.</summary>
    public Complex[,] Admittance(double omega)
    {
        double f = omega / (2 * Math.PI);
        Complex yb = 2 / new Complex(Math.Max(Resistance(f), 1e-9), omega * Inductance);               // each half span
        Complex yu = 2 / new Complex(Math.Max(CrossedResistance(f), 1e-9), omega * CrossedInductance);  // each half segment
        Complex yc = new(0, omega * CrossingCapacitance);

        // Nodes 0..3 the terminals, 4 the bridge midpoint, 5 the crossed segment's midpoint.
        var full = new Complex[6, 6];
        void Branch(int a, int b, Complex y) { full[a, a] += y; full[b, b] += y; full[a, b] -= y; full[b, a] -= y; }
        Branch(0, 4, yb); Branch(4, 1, yb);
        Branch(2, 5, yu); Branch(5, 3, yu);
        Branch(4, 5, yc);

        // Kron reduction: Y = Ytt − Yti·Yii⁻¹·Yit, Yii the 2×2 midpoint block.
        Complex a11 = full[4, 4], a12 = full[4, 5], a21 = full[5, 4], a22 = full[5, 5];
        Complex det = a11 * a22 - a12 * a21;
        Complex i11 = a22 / det, i12 = -a12 / det, i21 = -a21 / det, i22 = a11 / det;
        var y = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
            {
                Complex t0 = i11 * full[4, j] + i12 * full[5, j];
                Complex t1 = i21 * full[4, j] + i22 * full[5, j];
                y[i, j] = full[i, j] - (full[i, 4] * t0 + full[i, 5] * t1);
            }
        return y;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        _warning.Check(c.InstancePath, omega / (2 * Math.PI));
        var y = Admittance(omega);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                mna.AddBlockAdmittance(c.Nodes[i], c.Nodes[j], y[i, j]);
    }
}

internal static class MmicStamp
{
    public static void TwoPort(IMnaContext mna, int a, int b, Complex y11, Complex y12, Complex y22)
    {
        mna.AddBlockAdmittance(a, a, y11);
        mna.AddBlockAdmittance(a, b, y12);
        mna.AddBlockAdmittance(b, a, y12);
        mna.AddBlockAdmittance(b, b, y22);
    }
}

/// <summary>One warning per instance, the first time a lumped MMIC passive is stamped above the
/// frequency where its longest dimension is a twentieth of a wavelength.</summary>
internal sealed class LumpedValidityWarning(string token, string what, double length, double epsR)
{
    private bool _reported;
    private readonly List<(string, string)> _pending = [];

    public double Limit { get; } = MmicPassiveFormulas.LumpedValidityFrequency(length, epsR);

    public void Check(string instancePath, double freqHz)
    {
        if (_reported || !(freqHz > Limit)) return;
        _reported = true;
        _pending.Add(($"{token}-validity:{instancePath}",
            $"{instancePath}: this {token}'s {what} is {length * 1e6:G4} µm long, a twentieth of a wavelength at " +
            $"{Limit / 1e9:G4} GHz. Above that its lumped model is an extrapolation (this run goes past it, from " +
            $"{freqHz / 1e9:G4} GHz); an EM extraction of the drawn part is the reference there."));
    }

    public IReadOnlyList<(string Key, string Message)> Drain()
    {
        if (_pending.Count == 0) return [];
        var r = _pending.ToArray();
        _pending.Clear();
        return r;
    }
}
