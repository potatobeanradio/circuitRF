using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// The coupled array's closed forms (docs/design/filter-synthesis.md §5): from a prototype's g-values, each strip's
/// admittance to ground and each adjacent pair's mutual admittance per unit length, normalised to the terminations'
/// Y_A; then the widths and gaps whose field-solved cross-section has them.
/// </summary>
public static class CoupledArraySynthesis
{
    internal const double C0 = 299_792_458.0;

    /// <summary>
    /// Matthaei's interdigital design (<a href="https://doi.org/10.1109/TMTT.1962.1125556">10.1109/TMTT.1962.1125556</a>,
    /// Table I, the procedure for narrow and moderate bands), terminating lines 0 and n + 1: each line's admittance to
    /// ground (C_k/ε over 376.7·Y_A/√εr) and each adjacent pair's mutual one. <paramref name="w"/> is the band over its
    /// arithmetic centre, which sets θ1, the lines' electrical length at the lower edge; <paramref name="h"/> is the
    /// resonators' admittance scale.
    /// </summary>
    public static (double[] Ground, double[] Mutual) Interdigital(double[] g, double w, double h)
    {
        int n = g.Length - 2;
        double t = Math.Tan(Math.PI / 2 * (1 - w / 2));
        double[] j = [.. Enumerable.Range(0, n + 1).Select(k => 1 / Math.Sqrt(g[k] * g[k + 1]))];
        double N(int k) => Math.Sqrt(j[k] * j[k] + t * t / 4);
        double m1 = Math.Sqrt(h) * j[0] + 1, mn = Math.Sqrt(h) * j[n] + 1;
        var ground = new double[n + 2];
        var mutual = new double[n + 1];
        ground[0] = 2 - m1;
        ground[n + 1] = 2 - mn;
        mutual[0] = m1 - 1;
        mutual[n] = mn - 1;
        for (int k = 1; k < n; k++) mutual[k] = h * j[k];
        for (int k = 1; k <= n; k++)
        {
            double inner = (k > 1 ? N(k - 1) - j[k - 1] : 0) + (k < n ? N(k) - j[k] : 0);
            ground[k] = h * inner + (k == 1 ? 1 - m1 + h * (t / 2 + j[0] * j[0]) : 0)
                                  + (k == n ? 1 - mn + h * (t / 2 + j[n] * j[n]) : 0);
        }
        return (ground, mutual);
    }

    /// <summary>
    /// A combline's resonator slope parameter over its own admittance Y_a: a line shorted at one end and loaded at the
    /// other to resonate at θ0, b/Y_a = (cot θ0 + θ0·csc²θ0)/2 (Matthaei, <i>Microwave J.</i>, vol. 6, Aug. 1963, no DOI).
    /// </summary>
    public static double ComblineSlope(double theta0) => (1 / Math.Tan(theta0) + theta0 / Math.Pow(Math.Sin(theta0), 2)) / 2;

    /// <summary>
    /// A tapped combline's admittances (Matthaei 1963, no DOI): every strip a resonator of admittance
    /// <paramref name="ya"/> with its neighbours grounded, each pair's inverter J = w·b/√(g_k·g_(k+1)) realised by the
    /// mutual admittance Y_(k,k+1) = J·tan θ0, each strip's admittance to ground what is left of Y_a.
    /// </summary>
    public static (double[] Ground, double[] Mutual) Combline(double[] g, double w, double theta0, double ya)
    {
        int n = g.Length - 2;
        double b = ya * ComblineSlope(theta0);
        double[] mutual = [.. Enumerable.Range(1, n - 1).Select(k => w * b / Math.Sqrt(g[k] * g[k + 1]) * Math.Tan(theta0))];
        double[] ground = [.. Enumerable.Range(0, n).Select(k => ya - (k > 0 ? mutual[k - 1] : 0) - (k < n - 1 ? mutual[k] : 0))];
        return (ground, mutual);
    }

    /// <summary>
    /// A combline's length and loading on its solved cross-section: every strip <paramref name="theta0"/> long at
    /// <paramref name="f0"/> on the middle strip's own line (its neighbours grounded, εeff = Ckk/C₀kk), less its open
    /// end's extension; then each strip's loading C tuning it to f0 with every other strip's loaded end shorted, the
    /// textbook's resonance condition C = Y·cot θ0/ω0 (Matthaei, <i>Microwave J.</i>, vol. 6, Aug. 1963, no DOI) taken on
    /// the array's own susceptance (<see cref="ShortedSusceptance"/>), each mode at its own speed, less the open end's
    /// fringing capacitance the part models. <c>Slope</c> is each resonator's susceptance slope, b = (ω0/2)·dB/dω, there.
    /// </summary>
    public static (double L, double[] C, double[] Slope) ComblineLoading(MultiLineSection section, IReadOnlyList<double> w,
        double theta0, double f0, MicrostripSubstrate sub)
    {
        int n = w.Count;
        double omega = 2 * Math.PI * f0, l = ComblineLength(section, w, theta0, omega, sub);
        double[,] b = ShortedSusceptance(section, l, omega), b2 = ShortedSusceptance(section, l, 1.001 * omega);
        double[] open = [.. w.Select(x =>
        {
            var p = BranchLinePlan.Line(x, sub).LineParameters(f0);
            return MicrostripOpenEnd.Capacitance(Extension(x, sub), p.Eeff, p.Z0);
        })];
        double[] c = [.. Enumerable.Range(0, n).Select(k => -b[k, k] / omega - open[k])];
        return (l, c, [.. Enumerable.Range(0, n).Select(k => omega / 2 * (c[k] + open[k] + (b2[k, k] - b[k, k]) / (0.001 * omega)))]);
    }

    /// <summary>The strips' length that makes the middle one <paramref name="theta0"/> long, its open end's extension
    /// counted.</summary>
    public static double ComblineLength(MultiLineSection section, IReadOnlyList<double> w, double theta0, double omega,
        MicrostripSubstrate sub)
    {
        int mid = w.Count / 2;
        return theta0 * C0 / (omega * Math.Sqrt(section.C[mid, mid] / section.C0[mid, mid])) - Extension(w[mid], sub);
    }

    /// <summary>
    /// The strips' widths and gaps whose solved cross-section has the design's admittances (S, normalised to
    /// 1/<paramref name="z0"/>): each strip's diagonal of the characteristic admittance matrix (<see cref="Admittance"/>)
    /// its admittance to ground plus its adjacent mutuals, and each adjacent pair's mutual what
    /// <paramref name="mutualOf"/> reads off the widths and the section, by default the matrix's off-diagonal. For a
    /// homogeneous dielectric that is v·C, the design's own TEM form. A secant walk in the logarithms from strips 2H wide
    /// and 0.6H apart, each unknown on its own measure (a width on its strip's diagonal, which a gap barely moves; a gap on
    /// its pair's mutual), each step one field solve; refused when it does not settle to 0.3 %. Each step is on the plan's
    /// grid (<see cref="PassivePlan.OnGrid"/>), as the part draws and solves its strips, so a run of the realised part finds
    /// the last solve in <see cref="MicrostripMultiLineModel.Solve"/>'s cache. <paramref name="cancel"/> stops it between
    /// solves.
    /// </summary>
    public static (double[] W, double[] S, MultiLineSection Section) Fit(double[] ground, double[] mutual, double z0,
        MicrostripSubstrate sub, Func<double[], MultiLineSection, double[]>? mutualOf = null, CancellationToken cancel = default)
    {
        int n = ground.Length;
        mutualOf ??= (_, section) => { var y = Admittance(section); return [.. Enumerable.Range(0, n - 1).Select(k => -y[k, k + 1])]; };
        double[] target = [.. Enumerable.Range(0, n).Select(k => ground[k] + (k > 0 ? mutual[k - 1] : 0) + (k < n - 1 ? mutual[k] : 0))
                                .Concat(mutual).Select(y => y / z0)];
        double[] x = [.. Enumerable.Repeat(2 * sub.HMeters, n), .. Enumerable.Repeat(0.6 * sub.HMeters, n - 1)];
        // d ln(measure)/d ln(unknown): a wider strip has more admittance, a wider gap less mutual. Updated by the secant.
        // ponytail: one secant per unknown, the cross terms left out; a full Jacobian (one solve per unknown) if a stack
        // is found where the walk cycles.
        double[] slope = [.. Enumerable.Range(0, 2 * n - 1).Select(i => i < n ? 1.25 : -1.25)];
        double[]? lastX = null, lastOwn = null;
        for (int it = 0; it < 30; it++)
        {
            cancel.ThrowIfCancellationRequested();
            x = [.. x.Select(PassivePlan.OnGrid)];
            var section = MicrostripMultiLineModel.Solve(x[..n], x[n..], sub);
            var yc = Admittance(section);
            double[] own = [.. Enumerable.Range(0, n).Select(k => yc[k, k]), .. mutualOf(x[..n], section)];
            if (own.Zip(target).All(p => Math.Abs(p.First / p.Second - 1) < 0.003)) return (x[..n], x[n..], section);
            for (int i = 0; i < x.Length; i++)
            {
                if (lastX is not null && Math.Log(x[i] / lastX[i]) is var dx && Math.Abs(dx) > 1e-6)
                {
                    double p = Math.Log(own[i] / lastOwn![i]) / dx;
                    if (p * slope[i] > 0) slope[i] = Math.Sign(p) * Math.Clamp(Math.Abs(p), 0.2, 5.0);
                }
            }
            lastX = [.. x];
            lastOwn = own;
            for (int i = 0; i < x.Length; i++) x[i] *= Math.Clamp(Math.Exp(Math.Log(target[i] / own[i]) / slope[i]), 0.5, 2.0);
            // The design is mirror-symmetric; the solver's grid is not quite, so the walk keeps it so.
            for (int k = 0; k < n / 2; k++) x[k] = x[n - 1 - k] = (x[k] + x[n - 1 - k]) / 2;
            for (int k = 0; k < (n - 1) / 2; k++) x[n + k] = x[2 * n - 2 - k] = (x[n + k] + x[2 * n - 2 - k]) / 2;
            if (x.Any(v => !(v > 1e-3 * sub.HMeters && v < 100 * sub.HMeters))) break;
        }
        throw Refuse("unrealisable", "No strips on this substrate have the design's admittances: the fit's widths and gaps "
            + "left 0.001 to 100 substrate heights, or did not settle in 30 solves. A narrower band, or another order, asks for "
            + "less coupling.");
    }

    /// <summary>The lossless characteristic admittance matrix, Yc = L⁻¹·Tv·diag(1/v_m)·Tv⁻¹ with L⁻¹ = c²·C₀ and Tv
    /// diagonalising L·C: the current a voltage wave drives, symmetric. (The factors the other way round agree only in a
    /// homogeneous medium.)</summary>
    public static double[,] Admittance(MultiLineSection section) => Modal(section, m => 1 / section.Modes.PhaseVelocity(m));

    /// <summary>
    /// The susceptance matrix of the strips <paramref name="length"/> long, shorted at one end and seen from the other, at
    /// <paramref name="omega"/>: B = −L⁻¹·Tv·diag(cot(ω·ℓ/v_m)/v_m)·Tv⁻¹, each mode at its own speed. In a homogeneous
    /// medium −Yc·cot θ, whose off-diagonal is a combline pair's inverter, J = Y_m·cot θ.
    /// </summary>
    public static double[,] ShortedSusceptance(MultiLineSection section, double length, double omega)
        => Modal(section, m => -1 / Math.Tan(omega * length / section.Modes.PhaseVelocity(m)) / section.Modes.PhaseVelocity(m));

    /// <summary>L⁻¹·Tv·diag(f(m))·Tv⁻¹, L⁻¹ = c²·C₀.</summary>
    private static double[,] Modal(MultiLineSection section, Func<int, double> f)
    {
        var m = section.Modes;
        int n = section.C.RowCount;
        double[] d = [.. Enumerable.Range(0, n).Select(f)];
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                a[i, j] = Enumerable.Range(0, n).Sum(k => m.Tv[i, k] * d[k] * m.TvInv[k, j]);
        var y = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                y[i, j] = Enumerable.Range(0, n).Sum(k => C0 * C0 * section.C0[i, k] * a[k, j]);
        return y;
    }

    /// <summary>An open strip end's extension on <paramref name="sub"/> (Kirschning, Jansen and Koster, as MLIN's own).</summary>
    public static double Extension(double w, MicrostripSubstrate sub)
        => MicrostripOpenEnd.Extension(w, sub.HMeters, sub.EpsR, BranchLinePlan.Line(w, sub).LineParameters(0.0).EeffStatic);

    /// <summary>The refusal shared by the checks: the spec a coupled array takes, or why not.</summary>
    internal static Diagnostic? Unsupported(FilterSpec spec, string form)
    {
        if (spec.Band != FilterBand.Bandpass)
            return Diagnostic.Create("synthesis.mcla.band", DiagnosticSeverity.Error,
                "A {form} array is a bandpass filter; {band} is not one.", ("form", form), ("band", spec.Band));
        if (spec.TransmissionZerosHz is { Count: > 0 })
            return Diagnostic.Create("synthesis.mcla.zeros", DiagnosticSeverity.Error,
                "A {form} array couples neighbours only, so it places no transmission zeros.", ("form", form));
        if (!(spec.F2Hz > spec.F1Hz && spec.F1Hz > 0))
            return Diagnostic.Create("synthesis.mcla.edges", DiagnosticSeverity.Error,
                "The band's edges F1 = {f1} Hz and F2 = {f2} Hz must be positive, F2 above F1.", ("f1", spec.F1Hz), ("f2", spec.F2Hz));
        try { LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb); }
        catch (Exception e) when (e is NotSupportedException or ArgumentOutOfRangeException)
        {
            return Diagnostic.Create("synthesis.mcla.prototype", DiagnosticSeverity.Error,
                "A {form} array realises an all-pole ladder prototype: {why}", ("form", form), ("why", e.Message));
        }
        return null;
    }

    internal static ParameterRefusalException Refuse(string code, string template, params (string Name, object? Value)[] args)
        => new(Diagnostic.Create("synthesis.mcla." + code, DiagnosticSeverity.Error, template, args));

    internal static string Mm(double m) => (m * 1e3).ToString("0.######", CultureInfo.InvariantCulture) + "mm";

    internal static string Pf(double f) => (f * 1e12).ToString("0.######", CultureInfo.InvariantCulture) + "pF";

    /// <summary>The parameters as the part stores them: N, each strip's and gap's own row, L, Form and Feed.</summary>
    internal static Dictionary<string, string> Parameters(CoupledArraySpec a)
    {
        var p = new Dictionary<string, string> { ["N"] = a.N.ToString(CultureInfo.InvariantCulture) };
        for (int k = 0; k < a.N; k++) p[$"W[{k + 1}]"] = Mm(a.W[k]);
        for (int k = 0; k < a.N - 1; k++) p[$"S[{k + 1}]"] = Mm(a.S[k]);
        p["L"] = Mm(a.L);
        p["Form"] = a.Form.ToString();
        p["Feed"] = a.Feed.ToString();
        return p;
    }

    /// <summary>The plan's sites as the realisation lists them: a via on each shorted end, the strip's C on each loaded one.</summary>
    internal static SiteValue[] Sites(CoupledArraySpec a, MicrostripSubstrate sub)
        => [.. CoupledArrayPlan.Build(a, sub).Plan.Sites.Select(x => new SiteValue(x.Role.ToString(), x.PinA, x.PinB, x.Value ?? 0))];
}

/// <summary>
/// An interdigital bandpass filter on MCLA, coupled feed: n quarter-wave resonators between two terminating strips, from
/// Matthaei's closed forms (<see cref="CoupledArraySynthesis.Interdigital"/>) and the fitted cross-section. Every strip
/// is a quarter wave at the band's arithmetic centre on the modes' mean εeff, less the middle strip's open-end extension.
/// </summary>
public sealed class CoupledArrayInterdigitalRealiser : IFilterRealiser
{
    /// <summary>Matthaei's admittance scale h for the resonators: the interior strips' width.</summary>
    public double H { get; init; } = 0.15;

    /// <summary>The widest band, over its arithmetic centre, Matthaei's narrow-and-moderate procedure is used to: an
    /// octave.</summary>
    public const double MaxBandwidth = 2.0 / 3.0;

    public string Name => "Interdigital bandpass (MCLA)";

    public bool Supports(FilterSpec spec, out string? why)
    {
        why = Refusal(spec)?.Render();
        return why is null;
    }

    private static Diagnostic? Refusal(FilterSpec spec)
    {
        if (CoupledArraySynthesis.Unsupported(spec, "interdigital") is { } d) return d;
        double w = 2 * (spec.F2Hz - spec.F1Hz) / (spec.F1Hz + spec.F2Hz);
        return w <= MaxBandwidth ? null : Diagnostic.Create("synthesis.mcla.band-too-wide", DiagnosticSeverity.Error,
            "The band is {w} of its centre; the interdigital closed forms hold to an octave ({max}).",
            ("w", w.ToString("P0", CultureInfo.InvariantCulture)), ("max", MaxBandwidth.ToString("P0", CultureInfo.InvariantCulture)));
    }

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate) => Realise(spec, substrate, default);

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate, CancellationToken cancel)
    {
        if (Refusal(spec) is { } d) throw new ParameterRefusalException(d);
        var sub = substrate ?? throw CoupledArraySynthesis.Refuse("substrate", "An MCLA is sized on a substrate; none was given.");
        double f0 = (spec.F1Hz + spec.F2Hz) / 2, w = (spec.F2Hz - spec.F1Hz) / f0;
        var (ground, mutual) = CoupledArraySynthesis.Interdigital(LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb), w, H);
        if (ground.Concat(mutual).Any(y => !(y > 0)))
            throw CoupledArraySynthesis.Refuse("scale", "Matthaei's h = {h} leaves a strip with no admittance to ground; a "
                + "smaller h gives the terminating strips room.", ("h", H));
        var (ws, gaps, section) = CoupledArraySynthesis.Fit(ground, mutual, spec.Z0, sub, cancel: cancel);
        int mid = ws.Length / 2;
        double l = CoupledArraySynthesis.C0 / Math.Sqrt(section.Modes.Eeff.Average()) / (4 * f0) - CoupledArraySynthesis.Extension(ws[mid], sub);
        var a = new CoupledArraySpec(ws, gaps, l, CoupledArrayForm.Interdigital, CoupledArrayFeed.Coupled, null, 0, 0,
            [.. Enumerable.Repeat(CoupledArrayPlan.DefaultC, ws.Length)]);
        return new FilterRealisation(ComponentModelFactory.CoupledArrayTypeName, CoupledArraySynthesis.Parameters(a), CoupledArraySynthesis.Sites(a, sub),
            [$"Matthaei's interdigital closed forms (h = {H.ToString(CultureInfo.InvariantCulture)}), fitted on the quasi-static "
             + "cross-section; microstrip's modes are not TEM, so expect the centre within a few per cent and the band within 20 %."]);
    }
}

/// <summary>
/// A combline bandpass filter on MCLA, tapped: n strips shorted at their near ends and loaded at their far ends, from
/// Matthaei's 1963 design (<see cref="CoupledArraySynthesis.Combline"/>), fitted with each pair's mutual read as its
/// susceptance at the centre, so the modes' unequal speeds count; every strip θ0 long on the middle one's line and each
/// loading C tuning its strip to the centre (<see cref="CoupledArraySynthesis.ComblineLoading"/>). The tap is where the
/// end resonator's external Q is the prototype's, Qe = g0·g1/w: the port's conductance seen at the loaded end,
/// Y0·sin²θt/sin²θ, over the slope parameter b. The end resonators' C then gives back the pull of the port's conductance.
/// </summary>
public sealed class CoupledArrayComblineRealiser : IFilterRealiser
{
    /// <summary>The resonators' electrical length at the centre, in degrees: Matthaei's 45°.</summary>
    public double ThetaDeg { get; init; } = 45.0;

    /// <summary>Each resonator's admittance with its neighbours grounded, over the terminations' 1/Z0.</summary>
    public double ResonatorAdmittance { get; init; } = 1.0;

    public string Name => "Combline bandpass (MCLA)";

    public bool Supports(FilterSpec spec, out string? why)
    {
        why = Refusal(spec)?.Render();
        return why is null;
    }

    private Diagnostic? Refusal(FilterSpec spec)
    {
        if (CoupledArraySynthesis.Unsupported(spec, "combline") is { } d) return d;
        if (spec.Order < 2)
            return Diagnostic.Create("synthesis.mcla.order", DiagnosticSeverity.Error,
                "A tapped combline has at least two resonators, one tapped at each port.");
        if (!(ThetaDeg > 0 && ThetaDeg < 90))
            return Diagnostic.Create("synthesis.mcla.theta", DiagnosticSeverity.Error,
                "θ0 = {t}° must lie between 0° and 90°: below a quarter wave, so the capacitor has a line to load.",
                ("t", ThetaDeg));
        double t0 = ThetaDeg * Math.PI / 180, sin2 = TapSin2(spec, ResonatorAdmittance * CoupledArraySynthesis.ComblineSlope(t0), t0);
        return sin2 < Math.Pow(Math.Sin(t0), 2) ? null : Diagnostic.Create("synthesis.mcla.band-too-wide", DiagnosticSeverity.Error,
            "The band asks for an external Q of {q} that a tap on a {t}° resonator cannot give: even at the open end it loads "
            + "less. A narrower band, a longer θ0 or a lower resonator admittance reach it.",
            ("q", Qe(spec).ToString("G3", CultureInfo.InvariantCulture)), ("t", ThetaDeg));
    }

    private static double Qe(FilterSpec spec)
    {
        var g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
        return g[0] * g[1] / spec.FractionalBandwidth;
    }

    /// <summary>sin²θt for the tap: b·sin²θ0/(Y0·Qe), <paramref name="b"/> in units of Y0.</summary>
    private static double TapSin2(FilterSpec spec, double b, double theta0) => b * Math.Pow(Math.Sin(theta0), 2) / Qe(spec);

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate) => Realise(spec, substrate, default);

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate, CancellationToken cancel)
    {
        if (Refusal(spec) is { } d) throw new ParameterRefusalException(d);
        var sub = substrate ?? throw CoupledArraySynthesis.Refuse("substrate", "An MCLA is sized on a substrate; none was given.");
        double f0 = spec.CentreHz, w = spec.FractionalBandwidth, theta0 = ThetaDeg * Math.PI / 180;
        var (ground, mutual) = CoupledArraySynthesis.Combline(LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb),
            w, theta0, ResonatorAdmittance);
        if (ground.Any(y => !(y > 0)))
            throw CoupledArraySynthesis.Refuse("scale", "The couplings the band asks for exceed the resonators' own admittance, "
                + "leaving a strip none to ground; a narrower band reaches it.");
        // Each pair's mutual as its inverter at the centre, B_(k,k+1)·tan θ0 on the strips' own length: Matthaei's
        // Y_(k,k+1) in a homogeneous medium, and with the modes' unequal speeds, which couple a combline pair more, counted.
        double omega = 2 * Math.PI * f0;
        double[] Inverters(double[] wk, MultiLineSection x)
        {
            var b = CoupledArraySynthesis.ShortedSusceptance(x, CoupledArraySynthesis.ComblineLength(x, wk, theta0, omega, sub), omega);
            return [.. Enumerable.Range(0, wk.Length - 1).Select(k => b[k, k + 1] * Math.Tan(theta0))];
        }
        var (ws, gaps, section) = CoupledArraySynthesis.Fit(ground, mutual, spec.Z0, sub, Inverters, cancel);
        int n = ws.Length;
        var (l, c, slope) = CoupledArraySynthesis.ComblineLoading(section, ws, theta0, f0, sub);

        // The tap's electrical distance from the short, on the end strip's own line, whose loaded end is θ = β·L from it.
        double beta = omega * Math.Sqrt(section.C[0, 0] / section.C0[0, 0]) / CoupledArraySynthesis.C0;
        double tap = Math.Asin(Math.Sqrt(TapSin2(spec, slope[0] * spec.Z0, beta * l))) / beta;
        if (!(tap < l))
            throw CoupledArraySynthesis.Refuse("band-too-wide", "The end resonators' external Q needs a tap past their open "
                + "ends; a narrower band reaches it.");
        // The port's conductance at the tap also pulls the end resonator's frequency (0.7 % up at an external Q of 10):
        // its loaded end's susceptance on the end strip's own line, short to tap to end, with and without it, and the
        // difference taken off its C.
        double ye = CoupledArraySynthesis.C0 * Math.Sqrt(section.C0[0, 0] * section.C[0, 0]), t2 = Math.Tan(beta * (l - tap));
        Complex AtEnd(double g)
        {
            var yt = new Complex(g, -ye / Math.Tan(beta * tap));
            return ye * (yt + Complex.ImaginaryOne * ye * t2) / (ye + Complex.ImaginaryOne * yt * t2);
        }
        c[0] = c[n - 1] = c[0] - (AtEnd(1 / spec.Z0) - AtEnd(0)).Imaginary / omega;
        double wf = LadderPlan.Width(spec.Z0, f0, sub);
        var a = new CoupledArraySpec(ws, gaps, l, CoupledArrayForm.Combline, CoupledArrayFeed.Tapped, null, tap, wf, c);
        var p = CoupledArraySynthesis.Parameters(a);
        p["Tap"] = CoupledArraySynthesis.Mm(tap);
        p["Wf"] = CoupledArraySynthesis.Mm(wf);
        for (int k = 0; k < n; k++) p[$"C[{k + 1}]"] = CoupledArraySynthesis.Pf(c[k]);
        return new FilterRealisation(ComponentModelFactory.CoupledArrayTypeName, p, CoupledArraySynthesis.Sites(a, sub),
            [$"Matthaei's combline at θ0 = {ThetaDeg.ToString(CultureInfo.InvariantCulture)}°, fitted on the quasi-static "
             + "cross-section; the loading capacitors set the centre, the gaps and the tap the band."]);
    }
}
