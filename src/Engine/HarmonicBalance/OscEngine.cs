using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>An oscillator analysis's resolved keys (brief NZ-9): every frequency in Hz.</summary>
public sealed record OscParams(
    string  Analysis,
    string  Probe,
    double  FguessHz,
    double  FstartHz,
    double  FstopHz,
    int     MaxHarmonic,
    int     FFTOverSample,
    double  Tol,
    double? Aguess)
{
    /// <summary>How many points the start-up search samples across Fstart..Fstop before refining each crossing.</summary>
    public int StartupPoints { get; init; } = 401;

    /// <summary>Brief NZ-10: the phase-noise sweep of <c>PhaseNoise=yes</c>, or null — and a null run is the one from before.</summary>
    public OscPhaseNoiseParams? PhaseNoise { get; init; }
}

/// <summary>The small-signal start-up search: the probe's admittance across the span, and where the start-up condition holds.</summary>
public sealed record OscStartup(
    string      Probe,
    bool        Shunt,
    double[]    FreqsHz,
    Complex[]   Admittance,
    double?     StartHz,
    Complex     Immittance,
    double      Scale)
{
    /// <summary>True when the start-up condition holds somewhere in the span.</summary>
    public bool Starts => StartHz is not null;
}

/// <summary>A solved oscillator: the HB result at the oscillation with the oscillator's own cubes added.</summary>
public sealed record OscResult(DataSet Data, HbRunResult Run, double FoscHz, double Aosc, OscStartup Startup, int Iterations);

/// <summary>
/// Brief NZ-9 — the oscillator analysis: harmonic balance with the fundamental frequency as an unknown.
///
/// <para><b>The method.</b> At the <see cref="OscProbeModel"/> an ideal generator of amplitude A and frequency f is inserted;
/// it is idle at every other frequency (see the model for its two forms). For any trial (A, f) an ordinary driven
/// harmonic-balance solve at f, on the unchanged <see cref="HbEngine"/>, gives the voltage across the probe at the
/// fundamental, and from it the circuit's own large-signal immittance there: the admittance in shunt, the impedance in
/// series. The oscillation is the (A, f) where that immittance is zero — the generator does no work, so the circuit sustains
/// itself. Two real equations in two real unknowns, solved by a small Newton outside the HB solve.</para>
///
/// <para><b>Start-up.</b> The same immittance at vanishing amplitude is the small-signal one, found here from the circuit
/// linearised about its DC operating point. Where its real part is negative at a zero of its imaginary part, the circuit
/// starts. Then the amplitude is ramped from small until the real part turns positive, which brackets the oscillation and
/// keeps the trivial DC solution out: the residual is an immittance per unit amplitude, which at A → 0 is the small-signal
/// one and is not zero.</para>
/// </summary>
public static class OscEngine
{
    /// <summary>Resolves an <c>osc</c> directive against the elaborated globals.</summary>
    public static OscParams Resolve(OscillatorAnalysis a, IReadOnlyDictionary<string, Value> globals,
                                    IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        double Hz(string what, string expr)
        {
            try { return FreqUnit.ResolveHz(expr, a.Unit, globals, globalsWithUnit); }
            catch (ExpressionException ex)
            {
                throw new InvalidOperationException("Oscillator " + FreqUnit.UnresolvedFieldMessage(what, a.Name, expr, ex), ex);
            }
        }
        double Num(string expr, double def)
        {
            if (string.IsNullOrWhiteSpace(expr)) return def;
            try
            {
                var val = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(expr), HbEngine.BuildScope(globals));
                return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
            }
            catch (ExpressionException) { return def; }
        }

        double guess = Hz("Fguess", a.FguessExpr);
        double start = string.IsNullOrWhiteSpace(a.FstartExpr) ? 0.5 * guess : Hz("Fstart", a.FstartExpr);
        double stop  = string.IsNullOrWhiteSpace(a.FstopExpr)  ? 1.5 * guess : Hz("Fstop", a.FstopExpr);
        if (!(start > 0 && start < guess && guess < stop))
            throw new InvalidOperationException(OscDiagnostics.BadSpan(a.Name, start, guess, stop).Render());
        double aGuess = Num(a.AguessExpr, double.NaN);
        return new OscParams(a.Name, a.Probe.Trim(), guess, start, stop,
            Math.Max(1, (int)Num(a.MaxHarmonicExpr, 7)), Math.Max(1, (int)Num(a.FFTOverSampleExpr, 1)), Num(a.TolExpr, 1e-6),
            double.IsFinite(aGuess) && aGuess > 0 ? aGuess : null)
        {
            PhaseNoise = a.PhaseNoise ? ResolvePhaseNoise(a, globals) : null,
        };
    }

    /// <summary>Brief NZ-10: <c>PnStart</c>, <c>PnStop</c>, <c>PnNpts</c>, <c>PnLog</c> and <c>PnPort</c>, each refused by name
    /// when it does not resolve.</summary>
    private static OscPhaseNoiseParams ResolvePhaseNoise(OscillatorAnalysis a, IReadOnlyDictionary<string, Value> globals)
    {
        double Eval(string key, string text)
        {
            try
            {
                var val = HbEngine.BuildEvaluator(globals).EvalExpr(Parser.Parse(text), HbEngine.BuildScope(globals));
                return val.Kind == ValueKind.Real ? val.AsReal() : val.AsComplex().Real;
            }
            catch (ExpressionException)
            {
                throw new InvalidOperationException(OscDiagnostics.PnBadKey(a.Name, key, text).Render());
            }
        }
        double Hz(string key, string text)
        {
            string t = (text ?? "").Trim().Trim('"').Trim();
            double scale = 1;
            if (t.Length > 2 && t.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
            {
                // The prefix's case is its meaning where it has to be: M is mega and m is milli — a 1 mHz offset is where a
                // 1/f³ slope is read, and reading it as 1 MHz is nine decades out. k and G have one meaning either way.
                char p = t[^3];
                (int cut, scale) = p switch
                {
                    'G' or 'g' => (3, 1e9),
                    'M'        => (3, 1e6),
                    'm'        => (3, 1e-3),
                    'k' or 'K' => (3, 1e3),
                    _          => (2, 1.0),
                };
                if (t.Length > cut) t = t[..^cut].Trim();
            }
            double v = t.Length == 0 ? double.NaN : Eval(key, t) * scale;
            if (!(v > 0) || !double.IsFinite(v))
                throw new InvalidOperationException(OscDiagnostics.PnBadKey(a.Name, key, text ?? "").Render());
            return v;
        }
        double lo = Hz("PnStart", a.PnStartExpr), hi = Hz("PnStop", a.PnStopExpr);
        double n = Eval("PnNpts", a.PnNptsExpr);
        if (!(hi > lo) || !(n >= 2) || n != Math.Floor(n))
            throw new InvalidOperationException(OscDiagnostics.PnBadSweep(a.Name, lo, hi, a.PnNptsExpr).Render());
        int? port = null;
        if (!string.IsNullOrWhiteSpace(a.PnPortExpr))
        {
            double pv = Eval("PnPort", a.PnPortExpr);
            if (pv != Math.Floor(pv) || pv < 0)
                throw new InvalidOperationException(OscDiagnostics.PnBadKey(a.Name, "PnPort", a.PnPortExpr).Render());
            port = (int)pv;
        }
        return new OscPhaseNoiseParams(lo, hi, (int)n, a.PnLog, port);
    }

    /// <summary>The OscProbe an analysis runs on: the one <paramref name="probe"/> names, or the only one there is.</summary>
    public static ElaboratedComponent FindProbe(ElaboratedNetlist nl, string analysis, string probe)
    {
        var all = nl.Components.Where(c => c.Model is OscProbeModel).ToList();
        if (probe.Length > 0)
            return all.FirstOrDefault(c => c.InstancePath.Equals(probe, StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException(OscDiagnostics.UnknownProbe(analysis, probe).Render());
        return all.Count switch
        {
            0 => throw new InvalidOperationException(OscDiagnostics.NoProbe(analysis).Render()),
            1 => all[0],
            _ => throw new InvalidOperationException(
                     OscDiagnostics.SeveralProbes(analysis, string.Join(", ", all.Select(c => c.InstancePath))).Render()),
        };
    }

    /// <summary>Brief NZ-10: the instance the phase noise of <paramref name="p"/> is read at — a Port, a Term or the OscProbe —
    /// or the refusal the run gives, thrown. Null when the analysis does not ask for phase noise.</summary>
    public static string? PhaseNoiseReadAt(ElaboratedNetlist nl, OscParams p)
    {
        if (p.PhaseNoise is not { } pn) return null;
        var probe = FindProbe(nl, p.Analysis, p.Probe);
        return OscPhaseNoise.Output(nl, p.Analysis, probe, OscProbeModel.IsShunt(probe), pn.OutPort).Where;
    }

    // ── Start-up ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The small-signal start-up search: the impedance across the probe's terminals with the probe taken out, from the circuit
    /// linearised about its DC operating point, at <see cref="OscParams.StartupPoints"/> frequencies; each sign change of the
    /// imaginary part of the probe's natural immittance (admittance in shunt, impedance in series) refined by bisection; and the
    /// crossing nearest Fguess whose real part is negative. A sign change through a pole rather than a zero is not a crossing.
    /// </summary>
    public static OscStartup Startup(ElaboratedNetlist nl, OscParams p, AnalysisSettings? settings = null)
    {
        settings ??= AnalysisSettings.Default;
        var probe = FindProbe(nl, p.Analysis, p.Probe);
        bool shunt = OscProbeModel.IsShunt(probe);
        var dc = NonlinearDcEngine.Run(nl, settings);
        // The search linearises every device at this point, which reads charge: a linearised-charge core is frozen here
        // first, as every other engine that reads charge freezes it (BiasFreeze).
        BiasFreeze.Apply(nl, dc.Converged ? dc.NodeVoltages : null, dc.NodeVoltages);

        Complex W(double hz)
        {
            var z = ProbeImpedance(nl, probe, dc.NodeVoltages, 2 * Math.PI * hz, settings);
            return shunt ? 1.0 / z : z;
        }

        int n = Math.Max(3, p.StartupPoints);
        var f = new double[n];
        var w = new Complex[n];
        var y = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            f[i] = p.FstartHz + (p.FstopHz - p.FstartHz) * i / (n - 1);
            w[i] = W(f[i]);
            y[i] = shunt ? w[i] : 1.0 / w[i];
        }

        double? best = null;
        Complex bestW = Complex.Zero;
        for (int i = 0; i + 1 < n; i++)
        {
            double lo = f[i], hi = f[i + 1];
            Complex wLo = w[i], wHi = w[i + 1];
            if (Math.Sign(wLo.Imaginary) == Math.Sign(wHi.Imaginary) || wLo.Imaginary == 0 && wHi.Imaginary == 0) continue;
            double bound = 2 * Math.Max(wLo.Magnitude, wHi.Magnitude);
            for (int it = 0; it < 80 && hi - lo > 1e-14 * hi; it++)
            {
                double mid = 0.5 * (lo + hi);
                var wm = W(mid);
                if (Math.Sign(wm.Imaginary) == Math.Sign(wLo.Imaginary)) { lo = mid; wLo = wm; }
                else { hi = mid; wHi = wm; }
            }
            double fz = Math.Abs(wLo.Imaginary) <= Math.Abs(wHi.Imaginary) ? lo : hi;
            var wz = W(fz);
            if (!(wz.Magnitude <= bound) || !(wz.Real < 0)) continue;   // a pole, or no negative resistance
            if (best is null || Math.Abs(fz - p.FguessHz) < Math.Abs(best.Value - p.FguessHz)) { best = fz; bestW = wz; }
        }

        double scale = 0;
        if (best is { } fs)
        {
            double df = 1e-6 * fs;
            scale = (W(fs + df) - W(fs - df)).Magnitude / (2 * df) * fs;
        }
        return new OscStartup(probe.InstancePath, shunt, f, y, best, bestW, scale);
    }

    /// <summary>The impedance between the probe's two terminals, the probe removed and the rest of the circuit linearised about
    /// <paramref name="dcNodeVoltages"/> with every source zeroed, as harmonic balance sees the linear part (a Port or Term is
    /// its termination).</summary>
    internal static Complex ProbeImpedance(ElaboratedNetlist nl, ElaboratedComponent probe, double[] dcNodeVoltages,
                                           double omega, AnalysisSettings settings)
    {
        var mna = new MnaSystem(nl.Nodes.Count - 1);
        foreach (var ec in nl.Components)
        {
            if (ReferenceEquals(ec, probe) || ec.Model is MutualInductanceModel) continue;
            if (ec.Model is PortModel or TermModel)
            {
                if (PortModel.TerminationAdmittance(ec, omega) is { } yPort)
                    mna.AddAdmittance(ec.Nodes[0], ec.Nodes[1], yPort);
                continue;
            }
            if (ec.Model is ModSourceModel ms) { ms.StampSParamDriveTie(mna, ec); ec.Stamp(mna, omega); continue; }
            if (ec.Model is P1ToneModel p1)   { p1.StampSParamDriveTie(mna, ec); ec.Stamp(mna, omega); continue; }
            if (ec.Model.Kind == ModelKind.Nonlinear)
                ec.StampLinearized(mna, omega, SParameterEngine.BuildBias(ec, dcNodeVoltages));
            else
                ec.Stamp(mna, omega);
        }
        foreach (var ec in nl.Components)
            if (ec.Model is MutualInductanceModel) ec.Stamp(mna, omega);
        for (int node = 1; node <= mna.NodeCount; node++)
            mna.AddAdmittance(node, 0, new Complex(settings.Gmin, 0));

        var lu = mna.Factorize();
        var b  = new Complex[mna.Size];
        var x  = new Complex[mna.Size];
        int a = probe.Nodes[0], c = probe.Nodes[1];
        if (a > 0) b[a - 1] += Complex.One;
        if (c > 0) b[c - 1] -= Complex.One;
        lu.Solve(b, x);
        return (a > 0 ? x[a - 1] : Complex.Zero) - (c > 0 ? x[c - 1] : Complex.Zero);
    }

    // ── The oscillation ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the oscillator analysis on <paramref name="nl"/>: the start-up search (refused when the circuit does not start),
    /// the amplitude ramp, the Newton on (A, f), and the converged harmonic balance at the oscillation — whose DataSet is the
    /// one an HB analysis writes, with <c>Fosc</c> (Hz), <c>Aosc</c> (V in shunt, A in series) and <c>Startup [freq]</c>
    /// (the probe's small-signal admittance) added.
    /// </summary>
    public static OscResult Run(ElaboratedNetlist nl, TestBench tb, OscParams p, AnalysisSettings? settings = null,
                                Library? lib = null, string? baseDirectory = null)
    {
        settings ??= AnalysisSettings.Default;
        var startup = Startup(nl, p, settings);
        var probe   = FindProbe(nl, p.Analysis, p.Probe);
        if (startup.StartHz is not { } fs)
            throw new InvalidOperationException(
                OscDiagnostics.NoStartup(p.Analysis, probe.InstancePath, startup.Shunt, p.FstartHz, p.FstopHz).Render());

        var model = (OscProbeModel)probe.Model;
        bool shunt = startup.Shunt;
        // Scale of the probe's immittance: its slope with frequency at the start-up point, in siemens (shunt) or ohms
        // (series). The residual is measured against it, and the generator's own conductance is set from it — large enough
        // to make every driven solve well posed (it outweighs the circuit's negative resistance), and nothing at the answer.
        double scale = Math.Max(startup.Scale, 2 * Math.Abs(startup.Immittance.Real));
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        double gGen = shunt ? scale + 2 * Math.Abs(startup.Immittance.Real)
                            : 1.0 / (scale + 2 * Math.Abs(startup.Immittance.Real));
        string unit = shunt ? "V" : "A";
        string Amp(double a) => a.ToString("G6", CultureInfo.InvariantCulture) + " " + unit;

        var engine = new HbEngine(nl, tb, settings, wspCache: null, lib: lib, baseDirectory: baseDirectory);
        Complex[,]? warm = null;
        int solves = 0;

        // One driven solve: the probe's generator at (A, f), the circuit's immittance at the probe over its scale, and the
        // probe's own voltage at the fundamental.
        (Complex R, Complex[,] V, Complex Vab)? Eval(double a, double hz)
        {
            model.DriveOmega = 2 * Math.PI * hz;
            model.DriveAmplitude = a;
            model.DriveConductance = gGen;
            // The inner tolerance follows the generator: the immittance is read from a voltage the solve settles to within
            // its residual, so that residual is kept a billionth of the generator's own current, or tighter as asked.
            double genCurrent = shunt ? gGen * a : a;
            double tol = Math.Min(p.Tol, 1e-9 * genCurrent);
            var sp = engine.RunSinglePoint(Params(p, hz, tol), warm);
            solves++;
            if (!sp.Converged)
            {
                double res = sp.IterTrace.Count > 0 ? sp.IterTrace[^1].ResidualNorm : double.NaN;
                // The tightened tolerance is ours, not the user's: a solve that reached the analysis's own Tol is the
                // answer HB itself would give, and it is the circuit's conditioning that stopped it there.
                if (!(res < p.Tol) || sp.ToleranceVacuous) return null;
            }
            warm = sp.V;
            var back = sp.BackSolver!;
            Complex vab = back.GetNodeVoltage(probe.Nodes[0], 1, 0) - back.GetNodeVoltage(probe.Nodes[1], 1, 0);
            Complex w = shunt ? gGen * (a - vab) / vab : -vab / (a + gGen * vab);
            return (w / scale, sp.V, vab);
        }

        try
        {
            // ── Ramp the amplitude from small until the negative resistance runs out ──
            double f = fs;
            double small = shunt ? 1e-3 : 1e-3 / scale;
            double a = p.Aguess ?? small;
            var first = Eval(a, f);
            // Aguess is a start, not a promise: at the start-up frequency, which can lie well off the oscillation's, the
            // stated amplitude may not solve, and the ramp from small is then the way in, as it is without one.
            bool ramp = p.Aguess is null || first is null;
            if (first is null && p.Aguess is not null) a = small;
            var r = first ?? Eval(a, f) ?? throw Fail(f, a, "the first small-amplitude solve");
            if (ramp)
            {
                double aPrev = a; Complex rPrev = r.R;
                bool bracketed = r.R.Real >= 0;
                for (int k = 0; k < 80 && !bracketed; k++)
                {
                    aPrev = a; rPrev = r.R;
                    a *= 2;
                    r = Eval(a, f) ?? throw Fail(f, a, "the amplitude ramp");
                    bracketed = r.R.Real >= 0;
                }
                if (!bracketed)
                    throw new InvalidOperationException(OscDiagnostics.NoLimit(p.Analysis, fs, Amp(a)).Render());
                if (rPrev.Real < 0 && aPrev != a)
                {
                    // Interpolate the zero of Re in log-amplitude between the last two rungs.
                    double t = rPrev.Real / (rPrev.Real - r.R.Real);
                    double aStart = Math.Exp(Math.Log(aPrev) + t * (Math.Log(a) - Math.Log(aPrev)));
                    a = aStart;
                    r = Eval(a, f) ?? throw Fail(f, a, "the amplitude ramp");
                }
            }

            // ── Newton on (ln A, f) ──
            const double hA = 1e-5, hF = 1e-8;
            int iter = 0;
            bool done = r.R.Magnitude < 1e-10;
            for (; iter < 60 && !done; iter++)
            {
                var ra = Eval(a * Math.Exp(hA), f) ?? throw Fail(f, a, "a Newton derivative");
                var rf = Eval(a, f * (1 + hF)) ?? throw Fail(f, a, "a Newton derivative");
                Complex dA = (ra.R - r.R) / hA, dF = (rf.R - r.R) / hF;     // per unit ln A, per unit relative f
                // Solve [Re dA, Re dF; Im dA, Im dF]·[du; dv] = −[Re R; Im R].
                double det = dA.Real * dF.Imaginary - dF.Real * dA.Imaginary;
                if (det == 0 || !double.IsFinite(det)) break;
                double du = (-r.R.Real * dF.Imaginary + dF.Real * r.R.Imaginary) / det;
                double dv = (-dA.Real * r.R.Imaginary + dA.Imaginary * r.R.Real) / det;
                du = Math.Clamp(du, -Math.Log(4), Math.Log(4));
                dv = Math.Clamp(dv, -0.05, 0.05);

                double step = 1;
                (Complex R, Complex[,] V, Complex Vab)? next = null;
                double aN = a, fN = f;
                var keep = warm;
                for (int bt = 0; bt < 12; bt++, step *= 0.5)
                {
                    aN = a * Math.Exp(step * du);
                    fN = f * (1 + step * dv);
                    warm = keep;
                    next = Eval(aN, fN);
                    if (next is { } nx && nx.R.Magnitude < r.R.Magnitude) break;
                    next = null;
                }
                if (next is null) { warm = keep; Eval(a, f); break; }
                double moved = Math.Max(Math.Abs(aN / a - 1), Math.Abs(fN / f - 1));
                a = aN; f = fN; r = next.Value;
                done = r.R.Magnitude < 1e-10 || moved < 1e-13;
            }
            if (!(r.R.Magnitude < 1e-7))
                throw new InvalidOperationException(OscDiagnostics.NotConverged(p.Analysis, f, Amp(a), r.R.Magnitude).Render());

            // ── The converged harmonic balance at the oscillation, in the cubes an HB analysis writes ──
            model.DriveOmega = 2 * Math.PI * f; model.DriveAmplitude = a; model.DriveConductance = gGen;
            var run = engine.Run(Params(p, f, Math.Min(p.Tol, 1e-9 * (shunt ? gGen * a : a))), warm);
            var ds = run.DataSet;
            ds.Add("Fosc", new DataCube(Array.Empty<Axis>(), [f]) { Unit = "Hz" });
            ds.Add("Aosc", new DataCube(Array.Empty<Axis>(), [a]) { Unit = unit });
            ds.Add("Startup", new DataCube([new Axis("freq", startup.FreqsHz, "Hz")], startup.Admittance) { Unit = "S" });

            // ── Brief NZ-10: phase noise about this oscillation ──
            if (p.PhaseNoise is { } pn)
            {
                // The outer Newton's Jacobian, by central differences about the answer: what the oscillation's amplitude and
                // frequency do when the residual is pushed. Each column also records the probe's own level (|V| in shunt, the
                // loop current in series), so the circuit's immittance can be told apart from the residual's parametrisation.
                var keep = warm;
                const double dLnA = 1e-4, dRelF = 1e-5;
                (Complex R, Complex[,] V, Complex Vab) At(double aa, double ff)
                {
                    warm = keep;
                    return Eval(aa, ff) ?? throw Fail(ff, aa, "the phase-noise sensitivity");
                }
                var ap = At(a * Math.Exp(dLnA), f);
                var am = At(a * Math.Exp(-dLnA), f);
                var fp = At(a, f * (1 + dRelF));
                var fm = At(a, f * (1 - dRelF));
                double Level(double aa, Complex vab) => shunt ? vab.Magnitude : (aa + gGen * vab).Magnitude;
                Complex rLnA = (ap.R - am.R) / (2 * dLnA);
                Complex rF   = (fp.R - fm.R) / (2 * dRelF * f);
                double lvA = (Math.Log(Level(a * Math.Exp(dLnA), ap.Vab)) - Math.Log(Level(a * Math.Exp(-dLnA), am.Vab))) / (2 * dLnA);
                double lvF = (Math.Log(Level(a, fp.Vab)) - Math.Log(Level(a, fm.Vab))) / (2 * dRelF * f);
                Complex wLnL = rLnA / lvA;
                Complex wF   = rF - wLnL * lvF;

                warm = keep;
                model.DriveOmega = 2 * Math.PI * f; model.DriveAmplitude = a; model.DriveConductance = gGen;
                var point = engine.RunSinglePoint(Params(p, f, Math.Min(p.Tol, 1e-9 * (shunt ? gGen * a : a))), keep);
                if (point.Linearization is null || point.BackSolver is null)
                    throw Fail(f, a, "the phase-noise linearisation");
                Complex vabC = point.BackSolver.GetNodeVoltage(probe.Nodes[0], 1, 0) - point.BackSolver.GetNodeVoltage(probe.Nodes[1], 1, 0);
                OscPhaseNoise.Add(ds, nl, new OscPhaseNoiseInputs(p.Analysis, pn, point, probe, shunt, a, f, vabC, gGen, scale,
                                                                  rLnA, rF, wLnL, wF));
            }
            return new OscResult(ds, run, f, a, startup, iter);
        }
        finally
        {
            model.Disarm();
        }

        InvalidOperationException Fail(double hz, double a, string where)
            => new(OscDiagnostics.SolveFailed(p.Analysis, hz, Amp(a), where).Render());
    }

    private static HbAnalysisParams Params(OscParams p, double hz, double tol)
        => new([hz], p.MaxHarmonic, MaxMixOrder: 1, FFTOverSample: p.FFTOverSample, Tol: tol,
               DriveStepping: DcBiasSteppingMode.IfNecessary, GuardHarmonic: 0,
               SweepVarName: null, SweepStart: 0, SweepStop: 0, SweepStep: 1);
}
