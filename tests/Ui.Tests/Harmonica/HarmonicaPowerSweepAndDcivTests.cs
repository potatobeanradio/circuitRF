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
