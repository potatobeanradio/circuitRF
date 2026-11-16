using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Synthesis;

/// <summary>
/// The coupling-matrix core against Cameron's published 4–2 example (22 dB return loss, zeros at ω = 1.3217 and
/// 1.8082: R. J. Cameron, "Advanced filter synthesis", IEEE Microwave Magazine 12(6), 2011, Table 1 and Fig. 21,
/// 10.1109/MMM.2011.942007), against its own polynomials, and against the g-value formula.
/// </summary>
public sealed class CouplingMatrixTests
{
    private static readonly double[] Cameron42Zeros = [1.3217, 1.8082];

    [Fact]
    public void Cameron42_PolynomialsAndEpsilon_MatchThePublishedTable()
    {
        var p = CouplingMatrix.Characteristic(4, 22, Cameron42Zeros);
        // Table 1, ascending powers of s, printed to four decimals.
        Complex[] e = [new(-0.1268, -2.0658), new(2.4874, -3.6255), new(3.6706, -2.1950), new(2.4015, -0.7591), 1.0];
        Complex[] f = [0.0208, new(0, -0.5432), 0.7869, new(0, -0.7591), 1.0];
        Complex[] pp = [new(0, -2.3899), 3.1299, Complex.ImaginaryOne];
        AssertClose(e, [.. p.E.Reverse()], 1e-4);
        AssertClose(f, [.. p.F.Reverse()], 1e-4);
        AssertClose(pp, [.. p.P.Reverse()], 1e-4);
        Assert.Equal(1.1548, p.Epsilon, 1e-4);
        Assert.Equal(1.0, p.EpsilonR);
    }

    [Fact]
    public void Cameron42_Folded_HasTheFoldedFormAndThePublishedNetworksResponse()
    {
        double[,] m = CouplingMatrix.Synthesise(4, 22, Cameron42Zeros);
        // Folded, S 1 2 3 4 L = 0 … 5: the source row and load column cleared but for their end resonators, M13
        // cleared, M14 and M24 left to carry the two zeros.
        foreach (var (i, j) in new[] { (0, 2), (0, 3), (0, 4), (0, 5), (1, 3), (1, 5), (2, 5), (3, 5) })
            Assert.True(Math.Abs(m[i, j]) < 1e-12, $"M[{i},{j}] = {m[i, j]}");
        Assert.True(Math.Abs(m[1, 4]) > 0.1 && Math.Abs(m[2, 4]) > 0.1);

        // The same filter as the paper prints it, rotated into two trisections (Fig. 21), four decimals: a similar
        // matrix, so the same response to the printed digits.
        double[,] published =
        {
            { 0.0, 0.9221, 0.5921, 0.0, 0.0, 0.0 },
            { 0.9221, -0.8333, 0.6259, 0.0, 0.0, 0.0 },
            { 0.5921, 0.6259, 0.6022, 0.8634, 0.0, 0.0 },
            { 0.0, 0.0, 0.8634, 0.5812, 0.2590, 0.8471 },
            { 0.0, 0.0, 0.0, 0.2590, -1.1092, 0.6952 },
            { 0.0, 0.0, 0.0, 0.8471, 0.6952, 0.0 },
        };
        for (double w = -3.0; w <= 4.0; w += 0.01)
        {
            var (s11, s21) = CouplingMatrix.Response(m, w);
            var (r11, r21) = CouplingMatrix.Response(published, w);
            Assert.True(Math.Abs(s21.Magnitude - r21.Magnitude) < 1e-3 && Math.Abs(s11.Magnitude - r11.Magnitude) < 1e-3,
                $"ω = {w}: |S21| {s21.Magnitude} against {r21.Magnitude}, |S11| {s11.Magnitude} against {r11.Magnitude}");
        }
    }

    public static TheoryData<int, double, double[], double> Cases => new()
    {
        { 4, 22, [1.3217, 1.8082], 1e-9 },                 // 4–2 asymmetric, the published example
        { 6, 20, [-1.5, 1.5], 1e-9 },                      // 6–2 symmetric
        { 4, 22, [-3.7431, -1.8051, 1.5699, 6.1910], 1e-9 }, // 4–4 fully canonical (Cameron 2003)
        { 5, 25, [], 1e-9 },                               // all-pole
        // Two poles 1e-3 apart near the band edge: the residues lose digits (CouplingMatrix.Transversal).
        { 8, 23, [1.1185, 1.2973], 1e-7 },
        { 10, 23, [-1.2, -1.5, -1.1, 1.3], 1e-5 },
    };

    [Theory, MemberData(nameof(Cases))]
    public void MatrixResponse_EqualsThePolynomialResponse(int order, double rl, double[] zeros, double tol)
    {
        var p = CouplingMatrix.Characteristic(order, rl, zeros);
        double[,] m = CouplingMatrix.Folded(CouplingMatrix.Transversal(p));
        for (double w = -3.0; w <= 3.0; w += 0.05)
        {
            var (s11, s21) = CouplingMatrix.Response(m, w);
            var (f11, f21) = p.Response(w);
            Assert.True((s11 - f11).Magnitude < tol && (s21 - f21).Magnitude < tol, $"ω = {w}: {s11}, {s21} against {f11}, {f21}");
            Assert.Equal(1.0, s11.Magnitude * s11.Magnitude + s21.Magnitude * s21.Magnitude, 1e-12);

            // A finite Q shifts every resonator by δ = 1/(Qu·FBW): the polynomials at s = δ + jω.
            var s = new Complex(1.0 / (200 * 0.05), w);
            Complex e = MatchPoly.EvalComplex(p.E, s);
            var (l11, l21) = CouplingMatrix.Response(m, w, 200, 0.05);
            Assert.True((l11 - MatchPoly.EvalComplex(p.F, s) / (p.EpsilonR * e)).Magnitude < tol);
            Assert.True((l21 - MatchPoly.EvalComplex(p.P, s) / (p.Epsilon * e)).Magnitude < tol);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AllPoleChebyshev_EqualsTheGValueFormula(int order)
    {
        const double ripple = 0.1;
        double rl = -10 * Math.Log10(1 - Math.Pow(10, -ripple / 10));
        double[,] m = CouplingMatrix.Synthesise(order, rl);
        double[,] g = CouplingMatrix.AllPole(LowpassPrototype.GValues(FilterResponse.Chebyshev, order, ripple));
        // GValues takes 40·log10(e) as 17.37, which moves its values by about 1e-5.
        for (int i = 0; i < order + 2; i++)
            for (int j = 0; j < order + 2; j++)
                Assert.True(Math.Abs(Math.Abs(m[i, j]) - g[i, j]) < 5e-5, $"M[{i},{j}] = {m[i, j]} against {g[i, j]}");
    }

    [Fact]
    public void Physical_IsHongsQeAndK_AndRoundTrips()
    {
        double[] g = LowpassPrototype.GValues(FilterResponse.Chebyshev, 3, 0.1);
        var r = CouplingMatrix.ToPhysical(CouplingMatrix.AllPole(g), 2.4e9, 0.05);
        Assert.Equal(g[0] * g[1] / 0.05, r.QeSource, 1e-9);
        Assert.Equal(g[3] * g[4] / 0.05, r.QeLoad, 1e-9);
        Assert.Equal(0.05 / Math.Sqrt(g[1] * g[2]), r.K[0, 1], 1e-12);
        Assert.All(r.ResonantHz, f => Assert.Equal(2.4e9, f, 1e-3));

        double[,] m = CouplingMatrix.Synthesise(4, 22, Cameron42Zeros);
        double[,] back = CouplingMatrix.FromPhysical(CouplingMatrix.ToPhysical(m, 2.4e9, 0.05), 2.4e9, 0.05);
        for (int i = 0; i < 6; i++)
            for (int j = 0; j < 6; j++)
                Assert.Equal(Math.Abs(m[i, j]) < 1e-12 ? 0.0 : m[i, j], back[i, j], 1e-9);
    }

    public static TheoryData<string, Action> Refusals => new()
    {
        { "synthesis.cm.order", () => CouplingMatrix.Characteristic(0, 20) },
        { "synthesis.cm.order", () => CouplingMatrix.Characteristic(11, 20) },
        { "synthesis.cm.return-loss", () => CouplingMatrix.Characteristic(3, 0) },
        { "synthesis.cm.too-many-zeros", () => CouplingMatrix.Characteristic(2, 20, [2, 3, 4]) },
        { "synthesis.cm.zero-in-band", () => CouplingMatrix.Characteristic(4, 20, [0.5]) },
        { "synthesis.cm.port-cross-coupling",
            () => CouplingMatrix.ToPhysical(CouplingMatrix.Transversal(CouplingMatrix.Characteristic(3, 20)), 1e9, 0.1) },
        { "synthesis.cm.band-invalid", () => CouplingMatrix.ToPhysical(CouplingMatrix.Synthesise(3, 20), 1e9, 0) },
    };

    [Theory, MemberData(nameof(Refusals))]
    public void Refusals_AreCoded(string code, Action act)
        => Assert.Equal(code, Assert.Throws<ParameterRefusalException>(act).Diagnostic?.Id);

    private static void AssertClose(Complex[] want, Complex[] got, double tol)
    {
        Assert.Equal(want.Length, got.Length);
        for (int i = 0; i < want.Length; i++)
            Assert.True(Math.Abs(want[i].Real - got[i].Real) < tol && Math.Abs(want[i].Imaginary - got[i].Imaginary) < tol,
                $"[{i}] {got[i]} against {want[i]}");
    }
}
