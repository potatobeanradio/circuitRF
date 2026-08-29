using System.Numerics;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// A test-local ABCD cascade, written independently of <c>MatchResponse</c>.
/// </summary>
/// <remarks>
/// <c>tests/Core.Tests</c> references only <c>src/Core</c> and cannot see <c>SParameterEngine</c> —
/// which is the point. Asking our own S-parameter engine whether our own synthesis is right checks
/// two things that share assumptions; a second small implementation, written from the two-port
/// definitions rather than from <c>MatchResponse</c>'s code, does not.
/// </remarks>
internal static class MatchAbcdOracle
{
    /// <summary>Full 2x2 ABCD of the ladder at one frequency, by explicit matrix products.</summary>
    internal static Complex[,] Abcd(MatchNetwork network, double frequencyHz)
    {
        double om = 2.0 * Math.PI * frequencyHz;
        Complex[,] m = { { Complex.One, Complex.Zero }, { Complex.Zero, Complex.One } };

        foreach (var e in network.Elements)
        {
            Complex imp = e.Type == ElementType.L
                ? Complex.ImaginaryOne * om * e.Value
                : Complex.One / (Complex.ImaginaryOne * om * e.Value);

            Complex[,] step = e.IsShunt
                ? new[,] { { Complex.One, Complex.Zero }, { Complex.One / imp, Complex.One } }
                : new[,] { { Complex.One, imp }, { Complex.Zero, Complex.One } };

            m = Multiply(m, step);
        }
        return m;
    }

    private static Complex[,] Multiply(Complex[,] x, Complex[,] y) => new[,]
    {
        { x[0, 0] * y[0, 0] + x[0, 1] * y[1, 0], x[0, 0] * y[0, 1] + x[0, 1] * y[1, 1] },
        { x[1, 0] * y[0, 0] + x[1, 1] * y[1, 0], x[1, 0] * y[0, 1] + x[1, 1] * y[1, 1] },
    };

    /// <summary>S11 and S21 against the network's own (real) port resistances.</summary>
    internal static (Complex S11, Complex S21) S(MatchNetwork network, double frequencyHz)
    {
        var m = Abcd(network, frequencyHz);
        Complex a = m[0, 0], b = m[0, 1], c = m[1, 0], d = m[1, 1];
        double z1 = network.R1, z2 = network.R2;
        Complex den = a * z2 + b + c * z1 * z2 + d * z1;
        return ((a * z2 + b - c * z1 * z2 - d * z1) / den, 2.0 * Math.Sqrt(z1 * z2) / den);
    }

    /// <summary>The band sampled at <paramref name="points"/> frequencies.</summary>
    internal static IEnumerable<double> Band(double f1, double f2, int points = 41)
    {
        for (int i = 0; i < points; i++) yield return f1 + (f2 - f1) * i / (points - 1.0);
    }

    /// <summary>Worst in-band |S11| in dB.</summary>
    internal static double WorstS11Db(MatchNetwork network, double f1, double f2, int points = 401)
        => 20.0 * Math.Log10(Band(f1, f2, points).Max(f => S(network, f).S11.Magnitude));

    /// <summary>Insertion loss and its ripple, both dB.</summary>
    internal static (double LossDb, double RippleDb) Il(
        MatchNetwork network, double f1, double f2, int points = 401)
    {
        var db = Band(f1, f2, points).Select(f => 20.0 * Math.Log10(S(network, f).S21.Magnitude)).ToList();
        return (-db.Min(), db.Max() - db.Min());
    }

    /// <summary>
    /// A ladder built from a prototype g-vector for the golden problem's orientation (analysis end =
    /// the series termination 2), written here rather than reached for inside <c>MatchSynthesis</c>
    /// so a test can score an arbitrary family member the way the search does.
    /// </summary>
    internal static (MatchNetwork Net, double QFar) LadderFromG(MatchDesign d, double[] g, int n)
    {
        double om0 = d.Omega0, w = d.W, rAna = d.Term2.R;
        var s = new bool[n + 2];
        s[0] = s[1] = true;
        for (int j = 2; j <= n; j++) s[j] = !s[j - 1];
        s[n + 1] = s[n];

        var elements = new List<MatchElement>();
        for (int j = 1; j <= n; j++)
        {
            double gi = s[j] ? g[j] / (w * om0) * rAna : g[j] / (w * om0) / rAna;
            double l = s[j] ? gi : 1.0 / (om0 * om0 * gi);
            double c = s[j] ? 1.0 / (om0 * om0 * gi) : gi;
            elements.Add(new MatchElement { Name = $"L{j}", Type = ElementType.L, IsShunt = !s[j], Value = l });
            elements.Add(new MatchElement { Name = $"C{j}", Type = ElementType.C, IsShunt = !s[j], Value = c });
        }
        elements.Reverse();

        double qFar = g[n] * g[n + 1] / w;
        if (s[n + 1]) qFar = 1.0 / qFar;
        var net = new MatchNetwork { R1 = s[n + 1] ? rAna / g[n + 1] : g[n + 1] * rAna, R2 = rAna };
        net.Elements.AddRange(elements);
        return (net, qFar);
    }

    /// <summary>
    /// The same §4.4 transformation written for an ARBITRARY analysis end and arm count — what the
    /// multiband tests score a member with.
    /// </summary>
    /// <remarks>
    /// <b>An independent construction, not a call into the synthesis.</b> The one above is written
    /// for the golden problem's own orientation (series analysis end at termination 2) and cannot
    /// express a shunt-first ladder driven from termination 1, which is what match.md §18.4's problem
    /// is. Both read the design's <c>Omega0</c> and <c>W</c> — those are the SPEC, not the answer —
    /// and do the frequency-scale, impedance-scale and resonate steps themselves.
    /// </remarks>
    internal static (MatchNetwork Net, double RFar, double QFar) LadderFromG(
        MatchDesign d, double[] g, int arms, Termination ana, bool anaIsTerm1)
    {
        double om0 = d.Omega0, w = d.W, rAna = ana.R;

        var s = new bool[arms + 2];
        s[0] = s[1] = ana.Topology == TerminationTopology.Series;
        for (int j = 2; j <= arms; j++) s[j] = !s[j - 1];
        s[arms + 1] = s[arms];

        var elements = new List<MatchElement>();
        for (int j = 1; j <= arms; j++)
        {
            double gi = s[j] ? g[j] / (w * om0) * rAna : g[j] / (w * om0) / rAna;
            double l = s[j] ? gi : 1.0 / (om0 * om0 * gi);
            double c = s[j] ? 1.0 / (om0 * om0 * gi) : gi;
            elements.Add(new MatchElement { Name = $"L{j}", Type = ElementType.L, IsShunt = !s[j], Value = l });
            elements.Add(new MatchElement { Name = $"C{j}", Type = ElementType.C, IsShunt = !s[j], Value = c });
        }
        if (!anaIsTerm1) elements.Reverse();

        double rFar = s[arms + 1] ? rAna / g[arms + 1] : g[arms + 1] * rAna;
        double qFar = g[arms] * g[arms + 1] / w;
        if (s[arms + 1]) qFar = 1.0 / qFar;

        var net = new MatchNetwork
        {
            R1 = anaIsTerm1 ? rAna : rFar,
            R2 = anaIsTerm1 ? rFar : rAna,
        };
        net.Elements.AddRange(elements);
        return (net, rFar, qFar);
    }

    /// <summary>The design doc's §4.9 interstage problem — the acceptance anchor.</summary>
    internal static MatchDesign GoldenDesign() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };
}
