// A stroked path's outline comes out of SkPathOps, and SkPathOps answers in EVEN-ODD — where the
// direction a contour runs in carries no meaning at all. LayoutRenderer.AsWinding already re-winds
// that answer so a HOLE reads as a hole under the non-zero rule, which is what makes the path
// correct on its own. What it cannot say is which way the OUTER contour runs, and measured on a real
// imported board the builder answers both ways: a 2-point centreline strokes to a positive outer
// contour and a 3-point one to a negative one.
//
// Every other shape kind has a fixed absolute direction — Rect/Circle/RoundedRect/Via from Skia's own
// primitives, Polygon/Curve from NormalizeOuterWinding, which deliberately skips Path because a
// stroked centreline has no outer-ring vertex list to take a signed area from. So Path was the one
// kind with no fixed direction, and inside any batched non-zero SKPath a negative outline CANCELS the
// material it overlaps.
//
// Owner report, 2026-09-12, on an imported Gerber board: zoomed out, pads and trace stubs on Top
// Copper vanished, and what was missing was exactly the INTERSECTION of a trace with a pad — the
// shape of a boolean, which is how it was described. Only when zoomed out, because that is when the
// layer has enough shapes on screen to pass MergeShapeCountThreshold and batch at all.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LayoutOutlineWindingTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);
    private const int Size = 400;

    /// <summary>World width of the frame, in DBU — the fixture below sits inside it.</summary>
    private const double FrameDbu = 8_000_000.0;

    public LayoutOutlineWindingTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfOutlineWinding_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    // ── The fixture: the reported geometry, to scale ──────────────────────────────────────────────

    /// <summary>The trace that disappeared, in its own proportions: a 508,000-wide centreline that runs
    /// left and then turns down. Three vertices is what matters — the same builder gives a TWO-vertex
    /// centreline the opposite handedness, so a straight capsule cannot show this and a bend can.</summary>
    private static PathShape BentTrace() => new()
    {
        Layer = LayerA,
        Xy = [6_464_300, 635_000, 0, 635_000, 0, 0],
        Width = 508_000,
        End = PathEndStyle.Round,
    };

    /// <summary>The pad it ran onto — a plain <c>Rect</c>, whose winding is Skia's own fixed
    /// <c>AddRect</c> convention and therefore the direction everything else on the layer agrees
    /// with.</summary>
    private static RectShape Pad() => new() { Layer = LayerA, X1 = 4_000_000, Y1 = 250_000, X2 = 7_600_000, Y2 = 1_020_000 };

    /// <summary>A point inside BOTH — the intersection that came out as background.</summary>
    private const long OverlapX = 5_500_000, OverlapY = 635_000;

    /// <summary>A point inside the pad only, so a frame that drew nothing at all cannot pass.</summary>
    private const long PadOnlyX = 5_500_000, PadOnlyY = 950_000;

    private static void PadAndTrace(LayoutView v)
    {
        v.Shapes.Add(Pad());
        v.Shapes.Add(BentTrace());
    }

    // ── Through each tier that batches ────────────────────────────────────────────────────────────

    [Fact]
    public void TheMergeTier_KeepsTheMetalWhereATraceCrossesAPad()
    {
        var flat = MakeView();
        PadAndTrace(flat);
        AssertOverlapIsMetal(flat, null, o => o with { ForceMergeTier = true });
    }

    [Fact]
    public void APlacedPadAndTrace_KeepsItToo()
    {
        // A compiled instance chunk ALWAYS merges, so a placement shows this at every zoom.
        var cellDir = CreateCell("pad", PadAndTrace);
        var placed = MakeView();
        placed.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_workspaceDir, cellDir),
            X = 0, Y = 0, Rot = LayoutRotation.R0, Mag = 1, Rows = 1, Cols = 1, PitchX = 0, PitchY = 0,
        });
        AssertOverlapIsMetal(placed, _workspaceDir, o => o);
    }

    /// <summary>The control that says the fixture is the batching and not the geometry: drawn one at a
    /// time, every shape composites on its own and no winding can cancel anything.</summary>
    [Fact]
    public void DrawnIndividually_ItWasNeverWrong()
    {
        var flat = MakeView();
        PadAndTrace(flat);
        AssertOverlapIsMetal(flat, null, o => o);
    }

    // ── The defect at its source, with no renderer around it ──────────────────────────────────────

    [Fact]
    public void AStrokedOutline_UnionsWithAPrimitiveInsteadOfCancellingIt()
    {
        var ps = new LayoutRenderer.PathSpace(0, 0, 1.0 / 1000.0);
        using var trace = LayoutRenderer.BuildPathOutline(BentTrace(), ps)!;
        using var pad = LayoutRenderer.BuildShapePath(Pad(), ps)!;

        // What every batching tier does: AddPath copies CONTOURS into a path that keeps its own
        // (default, non-zero) fill rule.
        using var aggregate = new SKPath();
        aggregate.AddPath(pad);
        aggregate.AddPath(trace);
        Assert.Equal(SKPathFillType.Winding, aggregate.FillType);

        int merged = Lit(canvas => canvas.DrawPath(aggregate, Fill));
        int separate = Lit(canvas => { canvas.DrawPath(pad, Fill); canvas.DrawPath(trace, Fill); });

        Assert.True(separate > 0, "the fixture drew nothing — it is wrong, not the renderer");
        Assert.Equal(separate, merged);
    }

    private static SKPaint Fill => new() { Style = SKPaintStyle.Fill, Color = SKColors.Red, IsAntialias = false };

    /// <summary>Lit-pixel count over the fixture's own world rect — the quantity a cancelled
    /// intersection moves by thousands.</summary>
    private static int Lit(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size));
        surface.Canvas.Clear(SKColors.White);
        // Path space here is DBU/1000, and the fixture spans 8,000 x 1,020 of it.
        surface.Canvas.Scale((float)(Size / (FrameDbu / 1000.0)));
        surface.Canvas.Translate(0, 1_020f);   // path space runs Y-down from the shape's top
        draw(surface.Canvas);
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int lit = 0;
        foreach (var c in bmp.Pixels) if (c.Red > c.Blue + 8) lit++;
        return lit;
    }

    // ── Rig ───────────────────────────────────────────────────────────────────────────────────────

    private static Technology MakeTech() => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef
            {
                Key = LayerA, Name = "L1", Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
                FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
            },
        ],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private void AssertOverlapIsMetal(LayoutView view, string? baseDir,
                                      Func<LayoutRenderOptions, LayoutRenderOptions> tune)
    {
        var vp = new LayoutViewport(0, 0, Size / FrameDbu, Size, Size);
        var px = Render(view, vp, baseDir, tune);
        var bg = px[0];

        var padOnly = At(px, vp, PadOnlyX, PadOnlyY);
        Assert.True(Differs(padOnly, bg), $"the fixture drew no pad at all — {padOnly} against background {bg}");

        var overlap = At(px, vp, OverlapX, OverlapY);
        Assert.True(Differs(overlap, bg),
            $"the trace cancelled the pad it crosses: the intersection is {overlap}, the background is {bg}");
    }

    private static SKColor At(SKColor[] px, LayoutViewport vp, long worldX, long worldY)
        => px[(int)(Size - worldY * vp.Zoom) * Size + (int)(worldX * vp.Zoom)];

    private static bool Differs(SKColor c, SKColor bg)
        => Math.Abs(c.Red - bg.Red) > 6 || Math.Abs(c.Green - bg.Green) > 6 || Math.Abs(c.Blue - bg.Blue) > 6;

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "main.clay"), view);
        return cellDir;
    }

    private static SKColor[] Render(LayoutView view, LayoutViewport vp, string? baseDir,
                                    Func<LayoutRenderOptions, LayoutRenderOptions> tune)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        LayoutRenderer.Draw(surface.Canvas, view, MakeTech(), vp, tune(new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false, BaseDir = baseDir,
        }));
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Pixels;
    }
}
