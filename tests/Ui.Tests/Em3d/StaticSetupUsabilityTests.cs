// ================================================================
//  StaticSetupUsabilityTests.cs — brief-dfr34-4: an electrostatic setup's ground as a list, the conductor list, a Pick
//  through the dielectric, and the C_ground cube. The via-net half is in Layout/TestPadRound26Tests (the ten-layer fixture).
// ================================================================

using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class StaticSetupUsabilityTests
{
    /// <summary>StaticBodiesTests' board with its Inner 1 plane a ground-reference layer, and one more piece of copper on
    /// Inner 2 that touches nothing: a plane on a layer the technology does not mark as ground.</summary>
    private static (EmSetup Setup, EmLayoutSource Source, string Island, string Pad) Board()
    {
        var (setup, source) = StaticBodiesTests.Board(gndNets: false);
        source.View.Shapes.Add(new RectShape { Layer = new LayerKey(3, 0), X1 = 13_000_000, Y1 = -2_000_000, X2 = 15_000_000, Y2 = 2_000_000 });
        source.Technology!.Stackup.Layers.Single(l => l.Name == "Inner 1 (Ground Plane)").IsGroundReference = true;
        var first = Em3dGenerator.Generate(setup, source, source.Technology);
        Assert.True(first.Ok, first.Refusal);
        string pad = StaticBodiesTests.PieceAt(first.Problem!, "Bottom Copper (1 oz)", 10, 0);
        setup.Terminals3D = [new("T", "", null, [pad])];
        return (setup, source, StaticBodiesTests.PieceAt(first.Problem!, "Inner 2", 14, 0), pad);
    }

    // ── R-dfr34-4-1 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>"+ default ground, &lt;island&gt;" grounds the ground-reference plane (and the metal touching it) AND the
    /// island; the island's name alone grounds only the island, which is what one name always meant.</summary>
    [Fact]
    public void GroundList_KeepsTheDefaultGround_AndAddsTheNamedPlane()
    {
        var (setup, source, island, _) = Board();
        string plane = StaticBodiesTests.PieceAt(Em3dGenerator.Generate(setup, source, source.Technology!).Problem!,
                                                 "Inner 1 (Ground Plane)", 2, 0);

        setup.Ground3D = $"{Em3dGroundList.DefaultEntry}, body:{island}";
        var both = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(both.Ok, both.Refusal);
        Assert.Contains(plane, both.Problem!.GroundObjects);
        Assert.Contains(island, both.Problem.GroundObjects);
        Assert.DoesNotContain(both.Problem.Terminals, t => t.Floating);
        var low = GmshGeoWriter.Write(both.Problem, PalaceSettings.Default);
        Assert.True(low.Ok, low.Refusal);
        Assert.True(PalaceConfigWriter.Write(both.Problem, low.Groups, PalaceSettings.Default).Ok);

        setup.Ground3D = island;
        var alone = Em3dGenerator.Generate(setup, source, source.Technology!);
        Assert.True(alone.Ok, alone.Refusal);
        Assert.DoesNotContain(plane, alone.Problem!.GroundObjects);
        Assert.Contains(alone.Problem.Terminals, t => t.Floating && t.Objects.Contains(plane));
    }

    [Fact]
    public void GroundList_Add_KeepsTheDefaultOnABlankBox_AndListsANameOnce()
    {
        Assert.Equal("+ default ground, GND", Em3dGroundList.Add("", "GND"));
        Assert.Equal("P, GND", Em3dGroundList.Add("P", "GND"));
        Assert.Equal("P, GND", Em3dGroundList.Add("P, GND", "GND"));
        Assert.True(Em3dGroundList.IncludesDefault(""));
        Assert.False(Em3dGroundList.IncludesDefault("P"));
        Assert.Equal(new[] { "P" }, Em3dGroundList.Names("+ default ground, P"));
    }

    // ── R-dfr34-4-2 / -3 ────────────────────────────────────────────────────────────────────────

    /// <summary>The list: two lines each, net-carrying bodies first; a ground body marked "(ground)" in the ground list and not
    /// offered to a terminal; a body in a terminal carrying its name; a Pick on dielectric takes the conductor behind it, and the
    /// ground's Pick adds to what the box holds.</summary>
    [Fact]
    public void ThePickerMarksGroundAndTerminals_AndAPickPassesThroughTheDielectric()
    {
        var (setup, source, island, pad) = Board();
        var bodies = Em3dBodyList.Of(Em3dLayoutSolids.From(source, source.Technology!, null,
            new Em3dLayoutSolidsOptions(20e9, 25, Instance: false) { RegionSetup = setup }));
        var vm = new EmSetupEditorViewModel("/nowhere/board.cem", setup, embedded: true);
        vm.EmbeddedCommit = (_, _, _) => { };
        vm.SetBodies(bodies);

        var planes = bodies.Single(b => b.IsDefaultGround);
        Assert.DoesNotContain(vm.BodyPicks, p => p.Body == planes);
        Assert.EndsWith("(ground)", vm.GroundPicks.Single(p => p.Body == planes).Label);
        var signal = vm.GroundPicks.Single(p => p.Body?.Members.Contains(pad) == true);
        Assert.EndsWith("(in T)", signal.Label);
        Assert.Contains("1 via", signal.Detail);
        Assert.Contains("mm", signal.Detail);

        // A click on the substrate: the ray's next object is the island's copper.
        vm.PickTerminalCommand.Execute(vm.GroundPickRow);
        Assert.False(vm.PickConductor(["dielectric/FR-4"]));
        Assert.Contains("no conductor is behind it", vm.PickRefusal);
        Assert.True(vm.PickConductor(["dielectric/FR-4", island]));
        Assert.Equal($"{Em3dGroundList.DefaultEntry}, body:{island}", vm.Working.Ground3D);
        Assert.Contains(vm.GroundPicks, p => p.Body?.Members.Contains(island) == true && p.Label.EndsWith("(ground)"));
    }

    // ── R-dfr34-4-5 ─────────────────────────────────────────────────────────────────────────────

    /// <summary>C_ground is the row sums of C to 1e-12 on Capacitance Extraction's Maxwell matrix (its expected numbers), and an
    /// electrostatic result carries C_mutual whether or not the solver's own file was read.</summary>
    [Fact]
    public void CGround_IsTheRowSumsOfC_AndCMutualIsAlwaysThere()
    {
        double[,] c = { { 531.87e-15, -31.0e-15 }, { -31.0e-15, 2460.11e-15 } };
        var ds = Em3dStaticResult.Build(Em3dProblemType.Electrostatic, ["pad", "line"], c, null, []);
        var g = ds[Em3dStaticResult.GroundCapacitanceCube];
        Assert.Equal(new[] { "pad", "line" }, g.Axis(Em3dStaticResult.AxisI).Labels!);
        for (int i = 0; i < 2; i++)
            Assert.True(Math.Abs(g.RealValues[i] - (c[i, 0] + c[i, 1])) <= 1e-12 * Math.Abs(c[i, i]));
        var m = ds[Em3dStaticResult.MutualCapacitanceCube].RealValues;
        Assert.Equal(g.RealValues[1], m[3], 1e-27);
        Assert.Equal(31.0e-15, m[1], 1e-27);
    }
}
