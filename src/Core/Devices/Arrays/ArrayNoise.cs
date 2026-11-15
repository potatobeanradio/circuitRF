// brief-paa-7 R-paa7-1 / R-paa7-2 — a Phased Antenna Array as a receiver: the waves a plane wave puts on its elements, and the noise its ports
// see through their patterns (docs/design/phased-array.md §5).
//
// THE NOISE IS A WAVE SOURCE, b = S·a + c. Its correlation C = ⟨c·cᴴ⟩ (W/Hz, single-sided) is split in two so that the part a passive block
// has always had stays exact and only what the sky adds is integrated (note eq. (5)):
//
//     C = k·Λ^½·(I − S·Sᴴ)·Λ^½  +  k·Σ_d w_d·(T_b(d) − T̄)/η₀ · e_n(d)·e_m*(d)
//     Λ_n = η_n·T̄ + (1 − η_n)·T_phys,     η_n = min(1, R_nn / (I − S·Sᴴ)_nn),     R_nn = Σ_d w_d·|E_n(d)|²/η₀
//
// with E_n the element's terminated embedded pattern per unit incident wave (r-normalised volts), e_n = E_n·e^{jk·û·r_n}, w_d the record grid's
// solid-angle weights and T̄ the brightness averaged over the reference pattern's power. The first term is the network's own: with a uniform sky
// at the physical temperature it IS the Bosma correlation kT·(I − S·Sᴴ) and the second term is exactly zero, so nothing that ran before moves;
// with a cold sky on a lossless record (η = 1, T_b = 0) both are zero. Its diagonal is the classical antenna temperature
// η·T_b,avg + (1 − η)·T_phys (with T_b,avg taken over the element's own pattern), and the second term carries everything else the pattern
// sees. Off the diagonal the uniform part has the sparsity of S·Sᴴ, and the sky's part is
//
//     PAIR BY PAIR at exact level, each element its own pattern (N is the record's port count), or
//     BY OFFSET at isolated and embedded level: one integral per offset that occurs, of the reference (centre) pattern displaced, taken in
//     direction cosines on a grid fine enough for the largest offset (ArraySkyTransform) and HELD AS THAT TABLE (ArrayOffsetTerm): a sky bright
//     at the horizon correlates elements across the whole aperture, so a matrix filled from it would be dense. Each element's diagonal is still
//     its own pattern's. Nothing here forms an N×N array.

using System.Numerics;
using System.Threading;

namespace CircuitRF.Core.Devices.Arrays;

/// <summary>brief-paa-7 — a Hermitian correlation held sparse, with optional rank-one terms: <c>C = Sparse + Σ_r w_r·v_r·v_rᴴ</c>.</summary>
public sealed class ArrayCorrelation
{
    private static long _dense;

    /// <summary>Dense N×N copies made in this process (<see cref="Dense"/>): what a run that must hold none reads.</summary>
    public static long DenseCopies => Interlocked.Read(ref _dense);

    public ArrayCorrelation(ArraySparseMatrix sparse, IReadOnlyList<(Complex[] V, double W)>? lowRank = null, ArrayOffsetTerm? offsets = null)
    {
        Sparse = sparse;
        LowRank = lowRank ?? [];
        Offsets = offsets;
    }

    /// <summary>A term that depends only on the offset between two elements, held as its table and never filled (the sky's part).</summary>
    public ArrayOffsetTerm? Offsets { get; }

    public int Size => Sparse.Size;
    public ArraySparseMatrix Sparse { get; }
    public IReadOnlyList<(Complex[] V, double W)> LowRank { get; }

    /// <summary><c>Σ_ij x_i·C_ij·conj(y_j)</c>.</summary>
    public Complex Form(ReadOnlySpan<Complex> x, ReadOnlySpan<Complex> y)
    {
        var s = Sparse;
        Complex sum = 0;
        for (int i = 0; i < s.Size; i++)
        {
            if (x[i] == Complex.Zero) continue;
            Complex row = 0;
            for (int k = s.RowStart[i]; k < s.RowStart[i + 1]; k++) row += s.Values[k] * Complex.Conjugate(y[s.Columns[k]]);
            sum += x[i] * row;
        }
        foreach (var (v, w) in LowRank)
        {
            Complex xv = 0, yv = 0;
            for (int i = 0; i < v.Length; i++) { xv += x[i] * v[i]; yv += y[i] * v[i]; }
            sum += w * xv * Complex.Conjugate(yv);
        }
        if (Offsets is { } o) sum += o.Form(x, y);
        return sum;
    }

    /// <summary>The entry (i, j).</summary>
    public Complex this[int i, int j]
    {
        get
        {
            Complex c = Sparse[i, j];
            foreach (var (v, w) in LowRank) c += w * v[i] * Complex.Conjugate(v[j]);
            if (Offsets is { } o) c += o[i, j];
            return c;
        }
    }

    /// <summary>The whole matrix, dense: for the analyses that take a part's noise as a dense block (harmonic-balance and envelope noise),
    /// whose arrays are small. Counted (<see cref="DenseCopies"/>).</summary>
    public Complex[,] Dense()
    {
        Interlocked.Increment(ref _dense);
        int n = Size;
        var d = new Complex[n, n];
        for (int i = 0; i < n; i++)
            foreach (var (c, v) in Sparse.Row(i)) d[i, c] += v;
        foreach (var (v, w) in LowRank)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) d[i, j] += w * v[i] * Complex.Conjugate(v[j]);
        if (Offsets is { } o)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) d[i, j] += o[i, j];
        return d;
    }
}

/// <summary>brief-paa-7 R-paa7-2 — the brightness a Phased Antenna Array looks at: Tsky above the horizon (θ ≤ 90°, the array facing +z), a
/// temperature or an expression of the elevation tabulated every 0.01°, and Tground below it.</summary>
public sealed class ArraySky
{
    /// <summary>The elevation table's step, degrees.</summary>
    public const double StepDeg = 0.01;

    private readonly double[]? _sky;     // by elevation, 0…90° in StepDeg steps
    private readonly double _skyConst;

    public ArraySky(double sky, double ground, string description)
    {
        _skyConst = sky;
        Ground = ground;
        Description = description;
    }

    public ArraySky(double[] skyByElevation, double ground, string description)
    {
        _sky = skyByElevation;
        Ground = ground;
        Description = description;
    }

    public double Ground { get; }

    /// <summary>How it was stated, for a run's notes.</summary>
    public string Description { get; }

    /// <summary>The same temperature in every direction.</summary>
    public bool Uniform => _sky is null && _skyConst == Ground;

    /// <summary>The uniform temperature; NaN when it is not uniform.</summary>
    public double UniformKelvin => Uniform ? Ground : double.NaN;

    /// <summary>The brightness at θ (radians from boresight).</summary>
    public double At(double thetaRad)
    {
        double thetaDeg = thetaRad * 180 / Math.PI;
        if (thetaDeg > 90 + 1e-9) return Ground;
        if (_sky is null) return _skyConst;
        double el = Math.Clamp(90 - thetaDeg, 0, 90) / StepDeg;
        int i = Math.Min((int)Math.Floor(el), _sky.Length - 2);
        double f = el - i;
        return f == 0 ? _sky[i] : _sky[i] + f * (_sky[i + 1] - _sky[i]);
    }
}

/// <summary>
/// brief-paa-7 — what a Phased Antenna Array's record says of its elements' far field at one of its frequencies, as the receive and noise halves
/// read it. src/Design supplies it (<see cref="PhasedArrayModel.RadiationSource"/>): the record's far field is read there, beside the fields
/// analysis that reads the same patterns, so the two cannot disagree.
/// </summary>
public sealed class ArrayRadiation
{
    /// <summary>The record's frequency the patterns are at.</summary>
    public required double FrequencyHz { get; init; }

    /// <summary>The record's file name.</summary>
    public required string Record { get; init; }

    /// <summary>Each direction of the record's grid, row-major [theta, phi], radians.</summary>
    public required double[] ThetaRad { get; init; }
    public required double[] PhiRad { get; init; }

    /// <summary>Each direction's solid angle, the quadrature a radiated power on this grid is integrated with (sr).</summary>
    public required double[] Weight { get; init; }

    /// <summary>Each distinct pattern on the grid, per unit incident wave, referred to its own element (r-normalised volts).</summary>
    public required Complex[][] ETheta { get; init; }
    public required Complex[][] EPhi { get; init; }

    /// <summary>The pattern each element (0-based) takes.</summary>
    public required int[] PatternOf { get; init; }

    /// <summary>Pattern p at (θ, φ) in radians: read on the grid, interpolated elsewhere as the fields analysis interpolates.</summary>
    public required Func<int, double, double, (Complex ETheta, Complex EPhi)> At { get; init; }

    /// <summary>The free-space impedance and the speed of light the patterns' power is stated with.</summary>
    public required double Eta0 { get; init; }
    public required double C0 { get; init; }

    public int Directions => ThetaRad.Length;
}

/// <summary>brief-paa-7 R-paa7-1 — one incident wave as the component was given it: its arrival direction (radians), power density (dBm/m²),
/// frequency and polarization (p_θ, p_φ).</summary>
public sealed record ArrayIncidentWave(double ThetaRad, double PhiRad, double PdDbm, double Hz, Complex PolTheta, Complex PolPhi)
{
    /// <summary>The power density, W/m².</summary>
    public double DensityW => Math.Pow(10, (PdDbm - 30) / 10);
}

/// <summary>brief-paa-7 — one array's noise correlation at one frequency, with what its run's notes say of it.</summary>
public sealed record ArrayNoiseResult(ArrayCorrelation Correlation, string? Note, int Offsets, int OffsetsReaching);

/// <summary>brief-paa-7 R-paa7-2 — the noise-wave correlation of an array's ports (note eq. (5)).</summary>
public static class ArrayNoise
{
    /// <summary>The fraction of the largest diagonal entry the run's notes measure the sky's reach in offset by (isolated and embedded levels).
    /// Nothing is dropped at it: the term is held as its offset table (<see cref="ArrayOffsetTerm"/>).</summary>
    public const double ReachFraction = 1e-4;

    /// <summary>Boltzmann's constant.</summary>
    public const double Boltzmann = 1.380649e-23;

    private static long _offsetIntegrals, _pairIntegrals;

    internal static void CountOffsets(int n) => Interlocked.Add(ref _offsetIntegrals, n);
    internal static void CountPair() => Interlocked.Increment(ref _pairIntegrals);

    /// <summary>Offset integrals taken in this process, and pair integrals: the counters by which "never pair by pair" is held.</summary>
    public static long OffsetIntegrals => Interlocked.Read(ref _offsetIntegrals);
    public static long PairIntegrals => Interlocked.Read(ref _pairIntegrals);

    /// <summary>
    /// The correlation of the waves leaving the ports of an array whose stamped S at <paramref name="hz"/> is <paramref name="s"/>, at physical
    /// temperature <paramref name="kelvin"/>. <paramref name="sky"/> null (or uniform at <paramref name="kelvin"/>) is the network's own Bosma
    /// noise and needs no pattern. <paramref name="active"/> says which elements radiate (an Elements-mode element switched off is open at its
    /// pin and terminated inside: it neither radiates nor reaches the circuit).
    /// </summary>
    public static ArrayNoiseResult Correlation(ArraySparseMatrix s, double kelvin, ArraySky? sky, ArrayRadiation? rad, ArrayLattice lattice, double hz,
                                               bool pairwise, int referencePattern, Func<int, bool> active)
    {
        int n = s.Size;
        double k = Boltzmann;
        var sst = s.TimesAdjoint();
        // D = I − S·Sᴴ, row by row.
        var dRows = new List<(int Column, Complex Value)>[n];
        var dDiag = new double[n];
        for (int i = 0; i < n; i++)
        {
            var row = new List<(int, Complex)>();
            bool diag = false;
            foreach (var (c, v) in sst.Row(i))
            {
                Complex d = (c == i ? 1 : 0) - v;
                if (c == i) { diag = true; dDiag[i] = d.Real; }
                row.Add((c, d));
            }
            if (!diag) { row.Add((i, 1)); dDiag[i] = 1; }
            dRows[i] = row;
        }

        if (sky is null || (sky.Uniform && sky.UniformKelvin == kelvin))
        {
            for (int i = 0; i < n; i++)
                for (int q = 0; q < dRows[i].Count; q++) dRows[i][q] = (dRows[i][q].Column, k * kelvin * dRows[i][q].Value);
            return new ArrayNoiseResult(new ArrayCorrelation(ArraySparseMatrix.FromRows(n, dRows)), null, 0, 0);
        }
        if (rad is null) throw new InvalidOperationException("the sky's brightness is read through the record's patterns, and none was supplied.");

        int nd = rad.Directions, np = rad.ETheta.Length;
        double eta = rad.Eta0;
        var tb = new double[nd];
        for (int d = 0; d < nd; d++) tb[d] = sky.At(rad.ThetaRad[d]);
        var power = new double[np][];
        var radiated = new double[np];
        for (int p = 0; p < np; p++)
        {
            power[p] = new double[nd];
            for (int d = 0; d < nd; d++)
            {
                double pw = Norm(rad.ETheta[p][d]) + Norm(rad.EPhi[p][d]);
                power[p][d] = pw;
                radiated[p] += rad.Weight[d] * pw / eta;
            }
        }
        double tBar = MeanBrightness(rad, sky, referencePattern);

        // Λ: each element's uniform temperature, the sky's average over its radiated share and the metal's over the rest.
        var lambda = new double[n];
        for (int i = 0; i < n; i++)
        {
            double eff = dDiag[i] > 0 && active(i) ? Math.Clamp(radiated[rad.PatternOf[i]] / dDiag[i], 0, 1) : 0;
            lambda[i] = eff * tBar + (1 - eff) * kelvin;
        }
        var rows = new List<(int Column, Complex Value)>[n];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new List<(int, Complex)>(dRows[i].Count);
            foreach (var (c, v) in dRows[i]) rows[i].Add((c, k * Math.Sqrt(lambda[i] * lambda[c]) * v));
        }
        if (sky.Uniform)
            return new ArrayNoiseResult(new ArrayCorrelation(ArraySparseMatrix.FromRows(n, rows)),
                $"Noise from the sky: {sky.Description}, the same in every direction, so only each element's radiation efficiency is read from its pattern.",
                0, 0);

        // The sky's part: g_d = k·(T_b − T̄)·w_d/η₀.
        var g = new double[nd];
        for (int d = 0; d < nd; d++) g[d] = k * (tb[d] - tBar) * rad.Weight[d] / eta;
        var ownDiag = new double[np];
        for (int p = 0; p < np; p++)
            for (int d = 0; d < nd; d++) ownDiag[p] += g[d] * power[p][d];
        double largestDiag = 0;
        for (int i = 0; i < n; i++)
        {
            if (!active(i)) continue;
            rows[i].Add((i, ownDiag[rad.PatternOf[i]]));
            largestDiag = Math.Max(largestDiag, k * lambda[i] * dDiag[i] + ownDiag[rad.PatternOf[i]]);
        }

        double wave = 2 * Math.PI * hz / rad.C0;
        if (pairwise)
        {
            var e = PhasedPatterns(rad, lattice, wave);
            for (int i = 0; i < n; i++)
            {
                if (!active(i)) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (!active(j)) continue;
                    Complex v = PairSum(g, e.Theta[i], e.Phi[i], e.Theta[j], e.Phi[j]);
                    Interlocked.Increment(ref _pairIntegrals);
                    rows[i].Add((j, v));
                    rows[j].Add((i, Complex.Conjugate(v)));
                }
            }
            return new ArrayNoiseResult(new ArrayCorrelation(ArraySparseMatrix.FromRows(n, rows)),
                $"{SkyNote(sky, tBar)} The sky's part is integrated pair by pair over each element's own pattern ({n} elements, the record's own).",
                0, 0);
        }

        var transform = new ArraySkyTransform(rad, lattice, hz, sky, tBar, referencePattern);
        var table = transform.Table();
        // Held as the table: a term of the offset alone is block-Toeplitz, and a sky bright at the horizon correlates elements across the whole
        // aperture (a ring's transform decays as 1/√(kΔ)), so a matrix filled from it would be dense. Nothing is truncated; the note says how
        // far the term stays above the stated fraction of the diagonal, which is what a reader wants to know of its reach.
        double floor = ReachFraction * largestDiag;
        int above = 0, maxDr = 0, maxHc = 0;
        foreach (var (key, v) in table)
        {
            if (key == (0, 0) || v.Magnitude < floor) continue;
            above++;
            maxDr = Math.Max(maxDr, Math.Abs(key.Dr));
            maxHc = Math.Max(maxHc, Math.Abs(key.Hc));
        }
        string reach = above == 0 ? $"no offset reaches {ReachFraction:G2} of the largest diagonal"
            : $"{above} of them reach {ReachFraction:G2} of the largest diagonal, out to ±{maxDr} rows and ±{maxHc / 2.0:G4} columns";
        var offsets = new ArrayOffsetTerm(lattice, table, active);
        return new ArrayNoiseResult(new ArrayCorrelation(ArraySparseMatrix.FromRows(n, rows), offsets: offsets),
            $"{SkyNote(sky, tBar)} The sky's part is integrated once per element offset ({table.Count} offsets, the reference element's pattern " +
            $"displaced, in direction cosines on a {transform.Grid} grid per lattice period; each element's own pattern on its diagonal) and held as " +
            $"that table, never filled into a matrix, so none of it is dropped: {reach}.",
            table.Count, above);
    }

    /// <summary>T̄: the brightness averaged over the reference pattern's power on the record's grid.</summary>
    public static double MeanBrightness(ArrayRadiation rad, ArraySky sky, int referencePattern)
    {
        double num = 0, den = 0, plain = 0;
        for (int d = 0; d < rad.Directions; d++)
        {
            double tb = sky.At(rad.ThetaRad[d]);
            double pw = Norm(rad.ETheta[referencePattern][d]) + Norm(rad.EPhi[referencePattern][d]);
            num += rad.Weight[d] * tb * pw;
            den += rad.Weight[d] * pw;
            plain += tb;
        }
        return den > 0 ? num / den : plain / rad.Directions;
    }

    private static string SkyNote(ArraySky sky, double tBar)
        => $"Noise from the sky: {sky.Description}, {tBar:G5} K averaged over the element's pattern.";

    private static double Norm(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

    private static Complex PairSum(double[] g, Complex[] ti, Complex[] pi, Complex[] tj, Complex[] pj)
    {
        Complex sum = 0;
        for (int d = 0; d < g.Length; d++)
            if (g[d] != 0) sum += g[d] * (ti[d] * Complex.Conjugate(tj[d]) + pi[d] * Complex.Conjugate(pj[d]));
        return sum;
    }

    private static (Complex[][] Theta, Complex[][] Phi) PhasedPatterns(ArrayRadiation rad, ArrayLattice lattice, double wave)
    {
        int n = lattice.Count;
        var t = new Complex[n][];
        var p = new Complex[n][];
        for (int i = 0; i < n; i++) (t[i], p[i]) = Phased(rad, rad.PatternOf[i], lattice.Position(i), wave);
        return (t, p);
    }

    private static (Complex[] Theta, Complex[] Phi) Phased(ArrayRadiation rad, int pattern, (double X, double Y) at, double wave)
    {
        int nd = rad.Directions;
        var t = new Complex[nd];
        var p = new Complex[nd];
        for (int d = 0; d < nd; d++)
        {
            double st = Math.Sin(rad.ThetaRad[d]);
            var (sn, cs) = Math.SinCos(wave * st * (Math.Cos(rad.PhiRad[d]) * at.X + Math.Sin(rad.PhiRad[d]) * at.Y));
            var ph = new Complex(cs, sn);
            t[d] = rad.ETheta[pattern][d] * ph;
            p[d] = rad.EPhi[pattern][d] * ph;
        }
        return (t, p);
    }
}

/// <summary>
/// brief-paa-7 R-paa7-1 — the wave an incident plane wave puts on each element, by reciprocity from its embedded pattern (note eq. (4)):
///
///     c_n = j·λ·√(2S/η₀)·(p̂·E_n(θ,φ))·e^{jk·û·r_n}
///
/// in the peak-wave convention of the co-simulation (power ½|c|²), with S the power density (W/m²), p̂ the wave's unit polarization in
/// (θ̂, φ̂) of its arrival direction and E_n the element's terminated embedded pattern per unit incident wave (r-normalised volts). The
/// constant follows from the realized gain of a pattern so normalised, G_r = 4π|E|²/η₀: a matched element in a matched polarization delivers
/// ½|c|² = S·λ²·G_r/4π. The j is the reciprocity theorem's (an open-circuit voltage is E_inc·l_e, and l_e = j·2λ·√Z0·E/η₀ per unit wave).
/// </summary>
public static class ArrayReceive
{
    /// <summary>The polarization a word or an angle names, as (p_θ, p_φ). <c>rhcp</c> is the wave an RHCP antenna (the far-field cubes' IEEE
    /// sense, E ∝ θ̂ − jφ̂) receives whole; an angle is linear, measured from θ̂ towards φ̂.</summary>
    public static (Complex Theta, Complex Phi) Polarization(string word, double? angleRad)
    {
        double r = 1 / Math.Sqrt(2);
        return word.Trim().ToLowerInvariant() switch
        {
            "theta" => (1, 0),
            "phi" => (0, 1),
            "rhcp" => (r, new Complex(0, r)),
            "lhcp" => (r, new Complex(0, -r)),
            _ when angleRad is { } a => (Math.Cos(a), Math.Sin(a)),
            _ => throw new ArgumentException($"'{word}' is not a polarization: it is theta, phi, rhcp, lhcp or an angle."),
        };
    }

    /// <summary>Each element's received wave (peak, √W) for a plane wave from (θ, φ) radians of power density <paramref name="powerDensity"/>
    /// W/m² at <paramref name="hz"/> in polarization <paramref name="pol"/>; zero where <paramref name="active"/> says the element is not on.</summary>
    public static Complex[] Waves(ArrayRadiation rad, ArrayLattice lattice, double thetaRad, double phiRad, double powerDensity, double hz,
                                  (Complex Theta, Complex Phi) pol, Func<int, bool> active)
    {
        int n = lattice.Count;
        double lambda = rad.C0 / hz, wave = 2 * Math.PI * hz / rad.C0;
        Complex scale = new Complex(0, lambda * Math.Sqrt(2 * powerDensity / rad.Eta0));
        double st = Math.Sin(thetaRad), u = st * Math.Cos(phiRad), v = st * Math.Sin(phiRad);
        var patterns = new Dictionary<int, Complex>();
        var c = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            if (!active(i)) continue;
            int p = rad.PatternOf[i];
            if (!patterns.TryGetValue(p, out Complex along))
            {
                var (et, ep) = rad.At(p, thetaRad, phiRad);
                patterns[p] = along = pol.Theta * et + pol.Phi * ep;
            }
            var (x, y) = lattice.Position(i);
            var (sn, cs) = Math.SinCos(wave * (u * x + v * y));
            c[i] = scale * along * new Complex(cs, sn);
        }
        return c;
    }
}

/// <summary>
/// brief-paa-7 R-paa7-2 — the sky's part of an array's noise correlation by element offset, as a Fourier integral over direction cosines:
///
///     T(Δ) = (k/η₀)·∫∫_{u²+v²&lt;1} (T_b − T̄)·P(u,v)/cosθ · e^{jk(u·Δx + v·Δy)} du dv        (dΩ = du·dv/cosθ; both hemispheres where the record has both)
///
/// with P the reference pattern's power |E_θ|² + |E_φ|². Δ runs over the lattice's offsets, so only the integrand PERIODIZED by one lattice
/// period in (u, v) — λ/dx by λ/dy (λ/(dx/2) on a triangular lattice, whose odd rows sit half a pitch over) — is ever seen: it is sampled on a
/// fine grid of that period, the element pattern interpolated bilinearly from the record's grid (smooth over the whole sky: it is one
/// element's), and each offset is that grid's Fourier coefficient, a separable DFT. On the record's own 1° or 5° grid the same integral is
/// aliased past a few wavelengths of offset — the phase turns more than a radian between samples — and reads as a correlation that never
/// decays; on this one every offset of a 64 × 64 array is resolved. <see cref="Pair"/> is the same sum taken pair by pair, which the table must
/// equal.
/// </summary>
public sealed class ArraySkyTransform
{
    private readonly ArrayLattice _lattice;
    private readonly double _wave, _du, _dv, _pu, _pv;
    private readonly int _nu, _nv;
    private readonly bool _tri;
    private readonly List<(int Iu, int Iv, double F)> _samples = [];
    private readonly double[,] _cells;

    public ArraySkyTransform(ArrayRadiation rad, ArrayLattice lattice, double hz, ArraySky sky, double tBar, int referencePattern)
    {
        _lattice = lattice;
        _tri = lattice.Kind == ArrayLatticeKind.Tri;
        double lambda = rad.C0 / hz;
        _wave = 2 * Math.PI / lambda;
        double xStep = _tri ? lattice.Dx / 2 : lattice.Dx;
        _pu = lattice.Cols > 1 || _tri ? lambda / xStep : 2.0;
        _pv = lattice.Rows > 1 ? lambda / lattice.Dy : 2.0;
        int range = Math.Max(2 * lattice.Cols, 2 * lattice.Rows) * (_tri ? 2 : 1);
        _nu = Size(range, _pu);
        _nv = Size(range, _pv);
        _du = _pu / _nu;
        _dv = _pv / _nv;
        _cells = new double[_nu, _nv];

        var power = new PatternPower(rad, referencePattern);
        bool lower = rad.ThetaRad.Max() > Math.PI / 2 + 1e-9;
        double scale = ArrayNoise.Boltzmann / rad.Eta0 * _du * _dv;
        int iMax = (int)Math.Ceiling(1 / _du), jMax = (int)Math.Ceiling(1 / _dv);
        for (int i = -iMax; i <= iMax; i++)
        {
            double u = i * _du;
            for (int j = -jMax; j <= jMax; j++)
            {
                double v = j * _dv, rho2 = u * u + v * v;
                if (rho2 >= 1) continue;
                double cos = Math.Sqrt(1 - rho2), theta = Math.Asin(Math.Sqrt(rho2)), phi = Math.Atan2(v, u);
                double f = (sky.At(theta) - tBar) * power.At(theta, phi);
                if (lower) f += (sky.At(Math.PI - theta) - tBar) * power.At(Math.PI - theta, phi);
                if (f == 0) continue;
                f *= scale / cos;
                _samples.Add((i, j, f));
                _cells[Mod(i, _nu), Mod(j, _nv)] += f;
            }
        }
    }

    /// <summary>The sampling, for a run's notes: points per period in u and v.</summary>
    public string Grid => $"{_nu} × {_nv}";

    private static int Size(int range, double period)
    {
        // at least four samples per offset the lattice holds, and fine enough to resolve the sky's features: δu ≤ 1/256
        int n = 64;
        while (n < 4 * range || period / n > 1.0 / 256) n *= 2;
        return n;
    }

    private static int Mod(int i, int n) => ((i % n) + n) % n;

    /// <summary>The integral at every offset (Δrow, Δx in half column pitches) the lattice holds; Δ = r_n − r_m.</summary>
    public Dictionary<(int Dr, int Hc), Complex> Table()
    {
        int r = _lattice.Rows, c = _lattice.Cols;
        int hcMax = 2 * (c - 1) + (_tri ? 1 : 0), mus = _tri ? 1 : 2;
        // offsets in the units of the periodization: m_u = hc (triangular) or hc/2 (rectangular), m_v = −dr
        var mu = new List<int>();
        for (int hc = -hcMax; hc <= hcMax; hc++) if (_tri || (hc & 1) == 0) mu.Add(hc);
        // along u first: X[m_u][iv] = Σ_iu cells[iu, iv]·e^{j2π·iu·m/N_u}
        var along = new Complex[mu.Count][];
        Parallel.For(0, mu.Count, q =>
        {
            int m = _tri ? mu[q] : mu[q] / 2;
            var row = new Complex[_nv];
            var tw = new Complex[_nu];
            for (int iu = 0; iu < _nu; iu++) tw[iu] = Complex.FromPolarCoordinates(1, 2 * Math.PI * ((long)iu * m % _nu) / _nu);
            for (int iu = 0; iu < _nu; iu++)
                for (int iv = 0; iv < _nv; iv++)
                    if (_cells[iu, iv] != 0) row[iv] += _cells[iu, iv] * tw[iu];
            along[q] = row;
        });
        var table = new Dictionary<(int, int), Complex>();
        var lockObj = new object();
        Parallel.For(-(r - 1), r, dr =>
        {
            int m = -dr;
            var tw = new Complex[_nv];
            for (int iv = 0; iv < _nv; iv++) tw[iv] = Complex.FromPolarCoordinates(1, 2 * Math.PI * ((long)Mod(iv * m, _nv)) / _nv);
            var local = new List<((int, int), Complex)>(mu.Count);
            for (int q = 0; q < mu.Count; q++)
            {
                Complex sum = 0;
                for (int iv = 0; iv < _nv; iv++) sum += along[q][iv] * tw[iv];
                local.Add(((dr, mu[q]), sum));
            }
            lock (lockObj) foreach (var (k, v) in local) table[k] = v;
        });
        _ = mus;
        ArrayNoise.CountOffsets(table.Count);
        return table;
    }

    /// <summary>The same integral between elements <paramref name="i"/> and <paramref name="j"/> (0-based), summed pair by pair over the samples.</summary>
    public Complex Pair(int i, int j)
    {
        var (xi, yi) = _lattice.Position(i);
        var (xj, yj) = _lattice.Position(j);
        double dx = xi - xj, dy = yi - yj;
        double re = 0, im = 0;
        foreach (var (iu, iv, f) in _samples)
        {
            var (sn, cs) = Math.SinCos(_wave * (iu * _du * dx + iv * _dv * dy));
            re += f * cs;
            im += f * sn;
        }
        ArrayNoise.CountPair();
        return new Complex(re, im);
    }

    /// <summary>A pattern's power on the record's grid, bilinear in θ and φ (periodic in φ when the grid closes the circle), zero past its θ.</summary>
    private sealed class PatternPower
    {
        private readonly double[] _theta, _phi, _p;
        private readonly bool _periodic;

        public PatternPower(ArrayRadiation rad, int pattern)
        {
            _theta = [.. rad.ThetaRad.Distinct().OrderBy(x => x)];
            _phi = [.. rad.PhiRad.Distinct().OrderBy(x => x)];
            _p = new double[rad.Directions];
            for (int d = 0; d < _p.Length; d++)
            {
                var a = rad.ETheta[pattern][d];
                var b = rad.EPhi[pattern][d];
                _p[d] = a.Real * a.Real + a.Imaginary * a.Imaginary + b.Real * b.Real + b.Imaginary * b.Imaginary;
            }
            double step = _phi.Length > 1 ? _phi[1] - _phi[0] : 2 * Math.PI;
            _periodic = _phi.Length == 1 || Math.Abs(_phi[^1] + step - _phi[0] - 2 * Math.PI) < 1e-6;
        }

        public double At(double theta, double phi)
        {
            if (theta < _theta[0] - 1e-12 || theta > _theta[^1] + 1e-12) return 0;
            int i = Bracket(_theta, theta), i1 = Math.Min(i + 1, _theta.Length - 1);
            double ft = i1 == i ? 0 : (theta - _theta[i]) / (_theta[i1] - _theta[i]);
            int np = _phi.Length, j, j1;
            double fp;
            if (np == 1) { j = j1 = 0; fp = 0; }
            else if (_periodic)
            {
                double p = _phi[0] + ((phi - _phi[0]) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
                j = Bracket(_phi, p);
                j1 = (j + 1) % np;
                double hi = j1 == 0 ? _phi[0] + 2 * Math.PI : _phi[j1];
                fp = (p - _phi[j]) / (hi - _phi[j]);
            }
            else
            {
                double p = Math.Clamp(phi < _phi[0] ? phi + 2 * Math.PI : phi, _phi[0], _phi[^1]);
                j = Bracket(_phi, p);
                j1 = Math.Min(j + 1, np - 1);
                fp = j1 == j ? 0 : (p - _phi[j]) / (_phi[j1] - _phi[j]);
            }
            double P(int a, int b) => _p[a * np + b];
            return (1 - ft) * ((1 - fp) * P(i, j) + fp * P(i, j1)) + ft * ((1 - fp) * P(i1, j) + fp * P(i1, j1));
        }

        private static int Bracket(double[] axis, double x)
        {
            int lo = 0, hi = axis.Length - 1;
            if (x >= axis[hi]) return hi;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (axis[mid] <= x) lo = mid; else hi = mid;
            }
            return lo;
        }
    }
}

/// <summary>
/// brief-paa-7 — a correlation term that depends only on the offset between two elements: <c>C_nm = T(r_n − r_m)</c> for n ≠ m, both radiating,
/// held as the table of T over the offsets a lattice holds (at most (2R−1)·(4C−1) numbers) and never filled. A quadratic form is a sum over
/// element pairs, O(N²) multiply-adds and no N×N storage.
/// </summary>
public sealed class ArrayOffsetTerm
{
    private readonly ArrayLattice _lattice;
    private readonly Complex[,] _t;        // [dr + R − 1, hc + hcMax]
    private readonly bool[] _active;
    private readonly int _hcMax;
    private readonly int[] _shift;          // each row's half-pitch shift, in half pitches

    public ArrayOffsetTerm(ArrayLattice lattice, IReadOnlyDictionary<(int Dr, int Hc), Complex> table, Func<int, bool> active)
    {
        _lattice = lattice;
        int r = lattice.Rows, c = lattice.Cols;
        _hcMax = 2 * (c - 1) + (lattice.Kind == ArrayLatticeKind.Tri ? 1 : 0);
        _t = new Complex[2 * r - 1, 2 * _hcMax + 1];
        foreach (var ((dr, hc), v) in table)
            if (Math.Abs(dr) < r && Math.Abs(hc) <= _hcMax) _t[dr + r - 1, hc + _hcMax] = v;
        _active = [.. Enumerable.Range(0, lattice.Count).Select(active)];
        // ArrayLattice.RowShift: half a pitch on every odd row of a triangular lattice (less a mean common to all rows, which cancels here)
        _shift = [.. Enumerable.Range(0, r).Select(row => lattice.Kind == ArrayLatticeKind.Tri && row % 2 == 1 ? 1 : 0)];
        Offsets = table.Count;
    }

    /// <summary>How many offsets the table holds.</summary>
    public int Offsets { get; }

    public Complex this[int i, int j]
    {
        get
        {
            if (i == j || !_active[i] || !_active[j]) return Complex.Zero;
            int c = _lattice.Cols, ri = i / c, rj = j / c;
            int hc = 2 * (i % c - j % c) + _shift[ri] - _shift[rj];
            return _t[ri - rj + _lattice.Rows - 1, hc + _hcMax];
        }
    }

    /// <summary><c>Σ_{n≠m} x_n·T(r_n − r_m)·conj(y_m)</c>.</summary>
    public Complex Form(ReadOnlySpan<Complex> x, ReadOnlySpan<Complex> y)
    {
        int n = _lattice.Count, c = _lattice.Cols, r = _lattice.Rows;
        var yc = new Complex[n];
        for (int m = 0; m < n; m++) yc[m] = _active[m] ? Complex.Conjugate(y[m]) : Complex.Zero;
        var xs = x.ToArray();
        var partial = new Complex[n];
        Parallel.For(0, n, i =>
        {
            if (!_active[i] || xs[i] == Complex.Zero) return;
            int ri = i / c, ci = i % c;
            Complex sum = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == i || yc[j] == Complex.Zero) continue;
                int rj = j / c;
                sum += _t[ri - rj + r - 1, 2 * (ci - j % c) + _shift[ri] - _shift[rj] + _hcMax] * yc[j];
            }
            partial[i] = xs[i] * sum;
        });
        Complex total = 0;
        foreach (var v in partial) total += v;
        return total;
    }
}
