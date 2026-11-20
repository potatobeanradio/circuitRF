using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Diagnostics;
using RfCore.Stability;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>Where an MCRC realiser maps k and Qe to geometry (docs/design/filter-synthesis.md §6b).</summary>
public enum CouplingRefine
{
    /// <summary>The part's own circuit model (§4).</summary>
    Circuit,

    /// <summary>Curves measured by an EM solver (<see cref="ICouplingCurveSolver"/>), around the circuit model's answer.</summary>
    Em,
}

/// <summary>
/// The seam to an EM solver, so Core stays EM-free: the S-parameters of a two-pin plan at each frequency, port 1 on pin 1
/// and port 2 on pin 2, between 50 Ω ports. The Design layer's Planar MoM implements it.
/// </summary>
public interface ICouplingCurveSolver
{
    Complex[][,] Solve(PassivePlan plan, MicrostripSubstrate s, IReadOnlyList<double> fHz);
}

/// <summary>A measured curve, <see cref="Y"/> against <see cref="X"/> (k against the gap, Qe against the tap), sorted by
/// X and read backwards by piecewise-linear interpolation in ln Y, as k(S) falls about exponentially.</summary>
public sealed record CouplingCurve(double[] X, double[] Y)
{
    /// <summary>The curve through each of <paramref name="xs"/>, measured.</summary>
    public static CouplingCurve Through(IEnumerable<double> xs, Func<double, double> measure)
    {
        var x = xs.Distinct().Order().ToArray();
        return new CouplingCurve(x, [.. x.Select(measure)]);
    }

    /// <summary>The X at which the curve reaches <paramref name="y"/>; refused outside the measured span, as a curve
    /// three points long is not to be extrapolated.</summary>
    public double Where(double y, string what)
    {
        for (int i = 1; i < X.Length; i++)
            if ((Y[i - 1] - y) * (Y[i] - y) <= 0.0 && Y[i] != Y[i - 1])
                return X[i - 1] + (X[i] - X[i - 1]) * Math.Log(y / Y[i - 1]) / Math.Log(Y[i] / Y[i - 1]);
        throw CouplingCurves.Refusal("out-of-curve", "{what} lies outside the EM curve's {lo} to {hi} between {x0} and "
            + "{x1} m.", ("what", what), ("lo", Y.Min()), ("hi", Y.Max()), ("x0", X[0]), ("x1", X[^1]));
    }
}

/// <summary>
/// Coupling coefficients and external Q measured by an EM solver (docs/design/filter-synthesis.md §6b), on the benches
/// <see cref="CoupledResonatorDesign.FromCouplings"/> uses (J.-S. Hong and M. J. Lancaster, <i>Microstrip Filters for
/// RF/Microwave Applications</i>, Wiley, 2001, <see href="https://doi.org/10.1002/0471221619">10.1002/0471221619</see>,
/// §8.2). Each bench is swept over <see cref="Points"/> frequencies, ±15 % about its circuit-model resonance; a lightly fed
/// resonance is far narrower than that grid's step, so each is located as a pole of a rational fit to the samples
/// (<see cref="Poles"/>) rather than read off the samples.
/// </summary>
public static class CouplingCurves
{
    /// <summary>Frequencies per bench.</summary>
    public const int Points = 21;

    /// <summary>k from a pair's two resonances, (f2² − f1²)/(f2² + f1²).</summary>
    public static double K(double f1, double f2) => (f2 * f2 - f1 * f1) / (f2 * f2 + f1 * f1);

    /// <summary>One point of k(S): a lightly fed <paramref name="pair"/> (<see cref="CoupledResonatorDesign.LightPair"/>,
    /// or one of the cross square's own pairs), swept about <paramref name="fc"/>, its two resonances S21's two poles.</summary>
    public static double CouplingOf(ICouplingCurveSolver em, PassivePlan pair, MicrostripSubstrate s, double fc)
    {
        var f = Sweep(fc);
        var p = Poles(f, [.. em.Solve(pair, s, f).Select(m => m[1, 0])], 2);
        return K(p[0].Real, p[1].Real);
    }

    /// <summary>One point of Qe(Tap): <paramref name="one"/> tapped resonator, swept about <paramref name="fc"/> and singly
    /// loaded (pin 2's tap left open, S11 + S12·S21/(1 − S22)), Qe from S11's phase (<see cref="ExternalQ"/>).</summary>
    public static double ExternalQOf(ICouplingCurveSolver em, PassivePlan one, MicrostripSubstrate s, double fc)
    {
        var f = Sweep(fc);
        return ExternalQ(f, [.. em.Solve(one, s, f).Select(m => m[0, 0] + m[0, 1] * m[1, 0] / (1.0 - m[1, 1]))]);
    }

    /// <summary>
    /// A singly loaded resonator's external Q from its reflection: about the resonance S11 = (1 − j·2Qe·δ)/(1 + j·2Qe·δ)
    /// up to a slow phase, whose phase turns ±90° from its value at f0 at δ = ±1/(2Qe), so Qe = f0/Δf±90° (Hong and
    /// Lancaster, §8.2). The ±90° points are f0 ± |Im f_p| about the reflection's one pole f_p, so Qe = Re f_p/(2·|Im f_p|).
    /// </summary>
    public static double ExternalQ(IReadOnlyList<double> fHz, IReadOnlyList<Complex> s11)
    {
        var p = Poles(fHz, s11, 1)[0];
        return p.Real / (2.0 * Math.Abs(p.Imaginary));
    }

    /// <summary>
    /// The <paramref name="count"/> poles of y(f), sorted by their real parts, from a rational fit
    /// N(x)/D(x) over x = (f − fc)/(half span), N of degree count + 1 (a slow background) and D monic of degree count
    /// (<see cref="Rational"/>).
    /// </summary>
    public static Complex[] Poles(IReadOnlyList<double> fHz, IReadOnlyList<Complex> y, int count)
    {
        double fc = (fHz[0] + fHz[^1]) / 2.0, half = (fHz[^1] - fHz[0]) / 2.0;
        var (_, d) = Rational([.. fHz.Select(f => (f - fc) / half)], [y], [count + 1], count);
        return [.. Roots(d).Select(r => fc + half * r).OrderBy(r => r.Real)];
    }

    /// <summary>
    /// Responses over a real x that share one denominator, y_r(x) = N_r(x)/D(x): D monic of degree
    /// <paramref name="poles"/>, N_r of degree <paramref name="zeros"/>[r], every coefficient complex and ascending.
    /// Levy's linearised least squares, N_r − y_r·D = 0, reweighted by 1/|D| as Sanathanan and Koerner do
    /// (IEEE Trans. AC 8(1), 1963, <see href="https://doi.org/10.1109/TAC.1963.1105517">10.1109/TAC.1963.1105517</see>).
    /// Keep x within about ±1: the normal equations square the monomials' conditioning.
    /// </summary>
    public static (Complex[][] N, Complex[] D) Rational(IReadOnlyList<double> x, IReadOnlyList<IReadOnlyList<Complex>> y,
        IReadOnlyList<int> zeros, int poles)
    {
        int[] at = new int[zeros.Count + 1];
        for (int r = 0; r < zeros.Count; r++) at[r + 1] = at[r] + zeros[r] + 1;
        int nn = at[^1], m = nn + poles;
        var d = new Complex[poles + 1];
        d[poles] = 1.0;
        var u = new Complex[m];
        var w = Enumerable.Repeat(1.0, x.Count).ToArray();
        for (int pass = 0; pass < 4; pass++)
        {
            // Unknowns: each N_r's coefficients, then d_0 … d_(poles−1): Σ a_j x^j − y Σ d_j x^j = y x^poles.
            var ata = new Complex[m, m];
            var atb = new Complex[m];
            for (int r = 0; r < y.Count; r++)
                for (int i = 0; i < x.Count; i++)
                {
                    var row = new Complex[m];
                    for (int j = 0; j <= zeros[r]; j++) row[at[r] + j] = w[i] * Math.Pow(x[i], j);
                    for (int j = 0; j < poles; j++) row[nn + j] = -w[i] * y[r][i] * Math.Pow(x[i], j);
                    Complex b = w[i] * y[r][i] * Math.Pow(x[i], poles);
                    for (int a = 0; a < m; a++)
                    {
                        atb[a] += Complex.Conjugate(row[a]) * b;
                        for (int c = 0; c < m; c++) ata[a, c] += Complex.Conjugate(row[a]) * row[c];
                    }
                }
            u = new WspMatrix.Lu(ata).Solve(atb);
            for (int j = 0; j < poles; j++) d[j] = u[nn + j];
            for (int i = 0; i < x.Count; i++) w[i] = 1.0 / Horner(d, x[i]).Magnitude;
        }
        return ([.. Enumerable.Range(0, zeros.Count).Select(r => u[at[r]..at[r + 1]])], d);
    }

    private static Complex Horner(Complex[] c, Complex x)
    {
        Complex v = 0.0;
        for (int j = c.Length - 1; j >= 0; j--) v = v * x + c[j];
        return v;
    }

    // ponytail: the degree-1 and degree-2 roots in closed form, which is all a pair and a single resonator need; a
    // companion-matrix eigensolve when a bench holds more resonators.
    private static Complex[] Roots(Complex[] d) => d.Length switch
    {
        2 => [-d[0]],
        3 => [(-d[1] + Complex.Sqrt(d[1] * d[1] - 4.0 * d[0])) / 2.0, (-d[1] - Complex.Sqrt(d[1] * d[1] - 4.0 * d[0])) / 2.0],
        _ => throw new NotSupportedException("a bench of one or two resonators"),
    };

    private static double[] Sweep(double fc) => [.. Enumerable.Range(0, Points).Select(i => fc * (0.85 + 0.3 * i / (Points - 1)))];

    internal static ParameterRefusalException Refusal(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("synthesis.em." + code, DiagnosticSeverity.Error, "EM coupling curves: " + message, args));
}
