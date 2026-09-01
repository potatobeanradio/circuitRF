using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Cells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for importing a SPICE <c>.subckt</c> as a cell.
///
/// <para><b>The oracle is extraction, not the drawing.</b> Asserting that a wire runs from here to
/// there tests the router's taste; what has to be true is that reading the built schematic BACK
/// through <see cref="NetExtractor"/> — the same path a run takes — yields the netlist the file
/// wrote. That is what <see cref="AssertSameCircuit"/> does, up to net renaming, which is the only
/// freedom an importer legitimately has. It is also the only check that can catch the failure that
/// matters: circuitRF reads connectivity off the geometry, so a wire drawn a grid square out does
/// not look wrong, it JOINS two nets, and the cell then simulates as a different circuit.</para>
/// </summary>
public class SubcircuitImportTests : IDisposable
{
    private readonly string _root;

    public SubcircuitImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-subckt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string Write(string fileName, string text)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    private static SubcircuitTranslation Only(string text)
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        Assert.Single(all);
        return all[0];
    }

    private static SubcircuitTranslation Named(string text, string name)
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        return all.Single(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static SchematicEditModel Build(SubcircuitTranslation t, string cellName = "Part")
        => SubcircuitCellBuilder.BuildSchematic(t, cellName, n => "../../" + n, new List<string>());

    /// <summary>
    /// The built schematic read back the way a run reads it: components, wires and pins resolved to
    /// nets by geometry alone.
    /// </summary>
    private static NetExtractor.ExtractionResult Extract(SchematicEditModel model)
        => NetExtractor.Extract(model, "tb");

    /// <summary>
    /// Asserts that what extraction found IS the circuit the <c>.subckt</c> wrote — every element
    /// present exactly once, every terminal on the right net, and every port bound.
    ///
    /// <para>Net NAMES are not compared, because an importer is entitled to rename a net and
    /// circuitRF auto-names the ones no label claims. What is compared is the PARTITION: the map
    /// from the file's net names to the extracted ones must be a bijection, which is exactly the
    /// statement "the same terminals are shorted together, and no others".</para>
    /// </summary>
    private static void AssertSameCircuit(SubcircuitTranslation t, NetExtractor.ExtractionResult r)
    {
        var expected = t.Definition.Instances.ToDictionary(i => i.InstanceName, StringComparer.Ordinal);

        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.Ordinal),
            r.TestBench.Instances.Select(i => i.InstanceName).OrderBy(k => k, StringComparer.Ordinal));

        var fileToDrawn = new Dictionary<string, string>(StringComparer.Ordinal);
        var drawnToFile = new Dictionary<string, string>(StringComparer.Ordinal);

        void Bind(string fileNet, string drawnNet, string where)
        {
            // SPICE's ground and circuitRF's are both "0", and it is the one net whose name is a
            // fact rather than a choice — so it is pinned rather than merely mapped consistently.
            if (fileNet == "0" || drawnNet == "0")
            {
                Assert.True(fileNet == "0" && drawnNet == "0",
                    $"{where}: '{fileNet}' and '{drawnNet}' — ground was drawn as an ordinary net, "
                    + "or an ordinary net was drawn as ground.");
                return;
            }

            if (fileToDrawn.TryGetValue(fileNet, out var already))
                Assert.True(already == drawnNet,
                    $"{where}: net '{fileNet}' is '{already}' elsewhere and '{drawnNet}' here — "
                    + "the drawing split one net in two.");
            else
                fileToDrawn[fileNet] = drawnNet;

            if (drawnToFile.TryGetValue(drawnNet, out var back))
                Assert.True(back == fileNet,
                    $"{where}: drawn net '{drawnNet}' carries both '{back}' and '{fileNet}' — "
                    + "the drawing shorted two nets together.");
            else
                drawnToFile[drawnNet] = fileNet;
        }

        foreach (var drawn in r.TestBench.Instances)
        {
            var file = expected[drawn.InstanceName];
            Assert.Equal(file.NetBindings.Count, drawn.NetBindings.Count);
            for (int k = 0; k < file.NetBindings.Count; k++)
                Bind(file.NetBindings[k], drawn.NetBindings[k], $"{drawn.InstanceName} terminal {k}");
        }

        Assert.Equal(t.Definition.Ports, r.CellPorts);
    }

    /// <summary>A parameter row's value in base SI units — expression and unit token together, which
    /// is what the elaborator reads. The expression string alone cannot tell a correct import from
    /// one still sitting in the registry's picofarads.</summary>
    private static double Si(EditableComponent c, string name)
    {
        var p = c.Parameters.Single(q => q.Name == name);
        string? unit = p.Unit.Length > 0 ? UnitNormalizer.ToEngineUnit(p.Unit) : null;
        return new Evaluator().Eval(p.Expression, new Scope("import"), unit).AsReal();
    }

    private static EditableComponent Component(SchematicEditModel m, string name)
        => m.Components.Single(c => c.InstanceName == name);

    // ─────────────────────────────────────────────────────────────────────────
    //  The circuit survives the round trip
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The headline claim: a definition drawn by this importer extracts back as itself. A pi network
    /// is the smallest thing with a net that three terminals share, which is where a router that
    /// merely draws near things starts producing a different circuit.
    /// </summary>
    [Fact]
    public void APiNetwork_DrawnAndReadBack_IsTheSameCircuit()
    {
        var t = Only("""
            .subckt PI in out
            C1 in 0 1p
            L1 in out 2n
            C2 out 0 1p
            R1 in out 50
            .ends
            """);

        Assert.True(t.IsSupported, t.Refusal);
        AssertSameCircuit(t, Extract(Build(t)));
    }

    /// <summary>
    /// A definition with a transistor in it, so the three-terminal port order — which the file
    /// states as drain/gate/source and the symbol draws top/left/bottom — is exercised rather than
    /// assumed. A silent transposition here is a working amplifier wired as a follower.
    /// </summary>
    [Fact]
    public void ACommonSourceStage_DrawnAndReadBack_IsTheSameCircuit()
    {
        var t = Only("""
            .model NFET NJF (VTO=-2 BETA=1e-3 LAMBDA=0.02)
            .subckt STAGE gate drain vdd
            J1 drain gate src NFET
            R1 src 0 100
            R2 drain vdd 1k
            C1 gate 0 1p
            .ends
            """);

        Assert.True(t.IsSupported, t.Refusal);
        var model = Build(t);
        AssertSameCircuit(t, Extract(model));

        // The JFET landed as a JFET, not as the nearest thing that compiles.
        Assert.Equal(SymbolKind.JfetN, Component(model, "J1").Symbol);
    }

    /// <summary>
    /// Every net in one definition, several of them shared by three or more terminals, plus a
    /// component in the middle of the sheet the router has to get around. This is the case a naive
    /// straight-line router draws through a device body.
    /// </summary>
    [Fact]
    public void ADenseDefinition_WithSharedNetsAndComponentsInTheWay_IsStillTheSameCircuit()
    {
        var t = Only("""
            .subckt LADDER a b
            R1 a n1 10
            R2 n1 n2 20
            R3 n2 n3 30
            R4 n3 b  40
            C1 n1 0 1p
            C2 n2 0 2p
            C3 n3 0 3p
            L1 a n2 1n
            L2 n1 n3 2n
            L3 n2 b  3n
            .ends
            """);

        Assert.True(t.IsSupported, t.Refusal);
        var model = Build(t);
        AssertSameCircuit(t, Extract(model));

        // And it is genuinely WIRED. The net-label fallback makes any circuit extract correctly, so
        // without this the round-trip assertions above would pass on a schematic with no wires in it
        // at all — which is exactly the outcome the owner asked for the router to avoid.
        Assert.Empty(model.NetLabels);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The drawing itself
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>No wire is drawn over a component.</b> The owner asked for this directly, and it is not
    /// only cosmetic — a wire crossing a glyph is unreadable, and one that ENDS or BENDS on another
    /// component's terminal is a connection nobody drew.
    /// </summary>
    [Fact]
    public void NoWireIsDrawnAcrossAComponentBody()
    {
        var t = Only("""
            .subckt MESH a b c
            R1 a n1 10
            R2 n1 b 20
            R3 n1 c 30
            C1 a b 1p
            C2 b c 2p
            L1 a c 1n
            .ends
            """);

        var model = Build(t);

        // Every grid point a wire actually occupies, endpoints included.
        var occupied = new HashSet<(double, double)>();
        foreach (var w in model.Wires)
            for (int i = 0; i < w.Points.Count - 1; i++)
            {
                var (ax, ay) = w.Points[i];
                var (bx, by) = w.Points[i + 1];
                for (double x = Math.Min(ax, bx); x <= Math.Max(ax, bx) + 0.5; x += 100)
                    for (double y = Math.Min(ay, by); y <= Math.Max(ay, by) + 0.5; y += 100)
                        occupied.Add((x, y));
            }

        foreach (var c in model.Components)
        {
            var ports = SymbolPortDefs.For(c.Symbol);
            var terminals = new HashSet<(double, double)>(
                Enumerable.Range(0, ports.Length).Select(c.GetPortWorldCoord));

            // The body: everything strictly inside the glyph's own span, which is the box its ports
            // bound. A terminal is where a wire is SUPPOSED to land, so it is not part of it.
            double minX = Math.Min(0, ports.Length == 0 ? 0 : ports.Min(p => p.LocalX));
            double maxX = Math.Max(0, ports.Length == 0 ? 0 : ports.Max(p => p.LocalX));
            double minY = Math.Min(0, ports.Length == 0 ? 0 : ports.Min(p => p.LocalY));
            double maxY = Math.Max(0, ports.Length == 0 ? 0 : ports.Max(p => p.LocalY));

            for (double lx = minX; lx <= maxX + 0.5; lx += 100)
                for (double ly = minY; ly <= maxY + 0.5; ly += 100)
                {
                    var w = SchematicGeometry.LocalToWorld(
                        (float)lx, (float)ly, c.X, c.Y, c.Rotation, c.MirrorX);
                    if (terminals.Contains(w)) continue;
                    Assert.False(occupied.Contains(w),
                        $"a wire runs through {c.Symbol} '{c.InstanceName}' at {w}.");
                }
        }
    }

    /// <summary>
    /// The drawing sits on the connection grid. A point off it does not connect at all, so this is
    /// the difference between a schematic and a picture of one.
    /// </summary>
    [Fact]
    public void EveryWireVertexAndEveryComponent_SitsOnTheConnectionGrid()
    {
        var model = Build(Only("""
            .subckt G a b
            R1 a n1 10
            C1 n1 b 1p
            L1 n1 0 1n
            .ends
            """));

        foreach (var w in model.Wires)
            foreach (var (x, y) in w.Points)
            {
                Assert.Equal(0.0, x % 100, 6);
                Assert.Equal(0.0, y % 100, 6);
            }

        foreach (var c in model.Components)
        {
            Assert.Equal(0.0, c.X % 100, 6);
            Assert.Equal(0.0, c.Y % 100, 6);
        }
    }

    /// <summary>
    /// Net <c>0</c> becomes ground symbols, not a rail. Two things are asserted because only the
    /// pair is meaningful: there is one ground per terminal on it, and extraction still calls that
    /// net <c>0</c>.
    /// </summary>
    [Fact]
    public void EveryTerminalOnNetZero_GetsItsOwnGroundSymbol()
    {
        var t = Only("""
            .subckt SHUNT a b
            C1 a 0 1p
            C2 b 0 2p
            R1 a b 50
            .ends
            """);

        var model = Build(t);

        Assert.Equal(2, model.Components.Count(c => c.Symbol == SymbolKind.Ground));
        AssertSameCircuit(t, Extract(model));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ports and the symbol
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ports are the deliverable: a cell with no pins resolves to zero ports and cannot be
    /// placed in another schematic at all. Their ORDER is the contract — pin k of the symbol binds
    /// to the k-th name on the <c>.subckt</c> line.
    /// </summary>
    [Fact]
    public void OnePinPerDeclaredPort_NumberedAndNamedInTheOrderTheSubcktLineDeclaresThem()
    {
        var model = Build(Only("""
            .subckt THREE alpha beta gamma
            R1 alpha beta  10
            R2 beta  gamma 20
            .ends
            """));

        var pins = model.Components
            .Where(c => c.Symbol == SymbolKind.Pin)
            .OrderBy(c => int.Parse(c.Parameters.Single(p => p.Name == "Num").Expression))
            .ToList();

        Assert.Equal(3, pins.Count);
        Assert.Equal(
            ["alpha", "beta", "gamma"],
            pins.Select(p => p.Parameters.Single(q => q.Name == "Name").Expression));
    }

    /// <summary>
    /// The symbol is the SnP/auto-generated generic box, reused rather than reinvented — so a
    /// subcircuit's symbol has as many pins as it has ports, numbered, on the grid, exactly as an
    /// N-port component's does.
    /// </summary>
    [Fact]
    public void TheSymbolIsTheGenericBox_WithOnePinPerPort()
    {
        var t = Only("""
            .subckt FOURPORT a b c d
            R1 a b 10
            R2 c d 20
            .ends
            """);

        var result = SubcircuitCellBuilder.Write(_root, "FOURPORT", t, [t]);
        string symbol = Directory.GetFiles(
            Path.Combine(result.CellDir, CellFolder.SymbolSubFolder), "*.csym").Single();

        var sym = SymbolPersistence.LoadFromFile(symbol);
        Assert.Equal(4, sym.Pins.Count);
        Assert.Equal([0, 1, 2, 3], sym.Pins.Select(p => p.PortIndex).OrderBy(i => i));
        Assert.All(sym.Pins, p => Assert.Equal(0.0, p.LocalX % 100, 6));
        Assert.All(sym.Pins, p => Assert.Equal(0.0, p.LocalY % 100, 6));

        var ccell = CellPersistence.LoadFromFile(Path.Combine(result.CellDir, CellFolder.CcellFileName));
        Assert.Equal(4, ccell.NumPorts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Values
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The unit trap, one level down from the card importer's.</b> The registry declares a
    /// capacitor's <c>C</c> in PICOFARADS and an inductor's <c>L</c> in NANOHENRIES; a netlist writes
    /// <c>1p</c>, which the reader has already turned into <c>1e-12</c> farads. Writing that into a
    /// row that still says "pF" is a capacitance a trillion times too small, and it simulates.
    /// </summary>
    [Fact]
    public void EveryValueLandsInBaseSiUnits_NotTheRegistrysConvenienceUnit()
    {
        var model = Build(Only("""
            .subckt VALUES a b
            R1 a n1 2k
            C1 n1 b 1p
            L1 n1 0 4n
            .ends
            """));

        Assert.Equal(2000.0,  Si(Component(model, "R1"), "R"), 1e-9);
        Assert.Equal(1e-12,   Si(Component(model, "C1"), "C"), 1e-24);
        Assert.Equal(4e-9,    Si(Component(model, "L1"), "L"), 1e-21);
    }

    /// <summary>
    /// A model card reached through a subcircuit and the same card imported on its own must produce
    /// the same device with the same numbers — they go through one translation, and this is what
    /// says so rather than assuming it.
    /// </summary>
    [Fact]
    public void ACardReachedThroughASubcircuit_CarriesTheSameParametersAsTheCardImportedAlone()
    {
        const string card = ".model DMOD D (IS=2.5e-14 RS=0.8 CJO=2e-12 BV=75)";

        var direct = ModelCardCellBuilder.BuildSchematic(
            SpiceModelCardTranslation.Translate(SpiceNetlistReader.Read(card).ModelCards[0]), "DMOD");

        var viaSub = Build(Only($"""
            {card}
            .subckt WRAP a b
            D1 a b DMOD
            .ends
            """));

        var alone  = direct.Components.Single(c => c.Symbol == SymbolKind.Diode);
        var nested = Component(viaSub, "D1");

        foreach (string name in new[] { "Is", "Rs", "Cj0", "Bv" })
            Assert.Equal(Si(alone, name), Si(nested, name), Math.Abs(Si(alone, name)) * 1e-12);
    }

    /// <summary>
    /// An element line's own words win over the card's. Both are statements about the same
    /// parameter — the card giving the default, the line giving this one — and reading them the
    /// other way round makes every per-instance area silently inert.
    /// </summary>
    [Fact]
    public void AnInstanceParameter_WinsOverTheCardsOwnValueForTheSameName()
    {
        var model = Build(Only("""
            .model DMOD D (IS=1e-14 AREA=1)
            .subckt WRAP a b
            D1 a b DMOD AREA=4
            .ends
            """));

        Assert.Equal(4.0, Si(Component(model, "D1"), "Area"), 1e-9);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Refusals
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>One unreadable element refuses the whole definition.</b> A netlist with a line missing is
    /// not a smaller circuit, it is a DIFFERENT one — and a different one that elaborates, simulates
    /// and produces numbers nobody can tell from the right ones.
    /// </summary>
    [Fact]
    public void ADefinitionHoldingALineTheReaderCouldNotUse_IsRefusedWhole()
    {
        var t = Only("""
            .subckt HASVSOURCE a b
            R1 a n1 10
            V1 n1 b DC 5
            .ends
            """);

        Assert.False(t.IsSupported);
        Assert.Contains("could not read", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An element naming a model the file never defines is refused BY NAME — the user needs
    /// to know which model is missing, not that "something" was.</summary>
    [Fact]
    public void AnElementNamingAModelTheFileDoesNotDefine_IsRefusedByName()
    {
        var t = Only("""
            .subckt WRAP a b
            D1 a b NOSUCHMODEL
            .ends
            """);

        Assert.False(t.IsSupported);
        Assert.Contains("NOSUCHMODEL", t.Refusal!, StringComparison.Ordinal);
        Assert.Contains("D1", t.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A card circuitRF has no model for is refused through the subcircuit exactly as it is on its
    /// own, and the refusal carries the CARD's reason rather than a generic one — that sentence is
    /// what tells the user whether the fix is a different file or a feature.
    /// </summary>
    [Fact]
    public void AnElementNamingACardCircuitRfCannotBuild_CarriesTheCardsOwnRefusal()
    {
        var t = Only("""
            .model MYMOSFET NMOS (LEVEL=49 VTH0=0.4)
            .subckt WRAP d g s
            M1 d g s s MYMOSFET
            .ends
            """);

        Assert.False(t.IsSupported);
        Assert.Contains("M1", t.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Terminal counts are never reconciled by dropping or inventing a net. A four-net bipolar line
    /// — the fourth is the substrate — meets a three-terminal component, and tying the substrate
    /// somewhere plausible would be a different circuit that solves.
    /// </summary>
    [Fact]
    public void AnElementBindingMoreNetsThanTheComponentHasTerminals_IsRefused()
    {
        var t = Only("""
            .model QMOD NPN (IS=1e-16 BF=100)
            .subckt WRAP c b e sub
            Q1 c b e sub QMOD
            .ends
            """);

        Assert.False(t.IsSupported);
        Assert.Contains("Q1", t.Refusal!, StringComparison.Ordinal);
        Assert.Contains("terminal", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A definition with no ports could not be placed anywhere, so building it would create
    /// a cell that cannot be used.</summary>
    [Fact]
    public void ADefinitionThatDeclaresNoPorts_IsRefused()
    {
        var t = Only("""
            .subckt LOOSE
            R1 a b 10
            .ends
            """);

        Assert.False(t.IsSupported);
        Assert.Contains("port", t.Refusal!, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Nesting
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A definition that calls another becomes two cells, the inner one referenced by the outer.
    /// There is nowhere else a nested definition can live — a circuitRF cell instance references a
    /// cell FOLDER — so this is the shape or nothing.
    /// </summary>
    [Fact]
    public void ADefinitionCallingAnother_CreatesACellForEachAndReferencesIt()
    {
        const string text = """
            .subckt INNER p q
            R1 p q 10
            .ends
            .subckt OUTER a b
            X1 a n1 INNER
            X2 n1 b INNER
            C1 n1 0 1p
            .ends
            """;

        var all   = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        var outer = all.Single(t => t.Name == "OUTER");

        Assert.True(outer.IsSupported, outer.Refusal);
        Assert.Equal(["INNER"], outer.Dependencies);

        var result = SubcircuitCellBuilder.Write(_root, "OUTER", outer, all);

        Assert.Single(result.AlsoCreated);
        Assert.True(Directory.Exists(Path.Combine(_root, "INNER")));

        var schematic = SchematicPersistence.LoadFromFile(result.SchematicPath).model;
        foreach (string name in new[] { "X1", "X2" })
            Assert.Equal("../../INNER", schematic.Components.Single(c => c.InstanceName == name).CellRef);
    }

    /// <summary>
    /// A call whose net count disagrees with the definition's port count is refused. Binding pin k
    /// to port k is the whole of what a call means, so there is nothing to fall back on.
    /// </summary>
    [Fact]
    public void ACallBindingTheWrongNumberOfNets_IsRefused()
    {
        var outer = Named("""
            .subckt INNER p q
            R1 p q 10
            .ends
            .subckt OUTER a b c
            X1 a b c INNER
            .ends
            """, "OUTER");

        Assert.False(outer.IsSupported);
        Assert.Contains("X1", outer.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A definition whose child cannot be built cannot be built either — and says which child, and
    /// why. A parent reported as broken with no reason sends the user to the wrong file.
    /// </summary>
    [Fact]
    public void ADefinitionWhoseChildIsRefused_IsRefusedAndNamesTheChild()
    {
        var outer = Named("""
            .subckt INNER p q
            D1 p q MISSINGMODEL
            .ends
            .subckt OUTER a b
            X1 a b INNER
            .ends
            """, "OUTER");

        Assert.False(outer.IsSupported);
        Assert.Contains("INNER", outer.Refusal!, StringComparison.Ordinal);
    }

    /// <summary>Two definitions that call each other cannot both be cells; cutting the loop would
    /// build a hierarchy that terminates and is not the file's.</summary>
    [Fact]
    public void MutuallyRecursiveDefinitions_AreRefusedRatherThanCut()
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read("""
            .subckt A p q
            X1 p q B
            .ends
            .subckt B p q
            X1 p q A
            .ends
            """));

        Assert.All(all, t => Assert.False(t.IsSupported));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Writing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All-or-nothing across EVERY folder, not merely the one asked for: a half-written nested
    /// import leaves a parent cell pointing at a child that is not there, which the workspace
    /// scanner lists and a user places.
    /// </summary>
    [Fact]
    public void AnExistingChildCellFolder_RefusesTheWholeImportAndLeavesNothingBehind()
    {
        const string text = """
            .subckt INNER p q
            R1 p q 10
            .ends
            .subckt OUTER a b
            X1 a b INNER
            .ends
            """;

        var all   = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        var outer = all.Single(t => t.Name == "OUTER");

        Directory.CreateDirectory(Path.Combine(_root, "INNER"));

        Assert.Throws<IOException>(() => SubcircuitCellBuilder.Write(_root, "OUTER", outer, all));
        Assert.False(Directory.Exists(Path.Combine(_root, "OUTER")));
    }

    /// <summary>The <c>.subckt</c> line's own parameter defaults become the cell's published
    /// interface — which is what an instance is seeded from and what a caller's overrides bind
    /// against. Without them a parameterised subcircuit imports as one frozen at its defaults.</summary>
    [Fact]
    public void TheSubcktLinesParameters_BecomeTheCellsPublishedInterface()
    {
        var t = Only("""
            .subckt SCALED a b RVAL=100 CVAL=1p
            R1 a b {RVAL}
            C1 a b {CVAL}
            .ends
            """);

        var result = SubcircuitCellBuilder.Write(_root, "SCALED", t, [t]);
        var ccell  = CellPersistence.LoadFromFile(Path.Combine(result.CellDir, CellFolder.CcellFileName));

        Assert.Equal(["RVAL", "CVAL"], ccell.Parameters.Select(p => p.Name));
        Assert.Equal("100", ccell.Parameters[0].DefaultExpression);
    }

    /// <summary>
    /// The report is never just "created". A card parameter circuitRF has no home for is absent from
    /// the built cell and discoverable in no other way than an answer that is wrong by an amount
    /// nobody can attribute.
    /// </summary>
    [Fact]
    public void TheReport_NamesEveryCardParameterThatWasNotCarried()
    {
        var t = Only("""
            .model DMOD D (IS=1e-14 KF=1e-20 AF=1.0)
            .subckt WRAP a b
            D1 a b DMOD
            .ends
            """);

        var report = SubcircuitCellBuilder.Write(_root, "WRAP", t, [t]).Report;

        Assert.Contains(report, l => l.Contains("KF", StringComparison.Ordinal)
                                  && l.Contains("not carried", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The two doors
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One scan lists cards and subcircuits together. A supplier's file routinely holds both — the
    /// subcircuit that is the part, and the cards that are its transistors — and asking the user to
    /// classify the file before they can see what is in it is the thing this avoids.
    /// </summary>
    [Fact]
    public void OneScan_OffersBothTheCardsAndTheSubcircuitsInAFile()
    {
        string path = Write("part.subckt", """
            .model DMOD D (IS=1e-14)
            .subckt PART a b
            D1 a b DMOD
            R1 a b 1k
            .ends
            """);

        var scan = SpiceCellImport.Scan(path);

        Assert.Null(scan.Error);
        Assert.Contains(scan.Candidates, c => c.Name == "PART" && c.TypeLabel == ".SUBCKT");
        Assert.Contains(scan.Candidates, c => c.Name == "DMOD" && c.TypeLabel == ".D");
        Assert.Equal(2, scan.Supported.Count);
    }

    /// <summary>
    /// The project tree offers the command on a subcircuit file as well as a card file — the owner
    /// asked for one gesture, not two.
    ///
    /// <para>The list is every extension that names a SPICE deck and nothing else. <c>.sp</c> in
    /// particular is at least as common a spelling for a file holding a <c>.subckt</c> as
    /// <c>.subckt</c> itself.</para>
    /// </summary>
    [Theory]
    [InlineData("kit.model")]
    [InlineData("kit.mod")]
    [InlineData("part.subckt")]
    [InlineData("part.sub")]
    [InlineData("part.ckt")]
    [InlineData("part.sp")]
    [InlineData("part.spi")]
    [InlineData("part.cir")]
    public void TheProjectTreeOffersTheCommand_OnCardAndSubcircuitFilesAlike(string fileName)
        => Assert.True(ModelCardCellBuilder.IsSpiceCellFile(fileName));

    /// <summary>
    /// …and NOT on extensions that only sometimes hold one. The tree's item appears on a bookmarked
    /// file with nothing having read it, so a net cast this wide would put a dead menu item on most
    /// of a workspace — <c>.lib</c> is a static library everywhere outside this dialect, and
    /// <c>.txt</c> is anything at all. Both are still reachable through File ▸ Import, where the
    /// user has already said what the file is by choosing it.
    /// </summary>
    [Theory]
    [InlineData("kit.lib")]
    [InlineData("notes.txt")]
    [InlineData("design.csch")]
    public void TheProjectTreeDoesNotOfferIt_OnExtensionsThatOnlySometimesHoldOne(string fileName)
        => Assert.False(ModelCardCellBuilder.IsSpiceCellFile(fileName));

    /// <summary>Both menus say what they now do. The two are separate XAML trees — a native menu bar
    /// and an in-window one — so a rename that reaches only one leaves the other lying.</summary>
    [Fact]
    public void BothImportMenus_OfferModelOrSubcircuit()
    {
        string axaml = File.ReadAllText(RepoFile("src/Ui/Views/WorkspaceWindow.axaml"));

        Assert.Contains("<NativeMenuItem Header=\"Model or Subcircuit…\"", axaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"_Model or Subcircuit…\"", axaml, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The router's own contract
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the router gives up, the net-label fallback has to reach the WHOLE net — including the
    /// terminal it started from. That seed is the one terminal that is never a routing target, so a
    /// fallback that only labels the failures leaves the labelled terminals as one net and anything
    /// that did get wired as a second: a different circuit, and one that elaborates.
    ///
    /// <para>Driven through the router directly, with one terminal walled in, because no netlist
    /// this importer produces can be relied on to fail — and a test that depends on a routing
    /// failure it cannot force would quietly stop testing anything.</para>
    /// </summary>
    [Fact]
    public void WhenARouteCannotBeFound_TheFallbackNamesEveryTerminalOfThatNet_SeedIncluded()
    {
        // A ring of keep-out one cell thick around (2000, 2000): nothing can get in or out.
        var wall = new List<(double X, double Y)>();
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
                if (i != 0 || j != 0) wall.Add((2000 + i * 100, 2000 + j * 100));

        var result = SchematicAutoRouter.Route(
        [
            new SchematicAutoRouter.Block([(0, 0, "n")], []),
            new SchematicAutoRouter.Block([(2000, 2000, "n")], wall),
        ]);

        Assert.Empty(result.Wires);
        Assert.Equal(
            [(0.0, 0.0), (2000.0, 2000.0)],
            result.Unrouted.Select(u => (u.X, u.Y)).OrderBy(p => p.X));
        Assert.All(result.Unrouted, u => Assert.Equal("n", u.Net));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  End to end, through the files on disk
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole gesture, through the files: import, reload the written <c>.csch</c>, extract. Every
    /// earlier round-trip test works on the in-memory model, so this is what says the wires, pins and
    /// values all survive being written down and read back.
    /// </summary>
    [Fact]
    public void ImportedThenReloadedFromDisk_IsStillTheSameCircuit()
    {
        var t = Only("""
            .subckt TEE a b c
            R1 a n1 10
            R2 n1 b 20
            C1 n1 c 1p
            L1 n1 0 1n
            .ends
            """);

        var result = SubcircuitCellBuilder.Write(_root, "TEE", t, [t]);

        var (reloaded, _, _) = SchematicPersistence.LoadFromFile(result.SchematicPath);
        reloaded.SchematicDirectory = Path.GetDirectoryName(result.SchematicPath);

        AssertSameCircuit(t, NetExtractor.Extract(reloaded, "tb", new DiskCells()));
    }

    /// <summary>
    /// The nested case end to end: the outer cell's schematic, read from disk with the inner cell
    /// resolved off disk beside it, is the hierarchy the file wrote — the inner definition appearing
    /// once in the library, with the outer's calls bound to its ports in order.
    /// </summary>
    [Fact]
    public void ANestedImport_ReloadedFromDisk_ResolvesTheChildAndBindsItsPortsInOrder()
    {
        const string text = """
            .subckt INNER p q
            R1 p q 10
            .ends
            .subckt OUTER a b
            X1 a n1 INNER
            X2 n1 b INNER
            .ends
            """;

        var all   = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        var outer = all.Single(t => t.Name == "OUTER");
        var result = SubcircuitCellBuilder.Write(_root, "OUTER", outer, all);

        var (reloaded, _, _) = SchematicPersistence.LoadFromFile(result.SchematicPath);
        reloaded.SchematicDirectory = Path.GetDirectoryName(result.SchematicPath);

        var extraction = NetExtractor.Extract(reloaded, "tb", new DiskCells());

        AssertSameCircuit(outer, extraction);

        var inner = Assert.Single(extraction.Library.Cells);
        Assert.Equal("INNER", inner.Name);
        Assert.Equal(["p", "q"], inner.Ports);
        Assert.Equal("R", Assert.Single(inner.Instances).Reference);
    }

    /// <summary>
    /// The disk-backed resolver the extractor needs, composed exactly as
    /// <c>WorkspaceViewModel.Resolve</c> composes it. The production one is a view model that needs
    /// a live Avalonia application, which these tests deliberately never build.
    /// </summary>
    private sealed class DiskCells : ICellResolver
    {
        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containing)
        {
            if (HierarchyResolver.ResolvePrimaryPath(cellInstance, containing) is not { } primary)
                return null;

            var (model, _, _) = SchematicPersistence.LoadFromFile(primary);
            model.SchematicDirectory = Path.GetDirectoryName(primary);

            string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(primary))!;
            return new CellResolution(Path.GetFileName(cellDir), model, []);
        }
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
}
