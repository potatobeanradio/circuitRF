using System;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

// Tests for brief-sweep-range-units (SweepSpec.Unit + CNL round-trip) are appended to this file.

/// <summary>
/// Gate tests for the parametric-sweep Start/Stop/Step|Npts/log CNL forms.
/// Covers the brief's required cases: CnlReader parses both forms, CnlWriter emits
/// compact form on round-trip, and the Values= list form still works (regression).
/// </summary>
public sealed class SweepSpecCnlTests
{
    // ── Helper: build minimal .cnl text and read it ───────────────────────────

    private static ParametricSweepAnalysis ReadPsa(string analysisLine)
    {
        // Minimal .cnl: one resistor + a named HB + the sweep directive under test.
        string cnl = $"""
            R:R1 1 0 R=50
            analysis HB1 type=hb Tone=1e9 MaxHarm=7
            analysis {analysisLine}
            """;

        var (_, tb) = new CnlReader().Read(cnl);
        var psa = tb.Analyses.OfType<ParametricSweepAnalysis>().FirstOrDefault()
            ?? throw new InvalidOperationException("No ParametricSweepAnalysis found.");
        return psa;
    }

    // ── CnlReader_StartStopStep ───────────────────────────────────────────────

    [Fact]
    public void CnlReader_StartStopStep_121Points()
    {
        // Pin = −30 dBm to 0 dBm, step 0.25 → 121 points
        var psa = ReadPsa("SW1 type=parametric_sweep Var=Pin Start=-30 Stop=0 Step=0.25 Inner=HB1");

        Assert.Equal(121, psa.SweepValues.Length);
        Assert.Equal(-30.0, psa.SweepValues[0],  precision: 9);
        Assert.Equal(  0.0, psa.SweepValues[^1], precision: 9);
        Assert.NotNull(psa.Spec);
        Assert.Equal(SweepAxisMode.StepSize, psa.Spec!.Mode);
        Assert.Equal(SweepKind.Linear,       psa.Spec.Kind);
    }

    // ── CnlReader_StartStopNpts ───────────────────────────────────────────────

    [Fact]
    public void CnlReader_StartStopNpts_7Points_Linspace()
    {
        // 7 points linearly spaced from −20 to 10
        var psa = ReadPsa("SW1 type=parametric_sweep Var=Pin Start=-20 Stop=10 Npts=7 Inner=HB1");

        Assert.Equal(7, psa.SweepValues.Length);
        Assert.Equal(-20.0, psa.SweepValues[0],  precision: 9);
        Assert.Equal( 10.0, psa.SweepValues[^1], precision: 9);
        // Midpoint: index 3 → −20 + 3*(30/6) = −5
        Assert.Equal(-5.0, psa.SweepValues[3], precision: 9);
        Assert.NotNull(psa.Spec);
        Assert.Equal(SweepAxisMode.PointCount, psa.Spec!.Mode);
        Assert.Equal(SweepKind.Linear,          psa.Spec.Kind);
    }

    // ── CnlReader_Log ─────────────────────────────────────────────────────────

    [Fact]
    public void CnlReader_Log_FourDecades()
    {
        // log sweep 1→1000 with 4 points → {1, 10, 100, 1000}
        var psa = ReadPsa("SW1 type=parametric_sweep Var=Freq Start=1 Stop=1000 Npts=4 log Inner=HB1");

        Assert.Equal(4, psa.SweepValues.Length);
        Assert.Equal(   1.0, psa.SweepValues[0], precision: 6);
        Assert.Equal(  10.0, psa.SweepValues[1], precision: 4);
        Assert.Equal( 100.0, psa.SweepValues[2], precision: 3);
        Assert.Equal(1000.0, psa.SweepValues[3], precision: 2);
        Assert.NotNull(psa.Spec);
        Assert.Equal(SweepKind.Log, psa.Spec!.Kind);
    }

    // ── CnlReader_Log_EqualsTrue ──────────────────────────────────────────────

    [Fact]
    public void CnlReader_Log_EqualsTrue_Keyword()
    {
        // log=true form (alternative to bare log keyword)
        var psa = ReadPsa("SW1 type=parametric_sweep Var=Freq Start=1 Stop=100 Npts=3 log=true Inner=HB1");

        Assert.Equal(3, psa.SweepValues.Length);
        Assert.NotNull(psa.Spec);
        Assert.Equal(SweepKind.Log, psa.Spec!.Kind);
    }

    // ── CnlReader_ValuesStillWorks ────────────────────────────────────────────

    [Fact]
    public void CnlReader_ValuesStillWorks_Regression()
    {
        // Explicit Values= form must still parse correctly and produce no Spec.
        var psa = ReadPsa("SW1 type=parametric_sweep Var=Pin Values=-3.0,-3.2 Inner=HB1");

        Assert.Equal(2, psa.SweepValues.Length);
        Assert.Equal(-3.0, psa.SweepValues[0], precision: 9);
        Assert.Equal(-3.2, psa.SweepValues[1], precision: 9);
        Assert.Null(psa.Spec);
    }

    // ── T1: SweepSpec_AppliesUnit_StepSize ───────────────────────────────────

    [Fact]
    public void SweepSpec_AppliesUnit_StepSize()
    {
        // 1..5 GHz step 1 → 5 points at 1e9..5e9
        var psa = new ParametricSweepAnalysis("SW", "F",
            new SweepSpec(1, 5, 1, SweepAxisMode.StepSize, unit: "GHz"), "HB1");

        Assert.Equal(5, psa.SweepValues.Length);
        Assert.Equal(1e9, psa.SweepValues[0], precision: 0);
        Assert.Equal(5e9, psa.SweepValues[^1], precision: 0);

        // Empty unit → base-unit pass-through (back-compat).
        var psaBase = new ParametricSweepAnalysis("SW", "F",
            new SweepSpec(1, 5, 1, SweepAxisMode.StepSize), "HB1");
        Assert.Equal(5, psaBase.SweepValues.Length);
        Assert.Equal(1.0, psaBase.SweepValues[0], precision: 9);
        Assert.Equal(5.0, psaBase.SweepValues[^1], precision: 9);
    }

    // ── T2: SweepSpec_AppliesUnit_PointCount ──────────────────────────────────

    [Fact]
    public void SweepSpec_AppliesUnit_PointCount()
    {
        // 1..5 GHz, 5 pts → [1e9,2e9,3e9,4e9,5e9]; count (5) is NOT scaled.
        var psa = new ParametricSweepAnalysis("SW", "F",
            new SweepSpec(1, 5, 5, SweepAxisMode.PointCount, unit: "GHz"), "HB1");

        Assert.Equal(5, psa.SweepValues.Length);
        Assert.Equal(1e9, psa.SweepValues[0], precision: 0);
        Assert.Equal(2e9, psa.SweepValues[1], precision: 0);
        Assert.Equal(5e9, psa.SweepValues[^1], precision: 0);
    }

    // ── T3: Sweep_Cnl_RoundTrip_Unit ─────────────────────────────────────────

    [Fact]
    public void Sweep_Cnl_RoundTrip_Unit()
    {
        var psa = ReadPsa("SW1 type=parametric_sweep Var=RFfreq Start=1 Stop=5 Step=1 Unit=GHz Inner=HB1");

        Assert.NotNull(psa.Spec);
        Assert.Equal("GHz", psa.Spec!.Unit);
        // Coefficients stored unscaled.
        Assert.Equal(1.0, psa.Spec.Start,       precision: 9);
        Assert.Equal(5.0, psa.Spec.Stop,        precision: 9);
        Assert.Equal(1.0, psa.Spec.StepOrCount, precision: 9);
        // Materialized values are base-unit.
        Assert.Equal(1e9, psa.SweepValues[0],  precision: 0);
        Assert.Equal(5e9, psa.SweepValues[^1], precision: 0);

        // Round-trip: write → read → unit preserved; values identical.
        var tb = new TestBench("tb");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "1e9" });
        tb.Analyses.Add(psa);
        string cnl = CnlWriter.Write(tb);

        Assert.Contains("Unit=GHz", cnl, StringComparison.Ordinal);

        var (_, tb2)  = new CnlReader().Read(cnl);
        var psa2      = tb2.Analyses.OfType<ParametricSweepAnalysis>().First();
        Assert.Equal("GHz",  psa2.Spec!.Unit);
        Assert.Equal(1e9, psa2.SweepValues[0],  precision: 0);
        Assert.Equal(5e9, psa2.SweepValues[^1], precision: 0);

        // A file without Unit= reads Unit="" and expands to base.
        var psaBase = ReadPsa("SW1 type=parametric_sweep Var=F Start=1 Stop=5 Step=1 Inner=HB1");
        Assert.Equal("", psaBase.Spec!.Unit);
        Assert.Equal(1.0, psaBase.SweepValues[0],  precision: 9);
        Assert.Equal(5.0, psaBase.SweepValues[^1], precision: 9);
    }

    // ── Roundtrip_Spec ────────────────────────────────────────────────────────

    [Fact]
    public void Roundtrip_Spec_CompactFormPreserved()
    {
        // Read Start/Stop/Step → write → read again → same SweepValues, compact form.
        var psaOriginal = ReadPsa("SW1 type=parametric_sweep Var=Pin Start=-30 Stop=0 Step=0.25 Inner=HB1");

        Assert.NotNull(psaOriginal.Spec);
        Assert.Equal(121, psaOriginal.SweepValues.Length);

        // Build a TestBench and serialize it.
        var tb = new TestBench("tb");
        tb.Analyses.Add(new HarmonicBalanceAnalysis("HB1") { ToneExpr = "1e9", MaxHarmonicExpr = "7" });
        tb.Analyses.Add(psaOriginal);
        string cnl = CnlWriter.Write(tb);

        // Should emit compact form, not a 121-number Values= list.
        Assert.Contains("Start=-30", cnl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop=0",    cnl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Step=0.25", cnl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Values=", cnl, StringComparison.OrdinalIgnoreCase);

        // Re-read and verify round-trip fidelity.
        var (_, tb2) = new CnlReader().Read(cnl);
        var psa2     = tb2.Analyses.OfType<ParametricSweepAnalysis>().First();

        Assert.Equal(121,   psa2.SweepValues.Length);
        Assert.Equal(-30.0, psa2.SweepValues[0],  precision: 9);
        Assert.Equal(  0.0, psa2.SweepValues[^1], precision: 9);
        Assert.NotNull(psa2.Spec);
        Assert.Equal(SweepAxisMode.StepSize, psa2.Spec!.Mode);
        Assert.Equal(SweepKind.Linear,        psa2.Spec.Kind);
        Assert.Equal(-30.0, psa2.Spec.Start,        precision: 9);
        Assert.Equal(  0.0, psa2.Spec.Stop,         precision: 9);
        Assert.Equal( 0.25, psa2.Spec.StepOrCount,  precision: 9);
    }
}
