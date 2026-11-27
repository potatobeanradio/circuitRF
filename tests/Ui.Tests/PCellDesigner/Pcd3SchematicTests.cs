// Owner: PCD-3 (brief-pcd-3 R-pcd3-1).

using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd3SchematicTests
{
    private static string Bench(string model) =>
        "Port:P1 in 0 Num=1 Z=50 Ohm\n" +
        "Port:P2 out 0 Num=2 Z=50 Ohm\n" +
        "R:R1 in out R=50 Ohm\n" +
        "MSHAPE:SH1 in out Form=\"polygon\" W=0 um X[1]=0 um Y[1]=-50 um X[2]=100 um Y[2]=-50 um X[3]=100 um Y[3]=50 um " +
        "X[4]=0 um Y[4]=50 um PinX[1]=0 um PinY[1]=0 um PinW[1]=100 um PinDir[1]=180 deg " +
        "PinX[2]=100 um PinY[2]=0 um PinW[2]=100 um PinDir[2]=0 deg " + model + "\n" +
        "analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz\n";

    /// <summary>R-pcd3-1: MSHAPE is a kind with its .cnl name and a pin per PinX row; it reads into a schematic and writes
    /// back; a run by circuit models refuses Model="none" naming it, and runs Model=open with its pins open.</summary>
    [Fact]
    public void MShapeIsAKindWithItsNetlistNameAndRunsOpenOrNotAtAll()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("SH", out var kind, out _));
        Assert.Equal(SymbolKind.MShape, kind);
        Assert.Equal("MSHAPE", ComponentTypeRegistry.EngineReference(SymbolKind.MShape, 1));
        var placed = new EditableComponent { Symbol = SymbolKind.MShape };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
            placed.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit });
        Assert.Equal(1, placed.PortCount);
        Assert.Single(SymbolPortDefs.For(placed));

        var (lib, tb) = new CnlReader().Read(Bench("Model=\"none\""));
        var drawn = NetlistSchematic.Build(lib, tb);
        var shape = Assert.Single(drawn.Schematic!.Components, c => c.Symbol == SymbolKind.MShape);
        Assert.Equal(2, shape.PortCount);
        string cnl = SchematicCircuit.CnlTextOf(drawn.Schematic, "tb");
        string line = System.Text.RegularExpressions.Regex.Replace(cnl, " +", " ");   // the writer pads with two spaces
        Assert.Contains("MSHAPE:SH1 in out Form=\"polygon\" W=0 um X[1]=0 um", line);
        Assert.Contains("PinDir[2]=0 deg Model=\"none\"", line);

        var (lib2, tb2) = new CnlReader().Read(cnl);
        Assert.Equal(new[] { "SH1" }, EmModelSwitch.WithoutCircuitModel(tb2, lib2));
        var refused = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, null, null), new CircuitEvaluationRequest { Em = false });
        Assert.Equal(RunStatus.EngineError, refused.Status);
        Assert.Contains("SH1 has no circuit model", refused.StatusMessage);

        var open = PreparedCircuit.FromText(Bench("Model=\"open\""), null, null);
        Assert.Empty(EmModelSwitch.WithoutCircuitModel(open.Tb!, open.Lib!));
        var ran = CircuitEvaluation.Evaluate(open, new CircuitEvaluationRequest { Em = false });
        Assert.True(ran.Status == RunStatus.Success, ran.StatusMessage);
    }
}
