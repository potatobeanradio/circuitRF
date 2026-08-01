using System;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// A frequency-dependent value crossing a cell boundary.
///
/// <para><b>Why this shape.</b> A kit's frequency-dependent transmission line computes its RLGC —
/// skin effect, dielectric loss — in ordinary cell variables and passes them DOWN into the model
/// that evaluates per frequency. The elaborator resolves parameters once, with no frequency bound,
/// so the value has to survive the trip as an expression. These fixtures reproduce that two-level
/// shape with a network whose answer is known in closed form.</para>
///
/// <para>Every result here is checked against the analytic response of the network the chain matrix
/// describes, so the deferral is verified against network theory rather than against itself.</para>
/// </summary>
public class FreqDependentCellParameterTests
{
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Two cells deep, mirroring a kit: OUTER computes a frequency-dependent value in a cell
    /// variable and passes it as an ARGUMENT to INNER, which hands it to the Chain. The override
    /// `Bx=Bexpr` is the exact construct that cannot be evaluated at elaboration time.
    /// </summary>
    private static string SeriesInductorThroughTwoCells(double henries) => $@"
define INNER (a b)
  parameters Bx=0
  Chain:CH  a 0 b 0  A=1  B=Bx  C=0  D=1
end INNER

define OUTER (a b)
  parameters L={N(henries)}
  Bexpr = complex(0.0, 2*pi*freq*L)
  INNER:I1  a b  Bx=Bexpr
end OUTER

Port:P1  in  0   Num=1 Z=50
OUTER:X1 in out
Port:P2  out 0   Num=2 Z=50
";

    private static DataSet RunSp(string cnl, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), freqs);
    }

    /// <summary>|S21| of a series L between two Z0 ports: 2·Z0 / (2·Z0 + jωL).</summary>
    private static double AnalyticS21(double henries, double freqHz, double z0 = 50.0)
        => (2 * z0 / new Complex(2 * z0, 2 * Math.PI * freqHz * henries)).Magnitude;

    // ── The headline ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1e-9,  1e9)]
    [InlineData(1e-9,  10e9)]
    [InlineData(5e-9,  2e9)]
    [InlineData(1e-10, 20e9)]
    public void AFreqDependentValueCrossesACellBoundary_AndMatchesTheAnalyticResponse(
        double henries, double freqHz)
    {
        var ds = RunSp(SeriesInductorThroughTwoCells(henries), [freqHz]);

        double got = ((Complex)ds["S"][0, 1, 0]).Magnitude;

        Assert.Equal(AnalyticS21(henries, freqHz), got, 9);
    }

    [Fact]
    public void TheResponseGenuinelyVariesWithFrequency()
    {
        // A deferred expression that quietly collapsed to a constant would still elaborate, still
        // stamp, and still look plausible — it would just be flat. This is the check that catches it.
        double[] f = [1e9, 5e9, 10e9, 20e9];
        var ds = RunSp(SeriesInductorThroughTwoCells(2e-9), f);

        for (int i = 0; i < f.Length; i++)
            Assert.Equal(AnalyticS21(2e-9, f[i]), ((Complex)ds["S"][i, 1, 0]).Magnitude, 9);

        // Strictly decreasing: a series inductor's transmission falls with frequency.
        for (int i = 1; i < f.Length; i++)
            Assert.True(((Complex)ds["S"][i, 1, 0]).Magnitude < ((Complex)ds["S"][i - 1, 1, 0]).Magnitude,
                $"|S21| did not fall between {f[i - 1]:G3} Hz and {f[i]:G3} Hz.");
    }

    /// <summary>
    /// Three levels, with the dependence introduced at the top and consumed at the bottom — the kit's
    /// own depth. A one-level fixture would pass even if inlining never recursed.
    /// </summary>
    [Fact]
    public void DependenceSurvivesSeveralCellBoundaries()
    {
        const double L = 3e-9;
        string cnl = $@"
define L3 (a b)
  parameters Bx=0
  Chain:CH  a 0 b 0  A=1  B=Bx  C=0  D=1
end L3

define L2 (a b)
  parameters By=0
  L3:I1  a b  Bx=By
end L2

define L1 (a b)
  parameters L={N(L)}
  Bexpr = complex(0.0, 2*pi*freq*L)
  L2:I1  a b  By=Bexpr
end L1

Port:P1  in  0   Num=1 Z=50
L1:X1    in out
Port:P2  out 0   Num=2 Z=50
";
        var ds = RunSp(cnl, [7e9]);
        Assert.Equal(AnalyticS21(L, 7e9), ((Complex)ds["S"][0, 1, 0]).Magnitude, 9);
    }

    // ── Units ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AUnitOnADeferredBinding_IsAppliedExactlyOnce()
    {
        // Applying it twice is the natural bug here: inlining absorbs the unit of each binding it
        // walks through, and the site unit would then be applied again on top.
        string cnl = @"
define INNER (a b)
  parameters Bx=0
  Chain:CH  a 0 b 0  A=1  B=Bx  C=0  D=1
end INNER

define OUTER (a b)
  parameters L=2 nH
  Bexpr = complex(0.0, 2*pi*freq*L)
  INNER:I1  a b  Bx=Bexpr
end OUTER

Port:P1  in  0   Num=1 Z=50
OUTER:X1 in out
Port:P2  out 0   Num=2 Z=50
";
        var ds = RunSp(cnl, [4e9]);

        Assert.Equal(AnalyticS21(2e-9, 4e9), ((Complex)ds["S"][0, 1, 0]).Magnitude, 9);
    }

    // ── Termination ───────────────────────────────────────────────────────────

    [Fact]
    public void AFreqDependentValueReachingAnOrdinaryDevice_IsRefusedByName()
    {
        // A resistor takes one number. Saying so — naming the device and the parameter — beats the
        // bare "Unresolved name 'freq'" the evaluator would otherwise report from inside the value.
        string cnl = @"
define OUTER (a b)
  Rexpr = 50 + freq * 1e-9
  R:R1  a b  R=Rexpr
end OUTER

Port:P1  in  0   Num=1 Z=50
OUTER:X1 in out
Port:P2  out 0   Num=2 Z=50
";
        var (lib, tb) = new CnlReader().Read(cnl);

        var ex = Assert.Throws<FrequencyDependentValueException>(
            () => new Elaborator(lib).Elaborate(tb));

        Assert.Contains("R1", ex.Message);
        Assert.Contains("'R'", ex.Message);
        Assert.Contains("Chain", ex.Message);      // names where it CAN go
    }

    /// <summary>
    /// A Chain/Z_Port expression mentions <c>freq</c> BY DEFINITION — that is what the parameter is —
    /// so a rule that keys on "mentions freq" fires on every one of them and folds their ordinary
    /// scope variables into literals. That is numerically harmless and still wrong: those variables
    /// are injected by name, and rewriting expressions that were never a problem is how a fix for one
    /// kit breaks every existing design. Only a name that is ITSELF frequency-dependent may be
    /// inlined.
    /// </summary>
    [Fact]
    public void AnOrdinaryVariableInAChainExpression_StaysAReference_AndIsInjectedByName()
    {
        // No space inside the value: the generic instance-line parser splits on whitespace and
        // would read the rest as extra nets (src/Core/CLAUDE.md).
        string cnl = @"
Lval = 4e-9
Port:P1  in  0   Num=1 Z=50
Chain:CH in 0 out 0  A=1  B=complex(0.0,2*pi*freq*Lval)  C=0  D=1
Port:P2  out 0   Num=2 Z=50
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var chain = System.Linq.Enumerable.Single(
            nl.Components, c => c.Model is CircuitRF.Core.Devices.ChainModel);

        Assert.Contains("Lval", chain.Parameters["B"].AsString());   // not folded away
        Assert.True(chain.Parameters.ContainsKey("Lval"),            // and injected by name
            "the scope variable was not injected alongside the expression");

        // And it still gives the right answer.
        var ds = SParameterEngine.Run(nl, [6e9]);
        Assert.Equal(AnalyticS21(4e-9, 6e9), ((Complex)ds["S"][0, 1, 0]).Magnitude, 9);
    }

    // ── No regression for everything that is not frequency-dependent ──────────

    [Fact]
    public void AnOrdinaryCellHierarchy_IsUnaffected()
    {
        // The same two-level shape with a constant B must give the plain series-resistance answer,
        // proving the deferral path is not entered and changes nothing when it is not needed.
        string cnl = @"
define INNER (a b)
  parameters Bx=0
  Chain:CH  a 0 b 0  A=1  B=Bx  C=0  D=1
end INNER

define OUTER (a b)
  parameters R0=25
  Bexpr = R0 * 2
  INNER:I1  a b  Bx=Bexpr
end OUTER

Port:P1  in  0   Num=1 Z=50
OUTER:X1 in out
Port:P2  out 0   Num=2 Z=50
";
        var ds = RunSp(cnl, [1e9, 10e9]);

        // Series 50 Ω between 50 Ω ports: |S21| = 2·50/(2·50 + 50) = 2/3, flat in frequency.
        Assert.Equal(2.0 / 3.0, ((Complex)ds["S"][0, 1, 0]).Magnitude, 9);
        Assert.Equal(2.0 / 3.0, ((Complex)ds["S"][1, 1, 0]).Magnitude, 9);
    }
}
