// ================================================================
//  HarmonicaPowerSweepAndDcivTests.cs — §5 and §6's alignment gate of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using System;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaPowerSweepAndDcivTests(ITestOutputHelper output)
{
    private static PowerSweepPanelData Fixture(GridMetric metric) => new()
    {
        PinAvailDbm  = [-10, -5, 0, 5],
        PoutDbm      = [0, 5, 10, 12],
        GainDb       = [10, 10, 10, 9],
        EfficiencyPct = [10, 20, 30, 35],
        XUnit        = PowerSweepXUnit.PoutDbm,
        EfficiencyMetric = metric,
    };

    // ══ R-h9b-8 — the right axis label follows the metric ═════════════════════

    [Theory]
    [InlineData(GridMetric.DrainEfficiency, "Efficiency (%)")]
    [InlineData(GridMetric.Pae, "PAE (%)")]
    public void RightAxisLabel_FollowsTheEfficiencyMetric(GridMetric metric, string expected)
    {
        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(Fixture(metric), HarmonicaRenderTheme.Dark);
        Assert.True(plot.CustomY2LabelOn);
        Assert.Equal(expected, plot.CustomY2Label);
        Assert.Equal(expected, plot.Y2Label);
    }

    // ══ R-h9b-8's data bug (found while fixing the label) — the PLOTTED values follow too ═════

    [Fact]
    public void SolvedFrame_EfficiencyData_FollowsTheMetric_NotAlwaysDrainEfficiency()
    {
        var vmDe  = new HarmonicaViewModel();
        vmDe.SolveFrame(new HarmonicaSolver.Options { SkipContours = true, EfficiencyMetric = GridMetric.DrainEfficiency });
        var deValues = vmDe.Frame.PowerSweep.EfficiencyPct;

        var vmPae = new HarmonicaViewModel();
        vmPae.SolveFrame(new HarmonicaSolver.Options { SkipContours = true, EfficiencyMetric = GridMetric.Pae });
        var paeValues = vmPae.Frame.PowerSweep.EfficiencyPct;

        Assert.NotEmpty(deValues);
        Assert.Equal(deValues.Length, paeValues.Length);
        output.WriteLine($"DE[^1]={deValues[^1]:F3}, PAE[^1]={paeValues[^1]:F3}");
        // PAE <= DE always (PAE nets out the drive power DE does not), and they are not
        // coincidentally equal on a real device — proving the two really are different data series.
        Assert.True(paeValues[^1] < deValues[^1]);
        Assert.Equal(GridMetric.DrainEfficiency, vmDe.Frame.PowerSweep.EfficiencyMetric);
        Assert.Equal(GridMetric.Pae,             vmPae.Frame.PowerSweep.EfficiencyMetric);
    }

    // ══ R-h9b-9 — the right axis line/ticks/numbers are Harmonica.EfficiencyTrace ══════════════

    [Fact]
    public void RightAxisOverlay_PaintsInEfficiencyTraceColour()
    {
        const int W = 500, H = 320;
        var theme = HarmonicaRenderTheme.Dark;
        SkiaSharp.SKTypeface? saved = CircuitRF.Ui.Renderers.SkiaFonts.TestOverrideTypeface;
        CircuitRF.Ui.Renderers.SkiaFonts.TestOverrideTypeface = SkiaSharp.SKTypeface.Default;
        try
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, H));
            surface.Canvas.Clear(theme.Background);
            HarmonicaPanelRenderer.DrawPowerSweepPanel(surface.Canvas, (W, H), Fixture(GridMetric.DrainEfficiency),
                                                       theme, darkMode: true);
            using var bmp = SkiaSharp.SKBitmap.FromImage(surface.Snapshot());

            var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(Fixture(GridMetric.DrainEfficiency), theme);
            var tf = PlotRenderer.BuildTransforms(plot, (W, H));
            var rightBorder = tf.PrimaryToCanvas(plot.Axes.Window.Right, plot.Axes.Window.Top);

            // Sample a few pixels along the right border — at least one must be the efficiency colour.
            bool found = false;
            for (int dx = -2; dx <= 2 && !found; dx++)
            {
                int x = (int)Math.Round(rightBorder.X) + dx;
                if (x < 0 || x >= W) continue;
                for (int y = 4; y < H - 4; y += 2)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.Red - theme.EfficiencyTrace.Red) < 20 &&
                        Math.Abs(c.Green - theme.EfficiencyTrace.Green) < 20 &&
                        Math.Abs(c.Blue - theme.EfficiencyTrace.Blue) < 20)
                    { found = true; break; }
                }
            }
            Assert.True(found, "no pixel near the right border painted in Harmonica.EfficiencyTrace");
        }
        finally { CircuitRF.Ui.Renderers.SkiaFonts.TestOverrideTypeface = saved; }
    }

    // ══ R-h9b-10 — right-click begins no drag, and the four units are offered ══════════════════

    [Fact]
    public void RightClick_DoesNotStartAGesture()
    {
        var vm = new HarmonicaViewModel();
        var g = new HarmonicaGesture(vm);
        // A gesture only starts through HarmonicaCanvas's own button check (Properties.IsLeftButtonPressed);
        // HarmonicaGesture itself has no button concept, so this pins the CANVAS'S source instead —
        // the actual place R-h9b-10 lives.
        string src = ReadSource("src", "Ui", "Controls", "HarmonicaCanvas.cs");
        Assert.Contains("IsLeftButtonPressed", src, StringComparison.Ordinal);
        Assert.Contains("ContextMenuTarget = p", src, StringComparison.Ordinal);
    }

    [Fact]
    public void SetPowerSweepXUnitCommand_RelabelsWithoutASolve()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int solvesBefore = vm.LastSolveCount;

        vm.SetPowerSweepXUnitCommand.Execute(PowerSweepXUnit.PinAvailW);

        Assert.Equal(PowerSweepXUnit.PinAvailW, vm.PowerSweepXUnit);
        Assert.Equal(PowerSweepXUnit.PinAvailW, vm.Frame.PowerSweep.XUnit);
        Assert.Equal(solvesBefore, vm.LastSolveCount);   // a relabel, never a re-solve
    }

    // ══ R-h9b-11 — the DCIV and power-sweep DATA rectangles match ══════════════════════════════

    [Theory]
    [InlineData(700, 500)]
    [InlineData(1200, 300)]
    [InlineData(350, 900)]
    public void LoadlineAndPowerSweep_DataRectsMatch_AtSeveralWindowSizes(int w, int h)
    {
        var powerSweep = HarmonicaPanelRenderer.BuildPowerSweepPlot(Fixture(GridMetric.DrainEfficiency),
                                                                     HarmonicaRenderTheme.Dark);
        var loadline = HarmonicaPanelRenderer.BuildLoadlinePlot(new LoadlinePanelData
        {
            Dciv = [new LoadlinePanelData.Curve(-2, [0, 1, 2], [0, 0.1, 0.2])],
            LoadlineVds = [0, 1, 2, 0], LoadlineIds = [0.05, 0.1, 0.05, 0.05],
        }, HarmonicaRenderTheme.Dark);

        var vpP = PlotRenderer.BuildTransforms(powerSweep, (w, h)).Viewport;
        var vpL = PlotRenderer.BuildTransforms(loadline, (w, h)).Viewport;

        output.WriteLine($"{w}x{h}: power-sweep viewport {vpP}, loadline viewport {vpL}");
        Assert.Equal(vpP.X,      vpL.X,      3);
        Assert.Equal(vpP.Width,  vpL.Width,  3);
        Assert.Equal(vpP.Height, vpL.Height, 3);
    }

    // ══ R-h9b-12 — the DCIV override, all-or-nothing, and drawn once per distinct key ══════════

    [Fact]
    public void DcivOverride_InvalidCandidate_IsRejected_AndModelIsUntouched()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Model;

        Assert.False(vm.ApplyDcivOverride(vgsMin: 1, vgsMax: -1, vgsSteps: 9, vdsMin: 0, vdsMax: 10, vdsSteps: 50));
        Assert.Same(before, vm.Model);

        Assert.False(vm.ApplyDcivOverride(vgsMin: -5, vgsMax: -1, vgsSteps: 1, vdsMin: 0, vdsMax: 10, vdsSteps: 50));
        Assert.Same(before, vm.Model);
    }

    [Fact]
    public void DcivOverride_ValidCandidate_IsAppliedAndResolves()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.ApplyDcivOverride(vgsMin: -6, vgsMax: -2, vgsSteps: 5, vdsMin: 0, vdsMax: 20, vdsSteps: 40));

        var resolved = DcivFamily.ResolvedKey(vm.Model);
        Assert.Equal(-6, resolved.VgsMin);
        Assert.Equal(-2, resolved.VgsMax);
        Assert.Equal(5,  resolved.VgsSteps);
        Assert.Equal(0,  resolved.VdsMin);
        Assert.Equal(20, resolved.VdsMax);
        Assert.Equal(40, resolved.VdsSteps);

        // Not structural — the DCIV override must not reset the frame ladder.
        Assert.Equal(vm.Model.StructuralKey, HarmonicaViewModel.DefaultModel().StructuralKey);
    }

    [Fact]
    public void DcivOverride_RoundTripsThroughACharm_AndResetGoesBackToDefault()
    {
        var vm = new HarmonicaViewModel();
        vm.ApplyDcivOverride(-6, -2, 5, 0, 20, 40);
        string json = vm.ToCharmJson();

        var reopened = new HarmonicaViewModel();
        reopened.LoadCharm(json, null);
        Assert.Equal(5, DcivFamily.ResolvedKey(reopened.Model).VgsSteps);

        vm.ResetDcivOverride();
        Assert.Equal(DcivFamily.DefaultKey(vm.Model), DcivFamily.ResolvedKey(vm.Model));
    }

    [Fact]
    public void DcivFamily_RecomputesOncePerDistinctKey()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int before = vm.DcivComputeCount;

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });   // same key
        Assert.Equal(before, vm.DcivComputeCount);

        vm.ApplyDcivOverride(-6, -2, 5, 0, 20, 40);
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });   // new key
        Assert.Equal(before + 1, vm.DcivComputeCount);

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });   // same key again
        Assert.Equal(before + 1, vm.DcivComputeCount);
    }

    // ══ R-h9r2-18 — Power Sweep settings: validated before write, value not structural ══════════

    [Fact]
    public void PowerSweepSettings_InvalidRange_IsRejected_AndModelIsUntouched()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Model;

        // start >= stop
        Assert.False(vm.ApplyPowerSweepSettings(10, 5, 1, true, -50, false, out _));
        Assert.Same(before, vm.Model);

        // step <= 0
        Assert.False(vm.ApplyPowerSweepSettings(-10, 50, 0, true, -50, false, out _));
        Assert.Same(before, vm.Model);

        // over MaxSweepPoints (0.001 dB steps across 60 dB is 60,001 points)
        Assert.False(vm.ApplyPowerSweepSettings(-10, 50, 0.001, true, -50, false, out _));
        Assert.Same(before, vm.Model);
    }

    [Fact]
    public void PowerSweepSettings_TickleAtOrAboveStart_IsRejected()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Model;

        Assert.False(vm.ApplyPowerSweepSettings(-10, 50, 1, true, -10, false, out _));   // at Start
        Assert.Same(before, vm.Model);
        Assert.False(vm.ApplyPowerSweepSettings(-10, 50, 1, true, 0, false, out _));     // above Start
        Assert.Same(before, vm.Model);

        // A tickle above Start is fine when the tickle itself is OFF.
        Assert.True(vm.ApplyPowerSweepSettings(-10, 50, 1, false, 0, false, out _));
    }

    [Fact]
    public void PowerSweepSettings_ValidCandidate_IsApplied_AndIsNotStructural()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.ApplyPowerSweepSettings(-6, 20, 2, true, -40, true, out int count));
        Assert.Equal(14, count);   // (20-(-6))/2 + 1 = 14, exact multiple

        Assert.Equal(-6, vm.Model.Settings.PinStartDbm);
        Assert.Equal(20, vm.Model.Settings.PinMaxDbm);
        Assert.Equal(2,  vm.Model.Settings.PinStepDbm);
        Assert.True(vm.Model.Settings.TickleEnabled);
        Assert.Equal(-40, vm.Model.Settings.TickleDbm);
        Assert.True(vm.Model.Settings.ExactCompressionSolve);

        // Not structural — the Pin range must not reset the frame ladder.
        Assert.Equal(vm.Model.StructuralKey, HarmonicaViewModel.DefaultModel().StructuralKey);
    }

    [Fact]
    public void PowerSweepSettings_RoundTripsThroughACharm()
    {
        var vm = new HarmonicaViewModel();
        vm.ApplyPowerSweepSettings(-6, 20, 2, false, -35, true, out _);
        string json = vm.ToCharmJson();

        var reopened = new HarmonicaViewModel();
        reopened.LoadCharm(json, null);

        Assert.Equal(-6, reopened.Model.Settings.PinStartDbm);
        Assert.Equal(20, reopened.Model.Settings.PinMaxDbm);
        Assert.Equal(2,  reopened.Model.Settings.PinStepDbm);
        Assert.False(reopened.Model.Settings.TickleEnabled);
        Assert.Equal(-35, reopened.Model.Settings.TickleDbm);
        Assert.True(reopened.Model.Settings.ExactCompressionSolve);
    }

    // ══ R-h9r2-17/19 — the plotted sweep is exactly the ladder, every point, released AND dragging ══

    [Fact]
    public void SolvedFrame_PowerSweep_IsExactlyTheConfiguredLadder_BothEndpointsIncluded()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.ApplyPowerSweepSettings(-10, 20, 1, true, -50, false, out int expectedCount));
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var pin = vm.Frame.PowerSweep.PinAvailDbm;
        output.WriteLine($"{pin.Length} points, expected {expectedCount}: [{string.Join(", ", pin)}]");
        Assert.Equal(expectedCount, pin.Length);
        Assert.Equal(-10.0, pin[0], 6);
        Assert.Equal(20.0,  pin[^1], 6);
    }

    [Fact]
    public void SolvedFrame_PowerSweep_HasTheSamePointCount_DraggingOrReleased()
    {
        // R-h9r2-19's own gate: "a dragging frame's Steps.Count equals a released frame's."
        var vm = new HarmonicaViewModel();
        vm.ApplyPowerSweepSettings(-10, 20, 1, true, -50, false, out int expectedCount);

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true, Quality = FrameQuality.Full });
        int released = vm.Frame.PowerSweep.PinAvailDbm.Length;

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true, Quality = FrameQuality.FrozenContours });
        int dragging = vm.Frame.PowerSweep.PinAvailDbm.Length;

        Assert.Equal(expectedCount, released);
        Assert.Equal(expectedCount, dragging);
    }

    // ══ brief-harmonicarf-r4 §1.2 — the Pin-domain X axis is pinned, not autofit ═══════════════

    [Fact]
    public void PinDomainXAxis_IsPinnedToTheConfiguredRange_NotToWhereTheLadderActuallyStopped()
    {
        // A ladder that stopped WAY short of PinMaxDbm (an early-stopped sweep) must still show the
        // full configured [PinStartDbm, PinMaxDbm] on the X axis — the whole point of pinning it.
        var d = Fixture(GridMetric.DrainEfficiency) with
        {
            XUnit = PowerSweepXUnit.PinAvailDbm,
            PinAvailDbm = [-10, -5, 0, 5],   // stopped at 5 dBm, far short of PinMaxDbm below
            PinStartDbm = -10, PinMaxDbm = 50,
        };
        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, HarmonicaRenderTheme.Dark);

        // brief-harmonicarf-r6d §2 — the pinned range is extended by XHeadroomFraction past the
        // configured stop, so the compression cursor is never read sitting under the axis line.
        double span = 50.0 - (-10.0);
        Assert.Equal(-10.0, plot.Axes.Window.Left, 6);
        Assert.Equal(50.0 + span * HarmonicaPanelRenderer.XHeadroomFraction, plot.Axes.Window.Right, 6);
    }

    [Fact]
    public void PinDomainXAxis_IsStableAcrossDifferentEarlyStopPoints_ThisIsTheAntiJitterGate()
    {
        // Two "frames" whose ladders stopped at different Pin levels (as two different terminations'
        // early-stopped sweeps would) must produce the IDENTICAL X window — this is what stops the
        // axis breathing frame to frame during a drag.
        var dA = Fixture(GridMetric.DrainEfficiency) with
        {
            XUnit = PowerSweepXUnit.PinAvailDbm, PinAvailDbm = [-10, -5, 0],
            GainDb = [10, 10, 10], EfficiencyPct = [10, 20, 30],
            PinStartDbm = -10, PinMaxDbm = 50,
        };
        var dB = Fixture(GridMetric.DrainEfficiency) with
        {
            XUnit = PowerSweepXUnit.PinAvailDbm, PinAvailDbm = [-10, -5, 0, 5, 10, 15, 20],
            GainDb = [10, 10, 10, 10, 10, 10, 9], EfficiencyPct = [10, 20, 30, 32, 34, 35, 35],
            PinStartDbm = -10, PinMaxDbm = 50,
        };

        var plotA = HarmonicaPanelRenderer.BuildPowerSweepPlot(dA, HarmonicaRenderTheme.Dark);
        var plotB = HarmonicaPanelRenderer.BuildPowerSweepPlot(dB, HarmonicaRenderTheme.Dark);

        Assert.Equal(plotA.Axes.Window.Left,  plotB.Axes.Window.Left,  6);
        Assert.Equal(plotA.Axes.Window.Right, plotB.Axes.Window.Right, 6);
    }

    [Fact]
    public void PinDomainXAxis_InWatts_IsPinnedInTheWattsDomainToo()
    {
        var d = Fixture(GridMetric.DrainEfficiency) with
        {
            XUnit = PowerSweepXUnit.PinAvailW,
            PinAvailDbm = [-10, -5, 0],
            GainDb = [10, 10, 10], EfficiencyPct = [10, 20, 30],
            PinStartDbm = -10, PinMaxDbm = 20,
        };
        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, HarmonicaRenderTheme.Dark);

        double expectedLo = Math.Pow(10.0, (-10 - 30.0) / 10.0);
        double expectedHi = Math.Pow(10.0, (20 - 30.0) / 10.0);
        // brief-harmonicarf-r6d §2 — same headroom extension, applied in the Watts domain too.
        double span = expectedHi - expectedLo;
        Assert.Equal(expectedLo, plot.Axes.Window.Left,  9);
        Assert.Equal(expectedHi + span * HarmonicaPanelRenderer.XHeadroomFraction, plot.Axes.Window.Right, 9);
    }

    [Fact]
    public void PoutDomainXAxis_IsLeftToAutoScale_UnaffectedByThePinPin()
    {
        // Pout has no configured range to pin to — this documents that the fix is deliberately
        // scoped to the Pin-domain X units only (§1.2's own instruction), so a Pout-domain axis still
        // reflects the actual data range, exactly as before this brief.
        var d = Fixture(GridMetric.DrainEfficiency) with
        {
            XUnit = PowerSweepXUnit.PoutDbm,
            PoutDbm = [0, 5, 10, 12],
            PinStartDbm = -10, PinMaxDbm = 50,
        };
        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, HarmonicaRenderTheme.Dark);

        // Autofit to the actual Pout data (0..12), not to the Pin range (-10..50).
        Assert.True(plot.Axes.Window.Left > -10.0);
        Assert.True(plot.Axes.Window.Right < 50.0);
    }

    // ══ brief-harmonicarf-r6d §5 — the Time Domain view ══════════════════════════════════════════

    [Fact]
    public void SolvedFrame_Loadline_CarriesFrequencyHz_AndTheSampleCountMatchesSettings()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var loadline = vm.Frame.Loadline;
        Assert.Equal(vm.Model.Settings.FrequencyHz, loadline.FrequencyHz, 3);

        if (loadline.LoadlineVds.Length > 0)
        {
            // Closed over one cycle: the array is Settings.LoadlineSamples + 1 (the first sample
            // repeated as the last) — HarmonicaSolver.BuildLoadline's own note.
            Assert.Equal(vm.Model.Settings.LoadlineSamples + 1, loadline.LoadlineVds.Length);
            Assert.Equal(loadline.LoadlineVds.Length, loadline.LoadlineIds.Length);
        }
    }

    [Fact]
    public void TimeDomainPlot_ReadsTheExactLoadlineArrays_NeverRecomputingOrResampling()
    {
        var theme = HarmonicaRenderTheme.Dark;
        var d = new LoadlinePanelData
        {
            LoadlineVds = [0.0, 1.5, 3.0, 0.0],
            LoadlineIds = [0.05, 0.10, 0.05, 0.05],
            FrequencyHz = 2e9,
        };

        var plot = HarmonicaPanelRenderer.BuildTimeDomainPlot(d, theme);
        Assert.Equal("Time Domain", plot.Title);
        Assert.Equal("Time (ns)", plot.XLabel);
        Assert.Equal("Vds (V)",   plot.YLabel);
        Assert.Equal("Ids (A)",   plot.Y2Label);
        Assert.Equal(2, plot.Traces.Count);

        var vdsTrace = plot.Traces[0];
        var idsTrace = plot.Traces[1];
        Assert.False(vdsTrace.UseSecondaryAxis, "Vds is the LEFT axis");
        Assert.True(idsTrace.UseSecondaryAxis,  "Ids is the RIGHT axis");

        // Owner: 2 RF cycles, not 1 — cycleSamples * TimeDomainCycles + 1 points.
        double periodNs = 1e9 / d.FrequencyHz;
        int cycleSamples = d.LoadlineVds.Length - 1;
        int expectedTotal = cycleSamples * HarmonicaPanelRenderer.TimeDomainCycles + 1;
        Assert.Equal(expectedTotal, vdsTrace.Points.Count);
        Assert.Equal(expectedTotal, idsTrace.Points.Count);

        // Exact, not approximate (precision 4 is the float-vs-double rounding floor, not slack for a
        // resample) — the panel must read the SAME source arrays the loadline panel draws (wrapped
        // for the second cycle), never recompute or resample.
        for (int i = 0; i < expectedTotal; i++)
        {
            double expectedT = (double)i / cycleSamples * periodNs;
            int src = i % cycleSamples;
            Assert.Equal(expectedT,          vdsTrace.Points[i].X, precision: 4);
            Assert.Equal(expectedT,          idsTrace.Points[i].X, precision: 4);
            Assert.Equal(d.LoadlineVds[src], vdsTrace.Points[i].Y, precision: 4);
            Assert.Equal(d.LoadlineIds[src], idsTrace.Points[i].Y, precision: 4);
        }

        // t[0] = 0, t[last] = TWO full periods — the array is closed over 2 RF cycles.
        Assert.Equal(0.0,                                        vdsTrace.Points[0].X,   precision: 9);
        Assert.Equal(periodNs * HarmonicaPanelRenderer.TimeDomainCycles, vdsTrace.Points[^1].X, precision: 4);
    }

    [Fact]
    public void TimeDomainPanel_EmptyLoadline_DrawsAStatedNote_NeverZeros()
    {
        const int W = 300, H = 240;
        var theme = HarmonicaRenderTheme.Dark;
        var d = new LoadlinePanelData();   // R-h8-3's refusal: intrinsic plane not located

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, H));
        surface.Canvas.Clear(theme.Background);
        HarmonicaPanelRenderer.DrawTimeDomainPanel(surface.Canvas, (W, H), d, theme, darkMode: true);

        using var bmp = SkiaSharp.SKBitmap.FromImage(surface.Snapshot());
        int nonBackground = 0;
        for (int y = 0; y < H; y += 2)
        for (int x = 0; x < W; x += 2)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.Red - theme.Background.Red) > 4 || Math.Abs(c.Green - theme.Background.Green) > 4 ||
                Math.Abs(c.Blue - theme.Background.Blue) > 4) nonBackground++;
        }
        Assert.True(nonBackground > 0, "an empty loadline must still draw a stated note, not a blank panel");
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine([dir!.FullName, .. parts]);
        Assert.True(System.IO.File.Exists(path), $"source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }
}
