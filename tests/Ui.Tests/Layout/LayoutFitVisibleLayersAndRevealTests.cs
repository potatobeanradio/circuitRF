using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Two field reports against the layout editor's camera (2026-09-22).
///
/// <para><b>Zoom to Fit framed hidden layers.</b> <c>render --fit</c> already sized its page from
/// visible layers only, through <see cref="DocumentExtents"/>; the editor's canvas kept a second union
/// of its own that did not ask. The canvas now asks the same function, so the fit is pinned here on
/// that function's behaviour plus a scan that the canvas reaches it with the technology.</para>
///
/// <para><b>Update Layout from Schematic re-zoomed a layout that was already open.</b> An open layout
/// now keeps its zoom and is only panned, and only when what was added is off screen
/// (<see cref="LayoutViewport.Reveal"/>).</para>
/// </summary>
public sealed class LayoutFitVisibleLayersAndRevealTests
{
    private static readonly LayerKey Copper  = new(1, 0);
    private static readonly LayerKey Outline = new(2, 0);

    [Fact]
    public void TheFitBoxLeavesOutAHiddenLayer_AndTheCanvasFitsOnThatBox()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        view.Shapes.Add(new RectShape { Layer = Copper,  X1 = 0,       Y1 = 0,       X2 = 1_000,  Y2 = 1_000 });
        view.Shapes.Add(new RectShape { Layer = Outline, X1 = -50_000, Y1 = -50_000, X2 = 50_000, Y2 = 50_000 });

        var tech = new Technology
        {
            Layers =
            [
                new LayerDef { Key = Copper,  Name = "copper",  Visible = true },
                new LayerDef { Key = Outline, Name = "outline", Visible = false },
            ],
        };

        var bb = DocumentExtents.LayoutFitBox(view, tech, "", 800, 600);
        Assert.Equal(new Bbox(0, 0, 1_000, 1_000), bb);

        // The canvas no longer keeps a union of its own; it asks the function above, with the
        // document's technology — which is where LayerDef.Visible lives.
        string src = StripComments(Src("src/Ui/Controls/LayoutCanvas.cs"));
        var fit = Regex.Match(src, @"private void ZoomToFitInternal\(\)\s*\{(?<body>[\s\S]*?)\n    \}").Groups["body"].Value;
        Assert.Contains("DocumentExtents.LayoutFitBox(model, _viewModel.Technology", fit);
        Assert.DoesNotContain("foreach (var shape in model.Shapes)", fit);
    }

    [Fact]
    public void Reveal_KeepsTheZoom_AndMovesOnlyWhenTheRegionIsOffScreen()
    {
        var vp = new LayoutViewport(PanX: 0, PanY: 0, Zoom: 0.01, Width: 800, Height: 600);   // 80,000 x 60,000 DBU

        Assert.Equal(vp, vp.Reveal(new Bbox(10_000, 10_000, 20_000, 20_000)));

        var moved = vp.Reveal(new Bbox(200_000, 100_000, 210_000, 110_000));
        Assert.Equal(vp.Zoom, moved.Zoom);
        Assert.Equal(205_000, (moved.VisibleMinX + moved.VisibleMaxX) / 2, 6);
        Assert.Equal(105_000, (moved.VisibleMinY + moved.VisibleMaxY) / 2, 6);
    }

    [Fact]
    public void UpdateLayout_RevealsIntoALayoutThatWasAlreadyOpen_AndFramesOnlyOneItJustOpened()
    {
        string src = StripComments(Src("src/Ui/ViewModels/WorkspaceViewModel.SchematicToLayout.cs"));

        int wasOpen = src.IndexOf("bool layoutWasOpen = _openDocsByPath.ContainsKey(targetPath);", StringComparison.Ordinal);
        int opened  = src.IndexOf("OpenOrActivateLayout(targetPath);", StringComparison.Ordinal);
        Assert.True(wasOpen > 0 && wasOpen < opened, "whether the layout was open must be asked before opening it.");

        Assert.Matches(@"if \(!addedRegion\.IsEmpty && layoutWasOpen\)\s*layoutVm\.RequestRevealRegion\(addedRegion\);"
                     + @"\s*else if \(!addedRegion\.IsEmpty\)\s*layoutVm\.RequestZoomToRegion\(addedRegion\);", src);
    }

    private static string Src(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    /// <summary>Comments describe the rule; only code can break it.</summary>
    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
