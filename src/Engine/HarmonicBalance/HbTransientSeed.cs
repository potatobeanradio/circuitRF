// ================================================================
//  HbTransientSeed.cs  —  transient-assisted harmonic balance (docs/design/transient.md §10; brief TRN-8)
//
//  A harmonic-balance Newton normally starts from the DC point with the drive stepped
//  up. A circuit whose steady state is far from that path starts better from a few
//  periods of the native transient run from rest at full drive: the last period,
//  transformed onto the harmonics, is the Newton's starting point, and harmonic
//  balance then solves the real circuit to its own tolerance. The seed is never the
//  result.
//
//  Fast bias. An RF choke or block settles over its time constant with the rest of
//  the bias network, which can be thousands of carrier periods. Each inductor whose
//  reactance at the fundamental is above 50x the impedance it sees there, and each
//  capacitor whose reactance is below 1/50 of it (the circuit linearised at its DC point),
//  is scaled in the seed run only, to where the ratio is 50: still a choke or a block,
//  with a time constant against that impedance of 50/(2 pi) = 8 periods. A part already
//  between 10x and 50x is left alone, since scaling it to 50 would slow it. Harmonic
//  balance solves the unscaled circuit.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Transient;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary><c>InitialGuess=</c> on <c>hb</c> and <c>osc</c> (note §10): the DC start, a transient seed, or the DC start with the
/// seed when it fails.</summary>
public enum HbInitialGuess { Dc, Transient, Fallback }

/// <summary>The transient seed's keys, resolved (brief TRN-8 R-trn8-1/2/5).</summary>
public sealed record HbSeedSettings
{
    public HbInitialGuess Mode { get; init; } = HbInitialGuess.Dc;
    /// <summary><c>SeedTol</c>: how closely consecutive periods must agree, relative to each waveform's peak.</summary>
    public double Tol { get; init; } = 1e-3;
    /// <summary><c>SeedPeriods</c>: a fixed number of periods, or null for <c>auto</c> (until settled, at most <see cref="AutoCap"/>).</summary>
    public int? Periods { get; init; }
    /// <summary><c>SeedFastBias</c>: scale chokes and blocks in the seed run (R-trn8-2).</summary>
    public bool FastBias { get; init; } = true;
    /// <summary><c>KickV</c> (osc only): the kick at the OscProbe's node, volts; null for 1 % of the largest DC supply.</summary>
    public double? KickV { get; init; }

    /// <summary>The most periods an <c>auto</c> seed runs.</summary>
    public const int AutoCap = 200;
    /// <summary>The most periods a stated <c>SeedPeriods</c> may ask for.</summary>
    public const int MaxPeriods = 100_000;

    /// <summary>The DC start: no seed.</summary>
    public static HbSeedSettings Off { get; } = new();

    public bool Seeds => Mode != HbInitialGuess.Dc;

    /// <summary>
    /// The directive's words resolved, every malformed key refused by name. <paramref name="num"/> evaluates an expression
    /// (NaN when it cannot be).
    /// </summary>
    public static HbSeedSettings Resolve(string who, string initialGuess, string seedTol, string seedPeriods, string seedFastBias,
                                         string kickV, Func<string, double> num)
    {
        var mode = (initialGuess ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "dc" => HbInitialGuess.Dc,
            "transient" => HbInitialGuess.Transient,
            "fallback" => HbInitialGuess.Fallback,
            _ => throw new InvalidOperationException($"{who}: InitialGuess must be dc, transient or fallback, not '{initialGuess}'."),
        };
        double tol = string.IsNullOrWhiteSpace(seedTol) ? 1e-3 : num(seedTol.Trim());
        if (!(tol > 0) || !(tol < 1))
            throw new InvalidOperationException($"{who}: SeedTol must be a number between 0 and 1, not '{seedTol}'.");
        int? periods = null;
        string pt = (seedPeriods ?? "").Trim();
        if (pt.Length > 0 && !pt.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            double n = num(pt);
            if (!(n >= 3) || n != Math.Floor(n) || n > MaxPeriods)
                throw new InvalidOperationException(
                    $"{who}: SeedPeriods must be auto or a whole number of periods from 3 to {MaxPeriods}, not '{seedPeriods}'.");
            periods = (int)n;
        }
        bool fast = (seedFastBias ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "yes" or "true" or "1" or "on" => true,
            "no" or "false" or "0" or "off" => false,
            _ => throw new InvalidOperationException($"{who}: SeedFastBias must be yes or no, not '{seedFastBias}'."),
        };
        double? kick = null;
        if (!string.IsNullOrWhiteSpace(kickV) && !kickV.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            double k = num(kickV.Trim());
            if (!double.IsFinite(k) || k == 0)
                throw new InvalidOperationException($"{who}: KickV must be a non-zero voltage or auto, not '{kickV}'.");
            kick = k;
        }
        return new HbSeedSettings { Mode = mode, Tol = tol, Periods = periods, FastBias = fast, KickV = kick };
    }

    /// <summary>One line naming the settings, for explain and the run's note.</summary>
    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "InitialGuess={0}, SeedTol={1:G3}, SeedPeriods={2}, SeedFastBias={3}",
        Mode.ToString().ToLowerInvariant(), Tol, Periods is { } n ? n.ToString(CultureInfo.InvariantCulture) : "auto",
        FastBias ? "yes" : "no");
}

/// <summary>One element the seed run scaled (R-trn8-2): its path, the ratio its reactance had to the impedance it sees at the
/// fundamental, and the factor its value was multiplied by in the seed run only.</summary>
public sealed record HbSeedScaled(string Path, string Kind, double Ratio, double Factor);

/// <summary>A transient seed: the last period of each recorded unknown on the harmonics, and how the run went.</summary>
public sealed class HbSeed
{
    /// <summary>The recorded unknowns' rows (a node's index − 1; a branch row as stamped).</summary>
    public required int[] Rows { get; init; }
    /// <summary>[row, k] full-amplitude phasors of the last period, k = 0..K, on the run's own time origin.</summary>
    public required Complex[,] Spectrum { get; init; }
    /// <summary>[row][m] the last period's samples, m = 0..M−1.</summary>
    public required double[][] LastPeriod { get; init; }
    public required double PeriodS { get; init; }
    public required int Periods { get; init; }
    public required bool Settled { get; init; }
    public required IReadOnlyList<HbSeedScaled> Scaled { get; init; }
    public long TransientSteps { get; init; }
    /// <summary>The worst remaining change, relative, at the last check (what Settled compared with SeedTol).</summary>
    public double Remaining { get; init; }

    /// <summary>The phasors of node <paramref name="node"/>, or null when it was not recorded.</summary>
    public Complex[]? Node(int node)
    {
        int i = Array.IndexOf(Rows, node - 1);
        if (i < 0) return null;
        int k1 = Spectrum.GetLength(1);
        var s = new Complex[k1];
        for (int k = 0; k < k1; k++) s[k] = Spectrum[i, k];
        return s;
    }
}

/// <summary>The transient seed of note §10.2 and §10.3 (brief TRN-8).</summary>
public static class HbTransientSeed
{
    /// <summary>Output samples per period per retained harmonic: the step is T / (8 (K + 1)).</summary>
    public const int SamplesPerHarmonic = 8;

    /// <summary>|X| over the impedance seen above which an inductor is a choke (below the inverse, a capacitor a block).</summary>
    public const double ChokeRatio = 10;

    /// <summary>The ratio a scaled choke or block is brought to; only a part beyond it is scaled (one between <see cref="ChokeRatio"/>
    /// and this already settles faster than a scaled one would).</summary>
    public const double ScaledRatio = 50;

    /// <summary>The note every seeded harmonic balance carries (R-trn8-6).</summary>
    public const string Note = "harmonic balance started from a transient seed";

    /// <summary>Output samples per period for <paramref name="maxHarm"/> harmonics.</summary>
    public static int SamplesPerPeriod(int maxHarm) => SamplesPerHarmonic * (Math.Max(1, maxHarm) + 1);

    /// <summary>
    /// The chokes and blocks of <paramref name="nl"/> at <paramref name="f0Hz"/> (R-trn8-2): every plain inductor whose reactance is
    /// above <see cref="ChokeRatio"/> times the impedance it sees, and every capacitor whose reactance is below its inverse, the
    /// circuit linearised about its DC point; each beyond <see cref="ScaledRatio"/> with the factor that brings the ratio to it. Inductors
    /// a Mutual couples are left alone (their coupling is stated against their value).
    /// </summary>
    public static List<(int Index, HbSeedScaled Scale)> FastBiasPlan(ElaboratedNetlist nl, AnalysisSettings settings, double f0Hz)
    {
        var plan = new List<(int, HbSeedScaled)>();
        double[] dcV;
        try { dcV = NonlinearDcEngine.Run(nl, settings).NodeVoltages; }
        catch (NonlinearDcNotConvergedException) { return plan; }
        bool coupled = nl.Components.Any(c => c.Model is MutualInductanceModel);
        double w = 2 * Math.PI * f0Hz;
        var lu = OscEngine.LinearizedMna(nl, null, dcV, w, settings, out var mna);
        var b = new Complex[mna.Size];
        var x = new Complex[mna.Size];
        for (int i = 0; i < nl.Components.Count; i++)
        {
            var ec = nl.Components[i];
            if (ec.Nodes.Length < 2 || ec.Multiplicity != 1.0) continue;
            Complex ze;
            string kind;
            if (ec.Model is InductorModel && !coupled && !ec.Parameters.ContainsKey("C") && Real(ec, "L") is { } l && l > 0)
            {
                ze = new Complex(Real(ec, "R") ?? 0, w * l);
                kind = "L";
            }
            else if (ec.Model is CapacitorModel && Real(ec, "C") is { } c && c > 0)
            {
                ze = 1 / new Complex(0, w * c);
                kind = "C";
            }
            else continue;
            int na = ec.Nodes[0], nb = ec.Nodes[1];
            if (na == nb) continue;
            Array.Clear(b);
            if (na > 0) b[na - 1] += 1;
            if (nb > 0) b[nb - 1] -= 1;
            lu.Solve(b, x);
            Complex zWith = (na > 0 ? x[na - 1] : 0) - (nb > 0 ? x[nb - 1] : 0);
            if (zWith == Complex.Zero) continue;
            Complex ySee = 1 / zWith - 1 / ze;
            if (!(ySee.Magnitude > 1e-15)) continue;              // it sees an open: nothing to settle against at RF
            double zSee = 1 / ySee.Magnitude;
            double ratio = kind == "L" ? Math.Abs(w * Real(ec, "L")!.Value) / zSee : zSee * w * Real(ec, "C")!.Value;
            if (!double.IsFinite(ratio) || ratio <= ScaledRatio) continue;
            plan.Add((i, new HbSeedScaled(ec.InstancePath, kind, ratio, ScaledRatio / ratio)));
        }
        return plan;
    }

    /// <summary>Puts each element of <see cref="FastBiasPlan"/> into <paramref name="nl"/> at its scaled value (the same model, a copy
    /// of its parameters), adding it to <paramref name="scaled"/>; returns the originals for <see cref="Restore"/>.</summary>
    internal static List<(int Index, ElaboratedComponent Original)> ScaleChokesAndBlocks(ElaboratedNetlist nl, AnalysisSettings settings,
                                                                                        double f0Hz, List<HbSeedScaled> scaled)
    {
        var swapped = new List<(int, ElaboratedComponent)>();
        foreach (var (i, sc) in FastBiasPlan(nl, settings, f0Hz))
        {
            var ec = nl.Components[i];
            string key = sc.Kind == "L" ? "L" : "C";
            var pars = new Dictionary<string, Value>(ec.Parameters.Count, StringComparer.Ordinal);
            foreach (var (k, v) in ec.Parameters) pars[k] = v;
            pars[key] = new Value(ec.Parameters[key].AsReal() * sc.Factor);
            swapped.Add((i, ec));
            nl.Components[i] = new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, pars, ec.Model)
            {
                ReferenceNode = ec.ReferenceNode, Multiplicity = ec.Multiplicity, NoiseOff = ec.NoiseOff, NoiseOn = ec.NoiseOn,
            };
            scaled.Add(sc);
        }
        return swapped;
    }

    /// <summary>Every element <see cref="ScaleChokesAndBlocks"/> replaced, back as it was.</summary>
    internal static void Restore(ElaboratedNetlist nl, List<(int Index, ElaboratedComponent Original)> swapped)
    {
        for (int j = swapped.Count - 1; j >= 0; j--) nl.Components[swapped[j].Index] = swapped[j].Original;
    }

    private static double? Real(ElaboratedComponent ec, string key)
        => ec.Parameters.TryGetValue(key, out var v) && v.Kind == ValueKind.Real ? v.AsReal() : null;

    /// <summary>
    /// Runs the seed: the native transient from the DC point (<paramref name="ic"/> held through it and released at t = 0⁺), at
    /// every source's full drive, step T/(8(K+1)), the chokes and blocks scaled when asked; until consecutive periods agree within
    /// SeedTol (auto) or for SeedPeriods periods; then the last period transformed onto harmonics 0..K. <paramref name="rows"/>
    /// picks the unknowns recorded (default the interface nodes). Throws <see cref="TransientRefusedException"/> when the
    /// transient cannot run, naming why.
    /// </summary>
    public static HbSeed Run(ElaboratedNetlist nl, AnalysisSettings settings, double f0Hz, int maxHarm, HbSeedSettings s,
                             string who, IReadOnlyDictionary<string, double>? ic = null, Func<int[], int[]>? rows = null,
                             RunControl? control = null)
    {
        if (!(f0Hz > 0)) throw new TransientRefusedException($"{who}: the seed needs a fundamental frequency.");
        int m = SamplesPerPeriod(maxHarm);
        double T = 1 / f0Hz, h = T / m;
        int cap = s.Periods ?? HbSeedSettings.AutoCap;

        var scaled = new List<HbSeedScaled>();
        var swapped = s.FastBias ? ScaleChokesAndBlocks(nl, settings, f0Hz, scaled) : [];

        var monitor = new SettleMonitor(m, s.Tol, stopWhenSettled: s.Periods is null);
        TransientRunResult r;
        try
        {
            var tp = new TransientParams
            {
                Name = who + " seed", StopS = cap * T, StepS = h, MaxStepS = h, Start = TransientStart.Op,
                IcNets = ic ?? new Dictionary<string, double>(),
                MaxSteps = (long)Math.Max(10_000_000, 64.0 * cap * m),
                RecordRows = rows ?? (ifn => [.. ifn.Select(n => n - 1)]),
                PlayModulation = false,   // brief MTR-1 R-mtr1-5: the steady state, a MOD_Source's CW tone
                StopWhen = monitor.Push,
            };
            r = new TransientEngine(nl, settings).Run(tp, control);
        }
        finally { Restore(nl, swapped); }

        int n = r.Time.Length, nRec = r.NodeNames.Length;
        if (n < m + 1)
            throw new TransientRefusedException($"{who}: the seed run ended before one whole period.");
        var recRows = r.NodeNames.Select(nm => int.Parse(nm.AsSpan(3), CultureInfo.InvariantCulture)).ToArray();
        int k1 = maxHarm + 1;
        var spec = new Complex[nRec, k1];
        var last = new double[nRec][];
        double w0 = 2 * Math.PI * f0Hz;
        for (int c = 0; c < nRec; c++)
        {
            last[c] = new double[m];
            for (int j = 0; j < m; j++) last[c][j] = r.V[n - m + j][c];
            for (int k = 0; k < k1; k++)
            {
                Complex acc = 0;
                for (int j = 0; j < m; j++)
                {
                    double ph = -k * w0 * r.Time[n - m + j];
                    acc += last[c][j] * new Complex(Math.Cos(ph), Math.Sin(ph));
                }
                spec[c, k] = acc * (k == 0 ? 1.0 / m : 2.0 / m);
            }
        }
        // A stated SeedPeriods runs them all; whether its last two periods agree is still reported.
        bool settled = monitor.Settled;
        return new HbSeed
        {
            Rows = recRows, Spectrum = spec, LastPeriod = last, PeriodS = T, Periods = (n - 1) / m, Settled = settled, Scaled = scaled,
            TransientSteps = r.AcceptedSteps, Remaining = monitor.Remaining,
        };
    }

    /// <summary>
    /// The seed's cubes (R-trn8-6), on every point of a run that asks for a seed, so a sweep stacks them: <c>Seed.Periods</c> (0 where
    /// the point was not seeded), <c>Seed.Settled</c> (1 or 0), <c>Seed.HbIterations</c> (the Newton iterations from the seed) and
    /// <c>Seed.ScaledElements [element]</c>, each scaled element's factor, labelled by its path.
    /// </summary>
    public static void AddCubes(RfCore.Data.DataSet ds, HbSeed? seed, int hbIterations)
    {
        ds.Add("Seed.Periods", RfCore.Data.DataCube.Scalar(seed?.Periods ?? 0));
        ds.Add("Seed.Settled", RfCore.Data.DataCube.Scalar(seed is { Settled: true } ? 1 : 0));
        ds.Add("Seed.HbIterations", RfCore.Data.DataCube.Scalar(seed is null ? 0 : hbIterations));
        var sc = seed?.Scaled ?? [];
        ds.Add("Seed.ScaledElements", new RfCore.Data.DataCube(
            [new RfCore.Data.Axis("element", [.. Enumerable.Range(0, sc.Count).Select(i => (double)i)], "", [.. sc.Select(x => x.Path)])],
            [.. sc.Select(x => x.Factor)]));
    }

    /// <summary>The run's note: the seed and how it went (R-trn8-6).</summary>
    public static string Describe(HbSeed seed, int hbIterations, bool converged)
    {
        var inv = CultureInfo.InvariantCulture;
        string scaled = seed.Scaled.Count == 0 ? "no choke or block scaled"
            : "scaled in the seed run only: " + string.Join(", ", seed.Scaled.Select(x => string.Format(inv, "{0} x{1:G3}", x.Path, x.Factor)));
        return string.Format(inv, "{0}: {1} periods, {2}; {3}; harmonic balance {4} in {5} Newton iterations from it.",
            Note, seed.Periods,
            seed.Settled ? "settled" : string.Format(inv, "not settled (consecutive periods still {0:G2} apart, relative)", seed.Remaining),
            scaled, converged ? "converged" : "did not converge", hbIterations);
    }

    /// <summary>The seed spectrum on harmonic balance's interface (<paramref name="ifNodes"/>), [N, K + 1]; an interface node the seed
    /// did not record keeps <paramref name="fallback"/>'s row.</summary>
    public static Complex[,] OnInterface(HbSeed seed, int[] ifNodes, int maxHarm, Complex[,]? fallback = null)
    {
        var v = new Complex[ifNodes.Length, maxHarm + 1];
        for (int i = 0; i < ifNodes.Length; i++)
        {
            var s = seed.Node(ifNodes[i]);
            for (int k = 0; k <= maxHarm; k++)
                v[i, k] = s is not null ? s[k] : fallback is not null && k < fallback.GetLength(1) ? fallback[i, k] : 0;
        }
        return v;
    }

    /// <summary>
    /// Whether the run has settled, checked at every period boundary from the third (R-trn8-1). Per recorded waveform, the change
    /// from one period to the next, Δ (the largest over the period), and how it relates to the previous period's change, ρ (the
    /// least-squares ratio of the two difference waveforms). Settled when, for every waveform, Δ/(1 − ρ) — the change still to
    /// come were it to keep shrinking by ρ each period, with ρ ≤ 0 taken as 0 — is within SeedTol of the waveform's peak over the
    /// last period. A slow drift (a bias network still charging) has ρ near 1 and is not settled however small one period's change
    /// is; numerical noise has ρ near 0 and does not hold the run.
    /// </summary>
    internal sealed class SettleMonitor(int m, double tol, bool stopWhenSettled)
    {
        private readonly List<double[]> _s = [];
        public bool Settled { get; private set; }
        public double Remaining { get; private set; } = double.PositiveInfinity;

        public bool Push(double t, double[] v)
        {
            _s.Add((double[])v.Clone());
            int n = _s.Count;
            if ((n - 1) % m != 0 || n - 1 < 3 * m) return false;
            int nc = v.Length;
            var scale = new double[nc];
            double big = 0;
            for (int c = 0; c < nc; c++)
            {
                for (int j = 0; j < m; j++) scale[c] = Math.Max(scale[c], Math.Abs(_s[n - 1 - j][c]));
                big = Math.Max(big, scale[c]);
            }
            double worst = 0;
            for (int c = 0; c < nc; c++)
            {
                double sc = Math.Max(scale[c], Math.Max(1e-6 * big, 1e-300));
                double delta = 0, dot = 0, prev2 = 0;
                for (int j = 0; j < m; j++)
                {
                    double d1 = _s[n - 1 - j][c] - _s[n - 1 - m - j][c];
                    double d0 = _s[n - 1 - m - j][c] - _s[n - 1 - 2 * m - j][c];
                    delta = Math.Max(delta, Math.Abs(d1));
                    dot += d1 * d0;
                    prev2 += d0 * d0;
                }
                double rho = prev2 > 0 ? Math.Max(0, dot / prev2) : 0;
                double remaining = delta == 0 ? 0 : rho >= 1 ? double.PositiveInfinity : delta / (1 - rho);
                worst = Math.Max(worst, remaining / sc);
            }
            Remaining = worst;
            Settled = worst <= tol;
            return Settled && stopWhenSettled;
        }
    }
}
