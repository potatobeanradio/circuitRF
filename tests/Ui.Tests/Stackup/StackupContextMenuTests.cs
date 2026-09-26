using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using CircuitRF.Design.Layout;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Converters;
using CircuitRF.Ui.Layout;
using Xunit;

// The namespace is NOT `CircuitRF.Ui.Tests.Stackup`, though the folder is — see the note at the top
// of StackupSceneTests.cs: a namespace segment named `Stackup` shadows the `Stackup` TYPE for every
// file under `CircuitRF.Ui.Tests`.
namespace CircuitRF.Ui.Tests.StackupRender;

/// <summary>
/// brief-stackup-render-6-context-menu.md's gate — right-click the drawing, get a menu that depends
/// on what is under the pointer, and that edits through the same functions the cards do.
///
/// <h3>What stands in for a pointer, a menu popup and a rendered picture</h3>
/// <para>The same seams the rest of the series is tested through: a right-click is
/// <see cref="StackupCanvas.RightClickAt"/> and the menu's items are
/// <see cref="StackupCanvas.BuildContextMenuItems"/> — the real control over the real scene, with
/// only the event plumbing and Avalonia's own popup bypassed. This suite calls no Avalonia runtime
/// API and cannot construct <c>TechEditorView</c> (the note at the top of
/// <c>CanvasContextMenuPopOutTests</c> states why), so the half of R-stk6-1 that lives in the .axaml
/// and its handler is pinned by reading those two files.</para>
/// </summary>
public class StackupContextMenuTests
{
    private const double PaneWidth = 900;
    private const string FourLayerId = "pcb-4layer_FR-4_62mil_1oz";

    private static TechEditorViewModel Editor(Technology? tech = null) =>
        new(Path.Combine(Path.GetTempPath(), "stackup-context-menu-tests.ctech"),
            tech ?? ShippedTechnologies.Load(FourLayerId));

    private static StackupCanvas Canvas(TechEditorViewModel vm)
    {
        var canvas = new StackupCanvas { ViewModel = vm };
        canvas.MeasureForWidth(PaneWidth);   // the scene the right-click will be hit-tested against
        return canvas;
    }

    /// <summary>
    /// A point on the drawing that really does resolve to <paramref name="name"/>.
    ///
    /// <para><b>Not the centre of its rect.</b> A via barrel is drawn ACROSS the bands it spans and
    /// takes hit-test precedence over them (topmost last), so the middle of a conductor on this
    /// board is a via — which is correct, and is why the test has to aim where a user aiming at that
    /// band would. It re-measures first: a committed edit drops the scene, and a stale one would be
    /// hit-tested against rects that no longer exist.</para>
    /// </summary>
    private static Point PointOn(StackupCanvas canvas, string name)
    {
        var scene = Scene(canvas);
        var rect  = scene.Bands.FirstOrDefault(b => b.Name == name)?.Rect
                 ?? scene.Barrels.First(b => b.Name == name).Rect;

        for (int i = 1; i < 64; i++)
        {
            var p = new Point(rect.Left + rect.Width * i / 64f, rect.MidY);
            if (scene.HitTest((float)p.X, (float)p.Y)?.LayerName == name) return p;
        }
        throw new InvalidOperationException($"No point on the drawing resolves to \"{name}\".");
    }

    private static StackupScene Scene(StackupCanvas canvas)
    {
        canvas.MeasureForWidth(PaneWidth);
        return canvas.SceneCache.Current!;
    }

    private static Point Background(StackupCanvas canvas)
    {
        // Below the whole drawing: the scene's own height is what MeasureOverride returned, so a
        // point past it is background by construction rather than by a guessed coordinate.
        var scene = Scene(canvas);
        return new Point(scene.Width / 2, scene.Height + 40);
    }

    private static List<object> MenuAt(StackupCanvas canvas, TechEditorViewModel vm, string layerName)
    {
        var p = PointOn(canvas, layerName);
        canvas.RightClickAt(p);
        return canvas.BuildContextMenuItems(canvas.ConsumeContextMenuTarget()!.Value);
    }

    private static IEnumerable<MenuItem> Items(IEnumerable<object> items) => items.OfType<MenuItem>();

    private static MenuItem Item(IEnumerable<object> items, string header) =>
        Items(items).First(m => (string?)m.Header == header);

    private static MenuItem? ItemOrNull(IEnumerable<object> items, string header) =>
        Items(items).FirstOrDefault(m => (string?)m.Header == header);

    private static List<object> Sub(MenuItem parent) => ((IEnumerable<object>)parent.ItemsSource!).ToList();

    private static string Dielectric(TechEditorViewModel vm, int index) =>
        vm.Working.Stackup.Layers.Where(l => l.Kind == StackupKind.Dielectric).ElementAt(index).Name;

    // ── R-stk6-1 — one menu, rebuilt per opening, cancelled when nothing is pending ─────────────────

    /// <summary>
    /// <b>The stacking bug, stated as a property of this control.</b> The canvas never constructs or
    /// opens a <c>ContextMenu</c>; it records a point, and Avalonia opens the single instance the
    /// .axaml declares. Two successive right-clicks therefore leave exactly one pending target and
    /// produce exactly one menu — there is no second instance for a second click to make.
    /// </summary>
    [Fact]
    public void TheCanvasNeverConstructsAMenuOfItsOwn()
    {
        var vm = Editor();
        var canvas = Canvas(vm);

        canvas.RightClickAt(PointOn(canvas, Dielectric(vm, 0)));
        canvas.RightClickAt(PointOn(canvas, Dielectric(vm, 1)));

        // Nothing was attached by the control itself — the one instance is the .axaml's, and a
        // canvas built without that .axaml has none at all.
        Assert.Null(canvas.ContextMenu);

        string src = Source(Path.Combine("src", "Ui", "Controls", "StackupCanvas.ContextMenu.cs"))
                   + Source(Path.Combine("src", "Ui", "Controls", "StackupCanvas.cs"));
        Assert.DoesNotContain("new ContextMenu", StripComments(src), StringComparison.Ordinal);
    }

    [Fact]
    public void TheItemsAreRebuiltFreshOnEveryOpening()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = Dielectric(vm, 0);

        var first  = MenuAt(canvas, vm, name);
        var second = MenuAt(canvas, vm, name);

        Assert.Equal(first.Count, second.Count);
        // Not one shared instance between them: re-subscribing a reused item's Click is what fires an
        // action N times on the Nth opening, which is the mistake this pattern exists to prevent.
        foreach (var item in Items(first)) Assert.DoesNotContain(item, Items(second));
    }

    [Fact]
    public void ATargetIsConsumedOnceSoASecondOpeningIsCancelled()
    {
        var vm = Editor();
        var canvas = Canvas(vm);

        canvas.RightClickAt(PointOn(canvas, Dielectric(vm, 0)));
        Assert.NotNull(canvas.ConsumeContextMenuTarget());

        // The handler's own cancel condition: no pending target.
        Assert.Null(canvas.ConsumeContextMenuTarget());
    }

    /// <summary>The .axaml half — one instance, declared on the control, wired to the handler that
    /// consumes the target and cancels without one.</summary>
    [Fact]
    public void TheViewDeclaresExactlyOneMenuOnTheCanvasAndCancelsWithoutATarget()
    {
        string xaml = Source(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        Assert.Contains("<ctrl:StackupCanvas.ContextMenu>", xaml, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(xaml, @"Opening=""OnStackupContextMenuOpening"""));

        string code = StripComments(
            Source(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml.cs")));
        Assert.Contains("ConsumeContextMenuTarget()", code, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true", code, StringComparison.Ordinal);
        Assert.Contains("menu.ItemsSource = StackupDrawing.BuildContextMenuItems", code, StringComparison.Ordinal);
    }

    // ── R-stk6-1 — a right-click selects what is under it ────────────────────────────────────────

    [Fact]
    public void ARightClickSelectsWhatIsUnderItBeforeTheMenuBuilds()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string conductor = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;

        canvas.RightClickAt(PointOn(canvas, conductor));
        Assert.Equal(conductor, vm.SelectedStackupLayerName);
        Assert.True(vm.SelectedStackupLayerRow!.IsSelected);
    }

    [Fact]
    public void ARightClickOnTheBackgroundClearsTheSelectionAndOffersOnlyCopy()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        vm.SelectedStackupLayerName = Dielectric(vm, 0);

        var p = Background(canvas);
        canvas.RightClickAt(p);
        Assert.Null(vm.SelectedStackupLayerName);

        var items = canvas.BuildContextMenuItems(canvas.ConsumeContextMenuTarget()!.Value);
        Assert.Equal(["Copy"], Items(items).Select(m => (string?)m.Header));
    }

    // ── R-stk6-3 — Delete, labelled by kind ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(StackupKind.Conductor,  "Delete Conductor")]
    [InlineData(StackupKind.Dielectric, "Delete Dielectric")]
    [InlineData(StackupKind.Via,        "Delete Via")]
    public void DeleteIsLabelledByKindRemovesThatEntryAndUndoesInOneStep(StackupKind kind, string header)
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == kind).Name;
        int before = vm.Working.Stackup.Layers.Count;
        Assert.False(vm.UndoRedo.CanUndo);     // a fresh editor: the entry below is this menu's

        var items = MenuAt(canvas, vm, name);
        Click(Item(items, header));

        Assert.Equal(before - 1, vm.Working.Stackup.Layers.Count);
        Assert.DoesNotContain(vm.Working.Stackup.Layers, l => l.Name == name);

        // ONE entry: a single Undo restores it AND leaves nothing behind it.
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(before, vm.Working.Stackup.Layers.Count);
        Assert.Contains(vm.Working.Stackup.Layers, l => l.Name == name);
    }

    /// <summary>
    /// R-stk6-3's other half, and the one worth pinning: deleting a conductor a via's span names
    /// leaves that via UNRESOLVABLE, and that is not this menu's problem to fix. Deleting the via
    /// too, or silently re-pointing its span, would make one deletion do two things — so the via
    /// entry is untouched and the validator is what says so.
    /// </summary>
    [Fact]
    public void DeletingASpanReferencedConductorLeavesTheViaAloneAndValidationReportsIt()
    {
        var vm = Editor();
        var canvas = Canvas(vm);

        var via = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via);
        string spanned = via.SpanFromLayer!;
        string viaName = via.Name;
        Assert.DoesNotContain(TechValidation.Validate(vm.Working), p => p.Contains(viaName, StringComparison.Ordinal));

        Click(Item(MenuAt(canvas, vm, spanned), "Delete Conductor"));

        var after = vm.Working.Stackup.Layers.First(l => l.Name == viaName);
        Assert.Equal(spanned, after.SpanFromLayer);            // untouched, not re-pointed
        Assert.Equal(via.SpanToLayer, after.SpanToLayer);
        Assert.Contains(TechValidation.Validate(vm.Working),
                        p => p.Contains(viaName, StringComparison.Ordinal)
                          && p.Contains(spanned, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The menu's Delete and the <c>Delete</c> KEYSTROKE are one deletion</b> (owner,
    /// 2026-09-13). The keystroke reaches <c>RemoveStackupLayer</c> through
    /// <c>DeleteSelectedStackupLayer</c>, which is only the by-name lookup a keystroke needs and a
    /// pointer gesture does not — so a second removal written for the keyboard would drift from this
    /// menu silently, and the undo entry would be the first thing to go. Same board, same entry, the
    /// two gestures: identical documents afterwards, and identical undo descriptions.
    ///
    /// <para>Here rather than in <c>StackupDeleteKeyTests</c> because this class is the one that
    /// builds <c>MenuItem</c>s: doing it from two classes at once races that type's static
    /// registration, and xUnit runs classes in parallel.</para>
    /// </summary>
    [Fact]
    public void TheDeleteKeystrokeIsTheSameDeletionAsTheMenus()
    {
        var byMenu = Editor();
        string name = byMenu.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;
        Click(Item(MenuAt(Canvas(byMenu), byMenu, name), "Delete Conductor"));

        var byKey = Editor();
        byKey.SelectedStackupLayerName = name;
        Assert.True(byKey.DeleteSelectedStackupLayer());

        Assert.Equal(TechPersistence.Serialize(byMenu.Working),
                     TechPersistence.Serialize(byKey.Working));
        Assert.Equal(byMenu.UndoRedo.UndoDescription, byKey.UndoRedo.UndoDescription);

        // Both leave nothing selected: the name the selection held has just stopped naming anything.
        Assert.Null(byMenu.SelectedStackupLayerName);
        Assert.Null(byKey.SelectedStackupLayerName);
    }

    // ── R-stk6-4 — Add Via ──────────────────────────────────────────────────────────────────────

    /// <summary>Every dielectric of the shipped four-layer board, against the pair the stack itself
    /// says is around it — walked here independently of the production walk rather than read back
    /// from it.</summary>
    [Theory]
    [InlineData("Prepreg (top)",    "Top Copper (1 oz)",     "Inner 1 (Ground Plane)")]
    [InlineData("Core",             "Inner 1 (Ground Plane)", "Inner 2")]
    [InlineData("Prepreg (bottom)", "Inner 2",                "Bottom Copper (1 oz)")]
    public void AddViaSpansTheConductorsAroundTheClickedDielectric(string dielectric, string above, string below)
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        int before = vm.Working.Stackup.Layers.Count;

        var addVia = Item(MenuAt(canvas, vm, dielectric), "Add Via");
        Assert.True(addVia.IsEnabled);
        Click(addVia);

        Assert.Equal(before + 1, vm.Working.Stackup.Layers.Count);
        var via = vm.Working.Stackup.Layers[^1];
        Assert.Equal(StackupKind.Via, via.Kind);
        Assert.Equal(above, via.SpanFromLayer);
        Assert.Equal(below, via.SpanToLayer);

        // R-stk6-4 — selected, so the card the drawing layer and the wall thickness live on is what
        // the user is looking at next.
        Assert.Equal(via.Name, vm.SelectedStackupLayerName);
        Assert.NotNull(vm.SelectedStackupLayerRow);
    }

    /// <summary>A dielectric above the top metal has nothing to span up to. Disabled WITH ITS REASON,
    /// and the reason names the side that has none — never enabled then refused.</summary>
    [Theory]
    [InlineData(true,  "above")]
    [InlineData(false, "below")]
    public void AddViaIsDisabledWithItsReasonWhenOneSideHasNoConductor(bool onTop, string side)
    {
        var tech = ShippedTechnologies.Load(FourLayerId);
        var layers = tech.Stackup.Layers;
        var soldermask = new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Solder mask", ThicknessDbu = 20_000, Epsr = 3.5,
        };
        if (onTop) layers.Insert(0, soldermask); else layers.Insert(layers.Count, soldermask);

        var vm = Editor(tech);
        var canvas = Canvas(vm);
        int before = vm.Working.Stackup.Layers.Count;

        var addVia = Item(MenuAt(canvas, vm, "Solder mask"), "Add Via");
        Assert.False(addVia.IsEnabled);
        Assert.Contains(side, (string)ToolTip.GetTip(addVia)!, StringComparison.Ordinal);
        Assert.Contains("Solder mask", (string)ToolTip.GetTip(addVia)!, StringComparison.Ordinal);

        // And the guard behind the disabled item holds too.
        Click(addVia);
        Assert.Equal(before, vm.Working.Stackup.Layers.Count);
    }

    /// <summary>
    /// R-stk6-4's identity claim: a via added from the drawing and one added with the "＋ Via" button
    /// are THE SAME ENTRY but for its name and (when nothing is selected) its span. Both go through one constructor, which is
    /// what makes this true of fields nobody has added yet.
    /// </summary>
    [Fact]
    public void AMenuViaAndAButtonViaDifferOnlyInNameAndSpan()
    {
        var vm = Editor();
        var canvas = Canvas(vm);

        vm.ClearStackupSelection();
        vm.AddViaLayerCommand.Execute(null);
        var fromButton = vm.Working.Stackup.Layers[^1];

        Click(Item(MenuAt(canvas, vm, "Core"), "Add Via"));
        var fromMenu = vm.Working.Stackup.Layers[^1];

        Assert.NotEqual(fromButton.Name, fromMenu.Name);
        Assert.Equal(fromButton.Kind,             fromMenu.Kind);
        Assert.Equal(fromButton.ThicknessDbu,     fromMenu.ThicknessDbu);
        Assert.Equal(fromButton.Plated,           fromMenu.Plated);
        Assert.Equal(fromButton.Fill,             fromMenu.Fill);
        Assert.Equal(fromButton.WallThicknessDbu, fromMenu.WallThicknessDbu);
        Assert.Equal(fromButton.DrawingLayers,    fromMenu.DrawingLayers);
        Assert.Equal(fromButton.SigmaSm,          fromMenu.SigmaSm);

        // …and the two fields that DO differ are the two the menu exists to set.
        // With nothing selected the button spans top to bottom; the menu spans the dielectric.
        var conductors = vm.Working.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        Assert.Equal(conductors[0].Name,  fromButton.SpanFromLayer);
        Assert.Equal(conductors[^1].Name, fromButton.SpanToLayer);
        Assert.Equal("Inner 1 (Ground Plane)", fromMenu.SpanFromLayer);
        Assert.Equal("Inner 2",                fromMenu.SpanToLayer);
    }

    /// <summary>"＋ Via" with fewer than two conductors still adds the via, and the drawing says
    /// what to do about it rather than reporting two unset ends.</summary>
    [Fact]
    public void AButtonViaWithNothingToSpan_SaysToAddConductors()
    {
        var tech = ShippedTechnologies.Load(FourLayerId);
        tech.Stackup.Layers.RemoveAll(l => l.Kind != StackupKind.Dielectric);
        var vm = Editor(tech);

        vm.AddViaLayerCommand.Execute(null);
        var via = vm.Working.Stackup.Layers[^1];
        Assert.Equal(StackupKind.Via, via.Kind);
        Assert.Null(via.SpanFromLayer);

        var labels = StackupScene.Build(vm.Working, 900f).Labels;
        Assert.Contains(labels, l => l.Text.StartsWith("first add conductors", StringComparison.Ordinal));
        Assert.DoesNotContain(labels, l => l.Text.Contains("(unset)", StringComparison.Ordinal));
    }

    // ── R-stk6-5 — Plated and Fill: two fields, two items, never merged ─────────────────────────

    [Fact]
    public void ThePlatedHoleToggleWritesPlatedAndNeverFill()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;
        var fillBefore = Layer(vm, name).Fill;
        Assert.False(vm.UndoRedo.CanUndo);

        var hole = Item(MenuAt(canvas, vm, name), StackupCardText.PlatedHoleMenu);
        Assert.Equal(MenuItemToggleType.CheckBox, hole.ToggleType);
        Assert.True(hole.IsChecked);
        Click(hole);

        Assert.False(Layer(vm, name).Plated);
        Assert.Equal(fillBefore, Layer(vm, name).Fill);     // NOT the fill model

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);                  // one entry, not two
        Assert.NotEqual(false, Layer(vm, name).Plated);
    }

    [Fact]
    public void TheFillItemsWriteFillAndNeverPlated()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;
        var platedBefore = Layer(vm, name).Plated;
        Assert.False(vm.UndoRedo.CanUndo);

        var fill = Item(MenuAt(canvas, vm, name), StackupCardText.Fill);
        Click(Item(Sub(fill), StackupCardText.FillSolid));

        Assert.Equal(ViaFillKind.Solid, Layer(vm, name).Fill);
        Assert.Equal(platedBefore, Layer(vm, name).Plated);  // NOT the hole flag

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);                   // one entry, not two
        Assert.NotEqual(ViaFillKind.Solid, Layer(vm, name).Fill);
    }

    /// <summary>HIDDEN, not disabled — a fill model for a hole that is not metal is not a meaningful
    /// choice, which is exactly how the card treats it (<c>IsVisible="{Binding IsPlated}"</c>).</summary>
    [Fact]
    public void TheFillSubmenuIsAbsentWhenTheHoleIsNotPlated()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;

        Assert.NotNull(ItemOrNull(MenuAt(canvas, vm, name), StackupCardText.Fill));

        Click(Item(MenuAt(canvas, vm, name), StackupCardText.PlatedHoleMenu));

        var items = MenuAt(canvas, vm, name);
        Assert.Null(ItemOrNull(items, StackupCardText.Fill));
        Assert.False(Item(items, StackupCardText.PlatedHoleMenu).IsChecked);
    }

    // ── R-stk6-6 — the drawing follows ──────────────────────────────────────────────────────────

    /// <summary>
    /// The cheapest possible check that the two halves are connected: R-stk1-7 already draws all
    /// three states differently, so toggling either field from the MENU must change the barrel the
    /// scene places — through brief 2's <c>StackupChanged</c>, with nothing extra wired.
    /// </summary>
    [Fact]
    public void TogglingEitherFieldChangesTheDrawnBarrel()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;

        StackupViaLook Look() => Scene(canvas).Barrels.First(b => b.Name == name).Look;

        var plated = Look();

        var fill = Item(MenuAt(canvas, vm, name), StackupCardText.Fill);
        Click(Item(Sub(fill), StackupCardText.FillSolid));
        var solid = Look();

        Click(Item(MenuAt(canvas, vm, name), StackupCardText.PlatedHoleMenu));
        var unplated = Look();

        Assert.Equal(StackupViaLook.PlatedBarrel, plated);
        Assert.Equal(StackupViaLook.SolidFill,    solid);
        Assert.Equal(StackupViaLook.UnplatedHole, unplated);
        Assert.Equal(3, new HashSet<StackupViaLook> { plated, solid, unplated }.Count);
    }

    // ── R-stk6-8 — the closed-choice parameters, from the drawing ───────────────────────────────

    [Fact]
    public void GroundReferenceWritesItsOwnFieldAndOnlyItsOwn()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var conductor = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference);
        string name = conductor.Name;
        var sheetBefore = Layer(vm, name).SheetAt;
        Assert.False(vm.UndoRedo.CanUndo);

        var gr = Item(MenuAt(canvas, vm, name), StackupCardText.GroundReference);
        Assert.Equal(MenuItemToggleType.CheckBox, gr.ToggleType);
        Assert.False(gr.IsChecked);
        Click(gr);

        Assert.True(Layer(vm, name).IsGroundReference);
        Assert.Equal(sheetBefore, Layer(vm, name).SheetAt);
        Assert.Empty(Layer(vm, name).DrawingLayers.Except(conductor.DrawingLayers));

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoRedo.CanUndo);                   // one entry, not two
        Assert.False(Layer(vm, name).IsGroundReference);
    }

    [Fact]
    public void MetalThicknessGoesToWritesItsOwnFieldAndUsesTheCardsRowText()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;
        bool groundBefore = Layer(vm, name).IsGroundReference;

        var parent = Item(MenuAt(canvas, vm, name), StackupCardText.SheetAt);
        var rows = Sub(parent);
        Assert.Equal(
            StackupLayerRowViewModel.SheetAtChoices.Select(SheetAtLabelConverter.Label),
            Items(rows).Select(m => (string?)m.Header));

        Click(Item(rows, SheetAtLabelConverter.Label(ConductorSheetSurface.Top)));

        Assert.Equal(ConductorSheetSurface.Top, Layer(vm, name).SheetAt);
        Assert.Equal(groundBefore, Layer(vm, name).IsGroundReference);
    }

    [Fact]
    public void PatternedWithWritesItsOwnFieldAndOffersNonePlusTheSignalConductors()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = Dielectric(vm, 1);
        long thicknessBefore = Layer(vm, name).ThicknessDbu;

        var parent = Item(MenuAt(canvas, vm, name), StackupCardText.PresentWith);
        var rows = Sub(parent);
        var row  = vm.StackupLayers.First(r => r.Layer.Name == name);
        Assert.Equal(row.PresentWithChoices, Items(rows).Select(m => (string?)m.Header));

        Click(Item(rows, "Inner 2"));

        Assert.Equal("Inner 2", Layer(vm, name).PresentWithLayer);
        Assert.Equal(thicknessBefore, Layer(vm, name).ThicknessDbu);

        // …and "(none)" puts it back to an ordinary continuous dielectric.
        Click(Item(Sub(Item(MenuAt(canvas, vm, name), StackupCardText.PresentWith)), StackupLayerRowViewModel.SpanNone));
        Assert.Null(Layer(vm, name).PresentWithLayer);
    }

    [Fact]
    public void TheViaDrawingLayerItemWritesOnlyTheBinding()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;
        var spanBefore = Layer(vm, name).SpanFromLayer;
        var row = vm.StackupLayers.First(r => r.Layer.Name == name);

        var parent = Item(MenuAt(canvas, vm, name), row.DrawingLayersLabel);
        Assert.Equal("Drawing layer:", (string?)parent.Header);

        var target = vm.Working.Layers[1];
        Click(Item(Sub(parent), target.Name));

        Assert.Equal([target.Key], Layer(vm, name).DrawingLayers);
        Assert.Equal(spanBefore, Layer(vm, name).SpanFromLayer);
    }

    [Fact]
    public void TheConductorDrawingLayersItemTogglesOneBindingAtATime()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;
        var bound = Layer(vm, name).DrawingLayers.ToList();
        Assert.NotEmpty(bound);

        var parent = Item(MenuAt(canvas, vm, name), "Drawing layers:");
        var checkedNames = Items(Sub(parent)).Where(m => m.IsChecked).Select(m => (string?)m.Header).ToList();
        Assert.Equal(bound.Count, checkedNames.Count);

        Click(Item(Sub(parent), checkedNames[0]!));
        Assert.Equal(bound.Count - 1, Layer(vm, name).DrawingLayers.Count);
    }

    /// <summary>
    /// R-stk6-8's bound. A real process carries several hundred drawing layers — a flat submenu of
    /// them is not a menu. What is bound is never truncated away, the count is honest, and the
    /// overflow item SAYS the full picker is on the card rather than silently stopping.
    /// </summary>
    [Fact]
    public void TheDrawingLayersSubmenuIsBoundedOnATechnologyWith300Layers()
    {
        var tech = ShippedTechnologies.Load(FourLayerId);
        int real = tech.Layers.Count;
        for (int i = real; i < 300; i++)
            tech.Layers.Add(new LayerDef { Name = $"Filler {i}", Key = new LayerKey(900 + i, 0) });

        var vm = Editor(tech);
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;

        var rows = Sub(Item(MenuAt(canvas, vm, name), "Drawing layers:"));
        var layerItems = Items(rows).Where(m => ((string?)m.Header)?.StartsWith("More…", StringComparison.Ordinal) != true).ToList();

        Assert.Equal(StackupCanvas.ContextMenuLayerLimit, layerItems.Count);
        Assert.True(layerItems.Count < 300);

        // Everything bound is still listed, and listed CHECKED.
        foreach (var key in Layer(vm, name).DrawingLayers)
        {
            string layerName = vm.Working.Layers.First(l => l.Key.Equals(key)).Name;
            Assert.True(Item(rows, layerName).IsChecked);
        }

        var more = Items(rows).Single(m => ((string?)m.Header)!.StartsWith("More…", StringComparison.Ordinal));
        Assert.Contains((300 - StackupCanvas.ContextMenuLayerLimit).ToString(), (string?)more.Header!, StringComparison.Ordinal);
        Assert.Contains("card", (string)ToolTip.GetTip(more)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASmallTechnologyGetsNoOverflowItem()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        Assert.True(vm.Working.Layers.Count <= StackupCanvas.ContextMenuLayerLimit);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;

        var rows = Sub(Item(MenuAt(canvas, vm, name), "Drawing layers:"));
        Assert.DoesNotContain(Items(rows), m => ((string?)m.Header)!.StartsWith("More…", StringComparison.Ordinal));
    }

    /// <summary>
    /// R-stk6-8's "the labels are the card's labels, verbatim", and its "the tooltips come with
    /// them" — enforced rather than asserted about two copies. The card reads the same constants
    /// through <c>{x:Static}</c>, so this checks the .axaml actually points at them; the menu's own
    /// headers are compared to the constants directly above.
    ///
    /// <para><b>The drawing-layer rows carry no tooltip on the card and so carry none here</b>: they
    /// are the one pair of the five R-stk6-8 lists that were never given one. Inventing a tooltip
    /// for the menu alone would be the second wording the rule exists to prevent — and what the menu
    /// does have to say, that its list is bounded and the full picker is on the card, is on the
    /// overflow item where it belongs.</para>
    /// </summary>
    [Theory]
    [InlineData("GroundReference", "GroundReferenceTip")]
    [InlineData("SheetAt",         "SheetAtTip")]
    [InlineData("PresentWith",     "PresentWithTip")]
    [InlineData("Plated",          "PlatedTip")]
    public void TheCardReadsTheSameLabelAndTooltipConstantsTheMenuDoes(string label, string tip)
    {
        string xaml = Source(Path.Combine("src", "Ui", "Views", "Layout", "TechEditorView.axaml"));
        Assert.Contains($"{{x:Static lay:StackupCardText.{label}}}", xaml, StringComparison.Ordinal);
        Assert.Contains($"{{x:Static lay:StackupCardText.{tip}}}", xaml, StringComparison.Ordinal);

        // …and the card no longer carries a typed copy of either, which is what could drift.
        // XML COMMENTS ARE STRIPPED FIRST, for the same reason the C# scan strips its own: the
        // card's comments quote the wording at length while explaining why it is worded that way.
        string markup = Regex.Replace(xaml, @"<!--.*?-->", "", RegexOptions.Singleline);
        string text = (string)typeof(StackupCardText).GetField(label)!.GetRawConstantValue()!;
        Assert.DoesNotContain($"\"{text}\"", markup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>No item on the ROOT menu carries a tooltip</b> — owner, 2026-09-13: they pop up over the
    /// menu and hide the item names underneath it, and a menu you cannot read is worse than one that
    /// explains nothing.
    ///
    /// <para>The one exception is Add Via while it is DISABLED, which is the whole of R-stk6-4's
    /// "disabled with a reason": a disabled item cannot be hovered, so it covers nothing, and
    /// dropping that tooltip would leave an item greyed out and saying why nowhere. It is asserted
    /// as an exception rather than excused, so an enabled item that grows a tooltip still fails.</para>
    /// </summary>
    [Theory]
    [InlineData(StackupKind.Conductor)]
    [InlineData(StackupKind.Dielectric)]
    [InlineData(StackupKind.Via)]
    public void NoRootMenuItemCarriesATooltip(StackupKind kind)
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = kind == StackupKind.Dielectric
            ? Dielectric(vm, 0)
            : vm.Working.Stackup.Layers.First(l => l.Kind == kind).Name;

        foreach (var item in Items(MenuAt(canvas, vm, name)))
        {
            if (item.IsEnabled == false) continue;   // the disabled Add Via keeps its refusal
            Assert.Null(ToolTip.GetTip(item));
        }
    }

    /// <summary>
    /// …and the SUBMENU items keep theirs. That is where the closed choices are, where a reader who
    /// does not know what "Bottom of its own band" means is actually looking, and a submenu flyout is
    /// not what a tooltip over the root menu covers up.
    /// </summary>
    [Fact]
    public void EverySubmenuItemCarriesTheCardsTooltip()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string conductor = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Conductor).Name;
        string via       = vm.Working.Stackup.Layers.First(l => l.Kind == StackupKind.Via).Name;

        var cond = MenuAt(canvas, vm, conductor);
        foreach (var mi in Items(Sub(Item(cond, StackupCardText.SheetAt))))
            Assert.Equal(StackupCardText.SheetAtTip, ToolTip.GetTip(mi));

        var diel = MenuAt(canvas, vm, Dielectric(vm, 0));
        foreach (var mi in Items(Sub(Item(diel, StackupCardText.PresentWith))))
            Assert.Equal(StackupCardText.PresentWithTip, ToolTip.GetTip(mi));

        var v = MenuAt(canvas, vm, via);
        foreach (var mi in Items(Sub(Item(v, StackupCardText.Fill))))
            Assert.Equal(StackupCardText.FillTip, ToolTip.GetTip(mi));
    }

    // ── Copy — brief 6 shipped it disabled; brief 7 (R-stk7-4) is what enables it ────────────────

    /// <summary>
    /// The last item on every menu, on every kind of target, and now ENABLED — the shape brief 6
    /// reserved so that the menu would not change under the user when the feature arrived.
    /// </summary>
    [Theory]
    [InlineData(StackupKind.Conductor)]
    [InlineData(StackupKind.Dielectric)]
    [InlineData(StackupKind.Via)]
    public void EveryMenuEndsWithCopy(StackupKind kind)
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        string name = vm.Working.Stackup.Layers.First(l => l.Kind == kind).Name;

        var items = MenuAt(canvas, vm, name);
        var copy = (MenuItem)items[^1];
        Assert.Equal("Copy", (string?)copy.Header);
        Assert.True(copy.IsEnabled);
        Assert.IsType<Separator>(items[^2]);
    }

    /// <summary>
    /// R-stk7-3/R-stk7-4 at the gesture: the menu's Copy composes the SAME picture wherever it was
    /// raised from, because what it copies is the whole drawing rather than whatever was
    /// right-clicked — and the right-click SELECTS its target on the way past, which must not reach
    /// the picture either.
    /// </summary>
    [Fact]
    public void CopyComposesTheSamePictureFromEveryTarget()
    {
        var vm = Editor();
        var canvas = Canvas(vm);
        var theme = StackupRenderTheme.Light;

        string expected = StackupGraphicExport.BuildSvgString(vm.Working, theme);

        foreach (var kind in new[] { StackupKind.Conductor, StackupKind.Dielectric, StackupKind.Via })
        {
            string name = vm.Working.Stackup.Layers.First(l => l.Kind == kind).Name;
            MenuAt(canvas, vm, name);                      // selects that entry, as a right-click does
            Assert.Equal(name, vm.SelectedStackupLayerName);

            Assert.Equal(expected, StackupGraphicExport.BuildSvgString(vm.Working, theme),
                StringComparer.Ordinal);
        }
    }

    // ── R-stk6-7 — THE SOURCE SCAN ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The gate that holds the whole series.</b> By this brief the canvas has acquired every
    /// category of mutation the series adds — select, edit a value, reorder, re-span, delete, create,
    /// toggle. Every one of them goes through <see cref="TechEditorViewModel"/> or
    /// <see cref="StackupLayerRowViewModel"/>, which is where <c>CommitEdit</c> lives; this is what
    /// makes that a rule rather than a comment the next change does not know about.
    ///
    /// <para><b>Comments are stripped first.</b> This brief's own prose quotes every forbidden
    /// string, and so do the doc comments the implementation carries — an unstripped scan fails on
    /// its own documentation, which is the lesson already recorded from harmonicaRF H8.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Ui/Controls/StackupCanvas.cs")]
    [InlineData("src/Ui/Controls/StackupCanvas.ContextMenu.cs")]
    [InlineData("src/Ui/Controls/StackupDragController.cs")]
    [InlineData("src/Ui/Controls/StackupInlineEditor.cs")]
    [InlineData("src/Ui/Controls/StackupSceneCache.cs")]
    [InlineData("src/Ui/Views/Layout/TechEditorView.axaml.cs")]
    public void NoStackupCanvasFileHasASecondWritePath(string relativePath)
    {
        string code = StripComments(Source(relativePath.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var forbidden in new[]
        {
            // The model, written directly.
            "Working.Stackup",
            "Stackup.Layers",
            // The undo stack and the snapshot machinery, driven directly.
            "UndoRedo.Execute",
            "CommitEdit",
            "SnapshotJson",
            "ApplySnapshot",
            // Persistence, reached around the view model.
            "TechPersistence.Serialize",
            "TechPersistence.Deserialize",
        })
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);

        // Any StackupLayer property, assigned. `.Layer` is how these files would reach one at all —
        // the row VM's own model handle — so an assignment through it is the shape to refuse.
        var write = Regex.Match(code, @"\.Layer\s*\.\s*[A-Za-z_][A-Za-z0-9_]*\s*=(?!=)");
        Assert.False(write.Success, $"{relativePath} writes a StackupLayer property directly: {write.Value}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static StackupLayer Layer(TechEditorViewModel vm, string name) =>
        vm.Working.Stackup.Layers.First(l => l.Name == name);

    /// <summary>Raises a menu item's Click the way Avalonia would, minus the popup. A toggle item's
    /// IsChecked is flipped first, exactly as the control does, so a handler that read it would read
    /// what a real click leaves.</summary>
    private static void Click(MenuItem item)
    {
        if (item.ToggleType != MenuItemToggleType.None) item.IsChecked = !item.IsChecked;
        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static string Source(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}
