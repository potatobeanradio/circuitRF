// ================================================================
//  StratifiedSubFeedExtractionTests.cs — MIM-4 / milestone 4, the extractor half.
//
//  PlanarExtractor used to refuse outright when more than one dielectric sat between the ground
//  plane and the lowest analysis level: "L9's Green's function handles a stratified medium happily —
//  what does not is the de-embedding … Merge the layers under the feed into one substrate entry."
//
//  That merge was a change to the PHYSICS offered as a workaround: two dielectrics in series under a
//  trace are not one dielectric of either εᵣ, and the only reason to pretend otherwise was that C_pul
//  came from an image series over one grounded slab. MIM-4's InteriorStaticImages removes the reason,
//  so the layers are carried at their stated thicknesses and the de-embedding solves in the real
//  medium at the port level's own height.
//
//  These tests hold the replacement behaviour by name — R-mom-17's own corollary, that a refusal
//  which outlives its truth is the failure the rule exists to prevent.
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class StratifiedSubFeedExtractionTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static readonly LayerKey Signal = new(1, 0);

    /// <summary>
    /// Ground / 60 µm εᵣ 9.8 / 40 µm εᵣ 2.2 / 2 µm signal metal / open. Two dielectrics under the
    /// feed with a 4.5× permittivity ratio and no metal between them, which is exactly the shape the
    /// retired refusal named — and the ratio is large enough that no single εᵣ could stand in for the
    /// pair by accident.
    /// </summary>
    private static Technology Stratified() => new()
    {
        Name = "stratified sub-feed",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = Um(1),
        DefaultFlattenTolDbu = Um(1),
        Layers = [new LayerDef { Key = Signal, Name = "Signal", ZOrder = 1, Purpose = "drawing" }],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Ground,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Signal", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, DrawingLayers = [Signal] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Upper core", ThicknessDbu = Um(40), Epsr = 2.2, TanD = 0.001 },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Lower core", ThicknessDbu = Um(60), Epsr = 9.8, TanD = 0.002 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Ground", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, IsGroundReference = true },
            ],
        },
    };

    /// <summary>The same stackup with the two cores merged into ONE entry of the series-equivalent
    /// εᵣ — the workaround the retired refusal told the user to apply by hand. It is here so the
    /// difference between the two can be measured rather than asserted.</summary>
    private static Technology Merged()
    {
        var t = Stratified();
        double epsSeries = 100.0 / (40.0 / 2.2 + 60.0 / 9.8);
        t.Stackup.Layers =
        [
            t.Stackup.Layers[0],
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(100), Epsr = epsSeries, TanD = 0.0016 },
            t.Stackup.Layers[3],
        ];
        return t;
    }

    private static PlanarExtractionResult Extract(Technology tech) =>
        PlanarExtractor.Extract(
            [new RectShape { Layer = Signal, X1 = 0, Y1 = 0, X2 = Um(1200), Y2 = Um(90) }],
            tech, Dbu, 20e9);

    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>It extracts, and it carries BOTH dielectrics at their stated thicknesses.</b> The refusal
    /// that stood here is gone; the medium is the real one.
    /// </summary>
    [Fact]
    public void AStratifiedSubFeedRegionExtracts_AndBothLayersReachTheMedium()
    {
        var r = Extract(Stratified());
        Assert.True(r.Ok, r.Refusal);

        var p = r.Problem!;
        var stack = p.EffectiveStack;
        Assert.Equal(2, stack.LayerCount);
        Assert.Equal(60e-6, stack.Layers[0].ThicknessM, 12);
        Assert.Equal(40e-6, stack.Layers[1].ThicknessM, 12);
        Assert.Equal(9.8, stack.Layers[0].Material.EpsR, 9);
        Assert.Equal(2.2, stack.Layers[1].Material.EpsR, 9);
        Assert.Equal(100e-6, stack.TopZ, 12);

        // ONE conductor level, and the general kernel is on anyway — before MIM-4 an explicit medium
        // was attached only for a multi-level problem, and handing L8's one-slab kernel this stack
        // would be the plausible-wrong-answer failure L9d's D5 note guards against.
        Assert.Single(p.Layers);
        Assert.True(p.RequiresGeneralKernel);
    }

    /// <summary>
    /// The old advice — merge the layers — is not equivalent to carrying them, and the extraction
    /// says which is which. The SIZING slab is the series equivalent of the two (that is the right
    /// average for a mesh and a phase seed), so the two technologies agree on the slab and disagree
    /// on the medium, which is exactly the separation the note describes.
    /// </summary>
    [Fact]
    public void TheSizingSlabIsTheSeriesEquivalent_AndTheMediumIsNot()
    {
        var strat = Extract(Stratified());
        var merged = Extract(Merged());
        Assert.True(strat.Ok, strat.Refusal);
        Assert.True(merged.Ok, merged.Refusal);

        double epsSeries = 100.0 / (40.0 / 2.2 + 60.0 / 9.8);
        Assert.Equal(epsSeries, strat.Problem!.Slab.Material.EpsR, 6);
        Assert.Equal(merged.Problem!.Slab.Material.EpsR, strat.Problem!.Slab.Material.EpsR, 6);
        Assert.Equal(merged.Problem!.Slab.HeightM, strat.Problem!.Slab.HeightM, 12);

        // …and the media genuinely differ, or the test above proves nothing.
        Assert.Equal(1, merged.Problem!.EffectiveStack.LayerCount);
        Assert.Equal(2, strat.Problem!.EffectiveStack.LayerCount);
    }

    /// <summary>
    /// <b>The note replaces the refusal, and it says the thing the user needs.</b> Not "merge the
    /// layers": what the run actually did, and what the effective εᵣ it prints is and is not for.
    /// </summary>
    [Fact]
    public void ANoteNamesTheLayersAndSaysWhatTheEffectiveEpsIsFor()
    {
        var r = Extract(Stratified());
        Assert.True(r.Ok, r.Refusal);

        string note = Assert.Single(r.Notes, n => n.Contains("dielectric layers between the ground plane",
                                                             StringComparison.Ordinal));
        Assert.Contains("'Upper core'", note, StringComparison.Ordinal);
        Assert.Contains("'Lower core'", note, StringComparison.Ordinal);
        Assert.Contains("carried into the medium", note, StringComparison.Ordinal);
        Assert.Contains("series-capacitance", note, StringComparison.Ordinal);
        Assert.Contains("never as the published reference impedance", note, StringComparison.Ordinal);

        // The retired advice must not survive anywhere in the result.
        Assert.DoesNotContain(r.Notes, n => n.Contains("Merge the layers", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(r.Notes, n => n.Contains("un-run Tier 4", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A SINGLE dielectric under the feed is untouched: one layer in the medium, the slab's own
    /// material bit for bit, no note, and — the part that matters — the one-slab kernel path, which
    /// is what R-mlp-1 requires of everything this brief did not have to change.
    /// </summary>
    [Fact]
    public void AOneDielectricSubFeedRegionIsUnchanged()
    {
        var tech = Stratified();
        tech.Stackup.Layers =
        [
            tech.Stackup.Layers[0],
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = Um(100), Epsr = 4.4, TanD = 0.02 },
            tech.Stackup.Layers[3],
        ];

        var r = Extract(tech);
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(4.4, p.Slab.Material.EpsR, 12);
        Assert.Equal(0.02, p.Slab.Material.TanD, 12);
        Assert.Equal(100e-6, p.Slab.HeightM, 12);
        Assert.False(p.RequiresGeneralKernel);
        Assert.DoesNotContain(r.Notes, n => n.Contains("dielectric layers between the ground plane",
                                                       StringComparison.Ordinal));
    }
}
