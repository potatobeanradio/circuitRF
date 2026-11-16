using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices.Coupled;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Diagnostics;
using NumFlat;
using RfCore;

namespace CircuitRF.Core.Devices;

/// <summary>
/// One solved cross-section of N edge-coupled microstrip strips: the Maxwell capacitance matrices with and without the
/// dielectric (F/m, strips in order), the inductance matrix they imply, the capacitance with the dielectric's loss in it,
/// and the lossless modal decomposition of L·C.
/// </summary>
/// <param name="C">Maxwell C with the dielectric.</param>
/// <param name="C0">Maxwell C with every dielectric replaced by vacuum.</param>
/// <param name="L">μ₀ε₀·C₀⁻¹, H/m: the line's inductance, which the dielectric does not change.</param>
/// <param name="CComplex">C − j·tanδ·εr/(εr − 1)·(C − C₀): G = ω·tanδ·εr/(εr − 1)·(C − C₀), MLIN's filling-factor
/// dielectric loss in matrix form (exactly MLIN's per mode, and ω·tanδ·C for a homogeneous dielectric).</param>
/// <param name="Modes">Route A's modes (<see cref="MulticonductorLine.Decompose"/>): each mode's Z0, εeff and pattern.</param>
/// <param name="Span">The cross-section's width, first strip's outer edge to the last's (m).</param>
public sealed record MultiLineSection(Mat<double> C, Mat<double> C0, Mat<double> L, Mat<Complex> CComplex,
                                      MulticonductorLine.GeneralModes Modes, double Span);

/// <summary>
/// MCLINN — N edge-coupled microstrip strips, each of its own width, with a gap of its own between each two, all of one
/// length, on one substrate: a 2N-port, ground-referenced. Every strip couples to every other, the neighbours on both
/// sides at once and the strips beyond them, which no cascade of coupled pairs can describe.
///
/// <para>Pins: 1..N are the strips' near ends (strip 1 the top one, as drawn), N + 1..2N their far ends, so strip k runs
/// from pin k to pin N + k. Parameters (SI): <c>N</c>; <c>W</c> and <c>S</c>, every strip's width and every gap, with
/// <c>W[k]</c> (k = 1..N) and <c>S[k]</c> (k = 1..N − 1, the gap below strip k) stating their own; <c>L</c>; the
/// substrate injected as MLIN's (<c>H</c>, <c>T</c>, <c>Er</c>, <c>Sigma</c>, <c>TanD</c>, <c>Roughness</c>).</para>
///
/// <para><b>Method</b> (Tripathi and Lee, <a href="https://doi.org/10.1109/22.20041">10.1109/22.20041</a>; Paul,
/// <a href="https://doi.org/10.1109/9780470547212">10.1109/9780470547212</a>). The cross-section — the strips at H to
/// H + T over a ground plane, the substrate below them, air above — is solved by circuitRF's own 2D field solver
/// (<see cref="QuasiStaticSolver2D"/>) twice, with the dielectric (C) and without (C₀); L = μ₀ε₀·C₀⁻¹. Conductor loss is
/// MLIN's Wheeler form per strip, R_kk = 2·Rs·Kr/W_k (<see cref="MicrostripLoss.ConductorLossNpPerM"/> is Rs·Kr/(Z·W) = R/(2Z)),
/// so a symmetric pair's modes carry exactly MCLIN's conductor loss; G is the filling-factor form above. The 2N-port is
/// the modal one (<see cref="MulticonductorLine"/>, shared with the MoM quasi-static kernel): the modes of L·C from the
/// lossless problem, each an ordinary line of its own Zc = √(Zm/Ym) and γ = √(Zm·Ym), recombined through Ti into the
/// short-circuit admittance, Y = Σ_m Ti[:,m]·y_m·Ti[:,m]ᵀ/e_m.</para>
///
/// <para><b>Limits.</b> Quasi-static: solved once per geometry (at the first stamp, and cached process-wide), with no
/// dispersion, so each mode's εeff and Zc are their static values at every frequency; past a cross-section of a tenth of
/// the wavelength in the dielectric it says so. The solver's own accuracy holds: within about 0.5 % for C of strips with
/// thickness, about 1.2 % low in Z for zero-thickness ones (said once). Loss rides perturbatively on the lossless modes
/// (Route A), and the per-strip R leaves out proximity crowding between close strips.</para>
/// </summary>
public sealed class MicrostripMultiLineModel : ComponentModel, IReportsWarnings, IDcShortedTerminals
{
    /// <summary>The engine token.</summary>
    public const string TypeName = "MCLINN";

    /// <summary>The strips a part has when nothing says otherwise.</summary>
    public const int DefaultStrips = 3;

    /// <summary>The strips behind a pin count: half of it for an even count of four or more, else
    /// <see cref="DefaultStrips"/> (the one-argument symbol overload's count included).</summary>
    public static int StripsForPinCount(int pins) => pins >= 4 && pins % 2 == 0 ? pins / 2 : DefaultStrips;

    private static readonly ConcurrentDictionary<string, MultiLineSection> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 512;

    private readonly int _n;
    private readonly double[] _w;
    private readonly double _l;
    private readonly MicrostripSubstrate _sub;
    private readonly MicrostripValidityReporter _reporter;

    /// <summary>Strips, N.</summary>
    public int Strips => _n;

    private readonly Lazy<MultiLineSection> _section;

    /// <summary>The solved cross-section this instance stamps: solved at its first use (the first stamp of a sweep), not
    /// at construction, so building the part to ask its pin count costs no field solve.</summary>
    public MultiLineSection Section => _section.Value;

    public override int PortCount => 2 * _n;
    public override ModelKind Kind => ModelKind.Linear;

    /// <summary>Lossless at DC: each strip a short end to end, pin k to pin N + k.</summary>
    public IReadOnlyList<(int A, int B)> DcShortedTerminals { get; }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _reporter.Drain();

    /// <param name="w">Each strip's width, top strip first (N of them).</param>
    /// <param name="s">The edge-to-edge gap below each strip but the last (N − 1 of them).</param>
    public MicrostripMultiLineModel(IReadOnlyList<double> w, IReadOnlyList<double> s, double l, MicrostripSubstrate sub,
                                    string instancePath)
    {
        if (Refusal(w, s, l, sub) is { } why) throw new ArgumentException(why.Render());
        _n = w.Count;
        _w = [.. w];
        _l = l;
        _sub = sub;
        _reporter = new MicrostripValidityReporter(instancePath);
        DcShortedTerminals = [.. Enumerable.Range(0, _n).Select(k => (k, _n + k))];
        double[] gaps = [.. s];
        _section = new Lazy<MultiLineSection>(() => Solve(_w, gaps, _sub));
        if (sub.TMeters == 0.0)
            _reporter.ReportOnce($"{TypeName}:thin",
                "the strips have no thickness (T = 0): the field solver's impedances for a zero-thickness strip err low by up " +
                "to about 1.2 %; give T for its 0.5 %.");
    }

    /// <summary>The coded refusal for strips no cross-section can have; null when they are valid.</summary>
    public static Diagnostic? Refusal(IReadOnlyList<double> w, IReadOnlyList<double> s, double l, MicrostripSubstrate sub)
    {
        if (w.Count < 2)
            return Diagnostic.Create("mclinn.strips.too-few", DiagnosticSeverity.Error,
                "MCLINN: N={n} strips; coupled lines are at least two. One strip is MLIN.", ("n", w.Count));
        if (s.Count != w.Count - 1)
            return Diagnostic.Create("mclinn.gaps.count", DiagnosticSeverity.Error,
                "MCLINN: {n} strips need {need} gaps; {s} given.", ("n", w.Count), ("need", w.Count - 1), ("s", s.Count));
        for (int k = 0; k < w.Count; k++)
            if (!(w[k] > 0.0))
                return Diagnostic.Create("mclinn.width.not-positive", DiagnosticSeverity.Error,
                    "MCLINN: W[{k}]={w} m must be positive.", ("k", k + 1), ("w", w[k]));
        for (int k = 0; k < s.Count; k++)
            if (!(s[k] > 0.0))
                return Diagnostic.Create("mclinn.gap.not-positive", DiagnosticSeverity.Error,
                    "MCLINN: gap S[{k}]={s} m must be positive. Touching strips are one wider line.", ("k", k + 1), ("s", s[k]));
        if (!(l >= 0.0))
            return Diagnostic.Create("mclinn.length.negative", DiagnosticSeverity.Error, "MCLINN: L={l} m must not be negative.", ("l", l));
        if (!(sub.HMeters > 0.0))
            return Diagnostic.Create("mclinn.height.not-positive", DiagnosticSeverity.Error,
                "MCLINN: substrate H={h} m must be positive.", ("h", sub.HMeters));
        if (!(sub.TMeters >= 0.0))
            return Diagnostic.Create("mclinn.thickness.negative", DiagnosticSeverity.Error,
                "MCLINN: T={t} m must not be negative.", ("t", sub.TMeters));
        if (!(sub.EpsR >= 1.0))
            return Diagnostic.Create("mclinn.er.below-one", DiagnosticSeverity.Error,
                "MCLINN: Er={er} is below 1; a dielectric's relative permittivity is at least that of vacuum.", ("er", sub.EpsR));
        return null;
    }

    /// <summary>
    /// Every strip's width and every gap from a part's parameters: <c>W[k]</c> or else <c>W</c>, <c>S[k]</c> or else
    /// <c>S</c>, read through <paramref name="real"/> (name, fallback) so the model and the artwork read them alike.
    /// </summary>
    public static (double[] W, double[] S) StripsOf(int n, Func<string, double, double> real, double wDefault, double sDefault)
    {
        double w = real("W", wDefault), s = real("S", sDefault);
        return ([.. Enumerable.Range(1, Math.Max(n, 0)).Select(k => real($"W[{k}]", w))],
                [.. Enumerable.Range(1, Math.Max(n - 1, 0)).Select(k => real($"S[{k}]", s))]);
    }

    /// <summary>The solved cross-section of strips <paramref name="w"/> and gaps <paramref name="s"/> on
    /// <paramref name="sub"/>, from the cache when this geometry was solved before.</summary>
    public static MultiLineSection Solve(IReadOnlyList<double> w, IReadOnlyList<double> s, MicrostripSubstrate sub)
    {
        string key = string.Join(";", w.Select(R)) + "|" + string.Join(";", s.Select(R))
                   + $"|{R(sub.HMeters)};{R(sub.TMeters)};{R(sub.EpsR)};{R(sub.TanD)}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var solved = SolveUncached(w, s, sub);
        if (Cache.Count >= CacheLimit) Cache.Clear();
        Cache[key] = solved;
        return solved;
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }

    private static MultiLineSection SolveUncached(IReadOnlyList<double> w, IReadOnlyList<double> s, MicrostripSubstrate sub)
    {
        int n = w.Count;
        var strips = new StripRect[n];
        double x = 0.0;
        for (int k = 0; k < n; k++)
        {
            strips[k] = new StripRect(x, x + w[k], sub.HMeters, sub.HMeters + sub.TMeters);
            x += w[k] + (k < n - 1 ? s[k] : 0.0);
        }
        var cs = new LayeredCrossSection([new DielectricLayer(0.0, sub.HMeters, sub.EpsR)], null, strips);
        var c = QuasiStaticSolver2D.Solve(cs).C;
        var c0 = sub.EpsR > 1.0 ? QuasiStaticSolver2D.Solve(cs, airOnly: true).C : c;

        double fill = sub.EpsR > 1.0 ? sub.TanD * sub.EpsR / (sub.EpsR - 1.0) : 0.0;
        var cm = new Mat<double>(n, n);
        var c0m = new Mat<double>(n, n);
        var cc = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                cm[i, j] = c[i, j];
                c0m[i, j] = c0[i, j];
                cc[i, j] = new Complex(c[i, j], -fill * (c[i, j] - c0[i, j]));
            }
        var l = MulticonductorLine.Invert(c0m);
        double mu0Eps0 = MicrostripLoss.Mu0 * QuasiStaticSolver2D.Eps0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                l[i, j] *= mu0Eps0;
        return new MultiLineSection(cm, c0m, l, cc, MulticonductorLine.Decompose(cc, c0m, l), x);
    }

    /// <summary>[R](f): MLIN's Wheeler conductor loss per strip, diagonal; none for a perfect conductor.</summary>
    private Mat<double> Resistance(double freqHz)
    {
        var r = new Mat<double>(_n, _n);
        if (_sub.SigmaSPerM > 0 && !double.IsPositiveInfinity(_sub.SigmaSPerM))
            for (int k = 0; k < _n; k++)
                r[k, k] = 2.0 * MicrostripLoss.ConductorLossNpPerM(freqHz, _sub.SigmaSPerM, _w[k], 1.0, _sub.RoughnessMeters);
        return r;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var nodes = c.Nodes;
        if (omega <= 0.0)
        {
            var g = new Complex(1.0e9, 0.0);
            foreach (var (a, b) in DcShortedTerminals) mna.AddAdmittance(nodes[a], nodes[b], g);
            return;
        }
        double f = omega / (2.0 * Math.PI);
        BroadsideCoupledPair.ReportQuasiStatic(TypeName, Math.Max(Section.Span, _sub.HMeters + _sub.TMeters), _sub.EpsR, f,
                                               _reporter);

        var modes = Section.Modes;
        var pt = MulticonductorLine.EvaluateAt(Resistance(f), Section.L, Section.CComplex, modes, omega);
        var self = new Complex[_n];
        var cross = new Complex[_n];
        for (int m = 0; m < _n; m++)
        {
            Complex gamma = MulticonductorLine.PrincipalSqrt(pt.Z[m] * pt.Y[m]);
            Complex zc = MulticonductorLine.PrincipalSqrt(pt.Z[m] / pt.Y[m]);
            (self[m], cross[m]) = CLineModel.Modal(zc, gamma * _l);
        }
        var ySelf = MulticonductorLine.Block(modes.Ti, modes.CurrentScale, self);
        var yCross = MulticonductorLine.Block(modes.Ti, modes.CurrentScale, cross);
        for (int a = 0; a < _n; a++)
            for (int b = 0; b < _n; b++)
            {
                mna.AddBlockAdmittance(nodes[a], nodes[b], ySelf[a, b]);
                mna.AddBlockAdmittance(nodes[_n + a], nodes[_n + b], ySelf[a, b]);
                mna.AddBlockAdmittance(nodes[a], nodes[_n + b], yCross[a, b]);
                mna.AddBlockAdmittance(nodes[_n + a], nodes[b], yCross[a, b]);
            }
    }
}
