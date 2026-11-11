using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using SkiaSharp;
using Xunit;

// The namespace is NOT `CircuitRF.Ui.Tests.Stackup`, though the folder is — see the note at the top
// of StackupSceneTests.cs: a namespace segment named `Stackup` shadows the `Stackup` TYPE for every
// file under `CircuitRF.Ui.Tests`.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>
/// brief-stackup-render-3-selection.md's gate — click a band, land on its fields.
///
/// <h3>What stands in for a pointer and for a rendered picture</h3>
/// <para>There is no input device in this project and no app host, so a click is
/// <see cref="StackupCanvas.PressAt"/> and a hover is <c>MoveAt</c> — the same kind of internal seam
/// <c>MeasureForWidth</c> and <c>SceneCache</c> already are. What they exercise is the real control
/// over the real scene; only the event plumbing is bypassed.</para>
///
/// <para><see cref="ScrollIntoView"/> itself needs a realised, arranged <c>ListBox</c>. What is
/// asserted instead is the DECISION — which row was asked for, and that it is in
/// <c>FilteredStackupLayers</c> when it is asked for. A test that arranged a <c>ListBox</c> to read
/// back a scroll offset would be measuring the layout engine.</para>
/// </summary>
// SkiaFontsTypefaceCollection: this class ASSERTS OVER RENDERED TEXT BYTES, which is the second
// half of that collection's stated membership rule and the half it says is easy to miss — a class
// like this one looks as though it touches no global at all. It does not set either typeface static;
// what it cannot survive is another class setting one WHILE it renders. Caught in a full-solution
// run (2026-09-13): two builds of one scene came back with `font-family="Helvetica"` on one side and
// IBM Plex on the other, and the same test passed alone.
[Collection(CircuitRF.Ui.Tests.SkiaFontsTypefaceCollection.Name)]
public class StackupSelectionTests
{
    private const double PaneWidth = 900;
    private const string FourLayerId = "pcb-4layer_FR-4_62mil_1oz";

    private static TechEditorViewModel Editor(string id = FourLayerId) =>
        new(Path.Combine(Path.GetTempPath(), "stackup-selection-tests.ctech"),
            ShippedTechnologies.Load(id));

    private static StackupCanvas Canvas(TechEditorViewModel vm)
    {
        var canvas = new StackupCanvas { ViewModel = vm };
        canvas.MeasureForWidth(PaneWidth);   // the scene the click will be hit-tested against
        return canvas;
    }

    // ── R-stk3-1 — the selection survives a committed edit ───────────────────────────────────────

    /// <summary>
    /// <b>The test that pins the whole design decision.</b> <c>ApplySnapshot</c> assigns
    /// <c>Working</c> a freshly deserialized technology, so every <c>StackupLayer</c> is replaced on
    /// every committed edit, undo and redo, and every row VM too whenever entries are added, removed
    /// or reordered across kinds. A selection held as a <c>StackupLayer</c> fails this outright, and
    /// one held as a row-VM reference fails the first add; a name survives both.
    /// </summary>
    [Fact]
    public void TheSelectionSurvivesACommittedEditOnADifferentEntry()
    {
        var vm = Editor();
        var conductor = vm.StackupLayers.First(r => r.Layer.Kind == StackupKind.Conductor);
        var other     = vm.StackupLayers.First(r => r.Layer.Kind == StackupKind.Dielectric);

        vm.SelectedStackupLayerName = conductor.Layer.Name;
        var name = conductor.Layer.Name;

        other.StagedThicknessText = "1.234";
        other.CommitThickness();

        Assert.Equal(name, vm.SelectedStackupLayerName);

        // …and it still RESOLVES, to an entry of the technology that exists now. A value edit keeps the
        // row VMs and rebinds them (so the card list does not move — StackupLayerRowViewModel.Rebind),
        // but the StackupLayer under them is the new snapshot's, not the one the selection was made on.
        Assert.NotNull(vm.SelectedStackupLayerRow);
        Assert.Equal(name, vm.SelectedStackupLayerRow!.Layer.Name);
        Assert.Contains(vm.SelectedStackupLayerRow, vm.StackupLayers);
        Assert.Same(vm.Working.Stackup.Layers.First(l => l.Name == name), vm.SelectedStackupLayerRow.Layer);
        Assert.True(vm.SelectedStackupLayerRow.IsSelected);
    }

    /// <summary>
    /// A committed VALUE leaves the card list's items alone — no CollectionChanged at all, on the
    /// commit or its undo — because a replaced item is laid out again from the virtualizing panel's
    /// size estimates and the cards move under the user (designer report, 2026-10-12: 66 px measured
    /// headlessly). An added entry still changes the list.
    /// </summary>
    [Fact]
    public void ACommittedValueLeavesTheCardListsItemsAlone()
    {
        var vm    = Editor();
        var rows  = vm.FilteredStackupLayers.ToList();
        int changes = 0;
        vm.FilteredStackupLayers.CollectionChanged += (_, _) => changes++;

        var sub = vm.StackupLayers.First(r => r.IsDielectric);
        sub.StagedEpsr = "3.9";
        sub.CommitEpsr();
        vm.UndoRedo.Undo();

        Assert.Equal(0, changes);
        Assert.Equal(rows, vm.FilteredStackupLayers);
        Assert.Same(vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric), sub.Layer);

        vm.AddConductorLayerCommand.Execute(null);
        Assert.NotEqual(0, changes);
    }

    [Fact]
    public void AnUndoAndARedoBothKeepIt()
    {
        var vm = Editor();
        var target = vm.StackupLayers.First(r => r.Layer.Kind == StackupKind.Conductor);
        var name = target.Layer.Name;
        vm.SelectedStackupLayerName = name;

        vm.AddConductorLayerCommand.Execute(null);
        Assert.Equal(name, vm.SelectedStackupLayerRow?.Layer.Name);

        vm.UndoRedo.Undo();
        Assert.Equal(name, vm.SelectedStackupLayerRow?.Layer.Name);

        vm.UndoRedo.Redo();
        Assert.Equal(name, vm.SelectedStackupLayerRow?.Layer.Name);
    }

    // ── R-stk3-2 — what a click selects ─────────────────────────────────────────────────────────

    [Fact]
    public void AClickInsideABandSelectsThatBand()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        foreach (var band in scene.Bands)
        {
            vm.SelectedStackupLayerName = null;
            Assert.True(canvas.PressAt(PointInBand(scene, band)));
            Assert.Equal(band.Name, vm.SelectedStackupLayerName);
        }
    }

    [Fact]
    public void AClickOnTheBackgroundClearsIt()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        canvas.PressAt(PointInBand(scene, scene.Bands[0]));
        Assert.NotNull(vm.SelectedStackupLayerName);

        // 0,0 is the pane's own top-left corner: outside the gutter, above the first note.
        Assert.Null(scene.HitTest(0, 0));
        canvas.PressAt(new Avalonia.Point(0, 0));

        Assert.Null(vm.SelectedStackupLayerName);
        Assert.Null(vm.SelectedStackupLayerRow);
        Assert.All(vm.StackupLayers, r => Assert.False(r.IsSelected));
    }

    /// <summary>A barrel is drawn ACROSS the bands it spans, so the same point is inside both. The
    /// scene's hit order is the draw order back to front and <c>HitTest</c> walks it backwards, which
    /// is what makes the picture and the pointer agree (R-stk1-1).</summary>
    [Fact]
    public void AClickOnAViaBarrelSelectsTheVia_NotTheConductorBehindIt()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;
        Assert.NotEmpty(scene.Barrels);

        foreach (var barrel in scene.Barrels)
        {
            var p = new Avalonia.Point(barrel.Rect.MidX, barrel.Rect.MidY);

            // The point really is inside a band too, or this proves nothing about precedence.
            Assert.Contains(scene.Bands, b => b.Rect.Contains((float)p.X, (float)p.Y));

            vm.SelectedStackupLayerName = null;
            canvas.PressAt(p);
            Assert.Equal(barrel.Name, vm.SelectedStackupLayerName);
        }
    }

    /// <summary>A label hit selects the layer the label belongs to — brief 4 makes a DOUBLE click on
    /// a value open an editor; a single click still just selects.</summary>
    [Fact]
    public void AClickOnALabelSelectsItsOwnLayer()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        var label = scene.Labels.First(l => l.LayerName.Length > 0 && l.Field != StackupField.None);
        canvas.PressAt(new Avalonia.Point(label.Rect.MidX, label.Rect.MidY));

        Assert.Equal(label.LayerName, vm.SelectedStackupLayerName);
    }

    [Fact]
    public void ACanvasWithNoViewModelSelectsNothingAndDoesNotThrow()
    {
        var canvas = new StackupCanvas();
        canvas.MeasureForWidth(PaneWidth);
        Assert.False(canvas.PressAt(new Avalonia.Point(100, 100)));
    }

    // ── R-stk3-3 — the outline ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// OUTSIDE the band's own edge, with a gap — so it never hides the ground-reference heavy edge,
    /// which brief 1 §1 makes the one distinguishing mark on the whole picture.
    /// </summary>
    [Fact]
    public void TheOutlineRectIsStrictlyOutsideTheBandRect()
    {
        var scene = StackupScene.Build(ShippedTechnologies.Load(FourLayerId), (float)PaneWidth);

        foreach (var band in scene.Bands)
        {
            var outline = StackupRenderer.OutlineRectFor(band.Rect);
            Assert.True(outline.Contains(band.Rect), $"{band.Name}: {outline} must contain {band.Rect}");
            Assert.True(outline.Left   < band.Rect.Left);
            Assert.True(outline.Top    < band.Rect.Top);
            Assert.True(outline.Right  > band.Rect.Right);
            Assert.True(outline.Bottom > band.Rect.Bottom);
        }

        // The GAP is what the rule is actually about: a stroke is centred on its rect, so the
        // outline's inner face has to clear the heaviest edge the picture draws.
        Assert.True(StackupRenderer.SelectionGap - StackupRenderer.SelectionWidth / 2
                    > StackupRenderer.GroundEdgeWidth / 2,
            "the selection outline must not touch the ground reference's heavy edge");
    }

    /// <summary>For a via, around the BARREL — not around the bands it crosses.</summary>
    [Fact]
    public void AViasOutlineFramesItsBarrel()
    {
        var scene = StackupScene.Build(ShippedTechnologies.Load(FourLayerId), (float)PaneWidth);
        var barrel = scene.Barrels[0];

        var rect = scene.RectOf(barrel.Name);
        Assert.NotNull(rect);
        Assert.Equal(barrel.Rect, rect!.Value);
    }

    [Fact]
    public void TheSelectedLayerReachesTheRenderer()
    {
        var scene = StackupScene.Build(ShippedTechnologies.Load(FourLayerId), (float)PaneWidth);

        var none  = Svg(scene, StackupOverlay.Empty);
        var first = Svg(scene, new StackupOverlay { SelectedLayer = scene.Bands[0].Name });
        var last  = Svg(scene, new StackupOverlay { SelectedLayer = scene.Bands[^1].Name });

        Assert.NotEqual(none, first);
        Assert.NotEqual(first, last);

        // A name that is in no stackup draws nothing — RectOf returns null and the renderer skips it.
        Assert.Equal(none, Svg(scene, new StackupOverlay { SelectedLayer = "no such layer" }));
    }

    // ── R-stk3-4 — hover, which is GATED OFF (owner, 2026-09-13) ────────────────────────────────

    /// <summary>
    /// <b>The switch is off, and with it off a pointer crossing the drawing changes nothing.</b>
    ///
    /// <para>Owner, 2026-09-13: remove the mouse-over highlighting, keep the code, gate it, keep it
    /// off. So the default is the behaviour under test — and it is asserted as the DEFAULT rather
    /// than by setting it, because "it is off" is the requirement.</para>
    ///
    /// <para>The canvas does not even hit-test: with nothing to publish, tracking is pure cost on a
    /// pointer that raises a move event per pixel.</para>
    /// </summary>
    [Fact]
    public void HoverHighlightingIsOffByDefault_AndThePointerChangesNothing()
    {
        Assert.False(StackupCanvas.HoverHighlighting);

        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        string before = Svg(scene, canvas.CurrentOverlay);

        canvas.MoveAt(PointInBand(scene, scene.Bands[1]));
        Assert.Null(canvas.HoverLayer);
        Assert.Null(canvas.CurrentOverlay.HoverLayer);
        Assert.Null(vm.SelectedStackupLayerName);

        // …and the picture is the same one, which is the whole of what "removed" means here.
        Assert.Equal(before, Svg(scene, canvas.CurrentOverlay));
    }

    /// <summary>
    /// The gated-off code still WORKS, which is what "keep it" has to mean — a feature switched off
    /// and left to rot is one nobody can switch back on.
    ///
    /// <para>R-stk3-4's own two properties, exercised through the switch: hover tracks the pointer and
    /// clears on exit, and it selects nothing — hover is the CANVAS's while the selection is the VIEW
    /// MODEL's, so a pointer crossing the drawing must never rewrite the card list.</para>
    /// </summary>
    [Fact]
    public void WithTheSwitchOn_HoverStillTracksThePointerAndStillSelectsNothing()
    {
        using var _ = HoverOn();

        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        canvas.MoveAt(PointInBand(scene, scene.Bands[1]));
        Assert.Equal(scene.Bands[1].Name, canvas.HoverLayer);
        Assert.Equal(scene.Bands[1].Name, canvas.CurrentOverlay.HoverLayer);
        Assert.Null(vm.SelectedStackupLayerName);

        canvas.MoveAt(new Avalonia.Point(0, 0));
        Assert.Null(canvas.HoverLayer);
    }

    /// <summary>Turns the switch on for one test and puts it back. It is a process-wide static, so
    /// this class is party to <c>SkiaFontsTypefaceCollection</c> for the schedule as well as for the
    /// typeface — and it is the ONLY class that flips it: every other stackup test that calls
    /// <c>MoveAt</c> is driving a DRAG, which the gate is read after.</summary>
    private static IDisposable HoverOn()
    {
        StackupCanvas.HoverHighlighting = true;
        return new Restore(() => StackupCanvas.HoverHighlighting = false);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void AHoveredViaRevealsItsGrippers_AndAHoverOutlineIsDrawn()
    {
        var scene = StackupScene.Build(ShippedTechnologies.Load(FourLayerId), (float)PaneWidth);

        var none    = Svg(scene, StackupOverlay.Empty);
        var hovered = Svg(scene, new StackupOverlay { HoverLayer = scene.Barrels[0].Name });
        Assert.NotEqual(none, hovered);

        // A band that is BOTH hovered and selected shows the selected outline and no second one —
        // the two are the same rect, so drawing both would only thicken it.
        var band = scene.Bands[0].Name;
        Assert.Equal(
            Svg(scene, new StackupOverlay { SelectedLayer = band }),
            Svg(scene, new StackupOverlay { SelectedLayer = band, HoverLayer = band }));
    }

    // ── R-stk3-5 — the filter cannot hide the thing the user just clicked ───────────────────────

    [Fact]
    public void AClickOnABandTheFilterExcludesClearsTheFilter()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;
        var band = scene.Bands[^1];

        // A filter that matches something, and not the band about to be clicked.
        vm.StackupFilter = "Prepreg";
        Assert.DoesNotContain(vm.FilteredStackupLayers, r => r.Layer.Name == band.Name);

        canvas.PressAt(PointInBand(scene, band));

        Assert.Equal("", vm.StackupFilter);
        Assert.Equal(band.Name, vm.SelectedStackupLayerName);

        // The DECISION ScrollIntoView is given: the row exists, and it is in the projection the list
        // is bound to.
        Assert.NotNull(vm.SelectedStackupLayerRow);
        Assert.Contains(vm.SelectedStackupLayerRow!, vm.FilteredStackupLayers);
    }

    /// <summary>A filter that already shows the clicked band is left exactly as the user typed it —
    /// the clear is a remedy for a click that would otherwise appear to do nothing, not a policy.</summary>
    [Fact]
    public void AFilterThatWouldNotHideItIsLeftAlone()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        var band = scene.Bands.First(b => b.Name.Contains("Copper", StringComparison.Ordinal));
        vm.StackupFilter = "Copper";

        canvas.PressAt(PointInBand(scene, band));

        Assert.Equal("Copper", vm.StackupFilter);
        Assert.Contains(vm.SelectedStackupLayerRow!, vm.FilteredStackupLayers);
    }

    // ── R-stk3-5's scroll: to the card's TOP, eased, and painted once (owner, 2026-09-13) ───────

    /// <summary>
    /// <b>The card scroll never moves the list anywhere it is not going.</b>
    ///
    /// <para>It used to call <c>ScrollIntoView</c> to REALISE the card's container, measure off it,
    /// restore the offset and ease from there — all in one dispatcher frame, so the jump itself was
    /// never painted. It still flashed going DOWN (owner, 2026-09-13), because the jump de-realises
    /// everything at the origin and the restored offset is rendered before the virtualizing panel has
    /// realised it again: what flashed was a blank viewport, not a wrong position.</para>
    ///
    /// <para>So the scroll sets out on an ESTIMATE and the animator asks for the truth every frame.
    /// This asserts the shape that has no jump in it, which is the part a source scan can see; the
    /// re-aiming itself is <see cref="TheScrollEasesOut_StartingAtOnceAndSettlingAtTheEnd"/>'s
    /// neighbour below.</para>
    ///
    /// <para>The scroll is <c>ListBoxEasedScroll.BringToTop</c>'s now, which checks where it landed. The view's own
    /// copy of the ease had no such check, and a virtualizing list re-estimates its extent as it scrolls, so a card
    /// unrealised when the scroll set out (the TFR Level after Silicon) was missed and never found.</para>
    /// </summary>
    [Fact]
    public void TheCardScrollNeverJumpsTheListToMeasureIt()
    {
        // The view hands the scroll to the shared eased scroll, with its own one animator.
        var view = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));
        var handler = StripComments(view[At(view, "private void ScrollStackupSelectionIntoView()")..
                                         At(view, "private readonly ScrollOffsetAnimator")]);
        At(handler, "ListBoxEasedScroll.BringToTop(StackupList, current, rows.IndexOf(current), rows.Count, _stackupScroll)");
        Assert.DoesNotContain("ScrollIntoView", handler, StringComparison.Ordinal);

        var code = RepoFile(Path.Combine("src", "Ui", "Controls", "ListBoxEasedScroll.cs"));
        int at   = At(code, "private static void BringToTop(");
        // COMMENTS STRIPPED FIRST, the convention every source scan in this repo follows.
        var body = StripComments(code[at..]);

        // The exact position when it is knowable, an estimate when it is not, and a retarget callback
        // so the ease is re-aimed as the card is realised.
        At(body, "TopOffsetOf(list, item, scroll) ?? EstimatedOffsetOf(index, count, scroll)");
        int animate = At(body, "animator.AnimateTo(scroll, target, () => TopOffsetOf(list, item, scroll)");

        // ScrollIntoView appears in two places only, and neither measures and puts the offset back: the path with
        // no scroller to animate, before anything moves, and the LAST round's fallback, after the ease has landed
        // and the card is still unrealised, which moves the list on toward the card from where the ease ended.
        int fallback  = At(body, "if (scroll is null)");
        int first     = body.IndexOf("ScrollIntoView", StringComparison.Ordinal);
        int lastRound = At(body, "if (rounds <= 1)");
        int second    = body.IndexOf("ScrollIntoView", first + 1, StringComparison.Ordinal);
        Assert.True(first > fallback && first < animate, "the first ScrollIntoView is the no-scroller fallback");
        Assert.True(second > lastRound, "the second is the last round's, after landing");
        Assert.Equal(second, body.LastIndexOf("ScrollIntoView", StringComparison.Ordinal));

        // And the one direct offset write puts the realised card's top at the top, after that fallback — every
        // other movement goes through the ease.
        int write = At(body, "scroll.Offset = ");
        Assert.True(write > second, "the only direct write follows the last round's ScrollIntoView");
        Assert.Equal(write, body.LastIndexOf("scroll.Offset = ", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The card list does not scroll itself.</b>
    ///
    /// <para>Owner, 2026-09-13, three times: "a quick flash in the scrollview". Twice it was looked
    /// for in this view's own handler, and it was never there.
    /// <c>SelectingItemsControl.AutoScrollToSelectedItem</c> defaults to TRUE, and its
    /// <c>AutoScrollToSelectedItemIfNecessary</c> posts a <c>ScrollIntoView</c> that runs BEFORE this
    /// view's Background-priority handler — so every selection change made TWO movements: the list's
    /// own instant jump, rendered, then the eased scroll starting from wherever that left it. The jump
    /// is the flash.</para>
    ///
    /// <para>The default is asserted as well as the override, so this test says why the override is
    /// needed rather than merely that it is present — and fails loudly if a future Avalonia flips
    /// it.</para>
    /// </summary>
    [Fact]
    public void TheCardListsOwnAutoScrollIsOff_SoTheOnlyScrollIsTheEasedOne()
    {
        var tab = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

        int list = At(tab, "x:Name=\"StackupList\"");
        Assert.Contains("AutoScrollToSelectedItem=\"False\"", tab[list..(list + 600)],
                        StringComparison.Ordinal);

        // Non-vacuity: it is off because it is ON by default, and that is what made the list jump.
        Assert.True((bool)Avalonia.Controls.Primitives.SelectingItemsControl
            .AutoScrollToSelectedItemProperty.GetMetadata(typeof(Avalonia.Controls.ListBox))
            .DefaultValue!);
    }

    /// <summary>A card already wholly on screen is left where the user has it — with the list's own
    /// auto-scroll off, this handler is the only thing that decides not to move.</summary>
    [Fact]
    public void ACardAlreadyOnScreenIsNotScrolledAtAll()
    {
        var code = RepoFile(Path.Combine("src", "Ui", "Controls", "ListBoxEasedScroll.cs"));
        var body = StripComments(code[At(code, "private static void BringToTop(")..]);

        int guard   = At(body, "if (IsFullyVisible(list, item, scroll)) return;");
        int animate = At(body, "animator.AnimateTo");
        Assert.True(guard < animate, "the do-nothing case is decided before anything moves");
    }

    /// <summary>The re-aiming itself: a target that could not be known when the scroll started is
    /// adopted mid-flight, and stops being listened to before the landing so the scroll does not
    /// visibly settle twice.</summary>
    [Fact]
    public void TheScrollAdoptsATargetItCouldNotKnowWhenItStarted()
    {
        var code = RepoFile(Path.Combine("src", "Ui", "Controls", "ScrollOffsetAnimator.cs"));
        var tick = StripComments(code[At(code, "private void OnTick")..]);

        Assert.Contains("_retarget?.Invoke()", tick, StringComparison.Ordinal);
        Assert.Contains("RetargetUntil", tick, StringComparison.Ordinal);

        // Re-aimed, not restarted: _from and the start time are what keep it one continuous movement.
        Assert.DoesNotContain("_from =", tick, StringComparison.Ordinal);
        Assert.DoesNotContain("_startedAt =", tick, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ease itself, which is the only part of an animated scroll that can be checked without a
    /// window: it starts where it started, ends where it was sent, is monotone, and is an ease OUT —
    /// most of the distance is covered in the first half, because the scroll is a response to a click
    /// that has already happened and a lag at the front of it would be the one unacceptable thing.
    /// </summary>
    [Fact]
    public void TheScrollEasesOut_StartingAtOnceAndSettlingAtTheEnd()
    {
        Assert.Equal(0, ScrollOffsetAnimator.Ease(0), 6);
        Assert.Equal(1, ScrollOffsetAnimator.Ease(1), 6);

        double previous = -1;
        for (double t = 0; t <= 1.0001; t += 0.05)
        {
            double e = ScrollOffsetAnimator.Ease(t);
            Assert.True(e > previous, $"the ease must be monotone; it went back at t={t}");
            previous = e;
        }

        Assert.True(ScrollOffsetAnimator.Ease(0.5) > 0.75, "an ease-OUT covers most of it early");

        // Short enough never to be something to wait for.
        Assert.InRange(ScrollOffsetAnimator.DurationMs, 80, 300);
    }

    // ── R-stk3-6 — the shaded card ──────────────────────────────────────────────────────────────

    [Fact]
    public void ExactlyOneRowIsMarkedSelected()
    {
        var vm = Editor();
        var target = vm.StackupLayers[2];

        vm.SelectedStackupLayerName = target.Layer.Name;

        Assert.Single(vm.StackupLayers, r => r.IsSelected);
        Assert.True(target.IsSelected);
        Assert.All(vm.StackupLayers.Where(r => r != target), r => Assert.False(r.IsSelected));

        vm.SelectedStackupLayerName = null;
        Assert.All(vm.StackupLayers, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public void TheCardBindsItsBackgroundToIsSelected()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));

        Assert.Contains("Classes.selected=\"{Binding IsSelected}\"", axaml);
        Assert.Contains("Selector=\"Border.stackupcard.selected\"", axaml);
        Assert.Contains("<Setter Property=\"Background\" Value=\"{DynamicResource CrfStackupSelectedCardBrush}\"/>",
                        axaml);

        // SHADE, not outline, and low-alpha so the card's own text and its needsvalue marking stay
        // legible on top of it.
        var resources = RepoFile(Path.Combine("src", "Ui", "Styles", "CircuitRfResources.axaml"));
        int key = resources.IndexOf("x:Key=\"CrfStackupSelectedCardBrush\"", StringComparison.Ordinal);
        Assert.True(key >= 0, "the selected-card brush must be an application-scope resource");

        var color = resources[key..resources.IndexOf("/>", key, StringComparison.Ordinal)];
        var alpha = Convert.ToInt32(color[(color.IndexOf("Color=\"#", StringComparison.Ordinal) + 8)..][..2], 16);
        Assert.InRange(alpha, 1, 80);
    }

    // ── R-stk3-7 — one selection, two surfaces, no re-entrancy ──────────────────────────────────

    [Fact]
    public void SelectingACardSelectsItsBand()
    {
        var vm = Editor();
        var row = vm.StackupLayers[3];

        vm.SelectedStackupLayerRow = row;

        Assert.Equal(row.Layer.Name, vm.SelectedStackupLayerName);
        Assert.True(row.IsSelected);
    }

    [Fact]
    public void SettingTheNameSelectsTheCard()
    {
        var vm = Editor();
        var row = vm.StackupLayers[3];

        vm.SelectedStackupLayerName = row.Layer.Name;

        Assert.Same(row, vm.SelectedStackupLayerRow);
    }

    /// <summary>Each surface raises its own change exactly once per selection — a second raise is
    /// what a re-entrant round trip looks like from the outside.</summary>
    [Fact]
    public void NeitherDirectionReEnters()
    {
        var vm = Editor();
        var counts = new Dictionary<string, int>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } n) counts[n] = counts.GetValueOrDefault(n) + 1;
        };

        vm.SelectedStackupLayerName = vm.StackupLayers[1].Layer.Name;
        Assert.Equal(1, counts.GetValueOrDefault(nameof(TechEditorViewModel.SelectedStackupLayerName)));
        Assert.Equal(1, counts.GetValueOrDefault(nameof(TechEditorViewModel.SelectedStackupLayerRow)));

        counts.Clear();
        vm.SelectedStackupLayerRow = vm.StackupLayers[4];
        Assert.Equal(1, counts.GetValueOrDefault(nameof(TechEditorViewModel.SelectedStackupLayerName)));
        Assert.Equal(1, counts.GetValueOrDefault(nameof(TechEditorViewModel.SelectedStackupLayerRow)));
    }

    /// <summary>
    /// The ListBox drops its own <c>SelectedItem</c> — and pushes the null back through the two-way
    /// binding — whenever the collection it is bound to is emptied. That happens on every keystroke
    /// in the filter box and on every committed edit, so taking the write-back at face value would
    /// clear the selection constantly, silently, and for no reason the user could see.
    /// </summary>
    [Fact]
    public void AFilterKeystrokeDoesNotClearTheSelection()
    {
        var vm = Editor();
        var name = vm.StackupLayers[2].Layer.Name;
        vm.SelectedStackupLayerName = name;

        // What a bound ListBox does as ApplyStackupFilter clears and refills the collection.
        vm.FilteredStackupLayers.CollectionChanged += (_, _) => vm.SelectedStackupLayerRow = null;

        vm.StackupFilter = "Copper";

        Assert.Equal(name, vm.SelectedStackupLayerName);
    }

    [Fact]
    public void TheListBoxSelectionIsBoundTwoWay()
    {
        var axaml = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        int list = axaml.IndexOf("x:Name=\"StackupList\"", StringComparison.Ordinal);
        Assert.True(list >= 0);

        var element = axaml[list..axaml.IndexOf('>', list)];
        Assert.Contains("SelectedItem=\"{Binding ViewModel.SelectedStackupLayerRow, Mode=TwoWay}\"", element);
    }

    // ── R-stk3-8 — a rename keeps the selection ─────────────────────────────────────────────────

    [Fact]
    public void RenamingTheSelectedEntryKeepsItSelected()
    {
        var vm = Editor();
        var row = vm.StackupLayers.First(r => r.Layer.Kind == StackupKind.Conductor);
        vm.SelectedStackupLayerName = row.Layer.Name;

        row.StagedName = "Renamed Metal";
        row.CommitName();

        Assert.Equal("Renamed Metal", vm.SelectedStackupLayerName);
        Assert.NotNull(vm.SelectedStackupLayerRow);
        Assert.Equal("Renamed Metal", vm.SelectedStackupLayerRow!.Layer.Name);
        Assert.True(vm.SelectedStackupLayerRow.IsSelected);
    }

    /// <summary>Renaming a DIFFERENT entry must not steal the selection.</summary>
    [Fact]
    public void RenamingAnUnselectedEntryLeavesTheSelectionWhereItWas()
    {
        var vm = Editor();
        var selected = vm.StackupLayers[0];
        var other    = vm.StackupLayers[1];
        vm.SelectedStackupLayerName = selected.Layer.Name;
        var name = selected.Layer.Name;

        other.StagedName = "Something Else";
        other.CommitName();

        Assert.Equal(name, vm.SelectedStackupLayerName);
        Assert.Equal(name, vm.SelectedStackupLayerRow?.Layer.Name);
    }

    // ── R-stk3-9 — Esc ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearingTheSelectionClearsTheOutlineTheCardAndTheListSelectionTogether()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;
        canvas.PressAt(PointInBand(scene, scene.Bands[0]));
        Assert.NotNull(canvas.CurrentOverlay.SelectedLayer);

        vm.ClearStackupSelection();

        Assert.Null(vm.SelectedStackupLayerName);
        Assert.Null(vm.SelectedStackupLayerRow);
        Assert.Null(canvas.CurrentOverlay.SelectedLayer);
        Assert.All(vm.StackupLayers, r => Assert.False(r.IsSelected));
    }

    /// <summary>
    /// <c>Esc</c> does NOT put back a filter the selection cleared. Undoing that on <c>Esc</c> would
    /// make <c>Esc</c> a second undo, which it is not.
    /// </summary>
    [Fact]
    public void EscDoesNotRestoreAFilterTheSelectionCleared()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var scene = canvas.SceneCache.Current!;

        vm.StackupFilter = "Prepreg";
        canvas.PressAt(PointInBand(scene, scene.Bands[^1]));
        Assert.Equal("", vm.StackupFilter);

        vm.ClearStackupSelection();

        Assert.Equal("", vm.StackupFilter);
    }

    /// <summary>
    /// The keystroke itself is a TUNNELLING handler on the view, not a key handler on the drawing:
    /// the canvas is deliberately not focusable (R-stk2-10 — a focusable control inside the drawing's
    /// ScrollViewer re-points Page Up/Down at the drawing), and the ask is "pressing Esc will
    /// unselect", not "pressing Esc while the drawing happens to have focus". So it has to reach the
    /// card list too, which one tunnelling handler on the view does and a canvas handler cannot.
    /// </summary>
    [Fact]
    public void EscIsWiredAsATunnellingHandlerOnTheView_AndTheCanvasStillTakesNoFocus()
    {
        var code = RepoFile(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs"));

        // handledEventsToo, because WorkspaceWindow's own `<KeyBinding Gesture="Escape" …/>` marks
        // the key handled before visual-tree routing begins — without the flag this handler is
        // skipped entirely and Esc does nothing here (owner-reported twice, 2026-09-13).
        Assert.Contains(
            "AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);",
            code, StringComparison.Ordinal);

        int handler = code.IndexOf("private void OnEscapeKeyDown", StringComparison.Ordinal);
        Assert.True(handler >= 0);
        var body = code[handler..code.IndexOf("\n    }", handler, StringComparison.Ordinal)];
        Assert.Contains("Key.Escape", body);
        Assert.Contains("ClearStackupSelection()", body);

        Assert.False(new StackupCanvas().Focusable);
    }

    // ── The canvas hands the renderer what the view model says ──────────────────────────────────

    [Fact]
    public void TheCanvasOverlayFollowsTheViewModel_WithNoCopyOfItsOwn()
    {
        var vm = Editor();
        var canvas = Canvas(vm);

        vm.SelectedStackupLayerName = vm.StackupLayers[2].Layer.Name;
        Assert.Equal(vm.StackupLayers[2].Layer.Name, canvas.CurrentOverlay.SelectedLayer);

        // Re-pointed at a different editor, the canvas shows THAT editor's selection — it holds none.
        var second = Editor("mmic-GaAs_2LM_100um");
        canvas.ViewModel = second;
        Assert.Null(canvas.CurrentOverlay.SelectedLayer);

        second.SelectedStackupLayerName = second.StackupLayers[0].Layer.Name;
        Assert.Equal(second.StackupLayers[0].Layer.Name, canvas.CurrentOverlay.SelectedLayer);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A point inside <paramref name="band"/> that the scene agrees belongs to it — scanned rather
    /// than computed, because a barrel crosses the band at its own x and a label sits on it, and both
    /// are supposed to win there.
    /// </summary>
    private static Avalonia.Point PointInBand(StackupScene scene, StackupBand band)
    {
        float y = band.Rect.MidY;
        for (float x = band.Rect.Left + 2; x < band.Rect.Right - 2; x += 3)
            if (scene.HitTest(x, y) is { } hit && hit.LayerName == band.Name &&
                hit.Kind == StackupHitKind.Band)
                return new Avalonia.Point(x, y);

        throw new InvalidOperationException($"no point of band {band.Name} hit-tests to it");
    }

    private static string Svg(StackupScene scene, StackupOverlay overlay) =>
        PlotDocumentWriter.BuildSvgString(
            canvas => StackupRenderer.Draw(canvas, scene, StackupRenderTheme.Light, overlay),
            new PagePlacement(scene.Width, scene.Height, 0f));

    /// <summary>A C# source block with its <c>//</c> comments removed, so a scan asserts what the code
    /// DOES rather than what its rationale says.</summary>
    private static string StripComments(string source)
        => System.Text.RegularExpressions.Regex.Replace(source, @"//[^\r\n]*", "");

    /// <summary>The index of <paramref name="needle"/>, failing the test by NAME when it is absent —
    /// a source scan that silently matched nothing would be a passing test of nothing.</summary>
    private static int At(string haystack, string needle)
    {
        int i = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(i >= 0, $"expected to find: {needle}");
        return i;
    }

    private static string RepoFile(string rel)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, rel));
    }
}
