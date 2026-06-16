using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Ui.Schematic;
using Xunit;
using static CircuitRF.Ui.Schematic.VarTextParser;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the VAR component (brief-var-component-core):
/// node-less schematic-authored variable definitions routed by NetExtractor
/// into the enclosing frame's Cell.Variables / TestBench.GlobalVariables.
/// </summary>
public class VarComponentTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EditableComponent Var(params (string Name, string Expression)[] rows)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = "VAR1" };
        foreach (var (name, expr) in rows)
            c.Parameters.Add(new EditableParameter { Name = name, Expression = expr });
        return c;
    }

    private static EditableComponent Var(string instanceName, params (string Name, string Expression)[] rows)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = instanceName };
        foreach (var (name, expr) in rows)
            c.Parameters.Add(new EditableParameter { Name = name, Expression = expr });
        return c;
    }

    private static EditableComponent Resistor(string name, double cx, double cy, string r)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Resistor, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "R", Expression = r });
        return c;
    }

    private static EditableComponent Ground(double cx, double cy)
        => new() { Symbol = SymbolKind.Ground, X = cx, Y = cy };

    private static EditableComponent Pin(int num, double cx, double cy)
    {
        var c = new EditableComponent { Symbol = SymbolKind.Pin, X = cx, Y = cy };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // ── Test 1: Var_BindsIntoCellScope ───────────────────────────────────────

    /// <summary>
    /// A VAR defining Rval=100 at the testbench top, referenced as R=Rval in R1,
    /// must resolve through elaboration to R1.Parameters["R"] == 100.
    /// </summary>
    [Fact]
    public void Var_BindsIntoCellScope()
    {
        // Schematic: VAR(Rval=100), R1(R=Rval) connected between two wires grounded at port1.
        // R1 at (0,400): port0 at (0,200), port1 at (0,600).
        var model = new SchematicEditModel();
        model.Components.Add(Var(("Rval", "100")));
        model.Components.Add(Resistor("R1", 0, 400, "Rval"));
        model.Components.Add(Ground(0, 600));               // grounds R1.port1
        model.Wires.Add(Wire((0, 0), (0, 200)));            // net n1 at top

        var result = NetExtractor.Extract(model);

        // VAR variable must appear in GlobalVariables, not as an instance.
        Assert.Single(result.TestBench.GlobalVariables);
        Assert.Equal("Rval", result.TestBench.GlobalVariables[0].Name);
        Assert.Equal("100",  result.TestBench.GlobalVariables[0].Expression);

        // Elaborate and check R is resolved to 100.
        var nl    = new Elaborator().Elaborate(result.TestBench);
        var r1Ec  = nl.Components.First(c => c.InstancePath == "R1");
        Assert.Equal(100.0, r1Ec.Parameters["R"].AsReal());
    }

    // ── Test 2: Var_PerCellIsolation ─────────────────────────────────────────

    /// <summary>
    /// Two sub-cells each containing a VAR defining X to a different value must
    /// produce independent Variable lists in the Library — no cross-cell leakage.
    /// </summary>
    [Fact]
    public void Var_PerCellIsolation()
    {
        // Sub-cell A: Pin(1) at (0,200) + VAR(X=10) + Pin(2) at (0,600)
        var subA = new SchematicEditModel();
        subA.Components.Add(Pin(1, 0, 200));       // port at (0,0)
        subA.Components.Add(Var(("X", "10")));
        subA.Components.Add(Pin(2, 0, 600));       // port at (0,400)
        subA.Wires.Add(Wire((0, 0), (0, 200)));
        subA.Wires.Add(Wire((0, 400), (0, 600)));

        // Sub-cell B: Pin(1) at (0,200) + VAR(X=20) + Pin(2) at (0,600)
        var subB = new SchematicEditModel();
        subB.Components.Add(Pin(1, 0, 200));
        subB.Components.Add(Var(("X", "20")));
        subB.Components.Add(Pin(2, 0, 600));
        subB.Wires.Add(Wire((0, 0), (0, 200)));
        subB.Wires.Add(Wire((0, 400), (0, 600)));

        // Top schematic: two cell instances
        var top = new SchematicEditModel();
        var compA = new EditableComponent
            { InstanceName = "U1", CellRef = "CellA", X = 0, Y = 200 };
        var compB = new EditableComponent
            { InstanceName = "U2", CellRef = "CellB", X = 0, Y = 800 };
        top.Components.Add(compA);
        top.Components.Add(compB);

        var resolver = new StubCellResolver(new()
        {
            ["CellA"] = new CellResolution("CellA", subA, []),
            ["CellB"] = new CellResolution("CellB", subB, []),
        });

        var result = NetExtractor.Extract(top, cells: resolver);

        var cellA = result.Library.Find("CellA");
        var cellB = result.Library.Find("CellB");

        Assert.NotNull(cellA);
        Assert.NotNull(cellB);

        // Each cell has exactly one variable, named X, with the correct independent value.
        Assert.Single(cellA.Variables);
        Assert.Equal("X",  cellA.Variables[0].Name);
        Assert.Equal("10", cellA.Variables[0].Expression);

        Assert.Single(cellB.Variables);
        Assert.Equal("X",  cellB.Variables[0].Name);
        Assert.Equal("20", cellB.Variables[0].Expression);

        // Top-level GlobalVariables must be empty (no top-level VARs).
        Assert.Empty(result.TestBench.GlobalVariables);
    }

    // ── Test 3: Var_TopLevelGlobal ───────────────────────────────────────────

    /// <summary>
    /// A VAR at the testbench top defines Pin=-10.
    /// tb.GlobalVariables must contain Pin; elaboration must expose it in ResolvedGlobals.
    /// </summary>
    [Fact]
    public void Var_TopLevelGlobal()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Var(("Pin", "-10")));

        var result = NetExtractor.Extract(model);

        Assert.Single(result.TestBench.GlobalVariables);
        Assert.Equal("Pin",  result.TestBench.GlobalVariables[0].Name);
        Assert.Equal("-10",  result.TestBench.GlobalVariables[0].Expression);

        // Elaboration must expose Pin in ResolvedGlobals so HB sweep machinery can see it.
        var nl = new Elaborator().Elaborate(result.TestBench);
        Assert.True(nl.ResolvedGlobals.ContainsKey("Pin"));
        Assert.Equal(-10.0, nl.ResolvedGlobals["Pin"].AsReal());
    }

    // ── Test 4: Var_MultipleVarsUnion ────────────────────────────────────────

    /// <summary>
    /// Two VAR components in one cell produce a union of their variables.
    /// A duplicate name across them emits a conflict and keeps the first definition.
    /// </summary>
    [Fact]
    public void Var_MultipleVarsUnion()
    {
        var model = new SchematicEditModel();
        // VAR1: A=1, B=2  |  VAR2: C=3, B=99 (B is a duplicate)
        model.Components.Add(Var("VAR1", ("A", "1"), ("B", "2")));
        model.Components.Add(Var("VAR2", ("C", "3"), ("B", "99")));

        var result = NetExtractor.Extract(model);

        // A, B, C must all be present; duplicate B resolved to first (B=2).
        var vars = result.TestBench.GlobalVariables;
        Assert.Equal(3, vars.Count);
        Assert.Equal("A", vars[0].Name); Assert.Equal("1", vars[0].Expression);
        Assert.Equal("B", vars[1].Name); Assert.Equal("2", vars[1].Expression);
        Assert.Equal("C", vars[2].Name); Assert.Equal("3", vars[2].Expression);

        // A conflict message must be reported for the duplicate B.
        Assert.Contains(result.Conflicts, c => c.Contains("'B'") && c.Contains("more than once"));
    }

    // ── Test 5: Var_NotEmittedAsComponent ────────────────────────────────────

    /// <summary>
    /// A VAR must never appear in TestBench.Instances — only in GlobalVariables.
    /// </summary>
    [Fact]
    public void Var_NotEmittedAsComponent()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Var(("Foo", "42")));
        // Also add a resistor to confirm it IS emitted normally.
        model.Components.Add(Resistor("R1", 0, 400, "50"));
        model.Components.Add(Ground(0, 600));

        var result = NetExtractor.Extract(model);

        // No instance with reference "VAR" (or any InstanceName from a VAR component).
        Assert.DoesNotContain(result.TestBench.Instances,
            i => i.Reference.Equals("VAR", System.StringComparison.OrdinalIgnoreCase));

        // R1 IS emitted.
        Assert.Contains(result.TestBench.Instances, i => i.InstanceName == "R1");

        // Foo shows up as a global variable, not as an instance.
        Assert.Single(result.TestBench.GlobalVariables);
        Assert.Equal("Foo", result.TestBench.GlobalVariables[0].Name);
    }

    // ── Test 6: Var_Sweepable ────────────────────────────────────────────────

    /// <summary>
    /// A top-level VAR variable is visible in ElaboratedNetlist.ResolvedGlobals,
    /// confirming the existing sweep machinery can see it.
    /// </summary>
    [Fact]
    public void Var_Sweepable()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Var(("Freq", "2.4e9")));

        var result = NetExtractor.Extract(model);
        var nl     = new Elaborator().Elaborate(result.TestBench);

        Assert.True(nl.ResolvedGlobals.ContainsKey("Freq"));
        Assert.Equal(2.4e9, nl.ResolvedGlobals["Freq"].AsReal(), precision: 6);
    }

    // ── Test 7: ParseLines_RoundTrips ────────────────────────────────────────

    /// <summary>
    /// ParseLines must correctly classify valid lines, comments, blanks, and bad lines.
    /// SerializeLines must round-trip the valid subset back to "name = expression" text.
    /// </summary>
    [Fact]
    public void ParseLines_RoundTrips()
    {
        const string input = "Pin = -10\nGain = 2*Pin\n# comment\n\nBad line";
        var lines = ParseLines(input);

        // Two valid lines, one comment, one blank, one invalid.
        var valid   = lines.Where(l => l.IsValid).ToList();
        var comment = lines.Where(l => l.IsComment).ToList();
        var blank   = lines.Where(l => l.IsBlank).ToList();
        var invalid = lines.Where(l => !l.IsValid && !l.IsBlank && !l.IsComment).ToList();

        Assert.Equal(2, valid.Count);
        Assert.Single(comment);
        Assert.Single(blank);
        Assert.Single(invalid);
        Assert.NotNull(invalid[0].ErrorMessage);

        Assert.Equal("Pin",    valid[0].Name);
        Assert.Equal("-10",    valid[0].Expression);
        Assert.Equal("Gain",   valid[1].Name);
        Assert.Equal("2*Pin",  valid[1].Expression);

        // Round-trip: serialize valid lines back to text.
        var parms = valid.Select(l => new EditableParameter { Name = l.Name!, Expression = l.Expression! });
        string serialized = SerializeLines(parms);
        Assert.Equal("Pin = -10\nGain = 2*Pin", serialized);
    }

    // ── Test 8: Duplicate_EmptyName_Flagged ──────────────────────────────────

    /// <summary>
    /// A line with an empty name (e.g. "= 5") must be flagged invalid.
    /// FindDuplicateNames must surface names declared more than once.
    /// </summary>
    [Fact]
    public void Duplicate_EmptyName_Flagged()
    {
        const string input = "X = 1\n = 2\nX = 3";
        var lines = ParseLines(input);

        // " = 2" → invalid (empty name)
        var invalids = lines.Where(l => !l.IsValid && !l.IsBlank && !l.IsComment).ToList();
        Assert.Single(invalids);
        Assert.Contains("empty", invalids[0].ErrorMessage, System.StringComparison.OrdinalIgnoreCase);

        // X appears twice → duplicate
        var dupes = FindDuplicateNames(lines);
        Assert.Single(dupes);
        Assert.Equal("X", dupes[0]);
    }

    // ── Stub resolver ────────────────────────────────────────────────────────

    private sealed class StubCellResolver : ICellResolver
    {
        private readonly System.Collections.Generic.Dictionary<string, CellResolution> _map;

        public StubCellResolver(System.Collections.Generic.Dictionary<string, CellResolution> map)
            => _map = map;

        public CellResolution? Resolve(EditableComponent comp, SchematicEditModel _)
            => comp.CellRef is not null && _map.TryGetValue(comp.CellRef, out var r) ? r : null;
    }
}
