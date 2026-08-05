using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// A cell's own connection points, persisted (<see cref="LayoutPin"/>, <see cref="LayoutView.Pins"/>).
///
/// <para><b>What this closes.</b> A generated cell's pins used to survive only as <c>IsPort</c>
/// labels, which carry a name, a position and a layer — so the connecting WIDTH and the outward
/// DIRECTION were lost the moment the cell reached disk. The renderer worked around that by
/// re-invoking the generator, which is exact for a PCell and impossible for a cell that was merely
/// IMPORTED: it has no generator to invoke, so it could never show a pin at all. A pin list on the
/// view is what lets both routes carry connectivity.</para>
/// </summary>
public sealed class LayoutPinListTests : IDisposable
{
    private readonly string _root;

    public LayoutPinListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pins-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ── persistence ───────────────────────────────────────────────────────────

    /// <summary>Every field survives the round trip — especially the two that used to be dropped.</summary>
    [Fact]
    public void APin_RoundTripsWithItsWidthAndOutwardDirection()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Pins.Add(new LayoutPin
        {
            Name = "G", X = 435, Y = 150, WidthDbu = 510, OutwardDeg = 90.0,
            Layer = new LayerKey(5, 2),
        });

        string path = Path.Combine(_root, "a.clay");
        LayoutPersistence.SaveToFile(path, view);
        var reloaded = LayoutPersistence.LoadFromFile(path);

        var pin = Assert.Single(reloaded.Pins);
        Assert.Equal("G", pin.Name);
        Assert.Equal(435, pin.X);
        Assert.Equal(150, pin.Y);
        Assert.Equal(510, pin.WidthDbu);        // the connecting width — previously lost
        Assert.Equal(90.0, pin.OutwardDeg);     // the outward direction — previously lost
        Assert.Equal(new LayerKey(5, 2), pin.Layer);
    }

    /// <summary>
    /// A pin-free layout re-serializes byte-for-byte, so the field is genuinely additive and no
    /// <c>FormatVersion</c> bump is owed. Every existing <c>.clay</c> in the field is unaffected.
    /// </summary>
    [Fact]
    public void APinFreeLayout_ReSerializesByteIdentically_NoFormatVersionBumpOwed()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });

        string before = LayoutPersistence.Serialize(view);
        Assert.DoesNotContain("\"Pins\"", before, StringComparison.Ordinal);

        string path = Path.Combine(_root, "b.clay");
        LayoutPersistence.SaveToFile(path, view);
        Assert.Equal(before, LayoutPersistence.Serialize(LayoutPersistence.LoadFromFile(path)));
    }

    /// <summary>A hand-authored file predating the field loads cleanly with no pins.</summary>
    [Fact]
    public void AFileWithNoPinsField_LoadsWithNoPins()
    {
        string path = Path.Combine(_root, "c.clay");
        File.WriteAllText(path, """
            { "FormatVersion": 1, "DbuPerMicron": 1000, "Shapes": [], "Instances": [] }
            """);

        Assert.Empty(LayoutPersistence.LoadFromFile(path).Pins);
    }

    // ── the generated route ───────────────────────────────────────────────────

    /// <summary>
    /// A generated cell writes its pins to disk with width and direction intact. This is the exact
    /// loss the pin list exists to close: the <c>IsPort</c> label beside each one still carries the
    /// visible text, and carries neither of these.
    /// </summary>
    [Fact]
    public void AGeneratedCell_PersistsItsPins_WithWidthAndDirection_NotJustPortLabels()
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var view = LayoutPersistence.LoadFromFile(
            Directory.GetFiles(CellFolder.SubFolderPath(cellDir, ViewType.Layout), "*.clay").Single());

        Assert.Equal(2, view.Pins.Count);
        Assert.All(view.Pins, p => Assert.True(p.WidthDbu > 0, "a pin's connecting width must survive"));

        // MLIN's two pins face opposite ways along the line — the direction is real information, not
        // a default that happens to round-trip.
        var directions = view.Pins.Select(p => p.OutwardDeg).OrderBy(d => d).ToArray();
        Assert.NotEqual(directions[0], directions[1]);

        // The port labels are still there: they answer a different question (visible text), and
        // dropping them would have traded one loss for another.
        Assert.Equal(2, view.Shapes.OfType<LabelShape>().Count(l => l.IsPort));
    }

    // ── the imported route: previously impossible ─────────────────────────────

    /// <summary>
    /// A cell with persisted pins and NO generator still shows its pin markers. Before the pin list
    /// this could not work at all — the overlay recovered pins by re-invoking the generator, and an
    /// imported cell has none. This is what makes imported device artwork connectable.
    /// </summary>
    [Fact]
    public void AnImportedCell_WithNoGenerator_StillShowsItsPins()
    {
        // A cell folder holding plain artwork plus a pin list — exactly what GDSII import now writes,
        // and deliberately carrying no PCellOrigin.
        string cellDir = CellFolder.CreateCellFolder(_root, "Imported");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var sub = new LayoutView { DbuPerMicron = 1000 };
        sub.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -200, Y1 = -50, X2 = 200, Y2 = 50 });
        sub.Pins.Add(new LayoutPin { Name = "A", X = -200, Y = 0, WidthDbu = 100, OutwardDeg = 180, Layer = new LayerKey(1, 0) });
        sub.Pins.Add(new LayoutPin { Name = "B", X = 200, Y = 0, WidthDbu = 100, OutwardDeg = 0, Layer = new LayerKey(1, 0) });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "Imported.clay"), sub);

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        ccell.PrimaryLayout = "Imported.clay";
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell);
        Assert.Null(LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, "Imported.clay")).PCellOrigin);

        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(_root, cellDir), X = 0, Y = 0, Mag = 1.0 });

        using var surface = SKSurface.Create(new SKImageInfo(400, 200));
        surface.Canvas.Clear(SKColors.White);
        var vp = new LayoutViewport(-400, -200, 0.4, 400, 200);
        LayoutRenderer.Draw(surface.Canvas, top, null, vp,
            new LayoutRenderOptions
            {
                Theme = LayoutRenderTheme.Light, ShowGrid = false,
                ShowPCellPins = true, BaseDir = _root,
            });

        Assert.True(PinColorNear(surface, vp, -200, 0), "expected a pin marker at the imported cell's pin A");
        Assert.True(PinColorNear(surface, vp,  200, 0), "expected a pin marker at the imported cell's pin B");
    }

    private static bool PinColorNear(SKSurface surface, LayoutViewport vp, long wx, long wy, int radius = 4)
    {
        var pin = LayoutRenderTheme.Light.PCellPin;
        int sx = (int)Math.Round(vp.WorldToScreenX(wx));
        int sy = (int)Math.Round(vp.WorldToScreenY(wy));
        using var snapshot = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);

        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            int x = sx + dx, y = sy + dy;
            if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height) continue;
            var c = bitmap.GetPixel(x, y);
            if (Math.Abs(c.Red - pin.Red) < 40 && Math.Abs(c.Green - pin.Green) < 40 && Math.Abs(c.Blue - pin.Blue) < 40)
                return true;
        }
        return false;
    }

    // ── the second consumer: snap ─────────────────────────────────────────────

    /// <summary>
    /// An imported cell's pins are SNAPPABLE, not merely visible. This is the same blindness the pin
    /// overlay had, in the consumer where it matters more: a pin the user can see but cannot snap to
    /// is half a connection, and reads as the snap being broken rather than the pin being absent.
    /// </summary>
    [Fact]
    public void AnImportedCellsPins_AreSnapFeatures_NotJustVisible()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -200, Y1 = -50, X2 = 200, Y2 = 50 });
        view.Pins.Add(new LayoutPin { Name = "A", X = -200, Y = 0, WidthDbu = 100, OutwardDeg = 180, Layer = new LayerKey(1, 0) });
        view.Pins.Add(new LayoutPin { Name = "B", X = 200, Y = 0, WidthDbu = 100, OutwardDeg = 0, Layer = new LayerKey(1, 0) });
        Assert.Null(view.PCellOrigin);   // nothing generated this — there is no generator to fall back on

        var index = LayoutSnapFeatureIndex.Get(view, null);
        var counters = default(SnapQueryCounters);

        var atA = index.QueryNear(-200, 0, 10, ref counters).Where(f => f.Kind == SnapFeatureKind.Pin).ToArray();
        var atB = index.QueryNear(200, 0, 10, ref counters).Where(f => f.Kind == SnapFeatureKind.Pin).ToArray();

        Assert.Single(atA);
        Assert.Single(atB);
    }

    /// <summary>A cell with no pins contributes no pin snap features — and no exception.</summary>
    [Fact]
    public void ACellWithNoPins_ContributesNoPinSnapFeatures()
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });

        var counters = default(SnapQueryCounters);
        Assert.DoesNotContain(
            LayoutSnapFeatureIndex.Get(view, null).QueryNear(0, 0, 1000, ref counters),
            f => f.Kind == SnapFeatureKind.Pin);
    }

    // ── the drag ghost actually paints ────────────────────────────────────────

    /// <summary>
    /// The PCell drag ghost RENDERS — the overlay field being populated is not the same thing as the
    /// user seeing it, which is exactly the gap the pin overlay had (its data was right and its call
    /// site was gated shut). Asserted in pixels, at the ghost's own drop point.
    /// </summary>
    [Fact]
    public void ThePCellDragGhost_PaintsAtItsDropPoint()
    {
        var ghostView = new LayoutView { DbuPerMicron = 1000 };
        ghostView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 400, Y2 = 200 });

        var tech = new Technology
        {
            Name = "T",
            Layers = { new LayerDef { Key = new LayerKey(1, 0), Name = "M1" } },
        };

        var model = new LayoutView { DbuPerMicron = 1000 };
        var vp = new LayoutViewport(-200, -200, 0.5, 400, 300);

        using var withGhost = SKSurface.Create(new SKImageInfo(400, 300));
        LayoutRenderer.Draw(withGhost.Canvas, model, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            Overlay = new LayoutOverlay { PendingPCellPlacement = (ghostView, 100, 100) },
        });

        // Sampled INSIDE the ghost's own footprint, at the point it was told to sit.
        int sx = (int)System.Math.Round(vp.WorldToScreenX(300));
        int sy = (int)System.Math.Round(vp.WorldToScreenY(200));

        using var snapshot = withGhost.Snapshot();
        using var bitmap = SKBitmap.FromImage(snapshot);
        var painted = bitmap.GetPixel(sx, sy);

        using var noGhost = SKSurface.Create(new SKImageInfo(400, 300));
        LayoutRenderer.Draw(noGhost.Canvas, model, tech, vp, new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
        });
        using var bareSnapshot = noGhost.Snapshot();
        using var bareBitmap = SKBitmap.FromImage(bareSnapshot);

        // Compared against the SAME frame without the ghost, so this cannot pass on background alone.
        Assert.NotEqual(bareBitmap.GetPixel(sx, sy), painted);
    }
}
