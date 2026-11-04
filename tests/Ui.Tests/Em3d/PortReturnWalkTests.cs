using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-dfr28-4 — a 3D port's return is walked from the artwork. A line-end port walks down and up from its line's
//  end, as the pad-to-plane port already walked from its pad, and takes the nearer side's first plane with metal under
//  it, a ground-reference one preferred; other metal across the way is a refusal naming it. A technology that flags no
//  ground reference returns through the nearest metal (D1); a setup's legacy return plane is still honoured (D2); and
//  a port may state its own return in the 3D port table (R-dfr28-4-5).
//
//  On the shipped four-layer board: Top Copper (1 oz) — 203 µm — Inner 1 (Ground Plane) — 1067 µm — Inner 2 — 203 µm —
//  Bottom Copper (1 oz). Drawing layers 1-4 in that order.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class PortReturnWalkTests
{
    private const int Dbu = 1000;
    private static readonly LayerKey L1 = new(1, 0), L2 = new(2, 0), L3 = new(3, 0), L4 = new(4, 0);
    private const string Top = "Top Copper (1 oz)", In1 = "Inner 1 (Ground Plane)", In2 = "Inner 2", Bottom = "Bottom Copper (1 oz)";

    private static long Mm(double v) => (long)Math.Round(v * 1000 * Dbu);

    /// <summary>The four-layer board with the ground references named, the rest unflagged.</summary>
    private static Technology Tech(params string[] flagged)
    {
        var t = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        foreach (var l in t.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor))
            l.IsGroundReference = flagged.Contains(l.Name);
        return t;
    }

    /// <summary>A plane under the whole line and past both its ends.</summary>
    private static RectShape Pour(LayerKey layer) => new() { Layer = layer, X1 = Mm(-2), Y1 = Mm(-5), X2 = Mm(12), Y2 = Mm(5) };

    /// <summary>Metal on the layer, but nowhere under the line.</summary>
    private static RectShape Elsewhere(LayerKey layer) => new() { Layer = layer, X1 = Mm(0), Y1 = Mm(3), X2 = Mm(10), Y2 = Mm(5) };

    /// <summary>A 0.3 mm line from x = 0 to 10 mm on <paramref name="layer"/>, a port at each end, and the planes.</summary>
    private static List<LayoutShape> Line(LayerKey layer, params LayoutShape[] planes) =>
    [
        new RectShape { Layer = layer, X1 = 0, Y1 = Mm(-0.15), X2 = Mm(10), Y2 = Mm(0.15) },
        Port("P1", 0, layer, PlanarPortKind.Edge, LayoutRotation.R0),
        Port("P2", 10, layer, PlanarPortKind.Edge, LayoutRotation.R180),
        .. planes,
    ];

    private static LabelShape Port(string text, double xMm, LayerKey layer, PlanarPortKind kind, LayoutRotation? direction = null)
        => new()
        {
            Layer = layer, PortLayer = layer, X = Mm(xMm), Y = 0, Text = text, Height = Mm(0.2), IsPort = true,
            PortKind = kind, PortDirection = direction,
        };

    private static Em3dGenerationResult Generate(Technology tech, IEnumerable<LayoutShape> shapes, Action<EmSetup>? edit = null)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        var setup = new EmSetup
        {
            Name = "rp", LayoutRef = "rp/layout/rp.clay",
            Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        edit?.Invoke(setup);
        return Em3dGenerator.Generate(setup, new EmLayoutSource("/nowhere/rp.clay", view, tech, Dbu), tech);
    }

    private static string Returns(Em3dGenerationResult g, int port)
        => g.Problem!.Ports.Single(p => p.Number == port).NegativeObject;

    // ── R-dfr28-4-1 / gate 2 — the nearer side ──────────────────────────────────────────────────

    /// <summary>A line on Inner 1 between Top (203 µm above) and Bottom (1.29 mm below), both planes: it returns up,
    /// to the nearer, and the note says so. A line on Inner 2 with its two planes the same distance away returns below,
    /// as the inferred plane always did.</summary>
    [Fact]
    public void AStripline_ReturnsThroughTheNearerPlane_AndBelowOnATie()
    {
        var up = Generate(Tech(Top, Bottom), Line(L2, Pour(L1), Pour(L4)));
        Assert.True(up.Ok, up.Refusal);
        Assert.StartsWith(Top + "/", Returns(up, 1), StringComparison.Ordinal);
        Assert.Contains(up.Notes, n => n.Contains($"from '{In1}' up to '{Top}'", StringComparison.Ordinal));

        var tech = Tech(In1, Bottom);
        tech.Stackup.Layers.Single(l => l.Name == "Prepreg (bottom)").ThicknessDbu =
            tech.Stackup.Layers.Single(l => l.Name == "Core").ThicknessDbu;
        var tie = Generate(tech, Line(L3, Pour(L2), Pour(L4)));
        Assert.True(tie.Ok, tie.Refusal);
        Assert.StartsWith(Bottom + "/", Returns(tie, 1), StringComparison.Ordinal);
    }

    // ── gate 3 — unflagged metal on the way ─────────────────────────────────────────────────────

    /// <summary>Only Bottom is a ground reference. Inner 2's metal away from the line leaves Bottom the return; Inner 2's
    /// metal under the whole of the line's end is reached first, and is the return (brief-dfr34-1 R-dfr34-1-5; metal under
    /// part of it still refuses — PortReturnOpeningTests).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnflaggedPlane_IsPassedWhenItIsNotUnderThePort_AndIsTheReturnWhenItIs(bool under)
    {
        var g = Generate(Tech(Bottom), Line(L1, under ? Pour(L3) : Elsewhere(L3), Pour(L4)));

        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith((under ? In2 : Bottom) + "/", Returns(g, 1), StringComparison.Ordinal);
    }

    // ── R-dfr28-4-2 / gates 4 and 5 ─────────────────────────────────────────────────────────────

    /// <summary>D1 — a technology that flags no ground reference: the nearest metal under the port is its return, and
    /// the note says why.</summary>
    [Fact]
    public void NoGroundReferenceAnywhere_TheNearestMetalIsTheReturn()
    {
        var g = Generate(Tech(), Line(L1, Pour(L3), Pour(L4)));

        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith(In2 + "/", Returns(g, 1), StringComparison.Ordinal);
        Assert.Contains(g.Notes, n => n.Contains("marks no conductor as a ground reference", StringComparison.Ordinal));
    }

    /// <summary>No metal under the port on either side, and no floor (Bottom is drawn, elsewhere): refused, naming every
    /// layer looked at and what it had.</summary>
    [Fact]
    public void NoMetalUnderThePort_IsRefused_NamingEachLayerLookedAt()
    {
        var g = Generate(Tech(Bottom), Line(L1, Elsewhere(L4)));

        Assert.False(g.Ok);
        Assert.Contains("below it, ", g.Refusal, StringComparison.Ordinal);
        foreach (string layer in new[] { In1, In2, Bottom })
            Assert.Contains($"'{layer}' (no metal under the line's end)", g.Refusal, StringComparison.Ordinal);
    }

    // ── gate 6 — the cropped board edge ─────────────────────────────────────────────────────────

    /// <summary>A board cropped at the line's ends: the plane's edge lies exactly on each end, and is found there.</summary>
    [Fact]
    public void APlaneCutExactlyAtTheLinesEnd_IsFound_AndTheSheetStaysAtTheEnd()
    {
        var plane = new RectShape { Layer = L4, X1 = 0, Y1 = Mm(-5), X2 = Mm(10), Y2 = Mm(5) };
        var g = Generate(Tech(Bottom), Line(L1, plane));

        Assert.True(g.Ok, g.Refusal);
        Assert.Equal(0, g.Problem!.Ports.Single(p => p.Number == 1).Min.X, 12);
        Assert.Equal(10e-3, g.Problem.Ports.Single(p => p.Number == 2).Min.X, 12);
        Assert.All(g.Problem.Ports, p => Assert.StartsWith(Bottom + "/", p.NegativeObject, StringComparison.Ordinal));
    }

    // ── R-dfr28-4-3 / R-dfr28-4-5 / gates 7 and 8 — a stated return ────────────────────────────

    /// <summary>A line on Inner 2: the walk takes Bottom, 203 µm below. The setup's legacy return plane (D2) moves every
    /// port to Inner 1, above; port 2's own Return wins over it. Each note and each recorded return says who decided.</summary>
    [Fact]
    public void AStatedReturn_IsTaken_PortOverSetup_AndTheNoteSaysWhoStatedIt()
    {
        var shapes = Line(L3, Pour(L2), Pour(L4));
        var walked = Generate(Tech(In1, Bottom), shapes);
        Assert.StartsWith(Bottom + "/", Returns(walked, 1), StringComparison.Ordinal);
        Assert.Equal("walked", walked.PortReturns[1].DecidedBy);

        var g = Generate(Tech(In1, Bottom), shapes, s =>
        {
            s.GroundStackupLayerName = In1;
            s.Ports3D = [new EmPort3D(2, Return: Bottom)];
        });

        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith(In1 + "/", Returns(g, 1), StringComparison.Ordinal);
        Assert.StartsWith(Bottom + "/", Returns(g, 2), StringComparison.Ordinal);
        Assert.Equal(new Em3dPortReturn(In1, Above: true, Floor: false, "setup"), g.PortReturns[1]);
        Assert.Equal(new Em3dPortReturn(Bottom, Above: false, Floor: false, "port"), g.PortReturns[2]);
        Assert.Contains(g.Notes, n => n.StartsWith("Port 1 at its line's end", StringComparison.Ordinal) && n.EndsWith("as this setup names.", StringComparison.Ordinal));
        Assert.Contains(g.Notes, n => n.StartsWith("Port 2 at its line's end", StringComparison.Ordinal) && n.EndsWith("as port 2's Return states.", StringComparison.Ordinal));
    }

    /// <summary>A stated layer with no metal under the port, one with other metal in between, and one the technology does
    /// not have are each refused by name.</summary>
    [Theory]
    [InlineData(In2, false, "no metal of 'Inner 2' (as port 1's Return states) under its line's end")]
    [InlineData(Bottom, true, $"toward '{Bottom}' (as port 1's Return states), would cross metal on '{In2}'")]
    [InlineData("Nope", false, "Port 1's Return states 'Nope', but technology")]
    public void AStatedReturnThePortCannotReach_IsRefusedByName(string stated, bool metalBetween, string expected)
    {
        var g = Generate(Tech(Bottom), Line(L1, metalBetween ? Pour(L3) : Elsewhere(L3), Pour(L4)),
                         s => s.Ports3D = [new EmPort3D(1, Return: stated)]);

        Assert.False(g.Ok);
        Assert.Contains(expected, g.Refusal, StringComparison.Ordinal);
    }

    /// <summary>A pad-to-plane port honours its Return the same way: a pad on Inner 2 walks to Bottom, below, and goes
    /// to Inner 1, above, when its Return states it.</summary>
    [Fact]
    public void APadToPlanePort_HonoursItsReturn()
    {
        List<LayoutShape> shapes =
        [
            Pour(L2), Pour(L4),
            new CircleShape { Layer = L3, Cx = Mm(5), Cy = 0, R = Mm(0.75) },
            Port("P1", 5, L3, PlanarPortKind.Internal),
        ];
        var walked = Generate(Tech(In1, Bottom), shapes);
        Assert.True(walked.Ok, walked.Refusal);
        Assert.StartsWith(Bottom + "/", Returns(walked, 1), StringComparison.Ordinal);

        var g = Generate(Tech(In1, Bottom), shapes, s => s.Ports3D = [new EmPort3D(1, Return: In1)]);

        Assert.True(g.Ok, g.Refusal);
        Assert.StartsWith(In1 + "/", Returns(g, 1), StringComparison.Ordinal);
        Assert.Contains(g.Notes, n => n.Contains("pad-to-plane port", StringComparison.Ordinal) &&
                                      n.Contains("as port 1's Return states", StringComparison.Ordinal));
    }

    // ── R-dfr28-4-5 — the file and the table ────────────────────────────────────────────────────

    /// <summary>A port's Return is one key on its Ports3D entry, written only when stated, so a setup without it keeps
    /// its bytes.</summary>
    [Fact]
    public void TheReturnKey_RoundTrips_AndIsNeverWrittenAtItsDefault()
    {
        var setup = new EmSetup { Name = "rp", Ports3D = [new EmPort3D(3, Return: In1), new EmPort3D(5, Em3dPortKind.Wave)] };
        string json = EmSetupPersistence.Serialize(setup);
        Assert.Contains($"\"Return\": \"{In1}\"", json, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"Return\""));
        var back = EmSetupPersistence.Deserialize(json);
        Assert.Equal(In1, back.PortReturn3D(3));
        Assert.Null(back.PortReturn3D(5));
        Assert.Equal(json, EmSetupPersistence.Serialize(back));

        Assert.True(new EmPort3D(4, Return: "").IsDefault);
        string bare = EmSetupPersistence.Serialize(new EmSetup { Name = "rp", Ports3D = [new EmPort3D(4)] });
        Assert.DoesNotContain("Ports3D", bare, StringComparison.Ordinal);
    }

    /// <summary>The table's Return column: Automatic first, then every conductor layer, a ground reference marked, then the
    /// coplanar return (brief-dfr34-1). A
    /// lumped row's choice is written; a wave row writes none, since its return is the air box's face.</summary>
    [Fact]
    public void ThePortTablesReturnColumn_WritesALumpedPortsChoice_AndNoneForAWavePort()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(Line(L3, Pour(L2), Pour(L4)));
        var tech = Tech(In1, Bottom);
        var vm = new EmSetupEditorViewModel("/nowhere/rp.cem", new EmSetup { Name = "rp", LayoutRef = "rp.clay", Solver3D = Em3dSolverId.Palace })
        {
            ResolveLayout = _ => new EmLayoutSource("/nowhere/rp.clay", view, tech, Dbu),
        };
        vm.Refresh();

        Assert.Equal(["Automatic", Top, $"{In1}  — ground reference", In2, $"{Bottom}  — ground reference", EmPort3D.SameLayerDisplay],
                     vm.Port3DReturnChoices.Select(c => c.Display));
        Assert.Equal([1, 2], vm.Port3DRows.Select(r => int.Parse(r.Port)));
        Assert.All(vm.Port3DRows, r => Assert.Equal("Automatic", r.ReturnChoice!.Display));

        vm.Port3DRows[0].ReturnChoice = vm.Port3DReturnChoices.Single(c => c.Name == In1);
        Assert.Equal(In1, vm.Working.PortReturn3D(1));

        vm.Port3DRows[1].ReturnChoice = vm.Port3DReturnChoices.Single(c => c.Name == Bottom);
        vm.Port3DRows[1].Kind = Em3dPortKind.Wave;
        Assert.Null(vm.Working.PortReturn3D(2));
        Assert.Equal(Em3dPortKind.Wave, vm.Working.PortKind3D(2));
    }

    /// <summary>The table lists every port the layout labels even where circuitRF's own planar and cross-section
    /// extractions refuse the board (here: nothing flagged and an open bottom, so neither has a ground) — the rows are
    /// the labels as the 3D generator numbers them, not the planar extraction's ports.</summary>
    [Fact]
    public void ThePortTable_ListsTheLayoutsPorts_WhereThePlanarExtractionRefusesTheBoard()
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(Line(L1, Pour(L3)));
        var tech = Tech();
        tech.Stackup.Bottom = BoundaryCondition.Open;
        var vm = new EmSetupEditorViewModel("/nowhere/rp.cem", new EmSetup { Name = "rp", LayoutRef = "rp.clay", Solver3D = Em3dSolverId.Palace })
        {
            ResolveLayout = _ => new EmLayoutSource("/nowhere/rp.clay", view, tech, Dbu),
        };
        vm.Refresh();

        Assert.Empty(vm.PortRows);
        Assert.Equal([1, 2], vm.Port3DRows.Select(r => int.Parse(r.Port)));
        Assert.All(vm.Port3DRows, r => Assert.True(r.InLayout));
    }
}
