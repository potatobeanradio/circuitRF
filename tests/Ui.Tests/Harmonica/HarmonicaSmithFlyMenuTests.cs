// ================================================================
//  HarmonicaSmithFlyMenuTests.cs — brief-harmonicarf-r6b §4
//
//  The Smith panels' own right-click fly menus: title band vs body, Copy + Show Grid Points on the
//  body, Contour Plane/Harmonic (+ Efficiency Metric on the efficiency chart only) on the title band.
//  This codebase has no headless-Avalonia harness for a live HarmonicaView/ContextMenu (every existing
//  dialog/menu test in this folder — HarmonicaSetZ0DialogTests, HarmonicaPowerSweepAndDcivTests —
//  pins behaviour from source text instead), so the GEOMETRY half (title-band vs body resolution) is
//  tested directly against the real pure functions, and the MENU-SHAPE half is pinned from source.
// ================================================================

using System;
using System.IO;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSmithFlyMenuTests
{
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

    private static string ViewSource() =>
        ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

    // ══ §4.1 — title-band vs body resolution, at a boundary point ══════════

    [Theory]
    [InlineData(420.0, 340.0)]
    [InlineData(700.0, 650.0)]
    public void ATitleBandClick_IsOnePixelAboveTheBand_ABodyClick_IsOnePixelBelow(double w, double h)
    {
        var vm = new HarmonicaViewModel();
        var placement = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        (double W, double H) panelSize = (placement.W * w, placement.H * h);

        double bandH = HarmonicaPanelRenderer.TitleBandHeight(panelSize);

        // Exactly the dispatch's own condition: local.Y < TitleBandHeight(size).
        var titleLocal = new SkiaSharp.SKPoint((float)(panelSize.W / 2), (float)(bandH - 1));
        var bodyLocal  = new SkiaSharp.SKPoint((float)(panelSize.W / 2), (float)(bandH + 1));

        Assert.True(titleLocal.Y < bandH, "one pixel above the band must resolve as a title click");
        Assert.False(bodyLocal.Y < bandH, "one pixel below the band must resolve as a body click");

        // And PanelAt/ToPanel — the two accessors the dispatch actually calls — agree on the panel
        // and the local coordinates at both points, round-tripped through full canvas space.
        double canvasX = placement.X * w + titleLocal.X, canvasY = placement.Y * h + titleLocal.Y;
        string? panelId = HarmonicaHitTest.PanelAt(vm.Layout, canvasX, canvasY, w, h);
        Assert.Equal(HarmonicaPanelId.SmithPower, panelId);

        var (local, size) = HarmonicaHitTest.ToPanel(vm.Layout, panelId!, canvasX, canvasY, w, h);
        Assert.Equal(panelSize.W, size.W, 6);
        Assert.Equal(panelSize.H, size.H, 6);
        Assert.Equal(titleLocal.Y, local.Y, 3);
    }

    [Fact]
    public void OnCanvasContextMenuOpening_ResolvesTheSmithBranch_BeforeTheEditDisplayPanels()
    {
        // §4.1's own ordering rule: the Smith fly-menu branch must be tried BEFORE
        // HarmonicaEditTarget.Resolve's power-sweep/loadline branches, using HarmonicaHitTest.PanelAt
        // and HarmonicaPanelRenderer.TitleBandHeight rather than hand-deriving the geometry again.
        string src = ViewSource();
        int marker = src.IndexOf("BuildMarkerMenu(items, h, grab.Marker!);", StringComparison.Ordinal);
        int smith  = src.IndexOf("var smithPanelId = HarmonicaHitTest.PanelAt(", StringComparison.Ordinal);
        int edit   = src.IndexOf("HarmonicaEditTarget.Resolve(h.Layout, [.. h.PickedTraces]", StringComparison.Ordinal);

        Assert.True(marker >= 0 && smith >= 0 && edit >= 0);
        Assert.True(marker < smith, "the marker branch must resolve before the Smith fly menus");
        Assert.True(smith < edit,   "the Smith fly menus must resolve before the Edit-Display panels");

        Assert.Contains("HarmonicaPanelRenderer.TitleBandHeight(size)", src, StringComparison.Ordinal);
    }

    // ══ §4.2 — the body menu ═════════════════════════════════════════════

    [Fact]
    public void BodyMenu_OffersCopy_BoundToTheResolvedPanelId_NeverPanelUnderPointer()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildSmithBodyMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>brief-harmonicarf-r6b §4.3", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // brief-harmonicarf-r6d §6 — the inline Copy MenuItem is now built by the ONE shared helper
        // every fly-menu branch uses; the panelId it is bound to is still the resolved one, never
        // PanelUnderPointer().
        Assert.Contains("BuildCopyMenuItem(panelId)", body, StringComparison.Ordinal);
        Assert.Contains("menus.ToggleShowGridPointsCommand.Execute(null)", body, StringComparison.Ordinal);
        // R8B §5 — the dynamic-icon Toggle helper, never ToggleType.
        Assert.Contains("Toggle(\"Show Grid Points\", h.ShowGridPoints,", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelUnderPointer()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildCopyMenuItem_IsTheOneHelper_UsedByCopyPanelAsync_NeverASecondExporter()
    {
        string src = ViewSource();

        int helper = src.IndexOf("private MenuItem BuildCopyMenuItem(", StringComparison.Ordinal);
        Assert.True(helper >= 0);
        int helperEnd = src.IndexOf("\n    }", helper, StringComparison.Ordinal);
        string helperBody = src[helper..helperEnd];
        Assert.Contains("CopyPanelAsync(panelId)", helperBody, StringComparison.Ordinal);

        int m = src.IndexOf("private async System.Threading.Tasks.Task CopyPanelAsync(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    }", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        Assert.Contains("HarmonicaClipboard.CopyAsync(Canvas, h, panelId)", body, StringComparison.Ordinal);
    }

    // ══ §4.3 — the title menu ════════════════════════════════════════════

    [Fact]
    public void TitleMenu_BindsToTheSameCommands_TheDisplayMenuUses()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildSmithTitleMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// brief-harmonicarf-r6d §6", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("menus.SetGridSideCommand.Execute(\"Load\")", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetGridSideCommand.Execute(\"Source\")", body, StringComparison.Ordinal);
        Assert.Contains("menus.ContourHarmonics", body, StringComparison.Ordinal);
        Assert.Contains("band.SelectCommand.Execute(null)", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetEfficiencyMetricCommand.Execute(\"DE\")", body, StringComparison.Ordinal);
        Assert.Contains("menus.SetEfficiencyMetricCommand.Execute(\"PAE\")", body, StringComparison.Ordinal);

        // Never a hardcoded f₀/2f₀/3f₀ — that was the owner-reported bug this menu already learned
        // from (HarmonicaHarmonicMenuItem's own remark).
        Assert.DoesNotContain("\"f₀\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"2f₀\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void EfficiencyMetric_IsOnlyOfferedOnTheEfficiencyChart()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildSmithTitleMenu(", StringComparison.Ordinal);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// brief-harmonicarf-r6d §6", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        int guard = body.IndexOf("if (panelId == HarmonicaPanelId.SmithEfficiency)", StringComparison.Ordinal);
        int eff    = body.IndexOf("Header = \"Efficiency Metric\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && eff >= 0);
        Assert.True(guard < eff, "the Efficiency Metric item must be built INSIDE the efficiency-only guard");

        // And Contour Plane / Contour Harmonic must be built OUTSIDE (before) that guard — offered on
        // BOTH charts.
        int plane = body.IndexOf("Header = \"Contour Plane\"", StringComparison.Ordinal);
        int harmonic = body.IndexOf("Header = \"Contour Harmonic\"", StringComparison.Ordinal);
        Assert.True(plane >= 0 && plane < guard);
        Assert.True(harmonic >= 0 && harmonic < guard);
    }
}
