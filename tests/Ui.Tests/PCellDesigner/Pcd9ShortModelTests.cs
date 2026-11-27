// Owner: PCD-9 (MSHAPE Model="short").

using CircuitRF.Design.Circuit;
using CircuitRF.Design.Layout.Em.ParametricEm;
using RfCore.Data;

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd9ShortModelTests
{
    /// <summary>Model="short" ties every MSHAPE pin to one node and counts as a circuit model: three pins joining two Ports
    /// and a 50 Ω shunt give S21 = 2/3 and S11 = −1/3.</summary>
    [Fact]
    public void ShortModelIsOneNodeAndCountsAsACircuitModel()
    {
        const string text = "Port:P1 a 0 Num=1 Z=50 Ohm\n" +
            "Port:P2 b 0 Num=2 Z=50 Ohm\n" +
            "R:R1 c 0 R=50 Ohm\n" +
            "MSHAPE:SH1 a b c Form=\"polygon\" W=0 um X[1]=0 um Y[1]=-50 um X[2]=100 um Y[2]=-50 um X[3]=100 um Y[3]=50 um " +
            "X[4]=0 um Y[4]=50 um PinX[1]=0 um PinY[1]=0 um PinW[1]=100 um PinDir[1]=180 deg " +
            "PinX[2]=100 um PinY[2]=0 um PinW[2]=100 um PinDir[2]=0 deg " +
            "PinX[3]=50 um PinY[3]=50 um PinW[3]=100 um PinDir[3]=90 deg Model=\"short\"\n" +
            "analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz\n";
        var prepared = PreparedCircuit.FromText(text, null, null);
        Assert.Empty(EmModelSwitch.WithoutCircuitModel(prepared.Tb!, prepared.Lib!));
        var r = CircuitEvaluation.Evaluate(prepared, new CircuitEvaluationRequest { Em = false });
        Assert.True(r.Status == RunStatus.Success, r.StatusMessage);
        var s = DataSetBuilder.ToSnp(r.Results.Single(a => a.Name == "SP1").Data).Matrices[0];
        Assert.Equal(2.0 / 3, s[1, 0].Real, 9);
        Assert.Equal(-1.0 / 3, s[0, 0].Real, 9);
    }
}
