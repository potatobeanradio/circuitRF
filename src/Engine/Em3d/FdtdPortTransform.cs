// brief-em3d-9 R-em3d9-4 — openEMS's port time series to S, in the numeric layer (em-3d.md §5.3).
//
// Pure functions: arrays in, arrays out. No file, no process. OpenEmsRun (src/Design/Em3d) reads the
// probe files and hands their columns here; everything between a time series and an S-parameter is
// circuitRF code, written independently of openEMS's scripting interfaces (the GPL boundary, §5.2).
//
// EACH PROBE'S OWN TIME COLUMN (overview §1f, R-em3d9-4b). openEMS samples a current probe half a time
// step after its voltage probe (F0 Q7: 1.74247895953e-14 s against t = 0, in a 3.48496e-14 s step).
// A transform that assumed one time axis for both would carry a phase error growing with frequency —
// the classic hand-rolled-port bug. So the DFT is evaluated at the requested frequencies DIRECTLY on
// each probe's own samples, Σ x(tₙ)·e^(−jωtₙ)·Δtₙ, never by an FFT on an assumed shared grid.
//
// THE SIGN (R-em3d9-4c). CsxcadWriter places each port's probes so that U is the positive object's
// potential minus the negative object's, and I is the current flowing through the port sheet from
// the negative side to the positive side — which is the current the port delivers INTO the structure
// at its positive terminal. That is openEMS's own lumped-port convention (a voltage probe of weight
// −1 from start to stop, a current probe weighted by the direction from start to stop), confirmed on
// the pinned version by F0: its hand arithmetic on exactly these probes agreed with openEMS's own
// post-processing to 1e-16 (em-3d-f0-findings.md Q7), and gate 8 compares a generated run with F0's.
//
// FROM U AND I TO S — why not Z = U·I⁻¹. With every port excited once, column k of U(ω) and I(ω) is
// run k. The brief asks for Z = U·I⁻¹ and then RfCore's Z-to-S. But I is SINGULAR for any network
// with no Z-matrix — a series element between two ports has I₁ = −I₂ in every run, which is the
// brief's own gate 1 — and ill-conditioned for anything close to one (a short thru line at the bottom
// of a band). S itself is fine there. So the transform uses the same definition rearranged so it
// never inverts I:
//
//     RfCore:  Ẑ = √y·Z·√y,  S = (Ẑ − 1)(Ẑ + 1)⁻¹  =  √y·(Z − Z₀)(Z + Z₀)⁻¹·√z
//     here:    Z = U·I⁻¹  ⇒  S = √y·(U − Z₀·I)·(U + Z₀·I)⁻¹·√z
//
// identical wherever Z exists (gate 2 holds it to RFNetwork.ZToS for a complex Z₀), with RfCore's
// own complex square root, so there is still one definition of a wave. U + Z₀·I is the matrix of
// incident waves; it is singular only when some port's incident wave is not independent of the
// others — a port the excitation never reaches — and that is the error R-em3d9-4d names.

using System.Globalization;
using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Em3d;

/// <summary>One probe's record: its OWN time column, seconds, and its values (volts or amperes).</summary>
public sealed record FdtdProbe(double[] TimeS, double[] Value);

/// <summary>One port's two probes in one run.</summary>
public sealed record FdtdPortProbes(FdtdProbe Voltage, FdtdProbe Current);

/// <summary>
/// S (and the U and I it came from) at each requested frequency, or <see cref="Error"/> — a sentence
/// naming the port and the frequency — when some frequency has no independent incident waves.
/// </summary>
public sealed record FdtdPortResult(
    double[]         FrequenciesHz,
    Complex[][,]     U,
    Complex[][,]     I,
    Mat<Complex>[]   S,
    string?          Error);

public static class FdtdPortTransform
{
    /// <summary>
    /// A pivot this far below the largest incident wave at one frequency makes the incident-wave matrix
    /// singular there. The waves of an unreached port are round-off, some twelve orders below the
    /// excited ones; an ill-posed but real port is many orders above.
    /// </summary>
    public const double SingularRatio = 1e-10;

    /// <summary>
    /// The DFT of one probe at each of <paramref name="frequenciesHz"/>, on the probe's own samples:
    /// Σ x(tₙ)·e^(−jωtₙ)·Δtₙ, with Δtₙ = tₙ₊₁ − tₙ (the last sample repeats the step before it). O(N_t·N_f).
    /// </summary>
    public static Complex[] Dft(FdtdProbe probe, IReadOnlyList<double> frequenciesHz)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var t = probe.TimeS;
        var x = probe.Value;
        int n = Math.Min(t.Length, x.Length);
        var result = new Complex[frequenciesHz.Count];
        if (n == 0) return result;

        var dt = new double[n];
        for (int k = 0; k + 1 < n; k++) dt[k] = t[k + 1] - t[k];
        dt[n - 1] = n > 1 ? dt[n - 2] : 0;

        for (int f = 0; f < frequenciesHz.Count; f++)
        {
            double w = 2 * Math.PI * frequenciesHz[f];
            double re = 0, im = 0;
            for (int k = 0; k < n; k++)
            {
                double a = x[k] * dt[k], ph = w * t[k];
                re += a * Math.Cos(ph);
                im -= a * Math.Sin(ph);
            }
            result[f] = new Complex(re, im);
        }
        return result;
    }

    /// <summary>
    /// S from every run's probes. <paramref name="runs"/>[k][i] is port i's probes in the run that
    /// excited port k — every port excited once, in the order of <paramref name="portNumbers"/>, which
    /// also orders <paramref name="z0"/>, the reference impedances.
    /// </summary>
    public static FdtdPortResult Solve(IReadOnlyList<IReadOnlyList<FdtdPortProbes>> runs,
                                       double[] frequenciesHz, IReadOnlyList<int> portNumbers,
                                       IReadOnlyList<Complex> z0)
    {
        ArgumentNullException.ThrowIfNull(runs);
        int n = portNumbers.Count;
        if (runs.Count != n || runs.Any(r => r.Count != n) || z0.Count != n)
            throw new ArgumentException("There must be one run per port, each carrying every port's probes, and one Z0 per port.");

        int nf = frequenciesHz.Length;
        // DFTs, [run k][port i] → per frequency.
        var uf = new Complex[n, n][];
        var jf = new Complex[n, n][];
        for (int k = 0; k < n; k++)
            for (int i = 0; i < n; i++)
            {
                uf[i, k] = Dft(runs[k][i].Voltage, frequenciesHz);
                jf[i, k] = Dft(runs[k][i].Current, frequenciesHz);
            }

        var sqz = new Complex[n];
        for (int i = 0; i < n; i++) sqz[i] = RFNetwork.ComplexSqrt(z0[i]);

        var U = new Complex[nf][,];
        var I = new Complex[nf][,];
        var S = new Mat<Complex>[nf];
        for (int f = 0; f < nf; f++)
        {
            var u = new Complex[n, n];
            var c = new Complex[n, n];
            var a = new Complex[n, n];     // incident:  U + Z0·I
            var b = new Complex[n, n];     // reflected: U − Z0·I
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    u[i, k] = uf[i, k][f];
                    c[i, k] = jf[i, k][f];
                    a[i, k] = u[i, k] + z0[i] * c[i, k];
                    b[i, k] = u[i, k] - z0[i] * c[i, k];
                }
            U[f] = u;
            I[f] = c;

            var inverse = Invert(a, out int weakRow);
            if (inverse is null)
                return new FdtdPortResult(frequenciesHz, U, I, S,
                    $"Port {portNumbers[weakRow]} has no independent incident wave at " +
                    $"{(frequenciesHz[f] / 1e9).ToString("G6", CultureInfo.InvariantCulture)} GHz: the port matrix is " +
                    "singular there, so no S-parameter can be formed. Usually the port carries no current in any run — " +
                    "it touches nothing, or the structure between its two objects is not connected.");

            // S = √y · B · A⁻¹ · √z
            var s = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    Complex sum = Complex.Zero;
                    for (int k = 0; k < n; k++) sum += b[i, k] * inverse[k, j];
                    s[i, j] = sum / sqz[i] * sqz[j];
                }
            S[f] = s;
        }
        return new FdtdPortResult(frequenciesHz, U, I, S, null);
    }

    /// <summary>
    /// The inverse by Gauss–Jordan with partial pivoting, or null when a pivot falls below
    /// <see cref="SingularRatio"/> of the matrix's largest entry — <paramref name="weakRow"/> is then
    /// the ORIGINAL row (port) whose pivot it was, or the row with no entry at all when there is one.
    /// </summary>
    private static Complex[,]? Invert(Complex[,] m, out int weakRow)
    {
        int n = m.GetLength(0);
        weakRow = 0;
        double scale = 0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) scale = Math.Max(scale, m[i, j].Magnitude);

        // A row of nothing names its port directly — the pivot order would otherwise decide which.
        for (int i = 0; i < n; i++)
        {
            double row = 0;
            for (int j = 0; j < n; j++) row = Math.Max(row, m[i, j].Magnitude);
            if (!(row > SingularRatio * scale)) { weakRow = i; return null; }
        }

        var a = (Complex[,])m.Clone();
        var inv = new Complex[n, n];
        var origin = new int[n];
        for (int i = 0; i < n; i++) { inv[i, i] = Complex.One; origin[i] = i; }

        for (int col = 0; col < n; col++)
        {
            int p = col;
            for (int r = col + 1; r < n; r++)
                if (a[r, col].Magnitude > a[p, col].Magnitude) p = r;
            if (!(a[p, col].Magnitude > SingularRatio * scale)) { weakRow = origin[p]; return null; }
            if (p != col)
            {
                for (int j = 0; j < n; j++)
                {
                    (a[p, j], a[col, j]) = (a[col, j], a[p, j]);
                    (inv[p, j], inv[col, j]) = (inv[col, j], inv[p, j]);
                }
                (origin[p], origin[col]) = (origin[col], origin[p]);
            }
            Complex d = a[col, col];
            for (int j = 0; j < n; j++) { a[col, j] /= d; inv[col, j] /= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                Complex f = a[r, col];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < n; j++) { a[r, j] -= f * a[col, j]; inv[r, j] -= f * inv[col, j]; }
            }
        }
        return inv;
    }
}
