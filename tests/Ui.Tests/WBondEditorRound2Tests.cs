using System;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.Renderers;
using CircuitRF.WBond;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner's second batch of wBond editor changes (2026-08-16): panel precision and compaction,
/// Delete, selection dragging, the alt-drag anchor and its two live axes, the profile view's own
/// plane and marquee, and the persisted view arrangement.
///
/// <para>The toolbar itself — the view-mode button, the zoom buttons, the two combos and the V/I/
/// Delete key routing — lives in <c>WBondEditorView</c>'s code-behind and is not reachable from this
/// project (Ui.Tests calls no Avalonia runtime API, by the project's own rule). What IS reachable is
/// every rule those controls drive, and that is what is pinned here.</para>
/// </summary>
public class WBondEditorRound2Tests
{
    private static WBondDesign Design(int wires = 3, int arrays = 1, double azimuthDegrees = 0.0)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        double radians = azimuthDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);

        for (int a = 0; a < arrays; a++)
        {
            var array = new WireArray { Name = $"G{a + 1}", Profile = profile.Name };
            for (int w = 0; w < wires; w++)
            {
                double ox = -sin * (a * 200 + w * 6), oy = cos * (a * 200 + w * 6);
                array.Wires.Add(profile.CreateWire(
                    Point3.Mils(ox, oy, 4),
                    Point3.Mils(ox + 100 * cos, oy + 100 * sin, 1),
                    WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
            }
            design.Arrays.Add(array);
        }

        return design;
    }

    // ---------------------------------------------------------------- the panel card

    /// <summary>
    /// <b>The card is collapsed by default</b>, showing the array name and its inductance and nothing
    /// else. The detail is one click away, per row — a "let me look at this one", not a document
    /// setting.
    /// </summary>
    [Fact]
    public void ACard_StartsCollapsed()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        Assert.All(document.Panel.Rows, row => Assert.False(row.IsExpanded));
    }

    /// <summary>
    /// The self term is NOT repeated as a mutual — it is the card's headline number, and listing it
    /// again put the same value on the panel twice on every single-array document.
    ///
    /// <para>Updated for the 2026-08-16 round: the mutuals moved off the cards into the panel's own
    /// <c>MutualPairs</c> box. The requirement is unchanged — a single-array design has no mutual to
    /// report — only where it is asserted.</para>
    /// </summary>
    [Fact]
    public void ASingleArrayDesign_HasNoMutualsAtAll()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(arrays: 1)));

        Assert.Single(document.Panel.Rows);
        Assert.Empty(document.Panel.MutualPairs);
        Assert.False(document.Panel.HasMutualPairs);
    }

    /// <summary>
    /// A mutual to a DIFFERENT array is real information and is kept — removing the self term must not
    /// quietly remove coupling data with it.
    /// </summary>
    [Fact]
    public void ATwoArrayDesign_KeepsTheCrossArrayMutualAndDropsOnlyItsOwn()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design(wires: 2, arrays: 2)));

        Assert.Equal(2, document.Panel.Rows.Count);

        // Two arrays make exactly ONE pair — not one row per array, which is what listing them on
        // the cards produced.
        var pair = Assert.Single(document.Panel.MutualPairs);
        Assert.True(document.Panel.HasMutualPairs);
        Assert.DoesNotContain(pair.Mutual, document.Panel.Rows.Select(r => r.Self));
    }

    /// <summary>
    /// The ordinary return path — an image plane at z = 0 — is not stated. It said the same expected
    /// thing on every document and cost a row; the case WB20/RW13 exists for is the other one.
    /// </summary>
    [Fact]
    public void TheReturnPathLine_IsShownOnlyWhenItIsNotTheOrdinaryOne()
    {
        var withPlane = new WBondDocumentViewModel(new WBondViewModel(Design()));
        Assert.False(withPlane.Panel.ShowReturnPath);
        Assert.False(withPlane.Panel.ReturnPathUndeclared);

        var design = Design();
        design.GroundPlane.Enabled = false;
        var withoutPlane = new WBondDocumentViewModel(new WBondViewModel(design));

        Assert.True(withoutPlane.Panel.ShowReturnPath);
        Assert.True(withoutPlane.Panel.ReturnPathUndeclared);
    }

    // ---------------------------------------------------------------- Delete

    /// <summary>
    /// <b>Delete removes the selected wires, and it is undoable AND redoable.</b> Undo restores the
    /// wire OBJECTS from the snapshot rather than reconstructing them, which is what makes a deletion
    /// survive Ctrl+Z at all — see <c>WBondViewModel.ArraySnapshot</c>.
    /// </summary>
    [Fact]
    public void Delete_RemovesTheSelection_AndRoundTripsThroughUndoAndRedo()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        vm.Selection = new WireSelection { Wires = { 0, 2 } };

        Assert.Equal(2, vm.DeleteSelectedWires());
        Assert.Equal(1, vm.Design.WireCount);

        vm.Undo();
        Assert.Equal(3, vm.Design.WireCount);

        vm.Redo();
        Assert.Equal(1, vm.Design.WireCount);
    }

    /// <summary>Deleting nothing changes nothing and leaves no undo entry to walk back through.</summary>
    [Fact]
    public void Delete_WithNothingSelected_IsANoOp()
    {
        var vm = new WBondViewModel(Design(wires: 3));

        Assert.Equal(0, vm.DeleteSelectedWires());
        Assert.False(vm.CanUndo);
    }

    // ---------------------------------------------------------------- dragging a selection

    private static WBondLayoutOverlay Overlay(WBondViewModel vm) => new(vm) { SnapEnabled = false };

    private static long Tol => WBondUnits.ToNm(3.0, WBondUnit.Mil);

    /// <summary>
    /// <b>Pressing on an already-selected thing picks the SELECTION up.</b> The press used to
    /// re-resolve unconditionally, so grabbing a multi-segment selection to move it collapsed it to
    /// the one element under the cursor first — "clicking on the selection starts a new selection".
    /// </summary>
    [Fact]
    public void PressingOnTheSelection_KeepsItSoTheWholeSelectionDrags()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        var overlay = Overlay(vm);

        vm.Selection = new WireSelection { Wires = { 0, 1 } };
        var foot = vm.Design.AllWires().First().Points[0];

        overlay.OnPointerPressed(foot.X, foot.Y, Tol, KeyModifiers.None, 1);

        Assert.Equal([0, 1], vm.Selection.Wires.OrderBy(i => i));

        // ...and the drag moves BOTH wires, not just the one under the cursor.
        long step = WBondUnits.ToNm(10.0, WBondUnit.Mil);
        var before = vm.Design.AllWires().Select(w => w.Points[0].Y).ToList();

        overlay.OnPointerMoved(foot.X, foot.Y + step, 0, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(foot.X, foot.Y + step);

        var after = vm.Design.AllWires().Select(w => w.Points[0].Y).ToList();
        Assert.Equal(before.Select(y => y + step), after);
    }

    /// <summary>
    /// A plain CLICK on the selection still narrows it — the click-through. Without it an element
    /// inside a selected wire would be unreachable by clicking on it.
    /// </summary>
    [Fact]
    public void ClickingOnTheSelection_StillNarrowsItOnRelease()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        var overlay = Overlay(vm);

        vm.Selection = new WireSelection { Wires = { 0, 1 } };
        var foot = vm.Design.AllWires().First().Points[0];

        overlay.OnPointerPressed(foot.X, foot.Y, Tol, KeyModifiers.None, 1);
        overlay.OnPointerReleased(foot.X, foot.Y);

        Assert.Empty(vm.Selection.Wires);
        Assert.Contains(new PointRef(0, 0), vm.Selection.Points);
    }

    /// <summary>
    /// <b>A CLICK must not move geometry, and must not leave an undo entry.</b> A press used to open
    /// the gesture and snap the grabbed point immediately, so a click on a wire's input foot with a
    /// pixel of hand-shake moved that foot — and a moved foot is a changed span, which is what the
    /// owner reported.
    /// </summary>
    [Fact]
    public void AClickWithHandShake_MovesNothingAndPushesNoUndoEntry()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var overlay = Overlay(vm);

        var wire = vm.Design.AllWires().First();
        var foot = wire.Points[0];
        double spanBefore = wire.ChordLengthMetres();

        overlay.OnPointerPressed(foot.X, foot.Y, Tol, KeyModifiers.None, 1);

        // A few hundred nanometres of shake — well inside the hit tolerance.
        overlay.OnPointerMoved(foot.X + 300, foot.Y - 200, 0, leftButtonDown: true, KeyModifiers.None);
        overlay.OnPointerReleased(foot.X + 300, foot.Y - 200);

        Assert.Equal(foot, wire.Points[0]);
        Assert.Equal(spanBefore, wire.ChordLengthMetres(), 15);
        Assert.False(vm.CanUndo);
    }

    // ---------------------------------------------------------------- alt-drag

    /// <summary>
    /// <b>The span anchor is the foot the user is NOT dragging.</b> Always pinning <c>Points[0]</c>
    /// made an alt-drag on the left end of a wire pull the RIGHT end towards the cursor — the opposite
    /// of what the hand asked for.
    /// </summary>
    [Theory]
    [InlineData(true)]    // move the output foot: the input one stays put
    [InlineData(false)]   // and the reverse
    public void AltDragSpan_PinsTheFootTheUserIsNotDragging(bool moveOutputFoot)
    {
        var design = Design(wires: 1);
        var wire = design.AllWires().First();
        var input = wire.Points[0];
        var output = wire.Points[^1];

        WireEdits.ScaleSpan(wire, 1.5, moveOutputFoot);

        if (moveOutputFoot)
        {
            Assert.Equal(input, wire.Points[0]);
            Assert.NotEqual(output, wire.Points[^1]);
        }
        else
        {
            Assert.Equal(output, wire.Points[^1]);
            Assert.NotEqual(input, wire.Points[0]);
        }
    }

    /// <summary>The anchor is carried all the way through the array-wide scale, not just the primitive.</summary>
    [Fact]
    public void AltDragSpan_CarriesTheAnchorIntoTheWholeBoundArray()
    {
        var design = Design(wires: 3);
        var outputs = design.AllWires().Select(w => w.Points[^1]).ToList();
        var profile = design.Profiles[0];

        WireEdits.ScaleBoundWires(design, profile, heightFactor: 1.0, spanFactor: 1.4,
                                  moveOutputFoot: false);

        // Every wire's OUTPUT foot is pinned; every input foot moved.
        Assert.Equal(outputs, design.AllWires().Select(w => w.Points[^1]));
        Assert.All(design.AllWires(), w => Assert.NotEqual(0L, w.Points[0].X));
    }

    /// <summary>
    /// <b>Alt-drag changes span and height together.</b> The old rule declared one axis on the first
    /// few pixels of travel and ignored the other for the rest of the gesture, so a diagonal alt-drag
    /// silently did half of what it looked like.
    /// </summary>
    [Fact]
    public void AltDrag_ScalesSpanAndHeightInOneCall()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        vm.Selection = new WireSelection { Wires = { 0 } };

        var wire = vm.Design.AllWires().First();
        double spanBefore = wire.ChordLengthMetres();

        // Height ABOVE THE CHORD is the quantity WB24a scales — and it has to be, because the two feet
        // sit at different z here (die surface to package lead), so max-minus-min z is not a multiple
        // of it. Measured at mid-span, where the loop's own peak is.
        double heightBefore = ProfileEnvelope.HeightAt(wire, 0.5);

        Assert.True(vm.ScaleSelection(spanFactor: 1.5, heightFactor: 2.0, moveOutputFoot: true) > 0);

        Assert.Equal(spanBefore * 1.5, wire.ChordLengthMetres(), 9);
        Assert.Equal(heightBefore * 2.0, ProfileEnvelope.HeightAt(wire, 0.5), 0);
    }

    /// <summary>
    /// A factor of exactly 1.0 on one axis leaves that quantity alone, so a purely vertical or purely
    /// horizontal alt-drag still does only what it looks like.
    /// </summary>
    [Fact]
    public void AltDrag_WithOneFactorAtUnity_TouchesOnlyTheOtherAxis()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        vm.Selection = new WireSelection { Wires = { 0 } };

        var wire = vm.Design.AllWires().First();
        double spanBefore = wire.ChordLengthMetres();

        vm.ScaleSelection(spanFactor: 1.0, heightFactor: 1.5, moveOutputFoot: true);

        Assert.Equal(spanBefore, wire.ChordLengthMetres(), 12);
    }

    /// <summary>
    /// <b>Alt-drag works on a DETACHED wire.</b> It used to look up the selection's bound profile and
    /// give up when there was none, so the gesture silently did nothing on a free wire and said
    /// nothing about why.
    /// </summary>
    [Fact]
    public void AltDrag_AlsoScalesAWireThatFollowsNoProfile()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        vm.Selection = new WireSelection { Wires = { 0 } };
        vm.DetachSelection();

        var wire = vm.Design.AllWires().First();
        var other = vm.Design.AllWires().ElementAt(1);
        double spanBefore = wire.ChordLengthMetres();
        double otherBefore = other.ChordLengthMetres();

        Assert.Equal(1, vm.ScaleSelection(spanFactor: 1.5, heightFactor: 1.0, moveOutputFoot: true));

        Assert.Equal(spanBefore * 1.5, wire.ChordLengthMetres(), 9);
        Assert.Equal(otherBefore, other.ChordLengthMetres(), 12);   // the bound one is untouched
    }

    /// <summary>
    /// Alt-drag in the LAYOUT view scales span, which it did not do at all before — the gesture
    /// existed only in the profile view, so holding Alt there just moved the wire.
    /// </summary>
    [Fact]
    public void AltDragInTheLayoutView_ScalesSpanAlongTheWiresOwnChord()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var overlay = Overlay(vm);

        var wire = vm.Design.AllWires().First();
        var output = wire.Points[^1];
        double spanBefore = wire.ChordLengthMetres();

        // Grab near the OUTPUT foot, so the input foot is pinned, and pull along +x (the chord).
        long pull = WBondUnits.ToNm(50.0, WBondUnit.Mil);
        overlay.OnPointerPressed(output.X, output.Y, Tol, KeyModifiers.Alt, 1);
        overlay.OnPointerMoved(output.X + pull, output.Y, 0, leftButtonDown: true, KeyModifiers.Alt);
        overlay.OnPointerReleased(output.X + pull, output.Y);

        Assert.True(wire.ChordLengthMetres() > spanBefore * 1.3,
                    $"span {spanBefore} -> {wire.ChordLengthMetres()}");
        Assert.Equal(0L, wire.Points[0].X);   // the pinned foot did not move
    }

    // ---------------------------------------------------------------- the profile marquee

    /// <summary>
    /// A marquee drawn in the PROFILE view is resolved against that view's own axes — span and z —
    /// not against world x. Handing the resolver the same projection the curves were drawn with is
    /// what makes the box select what it visibly encloses.
    /// </summary>
    [Fact]
    public void AProfileMarquee_IsResolvedAgainstSpanAndZ()
    {
        var design = Design(wires: 3, azimuthDegrees: 90.0);   // wires running north-south
        var mesh = WireMesh.Build(design);

        long SpanOf(int wire, int index) =>
            (long)Math.Round(ProfileProjection.Project(mesh.Wires[wire], index,
                                                       ProfileProjection.SpanMode.Absolute).Span);

        // These wires run north-south from (-6w, 0) to (-6w, 100) mil, so their SPAN runs 0..100 mil
        // while their world x is a constant a few mil apart. A box covering span 0..60 therefore means
        // something completely different under the two readings, which is what makes this a test.
        long sixty = WBondUnits.ToNm(60.0, WBondUnit.Mil);
        long far = WBondUnits.ToNm(1000.0, WBondUnit.Mil);

        var projected = SelectionResolver.ResolveMarquee(mesh, 0, -far, sixty, far,
                                                         MarqueeDirection.LeftToRight, EditorView.Profile,
                                                         spanOf: SpanOf);

        // Every wire is caught, and each one PARTIALLY — the box covers the first 60% of every loop.
        Assert.Equal(3, projected.TouchedWires().Count);
        Assert.Empty(projected.Wires);
        Assert.NotEmpty(projected.Points);

        // Without the projection the resolver falls back on world x, where the same box encloses only
        // the one wire that happens to sit at x = 0 — and encloses it whole.
        var byWorldX = SelectionResolver.ResolveMarquee(mesh, 0, -far, sixty, far,
                                                        MarqueeDirection.LeftToRight, EditorView.Profile);

        Assert.Equal([0], byWorldX.Wires);
        Assert.Empty(byWorldX.Points);
    }

    // ---------------------------------------------------------------- the view arrangement

    /// <summary>Both → Profile → Layout → Both. The toolbar button and Tab share this one method.</summary>
    [Fact]
    public void TheViewMode_CyclesThroughAllThree()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));

        Assert.Equal(WBondViewMode.Both, document.ViewMode);
        Assert.True(document.ProfileVisible && document.LayoutVisible && document.SplitterVisible);

        document.CycleViewMode();
        Assert.Equal(WBondViewMode.Profile, document.ViewMode);
        Assert.True(document.ProfileVisible);
        Assert.False(document.LayoutVisible);
        Assert.False(document.SplitterVisible);

        document.CycleViewMode();
        Assert.Equal(WBondViewMode.Layout, document.ViewMode);
        Assert.False(document.ProfileVisible);
        Assert.True(document.LayoutVisible);

        document.CycleViewMode();
        Assert.Equal(WBondViewMode.Both, document.ViewMode);
    }

    /// <summary>
    /// The arrangement survives a save and reopen, through the <c>.wBond</c>'s own opaque ViewState
    /// field — <b>with no format-version bump</b>, which is the whole reason that field exists.
    /// </summary>
    [Fact]
    public void TheArrangement_SurvivesASaveAndReopen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wbond-viewstate-{Guid.NewGuid():N}.wBond");

        try
        {
            var document = new WBondDocument(new WBondViewModel(Design()), path);
            document.ViewModel.ViewMode = WBondViewMode.Profile;
            document.ViewModel.PanelVisible = false;
            document.ViewModel.Editor.DisplayUnit = WBondUnit.Um;
            document.ViewModel.Editor.CommitProfileAxisText("YZ");

            document.Save();

            var reopened = WBondDocument.Open(path);

            Assert.Equal(WBondViewMode.Profile, reopened.ViewModel.ViewMode);
            Assert.False(reopened.ViewModel.PanelVisible);
            Assert.Equal(WBondUnit.Um, reopened.ViewModel.Editor.DisplayUnit);
            Assert.Equal("YZ", reopened.ViewModel.Editor.ProfileAxisText);

            // The format version is unchanged: an older build still reads this file.
            Assert.Contains($"\"FormatVersion\": {WBondIo.CurrentFormatVersion}", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A file with no view state — every file written before this existed — opens at the defaults
    /// rather than failing, and so does one whose view state is corrupt. A view setting is never worth
    /// refusing to open a design over.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("\"a bare string\"")]
    public void AbsentOrCorruptViewState_TakesTheDefaults(string? json)
    {
        var design = Design();
        design.ViewStateJson = json;

        var state = WBondViewState.From(design);

        Assert.Equal(WBondViewMode.Both, state.ViewMode);
        Assert.True(state.PanelVisible);

        // The shipped plane is YZ (owner, 2026-08-16), not Auto — see the round trip below for why a
        // deliberately-chosen Auto still survives a save despite sharing null with "never set".
        Assert.Equal(WBondViewState.DefaultProfileAxisDegrees,
                     state.ProfileAzimuthRadians!.Value * 180.0 / Math.PI, 9);
    }

    /// <summary>
    /// <b>AUTO round-trips even though the DEFAULT is YZ.</b> Null means Auto and also means "the key
    /// was not written", so with a non-null default the two would collide: a design deliberately left
    /// on Auto would reopen in YZ. <c>WBondViewState</c> serialises nulls explicitly for exactly this
    /// reason, and this is the test that says so.
    /// </summary>
    [Fact]
    public void AutoProfilePlane_SurvivesASaveEvenThoughTheDefaultIsYz()
    {
        var design = Design();

        new WBondViewState { ProfileAxisDegrees = null }.To(design);

        Assert.Null(WBondViewState.From(design).ProfileAzimuthRadians);
    }

    // ---------------------------------------------------------------- Zoom to Fit

    /// <summary>
    /// <b>Zoom to Fit frames the WIRES.</b> The canvas fits the union of the layout's shapes and
    /// instances; a wBond's wires are an overlay by design (WB23) and are in neither, so a document on
    /// an empty scratch layout fitted to an empty extent and landed at an arbitrary default with every
    /// wire off screen.
    ///
    /// <para>The oracle is a rendered surface, not a bbox comparison: "I can't see wires after
    /// pressing Zoom to fit" is a claim about pixels.</para>
    /// </summary>
    [Fact]
    public void ZoomToFit_OnAnEmptyLayout_PutsTheWiresOnScreen()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = new WBondLayoutOverlay(vm);

        var bounds = overlay.ContentBounds();
        Assert.False(bounds.IsEmpty);

        // What LayoutCanvas.ZoomToFitInternal computes for an empty layout: the union is the overlay's
        // extent alone.
        var fitted = LayoutViewport.ZoomToFit(bounds, 800, 600);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);
        WBondRenderer.Draw(surface.Canvas, vm.Design, fitted, WBondRenderTheme.Fallback);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < 600; y++)
            for (int x = 0; x < 800; x++)
                if (bitmap.GetPixel(x, y) != SKColors.Black) lit++;

        Assert.True(lit > 500, $"every wire must be visible after a fit; {lit} lit pixels");

        // ...and every wire point is genuinely inside the framed world range, not merely near it.
        foreach (var wire in vm.Design.AllWires())
            foreach (var p in wire.Points)
            {
                Assert.InRange(p.X, (long)fitted.VisibleMinX, (long)fitted.VisibleMaxX);
                Assert.InRange(p.Y, (long)fitted.VisibleMinY, (long)fitted.VisibleMaxY);
            }
    }

    /// <summary>
    /// The extent crosses into the HOST LAYOUT's units, like every other wire coordinate that reaches
    /// the canvas. The two coincide exactly at the 1,000 DBU/µm default, so an implementation with no
    /// conversion passes on every default layout and frames a coarse one ten times too wide.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(100)]
    [InlineData(2000)]
    public void TheOverlaysExtent_IsInTheHostLayoutsOwnUnits(int dbuPerMicron)
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var overlay = new WBondLayoutOverlay(vm)
        {
            ReferenceLayout = new LayoutView { DbuPerMicron = dbuPerMicron },
        };

        long expected = WBondSnap.ToDbu(WBondUnits.ToNm(100.0, WBondUnit.Mil), dbuPerMicron);

        var bounds = overlay.ContentBounds();

        Assert.Equal(0, bounds.MinX);
        Assert.Equal(expected, bounds.MaxX);
    }

    /// <summary>
    /// Wires that are NOT DRAWN contribute no extent — a fit must never frame something invisible.
    ///
    /// <para>This is the only reachable empty case: a design with no wires (or an array with none)
    /// is refused outright by <c>WBondDesign.Validate</c>, because it makes the array-basis
    /// inductance singular. The reachable one is depth with an uncomposable descent chain (WB27),
    /// where the wires are deliberately suppressed rather than drawn at a silently wrong offset.</para>
    /// </summary>
    [Fact]
    public void WiresThatAreNotDrawnAtDepth_ContributeNoExtent()
    {
        var overlay = new WBondLayoutOverlay(new WBondViewModel(Design(wires: 2)))
        {
            DescentChain = [(new LayoutInstance(), 0, 0)],
            CanPlaceAtDepth = false,
        };

        Assert.True(overlay.ContentBounds().IsEmpty);

        overlay.CanPlaceAtDepth = true;
        Assert.False(overlay.ContentBounds().IsEmpty);
    }

    // ---------------------------------------------------------------- the shared live highlight

    /// <summary>
    /// <b>A live marquee's contents live on the shared view-model, not inside the canvas that owns the
    /// gesture.</b> A wire caught by a box dragged in the profile view is the same wire in the layout
    /// view, and has to light up in both — which is what <c>EffectiveSelection</c> is for. Every
    /// renderer reads it; none reads <c>Selection</c> to draw.
    /// </summary>
    [Fact]
    public void ALiveMarquee_PublishesItsPreviewForBothViews()
    {
        var vm = new WBondViewModel(Design(wires: 3));
        var overlay = Overlay(vm);
        long far = WBondUnits.ToNm(500.0, WBondUnit.Mil);

        Assert.Null(vm.PreviewSelection);
        Assert.Same(vm.Selection, vm.EffectiveSelection);

        overlay.OnPointerPressed(far, far, 0, KeyModifiers.None, 1);
        overlay.OnPointerMoved(-far, -far, 0, leftButtonDown: true, KeyModifiers.None);

        Assert.NotNull(vm.PreviewSelection);
        Assert.Same(vm.PreviewSelection, vm.EffectiveSelection);
        Assert.Equal(3, vm.EffectiveSelection.TouchedWires().Count);
        Assert.True(vm.Selection.IsEmpty);   // still uncommitted

        overlay.OnPointerReleased(-far, -far);

        Assert.Null(vm.PreviewSelection);
        Assert.Same(vm.Selection, vm.EffectiveSelection);
        Assert.Equal(3, vm.Selection.TouchedWires().Count);
    }

    /// <summary>The preview raises a notification of its own, which is what makes the highlight LIVE.</summary>
    [Fact]
    public void ChangingThePreview_NotifiesEffectiveSelection()
    {
        var vm = new WBondViewModel(Design(wires: 2));
        int raised = 0;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WBondViewModel.EffectiveSelection)) raised++;
        };

        vm.PreviewSelection = new WireSelection { Wires = { 0 } };
        vm.PreviewSelection = null;
        vm.Selection = new WireSelection { Wires = { 1 } };

        Assert.Equal(3, raised);
    }

    /// <summary>
    /// <b>The profile view accents individual POINTS, and draws a bound member that is selected.</b>
    /// It used to highlight whole wires only, and to skip every bound member but the representative —
    /// so an enclose marquee, whose whole job is catching some of a wire's vertices, and a marquee
    /// catching members of a large array, both appeared to select nothing.
    ///
    /// <para>A pixel oracle, because a counter cannot tell "drawn" from "drawn highlighted".</para>
    /// </summary>
    [Fact]
    public void TheProfileView_AccentsSelectedPointsOfABoundMember()
    {
        var design = Design(wires: 6);
        var theme = WBondRenderTheme.Fallback;

        // Wire 4 is a bound member, not the representative: without the fix it is not drawn at all.
        var selection = new WireSelection { Points = { new PointRef(4, 3) } };

        int plain = AccentedPixels(design, theme, null);
        int highlighted = AccentedPixels(design, theme, selection);

        Assert.True(highlighted > plain,
                    $"a selected point must add accent pixels; {plain} -> {highlighted}");
    }

    /// <summary>
    /// <b>Both views accent the same selection, whatever KIND it is.</b> The layout renderer coloured
    /// whole wires and nothing finer, so a segment picked in the profile view lit up there and showed
    /// nothing here — and a picked input foot lit up nowhere at all, because the input-end colour
    /// outranked the accent unconditionally.
    ///
    /// <para>Run over all three selection kinds, because the defect was per-kind: whole wires already
    /// worked, which is exactly why it went unnoticed.</para>
    /// </summary>
    [Theory]
    [InlineData("wire")]
    [InlineData("segment")]
    [InlineData("point")]
    [InlineData("inputfoot")]
    public void BothViewsAccentTheSameSelection(string kind)
    {
        var design = Design(wires: 1);

        WireSelection Selection() => kind switch
        {
            "wire" => new WireSelection { Wires = { 0 } },
            "segment" => new WireSelection { Segments = { new SegmentRef(0, 2) } },
            "point" => new WireSelection { Points = { new PointRef(0, 3) } },
            _ => new WireSelection { Points = { new PointRef(0, 0) } },
        };

        var theme = WBondRenderTheme.Fallback;

        Assert.True(AccentedPixels(design, theme, Selection()) > AccentedPixels(design, theme, null),
                    $"the profile view must accent a {kind} selection");
        Assert.True(AccentedLayoutPixels(design, theme, Selection()) > AccentedLayoutPixels(design, theme, null),
                    $"the layout view must accent a {kind} selection");
    }

    /// <summary>Counts pixels in the theme's SELECTED colour after a LAYOUT render.</summary>
    private static int AccentedLayoutPixels(WBondDesign design, WBondRenderTheme theme,
                                            WireSelection? selection)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);

        // Framed so the WHOLE 100 mil wire is on the canvas — at a tighter zoom only its first few mil
        // land on screen, and a test that renders an off-canvas point proves nothing about its colour.
        var viewport = new LayoutViewport
        {
            Zoom = 2.5e-4, PanX = -200_000, PanY = -1_000_000, Width = 800, Height = 600,
        };
        WBondRenderer.Draw(surface.Canvas, design, viewport, theme, selection);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 200 && px.Green > 200 && px.Blue > 200) lit++;   // theme.Selected is white
            }

        return lit;
    }

    /// <summary>Counts pixels in the theme's SELECTED colour after a profile render.</summary>
    private static int AccentedPixels(WBondDesign design, WBondRenderTheme theme, WireSelection? selection)
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        surface.Canvas.Clear(SKColors.Black);

        WBondRenderer.DrawProfile(
            surface.Canvas, design, theme,
            span => (float)(span / 4000.0), z => (float)(600 - z / 2000.0),
            selection: selection);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);

        int lit = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var px = bitmap.GetPixel(x, y);
                if (px.Red > 200 && px.Green > 200 && px.Blue > 200) lit++;   // theme.Selected is white
            }

        return lit;
    }

    // ---------------------------------------------------------------- Unit drives Snap

    /// <summary>
    /// <b>The Snap box reads in the unit the Unit box says.</b> It is the reference layout's own
    /// ladder and its own committing text field — reused rather than reimplemented — and both are
    /// formatted in the LAYOUT's display unit, which defaults to microns. So a document set to mil
    /// offered a snap ladder in µm right beside a Unit box saying mil.
    /// </summary>
    [Theory]
    [InlineData(WBondUnit.Mil, LayoutUnit.Mil)]
    [InlineData(WBondUnit.Um, LayoutUnit.Um)]
    [InlineData(WBondUnit.Mm, LayoutUnit.Mm)]
    [InlineData(WBondUnit.Inch, LayoutUnit.Inch)]
    [InlineData(WBondUnit.Nm, LayoutUnit.Nm)]
    public void TheReferenceLayout_FollowsTheEditorsDisplayUnit(WBondUnit chosen, LayoutUnit expected)
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());

        document.Editor.DisplayUnit = chosen;

        Assert.Equal(expected, document.ReferenceLayout.DisplayUnit);
        Assert.EndsWith(LayoutUnits.Suffix(expected), document.ReferenceLayout.SnapDistanceText,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference layout attached AFTER the unit was chosen still arrives on the right unit — the
    /// order these two happen in is not the user's business.
    /// </summary>
    [Fact]
    public void AReferenceLayoutAttachedLater_ArrivesOnTheEditorsUnit()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.Editor.DisplayUnit = WBondUnit.Mm;

        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());

        Assert.Equal(LayoutUnit.Mm, document.ReferenceLayout.DisplayUnit);
    }

    /// <summary>
    /// The whole ladder follows too, not just the current value — every preset a user can pick reads
    /// in the same unit as the box they picked it into.
    /// </summary>
    [Fact]
    public void TheSnapLadder_IsOfferedInTheEditorsUnit()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());
        document.Editor.DisplayUnit = WBondUnit.Mil;

        Assert.NotEmpty(document.ReferenceLayout.SnapLadderOptions);
        Assert.All(document.ReferenceLayout.SnapLadderOptions,
                   option => Assert.EndsWith("mil", option, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>A new wBond document snaps at 0.1 mil off a 1 mil ladder</b> (owner, 2026-08-16). The two
    /// are stated separately on purpose: with no technology to derive from, the ladder would otherwise
    /// re-base itself on whatever the snap happens to be and offer a 0.01 mil finest rung.
    /// </summary>
    [Fact]
    public void ANewDocument_SnapsAtOneTenthMil_OffAOneMilLadder()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        var layout = new LayoutEditorViewModel(new LayoutView());

        document.ReferenceLayout = layout;

        Assert.Equal(LayoutUnits.ToDbu(0.1m, LayoutUnit.Mil, layout.Model.DbuPerMicron), layout.SnapDbu);
        Assert.Equal("0.1 mil", layout.SnapDistanceText);
        Assert.Equal(["0.1 mil", "0.5 mil", "1 mil", "5 mil", "10 mil", "25 mil", "50 mil"],
                     layout.SnapLadderOptions);
    }

    /// <summary>
    /// A reference layout that already CARRIES a snap keeps it — the seeded default is for a document
    /// that has never had one, not an override of somebody's saved choice.
    /// </summary>
    [Fact]
    public void AReferenceLayoutWithItsOwnSnap_KeepsIt()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        var model = new LayoutView { SnapDbu = LayoutUnits.ToDbu(2m, LayoutUnit.Mil, 1000) };

        document.ReferenceLayout = new LayoutEditorViewModel(model);

        Assert.Equal(LayoutUnits.ToDbu(2m, LayoutUnit.Mil, 1000), document.ReferenceLayout.SnapDbu);
    }

    // ---------------------------------------------------------------- the grid, and snapping to it

    /// <summary>
    /// <b>Geometry snap first, grid second.</b> Landing exactly on a pad corner is what snapping is
    /// for (§6.6); a grid that overrode it would pull the foot back off the pad. With no geometry in
    /// reach the grid is what catches the point — otherwise the Snap box and the visible grid would
    /// describe a pitch the wires ignore.
    /// </summary>
    [Fact]
    public void AWirePoint_FallsBackToTheGridWhenNoGeometryIsInReach()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);

        var overlay = new WBondLayoutOverlay(vm) { SnapEnabled = true, GridPitchNm = mil };

        // Draw a wire between two points that are NOT on the grid, with no reference layout at all.
        overlay.WireDrawArmed = true;
        overlay.OnPointerPressed(mil * 10 + 700, mil * 4 - 300, 0, KeyModifiers.None, 1);
        overlay.OnPointerPressed(mil * 60 + 400, mil * 4 + 100, 0, KeyModifiers.None, 1);

        var placed = vm.Design.AllWires().Last();

        Assert.Equal(0L, placed.Points[0].X % mil);
        Assert.Equal(0L, placed.Points[0].Y % mil);
        Assert.Equal(0L, placed.Points[^1].X % mil);
    }

    /// <summary>A zero pitch means no grid, and leaves the point exactly where it was put.</summary>
    [Fact]
    public void AZeroGridPitch_SnapsNothing()
    {
        var vm = new WBondViewModel(Design(wires: 1));
        var overlay = new WBondLayoutOverlay(vm) { SnapEnabled = true, GridPitchNm = 0 };

        long mil = WBondUnits.ToNm(1.0, WBondUnit.Mil);
        overlay.WireDrawArmed = true;
        overlay.OnPointerPressed(mil * 10 + 700, mil * 4 - 300, 0, KeyModifiers.None, 1);
        overlay.OnPointerPressed(mil * 60 + 400, mil * 4 + 100, 0, KeyModifiers.None, 1);

        Assert.Equal(mil * 10 + 700, vm.Design.AllWires().Last().Points[0].X);
    }
}
