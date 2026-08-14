// ================================================================
//  StabilityCircleMarkerImpedanceTests.cs — brief-dd-stability-circle-marker-impedance
//
//  A marker on a load/source stability circle sits on a Γ-plane LOCUS, not on an S-matrix element.
//  Its impedance must therefore come from the marker POSITION, at the reference the locus itself
//  lives in — which BuildDerivedPath fixes, via NetworkMetrics.TwoPortUniformReal, at
//  Re(z0[InputPort−1]) on BOTH ports. Two defects are pinned here:
//
//   1. GetMarkerImpedanceString's per-port ("unusual source") branch ran BEFORE the derived check
//      and read S[Row, Col] — which for a derived trace is S11, since Derived forces Row = Col = 0.
//      The readout was then a constant that did not move when the marker did. Only reachable on a
//      non-uniform source, which is exactly where it was found.
//   2. The reference used was Trace.Z0 / port 1's, not Re(z0[InputPort−1]) — wrong whenever the
//      input port is not port 1, or the source reference is complex.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class StabilityCircleMarkerImpedanceTests
{
    private const double F0 = 1e9;

    // A potentially-unstable 2-port (|S21| large, |S11|,|S22| high) so real stability circles exist.
    private static SNP MakeFet(Complex uniformZ0)
    {
        var snp = new SNP([F0], 2, MatrixType.S, MatrixFormat.MA, uniformZ0);
        snp.Matrices[0][0, 0] = Complex.FromPolarCoordinates(0.70, -60 * Math.PI / 180);
        snp.Matrices[0][0, 1] = Complex.FromPolarCoordinates(0.05,  30 * Math.PI / 180);
        snp.Matrices[0][1, 0] = Complex.FromPolarCoordinates(3.00,  80 * Math.PI / 180);
        snp.Matrices[0][1, 1] = Complex.FromPolarCoordinates(0.60, -40 * Math.PI / 180);
        return snp;
    }

    private static Trace MakeCircleTrace(SNP snp, Complex[]? perPort, bool unusual,
                                         DerivedParameters kind = DerivedParameters.LoadStabilityCircle,
                                         int inPort = 1, int outPort = 2)
    {
        var t = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex)
        {
            SourceZ0PerPort   = perPort,
            SourceZ0IsUnusual = unusual,
            InputPort         = inPort,
            OutputPort        = outPort,
            Derived           = kind,
        };
        t.BuildPath(PlotType.Smith, FreqUnit.GHz);
        return t;
    }

    private static Marker PlaceMarker(Trace t, Complex gamma)
    {
        var m = new Marker(t, F0, isMulti: false, isDelta: false, index: 1) { Freq = F0 };
        m.UseNormalizedImpedance = false;
        m.PositionStatic = new System.Numerics.Vector2((float)gamma.Real, (float)gamma.Imaginary);
        return m;
    }

    private static string Expected(Complex gamma, Complex z0, Marker m)
    {
        var Z = z0 * (Complex.Conjugate(z0) / z0 + gamma) / (Complex.One - gamma);
        return $"impedance={m.FormatComplex(Z)} Ω";
    }

    // ---- Defect 1: the readout must track the marker, on a NON-UNIFORM source ----

    [Fact]
    public void NonUniformSource_ImpedanceFollowsMarkerPosition_NotS11()
    {
        var snp   = MakeFet(new Complex(50, 0));
        var trace = MakeCircleTrace(snp, [new Complex(50, 0), new Complex(12, 0)], unusual: true);

        Assert.True(trace.IsStabilityCircle, "pre-condition: a stability-circle trace");
        Assert.NotEmpty(trace.StabilityCircleCentres);

        var gA = new Complex(0.30, 0.10);
        var gB = new Complex(-0.20, 0.45);

        var mA = PlaceMarker(trace, gA);
        Assert.True(trace.MarkerShowsImpedance(mA), "pre-condition: the box shows an impedance line");
        string a = trace.GetMarkerImpedanceString(mA);

        var mB = PlaceMarker(trace, gB);
        string b = trace.GetMarkerImpedanceString(mB);

        // The regression in one line: the old per-port branch returned the S11-derived impedance,
        // identical for every marker position on the circle.
        Assert.NotEqual(a, b);

        // And each is the Γ at the locus's reference — Re(z0[InputPort−1]) = 50 Ω.
        var zRef = new Complex(50, 0);
        Assert.Equal(Expected(gA, zRef, mA), a);
        Assert.Equal(Expected(gB, zRef, mB), b);

        // Concretely NOT the S11 answer the old code gave.
        Assert.NotEqual(Expected(snp.Matrices[0][0, 0], zRef, mA), a);
    }

    [Fact]
    public void UniformSource_ImpedanceIsGammaAtFiftyOhms()
    {
        var trace = MakeCircleTrace(MakeFet(new Complex(50, 0)), null, unusual: false);
        var g     = new Complex(0.25, -0.35);
        var m     = PlaceMarker(trace, g);

        Assert.Equal(Expected(g, new Complex(50, 0), m), trace.GetMarkerImpedanceString(m));
    }

    // ---- Defect 2: the reference is Re(z0[InputPort−1]), not port 1's ----

    [Fact]
    public void ReferenceFollowsInputPort_NotPortOne()
    {
        // Ports swapped: the input is port 2 (12 Ω), so TwoPortUniformReal renormalizes BOTH ports
        // to 12 Ω and the circle lives in a 12 Ω Γ plane.
        var trace = MakeCircleTrace(MakeFet(new Complex(50, 0)),
                                    [new Complex(50, 0), new Complex(12, 0)], unusual: true,
                                    inPort: 2, outPort: 1);
        var g = new Complex(0.30, 0.10);
        var m = PlaceMarker(trace, g);

        Assert.Equal(new Complex(12, 0), trace.MarkerZ0);
        Assert.Equal(Expected(g, new Complex(12, 0), m), trace.GetMarkerImpedanceString(m));
        Assert.NotEqual(Expected(g, new Complex(50, 0), m), trace.GetMarkerImpedanceString(m));
    }

    [Fact]
    public void ComplexSourceReference_UsesRealPartOnly()
    {
        // TwoPortUniformReal's target is new Complex(z0[a].Real, 0) — the readout must mirror that
        // exactly, or the impedance disagrees with the circle actually drawn.
        var trace = MakeCircleTrace(MakeFet(new Complex(50, 0)),
                                    [new Complex(50, -10), new Complex(50, -10)], unusual: true);
        var g = new Complex(0.2, 0.2);
        var m = PlaceMarker(trace, g);

        Assert.Equal(new Complex(50, 0), trace.MarkerZ0);
        Assert.Equal(Expected(g, new Complex(50, 0), m), trace.GetMarkerImpedanceString(m));
    }

    // ---- The Z0 box must not leak into a derived trace ----

    [Fact]
    public void Z0Override_DoesNotMoveADerivedReadout()
    {
        // The Z0 control is not shown for a derived trace (ShowZ0Control requires Derived == None),
        // but a trace can carry a stale Z0/Override from a previous non-derived binding — and
        // BuildDerivedPath ignores both, so the readout must too, or it would state an impedance
        // the drawn circle does not have.
        var trace = MakeCircleTrace(MakeFet(new Complex(50, 0)), null, unusual: false);
        var g     = new Complex(0.25, -0.35);
        var m     = PlaceMarker(trace, g);
        string before = trace.GetMarkerImpedanceString(m);

        trace.Z0 = new Complex(75, 0);
        trace.Z0OverrideEnabled = true;

        Assert.Equal(before, trace.GetMarkerImpedanceString(m));
        Assert.Equal(new Complex(50, 0), trace.MarkerZ0);
    }

    // ---- Source stability circles take the same path ----

    [Fact]
    public void SourceStabilityCircle_SameRule()
    {
        var trace = MakeCircleTrace(MakeFet(new Complex(50, 0)),
                                    [new Complex(50, 0), new Complex(12, 0)], unusual: true,
                                    kind: DerivedParameters.SourceStabilityCircle);
        Assert.True(trace.IsStabilityCircle);
        Assert.NotEmpty(trace.StabilityCircleCentres);

        var g = new Complex(-0.15, 0.40);
        var m = PlaceMarker(trace, g);
        Assert.Equal(Expected(g, new Complex(50, 0), m), trace.GetMarkerImpedanceString(m));
    }

    // ---- µ is unaffected (it never used the marker position) ----

    [Fact]
    public void MuLineStillMatchesNetworkMetrics()
    {
        var perPort = new[] { new Complex(50, 0), new Complex(12, 0) };
        var snp     = MakeFet(new Complex(50, 0));
        var trace   = MakeCircleTrace(snp, perPort, unusual: true);
        var m       = PlaceMarker(trace, new Complex(0.3, 0.1));

        double expected = RfCore.Data.NetworkMetrics.TwoPortMetric(
            snp.Matrices, perPort, RfCore.Data.NetworkMetric.Mu, 1, 2)[0];

        string line = trace.MuString(m);
        Assert.StartsWith("Load Stability, µ=", line);
        Assert.Contains(expected.ToString($"{m.FormatString}{m.MaximumFractionDigits}"), line);
    }
}
