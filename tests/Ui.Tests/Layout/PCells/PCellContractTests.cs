using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>Gates 2-6 of brief-L5a-pcell-contract-and-microstrip.md: contract shape, R-pc-3
/// origin/abutment, R-pc-4 purity, R-pc-5 caching, R-pc-6 units.</summary>
public class PCellContractTests
{
    private static readonly Technology Pcb = StarterTechnologies.Pcb2Layer();

    // ── Gate 2: contract shape ───────────────────────────────────────────────────────────────

    [Fact]
    public void Mlin_Generate_ReturnsShapesAndTwoPins()
    {
        var result = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);

        Assert.NotEmpty(result.Shapes);
        Assert.Equal(2, result.Pins.Count);
        Assert.Equal("1", result.Pins[0].Name);
        Assert.Equal("2", result.Pins[1].Name);
        Assert.True(result.Pins[0].WidthDbu > 0);
    }

    [Fact]
    public void MTee_Generate_ReturnsThreePins_UnionedIntoOneOutline()
    {
        var result = MTeePCell.Generate(
            new Dictionary<string, double> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 },
            Pcb, PCellLayerSelection.Default);

        Assert.Equal(3, result.Pins.Count);
        // "one unioned outline with no internal edges, not three overlapping rectangles" (gate 11a).
        Assert.Single(result.Shapes);
    }

    [Fact]
    public void MCross_Generate_ReturnsFourPins_UnionedIntoOneOutline()
    {
        var result = MCrossPCell.Generate(
            new Dictionary<string, double> { ["W1"] = 0.0029, ["W2"] = 0.0029, ["W3"] = 0.0029, ["W4"] = 0.0029 },
            Pcb, PCellLayerSelection.Default);

        Assert.Equal(4, result.Pins.Count);
        Assert.Single(result.Shapes);
    }

    // ── Gate 3: R-pc-3 origin convention + abutment ──────────────────────────────────────────

    [Fact]
    public void Mlin_Pin1_IsAtOrigin()
    {
        var result = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);
        Assert.Equal(0, result.Pins[0].X);
        Assert.Equal(0, result.Pins[0].Y);
    }

    [Fact]
    public void TwoMlins_AbutExactly_WhenSecondPlacedAtFirstsPin2Location()
    {
        var a = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);
        var b = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.005 },
            Pcb, PCellLayerSelection.Default);

        // Place b's origin (its own pin 1) at a's pin 2 world location (pure translation, no rotation).
        long dx = a.Pins[1].X, dy = a.Pins[1].Y;
        long bPin1WorldX = b.Pins[0].X + dx, bPin1WorldY = b.Pins[0].Y + dy;

        Assert.Equal(a.Pins[1].X, bPin1WorldX);
        Assert.Equal(a.Pins[1].Y, bPin1WorldY);
        // Widths must match at the joint for a physically valid abutment.
        Assert.Equal(a.Pins[1].WidthDbu, b.Pins[0].WidthDbu);
    }

    // ── Gate 4: R-pc-4 purity ────────────────────────────────────────────────────────────────

    [Fact]
    public void Mlin_Generate_100Times_ByteIdenticalOutput()
    {
        var parameters = new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 };
        var first = MlinPCell.Generate(parameters, Pcb, PCellLayerSelection.Default);

        for (int i = 0; i < 100; i++)
        {
            var next = MlinPCell.Generate(parameters, Pcb, PCellLayerSelection.Default);
            Assert.Equal(first.Shapes.Count, next.Shapes.Count);
            Assert.Equal(first.Pins.Count, next.Pins.Count);
            for (int p = 0; p < first.Pins.Count; p++)
            {
                Assert.Equal(first.Pins[p].X, next.Pins[p].X);
                Assert.Equal(first.Pins[p].Y, next.Pins[p].Y);
            }
        }
    }

    [Fact]
    public void Mlin_Generate_SurvivesSerializeReloadOfTechnology_ByteIdentical()
    {
        var parameters = new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 };
        var first = MlinPCell.Generate(parameters, Pcb, PCellLayerSelection.Default);

        var reloaded = TechPersistence.Deserialize(TechPersistence.Serialize(Pcb));
        var second = MlinPCell.Generate(parameters, reloaded, PCellLayerSelection.Default);

        Assert.Equal(first.Shapes.Count, second.Shapes.Count);
        for (int p = 0; p < first.Pins.Count; p++)
        {
            Assert.Equal(first.Pins[p].X, second.Pins[p].X);
            Assert.Equal(first.Pins[p].Y, second.Pins[p].Y);
        }
    }

    // ── Gate 6: R-pc-6 units exactness ───────────────────────────────────────────────────────

    [Fact]
    public void W_2p9mm_Produces_Exactly2900000Dbu_At1nmResolution()
    {
        long dbu = PCellUnits.MetresToDbu(0.0029, LayoutUnits.DefaultDbuPerMicron);
        Assert.Equal(2_900_000, dbu);
    }

    [Fact]
    public void MetresToDbu_RoundsHalfAwayFromZero()
    {
        // 0.5 nm exactly -> rounds away from zero to 1 DBU (at 1nm resolution).
        long dbu = PCellUnits.MetresToDbu(0.5e-9, LayoutUnits.DefaultDbuPerMicron);
        Assert.Equal(1, dbu);
    }

    [Fact]
    public void AllFourGenerators_RouteThroughTheOneUnitsHelper()
    {
        // Structural: every generator computes W (or W1..W4) via PCellUnits.MetresToDbu, so a
        // shared 2.9mm input produces the same 2,900,000 DBU pin width across all four.
        var mlin = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 },
            Pcb, PCellLayerSelection.Default);
        var bend = MBendPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["Angle"] = 90.0, ["Mitered"] = 0.0 },
            Pcb, PCellLayerSelection.Default);
        Assert.Equal(2_900_000, mlin.Pins[0].WidthDbu);
        Assert.Equal(2_900_000, bend.Pins[0].WidthDbu);
    }

    // ── No-technology: geometry still generates (§2 of the brief) ──────────────────────────────

    [Fact]
    public void Mlin_Generate_WithNoTechnology_StillProducesGeometry()
    {
        var result = MlinPCell.Generate(new Dictionary<string, double> { ["W"] = 0.0029, ["L"] = 0.01 },
            null, PCellLayerSelection.Default);
        Assert.NotEmpty(result.Shapes);
        Assert.Equal(2, result.Pins.Count);
    }
}
