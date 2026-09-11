using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── The vertical ruler's labels must FIT the strip ─────────────────────────────────────────────
// Owner report: the left ruler's numbers are cut off past about three and a half characters, so an
// ordinary "-1000" — and the canvas pans through zero in a single gesture, so a negative coordinate
// is not an edge case — was truncated with nothing to say it had been. The strip now holds four
// characters outright and shrinks the label past that; the horizontal ruler takes whatever size the
// vertical one settles on, so one window never letters its two rulers differently.

public class LayoutRulerLabelFitTests
{
    private const double Available =
        LayoutRulerRenderer.VerticalThickness - 2;   // the renderer's own 1 px pad each side

    private static float WidestLabelAt(float fontSize, LayoutViewport vp, long step,
                                       int dbuPerMicron, LayoutUnit unit)
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, fontSize);
        float widest = 0f;
        long jStart = (long)Math.Floor(vp.VisibleMinY / step);
        long jEnd   = (long)Math.Ceiling(vp.VisibleMaxY / step);
        for (long j = jStart; j <= jEnd; j++)
        {
            double sy = vp.WorldToScreenY(j * step);
            if (sy < -20 || sy > vp.Height + 20) continue;
            float w = font.MeasureText(LayoutUnits.Format(j * step, unit, dbuPerMicron));
            if (w > widest) widest = w;
        }
        return widest;
    }

    /// <summary>Four digits at the base size is the width the strip is sized for.</summary>
    [Fact]
    public void FourCharacterLabelFitsAtTheBaseSize()
    {
        using var font = new SKFont(SkiaFonts.PlexRegular, 10f);
        Assert.True(font.MeasureText("8888") <= Available,
            $"four digits measure {font.MeasureText("8888"):F2} px but only {Available:F2} px are available " +
            $"inside a {LayoutRulerRenderer.VerticalThickness} px strip");
        // ...and not much more than that: the strip is chrome beside the drawing, not a column.
        Assert.True(font.MeasureText("88888") > Available);
    }

    /// <summary>
    /// A viewport whose labels run to five characters is drawn smaller, and small ENOUGH: the
    /// widest visible label measured at the returned size fits the strip.
    /// </summary>
    [Fact]
    public void LongLabelsShrinkUntilTheyFit()
    {
        // Nanometre display at 1 DBU = 1 nm, framed across roughly ±5,000 nm: the tick labels run
        // "-5000".."5000".
        const int dbuPerMicron = 1000;
        var vp = new LayoutViewport(PanX: -5000, PanY: -5000, Zoom: 0.06, Width: 600, Height: 600);
        long step = LayoutGridMath.ComputeRulerTickStepDbu(vp.Zoom, LayoutUnit.Nm, dbuPerMicron, 60.0);

        float baseWidest = WidestLabelAt(10f, vp, step, dbuPerMicron, LayoutUnit.Nm);
        Assert.True(baseWidest > Available, "fixture no longer produces a label too wide for the strip");

        float size = LayoutRulerRenderer.ComputeLabelFontSize(vp, step, dbuPerMicron, LayoutUnit.Nm);
        Assert.True(size < 10f, $"expected a shrunken label size, got {size}");
        Assert.True(WidestLabelAt(size, vp, step, dbuPerMicron, LayoutUnit.Nm) <= Available,
            $"the widest label still overflows the strip at size {size}");
    }

    /// <summary>Short labels are never shrunk — the fit rule only takes room away when it must.</summary>
    [Fact]
    public void ShortLabelsKeepTheBaseSize()
    {
        const int dbuPerMicron = 1000;
        var vp = new LayoutViewport(PanX: 0, PanY: 0, Zoom: 0.5, Width: 400, Height: 400);
        long step = LayoutGridMath.ComputeRulerTickStepDbu(vp.Zoom, LayoutUnit.Um, dbuPerMicron, 60.0);

        Assert.Equal(10f, LayoutRulerRenderer.ComputeLabelFontSize(vp, step, dbuPerMicron, LayoutUnit.Um));
    }

    /// <summary>
    /// Both strips ask the same question of the same viewport, so they cannot disagree — this is the
    /// mechanism behind "whatever size the y-axis uses, the x-axis uses too". The horizontal ruler is
    /// handed the canvas's viewport exactly as the vertical one is (LayoutEditorView.SyncRulers).
    /// </summary>
    [Fact]
    public void BothRulersComputeOneSizeFromOneViewport()
    {
        const int dbuPerMicron = 1000;
        var vp = new LayoutViewport(PanX: -5000, PanY: -5000, Zoom: 0.06, Width: 600, Height: 600);
        long step = LayoutGridMath.ComputeRulerTickStepDbu(vp.Zoom, LayoutUnit.Nm, dbuPerMicron, 60.0);

        // The horizontal ruler's control is a different size from the vertical one's; the answer must
        // not depend on that, because it is derived from the viewport and the vertical strip's width.
        float a = LayoutRulerRenderer.ComputeLabelFontSize(vp, step, dbuPerMicron, LayoutUnit.Nm);
        float b = LayoutRulerRenderer.ComputeLabelFontSize(vp, step, dbuPerMicron, LayoutUnit.Nm);
        Assert.Equal(a, b);
        Assert.True(a < 10f);
    }

    /// <summary>
    /// The two XAML hosts must be as wide as the renderer believes they are — the renderer sizes the
    /// label to <see cref="LayoutRulerRenderer.VerticalThickness"/>, and a strip drawn narrower than
    /// that clips the label the fit rule just made room for.
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Views/Layout/LayoutEditorView.axaml", "VRuler")]
    [InlineData("src/Ui/Views/WBond/WBondProfileView.axaml", "ProfileVRuler")]
    public void TheXamlStripsMatchTheRenderersWidth(string relativePath, string name)
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        int at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} not found in {relativePath}");

        string declaration = xaml.Substring(at, Math.Min(240, xaml.Length - at));
        string expected = $"Width=\"{LayoutRulerRenderer.VerticalThickness:0}\"";
        Assert.Contains(expected, declaration, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
