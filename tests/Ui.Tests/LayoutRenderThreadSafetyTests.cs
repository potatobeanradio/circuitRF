using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  The layout model is read from TWO threads, and this is what makes that safe.
//
//  Owner-reported crash (2026-08-22), on a plain Delete in the layout editor:
//
//      System.IndexOutOfRangeException: Index was outside the bounds of the array.
//         at System.Collections.Generic.Dictionary`2.TryInsert(...)
//         at CircuitRF.Design.Layout.LayoutSpatialIndex.StrPackLeaves(List`1 entries)
//         at CircuitRF.Design.Layout.LayoutSpatialIndex.RebuildFullShapes(...)
//         at CircuitRF.Design.Layout.LayoutView.NotifyChanged(LayoutChangeInfo info)
//         at CircuitRF.Ui.Commands.Layout.DeleteShapesCommand.Execute()
//
//  An IndexOutOfRangeException raised INSIDE Dictionary.TryInsert is not a bad key — it is a
//  Dictionary being written by two threads at once, throwing from a bucket index its own array no
//  longer has. The second writer is the RENDER thread: Avalonia runs LayoutCanvas's
//  ICustomDrawOperation.Render off the UI thread, and LayoutRenderer.Draw's culling query is not
//  read-only — LayoutSpatialIndex self-heals a stale index by rebuilding it. A delete arms both
//  sides at once: Shapes.RemoveAt makes the count disagree (so the next frame rebuilds on the render
//  thread) BEFORE NotifyChanged rebuilds it on the UI thread.
//
//  These tests reproduce that shape directly. Before the fix the first one failed in ~14 ms.
// ──────────────────────────────────────────────────────────────────────────────

public class LayoutRenderThreadSafetyTests
{
    private static RectShape Rect(int i) => new()
    {
        Layer = new LayerKey(1, 0), X1 = i * 100, Y1 = 0, X2 = i * 100 + 50, Y2 = 50,
    };

    private static LayoutView ViewWith(int shapes)
    {
        var view = new LayoutView();
        for (int i = 0; i < shapes; i++) view.Shapes.Add(Rect(i));
        view.NotifyChanged();
        return view;
    }

    /// <summary>Runs <paramref name="background"/> in a loop on another thread for the duration of
    /// <paramref name="foreground"/>, and returns every exception either side threw.</summary>
    private static List<Exception> RaceOnce(Action background, Action foreground)
    {
        var errors = new List<Exception>();
        var stop = new CancellationTokenSource();

        var task = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested) background();
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }
        });

        try { foreground(); }
        catch (Exception ex) { lock (errors) errors.Add(ex); }

        stop.Cancel();
        task.Wait(TimeSpan.FromSeconds(10));
        return errors;
    }

    // ── The reported crash ────────────────────────────────────────────────────

    [Fact]
    public void QueryingWhileEditing_NeverCorruptsTheSpatialIndex()
    {
        var errors = new List<Exception>();

        for (int round = 0; round < 8 && errors.Count == 0; round++)
        {
            var view = ViewWith(1500);
            errors.AddRange(RaceOnce(
                background: () => view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(0, 0, 200_000, 100)),
                foreground: () =>
                {
                    for (int i = 0; i < 200 && view.Shapes.Count > 10; i++)
                        new DeleteShapesCommand(view, [view.Shapes.Count / 2]).Execute();
                }));
        }

        Assert.Empty(errors);
    }

    // The instance side has its own freshness path (RefreshInstances), reached from the combined
    // query the renderer, hit-test and marquee all use.
    [Fact]
    public void CombinedQueryingWhileEditing_NeverCorruptsTheSpatialIndex()
    {
        var errors = new List<Exception>();
        Bbox BoxOf(LayoutInstance i) => new(i.X, i.Y, i.X + 100, i.Y + 100);

        for (int round = 0; round < 6 && errors.Count == 0; round++)
        {
            var view = ViewWith(800);
            for (int i = 0; i < 40; i++) view.Instances.Add(new LayoutInstance { CellRef = $"c{i}", X = i * 10, Y = 0 });
            view.NotifyChanged();

            long generation = 0;
            errors.AddRange(RaceOnce(
                background: () => view.SpatialIndex.QueryIntersecting(
                    view.Shapes, view.Instances, BoxOf, Interlocked.Read(ref generation), new Bbox(0, 0, 200_000, 100)),
                foreground: () =>
                {
                    for (int i = 0; i < 150 && view.Shapes.Count > 10; i++)
                    {
                        new DeleteShapesCommand(view, [view.Shapes.Count / 2]).Execute();
                        Interlocked.Increment(ref generation);   // a resolution change, as the resolver would
                    }
                }));
        }

        Assert.Empty(errors);
    }

    // ── The renderer's own tolerance ──────────────────────────────────────────

    // Deliberately renders WITHOUT taking RenderLock — the lock is what makes a real frame atomic,
    // and this asserts the layer underneath it is still robust: a candidate index handed out by the
    // index can name a shape the list no longer has, and indexing it would throw on a thread with
    // nothing to catch it.
    [Fact]
    public void RenderingWhileEditing_NeverThrowsOnAStaleCandidateIndex()
    {
        var errors = new List<Exception>();
        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 200_000, 1000), 400, 300, marginFrac: 0.05);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, TransparentBackground = true };

        for (int round = 0; round < 6 && errors.Count == 0; round++)
        {
            var view = ViewWith(1500);
            errors.AddRange(RaceOnce(
                background: () =>
                {
                    using var surface = SKSurface.Create(new SKImageInfo(400, 300));
                    LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);
                },
                foreground: () =>
                {
                    for (int i = 0; i < 150 && view.Shapes.Count > 10; i++)
                        new DeleteShapesCommand(view, [view.Shapes.Count / 2]).Execute();
                }));
        }

        Assert.Empty(errors);
    }

    // ── What the lock must NOT cost ───────────────────────────────────────────

    // instanceBboxOf resolves a cell and unions every shape in it, uncached — running it under the
    // index's lock would hold the index across a file system walk, so it runs outside. That is only
    // affordable if a query which changes nothing does not run it at all: this is a per-FRAME path,
    // and it is also every hit-test and every marquee mouse-move.
    [Fact]
    public void ARepeatedQueryResolvesNoInstanceBboxesWhileNothingChanges()
    {
        var view = ViewWith(50);
        for (int i = 0; i < 20; i++) view.Instances.Add(new LayoutInstance { CellRef = $"c{i}", X = i * 10, Y = 0 });
        view.NotifyChanged();

        int resolves = 0;
        Bbox BoxOf(LayoutInstance i) { resolves++; return new Bbox(i.X, i.Y, i.X + 100, i.Y + 100); }

        view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, BoxOf, 7, new Bbox(0, 0, 10_000, 100));
        Assert.Equal(20, resolves);   // the first query builds the instance side

        resolves = 0;
        for (int frame = 0; frame < 10; frame++)
            view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, BoxOf, 7, new Bbox(0, 0, 10_000, 100));

        Assert.Equal(0, resolves);

        // …and a real change still refreshes.
        view.SpatialIndex.MarkInstancesDirty();
        view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, BoxOf, 7, new Bbox(0, 0, 10_000, 100));
        Assert.Equal(20, resolves);
    }

    // ── The lock itself ───────────────────────────────────────────────────────

    // NotifyChanged holds RenderLock, so a frame holding it cannot be interrupted by an index rebuild
    // or — the part that would be a native crash rather than a managed one — by LayoutPathCache
    // disposing SKPath objects the frame is still drawing.
    [Fact]
    public void NotifyChanged_TakesTheRenderLock()
    {
        var view = ViewWith(10);
        bool notifyCompleted = false;

        lock (view.RenderLock)
        {
            var blocked = Task.Run(() => { view.NotifyChanged(); notifyCompleted = true; });

            // xUnit1031 warns that a blocking wait can deadlock. Here the block IS the assertion —
            // the claim under test is that this task does NOT finish while the lock is held, and the
            // timing out of the wait is the only way to observe that. Awaiting instead is not open to
            // us either: the lock is held across this line, and Monitor is thread-affine, so a
            // continuation resuming on another thread could not release it.
#pragma warning disable xUnit1031
            Assert.False(blocked.Wait(TimeSpan.FromMilliseconds(250)), "NotifyChanged ran while a frame held RenderLock");
#pragma warning restore xUnit1031
            Assert.False(notifyCompleted);
        }

        // …and completes as soon as the frame releases it.
        Assert.True(SpinWait.SpinUntil(() => notifyCompleted, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ADeleteHoldsTheRenderLockAcrossItsListMutationAndItsNotification()
    {
        var view = ViewWith(10);
        int countSeenByFrame = -1;

        lock (view.RenderLock)
        {
            var blocked = Task.Run(() => new DeleteShapesCommand(view, [0, 1, 2]).Execute());
            Thread.Sleep(100);
            countSeenByFrame = view.Shapes.Count;      // a frame holding the lock sees the whole list…
            Assert.False(blocked.IsCompleted);
        }

        Assert.Equal(10, countSeenByFrame);            // …not the half-deleted one
    }
}
