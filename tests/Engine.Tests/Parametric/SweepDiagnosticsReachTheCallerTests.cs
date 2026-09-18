using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Parametric;

/// <summary>
/// A warning raised inside a parametric sweep reaches the netlist the caller is holding.
///
/// <para><b>Nothing did, before this.</b> A sweep point is one re-elaboration plus one inner run —
/// the re-elaboration is the whole mechanism by which a swept variable reaches the circuit — so the
/// netlist every engine writes its warnings to is a fresh one, disposed at the end of the iteration.
/// The caller is left holding the netlist it elaborated in order to pick the analysis, which nothing
/// ever stamped. Both CLI run verbs and <c>SchematicRunService</c> read THAT one to fill the
/// Messages panel, so a swept run reported no engine diagnostics at all: not a non-convergence note,
/// not a microstrip outside its validity range, not a System block that is not passive.
///
/// <para><b>The fixture raises a diagnostic only an ENGINE can raise</b> — <c>sparam-zero-bias</c>,
/// which the S-parameter engine writes after solving the operating point and finding every node at
/// zero. Elaboration cannot produce it, so the caller's netlist is provably without it until a point
/// puts it there. A passivity report would not do: that one IS raised at elaboration, so the caller
/// would hold it either way and the test would pass against the bug.</para>
/// </summary>
public class SweepDiagnosticsReachTheCallerTests(ITestOutputHelper output)
{
    // The MESSAGE, not the key: AddWarningOnce keeps the key for its own dedup and publishes only
    // the sentence, so a test that matched on the key would pass vacuously.
    private const string Key = "No DC bias present";

    // The PIM level makes the circulator nonlinear, which is what gives the run an operating point
    // to solve at all; its own numbers are passive, so the only message in play is the engine's.
    private const string Cnl = """
        Pdrv = -20

        Port:P1 a 0 Num=1 Z=50 Ohm
        Circulator:CR1 a 0 b 0 c 0 Direction=CW IL=0.6 dB Isolation=30 dB RL=32 dB PIM=-67 dBm PIMPc=43 dBm
        Port:P2 b 0 Num=2 Z=50 Ohm
        R:Rc c 0 R=50 Ohm

        analysis SP1 type=sparam start=1 stop=2 npts=3 Unit=GHz
        analysis SWEEP type=parametric_sweep Var=Pdrv Start=-20 Stop=-16 Step=1 Inner=SP1
        """;

    private static (Library Lib, TestBench Tb, ParametricSweepAnalysis Sweep) Read()
    {
        var (lib, tb) = new CnlReader().Read(Cnl);
        return (lib, tb, (ParametricSweepAnalysis)tb.Analyses.First(a => a.Name == "SWEEP"));
    }

    [Fact]
    public void AWarningRaisedInsideASweep_ArrivesOnceOnTheCallersNetlist()
    {
        var (lib, tb, sweep) = Read();

        // Exactly what a run verb and the GUI do: elaborate once to pick the analysis, then sweep.
        using var caller = new Elaborator(lib).Elaborate(tb);
        Assert.DoesNotContain(caller.Warnings, w => w.Contains(Key, StringComparison.Ordinal));

        ParametricSweepEngine.Run(sweep, lib, tb, diagnosticsInto: caller);

        foreach (string w in caller.Warnings) output.WriteLine(w);

        int reported = caller.Warnings.Count(w => w.Contains(Key, StringComparison.Ordinal));
        Assert.True(reported == 1, $"expected the 5 points' shared warning once, got {reported}.");
    }

    /// <summary>Omitting the netlist keeps the old behaviour, which is what every test caller — and
    /// anything measuring an engine rather than reporting to a person — wants.</summary>
    [Fact]
    public void WithoutACallersNetlist_NothingIsCollected()
    {
        var (lib, tb, sweep) = Read();

        using var caller = new Elaborator(lib).Elaborate(tb);
        ParametricSweepEngine.Run(sweep, lib, tb);

        Assert.DoesNotContain(caller.Warnings, w => w.Contains(Key, StringComparison.Ordinal));
    }
}
