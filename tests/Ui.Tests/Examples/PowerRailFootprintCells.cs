// ================================================================
//  PowerRailFootprintCells.cs — the four land patterns the Power Rail example places.
//
//  THE EXAMPLE SHIPS ITS FOOTPRINTS AS ORDINARY CELL FOLDERS, AND THIS IS WHAT KEEPS THEM HONEST.
//
//  brief-footprint-5 R-fp5-2a turns the example's thirteen capacitors into INSTANCES, which means
//  the workspace has to carry the cells they are instances of. Two ways were available and only one
//  of them survives contact with brief 1's own header:
//
//    - draw the lands by hand in the generator, which is a second implementation of IPC-7351B
//      sitting beside `ChipLandPatternGenerator` with nothing holding the two together; or
//    - GENERATE them with the application's own generator, on the example's own technology, commit
//      the result, and gate it here.
//
//  The second is what this file is. `ChipLandPatternGenerator` is the only thing that computes a
//  land, `examples/Power Rail/footprints/` holds what it computed, and the test below regenerates
//  and compares byte for byte — so a change to the case table, to the fillet goals or to the
//  example's technology fails here rather than shipping an example whose artwork no longer matches
//  the tool that draws it.
//
//  TO REGENERATE: `CIRCUITRF_WRITE_EXAMPLE_FOOTPRINTS=1 dotnet test tests/Ui.Tests --filter
//  FullyQualifiedName~PowerRailFootprintCells`. Deliberately opt-in and deliberately not the
//  default: a suite that rewrites its own goldens verifies nothing, which this repo has already paid
//  for once (the Hero references).
// ================================================================

using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PowerRailFootprintCells(ITestOutputHelper output)
{
    /// <summary>Cell folder name → the footprint reference it was generated from.</summary>
    public static readonly (string Cell, string Reference)[] Cells =
    [
        ("C0402",   "smt:0402@N"),
        ("C0603",   "smt:0603@N"),
        ("C0805",   "smt:0805@N"),
        ("C7343-31", "smt:7343-31@N"),
    ];

    private static FootprintRef Reference(string text)
    {
        Assert.True(FootprintRef.TryParse(text, out var r, out string? why), why);
        return r!;
    }

    public static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    public static string ExampleRoot() => Path.Combine(RepoRoot(), "examples", "Power Rail");

    public static Technology ExampleTechnology() =>
        TechPersistence.Deserialize(File.ReadAllText(
            Path.Combine(ExampleRoot(), "tech", "pcb-4layer-1p6mm.ctech")));

    /// <summary>The <c>.clay</c> text one footprint cell holds, generated from nothing but the
    /// reference and the technology.</summary>
    public static string Generate(string reference, Technology technology)
    {
        if (!FootprintRef.TryParse(reference, out var parsed, out string? refusal) || parsed is null)
            throw new InvalidOperationException($"'{reference}' is not a footprint reference: {refusal}");

        var result = ChipLandPatternGenerator.Generate(parsed, technology, PCellLayerSelection.Default);
        Assert.NotEmpty(result.Shapes);

        var view = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = technology.DefaultDisplayUnit,
            SnapDbu      = technology.DefaultSnapDbu,
            AngleMode    = AngleMode.AnyAngle,
        };
        view.Shapes.AddRange(result.Shapes);

        // The PINS travel and the port LABELS deliberately do not. A generated cell store writes
        // both; here the label would be a LabelShape on the COPPER layer, and this artwork is read
        // by railRF's own extraction after a flatten — a zero-area label on a conductor is a shape
        // the power-integrity walk has no use for and every reason to be confused by.
        foreach (var pin in result.Pins)
            view.Pins.Add(new LayoutPin
            {
                Name = pin.Name, X = pin.X, Y = pin.Y,
                WidthDbu = pin.WidthDbu, OutwardDeg = pin.OutwardDirectionDeg, Layer = pin.Layer,
            });

        return LayoutPersistence.Serialize(view);
    }

    [Fact]
    public void EveryShippedFootprintCellIsWhatTheGeneratorProducesOnThisTechnology()
    {
        var technology = ExampleTechnology();
        bool write = Environment.GetEnvironmentVariable("CIRCUITRF_WRITE_EXAMPLE_FOOTPRINTS") == "1";
        string root = Path.Combine(ExampleRoot(), "footprints");

        foreach (var (cell, reference) in Cells)
        {
            string cellDir = Path.Combine(root, cell);
            string clay = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout),
                                       cell + CellFolder.ViewExtension(ViewType.Layout));
            string expected = Generate(reference, technology);

            if (write)
            {
                Directory.CreateDirectory(root);
                if (!Directory.Exists(cellDir)) CellFolder.CreateCellFolder(root, cell);
                Directory.CreateDirectory(Path.GetDirectoryName(clay)!);
                File.WriteAllText(clay, expected);
                output.WriteLine($"wrote {clay}");
                continue;
            }

            Assert.True(File.Exists(clay),
                $"the Power Rail example places '{reference}' and ships no cell for it at '{clay}'.");
            Assert.Equal(expected, File.ReadAllText(clay));
        }

        Assert.False(write, "regeneration mode: rerun without CIRCUITRF_WRITE_EXAMPLE_FOOTPRINTS.");
    }

    /// <summary>
    /// R-fp5-1 / gate 1: the example's technology resolves copper, soldermask and silkscreen by
    /// ROLE, so a land pattern generates on it with its mask openings and its body outline — which
    /// is the whole reason the three layers were added.
    /// </summary>
    [Fact]
    public void TheExampleTechnologyResolvesCopperMaskAndSilkByRole()
    {
        var technology = ExampleTechnology();
        var diagnostics = new List<string>();
        var roles = LandPatternLayers.Resolve(technology, PCellLayerSelection.Default, diagnostics);

        Assert.Equal(new LayerKey(1, 0), roles.Copper);
        Assert.Equal(new LayerKey(5, 0), roles.Soldermask);
        Assert.Equal(new LayerKey(6, 0), roles.Silkscreen);

        // And NOT the courtyard, which this technology still declares nothing for — R-fp1-3c: the
        // outline layer it now has is Edge.Cuts, and a courtyard rectangle there is a routed slot.
        Assert.Null(roles.Assembly);
        Assert.Contains(diagnostics, d => d.Contains("courtyard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diagnostics, d => d.Contains("silkscreen", StringComparison.OrdinalIgnoreCase));

        var outline = technology.Layers.Single(l =>
            string.Equals(l.Interchange?.PcbLayerName, "Edge.Cuts", StringComparison.OrdinalIgnoreCase));
        var pattern = ChipLandPatternGenerator.Generate(
            Reference("smt:0402@N"), technology, PCellLayerSelection.Default);
        Assert.DoesNotContain(pattern.Shapes, s => s.Layer == outline.Key);
    }

    /// <summary>
    /// R-fp5-1c: a PCB technology with copper and no silk is a real case and must stay covered. The
    /// five-layer table this example shipped before the three drawing layers were added is kept as a
    /// fixture for exactly that, so R-fp1-3a's "omitted, and NAMED, never relocated" path still has
    /// a technology to be tested on.
    /// </summary>
    [Fact]
    public void ACopperOnlyBoardTechnologyStillOmitsAndNamesEveryOptionalRole()
    {
        string fixture = Path.Combine(RepoRoot(), "tests", "Ui.Tests", "Footprints", "Fixtures",
                                      "pcb-4layer-copper-only.ctech");
        Assert.True(File.Exists(fixture), $"the copper-only technology fixture is not at '{fixture}'.");

        var technology = TechPersistence.Deserialize(File.ReadAllText(fixture));
        var result = ChipLandPatternGenerator.Generate(
            Reference("smt:0402@N"), technology, PCellLayerSelection.Default);

        Assert.All(result.Shapes, s => Assert.Equal(new LayerKey(1, 0), s.Layer));
        foreach (string role in new[] { "soldermask", "silkscreen", "courtyard" })
            Assert.Contains(result.Diagnostics ?? [], d =>
                d.Contains(role, StringComparison.OrdinalIgnoreCase) &&
                d.Contains(technology.Name, StringComparison.Ordinal));
    }
}
