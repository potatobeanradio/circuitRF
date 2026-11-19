// brief-paa-12 gates 1 and 2 — the Phased Array tool on a harmonic-balance bench a sweep wraps (the Phased Array example's 5 Brute Force):
// the tool's frame is one point at the bench's own values and says so, the bench's own Run still sweeps, and a drag of an array steered by
// its drives writes the steering VAR. Gate 3 (README promises) is ExampleReadmeToolPromisesTests; gate R-paa12-3's pictures are UiSnapshot
// PNGs, looked at.

using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.PhasedArray;
using CircuitRF.Ui.ViewModels;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.PhasedArray;

[Collection(Sar.FieldCosimulationRunsCollection.Name)]
public sealed class SweptWavesToolTests : IDisposable
{
    private const string Bench = "5 Brute Force";
    private readonly string _ws = Directory.CreateTempSubdirectory("crf-paa12-bf-").FullName;
    public void Dispose() { try { Directory.Delete(_ws, true); } catch (IOException) { } }

    public SweptWavesToolTests()
    {
        string from = Path.Combine(Em3d.PalaceBackendTests.RepoRoot(), "examples", "Phased Array");
        foreach (string d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(_ws, Path.GetRelativePath(from, d)));
        foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(_ws, Path.GetRelativePath(from, f)));
    }

    private string Csch => Path.Combine(_ws, Bench, "schematic", Bench + ".csch");

    /// <summary>Gate 1 — a frame at θ0 = 0°, Pin = 22 dBm, the README's PAE (68 % inner, 63 % outer), and a status naming the point.</summary>
    [Fact]
    public void Gate1_TheToolFrame_IsOnePointOfTheSweep_WithTheReadmesPae()
    {
        var session = new PhasedArraySession(PreparedCircuit.FromSchematic(Csch), "ARR");
        Assert.Equal(["SW_theta", "SW_Pin"], session.CollapsedSweeps.Select(s => s.Name));
        var tunables = new Dictionary<string, string> { ["Pin"] = "22" };
        var eval = session.Evaluate(tunables, interactive: false, (0, 0));
        Assert.True(eval.Frame is not null, eval.Error);
        var m = eval.Frame.Measurements;
        Assert.Equal(68, m["PAE2"], 0.5);
        Assert.Equal(68, m["PAE3"], 0.5);
        Assert.Equal(63, m["PAE1"], 0.5);
        Assert.Equal(63, m["PAE4"], 0.5);
        Assert.Equal("one point of sweeps 'SW_theta', 'SW_Pin': theta0 = 0°, Pin = 22 dBm", eval.Frame.SweepPoint);
        // four amplifiers of about 40 dBm into 9.8 dBi of gain
        Assert.Equal(55.8, eval.Frame.Readouts.EirpDbm!.Value, 0.1);
    }

    /// <summary>A run from HB's waves states TRP and EIRP at HB's own drive: at every sweep point EIRP is the gain times the watts HB delivers
    /// into the elements, and TRP those watts times the radiation efficiency (they read ~9 dBm whatever Pin was, at the record's 0 dBm).</summary>
    [Fact]
    public void TheRunsTrpAndEirp_AreAtHbsOwnDrive()
    {
        var run = CircuitEvaluation.Evaluate(PreparedCircuit.FromSchematic(Csch), new CircuitEvaluationRequest { Analyses = ["F1"], Measurements = false });
        Assert.True(run.GroupedResults is not null, run.StatusMessage);
        var c = run.GroupedResults.CubesIn("F1");
        double[] V(string name) => c[name].RealValues;
        double[] trp = V("farfield.TrpDbm"), eirp = V("farfield.PeakEirpDbm"), gain = V("farfield.GainDbi"), eff = V("farfield.RadiationEfficiency"),
                 pdel = V("array.Pdel");
        Assert.Equal(12, eirp.Length);
        Assert.Equal(48, pdel.Length);
        for (int i = 0; i < eirp.Length; i++)
        {
            double deliveredDbm = 10 * Math.Log10(pdel.Skip(4 * i).Take(4).Sum() * 1e3);
            Assert.Equal(gain[i] + deliveredDbm, eirp[i], 1e-6);
            Assert.Equal(deliveredDbm + 10 * Math.Log10(eff[i] / 100), trp[i], 1e-6);
        }
        Assert.InRange(eirp.Max() - eirp.Min(), 40, 90);   // the drive runs from -20 to 22 dBm
    }

    /// <summary>Gate 2 — the bench's own F1 is untouched by the tool: still a result at each of the 12 sweep points.</summary>
    [Fact]
    public void Gate2_TheBenchsOwnRun_StillSweeps()
    {
        var circuit = PreparedCircuit.FromSchematic(Csch);
        _ = new PhasedArraySession(circuit, "ARR").Evaluate(new Dictionary<string, string>(), interactive: false, null);
        Assert.All(circuit.Tb!.Analyses.OfType<ParametricSweepAnalysis>(), s => Assert.True(s.Enabled));
        var run = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest { Analyses = ["F1"], Measurements = false });
        Assert.True(run.GroupedResults is not null, run.StatusMessage);
        Assert.True(run.GroupedResults.CubesIn("F1").TryGetValue("farfield.RealizedGainDbi", out var cube), run.StatusMessage);
        Assert.Equal(12, cube.BufferLength);
    }

    /// <summary>R-paa12-1 — the array's steering is in its drives' Phase Shifters: a drag to 45° writes theta0 and leaves their φ0 literal as
    /// written, and the settled frame after it reads the README's PAE at 45° (37 % inner; 44 and 51 % outer).</summary>
    [Fact]
    public void ADrag_WritesTheta0_AndTheSettledFrameReadsTheReadmesPaeAt45()
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(Csch);
        model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters).Single(p => p.Name == "Pin").Expression = "22";
        var vm = new SchematicViewModel(model);
        var array = model.Components.Single(c => c.Symbol == SymbolKind.PhasedArray);
        using var tool = new PhasedArrayToolViewModel(vm, array, v =>
        {
            SchematicPersistence.SaveToFile(Csch, v.EditModel, Bench);
            return PreparedCircuit.FromSchematic(Csch);
        }, new ImmediatePhasedArrayHost());
        Assert.True(tool.Steering.Draggable, tool.Steering.Refusal);
        Assert.Equal(4, tool.Steering.Sites.Count);
        tool.Quantity = ApertureQuantity.PaPae;
        Assert.True(tool.BeginDrag());
        tool.DragToAngles(45, 0);
        tool.EndDrag();

        Assert.Equal("45", PhasedArraySteering.VarRow(model.Components, "theta0")!.Expression);
        Assert.All(tool.Steering.Sites, s => Assert.Contains("steer(theta0,0,", s.Text, StringComparison.Ordinal));
        Assert.Equal(ApertureQuantity.PaPae, tool.Quantity);
        var m = tool.Frame!.Measurements;
        Assert.Equal(37, (m["PAE2"] + m["PAE3"]) / 2, 0.5);
        Assert.Equal([44, 51], new[] { m["PAE1"], m["PAE4"] }.Order().Select(x => Math.Round(x)));
    }
}
