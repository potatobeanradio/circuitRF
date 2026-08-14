// ================================================================
//  CharmTracesAndGridReuseTests.cs  —  H7's framework-free half
//
//  R-h7-7   the .charm's picked-trace block: forward-compatible read, no block when empty.
//  R-h7-11  an imported grid is an arbitrary scatter and invalidates the RBF factorization.
//  R-h7-12  one moved Γ point re-solves one Γ point.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class CharmTracesAndGridReuseTests(ITestOutputHelper output)
{
    private static CircuitModel Model() => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[1,0]"] = "_v1/50",
                ["I[2,0]"] = "0.2*tanh(_v2/6)*(1+_v1*0.3)",
            },
        },
        Bias     = new BiasSpec { Vgs = -1.0, Vds = 20 },
        Settings = new HarmonicaSettings
        {
            HarmonicCount = 3, FrequencyHz = 2e9, Tol = 1e-8,
            BiasChokeHenries = 1e-6, DcBlockFarads = 1e-9,
            CompressionDb = 2.0, PinStartDbm = -10, PinMaxDbm = 20,
        },
    };

    // ══ R-h7-7 — the traces block ═══════════════════════════════════════════

    [Fact]
    public void PickedTraces_RoundTripThroughCharmIo()
    {
        var model = Model();
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        IReadOnlyList<CharmIo.CharmTrace> traces =
        [
            new("Gamma_intr[1, :]", "trace.1", "load glyph"),
            new("mag(Zs_conv[1, :])", "trace.2", null),
        ];

        string json = CharmIo.Write(model, terms, traces: traces);
        var back = CharmIo.ReadAll(json, baseDirectory: null);

        Assert.Equal(2, back.Traces.Count);
        Assert.Equal(traces[0], back.Traces[0]);
        Assert.Equal(traces[1], back.Traces[1]);
    }

    [Fact]
    public void NoPickedTraces_WritesNoBlock_SoAnUntouchedFileReSerialisesByteForByte()
    {
        var model = Model();
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        string a = CharmIo.Write(model, terms);
        string b = CharmIo.Write(model, terms, traces: []);

        Assert.DoesNotContain("\"Traces\"", a, StringComparison.Ordinal);
        Assert.Equal(a, b);

        var back = CharmIo.ReadAll(a, baseDirectory: null);
        Assert.Empty(back.Traces);
        Assert.Equal(a, CharmIo.Write(back.Model, back.Terminations, back.Appearance, back.Layout,
                                      back.Traces));
    }

    [Fact]
    public void ATraceEntryWithNoSpec_IsDropped_AndOneWithNoPanelStillOpens()
    {
        // Hand-written, or written by a future version that dropped a field: the forward-compatible
        // read rule is "an unknown block is ignored, a missing field is a default".
        const string json = """
            {
              "FormatVersion": 1,
              "Traces": [
                { "Spec": "Gamma_intr[1, :]" },
                { "Label": "nothing to plot" },
                { "Spec": "Zin[:]", "Panel": "trace.7" }
              ]
            }
            """;

        var back = CharmIo.ReadAll(json, baseDirectory: null);
        foreach (var t in back.Traces) output.WriteLine($"{t.Spec}  panel={t.PanelId}  label={t.Label}");

        Assert.Equal(2, back.Traces.Count);
        Assert.Equal("Gamma_intr[1, :]", back.Traces[0].Spec);
        Assert.False(string.IsNullOrWhiteSpace(back.Traces[0].PanelId));
        Assert.Equal("trace.7", back.Traces[1].PanelId);
    }

    // ══ brief-harmonicarf-r6b §2.2 — added grid points ══════════════════════

    [Fact]
    public void AddedGridPoints_RoundTripThroughCharmIo()
    {
        var model = Model();
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        IReadOnlyList<Complex> added = [new(0.3, -0.2), new(-0.1, 0.55)];

        string json = CharmIo.Write(model, terms, addedGridPoints: added);
        var back = CharmIo.ReadAll(json, baseDirectory: null);

        Assert.Equal(2, back.AddedGridPoints.Count);
        Assert.Equal(added[0], back.AddedGridPoints[0]);
        Assert.Equal(added[1], back.AddedGridPoints[1]);
    }

    [Fact]
    public void NoAddedGridPoints_WritesNoBlock_SoAnUntouchedFileReSerialisesByteForByte()
    {
        var model = Model();
        var terms = new TerminationSet(model.Settings.HarmonicCount);

        string a = CharmIo.Write(model, terms);
        string b = CharmIo.Write(model, terms, addedGridPoints: []);

        Assert.DoesNotContain("\"AddedGridPoints\"", a, StringComparison.Ordinal);
        Assert.Equal(a, b);

        var back = CharmIo.ReadAll(a, baseDirectory: null);
        Assert.Empty(back.AddedGridPoints);
    }

    // ══ R-h7-11 / R-h7-12 — the grid ═══════════════════════════════════════

    [Fact]
    public void AnArbitraryScatter_IsAValidGrid_BecauseThatIsWhatContourGridWasBuiltFor()
    {
        var ctx = HarmonicaContext.Create(Model());
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(40, 5));

        // Not a lattice: what a .gam file, or a hand-authored grid, actually looks like.
        Complex[] scatter =
        [
            new(0.00,  0.00), new(0.42,  0.11), new(-0.31, 0.48),
            new(0.09, -0.55), new(-0.62, -0.13), new(0.25, 0.30), new(-0.10, -0.20),
        ];

        var grid = new ContourGrid();
        grid.Build(ctx, terms, scatter);

        Assert.Equal(scatter.Length, grid.Points.Count);
        for (int i = 0; i < scatter.Length; i++)
            Assert.Equal(scatter[i], grid.Points[i].Gamma);

        output.WriteLine($"{grid.Points.Count} points, {grid.HoleCount} holes, {grid.SolveCount} solves");
    }

    [Fact]
    public void ReuseIsOffByDefault_SoAnOrdinaryFrameNeverKeepsAPreviousGridsAnswer()
    {
        var ctx = HarmonicaContext.Create(Model());
        var terms = new TerminationSet(3);
        terms.Set(TerminationSide.Load, 1, new Complex(40, 5));
        var scatter = ContourGrid.RingGrid(2, 6).ToArray();

        var grid = new ContourGrid();
        grid.Build(ctx, terms, scatter);
        grid.Build(ctx, terms, scatter);            // no reuseUnchanged — the default

        Assert.Equal(0, grid.ReusedPointCount);
    }
}
