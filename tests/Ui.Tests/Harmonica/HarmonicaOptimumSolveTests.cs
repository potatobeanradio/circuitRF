// ================================================================
//  HarmonicaOptimumSolveTests.cs — §2A (R-h9b-16/17) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
//  §2.4 (R9C) additions — brief-harmonicarf-r9c: a failed search never reports a fabricated optimum.
// ================================================================

using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaOptimumSolveTests(ITestOutputHelper output)
{
    [Fact]
    public void FullQualityFrame_SolvesTheOptimum_AndPublishesZin()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    Quality = FrameQuality.Full });

        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);
        Assert.NotNull(optimum!.Solved);
        Assert.NotNull(optimum.Published);
        Assert.True(optimum.Published!.Cubes.ContainsKey("Zin"), "the resolved optimum must publish Zin (§4.5.4)");

        output.WriteLine($"Power optimum Γ={optimum.Gamma:G6}, value={optimum.MetricValue:F3}, " +
                         $"solved Pin={optimum.Solved!.PavlDbm:F2} dBm");
    }

    [Fact]
    public void DegradedRung_TracksTheGlyphPosition_ButDoesNotSolveFoms()
    {
        var vm = new HarmonicaViewModel();
        // A coarse, dragging-style rung: the glyph should still track the interpolated surface (cheap,
        // no HB solve), but the expensive FOM drive-up must not run.
        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64,
            Quality = FrameQuality.CoarseGrid,
        });

        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);
        Assert.Null(optimum!.Solved);
        Assert.Null(optimum.Published);
    }

    [Fact]
    public void SkipContoursFrame_HasNoOptimum_NotAStaleOne()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        Assert.Null(vm.Frame.SmithPower.Optimum);
        Assert.Null(vm.Frame.SmithEfficiency.Optimum);
    }

    [Fact]
    public void MeasuredCost_TheOptimumSolvesCostRoughlyTwoDriveUps()
    {
        var vm = new HarmonicaViewModel();

        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64, SkipContours = true,
        });
        int gridOnly = vm.LastSolveCount;

        vm.SolveFrame(new HarmonicaSolver.Options
        {
            Rings = 3, Spokes = 12, RasterResolution = 64, Quality = FrameQuality.Full,
        });
        int withOptima = vm.LastSolveCount;

        output.WriteLine($"tier-A-only solves: {gridOnly}; full grid + two optimum drive-ups: {withOptima}");
        Assert.True(withOptima > gridOnly,
            "a full-quality frame with two resolved optima must cost more HB solves than tier A alone");
    }

    // ── R9C §2.4 — the gate for §2's whole rewrite ─────────────────────────────

    [Fact]
    public void MxColumn_PoutAgreesWithTheOperatingPointColumn_AtTheSameGamma()
    {
        // The owner's own exact test, made a gate: move L1 to the frame's own reported optimum Γ, so
        // the strip's operating-point column and the MX column are reading THE SAME termination — the
        // two must then report the identical Pout, because R9C §2.2 makes them the identical function
        // call (PinSearch.Sweep at the document's own ladder settings) rather than two different
        // searches that happen to usually agree.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    Quality = FrameQuality.Full });

        var optimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(optimum);
        Assert.NotNull(optimum!.Solved);

        var l1 = vm.Markers.Single(m => m.Side == TerminationSideKind.Load && m.Band == 1);
        vm.SetMarkerGamma(l1, optimum.Gamma);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    Quality = FrameQuality.Full });

        var mxpPout = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.Mxp && r.Label == "Pout");
        var opPout  = vm.Frame.Readouts.Single(r => r.Column == ReadoutColumn.OperatingPoint && r.Label == "Pout");

        double mxpDbm = ParseLeadingNumber(mxpPout.Value);
        double opDbm  = ParseLeadingNumber(opPout.Value);
        output.WriteLine($"MXP Pout={mxpPout.Value}, operating-point Pout={opPout.Value}");
        Assert.Equal(opDbm, mxpDbm, 2);   // 0.01 dB
    }

    [Fact]
    public void FailedSearch_YieldsNoSolved_ANonNullReason_AndATenRowAllDashColumn()
    {
        // R9C §2.1/§2.4 — a search that RAN this frame and did not reach the compression target must
        // never be reported as an answer. PinMaxDbm is set far below PinStart so the compression target
        // is unreachable ANYWHERE (mirrors ContourGridTests' own D4_ANonCompressingPointStopsAtPinMax-
        // AndSaysSo fixture) — a clean, deterministic PinStopReason.PinMax, independent of exactly
        // which Γ InterpolatedArgmax would naturally land on (measured to be a narrow, fixture-specific
        // band not worth chasing for a gate test — see this class's own remarks).
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Settings = HarmonicaViewModel.DefaultModel().Settings with { PinMaxDbm = -4.0 },
        };
        var ctx = HarmonicaContext.Create(model);
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(50, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 0));

        var opt = new HarmonicaSolver.Options { Quality = FrameQuality.Full };
        var seed = new SmithPanelData.SmithOptimum(new Complex(0.05, -0.02), MetricValue: 30.0,
                                                   Solved: null, Published: null);

        var solver = new HarmonicaSolver();
        var result = solver.SolveAtOptimum(ctx, terms, opt, seed);

        Assert.Null(result.Solved);
        Assert.Null(result.Published);
        Assert.Null(result.SolvedCompression);
        Assert.False(string.IsNullOrEmpty(result.UnsolvedReason));
        Assert.Contains("PinMax", result.UnsolvedReason, StringComparison.Ordinal);
        output.WriteLine($"UnsolvedReason: {result.UnsolvedReason}");

        // R7C §1.4 — the row SHAPE (ten rows, header + Pout/Eff/PAE/Gain/Gp/Zin/AM-PM/Pdc/γ) must be
        // IDENTICAL to the solved case, so the chunk cannot churn at frame rate as the search flips
        // between "ran and failed" and "solved". Only the header text and every tooltip say which case
        // this is.
        var rows = new System.Collections.Generic.List<HarmonicaReadout>();
        HarmonicaSolver.AddMxColumn(rows, ReadoutColumn.Mxp, "MXP", opt, result,
            HarmonicaReadoutFormatting.DefaultReadoutFormat, ctx);

        Assert.Equal(10, rows.Count);
        // The header row itself carries no tooltip (matching the pre-existing "no optimum" shape);
        // every SCALAR row's own tooltip is what states which case this is.
        Assert.Contains(result.UnsolvedReason, rows[1].Tooltip, StringComparison.Ordinal);
        string[] expectedLabels = ["Pout", "Eff", "PAE", "Gain", "Gp", "Zin", "AM/PM", "Pdc", "γ"];
        for (int i = 0; i < expectedLabels.Length; i++)
        {
            Assert.Equal(expectedLabels[i], rows[i + 1].Label);
            Assert.Equal("—", rows[i + 1].Value);
        }
    }

    [Fact]
    public void RowCount_IsIdenticalBetweenASolvedAndAFailedSearch()
    {
        // R7C §1.4, as its own explicit gate (rather than only incidentally checked above).
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 64,
                                                    Quality = FrameQuality.Full });
        var solvedOptimum = vm.Frame.SmithPower.Optimum;
        Assert.NotNull(solvedOptimum?.Solved);

        var ctx = HarmonicaContext.Create(vm.Model);
        var solvedRows = new System.Collections.Generic.List<HarmonicaReadout>();
        HarmonicaSolver.AddMxColumn(solvedRows, ReadoutColumn.Mxp, "MXP",
            new HarmonicaSolver.Options { Quality = FrameQuality.Full }, solvedOptimum,
            HarmonicaReadoutFormatting.DefaultReadoutFormat, ctx);

        var failedOptimum = solvedOptimum! with
        {
            Solved = null, Published = null, SolvedCompression = null,
            UnsolvedReason = "the drive-up at this optimum did not converge.",
        };
        var failedRows = new System.Collections.Generic.List<HarmonicaReadout>();
        HarmonicaSolver.AddMxColumn(failedRows, ReadoutColumn.Mxp, "MXP",
            new HarmonicaSolver.Options { Quality = FrameQuality.Full }, failedOptimum,
            HarmonicaReadoutFormatting.DefaultReadoutFormat, ctx);

        // R7C §1.4 — the row COUNT and the SCALAR labels (Pout/Eff/PAE/Gain/Gp/Zin/AM-PM/Pdc/γ) must
        // match exactly; only row 0's own header TEXT is expected to differ (it names which case this
        // is — the real impedance, or "no optimum") and is checked separately below.
        Assert.Equal(solvedRows.Count, failedRows.Count);
        for (int i = 1; i < solvedRows.Count; i++)
            Assert.Equal(solvedRows[i].Label, failedRows[i].Label);

        Assert.StartsWith("MXP 1f0 ZL1=", solvedRows[0].Label, StringComparison.Ordinal);
        Assert.Contains("no optimum", failedRows[0].Label, StringComparison.Ordinal);
    }

    private static double ParseLeadingNumber(string formatted)
    {
        string digits = new(formatted.TakeWhile(c => char.IsDigit(c) || c is '-' or '.').ToArray());
        return double.Parse(digits, CultureInfo.InvariantCulture);
    }
}
