using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Expressions;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// <c>at(x, "axis", index)</c> — pinning one axis BY NAME.
///
/// The case it exists for: an AM-PM measurement referenced to the first drive point. Written
/// positionally (<c>HB1.V("Vout", 1, 0)</c> or <c>HB1.V[0, "Vout", 1]</c>) the reference addresses a
/// POSITION, so adding an outer RFfreq sweep either breaks the expression or silently moves the pin
/// to the wrong axis. These tests run ONE expression against both cube shapes.
/// </summary>
public class AtAxisPinTests
{
    // Phase of V("Vout", 1) is chosen so AM-PM has an exact expected answer:
    //   phase = 10·pinIndex + 1·freqIndex  (degrees)
    // so AM-PM referenced to Pin index 0 is 0, 10, 20 at every frequency.
    private const int NPin = 3, NFreq = 2;

    private static Complex Phasor(double degrees) =>
        Complex.FromPolarCoordinates(1.0, degrees * Math.PI / 180.0);

    /// <summary>HB1.V[Pin, node, harmonic] — one sweep, the shape the owner started from.</summary>
    private static (MeasurementContext ctx, Scope scope) OneSweep()
    {
        var pin  = new Axis("Pin",      [-10.0, -5.0, 0.0], "dBm");
        var node = new Axis("node",     [0.0, 1.0], "", ["Vout", "Vin"]);
        var harm = new Axis("harmonic", [0.0, 1.0]);

        var data = new Complex[NPin * 2 * 2];
        for (int p = 0; p < NPin; p++)
            data[p * 4 + 0 * 2 + 1] = Phasor(10.0 * p);      // node Vout, harmonic 1

        var ds = new DataSet();
        ds.Add("V", new DataCube([pin, node, harm], data));
        return (new MeasurementContext(new Dictionary<string, DataSet> { ["HB1"] = ds }), new Scope("t"));
    }

    /// <summary>HB1.V[RFfreq, Pin, node, harmonic] — the same run with an outer frequency sweep.</summary>
    private static (MeasurementContext ctx, Scope scope) TwoSweeps()
    {
        var freq = new Axis("RFfreq",   [2.0e9, 2.4e9], "Hz");
        var pin  = new Axis("Pin",      [-10.0, -5.0, 0.0], "dBm");
        var node = new Axis("node",     [0.0, 1.0], "", ["Vout", "Vin"]);
        var harm = new Axis("harmonic", [0.0, 1.0]);

        var data = new Complex[NFreq * NPin * 2 * 2];
        for (int f = 0; f < NFreq; f++)
            for (int p = 0; p < NPin; p++)
                data[((f * NPin + p) * 2 + 0) * 2 + 1] = Phasor(10.0 * p + f);

        var ds = new DataSet();
        ds.Add("V", new DataCube([freq, pin, node, harm], data));
        return (new MeasurementContext(new Dictionary<string, DataSet> { ["HB1"] = ds }), new Scope("t"));
    }

    private static Value Eval(string expr, MeasurementContext ctx, Scope scope)
        => new Evaluator(ctx).Eval(expr, scope);

    // The measurement pair, verbatim — one text, both shapes.
    private const string TransPhase = "phase(HB1.V(\"Vout\", 1))";
    private const string Ampm       = "phase(HB1.V(\"Vout\", 1)) - at(phase(HB1.V(\"Vout\", 1)), \"Pin\", 0)";

    // ── The point of the whole exercise ──────────────────────────────────────

    [Fact]
    public void SameExpression_OneSweep_ReferencesTheFirstPinPoint()
    {
        var (ctx, scope) = OneSweep();
        var v = Eval(Ampm, ctx, scope);

        var cube = v.AsCube();
        Assert.Equal(1, cube.Rank);
        Assert.Equal("Pin", cube.Axes[0].Name);
        Assert.Equal([0.0, 10.0, 20.0], cube.RealValues.Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void SameExpression_TwoSweeps_ReferencesPerFrequency()
    {
        var (ctx, scope) = TwoSweeps();
        var v = Eval(Ampm, ctx, scope);

        var cube = v.AsCube();
        Assert.Equal(2, cube.Rank);
        Assert.Equal("RFfreq", cube.Axes[0].Name);
        Assert.Equal("Pin",    cube.Axes[1].Name);

        // Each frequency's own first Pin point is the reference — the +1°/frequency offset cancels
        // within each curve rather than leaking across them (which is what a single scalar reference
        // would have done).
        Assert.Equal([0.0, 10.0, 20.0, 0.0, 10.0, 20.0],
                     cube.RealValues.Select(x => Math.Round(x, 9)));
    }

    [Fact]
    public void At_KeepsTheOtherSweep_ItDoesNotCollapseToOneNumber()
    {
        var (ctx, scope) = TwoSweeps();
        var refCube = Eval($"at({TransPhase}, \"Pin\", 0)", ctx, scope).AsCube();

        Assert.Equal(1, refCube.Rank);
        Assert.Equal("RFfreq", refCube.Axes[0].Name);
        Assert.Equal([0.0, 1.0], refCube.RealValues.Select(x => Math.Round(x, 9)));
    }

    // ── Negative indexing ────────────────────────────────────────────────────

    [Fact]
    public void NegativeIndex_CountsFromTheEnd()
    {
        var (ctx, scope) = OneSweep();
        var last  = Eval($"at({TransPhase}, \"Pin\", -1)", ctx, scope);
        var first = Eval($"at({TransPhase}, \"Pin\", 0)",  ctx, scope);

        Assert.Equal(20.0, ((DataCube)last.AsCube()).RealValues[0],  9);
        Assert.Equal( 0.0, ((DataCube)first.AsCube()).RealValues[0], 9);

        // -1 on a 3-point axis is index 2; -3 is index 0.
        Assert.Equal(0.0, Eval($"at({TransPhase}, \"Pin\", -3)", ctx, scope).AsCube().RealValues[0], 9);
    }

    [Fact]
    public void PinningTheOnlyAxis_YieldsARank0Cube_NotACrash()
    {
        // The no-RFfreq case reduced to a single number — this used to be a NullReferenceException
        // out of DataCube.At, i.e. the first thing a shape-independent expression would hit.
        var (ctx, scope) = OneSweep();
        var v = Eval($"at({TransPhase}, \"Pin\", 0)", ctx, scope);

        var cube = v.AsCube();
        Assert.Equal(0, cube.Rank);
        Assert.Equal(0.0, cube.RealValues[0], 9);
    }

    // ── Strictness: a mistake must never read as "zero everywhere" ───────────

    [Fact]
    public void UnknownAxis_IsAnErrorNamingTheAxesThatExist()
    {
        var (ctx, scope) = TwoSweeps();
        var ex = Assert.Throws<ExpressionException>(() =>
            Eval($"at({TransPhase}, \"Pinn\", 0)", ctx, scope));

        Assert.Contains("Pinn", ex.Message);
        Assert.Contains("RFfreq", ex.Message);   // says what IS there
        Assert.Contains("Pin", ex.Message);
    }

    [Fact]
    public void IndexOutOfRange_SaysTheUsableRange_BothDirections()
    {
        var (ctx, scope) = OneSweep();
        var ex = Assert.Throws<ExpressionException>(() =>
            Eval($"at({TransPhase}, \"Pin\", 7)", ctx, scope));

        Assert.Contains("Pin", ex.Message);
        Assert.Contains("0..2", ex.Message);
        Assert.Contains("-1..-3", ex.Message);
    }

    [Fact]
    public void ScalarArgument_IsRefused_RatherThanSilentlyReturningItself()
    {
        var (ctx, scope) = OneSweep();
        var ex = Assert.Throws<ExpressionException>(() => Eval("at(3.0, \"Pin\", 0)", ctx, scope));
        Assert.Contains("single number", ex.Message);
        Assert.Contains("Pin", ex.Message);
    }

    [Fact]
    public void WrongArity_IsReported()
    {
        var (ctx, scope) = OneSweep();
        Assert.ThrowsAny<Exception>(() => Eval($"at({TransPhase}, \"Pin\")", ctx, scope));
    }

    // ── It works on any cube-valued expression, not just an accessor ─────────

    [Fact]
    public void At_AppliesToABracketSliceToo()
    {
        var (ctx, scope) = TwoSweeps();
        var v = Eval("at(phase(HB1.V[:, :, \"Vout\", 1]), \"Pin\", -1)", ctx, scope);

        var cube = v.AsCube();
        Assert.Equal(1, cube.Rank);
        Assert.Equal("RFfreq", cube.Axes[0].Name);
        Assert.Equal([20.0, 21.0], cube.RealValues.Select(x => Math.Round(x, 9)));
    }
}
