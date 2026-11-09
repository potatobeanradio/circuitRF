// ================================================================
//  EnvelopeTangentCheck.cs  —  which element the envelope's tangent fails
//
//  A linear stamp that is not affine in jw and was not fitted (an SnP or
//  a Z(freq) block the fit could not take) enters each harmonic's
//  envelope as its value and slope at that harmonic.  A line is stepped
//  as its delay (EnvelopeLineDelay) and is not measured here.  The run's matrix-wide band-edge
//  check (__EnvTangentError) cannot say WHICH element bends, so each
//  linear element is stamped on its own, into a recorder rather than the
//  run's matrix, with every node it touches tied to ground through 50 ohm,
//  and its terminated node impedances are compared with the tangent's:
//
//    err = RMS over 16 offsets out to +-band of  max_ij |dZ_ij| / |Z_ij|
//
//  (|Z_ij| floored at -60 dB).  Each choice was forced by a measurement
//  (src/Engine/RESOLVED.md): raw stamp entries blow up at a line's
//  sin(theta) = 0; an absolute error misses a filter whose skirt is deep
//  in the adjacent channel, where ACPR reads the RELATIVE error; a single
//  band-edge point saturates while ACPR keeps moving.  The band is the
//  signal's own extent (carriers and their ACPR2 bands).  The baseband
//  envelope (k = 0) is EnvelopeBaseband's: an element whose tangent there
//  is not exact is held at its DC stamp, and that is what it measures.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeTangentCheck
{
    /// <summary>The per-element error above which the run warns: below the error at which every measured case moved ACPR1 or
    /// channel power by 0.1 dB (src/Engine/RESOLVED.md).</summary>
    public const double WarnAbove = 0.5;

    /// <summary>Each linear element's worst tangent error over the products at <paramref name="omegas"/> (rad/s,
    /// non-negative), for a signal band of ±<paramref name="bandRad"/>. Elements that are affine come out at
    /// round-off; nonlinear devices and the modulated sources are not stamped.</summary>
    public static List<(string Instance, double Error)> Measure(ElaboratedNetlist nl, IEnumerable<double> omegas, double bandRad)
    {
        var ws = omegas.Where(w => w - bandRad > 0).Distinct().ToArray();
        var list = new List<(string, double)>();
        if (ws.Length == 0) return list;
        foreach (var ec in nl.Components)
        {
            if (ec.IsNonlinear || ec.Model is ModSourceModel or DelayLineModel) continue;
            double worst;
            try { worst = ElementError(ec, ws, bandRad); }
            catch (Exception) { continue; }    // an element that cannot be stamped or terminated alone is the run's to refuse, not this check's
            list.Add((ec.InstancePath, worst));
        }
        return list;
    }

    /// <summary>One element's worst tangent error over the products at <paramref name="ws"/> (rad/s, each above the band),
    /// by <see cref="Measure"/>'s measure. Throws when the element cannot be stamped or terminated alone.</summary>
    internal static double ElementError(ElaboratedComponent ec, IReadOnlyList<double> ws, double bandRad)
    {
        double worst = 0;
        foreach (double w in ws)
        {
            double dlt = Math.Max(1e-6 * Math.Max(w, 2 * Math.PI * 1e6), 2 * Math.PI * 1.0);   // the engines' own step
            var a0 = Stamped(ec, w);
            var ap = Stamped(ec, w + dlt);
            var am = Stamped(ec, w - dlt);
            double sum = 0;
            var offsets = Enumerable.Range(1, Offsets).SelectMany(i => new[] { bandRad * i / Offsets, -bandRad * i / Offsets }).ToArray();
            foreach (double e in offsets)
            {
                var edge = Stamped(ec, w + e);
                var tan = new Dictionary<long, Complex>();
                foreach (var key in edge.Keys.Union(a0.Keys).Union(ap.Keys).Union(am.Keys))
                    tan[key] = Get(a0, key) + new Complex(0, e) * (Get(ap, key) - Get(am, key)) / new Complex(0, 2 * dlt);
                double d = TerminatedDiff(edge, tan);
                sum += d * d;
            }
            worst = Math.Max(worst, Math.Sqrt(sum / offsets.Length));
        }
        return worst;
    }

    /// <summary>
    /// The baseband product (k = 0): how far the engines' own tangent — from stamps at dlt = 2π rad/s and 2·dlt — and
    /// the element's DC stamp each are from its true stamp, RMS over offsets out to +<paramref name="bandRad"/> (the
    /// baseband envelope is real, so one side), by the same terminated relative measure. See <see cref="EnvelopeBaseband"/>.
    /// </summary>
    internal static (double Tangent, double Held) BasebandErrors(ElaboratedComponent ec, double bandRad)
    {
        const double dlt = 2 * Math.PI;
        var a1 = Stamped(ec, dlt);
        var a2 = Stamped(ec, 2 * dlt);
        var dc = Stamped(ec, 0);
        double st = 0, sh = 0;
        for (int i = 1; i <= Offsets; i++)
        {
            double e = bandRad * i / Offsets;
            var actual = Stamped(ec, e);
            var tan = new Dictionary<long, Complex>();
            foreach (var key in actual.Keys.Union(a1.Keys).Union(a2.Keys))
                tan[key] = Get(a1, key) + (e - dlt) / dlt * (Get(a2, key) - Get(a1, key));
            double t = TerminatedDiff(actual, tan), h = TerminatedDiff(actual, dc);
            st += t * t; sh += h * h;
        }
        return (Math.Sqrt(st / Offsets), Math.Sqrt(sh / Offsets));
    }

    /// <summary>
    /// The baseband error of a line held shorted (<see cref="BasebandHeldModel.Shorts"/>): how far the short is from the
    /// line's true stamp, by <see cref="BasebandErrors"/>' measure. Both are stamped through the held element, so the
    /// short's branch is an unknown of both.
    /// </summary>
    internal static double HeldError(ElaboratedComponent held, BasebandHeldModel model, double bandRad)
    {
        Dictionary<long, Complex> dc;
        model.Holding = true;
        try { dc = Stamped(held, 0); }
        finally { model.Holding = false; }
        double sh = 0;
        for (int i = 1; i <= Offsets; i++)
        {
            double h = TerminatedDiff(Stamped(held, bandRad * i / Offsets), dc);
            sh += h * h;
        }
        return Math.Sqrt(sh / Offsets);
    }

    /// <summary>Offsets per side of the harmonic at which the tangent is compared, evenly out to the band edge.</summary>
    private const int Offsets = 8;

    /// <summary>The termination every node of an element sees for the measure (ohm).</summary>
    private const double Rterm = 50;
    /// <summary>A terminated impedance below this fraction of <see cref="Rterm"/> (-60 dB) is compared absolutely.</summary>
    private const double Floor = 1e-3;

    /// <summary>
    /// max |Z_a − Z_b| / |Z_a| (floored) over the node-to-node impedances of the element with every one of its nodes tied to ground
    /// through <see cref="Rterm"/>. Compared as a terminated RESPONSE, not as raw stamp entries: a line's Y stamp is
    /// singular wherever sin θ = 0 (and a Touchstone block's Z wherever S has an eigenvalue 1), so an entry-wise
    /// measure reads a resonance of the representation as an error in the circuit. Terminated, a passive element's
    /// impedances are bounded by Rterm, and the measure is in effect |ΔS|/|S|.
    /// </summary>
    private static double TerminatedDiff(Dictionary<long, Complex> a, Dictionary<long, Complex> b)
    {
        var ids = Ids(a.Keys.Concat(b.Keys));
        var za = Terminated(a, ids);
        var zb = Terminated(b, ids);
        double worst = 0;
        for (int i = 0; i < ids.Count; i++)
            for (int j = 0; j < ids.Count; j++)
                if (ids[i] < Recorder.B && ids[j] < Recorder.B)
                    worst = Math.Max(worst, (za[i, j] - zb[i, j]).Magnitude / Math.Max(za[i, j].Magnitude, Floor * Rterm));
        return worst;
    }

    /// <summary>The unknowns (nodes, then offset branches) a set of recorded entries touches, in order.</summary>
    internal static List<long> Ids(IEnumerable<long> keys) =>
        keys.SelectMany(k => new[] { k >> 32, k & 0xffffffffL }).Distinct().OrderBy(x => x).ToList();

    /// <summary>The inverse of the element's own entries over <paramref name="ids"/> with every node tied to ground
    /// through <see cref="Rterm"/>; its node rows and columns are the terminated node impedances.</summary>
    internal static Complex[,] Terminated(Dictionary<long, Complex> m, List<long> ids)
    {
        var at = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        int n = ids.Count;
        var d = new Complex[n, n];
        foreach (var (k, v) in m) d[at[k >> 32], at[k & 0xffffffffL]] += v;
        for (int i = 0; i < n; i++) if (ids[i] < Recorder.B) d[i, i] += 1 / Rterm;
        return EnvelopeEngine.Invert(d, n);
    }

    /// <summary>
    /// The element as a node-referenced S matrix in <see cref="Rterm"/>: one port per non-ground node it touches (in
    /// <paramref name="nodes"/>' order), S = 2 Z/Rterm − I from the terminated node impedances. Exists for any element
    /// whose terminated system is not singular — a floating block included, its common mode being terminated too.
    /// </summary>
    internal static Complex[,] NodeS(ElaboratedComponent ec, double omega, IReadOnlyList<int> nodes)
    {
        var m = Stamped(ec, omega);
        var ids = Ids(m.Keys.Concat(nodes.Select(x => ((long)x << 32) | (long)x)));
        var z = Terminated(m, ids);
        int n = nodes.Count;
        var s = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                s[i, j] = 2 * z[ids.IndexOf(nodes[i]), ids.IndexOf(nodes[j])] / Rterm - (i == j ? 1 : 0);
        return s;
    }

    /// <summary>The termination <see cref="NodeS"/> refers its S to (ohm).</summary>
    internal const double NodeSReference = Rterm;

    /// <summary>One warning per run naming every element above <see cref="WarnAbove"/>, worst first.</summary>
    public static void Warn(ElaboratedNetlist nl, List<(string Instance, double Error)> errors, double bandHz)
    {
        var bad = errors.Where(x => x.Error > WarnAbove).OrderByDescending(x => x.Error).ToList();
        if (bad.Count == 0) return;
        var inv = CultureInfo.InvariantCulture;
        string names = string.Join(", ", bad.Select(x => string.Format(inv, "'{0}' ({1:0.#} %)", x.Instance, 100 * x.Error)));
        nl.AddWarningOnce("env-tangent", string.Format(inv,
            "envelope: {0} {1} approximated to first order around each harmonic (its value and slope there), off by that much " +
            "(RMS) across the ±{2:0.##} MHz signal band; results across the band are approximate",
            names, bad.Count == 1 ? "is" : "are", bandHz / 1e6));
    }

    private static Complex Get(Dictionary<long, Complex> d, long k) => d.TryGetValue(k, out var v) ? v : Complex.Zero;

    /// <summary>The element's own stamp at <paramref name="w"/>, recorded instead of entered into a run's matrix.</summary>
    internal static Dictionary<long, Complex> Stamped(ElaboratedComponent ec, double w) => Recorded(r => ec.Stamp(r, w));

    /// <summary>Whatever <paramref name="stamp"/> enters, recorded instead of entered into a run's matrix — one element, or
    /// several that must share one numbering (a Mutual reads the branch its inductors were just given).</summary>
    internal static Dictionary<long, Complex> Recorded(Action<IMnaContext> stamp)
    {
        var r = new Recorder();
        stamp(r);
        return r.A;
    }

    /// <summary>The element's own matrix entries, rows/columns keyed by node or (offset) branch.</summary>
    internal sealed class Recorder : IMnaContext
    {
        public const long B = 1L << 30;
        public readonly Dictionary<long, Complex> A = [];
        private int _branches;
        private void Add(long r, long c, Complex v)
        {
            long k = (r << 32) | c;
            A[k] = (A.TryGetValue(k, out var o) ? o : Complex.Zero) + v;
        }
        public void AddAdmittance(int a, int b, Complex y)
        {
            if (a > 0) Add(a, a, y);
            if (b > 0) Add(b, b, y);
            if (a > 0 && b > 0) { Add(a, b, -y); Add(b, a, -y); }
        }
        public void AddBlockAdmittance(int r, int c, Complex y) { if (r > 0 && c > 0) Add(r, c, y); }
        public int AddBranch() => _branches++;
        public void AddBranchCurrent(int br, int from, int to)
        {
            if (from > 0) Add(from, B + br, Complex.One);
            if (to > 0) Add(to, B + br, -Complex.One);
        }
        public void AddConstraint(int br, int node, Complex coeff) { if (node > 0) Add(B + br, node, coeff); }
        public void AddNodeBranchCoupling(int node, int br, Complex coeff) { if (node > 0) Add(node, B + br, coeff); }
        public void AddBranchConstraint(int br, int other, Complex coeff) => Add(B + br, B + other, coeff);
        public void AddCurrentInjection(int node, Complex j) { }
        public void AddSourceValue(int br, Complex value) { }
    }
}
