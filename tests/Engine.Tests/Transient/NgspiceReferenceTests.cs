using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Transient;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Transient;

/// <summary>
/// brief TRN-7 R-trn7-9, gate 5 — the native transient engine against ngspice, externally generated: each bench in
/// <c>testdata/transient/ngspice/</c> was written as a deck by circuitRF's translation and run once by ngspice, which put its own
/// solution on the output grid (<c>linearize</c>); <c>generate.py</c> beside them is the script and <c>ngspice-version.txt</c> the
/// version. These tests need no ngspice. Two adaptive-step engines agree to their tolerances, not to round-off, so each circuit's
/// bound is stated with its reason; the measured worst case is printed.
/// </summary>
public sealed class NgspiceReferenceTests(ITestOutputHelper output)
{
    private static string TestData(params string[] parts)
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "transient", "ngspice")))
                return Path.Combine([dir, "testdata", .. parts]);
        throw new DirectoryNotFoundException("testdata/transient/ngspice not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The worst |native − ngspice| over every grid point and recorded net, as a fraction of the bench's largest swing,
    /// skipping <paramref name="skipBefore"/> seconds at the start.</summary>
    private double WorstError(string name, double skipBefore = 0)
    {
        var (lib, tb) = new CnlReader().Read(File.ReadAllText(TestData("transient", "ngspice", name + ".cnl")));
        var nl = new Elaborator(lib).Elaborate(tb);
        var r = new TransientEngine(nl).Run(TransientRunner.Resolve(tb.Analyses.OfType<TransientAnalysis>().Single(), nl.ResolvedGlobals));

        var lines = File.ReadAllLines(TestData("transient", "ngspice", name + ".csv"));
        var header = lines[0].Split(',');
        var rows = lines.Skip(1).Select(l => l.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Assert.Equal(r.Time.Length, rows.Length);

        double swing = 0, worst = 0, at = 0;
        string where = "";
        for (int c = 1; c < header.Length; c++)
        {
            int n = Array.IndexOf(r.NodeNames, header[c]);
            Assert.True(n >= 0, $"{name}: net {header[c]} is not recorded natively ({string.Join(", ", r.NodeNames)})");
            for (int k = 0; k < rows.Length; k++)
            {
                Assert.Equal(r.Time[k], rows[k][0], 1e-15);
                swing = Math.Max(swing, Math.Abs(rows[k][c]));
                if (r.Time[k] < skipBefore) continue;
                double e = Math.Abs(r.V[k][n] - rows[k][c]);
                if (e > worst) { worst = e; at = r.Time[k]; where = header[c]; }
            }
        }
        output.WriteLine($"{name}: worst |native − ngspice| {worst:E3} V of a {swing:G4} V swing ({worst / swing:E2}) at {where}, t = {at:E4} s");
        return worst / swing;
    }

    // Each bound is a few times the worst difference measured against ngspice 47 (in brackets, as a fraction of the swing), and
    // says why the two engines differ at all. A bench started from zero (InitialState=zero, the deck's uic) skips its first point:
    // circuitRF reports the zero state there, and ngspice's first point already has the sources applied.

    /// <summary>RC step [2.5e-6]: one exponential, which both engines resolve far inside RelTol 1e-3; what remains is where each puts
    /// its steps about the 1 ns edge.</summary>
    [Fact]
    public void Rc() => Assert.InRange(WorstError("rc"), 0, 1e-4);

    /// <summary>RLC ring, Q ≈ 6 [3.4e-5]: the ring's phase error accumulates cycle by cycle at each engine's RelTol.</summary>
    [Fact]
    public void Rlc() => Assert.InRange(WorstError("rlc"), 0, 5e-4);

    /// <summary>Half-wave rectifier [1.0e-4]: where each engine puts the diode's turn-on and turn-off decides the conduction
    /// angle.</summary>
    [Fact]
    public void Rectifier() => Assert.InRange(WorstError("rectifier"), 0, 1e-3);

    /// <summary>Buck converter [1.2e-3]: each switching edge is a 10 ns ramp on a 10 ns grid, and the two engines step through the
    /// switch's smoothstep and the diode's turn-on differently; the output's ripple carries that.</summary>
    [Fact]
    public void Buck() => Assert.InRange(WorstError("buck", skipBefore: 1e-15), 0, 1e-2);

    /// <summary>Lossless line [1.7e-2]: both run it as an exact delay; ngspice's points straddle the delayed edge's corners, and
    /// linearize joins them with a straight line, which puts a 0.1 ns edge's corner a few picoseconds early.</summary>
    [Fact]
    public void LosslessLine() => Assert.InRange(WorstError("line"), 0, 3e-2);

    /// <summary>Ring of five MOS level-1 inverters [1.3e-4]: a free-running oscillation, whose phase drifts between any two solvers;
    /// over its 8 periods the drift stays small, and a different or stopped oscillation would be a full swing.</summary>
    [Fact]
    public void MosRing() => Assert.InRange(WorstError("ring", skipBefore: 1e-15), 0, 2e-3);
}
