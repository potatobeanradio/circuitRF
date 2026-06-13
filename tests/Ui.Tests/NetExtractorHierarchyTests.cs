using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Hierarchical extraction gate: cell instances, Library building, recursion, dedupe,
/// cycle detection, and port-count mismatch.
/// </summary>
public class NetExtractorHierarchyTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Pin(int num, double cx, double cy)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static EditableComponent Resistor(string name, double cx, double cy)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = cx, Y = cy };

    /// <summary>
    /// Build a minimal 2-port sub-cell schematic: Pin1 at (0,200), R at (0,400), Pin2 at (0,600).
    /// Pin1.port → (0,0); R.port0 → (0,200); R.port1 → (0,600); Pin2.port → (0,400).
    /// Wires connect Pin1↔R.port0 and R.port1↔Pin2.
    /// </summary>
    private static SchematicEditModel TwoPortSubCell(string resistorName = "R1")
    {
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, 0, 200));   // port at (0,0)
        sub.Components.Add(Resistor(resistorName, 0, 400));
        sub.Components.Add(Pin(2, 0, 600));   // port at (0,400)
        sub.Wires.Add(Wire((0, 0), (0, 200)));   // Pin1.port → R.port0
        sub.Wires.Add(Wire((0, 400), (0, 600))); // R.port1 → Pin2.port
        return sub;
    }

    /// <summary>
    /// A stub resolver backed by a dictionary keyed on comp.CellRef.
    /// </summary>
    private sealed class StubResolver : ICellResolver
    {
        private readonly Dictionary<string, CellResolution> _map;
        public StubResolver(Dictionary<string, CellResolution> map) => _map = map;

        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel _)
            => comp.CellRef is not null && _map.TryGetValue(comp.CellRef, out var r) ? r : null;
    }

    private static StubResolver Resolver(params (string cellRef, CellResolution res)[] entries)
        => new(entries.ToDictionary(e => e.cellRef, e => e.res));

    // ── Test 1: flat regression ───────────────────────────────────────────────

    [Fact]
    public void FlatSchematic_NoLibraryCells_InstancesUnchanged()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Resistor("R2", 0, 600));
        model.Wires.Add(Wire((0, 400), (0, 400))); // isolate the two

        var resolver = Resolver(); // empty resolver — no cell defs
        var result = NetExtractor.Extract(model, "tb", resolver);

        // Library must be empty for a flat schematic.
        Assert.Empty(result.Library.Cells);

        // Instances are unchanged: both resistors emitted.
        Assert.Equal(2, result.TestBench.Instances.Count);
        Assert.Contains(result.TestBench.Instances, i => i.InstanceName == "R1");
        Assert.Contains(result.TestBench.Instances, i => i.InstanceName == "R2");
    }

    // ── Test 2: single 2-port cell instance ──────────────────────────────────

    [Fact]
    public void SingleCellInstance_TwoPorts_CorrectLibraryAndBindings()
    {
        // Sub-cell schematic: 2 Pins + R1
        var sub = TwoPortSubCell();

        // Resolution: "amp" cell has no declared parameters
        var resolution = new CellResolution("amp", sub, []);
        var resolver   = Resolver(("amp", resolution));

        // Parent schematic: X1 is a cell instance with Symbol=Resistor (2 ports),
        // placed at (0,200) so port0=(0,0) and port1=(0,400).
        var model = new SchematicEditModel();
        var x1 = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "amp",
            X            = 0,
            Y            = 200,
        };
        // Parameter override on the instance
        x1.Parameters.Add(new EditableParameter { Name = "Gain", Expression = "2" });
        model.Components.Add(x1);

        // Net labels at the port world coords so we get deterministic net names.
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 0,   Name = "a" });
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 400, Name = "b" });

        var result = NetExtractor.Extract(model, "tb", resolver);

        // ── Library assertion ──
        var cell = result.Library.Find("amp");
        Assert.NotNull(cell);
        Assert.Equal(2, cell.Ports.Count);
        // The sub-cell must contain R1
        Assert.Contains(cell.Instances, i => i.InstanceName == "R1");

        // ── Top-level instance assertion ──
        var inst = Assert.Single(result.TestBench.Instances);
        Assert.Equal("X1",  inst.InstanceName);
        Assert.Equal("amp", inst.Reference);
        Assert.Equal(2,     inst.NetBindings.Count);
        Assert.Equal("a",   inst.NetBindings[0]);
        Assert.Equal("b",   inst.NetBindings[1]);

        // Parameter override carried
        var ov = Assert.Single(inst.Overrides);
        Assert.Equal("Gain", ov.Name);
        Assert.Equal("2",    ov.Expression);
    }

    // ── Test 3: reuse / dedupe ────────────────────────────────────────────────

    [Fact]
    public void TwoInstancesSameCell_LibraryContainsCellOnce()
    {
        var sub      = TwoPortSubCell();
        var resolution = new CellResolution("buf", sub, []);
        var resolver   = Resolver(("buf", resolution));

        var model = new SchematicEditModel();

        // X1 at (0,200) — port0=(0,0), port1=(0,400)
        var x1 = new EditableComponent
            { InstanceName = "X1", Symbol = SymbolKind.Resistor, CellRef = "buf", X = 0, Y = 200 };
        x1.Parameters.Add(new EditableParameter { Name = "R", Expression = "50" });
        model.Components.Add(x1);

        // X2 at (0,800) — port0=(0,600), port1=(0,1000)
        var x2 = new EditableComponent
            { InstanceName = "X2", Symbol = SymbolKind.Resistor, CellRef = "buf", X = 0, Y = 800 };
        x2.Parameters.Add(new EditableParameter { Name = "R", Expression = "75" });
        model.Components.Add(x2);

        var result = NetExtractor.Extract(model, "tb", resolver);

        // "buf" must appear exactly once in the library.
        Assert.Single(result.Library.Cells);
        Assert.NotNull(result.Library.Find("buf"));

        // Two top-level instances, each with their own overrides.
        Assert.Equal(2, result.TestBench.Instances.Count);

        var inst1 = result.TestBench.Instances.First(i => i.InstanceName == "X1");
        var inst2 = result.TestBench.Instances.First(i => i.InstanceName == "X2");
        Assert.Equal("50", inst1.Overrides.Single(o => o.Name == "R").Expression);
        Assert.Equal("75", inst2.Overrides.Single(o => o.Name == "R").Expression);
    }

    // ── Test 4: nested cells ──────────────────────────────────────────────────

    [Fact]
    public void NestedCells_BothInLibrary_LeafFirst()
    {
        // Leaf cell B: a 2-port sub-cell
        var subB = TwoPortSubCell("RB");
        var resB = new CellResolution("cellB", subB, []);

        // Parent cell A: its schematic contains one instance of cellB
        // CellB instance at (0,200): port0=(0,0), port1=(0,400)
        var subA = new SchematicEditModel();
        var innerX = new EditableComponent
        {
            InstanceName = "XB",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "cellB",
            X            = 0,
            Y            = 200,
        };
        subA.Components.Add(innerX);
        // Two pins so A itself has 2 interface ports
        subA.Components.Add(Pin(1, 0, 0));    // Pin at (0,0): port at (0,-200) — attach wire
        subA.Components.Add(Pin(2, 0, 400));  // Pin at (0,400): port at (0,200)
        subA.Wires.Add(Wire((0, -200), (0, 0)));   // Pin1 → XB.port0
        subA.Wires.Add(Wire((0, 400), (0, 600)));  // XB.port1 → Pin2

        var resA = new CellResolution("cellA", subA, []);

        // Resolver handles both "cellA" and "cellB" lookups
        var resolver = Resolver(("cellA", resA), ("cellB", resB));

        // Top-level schematic instantiates cellA
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "XA",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "cellA",
            X            = 0,
            Y            = 200,
        });

        var result = NetExtractor.Extract(model, "tb", resolver);

        // Both cells present
        Assert.NotNull(result.Library.Find("cellA"));
        Assert.NotNull(result.Library.Find("cellB"));

        // Leaf-first: cellB must come before cellA
        var idxB = result.Library.Cells.FindIndex(c => c.Name == "cellB");
        var idxA = result.Library.Cells.FindIndex(c => c.Name == "cellA");
        Assert.True(idxB < idxA, "cellB (leaf) must appear before cellA in the library");

        // cellA's instances contain XB which references cellB
        var cellA = result.Library.Find("cellA")!;
        var xb = cellA.Instances.FirstOrDefault(i => i.InstanceName == "XB");
        Assert.NotNull(xb);
        Assert.Equal("cellB", xb.Reference);
    }

    // ── Test 5: cycle detection ───────────────────────────────────────────────

    [Fact]
    public void CyclicCell_ConflictAdded_ExtractionTerminates()
    {
        // "loop" cell's schematic instantiates itself (CellRef = "loop")
        var loopSchematic = new SchematicEditModel();
        var selfRef = new EditableComponent
        {
            InstanceName = "X_self",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "loop",
            X            = 0,
            Y            = 200,
        };
        loopSchematic.Components.Add(selfRef);

        var res      = new CellResolution("loop", loopSchematic, []);
        var resolver = Resolver(("loop", res));

        // Top-level instantiates "loop"
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "loop",
            X            = 0,
            Y            = 200,
        });

        // Must not throw / stack-overflow; must add a conflict
        var result = NetExtractor.Extract(model, "tb", resolver);

        Assert.Contains(result.Conflicts, c => c.Contains("cycle") || c.Contains("instantiates itself"));
    }

    // ── Test 6: port-count mismatch ───────────────────────────────────────────

    [Fact]
    public void PortMismatch_ConflictAdded_InstanceSkipped()
    {
        // Sub-cell has 3 interface pins (Num 1,2,3)
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, 0, 200));
        sub.Components.Add(Pin(2, 0, 600));
        sub.Components.Add(Pin(3, 0, 1000));
        sub.Components.Add(Resistor("R1", 0, 400));
        sub.Wires.Add(Wire((0, 0),   (0, 200)));
        sub.Wires.Add(Wire((0, 400), (0, 600)));
        sub.Wires.Add(Wire((0, 800), (0, 1000)));

        var res      = new CellResolution("tri", sub, []);
        var resolver = Resolver(("tri", res));

        // Parent places a Resistor-symbol instance (2 ports) for a 3-port cell — mismatch.
        var model = new SchematicEditModel();
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Resistor,  // 2 ports
            CellRef      = "tri",
            X            = 0,
            Y            = 200,
        });

        var result = NetExtractor.Extract(model, "tb", resolver);

        // A conflict must be reported.
        Assert.Contains(result.Conflicts, c => c.Contains("port") || c.Contains("skipped"));

        // The mismatched instance is not emitted.
        Assert.Empty(result.TestBench.Instances);
    }
}
