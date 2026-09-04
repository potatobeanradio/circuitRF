using System.Linq;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// The graphic export's page is sized by what will be PAINTED, and a layer the user turned off is not
/// painted (<c>LayoutRenderer.Draw</c> skips it on <c>LayerDef.Visible</c>).
///
/// <para>Owner report, 2026-09-04: two shapes on two layers, far apart, one layer hidden — pasted into
/// Keynote/PowerPoint as a mostly-empty page with the visible shape too small to read. The page was
/// sized from the hidden shape as well, so the further apart the layers' geometry happened to be, the
/// smaller the useful picture got. The JSON payload is deliberately unchanged: a paste back into a
/// layout still carries everything that was selected.</para>
/// </summary>
public class LayoutClipboardVisibilityTests
{
    private static readonly LayerKey Shown  = new(1, 0);
    private static readonly LayerKey Hidden = new(2, 0);

    private static Technology TechWith(bool hiddenVisible) => new()
    {
        Name = "T",
        Layers =
        [
            new LayerDef { Key = Shown,  Name = "A", Color = new CircuitRF.Design.Theming.Rgba(0, 200, 0), Visible = true },
            new LayerDef { Key = Hidden, Name = "B", Color = new CircuitRF.Design.Theming.Rgba(200, 0, 0), Visible = hiddenVisible },
        ],
    };

    /// <summary>A 10 µm square at the origin, and a second one a millimetre away on the other layer.</summary>
    private static LayoutFragment.Payload TwoFarApartShapes()
    {
        var p = new LayoutFragment.Payload { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        p.Shapes.Add(new RectShape { Layer = Shown,  X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        p.Shapes.Add(new RectShape { Layer = Hidden, X1 = 1_000_000, Y1 = 1_000_000, X2 = 1_010_000, Y2 = 1_010_000 });
        return p;
    }

    [Fact]
    public void PageIsSizedFromTheVisibleShapeAlone_WhenTheOtherLayerIsHidden()
    {
        var ctx = LayoutClipboard.MakeExportContext(TwoFarApartShapes(), TechWith(hiddenVisible: false),
                                                   LayoutRenderTheme.Light, transparent: true);

        var (worldW, worldH, minX, minY) = LayoutClipboard.SelectionBoundsForTests(ctx)!.Value;

        Assert.Equal(10_000, worldW);
        Assert.Equal(10_000, worldH);
        Assert.Equal(0, minX);
        Assert.Equal(0, minY);
    }

    [Fact]
    public void PageSpansBothShapes_WhenBothLayersAreVisible()
    {
        var ctx = LayoutClipboard.MakeExportContext(TwoFarApartShapes(), TechWith(hiddenVisible: true),
                                                   LayoutRenderTheme.Light, transparent: true);

        var (worldW, worldH, _, _) = LayoutClipboard.SelectionBoundsForTests(ctx)!.Value;

        Assert.Equal(1_010_000, worldW);
        Assert.Equal(1_010_000, worldH);
    }

    [Fact]
    public void WithNoTechnologyAtAll_EveryShapeStillCounts()
    {
        // No technology means the renderer falls back to FallbackPalette and paints everything, so the
        // filter must never subtract anything here.
        var ctx = LayoutClipboard.MakeExportContext(TwoFarApartShapes(), tech: null,
                                                   LayoutRenderTheme.Light, transparent: true);

        var (worldW, _, _, _) = LayoutClipboard.SelectionBoundsForTests(ctx)!.Value;
        Assert.Equal(1_010_000, worldW);
    }

    [Fact]
    public void ALayerTheTechnologyDoesNotDefine_StillCounts()
    {
        // Same reasoning as the no-technology case: an undefined layer is drawn from FallbackPalette,
        // so it is on the page and must be measured. Only an explicitly-hidden layer is subtracted.
        var p = new LayoutFragment.Payload { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        p.Shapes.Add(new RectShape { Layer = Shown,        X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        p.Shapes.Add(new RectShape { Layer = new(77, 0),   X1 = 500_000, Y1 = 0, X2 = 510_000, Y2 = 10_000 });

        var ctx = LayoutClipboard.MakeExportContext(p, TechWith(hiddenVisible: false),
                                                   LayoutRenderTheme.Light, transparent: true);

        var (worldW, _, _, _) = LayoutClipboard.SelectionBoundsForTests(ctx)!.Value;
        Assert.Equal(510_000, worldW);
    }

    [Fact]
    public void TheCopyPayloadItselfIsUnchanged_HiddenShapesStillTravel()
    {
        // The fix is about the PICTURE's size only — a paste back into a layout must still receive
        // every shape the user selected, hidden layer or not.
        var payload = TwoFarApartShapes();
        Assert.Equal(2, payload.Shapes.Count);
        Assert.Contains(payload.Shapes, s => s.Layer == Hidden);
    }
}
