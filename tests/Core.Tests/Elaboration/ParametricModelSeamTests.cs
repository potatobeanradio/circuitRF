using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// brief-sdl-6 D9 and D7 — the seam through which an instance is simulated by a model made at its own values, held with a
/// stub provider (no EM): which values it is asked at, what it places, and the rule an instance's override of a cell
/// VARIABLE follows (the same as a parameter's: evaluated in the parent, replacing the cell's expression for this
/// instance only, the cell's other variables following through ordinary evaluation).
/// </summary>
public sealed class ParametricModelSeamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-seam-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly StubProvider _stub;

    public ParametricModelSeamTests()
    {
        Directory.CreateDirectory(_root);
        string snp = Path.Combine(_root, "line.s2p");
        File.WriteAllText(snp, "# GHz S MA R 50\n1.0   0.1 0   0.9 -90   0.9 -90   0.1 0\n");
        _stub = new StubProvider("stub-" + Guid.NewGuid().ToString("N")[..8], snp);
        ParametricModelRegistry.Register(_stub);
    }

    public void Dispose()
    {
        ParametricModelRegistry.Unregister(_stub);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A cell whose two lines are set by its own variables, w50 and the L90 that follows it.</summary>
    private string Netlist(string instanceLine) => $$"""
        define Line (a b)
          w50 = 1
          L90 = w50*2
          R:R1 a m R=w50
          R:R2 m b R=L90
        end Line
        Port:P1 in 0 Num=1 Z=50 Ohm
        Port:P2 out 0 Num=2 Z=50 Ohm
        {{instanceLine}}
        analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz
        """;

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    [Fact]
    public void ModelEm_AsksTheProviderAtTheInstancesValues_AndPlacesItsSnp()
    {
        using var nl = Elaborate(Netlist($"Line:X1 in out w50=3 Model=EM EmCell=\"{_stub.Cell}\""));
        var request = Assert.Single(_stub.Requests);
        Assert.Equal(3.0, request.Values["w50"].AsReal(), 12);
        Assert.Equal(6.0, request.Values["L90"].AsReal(), 12);        // a dependent variable follows
        Assert.Equal(("X1", 2), (request.InstancePath, request.Ports));
        var snp = Assert.Single(nl.Components, c => c.ComponentType.Equals("SnP", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("X1", snp.InstancePath);
        Assert.DoesNotContain(nl.Components, c => c.InstancePath == "X1.R1");
    }

    [Fact]
    public void ModelCircuit_IsTheOrdinaryCell_WithTheReservedKeysTakenOff()
    {
        using var nl = Elaborate(Netlist($"Line:X1 in out w50=3 Model=Circuit EmCell=\"{_stub.Cell}\""));
        Assert.Empty(_stub.Requests);
        Assert.Equal(3.0, nl.Components.Single(c => c.InstancePath == "X1.R1").Parameters["R"].AsReal(), 12);
    }

    [Fact]
    public void PathA_ReferencesNoCell_AndIsAskedAtTheGlobals()
    {
        string cnl = $"""
            w50 = 7
            Port:P1 in 0 Num=1 Z=50 Ohm
            Port:P2 out 0 Num=2 Z=50 Ohm
            EM_C:EM_C in out Model=EM EmCell="{_stub.Cell}"
            analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz model=em
            """;
        var (lib, tb) = new CnlReader().Read(cnl);
        Assert.Equal(AnalysisModel.Em, tb.Analyses.Single().Model);
        using var nl = new Elaborator(lib).Elaborate(tb);
        Assert.Equal(7.0, Assert.Single(_stub.Requests).Values["w50"].AsReal(), 12);
    }

    [Fact]
    public void ACountedEvaluation_PlacesNothing_AndARefusalNamesTheInstance()
    {
        _stub.Answer = ParametricModelAnswer.Counted();
        using (var nl = Elaborate(Netlist($"Line:X1 in out Model=EM EmCell=\"{_stub.Cell}\"")))
            Assert.DoesNotContain(nl.Components, c => c.InstancePath.StartsWith("X1", StringComparison.Ordinal));

        _stub.Answer = ParametricModelAnswer.Refused("its layout has no EM setup");
        var ex = Assert.Throws<InvalidOperationException>(() => Elaborate(Netlist($"Line:X1 in out Model=EM EmCell=\"{_stub.Cell}\"")));
        Assert.Contains("'X1' cannot be simulated by EM: its layout has no EM setup", ex.Message);
    }

    /// <summary>D7: two instances of one cell with different w50 differ; an unoverridden one is the cell's own value; the
    /// variable that depends on w50 follows per instance.</summary>
    [Fact]
    public void D7_AnInstanceOverridesACellVariable_ForItselfOnly()
    {
        using var nl = Elaborate(Netlist("Line:X1 in mid w50=5\nLine:X2 mid out"));
        double R(string path) => nl.Components.Single(c => c.InstancePath == path).Parameters["R"].AsReal();
        Assert.Equal((5.0, 10.0), (R("X1.R1"), R("X1.R2")));
        Assert.Equal((1.0, 2.0), (R("X2.R1"), R("X2.R2")));
    }

    [Fact]
    public void ModelAndEmSetup_RoundTripOnTheAnalysisLine()
    {
        var (_, tb) = new CnlReader().Read(Netlist("Line:X1 in out").Replace("Unit=GHz", "Unit=GHz model=em emSetup=em/Line.cem"));
        var sp = tb.Analyses.Single();
        Assert.Equal((AnalysisModel.Em, "em/Line.cem"), (sp.Model, sp.EmSetup));
        string written = CnlWriter.Write(tb);
        Assert.Contains("model=em emSetup=em/Line.cem", written);
        var (_, again) = new CnlReader().Read(written);
        Assert.Equal((AnalysisModel.Em, "em/Line.cem"), (again.Analyses.Single().Model, again.Analyses.Single().EmSetup));
        Assert.DoesNotContain("model=", CnlWriter.Write(new CnlReader().Read(Netlist("Line:X1 in out")).Item2));
    }

    private sealed class StubProvider(string cell, string snp) : IParametricModelProvider
    {
        public string Cell { get; } = cell;
        public List<ParametricModelRequest> Requests { get; } = [];
        public ParametricModelAnswer? Answer { get; set; }
        public string Describe => "the test stub";

        public ParametricModelAnswer? Resolve(ParametricModelRequest request)
        {
            if (request.Cell != Cell) return null;
            Requests.Add(request);
            return Answer ?? ParametricModelAnswer.Snp(snp);
        }
    }
}
