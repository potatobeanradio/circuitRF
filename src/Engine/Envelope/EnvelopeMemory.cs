// ================================================================
//  EnvelopeMemory.cs  —  how long the circuit remembers, for the
//  warm-up of an envelope run's parallel segments.
//
//  Two sources, the larger wins:
//   - what a model DECLARES (ComponentModel.SlowestTimeConstant): a
//     trap's emission, a dispersion tau, a thermal pole.  Declared
//     because a linearisation at a capturing operating point reports
//     the nanosecond capture rate, not the emission.
//   - the linear network's slowest pole: the largest |eigenvalue| of
//     G^-1 C of the baseband product, the same G and C the envelope
//     engine recovers for k = 0 (stamps at 1 Hz and 2 Hz, a line held
//     at its DC stamp).  Power iteration on (G + sigma C)^-1 C, with
//     sigma = 1e-3 / Stop so a node whose only DC path is gmin (or an
//     inductor across an ideal source) still factors.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.HarmonicBalance;
using CSparse.Complex.Factorization;

namespace CircuitRF.Engine.Envelope;

internal static class EnvelopeMemory
{
    /// <summary>A time constant and what holds it, as a note names it ("X1's drain-lag state").</summary>
    internal sealed record Slowest(double Seconds, string What);

    /// <summary>The slowest state any model of <paramref name="nl"/> declares.</summary>
    internal static Slowest? Declared(ElaboratedNetlist nl)
    {
        Slowest? best = null;
        foreach (var ec in nl.Components)
        {
            if (ec.Model.SlowestTimeConstant() is not { } t || (best is not null && t.Seconds <= best.Seconds)) continue;
            string owner = ec.Model is ComposedCoreModel { Label: { Length: > 0 } label } ? label : ec.InstancePath;
            best = new Slowest(t.Seconds, $"{owner}'s {t.State}");
        }
        return best;
    }

    /// <summary>The linear network's slowest pole, on a netlist of its own (it is stamped with envelope drive and its
    /// lines are held, so the caller disposes it). Null when the network does not factor.</summary>
    internal static Slowest? LinearPole(ElaboratedNetlist nl, AnalysisSettings settings, double bandRad, double stopS)
    {
        var sources = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().ToArray();
        if (sources.Length == 0) return null;
        double f0 = sources[0].FcHz;
        foreach (var ec in nl.Components)
        {
            if (ec.Model is ICarrierContext carrierCtx) carrierCtx.SetCarrier(f0);   // brief-pch-6: a channel's f0
            if (ec.Model is P1ToneModel p1) p1.SetToneContext(fc: f0, driveFreqHz: f0);
            else if (ec.Model is PnToneModel pn) pn.SetToneContext(fc: f0);
        }
        foreach (var s in sources) { s.SetToneContext(f0); s.EnvelopeDrive = Complex.Zero; }

        using var baseband = EnvelopeBaseband.Apply(nl, bandRad);
        var ex = new HbLinearExtractor(nl, settings);
        var mna = ex.NewSmallSignalMna();
        double dlt = 2 * Math.PI;
        baseband.Holding = true;
        var a1 = EnvelopeEngine.Stamp(ex, mna, dlt);
        var a2 = EnvelopeEngine.Stamp(ex, mna, 2 * dlt);
        baseband.Holding = false;
        var C = EnvelopeEngine.Combine(a2, a1, 1.0 / new Complex(0, dlt), -1.0 / new Complex(0, dlt));
        if (C.Values is null || C.Values.All(v => v == Complex.Zero)) return null;   // nothing stores energy
        var G = EnvelopeEngine.Combine(a1, C, 1.0, -new Complex(0, dlt));

        double sigma = 1e-3 / stopS;
        SparseLU lu;
        try { lu = SparseLU.Create(EnvelopeEngine.Combine(G, C, 1.0, sigma), CSparse.ColumnOrdering.MinimumDegreeAtPlusA, 1.0); }
        catch (Exception) { return null; }

        int n = G.RowCount;
        var x = new Complex[n];
        for (int i = 0; i < n; i++) x[i] = 1.0 + 0.5 * Math.Sin(1.7 * i);   // not orthogonal to any mode in practice
        Normalise(x);
        var cx = new Complex[n];
        var y = new Complex[n];
        // |mu| from two successive gains, so a dominant complex pair (whose iterate does not settle) still gives its magnitude
        double gainPrev = double.NaN, mu = double.NaN, muPrev = double.NaN;
        for (int it = 0; it < 60; it++)
        {
            Array.Clear(cx);
            EnvelopeEngine.MulCsc(C, x, cx, 1.0);
            lu.Solve(cx, y);
            double gain = Normalise(y);
            if (!double.IsFinite(gain)) return null;
            if (gain == 0) return null;
            (x, y) = (y, x);
            if (!double.IsNaN(gainPrev)) mu = Math.Sqrt(gain * gainPrev);
            gainPrev = gain;
            if (it > 4 && Math.Abs(mu - muPrev) <= 1e-4 * mu) break;
            muPrev = mu;
        }
        if (!(mu > 0)) return null;
        // mu = 1/(sigma + 1/tau) for a real pole
        double inv = 1 / mu - sigma;
        double tau = inv > 1e-3 * sigma ? 1 / inv : 1e3 / sigma;

        int node = 0; double big = -1;
        int nodes = nl.Nodes.Count - 1;
        for (int i = 0; i < Math.Min(nodes, n); i++)
            if (x[i].Magnitude > big) { big = x[i].Magnitude; node = i + 1; }
        return new Slowest(tau, node > 0 ? $"the network at node '{nl.Nodes.NameOf(node)}'" : "the linear network");
    }

    private static double Normalise(Complex[] v)
    {
        double s = 0;
        foreach (var c in v) s += c.Real * c.Real + c.Imaginary * c.Imaginary;
        s = Math.Sqrt(s);
        if (s > 0) for (int i = 0; i < v.Length; i++) v[i] /= s;
        return s;
    }

    /// <summary>A time in the unit a reader would say it in: 1 ms, 20 µs, 350 ns.</summary>
    internal static string Time(double s)
    {
        var inv = CultureInfo.InvariantCulture;
        return s >= 1 ? string.Format(inv, "{0:0.##} s", s)
             : s >= 1e-3 ? string.Format(inv, "{0:0.##} ms", s * 1e3)
             : s >= 1e-6 ? string.Format(inv, "{0:0.##} µs", s * 1e6)
             : string.Format(inv, "{0:0.##} ns", s * 1e9);
    }
}
