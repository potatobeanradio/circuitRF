using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-C4: embedding the layout geometry into a `.wBond` (§9.1, WB33–35), and the wirebond-table
/// import (§9.3 / WB36).
/// </summary>
public class WBondInterchangeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wbond-interchange-" + Guid.NewGuid().ToString("N")[..8]);

    public WBondInterchangeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Writes a cell whose layout carries one rect, optionally marked as a PCell's output.</summary>
    private string MakeCell(string name, string? generatorId = null)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, Layer = new LayerKey(1, 0) });

        if (generatorId is not null)
            view.PCellOrigin = new PCellOrigin(generatorId, new Dictionary<string, PCellValue>());

        string clay = Path.Combine(layoutDir, name + ".clay");
        LayoutPersistence.SaveToFile(clay, view);
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName),
                                   new CcellFile { PrimaryLayout = name + ".clay" });

        CellLayoutResolver.InvalidateUnder(_root);
        return cellDir;
    }

    /// <summary>A root layout referencing the given cells, and the base directory it resolves against.</summary>
    private (LayoutView View, string BaseDir) MakeRoot(params string[] cellDirs)
    {
        string rootLayoutDir = Path.Combine(_root, "__top", CellFolder.LayoutSubFolder);
        Directory.CreateDirectory(rootLayoutDir);

        var view = new LayoutView();
        foreach (string cellDir in cellDirs)
            view.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(rootLayoutDir, cellDir) });

        return (view, rootLayoutDir);
    }

    // ---------------------------------------------------------------- WB33 / WB34

    /// <summary>
    /// <b>A vendor PCell is flattened; a circuitRF one is not.</b> That asymmetry is the whole reason
    /// the distinction is worth drawing — flattening everything would be simpler and would silently
    /// cost the recipient every parameter on cells that did not need to lose them.
    /// </summary>
    [Fact]
    public void Analyze_SeparatesVendorPCellsFromCircuitRfOnes()
    {
        // MLIN is one of circuitRF's own built-ins; the other id belongs to no built-in generator.
        string native = MakeCell("NativePad", generatorId: "MLIN");
        string vendor = MakeCell("VendorPad", generatorId: "acme_pdk_nmos");
        string plain = MakeCell("PlainPad");

        var (root, baseDir) = MakeRoot(native, vendor, plain);

        var plan = WBondGeometryEmbedding.Analyze(root, baseDir);

        Assert.Equal(3, plan.Cells.Count);
        Assert.Contains(plan.PdkFlattened, c => c.EndsWith("VendorPad", StringComparison.Ordinal));
        Assert.Contains(plan.NativeKept, c => c.EndsWith("NativePad", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.PdkFlattened, c => c.EndsWith("NativePad", StringComparison.Ordinal));
        Assert.False(plan.HasNothingToReport);
    }

    /// <summary>A design of ordinary cells costs the user nothing, and the dialog says nothing.</summary>
    [Fact]
    public void Analyze_OrdinaryCells_HaveNothingToReport()
    {
        var (root, baseDir) = MakeRoot(MakeCell("PadA"), MakeCell("PadB"));

        var plan = WBondGeometryEmbedding.Analyze(root, baseDir);

        Assert.True(plan.HasNothingToReport);
        Assert.Empty(plan.PdkFlattened);
        Assert.Empty(plan.Unresolved);
    }

    /// <summary>An unresolvable reference is NAMED rather than silently dropped from the file (WB35).</summary>
    [Fact]
    public void Analyze_NamesAnUnresolvableReference()
    {
        var (root, baseDir) = MakeRoot(MakeCell("PadA"));
        root.Instances.Add(new LayoutInstance { CellRef = "../../NotThere" });

        var plan = WBondGeometryEmbedding.Analyze(root, baseDir);

        Assert.Single(plan.Unresolved);
        Assert.False(plan.HasNothingToReport);
    }

    /// <summary>
    /// <b>Embedding must not change the user's design.</b> Flattening happens on a clone — mutating
    /// the resolver's cached view would leave the live workspace holding flattened geometry after a
    /// save, which is a save silently editing what it saved.
    /// </summary>
    [Fact]
    public void Embed_DoesNotFlattenTheLiveWorkspacesOwnCell()
    {
        string vendor = MakeCell("VendorPad", generatorId: "acme_pdk_nmos");
        var (root, baseDir) = MakeRoot(vendor);

        WBondGeometryEmbedding.Embed(root, baseDir);

        var live = CellLayoutResolver.Resolve(vendor, baseDir: "");
        Assert.Equal(CellLayoutState.Resolved, live.State);
        Assert.NotNull(live.View!.PCellOrigin);      // still parametric in the workspace
    }

    // ---------------------------------------------------------------- round trip

    /// <summary>
    /// The whole point of §9.1: embed, then open somewhere with no access to the original workspace
    /// and still have the geometry.
    /// </summary>
    [Fact]
    public void EmbedThenUnpack_ReproducesTheGeometry()
    {
        string cell = MakeCell("Pad");
        var (root, baseDir) = MakeRoot(cell);

        string bundle = WBondGeometryEmbedding.Embed(root, baseDir);

        string elsewhere = Path.Combine(_root, "unpacked");
        var unpacked = WBondGeometryEmbedding.Unpack(bundle, elsewhere);

        Assert.NotNull(unpacked);
        Assert.Single(unpacked!.Value.Root.Instances);

        // The instance resolves against the unpacked tree, with no reference to where it came from.
        var resolved = CellLayoutResolver.Resolve(
            unpacked.Value.Root.Instances[0].CellRef, unpacked.Value.BaseDir);

        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        Assert.Single(resolved.View!.Shapes);
    }

    /// <summary>A native PCell survives the round trip still parametric (WB34).</summary>
    [Fact]
    public void ANativePCell_StaysParametricThroughTheRoundTrip()
    {
        var (root, baseDir) = MakeRoot(MakeCell("NativePad", generatorId: "MLIN"));

        var unpacked = WBondGeometryEmbedding.Unpack(
            WBondGeometryEmbedding.Embed(root, baseDir), Path.Combine(_root, "unpacked-native"));

        var resolved = CellLayoutResolver.Resolve(
            unpacked!.Value.Root.Instances[0].CellRef, unpacked.Value.BaseDir);

        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        Assert.NotNull(resolved.View!.PCellOrigin);
    }

    /// <summary>A vendor PCell comes back as ordinary polygons, not claiming a generator that cannot travel.</summary>
    [Fact]
    public void AVendorPCell_ComesBackFlattened()
    {
        var (root, baseDir) = MakeRoot(MakeCell("VendorPad", generatorId: "acme_pdk_nmos"));

        var unpacked = WBondGeometryEmbedding.Unpack(
            WBondGeometryEmbedding.Embed(root, baseDir), Path.Combine(_root, "unpacked-vendor"));

        var resolved = CellLayoutResolver.Resolve(
            unpacked!.Value.Root.Instances[0].CellRef, unpacked.Value.BaseDir);

        Assert.Equal(CellLayoutState.Resolved, resolved.State);
        Assert.Null(resolved.View!.PCellOrigin);
        Assert.Single(resolved.View.Shapes);          // the geometry itself is still there
    }

    /// <summary>A foreign or absent bundle is ignored, never half-applied.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"marker\":\"something-else\"}")]
    [InlineData("not json at all")]
    public void AForeignBundle_UnpacksToNothing(string? bundle)
        => Assert.Null(WBondGeometryEmbedding.Unpack(bundle, Path.Combine(_root, "unpacked-foreign")));

    /// <summary>The whole file round-trips: a `.wBond` saved with geometry opens with it.</summary>
    [Fact]
    public void ADocumentSavedWithGeometry_ReopensWithIt()
    {
        var (root, baseDir) = MakeRoot(MakeCell("Pad"));

        var doc = new WBondDocument();
        doc.ViewModel.ReferenceLayout = new LayoutEditorViewModel(root)
        {
            CurrentLayoutPath = Path.Combine(baseDir, "top.clay"),
        };

        string file = Path.Combine(_root, "design.wBond");
        doc.Save(file, embedGeometry: true);

        var reopened = WBondDocument.Open(file, Path.Combine(_root, "reopened"));

        Assert.True(reopened.HasEmbeddedGeometry);
        Assert.NotNull(reopened.ViewModel.ReferenceLayout);
        Assert.Single(reopened.ViewModel.ReferenceLayout!.Model.Instances);
    }

    /// <summary>
    /// Saving WITHOUT embedding leaves the file small and geometry-free — and it still opens, with
    /// the wires intact. Referencing is the default for a reason.
    /// </summary>
    [Fact]
    public void ADocumentSavedWithoutGeometry_StillOpens()
    {
        var (root, baseDir) = MakeRoot(MakeCell("Pad"));

        var doc = new WBondDocument();
        doc.ViewModel.ReferenceLayout = new LayoutEditorViewModel(root)
        {
            CurrentLayoutPath = Path.Combine(baseDir, "top.clay"),
        };

        string file = Path.Combine(_root, "referenced.wBond");
        doc.Save(file, embedGeometry: false);

        var reopened = WBondDocument.Open(file, Path.Combine(_root, "reopened-ref"));

        Assert.False(reopened.HasEmbeddedGeometry);
        Assert.NotEmpty(reopened.ViewModel.Editor.Design.AllWires());   // the wires are the point
    }

    // ---------------------------------------------------------------- WB36 — the wire table

    /// <summary>
    /// A wirebond table becomes a design. Hand-placing 600 wires is not a workflow, and every
    /// packaging flow already has this table.
    /// </summary>
    [Fact]
    public void AWireTable_ImportsIntoADesign()
    {
        string csv = Path.Combine(_root, "bonds.csv");
        File.WriteAllText(csv, string.Join('\n',
        [
            "array,x1,y1,z1,x2,y2,z2",
            "G1,0,0,4,100,0,1",
            "G1,0,6,4,100,6,1",
            "Vdd,0,40,4,100,40,1",
        ]));

        var design = WireTableCsv.ReadFile(csv);

        Assert.Equal(3, design.WireCount);
        Assert.Equal(2, design.Arrays.Count);

        // And it is a design the editor can actually open — the reduction runs on it.
        var vm = new WBondViewModel(design);
        Assert.Equal(2, vm.Readout.Rows.Count);
    }

    /// <summary>A malformed table names the offending line rather than failing vaguely.</summary>
    [Fact]
    public void AMalformedWireTable_NamesTheProblem()
    {
        string csv = Path.Combine(_root, "bad.csv");
        File.WriteAllText(csv, "array,x1,y1\nG1,0,0\n");

        var ex = Assert.Throws<InvalidDataException>(() => WireTableCsv.ReadFile(csv));
        Assert.Contains("z2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
