using Avalonia.Input;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

// brief-lcp-4 — a .cem's component ports drawn on its layout. LCP-1's board: C1 and C2, each a two-pad part bridging a break in a
// line, so the gap under each mark is air. Nothing is solved.
public sealed class LayoutComponentPortMarksTests(PlanarComponentPortTests.Board board) : IClassFixture<PlanarComponentPortTests.Board>
{
    private const int Dbu = 1000;
    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>The layout open in an editor, and the .cem on it building its marks with no settle delay.</summary>
    private (LayoutEditorViewModel Layout, EmSetupEditorViewModel Cem) Open()
    {
        var layout = new LayoutEditorViewModel(board.Source().View, board.ClayPath);
        var cem = new EmSetupEditorViewModel(Path.Combine(board.Workspace, "lcp4.cem"), PlanarComponentPortTests.Setup(["C1", "C2"]))
        {
            ResolveLayout = _ => new EmLayoutSource(board.ClayPath, layout.Model, board.Tech, Dbu),
            ComponentPortMarksSettle = TimeSpan.Zero,
        };
        Build(layout, cem);
        return (layout, cem);
    }

    /// <summary>What the workspace does on a refresh: build, then push.</summary>
    private static bool Build(LayoutEditorViewModel layout, EmSetupEditorViewModel cem)
    {
        cem.Refresh();
        cem.ComponentPortMarksBuilding.GetAwaiter().GetResult();
        return layout.ShowComponentPortMarks("lcp4.cem", cem.ComponentPortMarks, cem.ComponentPortMarksEditCount);
    }

    /// <summary>R-lcp4-4 — an edit clears the marks at once; a build copied before the edit is refused; the settled edit's build
    /// puts the mark back at the part's new place.</summary>
    [Fact]
    public void AnEdit_ClearsTheMarks_AndTheSettledBuild_RestoresThemWhereThePartWent()
    {
        var (layout, cem) = Open();
        Assert.Equal(["C1", "C2"], layout.ComponentPortMarks.Select(m => m.Refdes));
        var c1 = layout.ComponentPortMarks[0];
        Assert.Equal((Um(4000), 0L), (c1.Gap.X, c1.Gap.Y));
        Assert.Equal(3, c1.Number);
        var stale = cem.ComponentPortMarks;
        long staleAt = cem.ComponentPortMarksEditCount;

        layout.Model.Instances[0].Y += Um(200);
        layout.Model.NotifyChanged();
        Assert.Empty(layout.ComponentPortMarks);
        Assert.False(layout.ShowComponentPortMarks("lcp4.cem", stale, staleAt));   // built before the edit: never lands over it
        Assert.Empty(layout.ComponentPortMarks);

        Assert.True(Build(layout, cem));
        var moved = layout.ComponentPortMarks.Single(m => m.Refdes == "C1");
        Assert.Equal((Um(4000), Um(200)), (moved.Gap.X, moved.Gap.Y));
    }

    /// <summary>A setup that stops building ports (switched to a kernel that has none) clears the marks after the layout has been
    /// edited: it pushes an empty list with no layout copy to count edits from, and an empty list is never about artwork.</summary>
    [Fact]
    public void AnEmptyList_Clears_WhateverEditItWasMadeAt()
    {
        var (layout, cem) = Open();
        layout.Model.Instances[0].Y += Um(200);
        layout.Model.NotifyChanged();
        Assert.True(Build(layout, cem));
        Assert.NotEmpty(layout.ComponentPortMarks);
        Assert.True(layout.ShowComponentPortMarks("lcp4.cem", [], 0));
        Assert.Empty(layout.ComponentPortMarks);
    }

    /// <summary>R-lcp4-3 — a mark is not a pick: a press on it selects its part (whose two pads leave only air under the mark),
    /// and dragging moves the part, with no label edited.</summary>
    [Fact]
    public void AMark_TakesNoDrag_APressOnItPicksItsPart()
    {
        var (layout, _) = Open();
        string shapesBefore = LayoutPersistence.Serialize(layout.Model);
        long tol = Um(20);

        layout.OnPointerPressed(Um(4000), 0, KeyModifiers.None, 1, tol);
        Assert.Equal([0], layout.SelectedInstanceIndices);
        Assert.Empty(layout.SelectedIndices);
        layout.OnPointerMoved(Um(4000), Um(300), leftDown: true, KeyModifiers.None, tol);
        layout.OnPointerReleased(Um(4000), Um(300), KeyModifiers.None);

        Assert.Equal(Um(300), layout.Model.Instances[0].Y);
        var after = layout.Model.Shapes.OfType<LabelShape>().Select(l => (l.Text, l.X, l.Y));
        var before = LayoutPersistence.Deserialize(shapesBefore).Shapes.OfType<LabelShape>().Select(l => (l.Text, l.X, l.Y));
        Assert.Equal(before, after);
        Assert.Empty(layout.ComponentPortMarks);                                    // the part moved: its mark waits for the build
        Assert.DoesNotContain(layout.Model.Shapes, s => s is LabelShape { Text: "P3" });
    }

    /// <summary>R-lcp4-6 — nothing pushed draws exactly what was drawn before; pushed marks draw (on the canvas only).</summary>
    [Fact]
    public void NoPushedPorts_DrawByteIdentically()
    {
        var (layout, _) = Open();
        var vp = new LayoutViewport(-Um(500), -Um(3000), 5e-5, 650, 300);
        byte[] Draw(IReadOnlyList<LayoutComponentPortMark>? marks)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
            LayoutRenderer.Draw(surface.Canvas, layout.Model, board.Tech, vp,
                                new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, BaseDir = Path.GetDirectoryName(board.ClayPath), ComponentPortMarks = marks });
            using var image = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(image);
            return bmp.Bytes;
        }
        byte[] none = Draw(null);
        Assert.Equal(none, Draw([]));
        Assert.NotEqual(none, Draw(layout.ComponentPortMarks));
    }
}
