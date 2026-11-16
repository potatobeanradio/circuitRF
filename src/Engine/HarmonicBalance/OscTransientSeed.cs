// ================================================================
//  OscTransientSeed.cs  —  the oscillator's transient seed (docs/design/transient.md §10.3; brief TRN-8 R-trn8-5)
//
//  The circuit is kicked at the OscProbe's node (an initial condition KickV above its
//  DC value, held through the operating point and released at t = 0+) and run with the
//  probe idle. Its rising crossings of the probe signal's own mean mark the cycles; the
//  run stops when the cycle's period and amplitude have settled. The last cycle gives
//  the frequency, the amplitude at the probe (the generator's: volts across a shunt
//  probe, the loop current through a series one) and every interface waveform's
//  harmonics, rotated so the probe's fundamental is real — the generator's phase — which
//  the oscillator analysis then starts its harmonic-balance search from.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Transient;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>An oscillator's transient seed: the oscillation it settled into and the interface's harmonics there.</summary>
public sealed class OscSeed
{
    public required double FreqHz { get; init; }
    /// <summary>The probe generator's amplitude at the oscillation: volts (shunt) or amperes (series).</summary>
    public required double Amplitude { get; init; }
    /// <summary>[N, K + 1] on <see cref="IfNodes"/>, the probe's fundamental real and positive.</summary>
    public required Complex[,] V { get; init; }
    public required int[] IfNodes { get; init; }
    /// <summary>Periods of the time scale the run was stepped at.</summary>
    public required int Periods { get; init; }
    public required int Cycles { get; init; }
    public required bool Settled { get; init; }
    public required IReadOnlyList<HbSeedScaled> Scaled { get; init; }
    public required double KickV { get; init; }
    public double Remaining { get; init; }
}

public static class OscTransientSeed
{
    /// <summary>The kick when KickV is auto: 1 % of the largest DC supply, or 10 mV with none.</summary>
    public static double DefaultKick(ElaboratedNetlist nl)
    {
        double big = 0;
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not VdcModel) continue;
            if ((ec.Parameters.TryGetValue("Vdc", out var v) || ec.Parameters.TryGetValue("V", out v)) && v.Kind == ValueKind.Real)
                big = Math.Max(big, Math.Abs(v.AsReal()));
        }
        return big > 0 ? 0.01 * big : 0.01;
    }

    /// <summary>
    /// Runs the seed at the time scale <paramref name="fScaleHz"/> (the start-up frequency, or Fguess): step T/(8(K+1)), at most
    /// SeedPeriods (auto: 200) of those periods, stopping when settled under auto. Throws the analysis's refusal when no
    /// oscillation settles, quoting <paramref name="startup"/>.
    /// </summary>
    public static OscSeed Run(ElaboratedNetlist nl, AnalysisSettings settings, ElaboratedComponent probe, bool shunt,
                              double fScaleHz, int maxHarm, HbSeedSettings s, string analysis, OscStartup startup,
                              RunControl? control = null)
    {
        int m = HbTransientSeed.SamplesPerPeriod(maxHarm);
        double T = 1 / fScaleHz, h = T / m;
        int cap = s.Periods ?? HbSeedSettings.AutoCap;
        double kick = s.KickV ?? DefaultKick(nl);
        int a = probe.Nodes[0], b = probe.Nodes.Length > 1 ? probe.Nodes[1] : 0;
        int kn = a > 0 ? a : b;
        double sign = a > 0 ? 1 : -1;
        double[] dcV;
        try { dcV = NonlinearDcEngine.Run(nl, settings).NodeVoltages; }
        catch (NonlinearDcNotConvergedException) { dcV = new double[nl.Nodes.Count]; }
        double Dc(int node) => node > 0 && node - 1 < dcV.Length ? dcV[node - 1] : 0;
        var ic = new Dictionary<string, double>(StringComparer.Ordinal) { [nl.Nodes.NameOf(kn)] = Dc(kn) + sign * kick };

        var scaled = new List<HbSeedScaled>();
        var swapped = s.FastBias ? HbTransientSeed.ScaleChokesAndBlocks(nl, settings, fScaleHz, scaled) : [];
        int[] ifNodes = [];
        int iA = -1, iB = -1, iBr = -1;
        var cycles = new CycleMonitor(s.Tol, s.Periods is null, Math.Abs(kick));
        TransientRunResult r;
        try
        {
            var tp = new TransientParams
            {
                Name = $"oscillator '{analysis}' seed", StopS = cap * T, StepS = h, MaxStepS = h, Start = TransientStart.Op, IcNets = ic,
                MaxSteps = (long)Math.Max(10_000_000, 64.0 * cap * m),
                RecordRows = ifn =>
                {
                    ifNodes = ifn;
                    var rows = ifn.Select(n => n - 1).ToList();
                    if (a > 0) { iA = rows.Count; rows.Add(a - 1); }
                    if (b > 0) { iB = rows.Count; rows.Add(b - 1); }
                    if (!shunt && ((OscProbeModel)probe.Model).LastBranchIndex is int br and >= 0) { iBr = rows.Count; rows.Add(br); }
                    return [.. rows];
                },
                StopWhen = (t, v) => cycles.Push(t, shunt ? (iA >= 0 ? v[iA] : 0) - (iB >= 0 ? v[iB] : 0) : iBr >= 0 ? v[iBr] : 0),
            };
            cycles.Level = shunt ? Dc(a) - Dc(b) : double.NaN;
            r = new TransientEngine(nl, settings).Run(tp, control);
        }
        finally { HbTransientSeed.Restore(nl, swapped); }

        int periods = (int)Math.Round(r.Time[^1] / T);
        if (!cycles.Oscillating || cycles.LastCycle is not { } cyc)
            throw new InvalidOperationException(
                OscDiagnostics.SeedNoOscillation(analysis, probe.InstancePath, periods, fScaleHz, OscDiagnostics.StartupSentence(startup)).Render());

        // The last whole cycle, resampled at m points and transformed (its own time origin at the cycle's start).
        double t0 = cyc.Start, P = cyc.Period;
        int nRec = r.NodeNames.Length, k1 = maxHarm + 1;
        var x = new Complex[nRec, k1];
        for (int q = 0; q < m; q++)
        {
            double tq = t0 + P * q / m;
            double u = (tq - r.Time[0]) / h;
            int j = Math.Clamp((int)Math.Floor(u), 0, r.Time.Length - 2);
            double w = u - j;
            for (int c = 0; c < nRec; c++)
            {
                double val = (1 - w) * r.V[j][c] + w * r.V[j + 1][c];
                for (int k = 0; k < k1; k++)
                {
                    double ph = -2 * Math.PI * k * q / m;
                    x[c, k] += val * new Complex(Math.Cos(ph), Math.Sin(ph)) * (k == 0 ? 1.0 / m : 2.0 / m);
                }
            }
        }
        Complex y1 = shunt ? (iA >= 0 ? x[iA, 1] : 0) - (iB >= 0 ? x[iB, 1] : 0) : iBr >= 0 ? x[iBr, 1] : 0;
        double phi = y1.Phase;
        var v = new Complex[ifNodes.Length, k1];
        for (int i = 0; i < ifNodes.Length; i++)
            for (int k = 0; k < k1; k++)
                v[i, k] = x[i, k] * Complex.FromPolarCoordinates(1, -k * phi);
        return new OscSeed
        {
            FreqHz = 1 / P, Amplitude = y1.Magnitude, V = v, IfNodes = ifNodes, Periods = periods, Cycles = cycles.Count,
            Settled = cycles.Settled, Scaled = scaled, KickV = kick, Remaining = cycles.Remaining,
        };
    }

    /// <summary>The run's note (R-trn8-6).</summary>
    public static string Describe(OscSeed seed, int hbIterations)
    {
        var inv = CultureInfo.InvariantCulture;
        string scaled = seed.Scaled.Count == 0 ? "no choke or block scaled"
            : "scaled in the seed run only: " + string.Join(", ", seed.Scaled.Select(x => string.Format(inv, "{0} x{1:G3}", x.Path, x.Factor)));
        return string.Format(inv,
            "{0}: kicked by {1:G3} V, {2} cycles, {3}, at {4:G9} Hz with amplitude {5:G6}; {6}; the oscillation found in {7} Newton steps from it.",
            HbTransientSeed.Note, seed.KickV, seed.Cycles,
            seed.Settled ? "settled" : string.Format(inv, "not settled (still {0:G2} apart, relative)", seed.Remaining),
            seed.FreqHz, seed.Amplitude, scaled, hbIterations);
    }

    /// <summary>One cycle of the probe signal: from one rising crossing of the level to the next.</summary>
    internal readonly record struct Cycle(double Start, double Period, double Amplitude, double Mean);

    /// <summary>
    /// The cycles of the probe signal and whether they have settled. A rising crossing of the level (the previous cycle's mean;
    /// at first the DC value) counts once the signal has been below it by a tenth of the last amplitude (a Schmitt trigger, so a
    /// ripple near the level is not a cycle). Settled when the period and the amplitude each, over the last three cycles, have a
    /// change still to come — the last change over 1 − ρ, ρ the ratio of the last two changes, as the HB seed's monitor reads it
    /// — within SeedTol, and the amplitude is above 1 % of the kick.
    /// </summary>
    internal sealed class CycleMonitor(double tol, bool stopWhenSettled, double kick)
    {
        public double Level { get; set; } = double.NaN;
        private double _tPrev = double.NaN, _yPrev, _min = double.PositiveInfinity, _max = double.NegativeInfinity;
        private double _sum, _sumT, _cross = double.NaN;
        private bool _armed;
        private readonly List<Cycle> _cycles = [];
        public bool Settled { get; private set; }
        public double Remaining { get; private set; } = double.PositiveInfinity;
        public int Count => _cycles.Count;
        public Cycle? LastCycle => _cycles.Count > 0 ? _cycles[^1] : null;
        /// <summary>At least three cycles, the last one above 1 % of the kick.</summary>
        public bool Oscillating => _cycles.Count >= 3 && _cycles[^1].Amplitude > 0.01 * kick;

        public bool Push(double t, double y)
        {
            if (double.IsNaN(Level)) Level = y;
            if (!double.IsNaN(_tPrev))
            {
                double dt = t - _tPrev;
                _sum += 0.5 * (y + _yPrev) * dt;
                _sumT += dt;
                double hyst = 0.1 * (_cycles.Count > 0 ? _cycles[^1].Amplitude : 0.1 * kick);
                if (y < Level - hyst) _armed = true;
                if (_armed && _yPrev < Level && y >= Level)
                {
                    double tc = _tPrev + (Level - _yPrev) / (y - _yPrev) * dt;
                    if (!double.IsNaN(_cross))
                    {
                        double mean = _sumT > 0 ? _sum / _sumT : Level;
                        _cycles.Add(new Cycle(_cross, tc - _cross, 0.5 * (_max - _min), mean));
                        Level = mean;
                    }
                    _cross = tc;
                    _armed = false;
                    _min = double.PositiveInfinity; _max = double.NegativeInfinity; _sum = 0; _sumT = 0;
                    if (Check()) { _tPrev = t; _yPrev = y; return stopWhenSettled; }
                }
            }
            _min = Math.Min(_min, y); _max = Math.Max(_max, y);
            _tPrev = t; _yPrev = y;
            return false;
        }

        private bool Check()
        {
            int n = _cycles.Count;
            if (n < 4) return false;
            double Rem(Func<Cycle, double> f)
            {
                double x2 = f(_cycles[n - 1]), x1 = f(_cycles[n - 2]), x0 = f(_cycles[n - 3]);
                double d1 = x2 - x1, d0 = x1 - x0;
                if (d1 == 0) return 0;
                double rho = d0 != 0 ? Math.Max(0, d1 / d0) : 0;
                return rho >= 1 ? double.PositiveInfinity : Math.Abs(d1) / (1 - rho) / Math.Abs(x2);
            }
            Remaining = Math.Max(Rem(c => c.Period), Rem(c => c.Amplitude));
            Settled = Remaining <= tol && Oscillating;
            return Settled;
        }
    }
}
