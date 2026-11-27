// Owner: PCD-1 (brief-pcd-1 R-pcd1-2 to -4).

using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd1EmTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-pcd1-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        PdkKitRegistry.ClearWorkspace(_ws);
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>R-pcd1-2: a generator cell, or a kit part, whose .ccell says EmCopper is copper; without it, a part.</summary>
    [Fact]
    public void EmCopperMakesAGeneratorCellAndAKitPartCopper()
    {
        Directory.CreateDirectory(_ws);
        File.WriteAllText(Path.Combine(_ws, ".cws"), WorkspacePersistence.Serialize(new CwsFile()));
        WorkspaceRootFinder.InvalidateCache();
        string schDir = CellFolder.SubFolderPath(CellFolder.CreateCellFolder(_ws, "Bench"), ViewType.Schematic);
        Directory.CreateDirectory(schDir);

        EmPartKind KindOfCell(bool copper)
        {
            string name = copper ? "Designed" : "Generated";
            string dir = CellFolder.CreateCellFolder(_ws, name);
            var ccell = CellFolder.ReadCcell(dir)!;
            ccell.LayoutGenerator = "cpcell:" + name;
            ccell.EmCopper = copper;
            CellPersistence.SaveToFile(Path.Combine(dir, CellFolder.CcellFileName), ccell);
            var c = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = ExternalCellRef.MakeCellRef(schDir, dir) };
            return EmPartKinds.Of(c, schDir);
        }
        Assert.Equal(EmPartKind.Copper, KindOfCell(copper: true));
        Assert.Equal(EmPartKind.Part, KindOfCell(copper: false));

        var symbol = new CircuitRF.Design.Symbol.Symbol(
            primitives: [], pins: [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")], portCount: 2);
        PdkKitRegistry.SetKit(_ws, "Kit", [new PdkKitPart("Pad", symbol, new CcellFile { NumPorts = 2, EmCopper = true }, null),
                                           new PdkKitPart("Fet", symbol, new CcellFile { NumPorts = 2 }, null)]);
        EmPartKind KindOfKit(string part) => EmPartKinds.Of(
            new EditableComponent { InstanceName = "K1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor("Kit", part) }, schDir);
        Assert.Equal(EmPartKind.Copper, KindOfKit("Pad"));
        Assert.Equal(EmPartKind.Part, KindOfKit("Fet"));
    }

    /// <summary>R-pcd1-3: with no Path A the floor's unit is the coarsest placed layout's, and a placed cell's own length
    /// parameter gets a step.</summary>
    [Fact]
    public void StepFloorWithoutPathA_TakesTheCoarsestPlacedUnit_AndCoversACellsOwnParameter()
    {
        EmEvaluationRecord Rec(int? dbu) => new("X1", "PadTee", "k", false, null, null, []) { DbuPerMicron = dbu };
        Assert.Equal(1e-6 / 100, OptimizationRun.StepFloorDbuMetres(null, [Rec(1000), Rec(100), Rec(null)]));
        Assert.Null(OptimizationRun.StepFloorDbuMetres(null, [Rec(null)]));

        var lpad = new OptimizationCoordinate("X1.Lpad", "X1.Lpad", null, "um", 100, 500, TuneScale.Lin, null, false);
        // Two units of 0.01 um over a 400 um box.
        Assert.Equal(2 * 0.01 / 400, OptimizationRun.LengthStepFraction(lpad, 1e-8, 0.5), 12);
    }

    /// <summary>R-pcd1-4: the no-circuit-model sentences name no cause.</summary>
    [Fact]
    public void NoCircuitModelSentencesNameNoCause()
    {
        foreach (var d in new[] { OptimizationDiagnostics.EmDirectForNoCircuitModel("X1"),
                                  OptimizationDiagnostics.EmModeNeedsCircuitModel("correct", "X1") })
        {
            Assert.Contains("circuit model", d.Render());
            Assert.DoesNotContain("3D-parametric", d.Render());
        }
    }
}
