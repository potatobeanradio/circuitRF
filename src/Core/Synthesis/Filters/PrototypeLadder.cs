using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// The doubly terminated lowpass prototype as a ladder (docs/design/filter-synthesis.md §1). <see cref="G"/> is g0 …
/// g(N+1), as <see cref="LowpassPrototype.GValues"/> gives them; <see cref="Omega"/>[k] is the prototype frequency at
/// which element k resonates with a companion, +∞ where it has none. Shunt-first, element k is a shunt C for odd k and a
/// series L for even k, and a series L of finite Ω has a C of 1/(g·Ω²) across it: a tank, the transmission zero at Ω.
/// Series-first is the dual, the same numbers: a series L for odd k, and a shunt C with an L of 1/(g·Ω²) in series.
/// </summary>
public sealed record PrototypeLadder(double[] G, double[] Omega)
{
    /// <summary>N, the number of reactive elements.</summary>
    public int Order => G.Length - 2;

    /// <summary>
    /// The ladder for one response. Butterworth and Chebyshev are <see cref="LowpassPrototype.GValues"/>' closed forms.
    /// Bessel, elliptic and inverse Chebyshev are extracted from <see cref="FilterPrototype"/>'s own polynomials, so the
    /// ladder has exactly the ideal filter's response: Bessel by continued fraction, the two with finite zeros by zero
    /// shifting, odd orders only (an even order has no equal-terminated ladder without a modified response).
    /// </summary>
    public static PrototypeLadder For(FilterResponse response, int order, double rippleDb = 0.1, double astopDb = 40.0)
        => response switch
        {
            FilterResponse.Elliptic or FilterResponse.InvChebyshev => WithZeros(response, order, rippleDb, astopDb),
            _ => new(LowpassPrototype.GValues(response, order, rippleDb), Infinite(order + 2)),
        };

    /// <summary>
    /// Bessel's g-values: the continued fraction of the input immittance of <see cref="FilterPrototype"/>'s Bessel
    /// response (unit group delay at DC), by Match's own extraction (<see cref="MatchPrototypes.Extract"/>).
    /// </summary>
    internal static double[] BesselG(int order)
    {
        if (order < 1) throw new ArgumentOutOfRangeException(nameof(order), order, "A prototype has at least one element.");
        var (num, den) = Immittance(FilterPrototype.Create(FilterResponse.Bessel, order));
        return MatchPrototypes.Extract(num, den, order)
               ?? throw Refuse("synthesis.proto.unrealisable",
                   "Prototype: the order-{order} Bessel ladder does not extract to positive elements.", ("order", order));
    }

    /// <summary>
    /// A ladder with finite transmission zeros, by zero shifting from the immittance W that has a pole at infinity (Y of
    /// the shunt-first ladder): for each zero Ωk, a shunt C partly removed, c = W(jΩk)/(jΩk), leaves a zero of W at jΩk,
    /// and 1/W's pole there is then removed whole as a tank in the line, A·s/(s² + Ωk²); what is left is the last shunt C
    /// and the load. The zeros are taken highest first, and the next order is tried until every element is positive
    /// (R. Saal and E. Ulbrich, "On the design of filters by synthesis",
    /// <a href="https://doi.org/10.1109/TCT.1958.1086481">10.1109/TCT.1958.1086481</a>; P. Amstutz, "Elliptic
    /// approximation and elliptic filter design on small computers",
    /// <a href="https://doi.org/10.1109/TCS.1978.1084429">10.1109/TCS.1978.1084429</a>).
    /// </summary>
    private static PrototypeLadder WithZeros(FilterResponse response, int order, double rippleDb, double astopDb)
    {
        if (order < 1 || order % 2 == 0)
            throw Refuse("synthesis.proto.even-order",
                "Prototype: a {response} ladder is built for an odd order; {order} has no equal-terminated ladder.",
                ("response", response), ("order", order));
        var p = FilterPrototype.Create(response, order, rippleDb, astopDb);
        var (num, den) = Immittance(p);
        double[] zeros = [.. MatchPoly.Roots(p.P).Where(r => r.Imaginary > 0.0).Select(r => r.Imaginary).OrderDescending()];
        foreach (double[] taken in Orders(zeros))
            if (Shift(num, den, taken) is { } ladder) return ladder;
        throw Refuse("synthesis.proto.unrealisable",
            "Prototype: the order-{order} {response} ladder has a negative element in every order of its zeros.",
            ("order", order), ("response", response));
    }

    private static PrototypeLadder? Shift(double[] num, double[] den, double[] zeros)
    {
        int n = num.Length - 1;
        var g = new double[n + 2];
        var omega = Infinite(n + 2);
        g[0] = 1.0;
        int k = 1;
        foreach (double z in zeros)
        {
            var s = new Complex(0.0, z);
            double c = (MatchPoly.Eval(num, s) / MatchPoly.Eval(den, s) / s).Real;
            double[] q = DivideOut(MatchPoly.Sub(num, MatchPoly.Mul([c, 0.0], den)), z);
            double a = (MatchPoly.Eval(den, s) / (s * MatchPoly.Eval(q, s))).Real;
            if (!(c > 0.0) || !(a > 0.0)) return null;
            (g[k], g[k + 1], omega[k + 1]) = (c, a / (z * z), z);
            (num, den) = (q, DivideOut(MatchPoly.Sub(den, MatchPoly.Mul([a, 0.0], q)), z));
            k += 2;
        }
        // W = c·s + G: the last shunt C, and the load (its resistance, g's convention after a shunt C).
        if (num.Length != 2 || den.Length != 1 || !(num[0] / den[0] > 0.0) || !(num[1] / den[0] > 0.0)) return null;
        (g[n], g[n + 1]) = (num[0] / den[0], den[0] / num[1]);
        return new PrototypeLadder(g, omega);
    }

    /// <summary>The input immittance with a pole at infinity, (E ± αF)/(E ∓ αF): Z of the series-first ladder and Y of
    /// the shunt-first one, its dual.</summary>
    private static (double[] Num, double[] Den) Immittance(FilterPrototype p)
    {
        double[] f = [.. p.F.Select(c => c * p.Alpha)];
        double[] a = MatchPoly.Trim(MatchPoly.Add(p.E, f)), b = MatchPoly.Trim(MatchPoly.Sub(p.E, f));
        return a.Length > b.Length ? (a, b) : (b, a);
    }

    /// <summary><paramref name="p"/> over s² + z², z a root by construction, so the remainder is rounding and dropped.</summary>
    private static double[] DivideOut(double[] p, double z)
    {
        double[] r = [.. MatchPoly.Trim(p)];
        var q = new double[r.Length - 2];
        for (int i = 0; i < q.Length; i++) (q[i], r[i + 2]) = (r[i], r[i + 2] - r[i] * z * z);
        return q;
    }

    private static IEnumerable<double[]> Orders(double[] z) => z.Length <= 1
        ? [z]
        : z.SelectMany((x, i) => Orders([.. z[..i], .. z[(i + 1)..]]).Select(rest => (double[])[x, .. rest]));

    private static double[] Infinite(int n) => Enumerable.Repeat(double.PositiveInfinity, n).ToArray();

    internal static ParameterRefusalException Refuse(string code, string template, params (string, object?)[] args)
        => new(Diagnostic.Create(code, DiagnosticSeverity.Error, template, args));
}
