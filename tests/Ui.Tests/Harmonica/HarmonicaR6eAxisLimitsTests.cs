// ================================================================
//  HarmonicaR6eAxisLimitsTests.cs — §5 gates 1, 3 and 4 of
//  brief-harmonicarf-r6e-plot-axis-limits-and-autoscale.md
//
//  §1  the DCIV dialog's Drain Sweep fields sit under their own title, not stretched to the bottom
//      of a "*" row.
//  §3  a stored limit overrides AutoScale, PinAxisPin AND the right-edge headroom fraction.
//  §4  the owner's actual complaint, as an assertion: with autoscale off, publishing two frames
//      whose data ranges differ materially leaves Axes.Window/WindowSecondary unchanged.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using CircuitRF.Ui.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR6eAxisLimitsTests
{
    // ── shared source-reading helper — the pattern every other Harmonica strip/dialog test uses ──

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>H8's own trap: a source-scan test that matches text inside an XML comment proves
    /// nothing. This dialog's own top comment names several Grid.Row values in prose.</summary>
    private static string XmlCodeOnly(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, "<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    // ══ §1 — the Drain Sweep fields sit under their own title ═════════════════════════════════

    [Fact]
    public void DcivDialog_HasNoStretchRow()
    {
        string axaml = XmlCodeOnly(ReadSource(
            "src", "Ui", "Views", "Dialogs", "HarmonicaDcivSweepsDialog.axaml"));

        int idx = axaml.IndexOf("RowDefinitions=\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, "no RowDefinitions found");
        int start = idx + "RowDefinitions=\"".Length;
        int end = axaml.IndexOf('"', start);
        string rows = axaml[start..end];

        Assert.DoesNotContain("*", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void DcivDialog_EachSectionTitleRow_IsImmediatelyFollowedByItsFieldRow()
    {
        string axaml = XmlCodeOnly(ReadSource(
            "src", "Ui", "Views", "Dialogs", "HarmonicaDcivSweepsDialog.axaml"));

        int gateRow  = TitleRow(axaml, "Gate sweep (Vgs)");
        int vgsRow   = FieldRowContaining(axaml, "VgsMinBox");
        Assert.Equal(gateRow + 1, vgsRow);

        int drainRow = TitleRow(axaml, "Drain sweep (Vds)");
        int vdsRow   = FieldRowContaining(axaml, "VdsMinBox");
        Assert.Equal(drainRow + 1, vdsRow);
    }

    private static int TitleRow(string axaml, string titleText)
    {
        int idx = axaml.IndexOf($"Text=\"{titleText}\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"title '{titleText}' not found");
        int tagStart = axaml.LastIndexOf('<', idx);
        int tagEnd   = axaml.IndexOf('>', idx);
        return ReadIntAttribute(axaml[tagStart..(tagEnd + 1)], "Grid.Row");
    }

    private static int FieldRowContaining(string axaml, string boxName)
    {
        int idx = axaml.IndexOf($"x:Name=\"{boxName}\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{boxName} not found");
        // Walk outward to the enclosing element that itself carries Grid.Row (the field TextBox's
        // own immediate parent Grid — the TextBox itself has no Grid.Row, only Grid.Column, in this
        // dialog's shape).
        int gridOpen = axaml.LastIndexOf("<Grid ", idx, StringComparison.Ordinal);
        Assert.True(gridOpen >= 0, $"no enclosing <Grid> found before {boxName}");
        int gridTagEnd = axaml.IndexOf('>', gridOpen);
        return ReadIntAttribute(axaml[gridOpen..(gridTagEnd + 1)], "Grid.Row");
    }

    private static int ReadIntAttribute(string tag, string attribute)
    {
        int idx = tag.IndexOf($"{attribute}=\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{attribute} not found on tag: {tag}");
        int start = idx + attribute.Length + 2;
        int end = tag.IndexOf('"', start);
        return int.Parse(tag[start..end]);
    }

    // ══ §3 — precedence: a stored limit beats AutoScale, PinAxisPin and the headroom fraction ═══

    private static LoadlinePanelData LoadlineFixture(double[] vds, double[] ids) => new()
    {
        LoadlineVds = vds, LoadlineIds = ids, FrequencyHz = 2e9,
    };

    [Fact]
    public void Loadline_StoredLimit_OverridesAutoScale()
    {
        var d = LoadlineFixture([0, 1, 2, 3, 4], [0, 0.1, 0.2, 0.05, 0.3]);
        var limits = new HarmonicaPanelRenderer.StoredAxisWindow(
            XMin: -5, XMax: 5, YMin: -1, YMax: 1, Y2Min: null, Y2Max: null, Autoscale: false);

        var plot = HarmonicaPanelRenderer.BuildLoadlinePlot(d, HarmonicaRenderTheme.Dark, limits);

        Assert.Equal(-5, plot.Axes.Window.X, 9);
        Assert.Equal(10, plot.Axes.Window.Width, 9);
        Assert.Equal(-1, plot.Axes.Window.Y, 9);
        Assert.Equal(2, plot.Axes.Window.Height, 9);
    }

    private static PowerSweepPanelData PowerSweepFixture() => new()
    {
        PinAvailDbm  = [-10, -5, 0, 5],
        PoutDbm      = [0, 5, 10, 12],
        GainDb       = [10, 10, 10, 9],
        EfficiencyPct = [10, 20, 30, 35],
        XUnit        = PowerSweepXUnit.PinAvailDbm,
        PinStartDbm  = -10, PinMaxDbm = 50,       // PinAxisPin would otherwise pin X to [-10, 50]
    };

    [Fact]
    public void PowerSweep_StoredLimit_OverridesAutoScaleAndPinAxisPinAndHeadroom()
    {
        var d = PowerSweepFixture();
        // Deliberately NOT [-10, 50] (what PinAxisPin would pin to) and not what AutoScale/headroom
        // would compute from the data (~[-10, 5] with a 5% right extension) — a value only the
        // stored-limit override could have produced.
        var limits = new HarmonicaPanelRenderer.StoredAxisWindow(
            XMin: -20, XMax: 20, YMin: 0, YMax: 15, Y2Min: 5, Y2Max: 40, Autoscale: false);

        var plot = HarmonicaPanelRenderer.BuildPowerSweepPlot(d, HarmonicaRenderTheme.Dark, limits);

        Assert.Equal(-20, plot.Axes.Window.X, 9);
        Assert.Equal(40, plot.Axes.Window.Width, 9);
        Assert.Equal(0, plot.Axes.Window.Y, 9);
        Assert.Equal(15, plot.Axes.Window.Height, 9);
        Assert.Equal(-20, plot.Axes.WindowSecondary.X, 9);
        Assert.Equal(40, plot.Axes.WindowSecondary.Width, 9);
        Assert.Equal(5, plot.Axes.WindowSecondary.Y, 9);
        Assert.Equal(35, plot.Axes.WindowSecondary.Height, 9);
    }

    // ══ §4 — the owner's actual complaint: autoscale off means the axes never move ════════════

    [Fact]
    public void Loadline_AutoscaleOff_WindowIdentical_AcrossTwoMateriallyDifferentFrames()
    {
        var limits = new HarmonicaPanelRenderer.StoredAxisWindow(
            XMin: -5, XMax: 5, YMin: -1, YMax: 1, Y2Min: null, Y2Max: null, Autoscale: false);

        var frameA = LoadlineFixture([0, 1, 2], [0, 0.1, 0.2]);
        var frameB = LoadlineFixture([0, 100, 200, 300], [0, 50, -50, 90]);   // wildly different range

        var plotA = HarmonicaPanelRenderer.BuildLoadlinePlot(frameA, HarmonicaRenderTheme.Dark, limits);
        var plotB = HarmonicaPanelRenderer.BuildLoadlinePlot(frameB, HarmonicaRenderTheme.Dark, limits);

        Assert.Equal(plotA.Axes.Window, plotB.Axes.Window);
    }

    [Fact]
    public void PowerSweep_AutoscaleOff_WindowAndSecondaryWindowIdentical_AcrossTwoMateriallyDifferentFrames()
    {
        var limits = new HarmonicaPanelRenderer.StoredAxisWindow(
            XMin: -20, XMax: 20, YMin: 0, YMax: 15, Y2Min: 5, Y2Max: 40, Autoscale: false);

        var frameA = PowerSweepFixture();
        var frameB = PowerSweepFixture() with
        {
            PinAvailDbm = [-10, 0, 10, 20, 30],
            PoutDbm = [0, 10, 20, 30, 40],
            GainDb = [30, 30, 25, 15, 5],           // very different gain range
            EfficiencyPct = [1, 2, 3, 4, 99],        // very different efficiency range
        };

        var plotA = HarmonicaPanelRenderer.BuildPowerSweepPlot(frameA, HarmonicaRenderTheme.Dark, limits);
        var plotB = HarmonicaPanelRenderer.BuildPowerSweepPlot(frameB, HarmonicaRenderTheme.Dark, limits);

        Assert.Equal(plotA.Axes.Window, plotB.Axes.Window);
        Assert.Equal(plotA.Axes.WindowSecondary, plotB.Axes.WindowSecondary);
    }

    // ══ CaptureAxisWindows — the write-back half, through the real ViewModel ═════════════════

    [Fact]
    public void CaptureAxisWindows_AutoscaleOff_ComputesOnceThenHolds_AcrossADraggedFrame()
    {
        var vm = new HarmonicaViewModel();
        Assert.False(vm.Model.Settings.DcivAutoscale);
        Assert.Null(vm.Model.Settings.DcivXMin);

        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.NotNull(vm.Model.Settings.DcivXMin);   // §2.2 — computed once from the first frame
        double? firstXMin = vm.Model.Settings.DcivXMin;
        double? firstXMax = vm.Model.Settings.DcivXMax;

        // A second solve, at a different bias — a materially different loadline, if the mechanism
        // were broken it would move the stored window.
        vm.SetMarkerImpedance(vm.Markers.First(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                             new System.Numerics.Complex(150, 40));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        Assert.Equal(firstXMin, vm.Model.Settings.DcivXMin);
        Assert.Equal(firstXMax, vm.Model.Settings.DcivXMax);
    }

    [Fact]
    public void CaptureAxisWindows_AutoscaleOn_TracksEveryFrame_AndFreezesOnceTurnedOff()
    {
        var vm = new HarmonicaViewModel();
        vm.SetDcivAutoscale(true);

        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.NotNull(vm.Model.Settings.DcivXMin);

        vm.SetMarkerImpedance(vm.Markers.First(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                             new System.Numerics.Complex(150, 40));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        double? trackedXMin = vm.Model.Settings.DcivXMin;

        // Turning it off freezes exactly what is currently stored/on screen — a further solve must
        // not move it again.
        vm.SetDcivAutoscale(false);
        vm.SetMarkerImpedance(vm.Markers.First(m => m.Band == 1 && m.Side == TerminationSideKind.Load),
                             new System.Numerics.Complex(150, 40));
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        Assert.Equal(trackedXMin, vm.Model.Settings.DcivXMin);
    }
}
