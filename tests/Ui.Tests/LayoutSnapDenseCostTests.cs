using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  What a snap query costs when the geometry under the cursor is dense.
//
//  Snap tolerance is a fixed SCREEN distance converted to world units, so the number of features
//  inside it is set by how dense the geometry is on screen, not by anything the query controls. Over a
//  generated cell carrying a six-figure via field, a cursor at full extent had tens of thousands of
//  features within eight device pixels — every one of them collected into a growing list, every one of
//  them asked "is your layer visible?" by a LINEAR SCAN of a several-hundred-layer process stack, and
//  the whole lot then sorted. That ran on every pointer move, including every move of a marquee drag,
//  which cannot use the answer at all.
//
//  Three bounds, and these tests hold all three:
//    1. The candidate list is capped, and the cap can never change which candidate is FIRST — the only
//       one any caller acts on.
//    2. Layer visibility is a map built once per query, not a scan per feature.
//    3. A marquee drag does not run the query.
//
//  These are counter/behaviour assertions, not timings (R-L2a-3).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutSnapDenseCostTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutSnapDenseCostTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfSnapDense_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir)) Directory.Delete(_workspaceDir, recursive: true);
    }

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    /// <summary>A field of small squares on a uniform pitch, all of it well inside the tolerance used
    /// below — the shape of a via array, and enough of them that an unbounded query would return
    /// thousands.</summary>
    private static void AddDenseField(LayoutView v, int side, long pitch, long size, long originX = 0, long originY = 0)
    {
        for (int r = 0; r < side; r++)
        for (int c = 0; c < side; c++)
            v.Shapes.Add(new RectShape
            {
                Layer = LayerA,
                X1 = originX + c * pitch, Y1 = originY + r * pitch,
                X2 = originX + c * pitch + size, Y2 = originY + r * pitch + size,
            });
    }

    // ── 1. Capped, and the cap cannot move the answer ─────────────────────────────────────────

    [Fact]
    public void ADenseFieldInsideTolerance_ReturnsABoundedList_WithTheNearestFeatureStillFirst()
    {
        var model = FreshModel();
        // 60 x 60 squares on a 400 DBU pitch — 3,600 shapes, each contributing several features, all
        // of them within the tolerance below.
        AddDenseField(model, 60, pitch: 400, size: 100);
        // …and one corner exactly under the cursor, added LAST so it is nowhere near the front of any
        // unsorted collection order. It is the nearest corner there is, so it must come back first.
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 12_000, Y1 = 12_000, X2 = 12_400, Y2 = 12_400 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(
            model, null, _workspaceDir, 12_000, 12_000, 30_000,
            includeIntersections: false, null, null, ref counters);

        // The fixture's own density, asserted against the FIXTURE rather than against how much of it
        // the query walked. It used to read FeaturesExamined, which was the same number back when a
        // tolerance this wide scanned the whole cell; the index now answers a wide tolerance from the
        // buckets nearest the cursor, so that reading fell to a few hundred and the check started
        // failing on a query that had got strictly better. What it is actually there to establish is
        // that thousands of features lie inside the tolerance, so the cap below is bounding something
        // real — and that is a property of the geometry, which no future tightening can invalidate.
        Assert.True(LayoutSnapFeatureIndex.Get(model, null).FeatureCount > 5_000,
            "the fixture is not dense enough to bound anything.");
        Assert.True(result.Count <= LayoutSnapCandidateSet.Cap,
            $"{result.Count} candidates came back; the cap is {LayoutSnapCandidateSet.Cap}.");

        Assert.Equal(SnapFeatureKind.CornerEndpoint, result[0].Kind);
        Assert.Equal(12_000, result[0].X);
        Assert.Equal(12_000, result[0].Y);
    }

    /// <summary>The cap must not be able to crowd out a HIGHER-PRIORITY candidate that happens to be
    /// further away — priority beats distance (R-snp-5), so a pin at the far edge of tolerance still
    /// outranks thousands of corners nearer the cursor. This is what makes the filter-before-cap
    /// ordering inside the feature index load-bearing rather than incidental.</summary>
    [Fact]
    public void AFarPin_StillOutranksThousandsOfNearerCorners()
    {
        var model = FreshModel();
        AddDenseField(model, 60, pitch: 400, size: 100);
        model.Pins.Add(new LayoutPin { Name = "P", X = 28_000, Y = 0, Layer = LayerA });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(
            model, null, _workspaceDir, 12_000, 12_000, 30_000,
            includeIntersections: false, null, null, ref counters);

        Assert.Equal(SnapFeatureKind.Pin, result[0].Kind);
        Assert.Equal(28_000, result[0].X);
    }

    // ── 2. A hidden layer is still hidden — the cap must not admit what the filter would drop ─────
    //
    // The filter runs INSIDE the feature index now, ahead of the cap, precisely so this cannot happen.
    // Applied the other way round, the cap would fill with the dense hidden field and the one visible
    // feature — the only thing that should come back at all — would be discarded before it was tested.

    [Fact]
    public void ADenseFieldOnAHiddenLayer_ContributesNothing_AndDoesNotCrowdOutTheVisibleOne()
    {
        var hidden = new LayerKey(2, 0);
        var tech = new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers =
            [
                new LayerDef { Key = LayerA, Name = "vis", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), ZOrder = 0, Visible = true, Selectable = true },
                new LayerDef { Key = hidden, Name = "hid", Color = new CircuitRF.Ui.Theming.Rgba(0, 255, 0), ZOrder = 1, Visible = false, Selectable = true },
            ],
        };

        var model = FreshModel();
        for (int r = 0; r < 60; r++)
        for (int c = 0; c < 60; c++)
            model.Shapes.Add(new RectShape
            {
                Layer = hidden,
                X1 = c * 400, Y1 = r * 400, X2 = c * 400 + 100, Y2 = r * 400 + 100,
            });
        model.Shapes.Add(new RectShape { Layer = LayerA, X1 = 12_000, Y1 = 12_000, X2 = 12_400, Y2 = 12_400 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(
            model, tech, _workspaceDir, 12_000, 12_000, 30_000,
            includeIntersections: false, null, null, ref counters);

        Assert.NotEmpty(result);
        Assert.All(result, c => Assert.Equal(LayerA, c.Layer));
    }

    // ── 3. A marquee drag runs no snap query at all ───────────────────────────────────────────
    //
    // Its rectangle is built from the raw pointer position and its commit reads only the marquee's own
    // hit computation, so the query was pure cost — and it ran on every move of the one gesture that
    // sweeps the cursor across the most geometry.

    [Fact]
    public void MarqueeDrag_RunsNoSnapQuery()
    {
        var model = FreshModel();
        AddDenseField(model, 60, pitch: 400, size: 100);
        var vm = new LayoutEditorViewModel(model, null) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        // Press on empty canvas, far from any geometry, so the gesture is a marquee and not a
        // snap-marker grab or a shape move.
        vm.OnPointerPressed(-50_000, -50_000, KeyModifiers.None, 1, 40);
        int afterPress = vm.SnapQueryRunCount;

        for (int i = 1; i <= 8; i++)
            vm.OnPointerMoved(-50_000 + i * 4_000, -50_000 + i * 4_000, leftDown: true,
                              KeyModifiers.None, hitTolDbu: 40, pixelDbu: 1, snapTolDbu: 3_000);

        Assert.Equal(afterPress, vm.SnapQueryRunCount);
        Assert.Null(vm.Overlay.SnapMarker);
    }

    /// <summary>The control for the test above: the SAME moves, with no button down, do run the query.
    /// Without this, deleting the marquee check would leave both tests green.</summary>
    [Fact]
    public void TheSameMovesWithoutADrag_DoRunTheSnapQuery()
    {
        var model = FreshModel();
        AddDenseField(model, 60, pitch: 400, size: 100);
        var vm = new LayoutEditorViewModel(model, null) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        int before = vm.SnapQueryRunCount;
        for (int i = 1; i <= 8; i++)
            vm.OnPointerMoved(-50_000 + i * 4_000, -50_000 + i * 4_000, leftDown: false,
                              KeyModifiers.None, hitTolDbu: 40, pixelDbu: 1, snapTolDbu: 3_000);

        Assert.True(vm.SnapQueryRunCount > before);
    }
}
