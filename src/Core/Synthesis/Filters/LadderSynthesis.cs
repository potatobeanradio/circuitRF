using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// The ladder's closed-form designs (docs/design/filter-synthesis.md §3): a lowpass prototype's g0 … g(N+1) in, MLAD
/// sections on one substrate out, every line and stub sized by its MLIN impedance and phase at the design frequency
/// (<see cref="LadderPlan.Width"/>). Pure. The closed forms place each element at a point; the junctions, the steps
/// between widths and the coupling between stubs are left to the part's model, as rf-passives.md §3.16 says.
/// </summary>
public static class LadderSynthesis
{
    private const double Eighth = Math.PI / 4.0, Quarter = Math.PI / 2.0;

    // ── Lines and stubs ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The length of a line of width <paramref name="w"/> that is <paramref name="theta"/> radians long at F.</summary>
    public static double Length(double w, double theta, double fHz, MicrostripSubstrate s)
        => PlanarLineAngle.LengthMeters(BranchLinePlan.Line(w, s).LineParameters(fHz), theta);

    /// <summary>An open end's extension (<see cref="MicrostripOpenEnd"/>), which an open stub's drawn length leaves out.</summary>
    public static double OpenEnd(double w, MicrostripSubstrate s)
        => MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, BranchLinePlan.Line(w, s).LineParameters(0.0).EeffStatic);

    /// <summary>A line of <paramref name="z"/> ohms, <paramref name="theta"/> long at F.</summary>
    public static LadderLine Line(double z, double theta, double fHz, MicrostripSubstrate s)
    {
        double w = LadderPlan.Width(z, fHz, s);
        return new LadderLine(w, Length(w, theta, fHz, s));
    }

    /// <summary>A stub of <paramref name="z"/> ohms per arm, <paramref name="theta"/> long at F; an open one drawn short
    /// by its end extension.</summary>
    public static LadderStub Stub(double z, double theta, double fHz, LadderStubEnd end, LadderStubSide side, MicrostripSubstrate s)
    {
        double w = LadderPlan.Width(z, fHz, s);
        return new LadderStub(w, Length(w, theta, fHz, s) - (end == LadderStubEnd.Open ? OpenEnd(w, s) : 0.0), end, side);
    }

    /// <summary>A shunt stub of <paramref name="z"/> ohms: below <paramref name="r0"/> a cross of two arms of 2z, so no arm
    /// is wider than the ports' line.</summary>
    private static LadderStub Shunt(double z, double r0, double theta, double fHz, LadderStubEnd end, MicrostripSubstrate s)
        => z < r0 ? Stub(2.0 * z, theta, fHz, end, LadderStubSide.Both, s) : Stub(z, theta, fHz, end, LadderStubSide.Up, s);

    // ── Lowpass ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stepped-impedance lowpass (D. M. Pozar, <i>Microwave Engineering</i>, 4th ed., §8.6; a book, no DOI): shunt
    /// element first, each shunt C a <paramref name="zLow"/> line of βl = g·zLow/R0 and each series L a
    /// <paramref name="zHigh"/> line of βl = g·R0/zHigh, at fc.
    /// </summary>
    public static LadderSection[] SteppedImpedance(double[] g, double fc, double r0, double zLow, double zHigh, MicrostripSubstrate s)
        => [.. Enumerable.Range(1, g.Length - 2).Select(k => k % 2 == 1
               ? Line(zLow, g[k] * zLow / r0, fc, s)
               : Line(zHigh, g[k] * r0 / zHigh, fc, s))];

    /// <summary>
    /// The open-stub lowpass: Richards' transform (P. I. Richards,
    /// <a href="https://doi.org/10.1109/JRPROC.1948.233274">10.1109/JRPROC.1948.233274</a>) makes the prototype's series
    /// L's series short stubs of g·R0 and its shunt C's shunt open stubs of R0/g, all λ/8 at fc; Kuroda's identities
    /// (<see cref="Kuroda"/>) then turn every series stub into a shunt one with a unit element beside it. The result is
    /// exact in Richards' variable: open stubs joined by λ/8 lines, a stub below R0 drawn as a cross.
    /// </summary>
    public static LadderSection[] KurodaStubs(double[] g, double fc, double r0, MicrostripSubstrate s)
        => [.. Kuroda(g, r0).Select(e => e.Unit
               ? (LadderSection)Line(e.Z, Eighth, fc, s)
               : Shunt(e.Z, r0, Eighth, fc, LadderStubEnd.Open, s))];

    /// <summary>
    /// The prototype in Richards' variable as unit elements (<c>Unit</c>) and shunt open stubs, by impedance: from the
    /// series-first form or the shunt-first one, whichever spreads the impedances less (series-first for N = 3 and 7,
    /// shunt-first for 5, each then symmetric). Kuroda's identity (as H. Ozaki and J. Ishii work it,
    /// <a href="https://doi.org/10.1109/TCT.1958.1086441">10.1109/TCT.1958.1086441</a>), [unit element Z1][series stub Z2]
    /// ≡ [shunt stub Z1(Z1 + Z2)/Z2][unit element Z1 + Z2] (and its mirror), turns a series stub with a unit element
    /// beside it; read backwards it moves a unit element through
    /// a shunt stub, leaving a series stub behind, which the next pass turns in turn. Each series stub is worked from the
    /// nearer port, where a unit element of R0 is added when none is left: on a matched port it changes only the phase.
    /// Every step is an exact identity, so |S21| is the prototype's at Ω = tan θ.
    /// </summary>
    private static List<(bool Unit, double Z)> Kuroda(double[] g, double r0)
        => new[] { true, false }.Select(seriesFirst => Kuroda(g, r0, seriesFirst))
               .MinBy(c => c.Max(e => e.Z) / c.Min(e => e.Z))!;

    private static List<(bool Unit, double Z)> Kuroda(double[] g, double r0, bool seriesFirst)
    {
        // (Kind, Z): 'U' a unit element, 'S' a series short stub, 'P' a shunt open stub.
        var c = Enumerable.Range(1, g.Length - 2)
            .Select(k => (k % 2 == 1) == seriesFirst ? ('S', g[k] * r0) : ('P', r0 / g[k])).ToList();
        while (c.FindIndex(e => e.Item1 == 'S') is var i and >= 0)
        {
            int k = c.FindLastIndex(e => e.Item1 == 'S');
            bool right = c.Count - 1 - k < i;
            if (right) c.Reverse();
            Inward(c, right ? c.Count - 1 - k : i, r0);
            if (right) c.Reverse();
        }
        return [.. c.Select(e => (e.Item1 == 'U', e.Item2))];

        // The series stub at i, with only unit elements and shunt stubs to its left, turned into a shunt stub.
        static void Inward(List<(char, double)> c, int i, double r0)
        {
            while (true)
            {
                if (i > 0 && c[i - 1].Item1 == 'U')
                {
                    var (z1, z2) = (c[i - 1].Item2, c[i].Item2);
                    (c[i - 1], c[i]) = (('P', z1 * (z1 + z2) / z2), ('U', z1 + z2));
                    return;
                }
                int j = i - 1;
                while (j >= 0 && c[j].Item1 != 'U') j--;
                if (j < 0) { c.Insert(0, ('U', r0)); i++; continue; }
                // The shunt stubs between j and i are in parallel: one stub. Then
                // [U Za][P Zb] ≡ [S Za²/(Za + Zb)][U Za·Zb/(Za + Zb)] puts a unit element beside the series stub.
                double y = 0.0;
                for (int m = j + 1; m < i; m++) y += 1.0 / c[m].Item2;
                c.RemoveRange(j + 2, i - j - 2);
                i = j + 2;
                var (za, zb) = (c[j].Item2, 1.0 / y);
                (c[j], c[j + 1]) = (('S', za * za / (za + zb)), ('U', za * zb / (za + zb)));
            }
        }
    }

    /// <summary>
    /// The stub lowpass with transmission zeros: the series-first prototype's L's as <paramref name="zHigh"/> lines of
    /// βl = g·R0/zHigh at fc, each shunt C an open stub λ/4 at its zero whose admittance at fc is the capacitor's,
    /// tan(π·fc/2f∞)/Zs = g/R0 (Pozar §8.6; a book, no DOI). One zero serves every stub, or there is one per stub.
    /// </summary>
    public static LadderSection[] StubZeros(double[] g, double fc, double r0, double zHigh, IReadOnlyList<double> zerosHz,
        MicrostripSubstrate s)
    {
        var list = new List<LadderSection>();
        for (int k = 1, m = 0; k <= g.Length - 2; k++)
        {
            if (k % 2 == 1) { list.Add(Line(zHigh, g[k] * r0 / zHigh, fc, s)); continue; }
            double fz = zerosHz[Math.Min(m++, zerosHz.Count - 1)];
            list.Add(Stub(Math.Tan(Quarter * fc / fz) * r0 / g[k], Quarter, fz, LadderStubEnd.Open, LadderStubSide.Up, s));
        }
        return [.. list];
    }

    // ── Bandpass and bandstop ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shorted-stub bandpass (G. L. Matthaei, <a href="https://doi.org/10.1109/TMTT.1960.1124801">
    /// 10.1109/TMTT.1960.1124801</a>; the equations as J.-S. Hong and M. J. Lancaster's book gives them,
    /// <a href="https://doi.org/10.1002/0471221619">10.1002/0471221619</a>, §5.3): shorted stubs λ/4 at f0 (a cross
    /// below R0), joined by λ/4 lines of admittance J(i,i+1). With θ = (π/2)(1 − Δ/2) and h = 2, the admittance scale that
    /// keeps the end stubs to the interior's form: J12 = √(h·g1/g2), J(n−1,n) = √(h·g1·g(n+1)/g(n−1)), the others
    /// h·g1/√(gi·gi+1); N(i,i+1) = √(J² + (h·g1·tan θ/2)²); the stubs Y1 = N12 − J12 (h = 2), Yn = (gn·gn+1 − g1)·tan θ +
    /// N(n−1,n) − J(n−1,n), and Yi = N(i−1,i) + N(i,i+1) − J(i−1,i) − J(i,i+1), all in Y0 = 1/R0.
    /// </summary>
    public static LadderSection[] ShortedStubBandpass(double[] g, double f0, double fbw, double r0, MicrostripSubstrate s)
    {
        const double h = 2.0;
        int n = g.Length - 2;
        double t = Math.Tan(Quarter * (1.0 - fbw / 2.0)), a = h * g[1] * t / 2.0;
        var j = new double[n];
        for (int i = 1; i < n; i++)
            j[i] = i == 1 ? Math.Sqrt(h * g[1] / g[2])
                 : i == n - 1 ? Math.Sqrt(h * g[1] * g[n + 1] / g[n - 1])
                 : h * g[1] / Math.Sqrt(g[i] * g[i + 1]);
        double N(int i) => Math.Sqrt(j[i] * j[i] + a * a);
        var list = new List<LadderSection>();
        for (int i = 1; i <= n; i++)
        {
            double y = i == 1 ? (1.0 - h / 2.0) * g[1] * t + N(1) - j[1]
                     : i == n ? (g[n] * g[n + 1] - g[1] * h / 2.0) * t + N(n - 1) - j[n - 1]
                     : N(i - 1) + N(i) - j[i - 1] - j[i];
            if (i > 1) list.Add(Line(r0 / j[i - 1], Quarter, f0, s));
            list.Add(Shunt(r0 / y, r0, Quarter, f0, LadderStubEnd.Short, s));
        }
        return [.. list];
    }

    /// <summary>The quarter-wave stub bandstop: open stubs λ/4 at f0 of Zk = 4·R0/(π·gk·Δ), joined by λ/4 lines of R0
    /// (Pozar §8.8; a book, no DOI).</summary>
    public static LadderSection[] StubBandstop(double[] g, double f0, double fbw, double r0, MicrostripSubstrate s)
    {
        var list = new List<LadderSection>();
        for (int k = 1; k <= g.Length - 2; k++)
        {
            if (k > 1) list.Add(Line(r0, Quarter, f0, s));
            list.Add(Stub(4.0 * r0 / (Math.PI * g[k] * fbw), Quarter, f0, LadderStubEnd.Open, LadderStubSide.Up, s));
        }
        return [.. list];
    }

    /// <summary>
    /// The end-coupled (gap-coupled) bandpass (S. B. Cohn, <a href="https://doi.org/10.1109/JRPROC.1957.278389">
    /// 10.1109/JRPROC.1957.278389</a>): half-wave resonators of R0 between gaps. The inverters Z0·J01 = √(πΔ/2g0g1),
    /// Z0·Jk = πΔ/(2√(gk·gk+1)), Z0·J(N,N+1) = √(πΔ/2gN·gN+1) are series susceptances b = Z0J/(1 − (Z0J)²), each a gap
    /// whose MGAP Cg is b/(ω0·R0), its S found by bisection on MGAP's own closed form (<see cref="MicrostripGap"/>);
    /// resonator k is π − ½[atan 2bk + atan 2b(k+1)] long, less what its two ends' Cp stand for, atan(ω0·Cp·Z) each.
    /// A gap narrower than the metal is thick (or a micron) is refused, with the capacitance it would have to give.
    /// </summary>
    public static LadderSection[] EndCoupled(double[] g, double f0, double fbw, double r0, MicrostripSubstrate s)
    {
        int n = g.Length - 2;
        double w0 = LadderPlan.Width(r0, f0, s), omega = 2.0 * Math.PI * f0;
        var b = new double[n + 1];
        for (int k = 0; k <= n; k++)
        {
            double j = k == 0 || k == n ? Math.Sqrt(Math.PI * fbw / (2.0 * g[k] * g[k + 1]))
                                        : Math.PI * fbw / (2.0 * Math.Sqrt(g[k] * g[k + 1]));
            if (j >= 1.0)
                throw Refuse("synthesis.ladder.band-too-wide",
                    "End-coupled bandpass: a band of {bw} % asks J({k}) for Z0·J = {j}, which a series gap cannot give "
                  + "(it needs Z0·J < 1); narrow the band.",
                    ("bw", Fmt(fbw * 100.0)), ("k", $"{k},{k + 1}"), ("j", Fmt(j)));
            b[k] = j / (1.0 - j * j);
        }
        // ponytail: the narrowest gap is the metal's thickness (a micron at least); a process's own minimum gap would be
        // the better floor once a substrate carries one.
        double sMin = Math.Max(s.TMeters, 1e-6);
        var gaps = b.Select(bk => Gap(bk / (omega * r0), w0, sMin, s)).ToArray();
        double z = BranchLinePlan.Line(w0, s).LineParameters(f0).Z0;
        var list = new List<LadderSection> { new LadderGap(gaps[0].S) };
        for (int k = 0; k < n; k++)
        {
            double theta = Math.PI - 0.5 * (Math.Atan(2.0 * b[k]) + Math.Atan(2.0 * b[k + 1]))
                         - Math.Atan(omega * gaps[k].Cp * z) - Math.Atan(omega * gaps[k + 1].Cp * z);
            if (!(theta > 0.0))
                throw Refuse("synthesis.ladder.band-too-wide",
                    "End-coupled bandpass: resonator {k} comes out {deg}° long, its gaps' loading more than a half wave; "
                  + "narrow the band.", ("k", k + 1), ("deg", Fmt(theta * 180.0 / Math.PI)));
            list.Add(new LadderLine(w0, Length(w0, theta, f0, s)));
            list.Add(new LadderGap(gaps[k + 1].S));
        }
        return [.. list];
    }

    /// <summary>The gap whose MGAP series capacitance is <paramref name="cg"/> on a line <paramref name="w"/> wide, and its
    /// shunt capacitance: Cg falls as S grows, so a bisection on log S between <paramref name="sMin"/> and 100·H.</summary>
    private static (double S, double Cp) Gap(double cg, double w, double sMin, MicrostripSubstrate s)
    {
        double reach = MicrostripGap.Capacitances(w, sMin, s.HMeters, s.EpsR).SeriesC;
        if (cg > reach)
            throw Refuse("synthesis.ladder.gap-unreachable",
                "End-coupled bandpass: a coupling needs a gap of Cg = {need} fF, more than the narrowest gap ({smin} µm) "
              + "gives on this board ({reach} fF); widen the band, or take a coupled-line filter.",
                ("need", Fmt(cg * 1e15)), ("smin", Fmt(sMin * 1e6)), ("reach", Fmt(reach * 1e15)));
        double lo = Math.Log(sMin), hi = Math.Log(100.0 * s.HMeters);
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (MicrostripGap.Capacitances(w, Math.Exp(mid), s.HMeters, s.EpsR).SeriesC > cg) lo = mid; else hi = mid;
        }
        double gap = Math.Exp(0.5 * (lo + hi));
        return (gap, MicrostripGap.Capacitances(w, gap, s.HMeters, s.EpsR).ShuntC);
    }

    internal static string Fmt(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);

    internal static ParameterRefusalException Refuse(string code, string template, params (string, object?)[] args)
        => new(Diagnostic.Create(code, DiagnosticSeverity.Error, template, args));
}
