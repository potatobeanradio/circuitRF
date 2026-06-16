using System.Collections.Generic;
using System.IO;
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

    private static EditableComponent Pin(int num, double cx, double cy, string name)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "Num",  Expression = num.ToString() });
        c.Parameters.Add(new EditableParameter { Name = "Name", Expression = name });
        return c;
    }

    private static EditableComponent Resistor(string name, double cx, double cy)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = cx, Y = cy };

    /// <summary>
    /// Build a minimal 2-port sub-cell schematic.
    /// Pin port is at local (200,0); Pin at (X,Y) connects at world (X+200,Y).
    /// Pin1 at (-200,0) → port at (0,0). Pin2 at (-200,400) → port at (0,400).
    /// Wires connect Pin1.port↔R.port0 and R.port1↔Pin2.port.
    /// </summary>
    private static SchematicEditModel TwoPortSubCell(string resistorName = "R1")
    {
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, -200, 0));    // port at (0,0)
        sub.Components.Add(Resistor(resistorName, 0, 400));
        sub.Components.Add(Pin(2, -200, 400));  // port at (0,400)
        sub.Wires.Add(Wire((0, 0), (0, 200)));   // Pin1.port → R.port0 at (0,200)
        sub.Wires.Add(Wire((0, 400), (0, 600))); // Pin2.port → R.port1 at (0,600)
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
        // Two pins so A itself has 2 interface ports.
        // Pin port is at local (200,0); Pin at (X,Y) connects at world (X+200,Y).
        subA.Components.Add(Pin(1, -200, -200)); // port at (0,-200) → XB.port0 via wire
        subA.Components.Add(Pin(2, -200,  400)); // port at (0, 400) → XB.port1 via wire
        subA.Wires.Add(Wire((0, -200), (0, 0)));   // Pin1.port → XB.port0
        subA.Wires.Add(Wire((0, 400), (0, 600)));  // Pin2.port = XB.port1 = (0,400), coincident

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

    // ── Test 5b: Pin-vs-label regression (the bug this brief fixes) ──────────

    [Fact]
    public void CellPinWithCoincidentLabel_BindsThroughToParent()
    {
        // Sub-cell: Pin1("in") + R1 + net label "mylabel" all on the same node.
        // Pin port is at local (200,0); Pin at (X,Y) connects at world (X+200,Y).
        // Pin1 at (-200,0): port at (0,0). Pin2 at (-200,600): port at (0,600).
        // Before fix, "mylabel" shadowed "in" → cell port mismatch → floating internal node.
        // After fix, Pin wins → net "in" matches Cell.Ports[0] → R1 connects through.
        var sub = new SchematicEditModel();
        sub.Components.Add(Pin(1, -200, 0, "in"));   // port at (0,0)
        sub.Components.Add(Resistor("R1", 0, 400));  // port0=(0,200), port1=(0,600)
        sub.Components.Add(Pin(2, -200, 600, "out")); // port at (0,600) — coincident with R1.port1
        sub.Wires.Add(Wire((0, 0), (0, 200)));        // Pin1.port → R1.port0
        sub.NetLabels.Add(new EditableNetLabel { Name = "mylabel", X = 0, Y = 0 });

        var resolution = new CellResolution("cellFoo", sub, []);
        var resolver   = Resolver(("cellFoo", resolution));

        // Top-level: X1 is a 2-port cell instance at (0,200); parent nets "a" and "b".
        var model = new SchematicEditModel();
        var x1 = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Resistor,
            CellRef      = "cellFoo",
            X            = 0,
            Y            = 200,
        };
        model.Components.Add(x1);
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 0,   Name = "a" });
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 400, Name = "b" });

        var result = NetExtractor.Extract(model, "tb", resolver);

        // Library cell has the correct interface — Port names come from Pin Names.
        var cell = result.Library.Find("cellFoo");
        Assert.NotNull(cell);
        Assert.Equal(2, cell.Ports.Count);
        Assert.Equal("in",  cell.Ports[0]);
        Assert.Equal("out", cell.Ports[1]);

        // Sub-cell's R1 is on net "in" — not "mylabel" (Pin overrode the label).
        var r1 = cell.Instances.First(i => i.InstanceName == "R1");
        Assert.Equal("in",  r1.NetBindings[0]);
        Assert.Equal("out", r1.NetBindings[1]);

        // Top-level X1 correctly binds to parent nets.
        var inst = Assert.Single(result.TestBench.Instances);
        Assert.Equal("a", inst.NetBindings[0]);
        Assert.Equal("b", inst.NetBindings[1]);
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

    // ── Test 7: resolved cell pins at non-default (horizontal) coordinates ───

    /// <summary>
    /// The core regression: a cell-ref instance whose .csym pins are at HORIZONTAL offsets
    /// (local ±200 on X), NOT the built-in vertical default (local 0, ±200).
    /// Before the fix, extraction used placeholder vertical geometry → wrong world coords →
    /// wires at (±200, 0) didn't connect → auto-names instead of wire nets.
    /// After the fix, PortDefsOf reads the resolved .csym pins → correct horizontal coords →
    /// wires bind → NetBindings = the wire net names.
    /// </summary>
    [Fact]
    public void CellInstance_PortsOnResolvedPins_NetThrough()
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "crftst_" + System.Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            const string cellName = "cell_h";
            string cellDir = CellFolder.CreateCellFolder(tempDir, cellName);

            // Symbol with HORIZONTAL pins at local (−200, 0) and (+200, 0).
            // This is deliberately NOT the default vertical (0, ±200) layout.
            var sym = new Symbol(
                primitives: [],
                pins:       [new SymbolPin(-200, 0, portIndex: 0, name: "P1"),
                             new SymbolPin(+200, 0, portIndex: 1, name: "P2")],
                portCount: 2);
            string symPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Symbol), $"{cellName}.csym");
            SymbolPersistence.SaveToFile(symPath, sym);

            // Sub-cell schematic: two interface pins establishing CellPorts.
            var sub = new SchematicEditModel();
            sub.Components.Add(Pin(1, -200, 0, "P1"));  // port at (0, 0)
            sub.Components.Add(Pin(2, -200, 400, "P2")); // port at (0, 400)
            sub.Wires.Add(Wire((0, 0), (0, 400)));
            var resolver = Resolver((cellName, new CellResolution(cellName, sub, [])));

            // Parent: X1 at (0,0), R0 rotation → horizontal world pins at (−200,0) and (+200,0).
            var model = new SchematicEditModel();
            model.SchematicDirectory = tempDir;   // enables CellSymbolResolver path
            var x1 = new EditableComponent
            {
                InstanceName = "X1",
                Symbol       = SymbolKind.Generic,
                CellRef      = cellName,
                X            = 0,
                Y            = 0,
            };
            model.Components.Add(x1);

            // Labels at the HORIZONTAL pin world coords — NOT the default vertical positions.
            model.NetLabels.Add(new EditableNetLabel { X = -200, Y = 0, Name = "in" });
            model.NetLabels.Add(new EditableNetLabel { X =  200, Y = 0, Name = "out" });

            var result = NetExtractor.Extract(model, "tb", resolver);

            Assert.Empty(result.Conflicts);

            var cell = result.Library.Find(cellName);
            Assert.NotNull(cell);
            Assert.Equal(2, cell.Ports.Count);

            // The instance must bind to the wire nets, not auto-names ("n1", "n2").
            var inst = Assert.Single(result.TestBench.Instances);
            Assert.Equal("X1",  inst.InstanceName);
            Assert.Equal(2,     inst.NetBindings.Count);
            Assert.Equal("in",  inst.NetBindings[0]);
            Assert.Equal("out", inst.NetBindings[1]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            CellSymbolResolver.InvalidateAll();
        }
    }

    // ── Test 8: 3-port cell — no always-2 PortCount guard ────────────────────

    [Fact]
    public void ThreePortCell_AllPortsBind()
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "crftst_" + System.Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            const string cellName = "cell_3p";
            string cellDir = CellFolder.CreateCellFolder(tempDir, cellName);

            // 3-port symbol: gate left, drain + source right.
            var sym = new Symbol(
                primitives: [],
                pins:       [new SymbolPin(-200,    0, portIndex: 0, name: "g"),
                             new SymbolPin( 200, -100, portIndex: 1, name: "d"),
                             new SymbolPin( 200,  100, portIndex: 2, name: "s")],
                portCount: 3);
            string symPath = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Symbol), $"{cellName}.csym");
            SymbolPersistence.SaveToFile(symPath, sym);

            // Sub-cell has 3 interface pins establishing CellPorts.
            var sub = new SchematicEditModel();
            sub.Components.Add(Pin(1, -200, 0,   "g")); // port at (0,  0)
            sub.Components.Add(Pin(2, -200, 400, "d")); // port at (0,400)
            sub.Components.Add(Pin(3, -200, 800, "s")); // port at (0,800)
            sub.Wires.Add(Wire((0, 0),   (0, 400)));
            sub.Wires.Add(Wire((0, 400), (0, 800)));
            var resolver = Resolver((cellName, new CellResolution(cellName, sub, [])));

            // Parent: X1 at (0,0), R0 — world pin coords: (−200,0), (200,−100), (200,100).
            var model = new SchematicEditModel();
            model.SchematicDirectory = tempDir;
            var x1 = new EditableComponent
            {
                InstanceName = "X1",
                Symbol       = SymbolKind.Generic,
                CellRef      = cellName,
                X            = 0,
                Y            = 0,
            };
            model.Components.Add(x1);
            model.NetLabels.Add(new EditableNetLabel { X = -200, Y =    0, Name = "gate" });
            model.NetLabels.Add(new EditableNetLabel { X =  200, Y = -100, Name = "drain" });
            model.NetLabels.Add(new EditableNetLabel { X =  200, Y =  100, Name = "source" });

            var result = NetExtractor.Extract(model, "tb", resolver);

            Assert.Empty(result.Conflicts);
            var inst = Assert.Single(result.TestBench.Instances);
            Assert.Equal(3, inst.NetBindings.Count);
            Assert.Equal("gate",   inst.NetBindings[0]);
            Assert.Equal("drain",  inst.NetBindings[1]);
            Assert.Equal("source", inst.NetBindings[2]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            CellSymbolResolver.InvalidateAll();
        }
    }

    // ── Test 9: built-in component unchanged after routing through PortDefsOf ─

    [Fact]
    public void BuiltInComponent_Unchanged()
    {
        // A plain resistor with wires attached — must extract identically after PortDefsOf routing.
        var model = new SchematicEditModel();
        model.Components.Add(Resistor("R1", 0, 200));  // pins at (0,0) and (0,400)
        model.Wires.Add(Wire((0, 0),   (0, 0)));        // degenerate — just ensures node exists
        model.Wires.Add(Wire((0, 400), (0, 400)));
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y =   0, Name = "a" });
        model.NetLabels.Add(new EditableNetLabel { X = 0, Y = 400, Name = "b" });

        var resolver = Resolver();
        var result   = NetExtractor.Extract(model, "tb", resolver);

        Assert.Empty(result.Conflicts);
        var r1 = Assert.Single(result.TestBench.Instances);
        Assert.Equal("R1", r1.InstanceName);
        Assert.Equal(2, r1.NetBindings.Count);
        Assert.Equal("a", r1.NetBindings[0]);
        Assert.Equal("b", r1.NetBindings[1]);
    }

    // ── Test 10: round-trip — no floating nodes after fix ─────────────────────

    [Fact]
    public void RoundTrip_CellInstanceBindsThroughWithResolvedPins()
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            "crftst_" + System.Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            const string cellName = "cell_rt";
            string cellDir = CellFolder.CreateCellFolder(tempDir, cellName);

            // 2-port symbol with horizontal pins — identical layout to Test 7.
            var sym = new Symbol(
                primitives: [],
                pins:       [new SymbolPin(-200, 0, portIndex: 0, name: "In"),
                             new SymbolPin(+200, 0, portIndex: 1, name: "Out")],
                portCount: 2);
            SymbolPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), $"{cellName}.csym"),
                sym);

            // Sub-cell: R1 between the two interface pins.
            // Pin port is at local (200,0); Pin at (X,Y) connects at world (X+200,Y).
            var sub = new SchematicEditModel();
            sub.Components.Add(Pin(1, -200, 0, "In"));   // port at (0, 0)
            sub.Components.Add(Resistor("R1", 0, 400));
            sub.Components.Add(Pin(2, -200, 400, "Out")); // port at (0, 400)
            sub.Wires.Add(Wire((0, 0),   (0, 200)));     // Pin1.port → R1.port0
            sub.Wires.Add(Wire((0, 400), (0, 600)));     // Pin2.port → R1.port1

            var resolver = Resolver((cellName, new CellResolution(cellName, sub, [])));

            // Top-level schematic: X1 between two net-label nodes.
            var model = new SchematicEditModel();
            model.SchematicDirectory = tempDir;
            var x1 = new EditableComponent
            {
                InstanceName = "X1",
                Symbol       = SymbolKind.Generic,
                CellRef      = cellName,
                X            = 0,
                Y            = 0,
            };
            model.Components.Add(x1);
            model.NetLabels.Add(new EditableNetLabel { X = -200, Y = 0, Name = "src" });
            model.NetLabels.Add(new EditableNetLabel { X =  200, Y = 0, Name = "dst" });

            var result = NetExtractor.Extract(model, "tb", resolver);

            // No conflicts — no floating nodes.
            Assert.Empty(result.Conflicts);

            // Library contains the cell with correct ports.
            var cell = result.Library.Find(cellName);
            Assert.NotNull(cell);
            Assert.Equal(["In", "Out"], cell.Ports);

            // Top-level instance binds to parent nets — not auto-names.
            var inst = Assert.Single(result.TestBench.Instances);
            Assert.Equal("src", inst.NetBindings[0]);
            Assert.Equal("dst", inst.NetBindings[1]);

            // Sub-cell's R1 sits between the cell interface ports.
            var r1 = cell.Instances.First(i => i.InstanceName == "R1");
            Assert.Equal("In",  r1.NetBindings[0]);
            Assert.Equal("Out", r1.NetBindings[1]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            CellSymbolResolver.InvalidateAll();
        }
    }
}
