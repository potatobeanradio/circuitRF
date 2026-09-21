// An assembly drawing is a LAYER, not an unidentified file (reported from the field, 2026-09-21:
// besides the silkscreen, a real output set also ships a top and a bottom assembly drawing).
//
// It costs more than a label. A land pattern draws its courtyard on whatever the technology offers
// for the assembly role, and LandPatternLayers resolves that role by NAME when nothing declares an
// alias — so a set whose assembly drawing was identified gives every hand-placed footprint on that
// board its courtyard, and a set whose assembly drawing fell through to the mapping dialog gives
// them none, with a diagnostic nobody has a reason to read.
//
// THE FIXTURES NAME NO TOOL, VENDOR OR PRODUCT — only the SHAPE of the names, which is all the
// heuristic rung may ever key on. Root CLAUDE.md §"Commercial Vendor References".

using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public sealed class GerberAssemblyLayerTests
{
    /// <summary>
    /// <b>An assembly or fabrication drawing is identified by name, on the correct side.</b>
    /// </summary>
    /// <remarks>
    /// The names asserted are <c>NameFor</c>'s own — the spelling a set that DECLARES
    /// <c>AssemblyDrawing,Top</c> in its <c>%TF.FileFunction</c> already lands on. Two spellings of
    /// one thing would put a declared set and a merely-named set on two near-duplicate layers, which
    /// is the failure the whole <c>KindNames</c> table exists to prevent.
    /// </remarks>
    [Theory]
    [InlineData("Assembly_top.gbr",    "Assembly Top")]
    [InlineData("Assembly_bottom.gbr", "Assembly Bottom")]
    [InlineData("assy-top.gbr",        "Assembly Top")]
    [InlineData("board-assembly.gbr",  "Assembly")]
    [InlineData("fab_top.gbr",         "Fabrication Top")]
    [InlineData("fabrication.gbr",     "Fabrication")]
    public void AnAssemblyOrFabricationDrawingIsIdentifiedByName(string fileName, string expected)
    {
        var identity = GerberLayerCascade.Identify(
            Path.Combine(Path.GetTempPath(), fileName), read: null, jobFunction: null, destTech: null);

        Assert.Equal(expected, identity.LayerName);

        // A guess, and reported as one — the heuristic rung is the one that can be confidently wrong,
        // and nothing here may present it as a declaration.
        Assert.True(identity.IsGuess);

        // And NOT a conductor: an assembly drawing entering the stackup would put a part outline
        // into the electrical model.
        Assert.False(identity.IsConductor);
    }

    /// <summary>
    /// <b>And a technology carrying that layer resolves a land pattern's courtyard onto it</b> —
    /// which is the whole reason the rows above are worth having.
    /// </summary>
    [Fact]
    public void ATechnologyWithAnAssemblyTopLayerGivesALandPatternItsCourtyard()
    {
        var withOut = BoardTechnology(assemblyTop: false);
        var withIn = BoardTechnology(assemblyTop: true);

        var whyMissing = new List<string>();
        var missing = LandPatternLayers.Resolve(withOut, PCellLayerSelection.Default, whyMissing);
        Assert.NotNull(missing.Copper);
        Assert.Null(missing.Assembly);
        Assert.Contains(whyMissing, d => d.Contains("courtyard", StringComparison.OrdinalIgnoreCase));

        var resolved = LandPatternLayers.Resolve(withIn, PCellLayerSelection.Default, []);
        Assert.NotNull(resolved.Assembly);
        Assert.Equal(
            withIn.Layers.Single(l => l.Name == "Assembly Top").Key,
            resolved.Assembly);
    }

    // ── fixture ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The smallest thing <c>LandPatternLayers</c> will call a board: a top conductor
    /// sitting directly on a solid dielectric.</summary>
    private static Technology BoardTechnology(bool assemblyTop)
    {
        var tech = new Technology { Name = "test board" };
        tech.Layers.Add(new LayerDef { Key = new LayerKey(1, 0), Name = "Top Copper" });
        tech.Layers.Add(new LayerDef { Key = new LayerKey(2, 0), Name = "Bottom Copper" });
        if (assemblyTop)
            tech.Layers.Add(new LayerDef { Key = new LayerKey(30, 0), Name = "Assembly Top" });

        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top Copper",
            DrawingLayers = { new LayerKey(1, 0) }, ThicknessDbu = 35_000, SigmaSm = 5.8e7,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Core", Epsr = 4.4, ThicknessDbu = 1_500_000,
        });
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Bottom Copper",
            DrawingLayers = { new LayerKey(2, 0) }, ThicknessDbu = 35_000, SigmaSm = 5.8e7,
        });
        return tech;
    }
}
