// ================================================================
//  Z0RenormalizationTests.cs — brief-dd-z0-renormalization.md gate tests (RfCore side)
//
//  §1  NetworkMetrics.RenormalizeSCube — round-trip identity, order-commutes-with-conversion,
//      Re(Z0)<=0 rejected.
//  §3  LoadpullSurface Γ-grid renormalization — RenormGamma generalization (via Reduce), FitKey
//      cache distinguishes references, Z-plane fit unaffected by z0.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class Z0RenormalizationTests
{
    // ---- §1: RenormalizeSCube ------------------------------------------------

    private static DataCube MakeTwoPortSCube()
    {
        var freqAxis = new Axis("freq", new[] { 1e9, 2e9 }, "Hz");
        var iAxis    = new Axis("i", new[] { 1.0, 2.0 }, "port");
        var jAxis    = new Axis("j", new[] { 1.0, 2.0 }, "port");

        // Two distinct, non-degenerate 2x2 S matrices (one per frequency) — not physically passive,
        // but SToS/SToZ are generic bilinear transforms that hold algebraically regardless.
        Complex[] f1 = { new(0.10, 0.20), new(0.30, 0.05), new(0.30, 0.05), new(-0.10, 0.10) };
        Complex[] f2 = { new(0.15, -0.10), new(0.25, 0.00), new(0.25, 0.00), new(0.05, -0.15) };
        var raw = f1.Concat(f2).ToArray();
        return new DataCube(new[] { freqAxis, iAxis, jAxis }, raw);
    }

    [Fact]
    public void RenormalizeSCube_RoundTrip_IsIdentity()
    {
        var sCube = MakeTwoPortSCube();
        var z0A = new[] { new Complex(50, 0), new Complex(50, 0) };
        var z0B = new[] { new Complex(75, -10), new Complex(75, -10) };

        var toB = NetworkMetrics.RenormalizeSCube(sCube, z0A, z0B);
        var backToA = NetworkMetrics.RenormalizeSCube(toB, z0B, z0A);

        var orig = sCube.ComplexValues;
        var round = backToA.ComplexValues;
        Assert.Equal(orig.Length, round.Length);
        for (int i = 0; i < orig.Length; i++)
        {
            Assert.Equal(orig[i].Real, round[i].Real, precision: 12);
            Assert.Equal(orig[i].Imaginary, round[i].Imaginary, precision: 12);
        }
    }

    // §1's brief text asserts "Z and Y are reference-independent, so the two orders [renormalize-S-
    // then-convert vs convert-directly] must agree — assert that in a test rather than assuming it."
    // Doing exactly that surfaced a genuine, pre-existing convention gap: RFNetwork.SToS is the
    // power-wave (Kurokawa) bilinear form (uses Conjugate(z0) in its P/Q coefficients — see its own
    // doc comment), while RFNetwork.SToZ/SToY use the ORDINARY (non-power-wave) √Z0 form — no
    // conjugate anywhere. These two conventions coincide when Z0 is REAL (conjugate is a no-op), but
    // genuinely diverge for a COMPLEX reference. This is not new to this brief:
    // NetworkMetrics.TwoPortUniformReal/FullUniformReal (R-stb-1..6) already renormalize to a REAL
    // target for exactly this reason, never a complex one. The two tests below pin both halves of
    // that finding: the invariant DOES hold for a real target (§1's gate, as the brief intended it —
    // Z/Y cube traces are unaffected by a real Z0 edit), and a complex target is where it stops
    // holding (documented, not silently papered over — see ResolveNetworkParamCube's own comment).

    [Fact]
    public void RenormalizeSCube_ThenConvert_CommutesWithDirectConversion_RealTarget()
    {
        var sCube = MakeTwoPortSCube();
        var z0Src = new[] { new Complex(50, 0), new Complex(50, 0) };
        var z0New = new[] { new Complex(75, 0), new Complex(75, 0) };   // real — power-wave == ordinary

        var direct = NetworkMetrics.ConvertSCube(sCube, z0Src, MatrixType.Z);

        var renormed = NetworkMetrics.RenormalizeSCube(sCube, z0Src, z0New);
        var viaRenorm = NetworkMetrics.ConvertSCube(renormed, z0New, MatrixType.Z);

        var a = direct.ComplexValues;
        var b = viaRenorm.ComplexValues;
        Assert.Equal(a.Length, b.Length);
        for (int i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].Real, b[i].Real, precision: 8);
            Assert.Equal(a[i].Imaginary, b[i].Imaginary, precision: 8);
        }
    }

    [Fact]
    public void RenormalizeSCube_ThenConvert_DivergesFromDirect_ComplexTarget()
    {
        // Pins the divergence found above so a future change to either SToS or SToZ/SToY that
        // silently makes them agree (or disagree further) is visible here, not just in prose.
        var sCube = MakeTwoPortSCube();
        var z0Src = new[] { new Complex(50, 0), new Complex(50, 0) };
        var z0New = new[] { new Complex(75, 10), new Complex(75, 10) };   // complex — conventions diverge

        var direct = NetworkMetrics.ConvertSCube(sCube, z0Src, MatrixType.Z);
        var renormed = NetworkMetrics.RenormalizeSCube(sCube, z0Src, z0New);
        var viaRenorm = NetworkMetrics.ConvertSCube(renormed, z0New, MatrixType.Z);

        Assert.NotEqual(direct.ComplexValues[0].Real, viaRenorm.ComplexValues[0].Real, precision: 3);
    }

    [Fact]
    public void RenormalizeSCube_NonPositiveRealZ0_Throws()
    {
        var sCube = MakeTwoPortSCube();
        var z0Src = new[] { new Complex(50, 0), new Complex(50, 0) };
        var badTarget = new[] { new Complex(50, 0), new Complex(-5, 0) };

        Assert.Throws<ArgumentException>(() => NetworkMetrics.RenormalizeSCube(sCube, z0Src, badTarget));
    }

    // ---- §3: LoadpullSurface Γ-grid renormalization ---------------------------

    // A genuine power sweep (PavlDbm, 5 points) per grid point — ReducePoint needs >= 2 ascending
    // domain points to interpolate at all. Pout_dBm is linear in PavlDbm with a per-grid-point gain
    // offset, so a ConstantMetric("PavlDbm", …) constraint gives an exact, hand-verifiable value —
    // sidesteps needing to hand-synthesize a realistic Gt_dB-derived compression curve for the
    // AtCompression path (out of scope for this brief; the Γ-plane renorm itself is plane/constraint-
    // agnostic — Reduce's z0 branch runs identically regardless of which constraint kind produced yi).
    private static DataSet SyntheticGammaLoadpull(Complex[] gammas, double[] gainOffsetDb)
    {
        int nGrid = gammas.Length;
        int nPin  = 5;
        var grid = new Axis("gridPoint", Enumerable.Range(0, nGrid).Select(i => (double)i).ToArray());
        var pin  = new Axis("pinStep",   Enumerable.Range(0, nPin).Select(i => (double)i).ToArray());

        var pavl = new double[nGrid * nPin];
        var pout = new double[nGrid * nPin];
        var gt   = new double[nGrid * nPin];
        for (int gi = 0; gi < nGrid; gi++)
        for (int pi = 0; pi < nPin; pi++)
        {
            int idx = gi * nPin + pi;
            double p = pi;   // PavlDbm sweep: 0..4 dBm, ascending
            pavl[idx] = p;
            pout[idx] = p + gainOffsetDb[gi];
            gt[idx]   = gainOffsetDb[gi];
        }

        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube(new[] { grid }, gammas));
        ds.Add("ZLoad",     new DataCube(new[] { grid }, gammas.Select(g => RfHelpers.G2Z(g) * 50.0).ToArray()));
        ds.Add("Pout_dBm",  new DataCube(new[] { grid, pin }, pout));
        ds.Add("Gt_dB",     new DataCube(new[] { grid, pin }, gt));
        ds.Add("PavlDbm",   new DataCube(new[] { grid, pin }, pavl));
        return ds;
    }

    // 8 well-separated Γ grid points with varying gain so the RBF fit (MinFitNodes=6) succeeds.
    private static readonly Complex[] GammaGridPoints =
    {
        new(0.0, 0.0), new(0.2, 0.0), new(0.2, 0.2), new(0.0, 0.2),
        new(-0.2, 0.0), new(-0.2, -0.2), new(0.0, -0.2), new(0.3, 0.1),
    };
    private static readonly double[] GammaGridGainDb = { 10, 11, 9, 10.5, 10.2, 9.8, 10.7, 11.5 };

    private static DataSet MakeGammaGrid() => SyntheticGammaLoadpull(GammaGridPoints, GammaGridGainDb);

    private static readonly ConstraintSpec PavlAt2Dbm = ConstraintSpec.AtConstantMetric("PavlDbm", 2.0);

    [Fact]
    public void Reduce_Gamma_AtDefault50_IsIdentity()
    {
        var sfc = new LoadpullSurface(MakeGammaGrid());
        var noZ0 = sfc.Reduce(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, null);
        var at50 = sfc.Reduce(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(50, 0));

        Assert.Equal(GammaGridPoints.Length, noZ0.Coords.Length);
        Assert.Equal(noZ0.Coords.Length, at50.Coords.Length);
        for (int i = 0; i < noZ0.Coords.Length; i++)
        {
            Assert.Equal(noZ0.Coords[i].Real, at50.Coords[i].Real, precision: 12);
            Assert.Equal(noZ0.Coords[i].Imaginary, at50.Coords[i].Imaginary, precision: 12);
        }
    }

    [Fact]
    public void Reduce_Gamma_RenormalizesTo25_MatchesHandComputation()
    {
        var sfc = new LoadpullSurface(MakeGammaGrid());

        var at50 = sfc.Reduce(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(50, 0));
        var at25 = sfc.Reduce(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(25, 0));

        Assert.Equal(GammaGridPoints.Length, at50.Coords.Length);
        Assert.Equal(at50.Coords.Length, at25.Coords.Length);

        // Hand-compute the expected renorm for every scattered point: the stored grid is assumed
        // referenced to 50 Ω (RfCore.Loadpull.LoadpullSurface.AssumedSourceZ0 — no per-run reference
        // is carried in this DataSet format today), so renormalizing FROM the at50 reduction TO 25 Ω
        // must match Z2G(G2Z(at50)*50/25).
        for (int i = 0; i < at50.Coords.Length; i++)
        {
            var z = RfHelpers.G2Z(at50.Coords[i]) * (50.0 / 25.0);
            var expected = RfHelpers.Z2G(z);
            Assert.Equal(expected.Real, at25.Coords[i].Real, precision: 9);
            Assert.Equal(expected.Imaginary, at25.Coords[i].Imaginary, precision: 9);
            // And genuinely differs from the un-renormalized point (not a silent no-op).
            Assert.False(
                Math.Abs(expected.Real - at50.Coords[i].Real) < 1e-9 &&
                Math.Abs(expected.Imaginary - at50.Coords[i].Imaginary) < 1e-9,
                "renormalized point must differ from the 50 Ω reduction");
        }
    }

    [Fact]
    public void Fit_Cache_DistinguishesReference_ThenReusesIdentical()
    {
        var sfc = new LoadpullSurface(MakeGammaGrid());

        var fit50a = sfc.Fit(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(50, 0));
        var fit25  = sfc.Fit(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(25, 0));
        var fit50b = sfc.Fit(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Gamma, new Complex(50, 0));

        Assert.NotNull(fit50a);
        Assert.NotNull(fit25);
        Assert.NotNull(fit50b);

        // Same reference, re-fit → cache hit → the SAME object.
        Assert.Same(fit50a, fit50b);
        // Different reference → a genuinely different fit (not merely a different object — different
        // node coordinates too, since the underlying Reduce differs).
        Assert.NotSame(fit50a, fit25);
        Assert.NotEqual(fit50a!.Rbf.NodesRe[0], fit25!.Rbf.NodesRe[0]);
    }

    [Fact]
    public void Fit_ZPlane_UnaffectedByZ0()
    {
        var sfc = new LoadpullSurface(MakeGammaGrid());

        var fitNoZ0 = sfc.Fit(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Z, null);
        var fitZ25  = sfc.Fit(0, "Pout_dBm", PavlAt2Dbm, SurfacePlane.Z, new Complex(25, 0));

        Assert.NotNull(fitNoZ0);
        Assert.NotNull(fitZ25);
        // Different cache entries (z0 is part of the key), but IDENTICAL values — Reduce's renorm
        // branch is gated to SurfacePlane.Gamma only, so a Z-plane fit must not move at all.
        Assert.NotSame(fitNoZ0, fitZ25);
        for (int i = 0; i < fitNoZ0!.Rbf.NodeCount; i++)
        {
            Assert.Equal(fitNoZ0.Rbf.NodesRe[i], fitZ25!.Rbf.NodesRe[i], precision: 12);
            Assert.Equal(fitNoZ0.Rbf.NodesIm[i], fitZ25.Rbf.NodesIm[i], precision: 12);
        }
    }
}
