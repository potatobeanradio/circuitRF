// ================================================================
//  UnboundLayerArtworkTests.cs — user-proposed, 2026-08-30: remove the dielectric's Drawing layer
//  control from the .ctech editor, on the reading that the binding is invisible to the user and
//  serves nothing but internal wiring. Probing it first found the opposite of invisible, and then
//  found something bigger. The binding's one real effect was to stop CrossSectionExtractor REFUSING
//  on artwork drawn over the slab — and
//  the same refusal fired on Outline, Silk Top/Bottom and Soldermask Top/Bottom of the shipped
//  2-layer PCB starter. Every PCB layout has a board outline, so the normal case was the failing
//  one, and the advice it gave ("add this drawing layer to a conductor entry") amounted to declaring
//  your board outline to be copper.
//
//  So the control went, but only after the rule underneath it was fixed: DECLARED-but-unbound is
//  the technology stating a layer is not metal (ignore, with a note naming the layers); UNDECLARED
//  is the case nobody has said anything about (still refuses).
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class UnboundLayerArtworkTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Mil(double m) => (long)Math.Round(m * 25.4 * Dbu);

    /// <summary>A solvable microstrip, so nothing below can pass merely by having no conductors.</summary>
    private static RectShape Trace(Technology tech) => new()
    {
        Layer = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference)
                                   .DrawingLayers[0],
        X1 = 0, Y1 = 0, X2 = Mil(2000), Y2 = Mil(140),
    };

    private static EmExtractionResult Extract(Technology tech, params LayoutShape[] extra)
        => CrossSectionExtractor.Extract([Trace(tech), .. extra], tech, Dbu);

    private static LayerKey LayerNamed(Technology tech, string name)
        => tech.Layers.First(l => l.Name == name).Key;

    // ── The defect this was really about ──────────────────────────────────────────────────────

    /// <summary>The one every PCB layout hits. A board outline must not refuse an EM run.</summary>
    [Theory]
    [InlineData("Outline")]
    [InlineData("Silk Top")]
    [InlineData("Silk Bottom")]
    [InlineData("Soldermask Top")]
    [InlineData("Soldermask Bottom")]
    public void ArtworkOnADeclaredNonMetalLayer_IsIgnoredWithANote_NotRefused(string layerName)
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, new RectShape
        {
            Layer = LayerNamed(tech, layerName),
            X1 = 0, Y1 = 0, X2 = Mil(2000), Y2 = Mil(1000),
        });

        Assert.True(r.Ok, r.Refusal);
        var note = Assert.Single(r.Notes, n =>
            n.Contains("binds to no stackup entry", StringComparison.Ordinal));
        Assert.Contains(layerName, note, StringComparison.Ordinal);
        // The note must still offer the fix, for the case where the shape really was meant to be metal.
        Assert.Contains("bind it to a conductor entry", note, StringComparison.Ordinal);
    }

    /// <summary>Ignoring is never silent, and the note names every distinct layer once — the whole
    /// reason this is safe to relax rather than a shape quietly vanishing from a solve.</summary>
    [Fact]
    public void TheNoteNamesEveryDistinctLayer_AndDoesNotRepeatOne()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var silk = LayerNamed(tech, "Silk Top");
        var r = Extract(tech,
            new RectShape { Layer = silk, X1 = 0, Y1 = 0, X2 = Mil(10), Y2 = Mil(10) },
            new RectShape { Layer = silk, X1 = Mil(20), Y1 = 0, X2 = Mil(30), Y2 = Mil(10) },
            new RectShape { Layer = LayerNamed(tech, "Outline"), X1 = 0, Y1 = 0, X2 = Mil(99), Y2 = Mil(99) });

        Assert.True(r.Ok, r.Refusal);
        var note = Assert.Single(r.Notes, n => n.Contains("binds to no stackup entry", StringComparison.Ordinal));
        Assert.Contains("2 layer(s)", note, StringComparison.Ordinal);
        Assert.Contains("Silk Top", note, StringComparison.Ordinal);
        Assert.Contains("Outline", note, StringComparison.Ordinal);
    }

    /// <summary>The refusal is NARROWED, not deleted. A layer the technology never declares is still
    /// the case where nothing says whether it is metal — and the message must no longer tell anyone
    /// to bind it to a conductor as the first move.</summary>
    [Fact]
    public void ArtworkOnAnUndeclaredLayer_StillRefuses_AndSaysWhichTabToFixItIn()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var r = Extract(tech, new RectShape
        {
            Layer = new LayerKey(742, 3),          // declared by nothing
            X1 = 0, Y1 = 0, X2 = Mil(100), Y2 = Mil(100),
        });

        Assert.False(r.Ok);
        Assert.Contains("does not declare at all", r.Refusal!, StringComparison.Ordinal);
        Assert.Contains("Layers tab", r.Refusal!, StringComparison.Ordinal);
    }

    // ── What the dielectric binding was doing, and that it is no longer needed ────────────────

    /// <summary>The MMIC die outline — the artwork the dielectric binding existed for. It must now
    /// extract with the binding REMOVED, which is what makes the editor control redundant.</summary>
    [Fact]
    public void TheMmicDieOutline_ExtractsWithNoDielectricBindingAtAll()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var gaas = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric && l.Name == "GaAs");
        var substrate = gaas.DrawingLayers[0];
        gaas.DrawingLayers.Clear();

        var r = Extract(tech, new RectShape
        {
            Layer = substrate, X1 = 0, Y1 = 0, X2 = Mil(200), Y2 = Mil(200),
        });

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n => n.Contains("binds to no stackup entry", StringComparison.Ordinal));
    }

    /// <summary>A file that still CARRIES a dielectric binding keeps its original, more specific
    /// note. Removing the control must not change how an existing .ctech behaves.</summary>
    [Fact]
    public void AShippedDielectricBinding_StillTakesTheSubstrateExtentPath()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var gaas = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric && l.Name == "GaAs");
        Assert.NotEmpty(gaas.DrawingLayers);            // the shipped binding is still there

        var r = Extract(tech, new RectShape
        {
            Layer = gaas.DrawingLayers[0], X1 = 0, Y1 = 0, X2 = Mil(200), Y2 = Mil(200),
        });

        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n =>
            n.Contains("bound only to a DIELECTRIC stackup entry", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("binds to no stackup entry", StringComparison.Ordinal));
    }

    // ── The editor control ────────────────────────────────────────────────────────────────────

    /// <summary>Only a via still offers a single-layer picker. The cardinality property is left
    /// alone on purpose — a dielectric that HAS a binding still holds at most one.</summary>
    [Fact]
    public void OnlyAViaRow_OffersADrawingLayerPicker()
    {
        var vm = new TechEditorViewModel("/tmp/x.ctech", StarterTechnologies.MmicGaAs());
        StackupLayerRowViewModel Row(string name) => vm.StackupLayers.First(r => r.StagedName == name);

        Assert.True(Row("Backside Via").ShowsDrawingLayerPicker);
        Assert.False(Row("GaAs").ShowsDrawingLayerPicker);           // the control that went
        Assert.False(Row("Metal1").ShowsDrawingLayerPicker);         // conductors use the checkbox list

        Assert.True(Row("GaAs").IsSingleDrawingLayer);               // cardinality rule unchanged
        Assert.True(Row("Metal1").AllowMultipleDrawingLayers);
    }
}
