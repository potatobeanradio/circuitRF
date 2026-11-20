using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>One extracted quantity against what was asked of it, and whether it is near enough.</summary>
public sealed record FilterCheck(string Name, double Value, double Target, bool Pass)
{
    /// <summary>Value − Target.</summary>
    public double Delta => Value - Target;

    /// <summary><paramref name="v"/> in this check's unit: a frequency in GHz (the bandwidth in MHz), a level in dB, a
    /// k to four places, else four figures.</summary>
    public string Format(double v) => Name switch
    {
        "Bandwidth" => Text(v / 1e6, "0.0") + " MHz",
        "Centre" => Text(v / 1e9, "0.0000") + " GHz",
        "Ripple" or "Return loss" => Text(v, "0.00") + " dB",
        _ when Name.StartsWith("Zero", StringComparison.Ordinal) || Name.StartsWith("f(", StringComparison.Ordinal)
            => Text(v / 1e9, "0.0000") + " GHz",
        _ when Name.StartsWith("k(", StringComparison.Ordinal) => Text(v, "0.0000"),
        _ => Text(v, "G4"),
    };

    private static string Text(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>
/// What a bandpass filter's two-port S-parameters say it is (docs/design/filter-synthesis.md §6c). Read off the samples:
/// the centre (geometric) and width of the band where |S21| is within the ripple (3 dB for a response without one) of
/// its peak, the ripple and the worst return loss across the spec's band, and the insertion loss at its centre. From the
/// fit: the unloaded Q, the finite transmission zeros, and the folded N+2 matrix, its main line made positive
/// (<see cref="FilterExtraction.Signed"/>), with its Qe, k_ij and resonant frequencies (null for a fully canonical
/// matrix, whose source–load coupling no external Q carries). <see cref="Checks"/> holds each sample metric against the
/// spec, then each transmission zero against the spec's, then each matrix entry against the target's.
/// </summary>
public sealed record ExtractedFilter(
    double CentreHz, double BandwidthHz, double RippleDb, double ReturnLossDb, double InsertionLossDb,
    double UnloadedQ, int Order, IReadOnlyList<double> TransmissionZerosHz, double[,] Matrix,
    CoupledResonators? Resonators, IReadOnlyList<FilterCheck> Checks);

/// <summary>
/// The reverse of <see cref="CouplingMatrix"/> (docs/design/filter-synthesis.md §6c): any two-port's S11 and S21 (a
/// circuit run, an EM run, a measured file) back to its filter's prototype. S11 = F/E and S21 = P/E are fitted with one
/// denominator in the normalised lowpass domain (<see cref="CouplingCurves.Rational"/>), as the rational-model route to
/// the coupling matrix does (A. García-Lampérez et al., IEEE Trans. MTT 52(2), 2004,
/// <see href="https://doi.org/10.1109/TMTT.2003.822021">10.1109/TMTT.2003.822021</see>). A uniform loss moves every
/// pole and zero the same δ = 1/(Qu·FBW) off the axis (G. Macchiarella, IEEE MWCL 20(6), 2010,
/// <see href="https://doi.org/10.1109/LMWC.2010.2047455">10.1109/LMWC.2010.2047455</see>), so the lossless F and P are
/// the fitted ones moved δ back; E follows from |E|² = |F/εR|² + |P/ε|², and the polynomials go through
/// <see cref="CouplingMatrix.Transversal"/> and <see cref="CouplingMatrix.Folded"/> as a synthesis does.
/// </summary>
public static class FilterExtraction
{
    /// <summary>How far either side of the band's centre the fit reaches, in normalised frequency ω; the samples beyond
    /// are left out, as the narrow-band model the matrix is no longer holds there.</summary>
    public const double Window = 3.0;

    /// <summary>
    /// <paramref name="spec"/>'s filter as its samples show it: the spec gives f0, FBW, the order and the number of finite
    /// zeros the fit allows. With <paramref name="target"/> (N+2, as <see cref="CouplingMatrix"/> gives it) each of its
    /// entries is checked too, in band-pass terms, and passes within <paramref name="tolerance"/> in M; the sample
    /// metrics pass within that fraction of the spec's (the centre within that fraction of half the band).
    /// </summary>
    public static ExtractedFilter Extract(FilterSpec spec, IReadOnlyList<double> fHz, IReadOnlyList<Complex> s11,
        IReadOnlyList<Complex> s21, double[,]? target = null, double tolerance = 0.02)
    {
        if (spec.Band != FilterBand.Bandpass)
            throw Refuse("band", "a coupling matrix is a bandpass filter's; a {band} filter has none.", ("band", spec.Band));
        if (s11.Count != fHz.Count || s21.Count != fHz.Count)
            throw Refuse("lengths", "{f} frequencies, but {a} S11 and {b} S21 samples.", ("f", fHz.Count), ("a", s11.Count),
                ("b", s21.Count));
        int n = spec.Order, nfz = spec.TransmissionZerosHz?.Count ?? 0;
        if (target is not null && target.GetLength(0) != n + 2)
            throw Refuse("target", "the target is {size} square; an order-{n} filter's is N+2.", ("size", target.GetLength(0)),
                ("n", n));
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth, bw = spec.F2Hz - spec.F1Hz;
        double W(double f) => (f / f0 - f0 / f) / fbw;
        int[] near = [.. Enumerable.Range(0, fHz.Count).Where(i => Math.Abs(W(fHz[i])) <= Window).OrderBy(i => fHz[i])];
        int[] band = [.. near.Where(i => fHz[i] >= spec.F1Hz && fHz[i] <= spec.F2Hz)];
        int need = 2 * (2 * n + nfz + 1);
        if (near.Length < need || band.Length < 2)
            throw Refuse("samples", "{near} samples lie within ω = ±{w} and {band} in the band; an order-{n} fit needs {need} "
                + "and 2.", ("near", near.Length), ("w", Window), ("band", band.Length), ("n", n), ("need", need));

        // ── Off the samples ─────────────────────────────────────────────────────────────────────────────────────────
        double[] a11 = [.. s11.Select(Db)], a21 = [.. s21.Select(Db)];
        double level = spec.Response == FilterResponse.Chebyshev ? spec.RippleDb : 10.0 * Math.Log10(2.0);
        double peak = band.Max(i => a21[i]), edge = peak - level;
        int lo = Array.FindIndex(near, i => a21[i] >= edge), hi = Array.FindLastIndex(near, i => a21[i] >= edge);
        // Where |S21| crosses the edge level between a sample outside and one inside, linearly in dB.
        double Cross(int outside, int inside) => outside < 0 || outside >= near.Length ? double.NaN
            : fHz[near[outside]] + (fHz[near[inside]] - fHz[near[outside]]) * (edge - a21[near[outside]])
              / (a21[near[inside]] - a21[near[outside]]);
        double f1 = Cross(lo - 1, lo), f2 = Cross(hi + 1, hi);
        int c = Array.FindIndex(near, i => fHz[i] >= f0);
        c = Math.Max(1, c < 0 ? near.Length - 1 : c);
        double il = -(a21[near[c - 1]] + (a21[near[c]] - a21[near[c - 1]]) * (f0 - fHz[near[c - 1]])
                      / (fHz[near[c]] - fHz[near[c - 1]]));
        double centre = Math.Sqrt(f1 * f2), ripple = peak - band.Min(i => a21[i]), rl = -band.Max(i => a11[i]);

        // ── The fit, over x = ω/scale within ±1, then back to s = jω, descending, with E monic ─────────────────────
        double scale = near.Max(i => Math.Abs(W(fHz[i])));
        var (num, den) = CouplingCurves.Rational([.. near.Select(i => W(fHz[i]) / scale)],
            [near.Select(i => s11[i]).ToArray(), near.Select(i => s21[i]).ToArray()], [n, nfz], n);
        Complex[] S(Complex[] a) => [.. a.Select((v, k) => v * Complex.Pow(new Complex(0.0, scale), n - k)).Reverse()];
        Complex[] e = S(den), f = S(num[0]), p = S(num[1]);

        // δ: the shift back to the left of the axis, s → s − δ, that restores |S11|² + |S21|² = 1 on the samples, by
        // ternary search short of the nearest pole.
        double Unitary(double d) => near.Sum(i =>
        {
            var s = new Complex(-d, W(fHz[i]));
            Complex ev = MatchPoly.EvalComplex(e, s), r = MatchPoly.EvalComplex(f, s) / ev, t = MatchPoly.EvalComplex(p, s) / ev;
            double u = r.Magnitude * r.Magnitude + t.Magnitude * t.Magnitude - 1.0;
            return u * u;
        });
        double dLo = 0.0, dHi = Math.Max(0.0, 0.999 * MatchPoly.Roots(e).Min(r => -r.Real));
        for (int k = 0; k < 100; k++)
        {
            double m1 = dLo + (dHi - dLo) / 3.0, m2 = dHi - (dHi - dLo) / 3.0;
            if (Unitary(m1) < Unitary(m2)) dHi = m2; else dLo = m1;
        }
        double delta = (dLo + dHi) / 2.0;

        // The lossless zeros in ω, each fitted one moved δ back: ω = −j·(z + δ). A reflection zero may stay off the axis
        // (a mismatched filter's do), so F is complex in ω; the transmission zeros are put on it.
        // ponytail: real transmission zeros only, as FilterSpec holds them; complex pairs (§7) would keep their parts.
        Complex[] Zeros(Complex[] a) => [.. MatchPoly.Roots(a).Select(z => -Complex.ImaginaryOne * (z + delta))];
        Complex[] fw = CouplingMatrix.FromRoots(Zeros(f));
        double[] tz = [.. Zeros(p).Select(z => z.Real).Order()];
        double[] pw = MatchPoly.FromRoots(tz.Select(z => (Complex)z));
        double eps = 1.0 / p[0].Magnitude, epsR = nfz == n ? eps / Math.Sqrt(eps * eps - 1.0) : 1.0;

        // |E|² = |F/εR|² + |P/ε|² on the real ω axis: a real polynomial of degree 2N whose roots pair off across the
        // axis; E takes those above it (the left half of s), as Characteristic does.
        var sq = new double[2 * n + 1];
        for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++) sq[i + j] += (fw[i] * Complex.Conjugate(fw[j])).Real / (epsR * epsR);
        for (int i = 0; i <= nfz; i++)
            for (int j = 0; j <= nfz; j++) sq[2 * (n - nfz) + i + j] += pw[i] * pw[j] / (eps * eps);
        Complex[] ew = CouplingMatrix.FromRoots(MatchPoly.Roots(sq).OrderByDescending(r => r.Imaginary).Take(n));
        Complex[] ps = CouplingMatrix.ToS([.. pw.Select(v => (Complex)v)]);
        if ((n - nfz) % 2 == 0) ps = [.. ps.Select(v => v * Complex.ImaginaryOne)];
        double[,] m = Signed(CouplingMatrix.Folded(CouplingMatrix.Transversal(
            new ChebyshevPolynomials(CouplingMatrix.ToS(ew), CouplingMatrix.ToS(fw), ps, eps, epsR))));

        // ── Against the spec, then the target ───────────────────────────────────────────────────────────────────────
        double rlSpec = -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -level / 10.0));
        var checks = new List<FilterCheck>
        {
            new("Centre", centre, f0, Math.Abs(centre - f0) <= tolerance * bw / 2.0),
            new("Bandwidth", f2 - f1, bw, f2 - f1 >= (1.0 - tolerance) * bw),
            new("Ripple", ripple, level, ripple <= (1.0 + tolerance) * level),
            new("Return loss", rl, rlSpec, rl >= (1.0 - tolerance) * rlSpec),
        };
        double[] zeros = [.. tz.Select(w => { double x = w * fbw / 2.0; return f0 * (Math.Sqrt(x * x + 1.0) + x); })];
        for (int i = 0; i < nfz; i++)
        {
            double want = spec.TransmissionZerosHz!.Order().ElementAt(i);
            checks.Add(new FilterCheck($"Zero {i + 1}", zeros[i], want, Math.Abs(zeros[i] - want) <= tolerance * bw / 2.0));
        }
        if (target is not null)
        {
            double[,] t = Signed(target);
            for (int i = 0; i < n + 2; i++)
                for (int j = i; j < n + 2; j++)
                    if (i == j ? i > 0 && i <= n : Math.Abs(t[i, j]) > 1e-9 || Math.Abs(m[i, j]) > tolerance)
                    {
                        var (name, value) = Term(i, j, m[i, j]);
                        checks.Add(new FilterCheck(name, value, Term(i, j, t[i, j]).Value, Math.Abs(m[i, j] - t[i, j]) <= tolerance));
                    }
        }
        return new ExtractedFilter(centre, f2 - f1, ripple, rl, il, delta > 0.0 ? 1.0 / (delta * fbw) : double.PositiveInfinity,
            n, zeros, m,
            nfz < n ? CouplingMatrix.ToPhysical(m, f0, fbw) : null, checks);

        // An entry in band-pass terms (CouplingMatrix.ToPhysical): a resonator's own frequency, an external Q, a k.
        (string Name, double Value) Term(int i, int j, double v)
        {
            double b = fbw * v / 2.0;
            string Node(int k) => k == 0 ? "S" : k == n + 1 ? "L" : k.ToString(CultureInfo.InvariantCulture);
            return (i, j) switch
            {
                _ when i == j => ($"f({i})", f0 * (Math.Sqrt(1.0 + b * b) - b)),
                (0, 1) => ("Qe(S)", 1.0 / (fbw * v * v)),
                _ when i == n && j == n + 1 => ("Qe(L)", 1.0 / (fbw * v * v)),
                _ when i > 0 && j <= n => ($"k({i},{j})", fbw * v),
                _ => ($"M({Node(i)},{Node(j)})", v),
            };
        }
    }

    /// <summary>The matrix with its main line S–1–…–N–L made non-negative by flipping whole resonator rows and columns,
    /// which leaves |S11| and |S21| as they are, so that two matrices of one response compare entry by entry.</summary>
    public static double[,] Signed(double[,] m)
    {
        var r = (double[,])m.Clone();
        int size = r.GetLength(0);
        for (int i = 0; i + 1 < size; i++)
            if (r[i, i + 1] < 0.0)
            {
                for (int k = 0; k < size; k++) r[i + 1, k] = -r[i + 1, k];
                for (int k = 0; k < size; k++) r[k, i + 1] = -r[k, i + 1];
            }
        return r;
    }

    private static double Db(Complex s) => 20.0 * Math.Log10(Math.Max(s.Magnitude, 1e-300));

    private static ParameterRefusalException Refuse(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("synthesis.extract." + code, DiagnosticSeverity.Error, "Filter extraction: " + message, args));
}
