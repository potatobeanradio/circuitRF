// ================================================================
//  ReachabilityTests.cs  —  M3's gate, brief-harmonicarf-h6
//
//  R-h6-12  §6.6: the intrinsic map is NOT onto. The gate compares a lossy embedding against a
//           lossless one THROUGH THE FORWARD PATH — the region is a set of forward solves, so
//           "smaller" is measured rather than asserted.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class ReachabilityTests(ITestOutputHelper output)
{
    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    private static CircuitModel Model(LumpedPackage? package) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "(1130*1.507*tanh(_v2*0.176*(tanh(0.089*(4.268-_v1+_v2*0.001+0.71*ln(exp(-(-0.837-_v1)/0.71)+1)))+1))*ln(exp(-(2*4.268-2*_v1+2*_v2*0.001+2*0.71*ln(exp(-(-0.837-_v1)/0.71)+1))/1.507)+1)*(_v2*0.0012+1))/2",
            },
        },
        Embedding = package is null ? EmbeddingStack.None : new EmbeddingStack { Package = package },
        Bias     = new BiasSpec { Vgs = -3.05, Vds = 48 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9, Tol = 1e-8,
            CompressionDb = 3.0, PinStartDbm = -10, PinMaxDbm = 34,
        },
    };

    private static TerminationSet Terms(CircuitModel m)
    {
        var t = new TerminationSet(m.Settings.HarmonicCount);
        t.Set(TerminationSide.Source, 1, new Complex(25, 0));
        t.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return t;
    }

    private const double OperatingPointDbm = 22.0;

    [Fact]
    public void ALossyEmbedding_ReachesAGenuinelySmallerRegionThanALosslessOne()
    {
        // §6.6's own example: "with series Rd/Rs or a lossy embedding, whole regions of the intrinsic
        // plane are unreachable from any extrinsic termination". A series drain resistance sits
        // between the intrinsic drain and the load, so no extrinsic termination can present the
        // intrinsic generator with less than that resistance.
        var band = new InverseBand(TerminationSide.Load, 1);

        var lossless = Sample(Model(null), band);
        var lossy    = Sample(Model(new LumpedPackage { Rd = 4.0, Rs = 1.0 }), band);

        output.WriteLine($"lossless: area {lossless.Area:F5} Γ², {lossless.Boundary.Count} boundary " +
                         $"points, {lossless.Solves} solves");
        output.WriteLine($"lossy   : area {lossy.Area:F5} Γ², {lossy.Boundary.Count} boundary " +
                         $"points, {lossy.Solves} solves");
        output.WriteLine($"ratio   : {lossy.Area / lossless.Area:P1} of the lossless region");

        Assert.False(lossless.IsEmpty);
        Assert.False(lossy.IsEmpty);
        Assert.True(lossy.Area < lossless.Area * 0.9,
            $"the lossy embedding's reachable region is {lossy.Area:F5} Γ² against the lossless " +
            $"{lossless.Area:F5} — if a 4 Ω series drain resistance does not shrink it, the sampler " +
            "is not measuring reachability at all");
    }

    [Fact]
    public void TheRegionAgreesWithTheFORWARDPath_InteriorSamplesLandInside()
    {
        // The boundary is the image of the extrinsic sampling CIRCLE. That is the region's boundary
        // only if the map does not fold — so the interior probes, which are ordinary forward solves at
        // extrinsic points inside the circle, must land inside the polygon. This is the check that
        // makes the boundary-only sampling honest rather than an assumption.
        var band = new InverseBand(TerminationSide.Load, 1);
        var r = Sample(Model(new LumpedPackage { Rd = 1.5, Rs = 0.4, Ls = 20e-12 }), band);

        Assert.NotEmpty(r.Interior);
        int outside = r.Interior.Count(g => !r.Contains(g));
        output.WriteLine($"{r.Interior.Count} interior forward samples, {outside} outside the shaded " +
                         $"polygon (area {r.Area:F5} Γ²)");
        foreach (var g in r.Interior)
            output.WriteLine($"   {g.Real:F4}{(g.Imaginary < 0 ? "" : "+")}{g.Imaginary:F4}j " +
                             $"{(r.Contains(g) ? "inside" : "OUTSIDE")}");

        Assert.Equal(0, outside);

        // NON-VACUITY: a point far outside must be reported outside, or Contains says yes to
        // everything and the assertion above means nothing.
        Assert.False(r.Contains(new Complex(40, 40)));
    }

    [Fact]
    public void ThePolygonIsClosedAndOrdered_NotAScatterOfPoints()
    {
        var r = Sample(Model(null), new InverseBand(TerminationSide.Load, 1));

        // Consecutive boundary points come from consecutive extrinsic angles, so the polygon must not
        // have a step far larger than its neighbours — that would be a fold, and the shading would be
        // reporting a self-intersecting region.
        var steps = new List<double>();
        for (int i = 0; i < r.Boundary.Count; i++)
            steps.Add((r.Boundary[(i + 1) % r.Boundary.Count] - r.Boundary[i]).Magnitude);

        double mean = steps.Average(), max = steps.Max();
        output.WriteLine($"boundary steps: mean {mean:F5}, max {max:F5} Γ ({max / mean:F1}× the mean)");
        Assert.True(max < mean * 6,
            $"one boundary step is {max / mean:F1}× the mean — the image has folded and the polygon " +
            "does not bound the reachable region");
    }

    [Fact]
    public void TheCacheKey_MovesOnStructureAndDrive_AndNotOnATerminationChange()
    {
        // §6.6: "sampled coarsely and cached; refreshed on structural change." The key is what makes
        // that literally true, so it is asserted rather than described.
        var band = new InverseBand(TerminationSide.Load, 1);
        var a = Model(null);
        var b = Model(new LumpedPackage { Rd = 4.0 });

        Assert.Equal(Reachability.KeyFor(a, band, 20), Reachability.KeyFor(a, band, 20));
        Assert.NotEqual(Reachability.KeyFor(a, band, 20), Reachability.KeyFor(b, band, 20));
        Assert.NotEqual(Reachability.KeyFor(a, band, 20), Reachability.KeyFor(a, band, 21));
        Assert.NotEqual(Reachability.KeyFor(a, band, 20),
                        Reachability.KeyFor(a, new InverseBand(TerminationSide.Source, 1), 20));

        // A bias change is a VALUE change in the structural key's own terms, and the region does move
        // with it — so this is a stated limitation of the design note's caching rule, not a bug this
        // phase invented. Recorded here so the next reader finds it as a fact rather than a surprise.
        var biased = a with { Bias = a.Bias with { Vgs = -2.5 } };
        Assert.Equal(Reachability.KeyFor(a, band, 20), Reachability.KeyFor(biased, band, 20));
    }

    private static ReachableRegion Sample(CircuitModel model, InverseBand band)
    {
        var ctx = HarmonicaContext.Create(model, Settings);
        return Reachability.Sample(ctx, Terms(model), band, OperatingPointDbm);
    }
}
