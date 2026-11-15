// ================================================================
//  EnvelopeLineDelay.cs  —  a line in the circuit envelope, as its delay
//
//  A line's response turns by 360° per 1/τ Hz, so neither its value and
//  slope at a harmonic nor a rational fit from DC to the top of the run
//  follows it (src/Engine/RESOLVED.md, ENV-FIT).  For an envelope run a
//  two-conductor line (IUniformLine) is stepped as what it is, a delay:
//  each end is its Zc in series with the wave that left the other end τ
//  earlier (the method of characteristics),
//
//    V_a - Zc I_a = H~ * W_b(t - τ),   W_b = V_b + Zc I_b,
//
//  per product k, on that product's envelope rotated by e^(-j w_k τ).
//  The residual H~(f) = e^(-γl) e^(j2πfτ) is what is left of the line
//  with its delay taken out: 1 for a lossless line, and otherwise smooth
//  across a product's band, so it is taken to first order there
//  (h0 + h1·Δf, h1 as a time derivative of the delayed wave).  τ is the
//  line's group delay at each product, so h1 carries no phase slope.  The
//  line loss models attenuate with no matching phase (not causal), which
//  is why the residual is NOT fitted globally: it cannot be.  At the
//  baseband the envelope is real, so H~ is held at its DC value, 1, which
//  keeps DC exact.
//
//  τ is usually shorter than a step, so the delayed wave is interpolated:
//  quadratic Lagrange on the step grid (passive: |L| <= 1 to Nyquist,
//  measured; cubic is not), whose weight on the CURRENT step, when τ < h,
//  is a term of the stepped matrix (fixed per run: one factorization per
//  product, as before).  A split step interpolates linearly.
//
//  At a steady state (constant envelopes) the realization is the line's
//  exact stamp at w_k, which is what the DC solve, the harmonic-balance
//  start and every extraction stamp (DelayLineModel with no product set):
//  no resonance clamp anywhere, and at w = 0 the Branin rows are the
//  line's DC short.  The substitution is scoped to the run.
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CSparse;
using CSparse.Complex;
using CSparse.Storage;

namespace CircuitRF.Engine.Envelope;

/// <summary>
/// A two-conductor line as its Branin (characteristic) form: two branch currents into the line, and rows
/// <c>V_a − Zc·I_a − c·(V_b + Zc·I_b) = rhs</c> and the same with a and b exchanged. With no product set it stamps the
/// line's exact steady state at ω (<c>c = e^(−γl(ω))</c>); while the envelope stamps one of its products it stamps that
/// product's constant <c>Zc</c> with <c>c = 0</c>, the delayed wave arriving through the stepped matrix and right-hand
/// side (<see cref="EnvelopeLineDelay"/>).
/// </summary>
public sealed class DelayLineModel : ComponentModel, IReportsWarnings
{
    private readonly ComponentModel _inner;
    private readonly IUniformLine _line;
    private readonly double _m;

    public DelayLineModel(ComponentModel inner, double multiplicity)
    {
        _inner = inner;
        _line = (IUniformLine)inner;
        _m = multiplicity;
    }

    public override int PortCount => 2;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => _inner.TerminalNames;

    /// <summary>The characteristic impedance the product being stamped uses; null for the steady state.</summary>
    internal double? SteppingZc { get; set; }
    /// <summary>The branch rows of the last stamp: the current into the line at its first and second node.</summary>
    internal int BranchA { get; private set; } = -1;
    internal int BranchB { get; private set; } = -1;

    /// <summary>The line's characteristic impedance (Ω, the multiplier applied) and γl at <paramref name="hz"/>.</summary>
    internal (double Zc, Complex GammaLength) At(double hz)
    {
        var (z0, gl) = _line.LineAt(hz);
        return (z0 / _m, gl);
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        var (na, nb) = _line.Ends(c.Nodes) ?? (c.Nodes[0], c.Nodes[1]);
        int ia = mna.AddBranch(), ib = mna.AddBranch();
        BranchA = ia; BranchB = ib;
        mna.AddBranchCurrent(ia, na, 0);
        mna.AddBranchCurrent(ib, nb, 0);
        double zc;
        Complex k;
        if (SteppingZc is double z) { zc = z; k = Complex.Zero; }
        else
        {
            (zc, var gl) = At(Math.Abs(omega) / (2 * Math.PI));
            k = Complex.Exp(-gl);
            if (omega < 0) k = Complex.Conjugate(k);
        }
        void Row(int br, int n1, int b1, int n2, int b2)
        {
            mna.AddConstraint(br, n1, Complex.One);
            mna.AddBranchConstraint(br, b1, -zc);
            if (k == Complex.Zero) return;
            mna.AddConstraint(br, n2, -k);
            mna.AddBranchConstraint(br, b2, -k * zc);
        }
        Row(ia, na, ia, nb, ib);
        Row(ib, nb, ib, na, ia);
    }

    public IReadOnlyList<(string Key, string Message)> DrainWarnings() => _inner is IReportsWarnings rw ? rw.DrainWarnings() : [];
}

public static class EnvelopeLineDelay
{
    /// <summary>What one product needs of one line: its Zc, delay and residual, and the delayed waves it has seen.</summary>
    internal sealed class Coeffs
    {
        public double Zc, Tau;
        public Complex Rot = Complex.One, H0 = Complex.One, G1;
        public bool SlopeDropped;
        // quadratic Lagrange on the step grid: nodes N0, N0+1, N0+2 steps back, value and d/dt weights
        public int N0;
        public readonly double[] L = new double[3], Ld = new double[3];
        // the waves V + Zc I at each end, a ring of past step samples
        public Complex[] Wa = null!, Wb = null!;
        public Complex Wa0, Wb0;
    }

    internal sealed class Line
    {
        public required int Index;
        public required ElaboratedComponent Original;
        public required DelayLineModel Model;
        public required int Na, Nb;
        public required Coeffs[] P;
        public int Ia = -1, Ib = -1;                           // its branch rows in the run's own system
    }

    /// <summary>The lines of one run: puts them back on <see cref="Dispose"/>, and carries their history.</summary>
    public sealed class Run : IDisposable
    {
        private readonly ElaboratedNetlist _nl;
        private readonly List<Line> _lines = [];
        private readonly double _h;
        private int _n;                                       // the last accepted step
        private readonly List<Complex[][]> _sub = [];          // a split's accepted sub-steps, [i][product]
        internal Run(ElaboratedNetlist nl, double h) { _nl = nl; _h = h; }
        internal void Add(Line l)
        {
            _lines.Add(l);
            // the multiplier is in the model's Zc (a branch cannot be multiplied)
            _nl.Components[l.Index] = new ElaboratedComponent(
                l.Original.ComponentType, l.Original.InstancePath, l.Original.Nodes, l.Original.Parameters, l.Model);
        }

        public bool Any => _lines.Count > 0;

        /// <summary>The product being stamped: each line stamps its constant form for it. Null: the steady state.</summary>
        public int? Product { set { foreach (var l in _lines) l.Model.SteppingZc = value is int k ? l.P[k].Zc : null; } }

        /// <summary>Takes each line's branch rows from the stamp just made: call it after a product is stamped. (Other
        /// systems, the DC solve's for one, number the same branches differently.)</summary>
        public void Bind() { foreach (var l in _lines) { l.Ia = l.Model.BranchA; l.Ib = l.Model.BranchB; } }

        /// <summary>
        /// The stepped matrix's share of the current step's delayed wave for product <paramref name="k"/> at a step of
        /// h/<paramref name="split"/> (1 = the full step), or null when every line is at least that long.
        /// </summary>
        public CompressedColumnStorage<Complex>? Coupling(int k, int split, int n)
        {
            var e = new List<(int r, int c, Complex v)>();
            foreach (var l in _lines)
            {
                var p = l.P[k];
                Complex c0 = split == 1 ? (p.N0 == 0 ? p.Rot * (p.H0 * p.L[0] + p.G1 * p.Ld[0]) : Complex.Zero) : SplitNow(p, _h / split);
                if (c0 == Complex.Zero) continue;
                int ia = l.Ia, ib = l.Ib;
                if (l.Nb > 0) e.Add((ia, l.Nb - 1, -c0));
                e.Add((ia, ib, -c0 * p.Zc));
                if (l.Na > 0) e.Add((ib, l.Na - 1, -c0));
                e.Add((ib, ia, -c0 * p.Zc));
            }
            if (e.Count == 0) return null;
            var tri = new CoordinateStorage<Complex>(n, n, e.Count);
            foreach (var (r, c, v) in e) tri.At(r, c, v);
            return SparseMatrix.OfIndexed(tri);
        }

        /// <summary>The weight on the current sub-step's wave when τ is shorter than a sub-step of <paramref name="hs"/>.</summary>
        private static Complex SplitNow(Coeffs p, double hs) =>
            p.Tau < hs ? p.Rot * (p.H0 * (1 - p.Tau / hs) + p.G1 / hs) : Complex.Zero;

        /// <summary>The history sample 0: the start's steady state.</summary>
        public void Start(Complex[][] x)
        {
            _n = 0;
            foreach (var l in _lines)
                for (int k = 0; k < l.P.Length; k++)
                {
                    var p = l.P[k];
                    var (wa, wb) = Waves(l, p, x[k]);
                    p.Wa0 = wa; p.Wb0 = wb;
                    p.Wa = new Complex[p.N0 + 4]; p.Wb = new Complex[p.N0 + 4];
                    Array.Fill(p.Wa, wa); Array.Fill(p.Wb, wb);
                }
        }

        /// <summary>An accepted full step.</summary>
        public void Push(Complex[][] x)
        {
            _n++;
            _sub.Clear();
            foreach (var l in _lines)
                for (int k = 0; k < l.P.Length; k++)
                {
                    var p = l.P[k];
                    var (wa, wb) = Waves(l, p, x[k]);
                    p.Wa[_n % p.Wa.Length] = wa; p.Wb[_n % p.Wb.Length] = wb;
                }
        }

        /// <summary>A split attempt starting over (its sub-steps forgotten), and one of its accepted sub-steps.</summary>
        public void BeginSplit() => _sub.Clear();
        public void PushSub(Complex[][] x) => _sub.Add(x);

        private static (Complex, Complex) Waves(Line l, Coeffs p, Complex[] x)
        {
            Complex va = l.Na > 0 ? x[l.Na - 1] : Complex.Zero, vb = l.Nb > 0 ? x[l.Nb - 1] : Complex.Zero;
            return (va + p.Zc * x[l.Ia], vb + p.Zc * x[l.Ib]);
        }

        private static (Complex a, Complex b) At(Coeffs p, int step) =>
            step <= 0 ? (p.Wa0, p.Wb0) : (p.Wa[step % p.Wa.Length], p.Wb[step % p.Wb.Length]);

        /// <summary>The delayed waves' known part for product <paramref name="k"/> of the full step being taken.</summary>
        public void AddMain(int k, Complex[] b)
        {
            int n = _n + 1;
            foreach (var l in _lines)
            {
                var p = l.P[k];
                Complex ra = 0, rb = 0;
                for (int j = 0; j < 3; j++)
                {
                    int s = n - p.N0 - j;
                    if (s == n) continue;                     // the current step: in the matrix
                    Complex w = p.Rot * (p.H0 * p.L[j] + p.G1 * p.Ld[j]);
                    if (w == Complex.Zero) continue;
                    var (wa, wb) = At(p, s);
                    ra += w * wb; rb += w * wa;
                }
                b[l.Ia] += ra;
                b[l.Ib] += rb;
            }
        }

        /// <summary>The delayed waves' known part for product <paramref name="k"/> of sub-step <paramref name="i"/> of
        /// <paramref name="split"/> (linear interpolation: the sub-steps have their own spacing).</summary>
        public void AddSplit(int k, Complex[] b, int split, int i)
        {
            double hs = _h / split;
            foreach (var l in _lines)
            {
                var p = l.P[k];
                (Complex a, Complex b) Sub(int s) => s == 0 ? At(p, _n) : Waves(l, p, _sub[s - 1][k]);
                Complex ra = 0, rb = 0;
                void Take((Complex a, Complex b) w, Complex c) { ra += c * w.b; rb += c * w.a; }
                if (p.Tau < hs)
                    Take(Sub(i - 1), p.Rot * (p.H0 * p.Tau / hs - p.G1 / hs));
                else
                {
                    double target = _n + (double)i / split - p.Tau / _h;   // in steps
                    if (target >= _n)
                    {
                        double s = (target - _n) * split;
                        int s0 = (int)Math.Floor(s);
                        double f = s - s0;
                        if (f > 0)
                        {
                            Take(Sub(s0), p.Rot * (p.H0 * (1 - f) - p.G1 / hs));
                            Take(Sub(s0 + 1), p.Rot * (p.H0 * f + p.G1 / hs));
                        }
                        else Take(Sub(s0), p.Rot * p.H0);             // on a sample whose successor is the unknown: no slope
                    }
                    else
                    {
                        int s0 = (int)Math.Floor(target);
                        double f = target - s0;
                        Take(At(p, s0), p.Rot * (p.H0 * (1 - f) - p.G1 / _h));
                        Take(At(p, s0 + 1), p.Rot * (p.H0 * f + p.G1 / _h));
                    }
                }
                b[l.Ia] += ra;
                b[l.Ib] += rb;
            }
        }

        public void Dispose()
        {
            foreach (var l in _lines) _nl.Components[l.Index] = l.Original;
            _lines.Clear();
        }
    }

    /// <summary>Quadratic Lagrange weights on nodes 0, 1, 2 at <paramref name="d"/> (value) and their d/dx.</summary>
    internal static void Lagrange(double d, double[] l, double[] ld)
    {
        l[0] = (d - 1) * (d - 2) / 2; l[1] = -d * (d - 2); l[2] = d * (d - 1) / 2;
        ld[0] = (2 * d - 3) / 2;      ld[1] = -(2 * d - 2);  ld[2] = (2 * d - 1) / 2;
    }

    /// <summary>
    /// Replaces each two-conductor line of <paramref name="nl"/> by its delay realization for one envelope run whose
    /// products sit at <paramref name="productHz"/> (index 0 the baseband, all ≥ 0), stepped at <paramref name="h"/>, with
    /// a signal band of ±<paramref name="bandHz"/>. Each line posts a note: its delay and how far the realization is from
    /// the line across the band; a warning when that is above <see cref="EnvelopeTangentCheck.WarnAbove"/>.
    /// </summary>
    public static Run Apply(ElaboratedNetlist nl, IReadOnlyList<double> productHz, double h, double bandHz)
    {
        var run = new Run(nl, h);
        var inv = CultureInfo.InvariantCulture;
        double nyq = 0.5 / h;
        for (int i = 0; i < nl.Components.Count; i++)
        {
            var ec = nl.Components[i];
            if (ec.IsNonlinear || ec.Model is not IUniformLine line || line.Ends(ec.Nodes) is not { } ends) continue;
            var (endA, endB) = ends;
            var model = new DelayLineModel(ec.Model, ec.Multiplicity);
            var P = new Coeffs[productHz.Count];
            double worst = 0;
            var dropped = new List<double>();
            for (int k = 0; k < P.Length; k++)
            {
                var p = P[k] = Of(model, productHz[k], h, nyq);
                worst = Math.Max(worst, Error(model, p, productHz[k], bandHz));
                if (p.SlopeDropped) dropped.Add(productHz[k]);
            }
            run.Add(new Line { Index = i, Original = ec, Model = model, Na = endA, Nb = endB, P = P });
            string who = $"{ec.ComponentType} '{ec.InstancePath}'";
            nl.AddNoteOnce("env-line-delay:" + ec.InstancePath, string.Format(inv,
                "{0}: stepped as its delay ({1:0.####} ns) with its loss to first order around each harmonic; {2:0.##} % (RMS) " +
                "from the line across the ±{3:0.##} MHz signal band{4}",
                who, P[0].Tau * 1e9, 100 * worst, bandHz / 1e6,
                dropped.Count == 0 ? "" : "; its loss slope is left out at " + string.Join(", ", dropped.Select(f => (f / 1e9).ToString("0.####", inv) + " GHz")) +
                                          ", where it would make the line gain energy within the step's band"));
            if (worst > EnvelopeTangentCheck.WarnAbove)
                nl.AddWarningOnce("env-line-delay-error:" + ec.InstancePath, string.Format(inv,
                    "envelope: '{0}' ({1:0.#} %) is stepped as its delay with its loss to first order around each harmonic, off by " +
                    "that much (RMS) across the ±{2:0.##} MHz signal band; results across the band are approximate",
                    ec.InstancePath, 100 * worst, bandHz / 1e6));
        }
        return run;
    }

    /// <summary>One product's Zc, delay and residual. The baseband takes the line's low-frequency delay and H~ = 1.</summary>
    private static Coeffs Of(DelayLineModel m, double fk, double h, double nyq)
    {
        var p = new Coeffs();
        if (fk <= 0)
        {
            const double lo = 1e3;
            p.Zc = m.At(0).Zc;
            p.Tau = m.At(lo).GammaLength.Imaginary / (2 * Math.PI * lo);
        }
        else
        {
            double dl = Math.Min(1e6, 0.25 * fk);
            var (zc, g0) = m.At(fk);
            var gp = m.At(fk + dl).GammaLength;
            var gm = m.At(fk - dl).GammaLength;
            p.Zc = zc;
            p.Tau = (gp.Imaginary - gm.Imaginary) / (2 * Math.PI * 2 * dl);       // group delay: no phase slope left
            Complex Res(double f, Complex gl) => Complex.Exp(-gl + new Complex(0, 2 * Math.PI * f * p.Tau));
            p.H0 = Res(fk, g0);
            var h1 = (Res(fk + dl, gp) - Res(fk - dl, gm)) / (2 * dl);
            p.Rot = Complex.Exp(new Complex(0, -2 * Math.PI * fk * p.Tau));
            // never step a residual that is not passive across the band the step resolves (|h0 + h1·Δf| is largest at its ends)
            if ((p.H0 + h1 * nyq).Magnitude > 1 || (p.H0 - h1 * nyq).Magnitude > 1) { h1 = 0; p.SlopeDropped = true; }
            p.G1 = h1 / new Complex(0, 2 * Math.PI);
        }
        p.Tau = Math.Max(0, p.Tau);
        double D = p.Tau / h;
        p.N0 = (int)Math.Floor(D);
        Lagrange(D - p.N0, p.L, p.Ld);
        for (int j = 0; j < 3; j++) p.Ld[j] *= -1 / h;                         // nodes run back in time
        if (fk <= 0) p.G1 = 0;
        return p;
    }

    /// <summary>
    /// How far the realization (the delay exact, the residual to first order) is from the line across the band: RMS over
    /// 8 offsets per side of max_ij |ΔS_ij| / |S_ij| (|S| floored at 1e-3), S in 50 Ω — the tangent check's measure.
    /// </summary>
    private static double Error(DelayLineModel m, Coeffs p, double fk, double bandHz)
    {
        const int n = 8;
        double sum = 0; int cnt = 0;
        for (int i = -n; i <= n; i++)
        {
            if (i == 0 || (fk <= 0 && i < 0)) continue;
            double df = bandHz * i / n, f = fk + df;
            var (zc, gl) = m.At(f);
            var exact = S(zc, Complex.Exp(-gl));
            var real = S(p.Zc, Complex.Exp(new Complex(0, -2 * Math.PI * f * p.Tau)) * (fk <= 0 ? Complex.One : p.H0 + p.G1 * new Complex(0, 2 * Math.PI * df)));
            double d = Math.Max((exact.s11 - real.s11).Magnitude / Math.Max(exact.s11.Magnitude, 1e-3),
                                (exact.s21 - real.s21).Magnitude / Math.Max(exact.s21.Magnitude, 1e-3));
            sum += d * d; cnt++;
        }
        return cnt == 0 ? 0 : Math.Sqrt(sum / cnt);
    }

    private static (Complex s11, Complex s21) S(double zc, Complex H)
    {
        double g = (zc - 50) / (zc + 50);
        var d = 1 - g * g * H * H;
        return (g * (1 - H * H) / d, H * (1 - g * g) / d);
    }
}
