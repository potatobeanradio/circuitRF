using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Cells;
using CircuitRF.Engine;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// <b>The two doors onto one SPICE file must produce the same numbers</b> (owner, 2026-09-01).
///
/// <para>circuitRF offers a user two ways to use a supplier's <c>.model</c> card or <c>.subckt</c>:
/// the project tree's <b>Copy to Workspace as Cell…</b>, which builds an editable cell folder out of
/// it, and the <see cref="SymbolKind.SpiceModel"/> component, which runs the file where it lies.
/// They are different code paths end to end — one draws a schematic and reads the nets back off the
/// geometry, the other translates straight to design-layer cells — and a user choosing between them
/// is choosing a workflow, never a different answer.</para>
///
/// <para><b>This is the strongest oracle available for either path, and it is symmetric.</b> Neither
/// side is the reference: a disagreement says one of them is wrong without saying which, which is
/// exactly the finding worth having. It is also the only check that can catch the import path's own
/// characteristic failure — circuitRF reads connectivity off the DRAWING, so a wire laid a grid
/// square out does not look wrong, it JOINS two nets, and the cell simulates as a different circuit
/// with nothing reporting it.</para>
/// </summary>
public class SpiceModelVersusImportedCellTests : IDisposable
{
    private readonly string _root;

    public SpiceModelVersusImportedCellTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-smvc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        SpiceModelPeek.InvalidateAll();
    }

    public void Dispose()
    {
        SpiceModelPeek.InvalidateAll();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The three fixtures
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A two-port with a shunt branch — enough topology that a transposed or shorted net
    /// changes the answer rather than merely relabelling it.</summary>
    private const string Lowpass = """
        .subckt lowpass in out
        L1 in mid 5n
        C1 mid 0 1.2p
        R1 mid out 8
        .ends
        """;

    /// <summary>Three ports, so the port ORDER is testable at all — a two-port that happens to be
    /// symmetric would agree even with its ports swapped.</summary>
    private const string Tee = """
        .subckt tee a b c
        R1 a n1 10
        R2 b n1 22
        R3 c n1 47
        .ends
        """;

    /// <summary>A nested part, so the import's extra cell folders and this path's extra library
    /// cells are compared as circuits rather than as file listings.</summary>
    private const string Nested = """
        .subckt piece p q
        R1 p q 25
        C1 p q 0.5p
        .ends
        .subckt part a b
        X1 a m piece
        X2 m b piece
        .ends
        """;

    // ─────────────────────────────────────────────────────────────────────────
    //  The gate
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("lowpass", 2)]
    [InlineData("tee",     3)]
    [InlineData("part",    2)]
    public void PlacingTheFileAndImportingItAsACell_GiveTheSameSParameters(string definition, int ports)
    {
        string text = definition switch
        {
            "lowpass" => Lowpass,
            "tee"     => Tee,
            _         => Nested,
        };
        File.WriteAllText(Path.Combine(_root, "part.sp"), text);

        double[] freqs = [1e8, 1e9, 4e9, 12e9];

        var placed   = RunPlacedComponent("part.sp", definition, ports, freqs);
        var imported = RunImportedCell(text, definition, ports, freqs);

        // Two circuits that are both disconnected also agree, so the comparison is only worth
        // anything once the ports are demonstrably wired to the network. S21 is what says so.
        AssertPortsAreActuallyConnected(placed);
        AssertPortsAreActuallyConnected(imported);

        AssertSameDataSet(imported, placed);
    }

    /// <summary>
    /// A refused definition is refused by BOTH doors, and that matters as much as the agreement
    /// above: a user who is told "circuitRF has no model for this" by one route and handed a cell by
    /// the other has been told something false by one of them.
    /// </summary>
    [Fact]
    public void ADefinitionOneDoorRefuses_IsRefusedByTheOtherToo()
    {
        // An element naming a model the file never defines. The import refuses the whole definition
        // — a netlist with a line missing is a DIFFERENT circuit, not a smaller one.
        string text = """
            .subckt broken a b
            M1 a b 0 0 nowhere_nch
            .ends
            """;
        File.WriteAllText(Path.Combine(_root, "broken.sp"), text);

        var translation = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text))
            .Single(t => t.Name.Equals("broken", StringComparison.OrdinalIgnoreCase));
        Assert.False(translation.IsSupported);

        var peeked = SpiceModelPeek.Read(Path.Combine(_root, "broken.sp"));
        var def = Assert.Single(peeked.Definitions);
        Assert.False(def.IsSupported);

        // And the placed component says so rather than emitting something else.
        var (model, comp) = PlaceSpiceModel("broken.sp", "broken");
        _ = comp;
        var r = NetExtractor.Extract(model, "tb");
        Assert.Empty(r.TestBench.Instances.Where(i => i.InstanceName == "X1"));
        Assert.NotEmpty(r.Conflicts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Door 1 — the placed component
    // ─────────────────────────────────────────────────────────────────────────

    private DataSet RunPlacedComponent(string file, string definition, int ports, double[] freqs)
    {
        var (model, comp) = PlaceSpiceModel(file, definition);
        var pins = ResolvedPins(comp, _root);
        Assert.Equal(ports, pins.Count);
        Terminate(model, comp, pins);

        var r = NetExtractor.Extract(model, "tb");
        Assert.Empty(r.Conflicts);
        return SParameterEngine.Run(new Elaborator(r.Library).Elaborate(r.TestBench), freqs);
    }

    private (SchematicEditModel Model, EditableComponent Comp) PlaceSpiceModel(string file, string definition)
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.SpiceModel, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        comp.Parameters.First(p => p.Name == "File").Expression = file;
        comp.Parameters.First(p => p.Name == "Name").Expression = definition;
        model.Components.Add(comp);
        return (model, comp);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Door 2 — the imported cell
    // ─────────────────────────────────────────────────────────────────────────

    private DataSet RunImportedCell(string text, string definition, int ports, double[] freqs)
    {
        var all = SubcircuitTranslator.TranslateAll(SpiceNetlistReader.Read(text));
        var top = all.Single(t => t.Name.Equals(definition, StringComparison.OrdinalIgnoreCase));

        string cellName = definition.ToUpperInvariant() + "CELL";
        var written = SubcircuitCellBuilder.Write(_root, cellName, top, all);
        _ = written;

        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = cellName,
            X = 0, Y = 0,
        };
        model.Components.Add(comp);

        var pins = ResolvedPins(comp, _root);
        Assert.Equal(ports, pins.Count);
        Terminate(model, comp, pins);

        var r = NetExtractor.Extract(model, "tb", new DiskCells());
        Assert.Empty(r.Conflicts);
        return SParameterEngine.Run(new Elaborator(r.Library).Elaborate(r.TestBench), freqs);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Shared harness
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The component's pins as its own symbol resolves them, in port order. Read from the symbol
    /// rather than assumed, because the two doors legitimately draw DIFFERENT boxes — this test is
    /// about the circuit, not the artwork.
    /// </summary>
    private static List<(double X, double Y)> ResolvedPins(EditableComponent comp, string baseDir)
    {
        var res = CellSymbolResolver.Resolve(comp.ExternalSymbolRef!, baseDir);
        Assert.Equal(CellSymbolState.Resolved, res.State);
        return [.. res.Symbol!.Pins.OrderBy(p => p.PortIndex).Select(p => (p.LocalX + comp.X, p.LocalY + comp.Y))];
    }

    /// <summary>
    /// One 50 Ω port on every pin, numbered in PORT ORDER — which is what makes the comparison a
    /// test of port order and not only of the network inside.
    ///
    /// <para>A Term's "+" sits 200 above its own origin and its "−" 200 below, and a Ground's single
    /// pin is at its origin, so a Term placed at (px, py+200) lands its "+" exactly on the component
    /// pin with no wire needed — coincident connection points ARE a net.</para>
    /// </summary>
    private static void Terminate(
        SchematicEditModel model, EditableComponent comp, List<(double X, double Y)> pins)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            var (px, py) = pins[i];
            var term = new EditableComponent
            {
                InstanceName = $"P{i + 1}",
                Symbol       = SymbolKind.Term,
                X = px, Y = py + 200,
            };
            term.Parameters.Add(new EditableParameter { Name = "Num", Expression = (i + 1).ToString() });
            term.Parameters.Add(new EditableParameter { Name = "Z",   Expression = "50" });
            model.Components.Add(term);

            model.Components.Add(new EditableComponent
            {
                InstanceName = $"GP{i + 1}",
                Symbol       = SymbolKind.Ground,
                X = px, Y = py + 400,
            });
        }
        _ = comp;
    }

    /// <summary>
    /// Guards the whole comparison against passing vacuously.
    ///
    /// <para>A harness whose Terms did not land on the component's pins produces an ALL-PORTS-OPEN
    /// network — every |S| exactly 0 or 1 — on BOTH sides, which agrees perfectly and tests nothing.
    /// A network the ports actually reach has at least one entry strictly between the two, and that
    /// is checked without depending on the cube's axis order, which is not what this test is
    /// about.</para>
    /// </summary>
    private static void AssertPortsAreActuallyConnected(DataSet ds)
    {
        var s = ds.Cubes["S"];
        Assert.Equal(DataKind.Complex, s.DataKind);
        Assert.Contains(s.ComplexValues, v => v.Magnitude > 1e-6 && v.Magnitude < 1 - 1e-6);
    }

    private static void AssertSameDataSet(DataSet expected, DataSet actual, double tolerance = 1e-9)
    {
        Assert.Equal(expected.Cubes.Keys.OrderBy(k => k), actual.Cubes.Keys.OrderBy(k => k));

        foreach (var key in expected.Cubes.Keys)
        {
            var e = expected.Cubes[key];
            var a = actual.Cubes[key];
            Assert.Equal(e.DataKind, a.DataKind);

            if (e.DataKind == DataKind.Complex)
            {
                Assert.Equal(e.ComplexValues.Length, a.ComplexValues.Length);
                for (int i = 0; i < e.ComplexValues.Length; i++)
                    Assert.True((e.ComplexValues[i] - a.ComplexValues[i]).Magnitude <= tolerance,
                        $"'{key}'[{i}]: imported {e.ComplexValues[i]}, placed {a.ComplexValues[i]}");
            }
            else
            {
                Assert.Equal(e.RealValues.Length, a.RealValues.Length);
                for (int i = 0; i < e.RealValues.Length; i++)
                    Assert.True(Math.Abs(e.RealValues[i] - a.RealValues[i]) <= tolerance,
                        $"'{key}'[{i}]: imported {e.RealValues[i]}, placed {a.RealValues[i]}");
            }
        }
    }

    /// <summary>The disk-backed resolver the extractor needs for the IMPORTED cell, composed exactly
    /// as <c>WorkspaceViewModel.Resolve</c> composes it.</summary>
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
}
