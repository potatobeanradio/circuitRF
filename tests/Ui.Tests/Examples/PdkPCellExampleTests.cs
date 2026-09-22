// ================================================================
//  PdkPCellExampleTests.cs — the "PDK PCells" example workspace.
//
//  Every assertion here is a bug this example actually shipped with (2026-09-15), and every one of
//  them was SILENT: the artwork drew, the workspace opened, and nothing anywhere said what was
//  wrong.
//
//   1. A PARAMETER WITH NO DECLARED DEFAULT IS NOT PLACED. The generator falls back to its own
//      internal number and draws correctly, so the cell looks finished — while the instance carries
//      no parameters at all, the Properties Inspector has nothing to edit, and every drag handle is
//      rejected with "declares a drag handle for 'L', which is not one of its parameters."
//
//   2. THE DEFAULT SIGNAL LAYER IS THE TOPMOST CONDUCTOR, and on this GaAs stackup that is the AIR
//      BRIDGE — 3 um up with nothing under it. A coil drawn there is suspended in air, and the
//      microstrip testbench beside it extracted h = 102.75 um through air, nitride and GaAs rather
//      than the 100 um of substrate it is supposed to demonstrate.
//
//   3. A COMMITTED PCELL INSTANCE HAS TO REBUILD ON SOMEBODY ELSE'S MACHINE. The generated cell
//      folder is content-addressed and git-ignored, so the .clay in the repository names a folder
//      that does not exist until a generator runs. If the name it rebuilds to differs, every
//      instance is repointed and the committed file is rewritten on first open.
//
//   4. A KIT THAT SHIPS ONLY ARTWORK HAD NO SCHEMATIC SIDE AT ALL. Update Schematic from Layout on
//      the spiral created a schematic and placed NOTHING in it, reporting that the kit "is not
//      loaded" — it was; what it had no part for was the cell. The kit now ships a .csym per
//      generator and circuitRF mounts a part around it, carrying the generator's own declared
//      parameters. See PCellKitSchematicParts.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine.Mom;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

[Collection(PCellResolverCollection.Name)]
public sealed class PdkPCellExampleTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _scratch = [];

    public void Dispose()
    {
        PCellRegistry.ClearResolvers();
        foreach (var d in _scratch) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private const string Mlin    = "KIT_MLIN";
    private const string Spiral  = "KIT_SPIRAL";
    private const string OSpiral = "KIT_OSPIRAL";
    private const string MimCap  = "KIT_MIMCAP";

    /// <summary>The example technology's resolution, and every length below is in its DBU.</summary>
    private const int Dbu = 1000;

    /// <summary>Metal1 is the conductor that sits on the GaAs; Metal2 is the air bridge above it.</summary>
    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey Metal2 = new(2, 0);
    private static readonly LayerKey Via    = new(3, 0);

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    private static string ExampleRoot() => Path.Combine(RepoRoot(), "examples", "PDK PCells");

    private static Technology Tech()
        => TechPersistence.LoadFromFile(
               Path.Combine(ExampleRoot(), "tech", "mmic-GaAs_2LM_100um.ctech"));

    private PCellWorkerProvider StartKit(string workspaceRoot)
    {
        string kitDir = Path.Combine(workspaceRoot, "pcell-kit");
        var manifest = PCellGeneratorManifest.TryRead(kitDir, out _)
            ?? throw new InvalidOperationException($"No PCell manifest in '{kitDir}'.");

        // The package the application ships is on the kit's path in production; a test supplies it
        // the same way rather than relying on whatever happens to be importable here.
        return new PCellWorkerProvider(ProcessPCellWorkerTransport.Start(
            PythonRunner.Interpreter!, manifest.ResolveEntry(kitDir),
            [.. manifest.ResolvePythonPath(kitDir), PythonRunner.PackageRoot]));
    }

    // ══ 1. Every parameter is placeable, and every handle is draggable ═══════

    /// <summary>
    /// The whole parameter list arrives on a placed instance, and every drag handle names a
    /// parameter that is on it.
    ///
    /// <para>These are one fact, not two: <see cref="PCellHandleSolver.Validate"/> is asked about
    /// the instance's PARAMETERS, so a parameter missing for want of a default takes its handle
    /// down with it. Asserting the handles alone would pass on a cell whose defaults were declared
    /// and whose geometry was wrong; asserting the defaults alone would miss a handle naming a
    /// parameter that was renamed.</para>
    /// </summary>
    [PythonFact]
    public void EveryDeclaredParameterHasADefault_AndEveryHandleIsDraggable()
    {
        using var kit = StartKit(ExampleRoot());
        var tech = Tech();

        foreach (string id in kit.GeneratorIds)
        {
            var declared = kit.DeclaredParameters(id)!;
            var defaults = kit.DeclaredDefaults(id)!;
            Assert.NotEmpty(declared);

            foreach (var d in declared)
                Assert.True(defaults.ContainsKey(d.Name),
                    $"'{id}' declares '{d.Name}' with no default, so a placed instance will not " +
                    "carry it: nothing to edit, and any handle naming it is rejected.");

            Assert.True(kit.TryGetGenerator(id, out var generate));
            var result = generate(defaults, tech, PCellLayerSelection.Default);

            foreach (var handle in result.Handles ?? [])
                Assert.Equal(PCellHandleRejection.None,
                             PCellHandleSolver.Validate(handle, defaults));

            output.WriteLine($"{id}: {declared.Count} parameter(s), {(result.Handles?.Count ?? 0)} handle(s)");
        }
    }

    /// <summary>
    /// The layer choice is a DROPDOWN, not a free-text box — the generator declares the two values
    /// it accepts, which is the only thing that can make the Properties Inspector offer them.
    ///
    /// <para>Every cell that HAS a choice of metal declares it this way; the MIM capacitor has none
    /// and declares no such parameter, because its stack is Metal1, the MIM dielectric and the MIM
    /// top plate in that order and there is no second arrangement of it. A parameter offering a
    /// choice that does not exist is worse than no parameter, so the absence is asserted too.</para>
    /// </summary>
    [PythonFact]
    public void TheMetalParameterOffersItsTwoLayersAsChoices()
    {
        using var kit = StartKit(ExampleRoot());

        foreach (string id in new[] { Mlin, Spiral, OSpiral })
        {
            var metal = Assert.Single(kit.DeclaredParameters(id)!, p => p.Name == "Metal");
            Assert.Equal(["Metal1", "Metal2"], metal.Choices!.Select(c => c.AsText()));
            Assert.Equal("Metal1", metal.Default!.Value.AsText());
        }

        Assert.DoesNotContain(kit.DeclaredParameters(MimCap)!, p => p.Name == "Metal");
    }

    // ══ 2. Nothing is drawn on the air bridge by default ════════════════════

    /// <summary>
    /// Both generators draw on Metal1 — the conductor that lies on the GaAs — and not on the
    /// technology's own default signal layer, which is the topmost conductor and therefore the air
    /// bridge. A coil suspended 3 um up in air over nothing is not a coil.
    /// </summary>
    [PythonFact]
    public void ThePlacedArtworkIsOnMetal1_NotOnTheAirBridge()
    {
        var tech = Tech();
        Assert.Equal(Metal2, SubstrateResolver.ResolveSignalLayerKey(tech, PCellLayerSelection.Default, out _));

        using var kit = StartKit(ExampleRoot());

        // The line is one rectangle and it is on the substrate metal, full stop.
        Assert.True(kit.TryGetGenerator(Mlin, out var line));
        var lineShapes = line(kit.DeclaredDefaults(Mlin)!, tech, PCellLayerSelection.Default).Shapes;
        Assert.All(lineShapes, s => Assert.Equal(Metal1, s.Layer));

        // The spiral is allowed geometry on Metal2 — that is its crossover — but the coil itself is
        // on the substrate metal, and the crossover is a single span rather than the bulk of the cell.
        Assert.True(kit.TryGetGenerator(Spiral, out var coil));
        var coilShapes = coil(kit.DeclaredDefaults(Spiral)!, tech, PCellLayerSelection.Default).Shapes;
        Assert.Contains(coilShapes, s => s.Layer == Metal1);
        Assert.Single(coilShapes, s => s.Layer == Metal2);
    }

    /// <summary>
    /// The spiral's inner terminal escapes on the OTHER metal, through via posts — three distinct
    /// layers, and both pins back on the coil's own one. Without the crossover the inner end of the
    /// coil is a terminal nothing can reach; with it on the wrong layer it shorts every turn it
    /// passes over.
    /// </summary>
    [PythonFact]
    public void TheSpiralsInnerTerminalEscapesOverAnAirBridge()
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(Spiral, out var generate));

        var coilOnMetal1 = generate(kit.DeclaredDefaults(Spiral)!, Tech(), PCellLayerSelection.Default);
        AssertThreeLayerEscape(coilOnMetal1, coil: Metal1, bridge: Metal2);

        // The inverse arrangement: the coil moves up onto the bridge metal and the escape becomes an
        // underpass. Asserted because it is what makes `Metal` a real parameter rather than a label.
        var flipped = new Dictionary<string, PCellValue>(kit.DeclaredDefaults(Spiral)!)
        {
            ["Metal"] = PCellValue.Text("Metal2"),
        };
        AssertThreeLayerEscape(generate(flipped, Tech(), PCellLayerSelection.Default),
                               coil: Metal2, bridge: Metal1);
    }

    private static void AssertThreeLayerEscape(PCellResult result, LayerKey coil, LayerKey bridge)
    {
        var layers = result.Shapes.Select(sh => sh.Layer).ToHashSet();
        Assert.Contains(coil,   layers);
        Assert.Contains(bridge, layers);
        Assert.Contains(Via,    layers);

        // Both terminals on the coil's own metal, or the cell abuts two different things.
        Assert.Equal(2, result.Pins.Count);
        Assert.All(result.Pins, p => Assert.Equal(coil, p.Layer));

        // The crossover spans every turn it has to clear: it lands on the terminal pad and reaches
        // past the innermost rail. A bridge that stops short is a broken terminal that still renders.
        var span = result.Shapes.OfType<RectShape>().Single(r => r.Layer == bridge);
        long innermost = result.Shapes.Where(sh => sh.Layer == coil).Max(RightmostX);
        long bridgeOuter = Math.Min(span.X1, span.X2);
        Assert.True(Math.Max(span.X1, span.X2) < innermost);

        // ...and it STOPS SHORT OF THE PIN, which is the whole point of the pad running past it
        // (kit.py RULE 10). A port label standing where metal on two conductor levels overlaps is
        // refused by name at extraction — "driving the wrong one drives a different conductor with
        // the same footprint" — so pin 2 has to sit on metal the bridge does not reach. The two used
        // to be the same coordinate, and that terminal could not be driven at all.
        Assert.True(bridgeOuter > result.Pins[1].X,
            $"the {bridge} crossover reaches out to {bridgeOuter}, at or past pin 2 at " +
            $"{result.Pins[1].X}: there is no single-level metal left to place a port on.");
    }

    private static long RightmostX(LayoutShape shape) => shape switch
    {
        RectShape r    => Math.Max(r.X1, r.X2),
        PolygonShape p => Enumerable.Range(0, p.Xy.Length / 2).Max(i => p.Xy[i * 2]),
        _              => long.MinValue,
    };

    /// <summary>
    /// <b>The coil is emitted as the REGIONS it forms, not as the rectangles it was drawn from.</b>
    ///
    /// <para>Owner, 2026-09-15: the spiral was built out of many segments and should render as one
    /// continuous piece of metal. Overlapping rectangles already LOOK solid, so this is easy to leave
    /// — but they are one picture and not one figure: a DRC width check measures each rectangle
    /// rather than the conductor, an EM extraction meshes internal edges that carry no current, and
    /// an export carries every seam into whatever reads it next.</para>
    ///
    /// <para>The union is performed by circuitRF over the boolean channel, so the generator's answer
    /// and the layout editor's own Boolean commands cannot disagree — which is also what this test
    /// exercises, since a `clip` call is a host round trip made in the middle of a `generate`.</para>
    /// </summary>
    [PythonFact]
    public void TheCoilIsOneContinuousPieceOfMetal_NotSixteenRectangles()
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(Spiral, out var generate));
        var result = generate(kit.DeclaredDefaults(Spiral)!, Tech(), PCellLayerSelection.Default);

        var onCoil = result.Shapes.Where(sh => sh.Layer == Metal1).ToList();
        Assert.All(onCoil, sh => Assert.IsType<PolygonShape>(sh));

        // Two regions, and the second is not an accident: the coil with its outer lead is one piece,
        // and the landing pad the escape drops back onto is deliberately separate — it is joined
        // through a via, not through metal, which is the whole reason the crossover exists.
        Assert.Equal(2, onCoil.Count);

        // A three-turn square spiral plus its lead: one closed outline, and far fewer vertices than
        // the 16 rectangles (64 corners) that drew it. Asserted as a bound rather than an exact
        // count — the point is that the seams are gone, not a particular vertex total.
        var coilRegion = onCoil.Cast<PolygonShape>().MaxBy(p => p.Xy.Length)!;
        Assert.True(coilRegion.Xy.Length / 2 < 64,
                    $"the coil came back as {coilRegion.Xy.Length / 2} vertices — it was not merged.");
        Assert.True(coilRegion.Holes is null or { Count: 0 },
                    "a spiral's turns open to the outside; a hole means the outline closed on itself.");
    }

    /// <summary>
    /// With NO technology at all the three layers are still three layers. A layout that resolves no
    /// technology still generates geometry (the PCell contract's own §2), and a generator that
    /// leaned on <c>tech.signal_layer</c> there would collapse coil, bridge and via onto one — which
    /// draws perfectly and shorts the cell.
    /// </summary>
    [PythonFact]
    public void TheLayersSurviveALayoutWithNoTechnology()
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(Spiral, out var generate));
        AssertThreeLayerEscape(
            generate(kit.DeclaredDefaults(Spiral)!, technology: null, PCellLayerSelection.Default),
            coil: Metal1, bridge: Metal2);
    }

    /// <summary>
    /// <b>The winding contains exactly the metal its turn count calls for — no dead-ended stub.</b>
    ///
    /// <para>OWNER REPORT: for any turn count there was a trace that ran off to the right, bent
    /// twice and stopped. It was the outermost lap's free end. The outer lead was hung off the
    /// MIDDLE of the outermost side, so metal continued from it in both directions — one way
    /// spiralling inward, the other running three-quarters of a lap to the winding's real end and
    /// dead-ending there. In the same metal as the coil, with nothing in the picture to tell them
    /// apart, and an inductor with a ¾-lap open stub on it is a different part.</para>
    ///
    /// <para>Checked as AREA against a closed form, because that is what a stub changes and what no
    /// amount of looking at the outline will tell you. A rectilinear path of constant width w and
    /// centre-line length L, mitred square, covers exactly <c>L·w + w²</c>; and the centre line of a
    /// square spiral is an arithmetic series — two sides of every length, each pair one pitch longer
    /// than the last — so for n whole turns it sums to <c>8na + 2pn(2n−1)</c>. Both are written out
    /// here rather than asked of the generator: an oracle that shares the code under test proves
    /// nothing. The old stub was some 30% of the winding, against the 1% tolerance below.</para>
    /// </summary>
    [PythonTheory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void TheWindingHoldsExactlyTheMetalItsTurnCountCallsFor(int turns)
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(Spiral, out var generate));

        var defaults = kit.DeclaredDefaults(Spiral)!;
        long w     = PCellUnits.MetresToDbu(defaults["Width"].AsReal(), Dbu);
        long pitch = w + PCellUnits.MetresToDbu(defaults["Space"].AsReal(), Dbu);
        long a     = PCellUnits.MetresToDbu(defaults["Inner"].AsReal(), Dbu) / 2 + w / 2;
        long lead  = 2 * w;

        var parameters = new Dictionary<string, PCellValue>(defaults) { ["Turns"] = PCellValue.Real(turns) };
        var coil = generate(parameters, Tech(), PCellLayerSelection.Default)
                   .Shapes.OfType<PolygonShape>().Where(g => g.Layer == Metal1)
                   .MaxBy(g => Math.Abs(SignedArea(g)))!;

        long centreLine = 8L * turns * a + 2L * pitch * turns * (2L * turns - 1) + lead;
        double expected = (double)centreLine * w + (double)w * w;
        double actual   = Math.Abs(SignedArea(coil));

        Assert.True(Math.Abs(actual - expected) / expected < 0.01,
            $"{turns} turns should cover {expected:N0} DBU² of metal; the winding covers {actual:N0}. " +
            "More than that is metal on a path nothing asked for.");
    }

    /// <summary>Shoelace. The polygon is closed implicitly, like every <c>.clay</c> ring.</summary>
    private static double SignedArea(PolygonShape polygon)
    {
        double sum = 0;
        int n = polygon.Xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += (double)polygon.Xy[i * 2] * polygon.Xy[j * 2 + 1]
                 - (double)polygon.Xy[j * 2] * polygon.Xy[i * 2 + 1];
        }
        return sum / 2.0;
    }

    /// <summary>
    /// <b>A fractional turn count is a real turn count, and it moves the outer terminal.</b>
    ///
    /// <para>Owner: 3.5 turns should put the second terminal somewhere else. A square spiral changes
    /// heading every quarter lap, so each extra quarter brings the outer end out on the next side —
    /// and there is nothing to special-case, because the winding is walked rather than stacked out of
    /// whole rings. `Turns` is declared REAL for this; as an integer the parameter editor refuses
    /// "3.5" outright, which is the correct behaviour for a declared integer and the wrong
    /// declaration for a spiral.</para>
    /// </summary>
    [PythonFact]
    public void AQuarterTurnMovesTheOuterTerminalToTheNextSide()
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(Spiral, out var generate));
        var defaults = kit.DeclaredDefaults(Spiral)!;

        double OuterHeading(double turns)
        {
            var parameters = new Dictionary<string, PCellValue>(defaults) { ["Turns"] = PCellValue.Real(turns) };
            var result = generate(parameters, Tech(), PCellLayerSelection.Default);
            return result.Pins.Single(pin => pin.Name == "1").OutwardDirectionDeg;
        }

        // Four consecutive quarters, four different headings — and the fifth comes back round.
        var headings = new[] { 3.0, 3.25, 3.5, 3.75 }.Select(OuterHeading).ToList();
        Assert.Equal(4, headings.Distinct().Count());
        Assert.Equal(headings[0], OuterHeading(4.0));

        // The declaration is Real, or none of the above can be typed into the parameter editor:
        // an edit is parsed back into the kind the parameter already has, never coerced to another.
        Assert.Equal(PCellValueKind.Real, defaults["Turns"].Kind);
        Assert.Equal(PCellValueKind.Real,
                     kit.DeclaredParameters(Spiral)!.Single(d => d.Name == "Turns").Kind);
    }

    /// <summary>
    /// <b>Every grip has a floor, and the floor actually stops the drag.</b>
    ///
    /// <para>OWNER REPORT: the grip handles need a minimum or the geometry goes crazy. It does, and
    /// specifically: a width or an inner opening dragged through zero turns negative, every
    /// rectangle in the winding inverts, and what reaches the clipper is a self-intersecting mess
    /// that still renders. <see cref="PCellHandleSolver"/> clamps each proposal to the handle's own
    /// declared <c>Min</c>, so the bound has to be ON the handle — a guard inside the generator is
    /// never consulted by the solver's search, it only refuses the result afterwards.</para>
    ///
    /// <para>Driven through the real solver rather than by reading the declaration back: a bound
    /// that is declared and not honoured looks identical from the outside.</para>
    /// </summary>
    [PythonTheory]
    [InlineData(Mlin)]
    [InlineData(Spiral)]
    [InlineData(OSpiral)]
    [InlineData(MimCap)]
    public void DraggingAGripPastZero_StopsAtTheProcessMinimum(string generatorId)
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(generatorId, out var generate));
        var tech = Tech();
        var defaults = kit.DeclaredDefaults(generatorId)!;

        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> p)
            => generate(p, tech, PCellLayerSelection.Default);

        var handles = Generate(defaults).Handles!;
        Assert.NotEmpty(handles);

        for (int i = 0; i < handles.Count; i++)
        {
            var handle = handles[i];
            Assert.True(handle.Min is > 0,
                $"'{handle.Parameter}' has no declared floor — dragging it through zero inverts the cell.");

            // Aim the grip a long way past the anchor, which is the centre: the drag the report is
            // about. Where it lands is the generator's answer, and it must be at or above the floor.
            Assert.True(PCellHandleSolver.MeasureSensitivity(
                Generate, defaults, handle, i, out double valuePerProjection, out _));
            var solved = PCellHandleSolver.Solve(
                Generate, defaults, handle, i, targetProjection: -10_000_000, valuePerProjection);

            Assert.True(solved.Ok);
            double landed = solved.Value.AsReal();
            Assert.True(landed >= handle.Min!.Value - 1e-15,
                $"'{handle.Parameter}' was dragged to {landed}, under its declared floor of {handle.Min}.");
        }
    }

    /// <summary>
    /// And a value TYPED past the floor is refused, naming the parameter — because a handle's bound
    /// binds the gesture and nothing else. The Properties Inspector writes straight through to the
    /// generator, so the floor has to be stated in both places or it holds in only one of them.
    /// </summary>
    [PythonTheory]
    [InlineData(Mlin,    "W", "L")]
    [InlineData(Spiral,  "Width", "Space", "Inner")]
    [InlineData(OSpiral, "Width", "Space", "Inner")]
    [InlineData(MimCap,  "W", "L")]
    public void AValueTypedBelowTheProcessMinimum_IsRefusedRatherThanDrawn(
        string generatorId, params string[] lengths)
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(generatorId, out var generate));
        var defaults = kit.DeclaredDefaults(generatorId)!;

        foreach (string name in lengths)
        {
            var parameters = new Dictionary<string, PCellValue>(defaults)
            {
                [name] = PCellValue.Real(0.5e-6),   // half a micron, on a 4 µm process
            };
            var refusal = Assert.Throws<PCellWireException>(
                () => generate(parameters, Tech(), PCellLayerSelection.Default));
            Assert.Contains(name, refusal.Message, StringComparison.Ordinal);
        }

        // …and a negative one, which is the value that actually inverts the artwork.
        var inverted = new Dictionary<string, PCellValue>(defaults) { [lengths[0]] = PCellValue.Real(-50e-6) };
        Assert.Throws<PCellWireException>(() => generate(inverted, Tech(), PCellLayerSelection.Default));
    }

    // ══ 2b. A PORT CAN ACTUALLY BE PLACED ON EITHER TERMINAL ════════════════

    /// <summary>
    /// <b>A port label standing on each declared pin extracts as an ordinary EDGE port on the coil's
    /// own level — for both spirals, on both metals.</b>
    ///
    /// <para>Owner report, 2026-09-15. Two defects, one test, because the artwork fix for each is
    /// what makes the other's assertion reachable:</para>
    ///
    /// <list type="number">
    /// <item><b>Pin 2 stood on metal on two conductor levels at once.</b> The Metal1 landing pad was
    /// exactly the via's footprint with the crossover ending on top of it, so every point of the
    /// terminal carried Metal1 AND Metal2 — and <c>EmPortExtraction</c> refuses that by name
    /// ("driving the wrong one drives a different conductor with the same footprint"). The terminal
    /// could not be driven at all. The pad runs a lead past the via now (kit.py RULE 10).</item>
    /// <item><b>Pin 1 sat half a turn width INSIDE the metal</b>, because it was the end of the
    /// centre LINE and <c>_segment_runs</c> cuts a free end square half a width beyond that. A label
    /// there is in the conductor's interior, so it came out as an <c>Internal</c> to-ground port
    /// rather than an edge port — a different structure, answered plausibly. The pin is on the metal's
    /// own end face now (RULE 11), which is where KIT_MLIN has always put its two.</item>
    /// </list>
    /// </summary>
    [PythonTheory]
    [InlineData(Spiral,  "Metal1")]
    [InlineData(Spiral,  "Metal2")]
    [InlineData(OSpiral, "Metal1")]
    [InlineData(OSpiral, "Metal2")]
    public void APortOnEitherTerminalDrivesTheCoilsOwnLevel(string generatorId, string metal)
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(generatorId, out var generate));

        var parameters = new Dictionary<string, PCellValue>(kit.DeclaredDefaults(generatorId)!)
        {
            ["Metal"] = PCellValue.Text(metal),
        };
        var tech   = Tech();
        var result = generate(parameters, tech, PCellLayerSelection.Default);

        var shapes = new List<LayoutShape>(result.Shapes);
        foreach (var pin in result.Pins)
            shapes.Add(new LabelShape
            {
                Layer = pin.Layer, X = pin.X, Y = pin.Y, Text = pin.Name,
                Height = 5 * Dbu, IsPort = true, PortLayer = pin.Layer,
            });

        var planar = PlanarExtractor.Extract(shapes, tech, Dbu, 20e9);
        Assert.True(planar.Ok, planar.Refusal);

        var ports = EmPortExtraction.Extract(shapes, planar.Problem!, Dbu);
        Assert.True(ports.Ok, ports.Refusal);
        Assert.Equal(2, ports.Rows.Count);
        Assert.All(ports.Rows, r =>
        {
            Assert.Null(r.Problem);
            Assert.Equal(PlanarPortKind.Edge, r.Port!.Kind);
        });

        // Both on ONE level, and it is the coil's — the property "both terminals are on the coil's
        // own metal" is what lets the cell abut anything, and a port is where it becomes checkable.
        int level = Assert.Single(ports.Rows.Select(r => r.Port!.LayerIndex).Distinct())!.Value;
        Assert.Equal(metal, planar.Problem!.Layers[level].Name);
    }

    /// <summary>
    /// <b>A port placed on the PLACED INSTANCE reports the pin's own 10 µm, not the coil's 250 µm
    /// envelope.</b>
    ///
    /// <para>The other half of the same report, and the half a user actually sees: a port on an
    /// instance takes its width from the cell's pin only when the label lands on that pin EXACTLY
    /// (<c>LayoutConductorLookup.PinAt</c>, zero tolerance), and falls back to the instance's
    /// array-expanded BOUNDING BOX otherwise. So a pin a few micrometres off the metal's visible end
    /// is not a cosmetic error: the user aims at the end of the metal, geometry snap offers the edge
    /// midpoint there rather than the pin, and the port reports the whole part as its excitation
    /// width. One terminal came out right and one 25× wide, from one pin half a line width out.</para>
    /// </summary>
    [Fact]
    public void APortOnThePlacedCoilTakesItsWidthFromThePin_NotTheInstanceBox()
    {
        string clay = Path.Combine(ExampleRoot(), "SpiralInductor", "layout", "SpiralInductor.clay");
        var view = LayoutPersistence.LoadFromFile(clay);
        var tech = Tech();
        var lookup = LayoutConductorLookup.LookupFor(view, tech, Path.GetDirectoryName(clay)!);

        var instance = Assert.Single(view.Instances);
        string cellDir = RefPath.Resolve(Path.GetDirectoryName(clay)!, instance.CellRef);
        var cell = LayoutPersistence.LoadFromFile(
            Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));
        Assert.Equal(2, cell.Pins.Count);

        foreach (var pin in cell.Pins)
        {
            var label = new LabelShape
            {
                Layer = pin.Layer, X = pin.X, Y = pin.Y, Text = pin.Name,
                Height = 5 * Dbu, IsPort = true,
            };
            var hint = LayoutPortDirection.Resolve(lookup, label);
            Assert.NotNull(hint);
            Assert.Equal(pin.WidthDbu, hint!.Value.WidthDbu);
        }
    }

    // ══ 3. The committed instance rebuilds somewhere else ═══════════════════

    /// <summary>
    /// A copy of the workspace with its generated-cells cache deleted — which is every clone, since
    /// the folder is git-ignored — rebuilds the cell the committed <c>.clay</c> names, and the
    /// instance ends up pointing at real artwork.
    ///
    /// <para><b>The snapshot deliberately records NO technology identity.</b> That field is an
    /// ABSOLUTE PATH on the machine that placed the cell: committed, it would be somebody's home
    /// directory in a public repository and a path that resolves nowhere else, so every other
    /// machine would silently rebuild against no technology at all. The generators carry their own
    /// fallback for the layer names instead (see the test above), which makes the answer the same
    /// everywhere.</para>
    ///
    /// <para><b>Repointing is allowed here, and that is not a weakened assertion.</b> A generated
    /// cell's folder name hashes the generator's sources AND circuitRF's own shipped PCell Python
    /// package, so editing a comment in <c>tools/pcell-python</c> moves it. Repointing is precisely
    /// the designed response to that; demanding a stable name would turn an ordinary edit to the
    /// package into a failure of an example workspace's test. What must hold is that the rebuild
    /// HAPPENS and the instance resolves afterwards.</para>
    /// </summary>
    [PythonFact]
    public void TheCommittedSpiralRebuildsFromAnEmptyCache_WithoutRepointingAnything()
    {
        string copy = Path.Combine(Path.GetTempPath(), "crf-pdkex-" + Guid.NewGuid().ToString("N")[..12]);
        _scratch.Add(copy);
        CopyDirectory(ExampleRoot(), copy);
        try { Directory.Delete(Path.Combine(copy, GeneratedCellStore.ReservedFolderName), true); }
        catch (DirectoryNotFoundException) { /* already absent, which is the clone's own state */ }

        string clayPath = Path.Combine(copy, "SpiralInductor", "layout", "SpiralInductor.clay");
        string before = File.ReadAllText(clayPath);

        var view = LayoutPersistence.LoadFromFile(clayPath);
        var snapshot = Assert.Single(view.PCellSnapshots).Value;
        Assert.Equal(Spiral, snapshot.GeneratorId);
        Assert.Null(snapshot.TechIdentity);
        Assert.NotEmpty(snapshot.Parameters);
        Assert.Single(view.Instances);

        // Nothing but the two EM port labels: the artwork itself is the INSTANCE's, which is the
        // whole point of the cache being rebuildable. (R-pcal7 M0 put the labels here — a port label
        // inside a placed instance is artwork rather than a port, see TheSpiralsPortLabelsSitOnItsPins.)
        Assert.All(view.Shapes, sh => Assert.True(sh is LabelShape { IsPort: true }));
        Assert.Equal(2, view.Shapes.Count);

        PCellRegistry.ClearResolvers();
        using var resolver = new PCellWorkerResolver(
            copy,
            findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "test", "supplied by the test"),
            report: output.WriteLine);
        PCellRegistry.AddResolver(resolver);

        GeneratedCellsLifecycle.RegenerateAll(copy, _ => null, output.WriteLine);

        // Whatever the rebuild named the cell, the instance now points at it and it is there.
        var rebuilt = LayoutPersistence.LoadFromFile(clayPath);
        string cellDir = RefPath.Resolve(
            Path.GetDirectoryName(clayPath)!, Assert.Single(rebuilt.Instances).CellRef);
        Assert.True(Directory.Exists(cellDir), $"'{cellDir}' was not rebuilt.");

        var cell = LayoutPersistence.LoadFromFile(
            Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));
        Assert.Equal(Spiral, cell.PCellOrigin!.GeneratorId);
        AssertThreeLayerLayout(cell, coil: Metal1, bridge: Metal2);

        // Untouched when nothing moved — the ordinary case, and what says the committed file is the
        // one this build produces rather than one that is silently rewritten on every open.
        if (File.ReadAllText(clayPath) != before)
            output.WriteLine("NOTE: the cell was repointed, so the committed .clay is stale against " +
                             "this build of tools/pcell-python. Re-author it to remove the churn.");
    }

    /// <summary>The generated cell on disk carries the same three layers the generator produced —
    /// asserted against the FILE, because that is what the layout actually draws.</summary>
    private static void AssertThreeLayerLayout(LayoutView cell, LayerKey coil, LayerKey bridge)
    {
        var layers = cell.Shapes.Where(sh => sh is not LabelShape).Select(sh => sh.Layer).ToHashSet();
        Assert.Contains(coil, layers);
        Assert.Contains(bridge, layers);
        Assert.Contains(Via, layers);
        Assert.Equal(2, cell.Pins.Count);
        Assert.All(cell.Pins, p => Assert.Equal(coil, p.Layer));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ══ 4. The testbench beside it models the right substrate ═══════════════

    /// <summary>
    /// The microstrip testbench extracts the GaAs substrate, not the air above it: h = 100 um and
    /// er = 12.9, which is what `TL1`'s explicit <c>SignalLayer = Metal1</c> buys. Left to the
    /// default the resolver picks the topmost conductor — the air bridge — and quietly produces
    /// h = 102.75 um through a mixture of air, nitride and GaAs.
    /// </summary>
    [Fact]
    public void TheMicrostripTestbenchIsReferencedToTheGaAsSubstrate()
    {
        var tech = Tech();
        var overrides = CircuitRF.Design.Schematic.MicrostripSubstrateInjection.BuildOverrides(
            tech, out _, signalLayerNameOverride: "Metal1");

        double H  = double.Parse(overrides.Single(o => o.Name == "H").Expression,  System.Globalization.CultureInfo.InvariantCulture);
        double Er = double.Parse(overrides.Single(o => o.Name == "Er").Expression, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(100e-6, H, 12);
        Assert.Equal(12.9,   Er, 10);

        // …and the schematic actually says so. A correct resolver reached through no parameter is
        // not what the testbench runs.
        string csch = File.ReadAllText(
            Path.Combine(ExampleRoot(), "MicrostripLine", "schematic", "MicrostripLine.csch"));
        Assert.Contains("\"SignalLayer\"", csch, StringComparison.Ordinal);
        Assert.Contains("\"Metal1\"", csch, StringComparison.Ordinal);
    }

    // ══ 5. The kit has a schematic side, because it ships a symbol ═══════════

    /// <summary>
    /// The example ships one <c>.csym</c> per generator, beside its manifest, and that file's NAME
    /// is the whole declaration — nothing lists it. Both are readable, and both declare the two pins
    /// their generator declares, which is what decides the placed component's port count.
    /// </summary>
    [Theory]
    [InlineData(Mlin)]
    [InlineData(Spiral)]
    [InlineData(OSpiral)]
    [InlineData(MimCap)]
    public void TheKitShipsASchematicSymbolForEveryGenerator(string generatorId)
    {
        string kitDir = Path.Combine(ExampleRoot(), "pcell-kit");

        string path = PCellKitSchematicParts.FindSymbolFile(kitDir, generatorId)
            ?? throw new Xunit.Sdk.XunitException(
                $"'{generatorId}' has no shipped symbol under '{kitDir}'.");

        var part = PCellKitSchematicParts.TryBuild("pcell-kit", generatorId, kitDir,
                                                   declaredParameters: null, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(part);
        Assert.Equal(2, part!.Symbol.Pins.Count);
        Assert.Equal(2, part.Ccell.NumPorts);
        output.WriteLine($"{generatorId}: {path} — {part.Symbol.Primitives.Count} primitive(s)");
    }

    /// <summary>
    /// The published parameter interface is the GENERATOR's declaration, not the symbol's — the
    /// one-list rule the PDK authoring reference states. A length default arrives in SI metres and
    /// is written in the mm baseline a placement then rewrites to the workspace's own unit; a count
    /// is left exactly as declared, because circuitRF scales a length and never a count.
    /// </summary>
    [Fact]
    public void TheSchematicPartPublishesTheGeneratorsOwnParameters()
    {
        string workspace = ExampleRoot();
        using var provider = StartKit(workspace);
        var declared = provider.DeclaredParameters(Spiral)
            ?? throw new InvalidOperationException($"'{Spiral}' declared no parameters.");

        var part = PCellKitSchematicParts.TryBuild(
            "pcell-kit", Spiral, Path.Combine(workspace, "pcell-kit"), declared, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(part);

        var names = part!.Ccell.Parameters.Select(p => p.Name).ToList();
        Assert.Equal(["Width", "Space", "Inner", "Turns", "Metal"], names);

        // 10 um, stated by the generator in SI metres, reaches the schematic as 0.01 mm — the same
        // physical width. Writing the SI number verbatim would put 1E-05 in a field with no unit.
        var width = part.Ccell.Parameters.Single(p => p.Name == "Width");
        Assert.Equal(UnitDimension.Length, width.Dimension);
        Assert.Equal("mm", width.Unit);
        Assert.Equal(0.01, double.Parse(width.DefaultExpression, System.Globalization.CultureInfo.InvariantCulture), 12);

        // A count is dimensionless. Scaled as a length it would come out a billion turns.
        var turns = part.Ccell.Parameters.Single(p => p.Name == "Turns");
        Assert.Equal(UnitDimension.None, turns.Dimension);
        Assert.Equal("", turns.Unit);
        Assert.Equal(3.0, double.Parse(turns.DefaultExpression, System.Globalization.CultureInfo.InvariantCulture), 12);

        // The dropdown the generator declares survives onto the schematic side as a closed set.
        var metal = part.Ccell.Parameters.Single(p => p.Name == "Metal");
        Assert.Equal(["Metal1", "Metal2"], metal.Choices);
        Assert.Equal("Metal1", metal.DefaultExpression);

        // Every one of them is ANNOTATED. A parametric cell is its parameters — a spiral is three
        // turns of 10 um metal — and a sheet that hides all five makes the reader click each part to
        // find out what the design is.
        Assert.All(part.Ccell.Parameters, p => Assert.True(p.ShowOnSchematic, $"'{p.Name}' is hidden"));
    }

    /// <summary>
    /// <b>The reported bug, end to end.</b> Open <c>SpiralInductor.clay</c>, run Update Schematic
    /// from Layout, and a component appears — with the coil's own parameters on it, in the
    /// technology's own unit.
    ///
    /// <para>Before the kit had a schematic side this placed NOTHING: the generator matched no part,
    /// so <c>PdkKitRegistry.Find</c> answered null and the run reported that the kit was not loaded.
    /// The schematic was created and left empty, which is exactly what was reported.</para>
    /// </summary>
    [Fact]
    public void UpdateSchematicFromLayout_PlacesTheCoil_WithItsParameters()
    {
        string copy = Path.Combine(Path.GetTempPath(), "crf-pcell-sch-" + Guid.NewGuid().ToString("N")[..8]);
        _scratch.Add(copy);
        CopyDirectory(ExampleRoot(), copy);
        try { Directory.Delete(Path.Combine(copy, GeneratedCellStore.ReservedFolderName), true); }
        catch (DirectoryNotFoundException) { /* already absent, which is a fresh clone's own state */ }

        PdkKitRegistry.ResetAllForTests();
        KitLayoutGenerators.ResetAllForTests();
        PCellRegistry.ClearResolvers();
        using var resolver = new PCellWorkerResolver(
            copy,
            findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "test", "supplied by the test"),
            report: output.WriteLine);
        PCellRegistry.AddResolver(resolver);
        GeneratedCellsLifecycle.RegenerateAll(copy, _ => null, output.WriteLine);

        // What the workspace does on open: mount the schematic side of every parametric cell whose
        // kit ships a symbol, then publish the palette so the generator and the part are one tile.
        var kitNames = resolver.KitNameByGeneratorId;
        var kitDirs  = resolver.KitDirectoryByGeneratorId;
        var parts    = new List<PdkKitPart>();
        var tiles    = new List<PaletteItem>();
        foreach (var (gid, kitName) in kitNames)
        {
            var built = PCellKitSchematicParts.TryBuild(
                kitName, gid, kitDirs.GetValueOrDefault(gid), resolver.DeclaredParameters(gid), out _);
            if (built is null) continue;
            parts.Add(built);
            tiles.Add(PCellKitSchematicParts.PaletteItemFor(kitName, built));
        }
        Assert.Equal(4, parts.Count);

        string kit = kitNames[Spiral];
        PdkKitRegistry.SetPCellParts(copy, kit, parts);
        KitLayoutGenerators.Publish(copy, KitPaletteMerge.Compose(
            tiles, kitNames.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)));

        var layout = LayoutPersistence.LoadFromFile(
            Path.Combine(copy, "SpiralInductor", "layout", "SpiralInductor.clay"));
        string layoutDir = Path.Combine(copy, "SpiralInductor", "layout");

        var schematic = new SchematicEditModel
        {
            SchematicDirectory = Path.Combine(copy, "SpiralInductor", "schematic"),
        };
        Directory.CreateDirectory(schematic.SchematicDirectory);

        var result = LayoutToSchematicGenerator.Run(layout, schematic, layoutDir, Tech());
        foreach (var l in result.Lines) output.WriteLine($"{l.Severity}: {l.Text}");

        Assert.Equal(1, result.CreatedCount);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        var placed = Assert.Single(schematic.Components);
        Assert.Equal(PdkKitRegistry.RefFor(kit, Spiral), placed.CellRef);

        // The symbol the kit ships is what this instance renders as — two pins, not a blank box.
        var symbol = CellSymbolResolver.Resolve(placed.CellRef!, schematic.SchematicDirectory);
        Assert.Equal(CellSymbolState.Resolved, symbol.State);
        Assert.Equal(2, symbol.Symbol!.Pins.Count);

        // …and the parameters are the coil's own, in the technology's display unit. The .clay holds
        // Width = 1E-05 m; this workspace displays lengths in micrometres, so the schematic says 10.
        Assert.Equal(["Width", "Space", "Inner", "Turns", "Metal"],
                     placed.Parameters.Select(p => p.Name).ToList());
        var width = placed.Parameters.Single(p => p.Name == "Width");
        Assert.Equal("\u00b5m", width.Unit);   // the MMIC technology's own display unit
        Assert.Equal(10.0, double.Parse(width.Expression, System.Globalization.CultureInfo.InvariantCulture), 9);
        Assert.Equal("3", placed.Parameters.Single(p => p.Name == "Turns").Expression);
        Assert.Equal("Metal1", placed.Parameters.Single(p => p.Name == "Metal").Expression);
        Assert.All(placed.Parameters, p => Assert.True(p.ShowOnSchematic, $"'{p.Name}' is hidden"));
    }

    // ══ 6. The octagonal coil and the MIM capacitor ══════════════════════════

    /// <summary>
    /// <b>The octagonal coil is the square one with its corners cut — one cell body, two walks.</b>
    ///
    /// <para>Every side of it runs at a multiple of forty-five degrees, and four of the eight are
    /// diagonal; the square coil has none. Asserted on the MERGED outline rather than on the pieces
    /// that drew it, because a diagonal run is emitted as a four-cornered polygon and the whole
    /// question is whether those polygons union into one conductor with clean edges instead of a
    /// notched one — a mitre that does not quite close leaves a sliver that renders invisibly and
    /// exports.</para>
    /// </summary>
    [PythonFact]
    public void TheOctagonalCoilRunsAtFortyFiveDegrees_AndTheSquareOneNever()
    {
        using var kit = StartKit(ExampleRoot());
        var tech = Tech();

        PolygonShape Coil(string id)
        {
            Assert.True(kit.TryGetGenerator(id, out var generate));
            return generate(kit.DeclaredDefaults(id)!, tech, PCellLayerSelection.Default)
                   .Shapes.OfType<PolygonShape>().Where(g => g.Layer == Metal1)
                   .MaxBy(g => Math.Abs(SignedArea(g)))!;
        }

        static List<double> Bearings(PolygonShape coil)
        {
            var bearings = new List<double>();
            int n = coil.Xy.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double deg = Math.Atan2(coil.Xy[j * 2 + 1] - coil.Xy[i * 2 + 1],
                                        coil.Xy[j * 2]     - coil.Xy[i * 2]) * 180 / Math.PI;
                bearings.Add((deg + 360) % 180);      // an edge and its reverse are one direction
            }
            return bearings;
        }

        static double ShortestEdge(PolygonShape coil)
        {
            double shortest = double.MaxValue;
            int n = coil.Xy.Length / 2;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                shortest = Math.Min(shortest, Math.Sqrt(
                    Math.Pow(coil.Xy[j * 2]     - coil.Xy[i * 2],     2) +
                    Math.Pow(coil.Xy[j * 2 + 1] - coil.Xy[i * 2 + 1], 2)));
            }
            return shortest;
        }

        var square = Bearings(Coil(Spiral));
        Assert.All(square, b => Assert.True(Math.Abs(b % 90) < 0.01,
            $"the square coil has an edge at {b:0.###}°, which is neither horizontal nor vertical."));

        var octagonCoil = Coil(OSpiral);
        var octagon = Bearings(octagonCoil);
        Assert.All(octagon, b => Assert.True(Math.Abs(b % 45) < 0.05,
            $"the octagonal coil has an edge at {b:0.###}°, which is not a multiple of 45°."));
        Assert.True(octagon.Count(b => Math.Abs(b % 90) > 0.05) >= octagon.Count / 3,
            "the octagonal coil came back with hardly any diagonal edges — it is a square spiral.");

        // …and the joints closed: no sliver left behind by a mitre that did not quite meet. A notch
        // a database unit across renders invisibly, survives the union and exports.
        double shortest = ShortestEdge(octagonCoil);
        Assert.True(shortest > 1000,
            $"the octagonal outline has a {shortest:0.##} DBU edge — that is a mitre artefact, not a side.");

        output.WriteLine($"square {square.Count} edges; octagon {octagon.Count} edges, " +
                         $"shortest {shortest:N0} DBU");
    }

    /// <summary>
    /// <b>The octagon holds its turn-to-turn gap at exactly <c>Space</c>, and so does the square.</b>
    ///
    /// <para>Its sides are placed by their own perpendicular distance from the centre, so side
    /// <i>k</i> and side <i>k+8</i> are parallel and one pitch apart by construction. Walking corner
    /// to corner along rays from the centre is the obvious alternative and looks identical on
    /// screen: the two ends of a side then sit at slightly different radii, adjacent turns are not
    /// quite parallel, and the gap drifts across the side — which a min-spacing check finds and a
    /// person does not.</para>
    ///
    /// <para>Measured by scanning a horizontal line through the middle of the merged winding and
    /// reading off the bands of metal it crosses. Both coils have vertical sides due east and west
    /// of centre, so that line meets them square and the gaps between bands are the turn spacing
    /// itself — no reconstruction of the centre line, and nothing shared with the generator.</para>
    /// </summary>
    [PythonTheory]
    [InlineData(Spiral)]
    [InlineData(OSpiral)]
    public void EveryTurnIsSpacedExactlyItsSpaceFromTheNext(string generatorId)
    {
        using var kit = StartKit(ExampleRoot());
        var defaults = kit.DeclaredDefaults(generatorId)!;
        long space = PCellUnits.MetresToDbu(defaults["Space"].AsReal(), Dbu);
        long width = PCellUnits.MetresToDbu(defaults["Width"].AsReal(), Dbu);

        Assert.True(kit.TryGetGenerator(generatorId, out var generate));
        var coil = generate(defaults, Tech(), PCellLayerSelection.Default)
                   .Shapes.OfType<PolygonShape>().Where(g => g.Layer == Metal1)
                   .MaxBy(g => Math.Abs(SignedArea(g)))!;

        int n = coil.Xy.Length / 2;
        var ys = Enumerable.Range(0, n).Select(i => coil.Xy[i * 2 + 1]).ToList();
        // Off the exact centre by one unit, so the scan cannot land on a horizontal edge.
        long scanY = (ys.Min() + ys.Max()) / 2 + 1;

        var crossings = new List<double>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            long y1 = coil.Xy[i * 2 + 1], y2 = coil.Xy[j * 2 + 1];
            if ((y1 > scanY) == (y2 > scanY)) continue;           // no crossing, horizontals included
            long x1 = coil.Xy[i * 2], x2 = coil.Xy[j * 2];
            crossings.Add(x1 + (x2 - x1) * (double)(scanY - y1) / (y2 - y1));
        }
        crossings.Sort();
        Assert.True(crossings.Count >= 6 && crossings.Count % 2 == 0,
            $"the scan line met {crossings.Count} edges, which is not a set of metal bands.");

        // Alternating band, gap, band, gap … across the coil. Every band is one turn wide.
        for (int i = 0; i + 1 < crossings.Count; i += 2)
            Assert.Equal(width, crossings[i + 1] - crossings[i], 0);

        // Every gap between two of them is the declared spacing — except the one in the MIDDLE,
        // which is the coil's own opening and is a different thing entirely. It comes out wider than
        // `Inner` on both shapes, because `Inner` is measured across the innermost side and the turn
        // has to step outward somewhere: once per lap, on whichever axis it steps.
        var gaps = new List<double>();
        for (int i = 1; i + 1 < crossings.Count; i += 2)
            gaps.Add(crossings[i + 1] - crossings[i]);
        Assert.True(gaps.Count % 2 == 1, $"{gaps.Count} gaps is an even number — there is no middle.");

        int opening = gaps.Count / 2;
        for (int i = 0; i < gaps.Count; i++)
            if (i != opening)
                Assert.Equal(space, gaps[i], 0);

        long inner = PCellUnits.MetresToDbu(defaults["Inner"].AsReal(), Dbu);
        Assert.True(gaps[opening] >= inner,
            $"the opening measures {gaps[opening]:N0} DBU against a declared Inner of {inner:N0}.");

        output.WriteLine($"{generatorId}: {crossings.Count / 2} bands of {width} DBU on {space} DBU " +
                         $"gaps, around a {gaps[opening]:N0} DBU opening");
    }

    /// <summary>
    /// <b>The MIM capacitor is a three-storey cell, and the insulator between two of those storeys
    /// is drawn ONCE — as a mask.</b>
    ///
    /// <para>Metal1 is the bottom plate; <c>MIM Metal</c> is the top plate 0.2 µm above it. The
    /// insulator appears twice in the technology and only one of the two is drawable, which is the
    /// part worth getting right: <c>Nitride</c> is the MASK the process streams out, and a
    /// <c>.gds</c> written without it is not manufacturable and gives a DRC deck nothing to check;
    /// <c>MIM Dielectric</c> is the stackup BAND the field solver reads, it carries no drawing layer
    /// at all, and it is declared <c>PresentWithLayer: Nitride</c> — so drawing the mask is what
    /// puts the band in a run (MIM-11). A generator that also drew a shape for the band would stack
    /// a second insulator under the first, and the artwork would look exactly the same.</para>
    ///
    /// <para>The escape is the spiral's crossover one storey higher, and it is not a stylistic
    /// choice: <c>MIM Via</c> spans MIM Metal to Metal2 and nothing in this stackup spans MIM Metal
    /// to Metal1, so the top plate can only leave upwards. It comes back down through an ordinary
    /// Metal1–Metal2 post, which is what puts both terminals on one layer.</para>
    /// </summary>
    [PythonFact]
    public void TheSeriesMimCapIsMetal1AndMimMetal_WithTheNitrideDrawnAsAMask()
    {
        var nitride  = new LayerKey(6, 0);
        var mimMetal = new LayerKey(9, 0);
        var mimVia   = new LayerKey(10, 0);

        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(MimCap, out var generate));
        var result = generate(kit.DeclaredDefaults(MimCap)!, Tech(), PCellLayerSelection.Default);

        var layers = result.Shapes.Select(sh => sh.Layer).ToHashSet();
        Assert.Equal([Metal1, Metal2, Via, nitride, mimMetal, mimVia],
                     layers.OrderBy(k => k.Layer).ToArray());

        // MIM-11 — the mask ENCLOSES the top plate, which is the rule that matters: a plate whose
        // edge ran past the nitride would sit straight on the bottom plate and short it.
        var mask = Assert.Single(result.Shapes.OfType<RectShape>(), r => r.Layer == nitride);
        var plate = Assert.Single(result.Shapes.OfType<RectShape>(), r => r.Layer == mimMetal);
        Assert.True(Math.Min(mask.X1, mask.X2) <= Math.Min(plate.X1, plate.X2));
        Assert.True(Math.Max(mask.X1, mask.X2) >= Math.Max(plate.X1, plate.X2));
        Assert.True(Math.Min(mask.Y1, mask.Y2) <= Math.Min(plate.Y1, plate.Y2));
        Assert.True(Math.Max(mask.Y1, mask.Y2) >= Math.Max(plate.Y1, plate.Y2));

        // The bottom plate ENCLOSES the top plate — the enclosure is a process rule, not a
        // parameter, which is why W x L is declared as the top plate and the bottom one is grown.
        var bottom = result.Shapes.OfType<RectShape>().Where(r => r.Layer == Metal1)
                                  .MaxBy(r => Math.Abs((r.X2 - r.X1) * (r.Y2 - r.Y1)))!;
        var top    = plate;
        Assert.True(Math.Min(bottom.X1, bottom.X2) < Math.Min(top.X1, top.X2));
        Assert.True(Math.Max(bottom.X2, bottom.X1) > Math.Max(top.X2, top.X1));
        Assert.True(Math.Min(bottom.Y1, bottom.Y2) < Math.Min(top.Y1, top.Y2));
        Assert.True(Math.Max(bottom.Y2, bottom.Y1) > Math.Max(top.Y2, top.Y1));

        // Both terminals land on Metal1, so the cell abuts the same things at either end.
        Assert.Equal(2, result.Pins.Count);
        Assert.All(result.Pins, p => Assert.Equal(Metal1, p.Layer));
    }

    /// <summary>
    /// <b>The capacitance is a READOUT, and it tracks the plate.</b>
    ///
    /// <para><c>C</c> is declared as an output — circuitRF renders it as text rather than an edit
    /// box, because typing into it cannot do anything — and the generator reports what it derived
    /// it to on every run. Without that report the parameter list can only show the number the
    /// instance was stored with, while the geometry that determines it moves underneath.</para>
    ///
    /// <para>Checked against the parallel-plate value written out here from the technology's own
    /// stackup rather than asked of the generator: an oracle that shares the code under test proves
    /// nothing. Doubling the plate area doubles it, which is the property that says the report is
    /// being recomputed rather than echoed.</para>
    /// </summary>
    [PythonFact]
    public void TheMimCapReportsACapacitanceThatFollowsItsPlate()
    {
        const double Eps0 = 8.8541878128e-12;
        var tech = Tech();
        var mim = tech.Stackup.Layers.Single(l => l.Name == "MIM Dielectric");

        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(MimCap, out var generate));
        var defaults = kit.DeclaredDefaults(MimCap)!;

        Assert.True(kit.DeclaredParameters(MimCap)!.Single(d => d.Name == "C").Computed,
            "'C' is not declared as an output, so the Properties Inspector offers an edit box that " +
            "cannot do anything.");

        double Reported(double wMetres, double lMetres)
        {
            var p = new Dictionary<string, PCellValue>(defaults)
            {
                ["W"] = PCellValue.Real(wMetres),
                ["L"] = PCellValue.Real(lMetres),
            };
            var r = generate(p, tech, PCellLayerSelection.Default);
            return r.ComputedValues!["C"].AsReal();
        }

        // 60 um square of MIM dielectric, er 6.8 over 0.2 um: about 1.08 pF on this process.
        double areaM2 = 60e-6 * 60e-6;
        double expectedPf = Eps0 * mim.Epsr * areaM2 / (mim.ThicknessDbu / (double)Dbu * 1e-6) * 1e12;
        Assert.Equal(expectedPf, Reported(60e-6, 60e-6), 3);

        // Twice the plate, twice the capacitance — it is recomputed, not echoed.
        Assert.Equal(2 * expectedPf, Reported(60e-6, 120e-6), 3);
    }

    /// <summary>
    /// <b>A shunt capacitor grounds a plate itself, and it is the BOTTOM plate.</b>
    ///
    /// <para>Nothing above Metal1 can reach the metal on the back of the wafer — <c>Backside Via</c>
    /// spans Metal1 to Backside Metal and the MIM top plate is two storeys above that — so the
    /// grounded terminal has to be the plate lying on the substrate. That is also the plate you
    /// want grounded: it is the one facing 100 µm of εr 12.9, and grounding it shorts out a
    /// plate-to-backside capacitance that would otherwise hang off the signal node. A cell that
    /// grounded the top plate instead still works, still draws, and carries a parasitic nobody
    /// declared.</para>
    ///
    /// <para>So the signal terminal is the top plate's, and pin 1 is at the cell origin — which is
    /// the other end of the cell from where the series part's pin 1 is. The shunt cell is therefore
    /// the series cell reflected, and both assertions below are really the same one: the ground via
    /// is on Metal1, at the far end, and the signal is at the origin.</para>
    /// </summary>
    [PythonFact]
    public void TheShuntCapacitorGroundsItsBottomPlateThroughTheWafer()
    {
        var backsideVia = new LayerKey(8, 0);

        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(MimCap, out var generate));
        var tech = Tech();

        PCellResult Generate(string connection)
        {
            var p = new Dictionary<string, PCellValue>(kit.DeclaredDefaults(MimCap)!)
            {
                ["Connection"] = PCellValue.Text(connection),
            };
            return generate(p, tech, PCellLayerSelection.Default);
        }

        // The series part reaches no ground at all — it is two signal terminals and nothing else.
        var series = Generate("Series");
        Assert.DoesNotContain(series.Shapes, sh => sh.Layer == backsideVia);

        var shunt = Generate("Shunt");
        var hole = Assert.Single(shunt.Shapes.OfType<RectShape>(), r => r.Layer == backsideVia);

        // The via spans Metal1 to the backside, so it has to sit on Metal1 — and inside it, because
        // a drilled hole with no pad round it is an open circuit that draws.
        var onMetal1 = shunt.Shapes.Where(sh => sh.Layer == Metal1).ToList();
        Assert.Contains(onMetal1, sh => Covers(sh, hole));

        // Both terminals still land on Metal1, and pin 1 — the signal — is still at the origin.
        Assert.Equal(2, shunt.Pins.Count);
        Assert.All(shunt.Pins, p => Assert.Equal(Metal1, p.Layer));
        Assert.Equal(0, shunt.Pins[0].X);
        Assert.Equal(0, shunt.Pins[0].Y);

        // …and pin 2, the ground, is the far end of the cell, on the via's own pad.
        Assert.True(shunt.Pins[1].X > Math.Max(hole.X1, hole.X2));

        // The plates did not move, only what they face: the same capacitance either way.
        Assert.Equal(series.ComputedValues!["C"].AsReal(), shunt.ComputedValues!["C"].AsReal(), 9);

        output.WriteLine($"shunt: {shunt.Shapes.Count} shapes, ground via " +
                         $"{Math.Abs(hole.X2 - hole.X1)} x {Math.Abs(hole.Y2 - hole.Y1)} DBU");
    }

    /// <summary>Whether <paramref name="outer"/> contains every corner of <paramref name="inner"/>.
    /// A merged region is a polygon, so this takes either.</summary>
    private static bool Covers(LayoutShape outer, RectShape inner)
    {
        var ring = outer switch
        {
            RectShape r    => new[] { (r.X1, r.Y1), (r.X2, r.Y1), (r.X2, r.Y2), (r.X1, r.Y2) },
            PolygonShape p => Enumerable.Range(0, p.Xy.Length / 2)
                                        .Select(i => (p.Xy[i * 2], p.Xy[i * 2 + 1])).ToArray(),
            _ => [],
        };
        if (ring.Length < 3) return false;

        foreach (var (x, y) in new[] { (inner.X1, inner.Y1), (inner.X2, inner.Y1),
                                       (inner.X2, inner.Y2), (inner.X1, inner.Y2) })
        {
            bool inside = false;
            for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                if (ring[i].Item2 > y != ring[j].Item2 > y &&
                    x < (double)(ring[j].Item1 - ring[i].Item1) * (y - ring[i].Item2)
                        / (ring[j].Item2 - ring[i].Item2) + ring[i].Item1)
                    inside = !inside;
            if (!inside) return false;
        }
        return true;
    }

    /// <summary>
    /// <b>The shunt cell is the series cell reflected — the same plates, read back the other way.</b>
    ///
    /// <para>One layout routine draws both, so the two must agree everywhere the reflection does not
    /// change anything: the same layers, the same plate size, the same enclosure, the same
    /// capacitance. What differs is the ground via and which end each terminal is on.</para>
    ///
    /// <para>And the grips have to survive it. They are stated in the frame the cell ENDED UP in
    /// rather than the one it was drawn in, which is the part that is easy to get wrong: a handle
    /// mirrored along with the artwork names an anchor that moves when the parameter changes, and
    /// the drag solver then measures a sensitivity that is not the one the user is dragging.</para>
    /// </summary>
    [PythonTheory]
    [InlineData("Series")]
    [InlineData("Shunt")]
    public void EitherConnectionDrawsTheSamePlates_AndBothGripsStillWork(string connection)
    {
        var mimMetal = new LayerKey(9, 0);

        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(MimCap, out var generate));
        var tech = Tech();
        var parameters = new Dictionary<string, PCellValue>(kit.DeclaredDefaults(MimCap)!)
        {
            ["Connection"] = PCellValue.Text(connection),
        };

        PCellResult Generate(IReadOnlyDictionary<string, PCellValue> p)
            => generate(p, tech, PCellLayerSelection.Default);

        var result = Generate(parameters);
        var top = Assert.Single(result.Shapes.OfType<RectShape>(), r => r.Layer == mimMetal);
        long w = PCellUnits.MetresToDbu(parameters["W"].AsReal(), Dbu);
        long l = PCellUnits.MetresToDbu(parameters["L"].AsReal(), Dbu);
        Assert.Equal(l, Math.Abs(top.X2 - top.X1));
        Assert.Equal(w, Math.Abs(top.Y2 - top.Y1));

        // Both grips move the parameter they name, in the direction they claim, in this frame.
        var handles = result.Handles!;
        Assert.Equal(2, handles.Count);
        for (int i = 0; i < handles.Count; i++)
        {
            Assert.True(PCellHandleSolver.MeasureSensitivity(
                    Generate, parameters, handles[i], i, out double valuePerProjection, out _),
                $"'{handles[i].Parameter}' is undraggable in {connection}: the grip does not move " +
                "along the axis it declares when the parameter changes.");

            var solved = PCellHandleSolver.Solve(
                Generate, parameters, handles[i], i, targetProjection: -10_000_000, valuePerProjection);
            Assert.True(solved.Ok);
            Assert.True(solved.Value.AsReal() >= handles[i].Min!.Value - 1e-15);
        }
    }

    /// <summary>
    /// <b>A coil's own ORIGIN is the middle of its opening — there is no metal there at all.</b>
    ///
    /// <para>The cell re-centres on its winding, so a freshly placed instance sits with its centre
    /// on the layout origin and its nearest metal tens of micrometres away. That is the right place
    /// for it, and it is also why Update Layout from Schematic reported "1 added" while the owner saw
    /// an empty canvas (2026-09-15): an empty MMIC layout is framed on about one micrometre, and one
    /// micrometre at the middle of a coil is the hole. Measured here rather than reasoned about,
    /// because it is the fact that turns "I did not see it" into a framing bug rather than a
    /// generation one — see <c>EmptyLayoutIsFramedOnWhatIsWrittenTests</c> for the fix.</para>
    /// </summary>
    [PythonTheory]
    [InlineData(Spiral)]
    [InlineData(OSpiral)]
    public void TheCoilsOriginIsInsideItsOwnOpening(string generatorId)
    {
        using var kit = StartKit(ExampleRoot());
        Assert.True(kit.TryGetGenerator(generatorId, out var generate));
        var result = generate(kit.DeclaredDefaults(generatorId)!, Tech(), PCellLayerSelection.Default);

        var origin = new RectShape { Layer = Metal1, X1 = 0, Y1 = 0, X2 = 0, Y2 = 0 };
        Assert.DoesNotContain(result.Shapes, sh => Covers(sh, origin));

        // …and the cell itself is two orders of magnitude bigger than the window that framed nothing.
        long width = result.Shapes.Max(RightmostX) - result.Shapes.Min(LeftmostX);
        Assert.True(width > 200 * Dbu, $"{generatorId} measures {width} DBU across — too small to be this cell.");
        output.WriteLine($"{generatorId}: {width / (double)Dbu:N0} µm across, nothing at its origin");
    }

    private static long LeftmostX(LayoutShape shape) => shape switch
    {
        RectShape r    => Math.Min(r.X1, r.X2),
        PolygonShape p => Enumerable.Range(0, p.Xy.Length / 2).Min(i => p.Xy[i * 2]),
        _              => long.MaxValue,
    };

    /// <summary>
    /// <b>OWNER REPORT (2026-09-15): three of this kit's cells placed on one schematic, and after
    /// Update Layout from Schematic only one of them was visible in the layout.</b>
    ///
    /// <para>All three were written and all three resolved. They were at x = 0, <b>10 mm</b> and
    /// <b>20 mm</b> — the generator's placement pitch was a fixed 10 mm, chosen when the parts this
    /// command placed were board-scale microstrip. These cells measure 84 and 250 µm, so it scattered
    /// the design across twenty millimetres of empty wafer, forty cell-widths between neighbours, and
    /// a view framed on the first one contains none of the others.</para>
    ///
    /// <para>A fixed pitch cannot be right for a tool that spans four orders of magnitude of part
    /// size, so the pitch is MEASURED from the cells being placed. Asserted here as a relation to the
    /// cells themselves — neighbours clear of each other, and the whole row inside a millimetre —
    /// rather than against a number, because a number is the thing that just went stale.</para>
    /// </summary>
    [PythonFact]
    public void UpdateLayoutFromSchematic_PlacesEveryPartWhereTheOthersCanBeSeen()
    {
        string copy = Path.Combine(Path.GetTempPath(), "crf-s2l-" + Guid.NewGuid().ToString("N")[..8]);
        _scratch.Add(copy);
        CopyDirectory(ExampleRoot(), copy);
        try { Directory.Delete(Path.Combine(copy, GeneratedCellStore.ReservedFolderName), true); }
        catch (DirectoryNotFoundException) { /* a fresh clone's own state */ }

        PdkKitRegistry.ResetAllForTests();
        KitLayoutGenerators.ResetAllForTests();
        PCellRegistry.ClearResolvers();
        using var resolver = new PCellWorkerResolver(
            copy,
            findInterpreter: (_, _) => new PythonInterpreter(PythonRunner.Interpreter!, [], "test", "supplied by the test"),
            report: output.WriteLine);
        PCellRegistry.AddResolver(resolver);
        GeneratedCellsLifecycle.RegenerateAll(copy, _ => null, output.WriteLine);

        var kitNames = resolver.KitNameByGeneratorId;
        var kitDirs  = resolver.KitDirectoryByGeneratorId;
        var parts = new List<PdkKitPart>();
        var tiles = new List<PaletteItem>();
        foreach (var (gid, kitName) in kitNames)
        {
            var built = PCellKitSchematicParts.TryBuild(
                kitName, gid, kitDirs.GetValueOrDefault(gid), resolver.DeclaredParameters(gid), out _);
            if (built is null) continue;
            parts.Add(built);
            tiles.Add(PCellKitSchematicParts.PaletteItemFor(kitName, built));
        }
        string kit = kitNames[Spiral];
        PdkKitRegistry.SetPCellParts(copy, kit, parts);
        KitLayoutGenerators.Publish(copy, KitPaletteMerge.Compose(
            tiles, kitNames.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)));

        // The reported sheet: one of each coil and the capacitor, seeded exactly as placing them does.
        string schDir = Path.Combine(copy, "SpiralInductor", "schematic");
        string layDir = Path.Combine(copy, "SpiralInductor", "layout");
        Directory.CreateDirectory(schDir);
        Directory.CreateDirectory(layDir);
        var schematic = new SchematicEditModel { SchematicDirectory = schDir };
        int n = 0;
        foreach (string gid in new[] { Spiral, OSpiral, MimCap })
        {
            string kitRef = PdkKitRegistry.RefFor(kit, gid);
            var comp = new EditableComponent
            {
                InstanceName = "X" + ++n, Symbol = SymbolKind.Generic, CellRef = kitRef,
            };
            foreach (var cp in CellSymbolResolver.ResolveCcell(kitRef, schDir)!.Parameters)
                comp.Parameters.Add(new EditableParameter
                {
                    Name = cp.Name, Expression = cp.DefaultExpression, Unit = cp.Unit,
                    Dimension = cp.Dimension, ShowOnSchematic = cp.ShowOnSchematic,
                });
            schematic.Components.Add(comp);
        }

        var tech = Tech();
        var layout = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = tech.DefaultSnapDbu };
        var result = SchematicToLayoutGenerator.Run(schematic, layout, schDir, copy, layDir, tech, null, null);

        foreach (var w in result.NoLayoutWarnings) output.WriteLine($"NO-LAYOUT: {w}");
        Assert.Empty(result.NoLayoutWarnings);
        Assert.Equal(3, result.AddedCount);
        result.Command!.Execute();
        Assert.Equal(3, layout.Instances.Count);

        var boxes = layout.Instances
            .Select(i => (i.SchematicId, Box: CellHierarchy.InstanceBbox(i, layDir)))
            .OrderBy(b => b.Box.MinX).ToList();
        foreach (var (id, box) in boxes)
            output.WriteLine($"{id}: x {box.MinX / (double)Dbu:N1}…{box.MaxX / (double)Dbu:N1} µm");

        long widest = boxes.Max(b => Math.Max(b.Box.MaxX - b.Box.MinX, b.Box.MaxY - b.Box.MinY));

        // Neighbours are clear of each other — the pitch is not so tight that the parts overlap…
        for (int i = 1; i < boxes.Count; i++)
            Assert.True(boxes[i].Box.MinX > boxes[i - 1].Box.MaxX,
                $"'{boxes[i].SchematicId}' overlaps '{boxes[i - 1].SchematicId}'.");

        // …and not so loose that finding one tells you nothing about where the others are. Three
        // parts of which the largest is a quarter of a millimetre belong within a few of its own
        // widths, not within forty.
        long span = boxes.Max(b => b.Box.MaxX) - boxes.Min(b => b.Box.MinX);
        Assert.True(span < 6 * widest,
            $"the three parts span {span / (double)Dbu:N0} µm — {span / (double)widest:N0} times the " +
            "widest of them. That is the fixed-pitch defect, back again.");

        // And the command is told WHERE they went, so it can bring them on screen. An instance is the
        // one thing a user cannot find by looking: it lands where this puts it, not where they clicked.
        Assert.False(result.AddedRegion.IsEmpty);
        Assert.True(result.AddedRegion.MinX <= boxes[0].Box.MinX);
        Assert.True(result.AddedRegion.MaxX >= boxes[^1].Box.MaxX);
        output.WriteLine($"added region {(result.AddedRegion.MaxX - result.AddedRegion.MinX) / (double)Dbu:N0} µm wide");
    }

    /// <summary>
    /// The two halves of a kit are replaced on different occasions and must not replace each other:
    /// a kit re-import rebuilds its own parts while its interpreters keep running, and a kit's
    /// scripts are re-read while its imported parts sit untouched. A part the kit ITSELF ships wins,
    /// because the kit has stated what that part is.
    /// </summary>
    [Fact]
    public void AnImportedPartWins_AndNeitherHalfDiscardsTheOther()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-pcell-mount-" + Guid.NewGuid().ToString("N")[..8]);
        _scratch.Add(root);
        Directory.CreateDirectory(root);

        PdkKitRegistry.ResetAllForTests();
        string kitDir = Path.Combine(ExampleRoot(), "pcell-kit");

        var spiral = PCellKitSchematicParts.TryBuild("k", Spiral, kitDir, null, out _)!;
        var mlin   = PCellKitSchematicParts.TryBuild("k", Mlin,   kitDir, null, out _)!;
        PdkKitRegistry.SetPCellParts(root, "k", [spiral, mlin]);
        Assert.True(PdkKitRegistry.HasKit(root, "k"));
        Assert.NotNull(PdkKitRegistry.Find(PdkKitRegistry.RefFor("k", Spiral), root));

        // The kit's own import lands, naming one of the same cells. Its part replaces the
        // synthesised one; the other synthesised part is untouched.
        var imported = new PdkKitPart(
            Spiral,
            new CircuitRF.Design.Symbol.Symbol([], [new CircuitRF.Design.Symbol.SymbolPin(0, 0, 0, "p")], 1),
            new CcellFile { NumPorts = 1 }, IconPath: null);
        PdkKitRegistry.SetKit(root, "k", [imported]);

        Assert.Single(PdkKitRegistry.Find(PdkKitRegistry.RefFor("k", Spiral), root)!.Symbol.Pins);
        Assert.Equal(2, PdkKitRegistry.Find(PdkKitRegistry.RefFor("k", Mlin),   root)!.Symbol.Pins.Count);

        // …and a later re-reading of the kit's scripts does not take the imported part back off.
        PdkKitRegistry.SetPCellParts(root, "k", [spiral, mlin]);
        Assert.Single(PdkKitRegistry.Find(PdkKitRegistry.RefFor("k", Spiral), root)!.Symbol.Pins);
        Assert.Equal(2, PdkKitRegistry.Find(PdkKitRegistry.RefFor("k", Mlin),   root)!.Symbol.Pins.Count);

        PdkKitRegistry.ResetAllForTests();
    }

    // ══ 5. The spiral demonstrates EM on a PCell ════════════════════════════

    /// <summary>
    /// <b>R-pcal7 M0 — the shipped spiral carries port labels and an EM setup, and the setup
    /// includes BOTH metal levels.</b>
    ///
    /// <para>The example carried the coil and nothing that simulated it, so the one PCell workspace
    /// circuitRF ships did not demonstrate the thing a PCell coil is for. Both halves of what was
    /// added are gated, and the second is not a formality: the coil's inner terminal escapes on
    /// Metal2 through two via posts, so a run whose analysis levels are Metal1 alone drops the
    /// underpass and the two ports are not connected at all. It publishes |S21| = 4e-4 — a clean,
    /// smooth, perfectly passive OPEN CIRCUIT — with one note among thirty saying two via shapes
    /// were ignored. Measured, on this very file.</para>
    ///
    /// <para><b>THE SETUP MUST NAME NO LEVELS AT ALL, and that is the whole point of this test.</b>
    /// The first fix for the open circuit above was to PIN the list — <c>SignalStackupLayerName</c>
    /// plus <c>AnalysisLevelNames: ["Metal1", "Metal2"]</c> — which is correct for the artwork as
    /// shipped and wrong the moment anybody adds to it. A user copied this example, dropped a
    /// <c>KIT_MIMCAP</c> into the layout to make an L+C resonator, and ran it: the capacitor's top
    /// plate is on <c>MIM Metal</c>, which the pinned list does not name, so the plate, the
    /// <c>MIM Via</c> that reaches it and the patterned film between the plates were all dropped and
    /// the published answer was a flat 5.9 fF open across the band — the SAME failure this pin was
    /// added to fix, one level up. Reported 2026-09-16, and it had caught them twice in two days.
    /// </para>
    ///
    /// <para>With neither key present, <c>PlanarExtractor</c>'s own default governs — every signal
    /// conductor that carries artwork — which yields these two levels here and picks up a third by
    /// itself when a capacitor is added. <b>Both keys have to go:</b> removing
    /// <c>AnalysisLevelNames</c> alone falls through to <c>SignalStackupLayerName</c>
    /// (<c>PlanarExtractor</c>'s middle arm), which names ONE conductor and reinstates the original
    /// open circuit. That is also why the assertion below is on the EXTRACTION rather than on the
    /// file: what matters is the level set a run gets, not which keys are absent.</para>
    ///
    /// <para>The port labels are on the TOP cell rather than inside the generated one because
    /// <c>EmPortExtraction</c> reads the view's own shapes and not the flattened ones. A label
    /// inside a placed instance is artwork, not a port, and the run refuses with "this layout has
    /// no port labels".</para>
    /// </summary>
    [Fact]
    public void TheSpiralCarriesAnEmSetup_OverBothMetalLevels()
    {
        string cem = Path.Combine(ExampleRoot(), "SpiralInductor", "em", "SpiralInductor.cem");
        Assert.True(File.Exists(cem), $"'{cem}' is missing: the example demonstrates no EM run.");

        var setup = EmSetupPersistence.LoadFromFile(cem);
        Assert.Equal("SpiralInductor/layout/SpiralInductor.clay", setup.LayoutRef);

        // Neither key, so nothing pins the level set and the extractor's own default governs.
        // Empty or absent — PlanarExtractor tests both keys with `is { Length: > 0 }`, so that is
        // the condition the gate has to state rather than `null` specifically.
        Assert.True(string.IsNullOrEmpty(setup.SignalStackupLayerName),
                    $"the setup still pins a signal conductor: '{setup.SignalStackupLayerName}'");
        Assert.Empty(setup.AnalysisLevelNames);

        // The via posts the two levels exist for.
        string clay = Path.Combine(ExampleRoot(), "SpiralInductor", "layout", "SpiralInductor.clay");
        var view     = LayoutPersistence.LoadFromFile(clay);
        var instance = Assert.Single(view.Instances);
        string cellDir = RefPath.Resolve(Path.GetDirectoryName(clay)!, instance.CellRef);
        var cell = LayoutPersistence.LoadFromFile(
            Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));
        Assert.Contains(cell.Shapes, sh => sh.Layer == Via);
        Assert.Contains(cell.Shapes, sh => sh.Layer == Metal2);

        // ── The gate: what the run actually meshes, derived rather than declared ───────────────
        var geometry = EmGeometry.Flatten(view, clay);
        var r = PlanarExtractor.Extract(
            geometry.Shapes, Tech(), view.DbuPerMicron, 8e9, setup.ToExtractionSettings());
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(["Metal1", "Metal2"], r.Problem!.Layers.Select(l => l.Name));

        // And nothing was dropped on the way — the underpass is IN, which is the thing the flat
        // open circuit was the absence of.
        Assert.Empty(r.Warnings);
        Assert.NotEmpty(r.Problem!.ViaList);
    }

    /// <summary>
    /// <b>The two port labels are on the TOP layout, at the generated cell's own pins.</b> A port
    /// half a line width off the metal takes its width from the instance's bounding box rather than
    /// from the pin — the defect the test above this one exists for — so the anchors are asserted
    /// against the pins rather than against the numbers that happen to be in the file.
    /// </summary>
    [Fact]
    public void TheSpiralsPortLabelsSitOnItsPins()
    {
        string clay = Path.Combine(ExampleRoot(), "SpiralInductor", "layout", "SpiralInductor.clay");
        var view = LayoutPersistence.LoadFromFile(clay);

        var labels = view.Shapes.OfType<LabelShape>().Where(l => l.IsPort).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Equal(["1", "2"], labels.Select(l => l.Text).Order());
        Assert.All(labels, l => Assert.Equal(Metal1, l.Layer));

        var instance = Assert.Single(view.Instances);
        Assert.Equal(0, instance.X);
        Assert.Equal(0, instance.Y);
        string cellDir = RefPath.Resolve(Path.GetDirectoryName(clay)!, instance.CellRef);
        var cell = LayoutPersistence.LoadFromFile(
            Path.Combine(cellDir, "layout", Path.GetFileName(cellDir) + ".clay"));

        foreach (var pin in cell.Pins)
        {
            var label = Assert.Single(labels, l => l.Text == pin.Name);
            Assert.Equal(pin.X, label.X);
            Assert.Equal(pin.Y, label.Y);
        }
    }
}
