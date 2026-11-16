using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using Xunit;

namespace CircuitRF.Core.Tests.Synthesis;

/// <summary>
/// The prototype ladders against the ideal filter's own response (<see cref="FilterPrototype"/>) to 1e-6, Bessel against
/// Matthaei, Young and Jones' table 4.07-1, the four transforms against the prototype at the mapped frequency, and the
/// lumped realiser's sites against the MLAD plan they build.
/// </summary>
public sealed class PrototypeLadderTests
{
    private static readonly double[] Omegas = [0.05, 0.3, 0.7, 0.95, 1.0, 1.2, 1.7, 3.0, 9.0];

    /// <summary>S21 of a lumped ladder from Z0 into its load at ω, by ABCD, each port referenced to its own termination.</summary>
    private static Complex S21(LumpedLadder l, double z0, double w)
    {
        var s = new Complex(0.0, w);
        Complex a = 1, b = 0, c = 0, d = 1;
        foreach (var arm in l.Arms)
        {
            // Each part in the arm's own kind (an admittance to ground, an impedance in the line); parts in parallel in the
            // line, or in series to ground, add as the other kind.
            var x = arm.Parts.Select(p => (p.Role == PlanSiteRole.L) != arm.Shunt ? s * p.Value : 1.0 / (s * p.Value)).ToArray();
            Complex v = arm.Parallel == arm.Shunt ? x.Aggregate(Complex.Add) : 1.0 / x.Select(y => 1.0 / y).Aggregate(Complex.Add);
            (a, b, c, d) = arm.Shunt ? (a + b * v, b, c + d * v, d) : (a, a * v + b, c, c * v + d);
        }
        double rl = l.LoadOhms;
        return 2.0 * Math.Sqrt(z0 * rl) / (a * rl + b + c * z0 * rl + d * z0);
    }

    /// <summary>The normalised ladder: a lowpass at 1 rad/s and 1 Ω.</summary>
    private static LumpedLadder Normalised(PrototypeLadder p, bool seriesFirst)
        => LumpedLadder.Transform(p, FilterBand.Lowpass, 0.5 / Math.PI, 0.5 / Math.PI, 1.0, seriesFirst);

    [Theory]
    [InlineData(FilterResponse.Butterworth, 4, 0.1, 40.0)]
    [InlineData(FilterResponse.Chebyshev, 4, 0.5, 40.0)]
    [InlineData(FilterResponse.Chebyshev, 7, 0.01, 40.0)]
    [InlineData(FilterResponse.Bessel, 2, 0.1, 40.0)]
    [InlineData(FilterResponse.Bessel, 7, 0.1, 40.0)]
    [InlineData(FilterResponse.Elliptic, 3, 0.1, 30.0)]
    [InlineData(FilterResponse.Elliptic, 5, 0.1, 50.0)]
    [InlineData(FilterResponse.Elliptic, 7, 0.5, 60.0)]
    [InlineData(FilterResponse.InvChebyshev, 5, 0.1, 40.0)]
    public void EachLadder_HasTheIdealResponse(FilterResponse response, int order, double rippleDb, double astopDb)
    {
        var p = PrototypeLadder.For(response, order, rippleDb, astopDb);
        var ideal = FilterPrototype.Create(response, order, rippleDb, astopDb);
        Assert.All(p.G.Skip(1), g => Assert.True(g > 0.0));
        foreach (bool seriesFirst in (bool[])[false, true])
            foreach (double w in Omegas)
            {
                Complex got = S21(Normalised(p, seriesFirst), 1.0, w), want = ideal.At(w).S21;
                Assert.True((got - want).Magnitude < 1e-6, $"{response} {order}, series-first {seriesFirst}, Ω = {w}: {got} vs {want}");
            }
    }

    [Fact]
    public void Bessel_MatchesTheTable()
    {
        double[][] table =
        [
            [1.5774, 0.4226],
            [1.2550, 0.5528, 0.1922],
            [1.0598, 0.5116, 0.3181, 0.1104],
            [0.9303, 0.4577, 0.3312, 0.2090, 0.0718],
            [0.8377, 0.4116, 0.3158, 0.2364, 0.1480, 0.0505],
        ];
        foreach (var row in table)
            Assert.Equal([1.0, .. row, 1.0], LowpassPrototype.GValues(FilterResponse.Bessel, row.Length), new Tol(5e-5));
    }

    [Theory]
    [InlineData(FilterResponse.Chebyshev, FilterBand.Lowpass)]
    [InlineData(FilterResponse.Chebyshev, FilterBand.Highpass)]
    [InlineData(FilterResponse.Chebyshev, FilterBand.Bandpass)]
    [InlineData(FilterResponse.Chebyshev, FilterBand.Bandstop)]
    [InlineData(FilterResponse.Elliptic, FilterBand.Lowpass)]
    [InlineData(FilterResponse.Elliptic, FilterBand.Highpass)]
    public void EachTransform_IsThePrototypeAtTheMappedFrequency(FilterResponse response, FilterBand band)
    {
        const double f1 = 0.9e9, f2 = 1.1e9, z0 = 50.0;
        int order = response == FilterResponse.Elliptic ? 5 : 4;   // an even Chebyshev: a load of g(N+1)·Z0
        var spec = new FilterSpec(response, band, order, f1, band is FilterBand.Lowpass or FilterBand.Highpass ? f1 : f2, 0.5);
        var ideal = FilterPrototype.Create(response, order, 0.5, 40.0);
        double w1 = 2.0 * Math.PI * f1, w0 = 2.0 * Math.PI * Math.Sqrt(f1 * f2), b = 2.0 * Math.PI * (f2 - f1);
        foreach (bool seriesFirst in (bool[])[false, true])
        {
            var l = LumpedLadder.For(spec, seriesFirst);
            foreach (double f in (double[])[0.3e9, 0.85e9, 0.95e9, 1.0e9, 1.05e9, 1.3e9, 4e9])
            {
                double w = 2.0 * Math.PI * f;
                double omega = band switch
                {
                    FilterBand.Lowpass => w / w1,
                    FilterBand.Highpass => -w1 / w,
                    FilterBand.Bandpass => (w * w - w0 * w0) / (b * w),
                    _ => b * w / (w0 * w0 - w * w),
                };
                Complex got = S21(l, z0, w), want = ideal.At(omega).S21;
                Assert.True((got - want).Magnitude < 1e-6, $"{band}, series-first {seriesFirst}, {f / 1e9} GHz: {got} vs {want}");
            }
        }
    }

    [Theory]
    [InlineData(FilterResponse.Butterworth, FilterBand.Lowpass, 3)]
    [InlineData(FilterResponse.Chebyshev, FilterBand.Highpass, 4)]
    [InlineData(FilterResponse.Bessel, FilterBand.Bandpass, 3)]
    [InlineData(FilterResponse.Elliptic, FilterBand.Lowpass, 5)]
    [InlineData(FilterResponse.Elliptic, FilterBand.Highpass, 3)]
    public void TheRealisersSites_AreThePlansSites(FilterResponse response, FilterBand band, int order)
    {
        var spec = new FilterSpec(response, band, order, 180e6, band == FilterBand.Bandpass ? 220e6 : 180e6);
        var r = new LumpedLadderRealiser().Realise(spec, null);
        var plan = LadderPlan.Build(r.Parameters["Sections"], spec.Z0, spec.CentreHz,
            new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037)).Plan;
        Assert.Equal("MLAD", r.PartType);
        Assert.Equal(plan.Sites.Select(s => (s.Role.ToString(), s.PinA, s.PinB)), r.Sites.Select(s => (s.Role, s.PinA, s.PinB)));
        Assert.Equal(plan.Sites.Select(s => s.Value!.Value), r.Sites.Select(s => s.Value), new Tol(1e-6, relative: true));
        Assert.Contains(new LumpedLadderRealiser().Name, FilterRealisers.For(spec).Select(x => x.Name));
    }

    [Fact]
    public void AParallelSite_SharesThePinsOfTheSiteBeforeIt()
    {
        var list = LadderPlan.Parse("site(role=C, value=2pF, type=shunt); site(role=L, value=5nH, type=parallel); "
                                    + "site(role=L, value=10nH); site(role=C, value=1pF, type=PARALLEL)");
        Assert.Equal(list, LadderPlan.Parse(LadderPlan.Format(list)));
        Assert.Equal(3, LadderPlan.Grounds(list));
        var sites = LadderPlan.Build(list, 50.0, 1e9, new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037)).Plan.Sites;
        Assert.Equal(["G1", "G1", "G2-G3", "G2-G3"], sites.Select(s => s.Name));
        Assert.Equal([PlanSiteRole.C, PlanSiteRole.L, PlanSiteRole.L, PlanSiteRole.C], sites.Select(s => s.Role));
    }

    [Fact]
    public void EachRefusal_IsCoded()
    {
        static string Code(Action a) => Assert.Throws<ParameterRefusalException>(a).Diagnostic!.Id;
        var realiser = new LumpedLadderRealiser();
        Assert.Equal("device.ladder.parallel-alone", Code(() => LadderPlan.Build("site(role=C, value=1pF, type=parallel)",
            50.0, 1e9, new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037))));
        Assert.Equal("synthesis.proto.even-order", Code(() => PrototypeLadder.For(FilterResponse.Elliptic, 4)));
        Assert.Equal("synthesis.proto.zeros-in-band", Code(() => realiser.Realise(
            new FilterSpec(FilterResponse.Elliptic, FilterBand.Bandpass, 3, 1e9, 1.2e9), null)));
        Assert.Equal("synthesis.proto.bandstop", Code(() => realiser.Realise(
            new FilterSpec(FilterResponse.Butterworth, FilterBand.Bandstop, 3, 1e9, 1.2e9), null)));
        Assert.Equal("synthesis.proto.spec", Code(() => realiser.Realise(
            new FilterSpec(FilterResponse.Butterworth, FilterBand.Bandpass, 3, 1.2e9, 1e9), null)));
        Assert.Equal("synthesis.proto.spec", Code(() => realiser.Realise(
            new FilterSpec(FilterResponse.Butterworth, FilterBand.Lowpass, 0, 1e9, 1e9), null)));
        Assert.False(realiser.Supports(new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandstop, 3, 1e9, 1.2e9), out string? why));
        Assert.Contains("trap", why);
    }

    private sealed class Tol(double eps, bool relative = false) : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) <= eps * (relative ? Math.Abs(b) : 1.0);
        public int GetHashCode(double x) => 0;
    }
}
