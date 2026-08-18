using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// WB-F / wbond.md §6.11 (WB39, WB39a) — <b>the wBond editor HOSTS <c>LayoutEditorView</c> instead of
/// transcribing it.</b>
///
/// <para>The phase is a SUBTRACTION: ~2,700 lines of duplicated view shell (toolbar, keyboard routing,
/// context menu, focus handling) replaced by the real control over a <c>LayoutDocument</c> wrapping
/// the editor's own reference layout. What is pinned here is the seam that makes that possible and the
/// two things that had to move rather than be deleted — the wire context menu and the descent chain.
/// </para>
///
/// <para>As in every other wBond test file, the view's own code-behind is not reachable from this
/// project; the source-scan tests at the bottom are how the shell's SHAPE is held.</para>
/// </summary>
public class WBondOneEditorTests
{
    private static WBondDesign Design(int wires = 2)
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        var array = new WireArray { Name = "G1", Profile = profile.Name };
        for (int w = 0; w < wires; w++)
            array.Wires.Add(profile.CreateWire(
                Point3.Mils(0, w * 6.0, 4), Point3.Mils(100, w * 6.0, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        design.Arrays.Add(array);

        return design;
    }

    private static string Read(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, .. parts]));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1. The seam: a LayoutDocument over the editor's own reference layout
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>This is the whole trick, and it needed no new abstraction.</b> <c>LayoutDocument</c>'s
    /// constructor already takes an existing view model, so the wBond editor's layout half can be the
    /// real <c>LayoutEditorView</c> bound to a real document — no interface extraction, no view-model
    /// surgery.
    /// </summary>
    [Fact]
    public void AReferenceLayout_ArrivesWrappedInALayoutDocument()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        Assert.Null(document.LayoutDocument);

        string dir = Path.Combine(Path.GetTempPath(), "crf-wbf-" + Guid.NewGuid().ToString("N")[..8]);
        document.EnsureReferenceLayout(dir);

        Assert.NotNull(document.LayoutDocument);
        Assert.Same(document.ReferenceLayout, document.LayoutDocument!.ViewModel);
        Assert.Same(document.ReferenceLayout, document.LayoutDocument.ActiveViewModel);

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The document is REPLACED with the layout, not stale beside it — an unpacked bundle (§9.1)
    /// installs a second reference layout over the first, and the hosted view has to follow it.
    /// </summary>
    [Fact]
    public void ReplacingTheReferenceLayout_ReplacesTheLayoutDocument()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());
        var first = document.LayoutDocument;

        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());

        Assert.NotNull(document.LayoutDocument);
        Assert.NotSame(first, document.LayoutDocument);
        Assert.Same(document.ReferenceLayout, document.LayoutDocument!.ViewModel);

        // …and dropping it entirely leaves nothing for the hosted view to bind to, which is §10's
        // third entry point before a cell has been dragged in.
        document.ReferenceLayout = null;
        Assert.Null(document.LayoutDocument);
    }

    /// <summary>
    /// The workspace hands the hierarchy service over AFTER the document exists — the reference layout
    /// is created on demand, so there is no creation point the host can inject it at. Same
    /// "apply now, and to every later one" setter shape <c>ConfigureReferenceLayout</c> already has.
    /// </summary>
    [Fact]
    public void TheHierarchyHost_ReachesADocumentThatAlreadyExists()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());
        Assert.Null(document.LayoutDocument!.Hierarchy);

        var host = new FakeHierarchy();
        document.LayoutHierarchy = host;
        Assert.Same(host, document.LayoutDocument.Hierarchy);

        // …and a LATER reference layout gets it too, without the host being asked again.
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());
        Assert.Same(host, document.LayoutDocument!.Hierarchy);
    }

    private sealed class FakeHierarchy : ILayoutHierarchyHost
    {
        public bool CanPushInto(LayoutInstance? instance, LayoutEditorViewModel? parentVm, out string? reason)
        { reason = null; return true; }
        public void PushIntoCell(LayoutDocument doc, LayoutInstance instance) { }
        public void PopOutOf(LayoutDocument doc) { }
        public void PopToLevel(LayoutDocument doc, int frameIndex) { }
        public void OpenCellInNewTab(LayoutDocument fromDoc, LayoutInstance instance) { }
        public System.Threading.Tasks.Task SaveLayoutDocumentAsync(LayoutDocument doc)
            => System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// <b>Hosting is what finally makes WB27 reachable.</b> The descent transform, the "locked
    /// reference at depth" rule and their refusal path have existed since WB-C — with no push-in in
    /// the wBond editor to trigger any of it. A hosted <c>LayoutDocument</c> has a frame stack, so
    /// pushing in now genuinely puts the wires into the sub-cell's frame.
    /// </summary>
    [Fact]
    public void PushingIn_GivesTheOverlayADescentChainToDrawThrough()
    {
        var editor = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(editor, frameBudgetMs: 1e9);

        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc = new LayoutDocument("cell.clay", baseVm);

        Assert.Empty(doc.DescentChain);
        Assert.True(WBondDescent.CanPlace(doc, baseVm.Model, doc.ActiveViewModel.Model));

        var sub = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        doc.PushIn(sub, "X1", new LayoutInstance { CellRef = "pad" });

        overlay.DescentChain = doc.DescentChain;
        overlay.CanPlaceAtDepth = WBondDescent.CanPlace(doc, baseVm.Model, doc.ActiveViewModel.Model);

        Assert.True(overlay.IsAtDepth);
        Assert.True(overlay.CanPlaceAtDepth);

        // At depth the wires are a LOCKED reference — every gesture belongs to the layout editor.
        Assert.False(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, Avalonia.Input.KeyModifiers.None, 1));
    }

    /// <summary>
    /// A resolution change part-way down cannot be composed exactly, so the overlay draws NOTHING
    /// rather than putting a wire foot at a silently wrong offset — the one thing that would be worse
    /// than not drawing, since judging the foot against the pad under it is why anyone pushed in.
    /// </summary>
    [Fact]
    public void ADescentThroughAResolutionChange_IsRefused()
    {
        var baseVm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        var doc = new LayoutDocument("cell.clay", baseVm);

        doc.PushIn(new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 2000 }),
                   "X1", new LayoutInstance { CellRef = "pad" });

        Assert.False(WBondDescent.CanPlace(doc, baseVm.Model, doc.ActiveViewModel.Model));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2. One Unit picker, and it is the hosted one
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The unit arrow now runs both ways</b>, because WB39a deleted the wBond editor's own metadata
    /// bar in favour of the hosted one. The visible picker writes the LAYOUT's display unit; every
    /// wBond readout follows the EDITOR's. Left one-way they would disagree the moment anyone touched
    /// the picker.
    /// </summary>
    [Fact]
    public void TheUnitFollowsInBothDirections()
    {
        var document = new WBondDocumentViewModel(new WBondViewModel(Design()));
        document.Editor.DisplayUnit = WBondUnit.Mil;
        document.ReferenceLayout = new LayoutEditorViewModel(new LayoutView());

        // Seeded from the .wBond's own saved unit on open…
        Assert.Equal(LayoutUnit.Mil, document.ReferenceLayout!.DisplayUnit);

        // …the hosted picker drives the editor…
        document.ReferenceLayout.DisplayUnit = LayoutUnit.Um;
        Assert.Equal(WBondUnit.Um, document.Editor.DisplayUnit);

        // …and anything that still sets the editor's unit drives the layout back.
        document.Editor.DisplayUnit = WBondUnit.Mm;
        Assert.Equal(LayoutUnit.Mm, document.ReferenceLayout.DisplayUnit);
    }

    /// <summary>The two unit tables are inverses of each other, member for member.</summary>
    [Fact]
    public void TheTwoUnitTables_AreInverses()
    {
        foreach (var unit in Enum.GetValues<WBondUnit>())
            Assert.Equal(unit, WBondDocumentViewModel.ToWBondUnit(WBondDocumentViewModel.ToLayoutUnit(unit)));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  3. The wire context menu moved to the OVERLAY, which is what makes it reachable at all
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// With one shared canvas there is one <c>ContextMenu</c> and one <c>Opening</c> handler — the
    /// Layout Editor's. The wire commands reach it through the overlay seam rather than through a
    /// second menu the wBond view declares, which is also what will hand a wirebond CELL (WB40) its
    /// wire menu in the ordinary Layout Editor with no wBond code in that view.
    /// </summary>
    [Fact]
    public void TheOverlay_ContributesTheWireCommands()
    {
        var editor = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(editor, frameBudgetMs: 1e9);

        var headers = overlay
            .BuildContextMenuItems(0, 0, 500, layout: null, host: new Panel())
            .OfType<MenuItem>()
            .Select(m => m.Header as string)
            .ToArray();

        Assert.Contains("Select All", headers);
        Assert.Contains("Select All Wires", headers);
        Assert.Contains("Invert Wire Selection", headers);
        Assert.Contains("Deselect All", headers);
        Assert.Contains("Delete Vertex", headers);
        Assert.Contains("Delete Segment", headers);
        Assert.Contains("Delete Wire", headers);
    }

    /// <summary>
    /// The deletes are always PRESENT and disabled with a reason when the click found nothing — an
    /// item that vanishes reads as the feature being broken, and one that no-ops reads as the click
    /// having missed.
    /// </summary>
    [Fact]
    public void AClickOnNothing_LeavesTheDeletesDisabledRatherThanAbsent()
    {
        var editor = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(editor, frameBudgetMs: 1e9);

        var deletes = overlay
            .BuildContextMenuItems(9_000_000, 9_000_000, 500, layout: null, host: new Panel())
            .OfType<MenuItem>()
            .Where(m => m.Header is string h && h.StartsWith("Delete", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(3, deletes.Length);
        Assert.All(deletes, m => Assert.False(m.IsEnabled));
    }

    /// <summary>
    /// At depth the wires are a locked reference (WB27), so the menu offers no wire commands at all —
    /// items that could not fire would be worse than none.
    /// </summary>
    [Fact]
    public void AtDepth_TheOverlayOffersNoMenuItems()
    {
        var editor = new WBondViewModel(Design());
        var overlay = new WBondLayoutOverlay(editor, frameBudgetMs: 1e9)
        {
            DescentChain = [(new LayoutInstance { CellRef = "pad" }, 0, 0)],
        };

        Assert.Empty(overlay.BuildContextMenuItems(0, 0, 500, layout: null, host: new Panel()));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  4. §4's own checklist — the shell really is gone
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The transcribed toolbar row is deleted, not disabled.</b> WB-F §2/M1: its whole file is
    /// gone, and nothing in the wBond view routes a layout tool any more.
    /// </summary>
    [Fact]
    public void TheTranscribedLayoutToolbar_IsGone()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "circuitrf.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        Assert.False(File.Exists(Path.Combine(dir!, "src", "Ui", "Views", "WBond",
                                              "WBondEditorView.LayoutTools.cs")));

        var xaml = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");
        foreach (var handler in new[] { "OnLayoutTool", "OnLayoutSelectTool", "OnLayoutInstanceTool",
                                        "OnLayoutRotateCcw", "OnLayoutRotateCw", "OnLayoutMirrorH",
                                        "OnLayoutMirrorV", "OnInsertBitmap", "OnLayoutPathWidthCommit" })
            Assert.DoesNotContain(handler, xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// §0.2's rule, as a test: <b>if a change would be needed in both editors it belongs in neither.</b>
    /// Every surviving <c>Click=</c> in the wBond editor is a wire, profile, view-arrangement, file or
    /// export handler — none of them does what a <c>LayoutEditorView</c> handler already does.
    /// </summary>
    [Fact]
    public void EverySurvivingClickHandler_IsWBondsOwn()
    {
        var xaml = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");

        var handlers = System.Text.RegularExpressions.Regex
            .Matches(xaml, "Click=\"(?<h>[A-Za-z0-9_]+)\"")
            .Select(m => m.Groups["h"].Value)
            .Distinct()
            .ToArray();

        string[] allowed =
        [
            "OnZoomToFit", "OnZoomIn", "OnZoomOut", "OnZoom1To1",
            "OnSave", "OnSaveAs", "OnCycleViewMode",
            "OnReverse", "OnStraighten", "OnReapplyProfile", "OnTransform", "OnDetach",
            "OnExportDxf", "OnImportWires", "OnExportTouchstone", "OnCopyGraphic",
        ];

        Assert.All(handlers, h => Assert.Contains(h, allowed));
    }

    /// <summary>
    /// The layout half IS the hosted control, bound to the document the view model builds — not a bare
    /// canvas with a toolbar transcribed around it.
    /// </summary>
    [Fact]
    public void TheLayoutHalf_IsTheHostedLayoutEditorView()
    {
        var xaml = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");

        Assert.Contains("lv:LayoutEditorView", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding ViewModel.LayoutDocument}\"", xaml, StringComparison.Ordinal);

        // …and its canvas is reached through the pass-through property, never the visual tree.
        var code = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs");
        Assert.Contains("HostedLayoutView.CanvasOverlay = _bound.Overlay;", code, StringComparison.Ordinal);
        Assert.DoesNotContain("GetVisualDescendants", code, StringComparison.Ordinal);
        Assert.DoesNotContain("FindControl<LayoutCanvas>", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// A wBond document is a document, so the layout half must not add a SECOND torn-off File menu
    /// describing a different file (it would appear the moment the tab was floated on Windows/Linux).
    /// </summary>
    [Fact]
    public void TheHostedEditor_SuppressesItsOwnFileMenu()
    {
        var code = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs");
        Assert.Contains("HostedLayoutView.IsHostedInAnotherDocument = true;", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Delete must reach the layout editor when no wire is selected.</b> The wBond key handler is a
    /// TUNNEL handler on an ancestor of the hosted view, so an unconditional <c>e.Handled</c> would
    /// swallow every Delete meant for a selected shape or instance.
    /// </summary>
    [Fact]
    public void DeleteIsGatedOnAWireSelection_SoTheLayoutEditorStillGetsIt()
    {
        var code = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml.cs");

        int at = code.IndexOf("case Key.Delete:", StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.Contains("if (_bound.Editor.Selection.IsEmpty) return;",
                        code[at..(at + 400)], StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  5. Ctrl+Z undoes what the user did LAST, across two histories
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two real histories, one Ctrl+Z.</b> Routing by focus is wrong (a WIRE drag happens on the
    /// LAYOUT canvas) and "wires first" is wrong (it would undo a wire move made ten minutes ago
    /// rather than the rectangle just drawn). The stamp on each recorded entry is what makes the
    /// question total.
    /// </summary>
    [Fact]
    public void TheEditStamps_OrderTheTwoHistoriesAgainstEachOther()
    {
        var wires = new WBondViewModel(Design());
        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });

        // A wire edit, then a layout edit: the layout's entry is the newer one.
        wires.StraightenSelection();
        wires.SelectAllWires();
        wires.StraightenSelection();
        layout.Execute(new CircuitRF.Ui.Commands.Layout.AddShapeCommand(layout.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));

        Assert.True(wires.TopUndoStamp > 0);
        Assert.True(layout.UndoRedo.TopUndoStamp > wires.TopUndoStamp);

        // …and after undoing it, the WIRE entry is once again the most recent thing done.
        layout.UndoRedo.Undo();
        Assert.Equal(0, layout.UndoRedo.TopUndoStamp);
        Assert.True(wires.TopUndoStamp > 0);
    }

    /// <summary>
    /// Undo MOVES a cursor through history rather than adding to it: an undone entry keeps the stamp
    /// it was recorded with. Re-stamping on undo would make every later Ctrl+Z pick the same history
    /// again, forever.
    /// </summary>
    [Fact]
    public void UndoingDoesNotRestampTheEntry()
    {
        var layout = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        layout.Execute(new CircuitRF.Ui.Commands.Layout.AddShapeCommand(layout.Model,
            new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }));
        long recorded = layout.UndoRedo.TopUndoStamp;

        layout.UndoRedo.Undo();
        Assert.Equal(recorded, layout.UndoRedo.TopRedoStamp);

        layout.UndoRedo.Redo();
        Assert.Equal(recorded, layout.UndoRedo.TopUndoStamp);
    }

    /// <summary>
    /// An edit that changed nothing leaves NO entry — and no stamp either. A stamp left behind would
    /// make the wire history look more recently edited than it is, and Ctrl+Z would come to it
    /// instead of to the layout's.
    /// </summary>
    [Fact]
    public void AnEditThatChangedNothing_LeavesNoStampBehind()
    {
        var wires = new WBondViewModel(Design());
        Assert.Equal(0, wires.TopUndoStamp);

        // Nothing is selected, so this transform touches nothing.
        Assert.Equal(0, wires.StraightenSelection());
        Assert.Equal(0, wires.TopUndoStamp);
        Assert.False(wires.CanUndo);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  6. WB40 — a wirebond CELL
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>WB40, revised 2026-08-17 — the wires are an ATTACHMENT: <c>layout/&lt;stem&gt;.wBond</c>
    /// beside <c>layout/&lt;stem&gt;.clay</c>.</b> Not a view (a view sub-folder means one primary and
    /// the rest inert, which WB28 makes wrong), and not the cell root (a cell may hold several
    /// <c>.clay</c> files, and wires are drawn over SPECIFIC pads).
    /// </summary>
    [Fact]
    public void TheAttachment_IsFoundBesideItsOwnClay()
    {
        using var cell = new TempCell("amp");

        Assert.Null(WBondCell.FindFor(cell.ClayPath));

        File.WriteAllText(cell.WBondPath, "{}");
        Assert.Equal(cell.WBondPath, WBondCell.FindFor(cell.ClayPath));
    }

    /// <summary>
    /// <b>Stem pairing is the whole point: a second layout in the same cell gets its own wires.</b>
    /// This is what cell-root placement could not express — it had one slot for a cell that may have
    /// many layouts, and could only guess which artwork a bond list belonged to.
    /// </summary>
    [Fact]
    public void TwoLayoutsInOneCell_EachResolveTheirOwnWires()
    {
        using var cell = new TempCell("amp");

        string clayB  = Path.Combine(cell.LayoutDir, "amp_revB.clay");
        string wiresB = Path.Combine(cell.LayoutDir, "amp_revB.wBond");
        File.WriteAllText(clayB, "{}");
        File.WriteAllText(cell.WBondPath, "{}");
        File.WriteAllText(wiresB, "{}");

        Assert.Equal(cell.WBondPath, WBondCell.FindFor(cell.ClayPath));
        Assert.Equal(wiresB,         WBondCell.FindFor(clayB));
    }

    /// <summary>
    /// <b>A pre-2026-08-17 workspace still opens with its wires, and the move is NAMED.</b> Silently
    /// dropping them is the one outcome that is not acceptable; silently reading them forever is how a
    /// migration never happens.
    /// </summary>
    [Fact]
    public void ALegacyCellRootSidecar_StillResolves_AndSaysSo()
    {
        using var cell = new TempCell("amp");
        File.WriteAllText(cell.LegacyWBondPath, "{}");

        var (path, note) = WBondCell.Resolve(cell.ClayPath);

        Assert.Equal(cell.LegacyWBondPath, path);
        Assert.NotNull(note);
        Assert.Contains("layout/amp.wBond", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The attachment wins over a legacy root file, and silently.</b> Once the file has been moved
    /// there is nothing left to say — and a stale copy left behind at the root must not be able to
    /// shadow the one that is correctly placed.
    /// </summary>
    [Fact]
    public void TheAttachment_WinsOverALegacyRootFile()
    {
        using var cell = new TempCell("amp");
        File.WriteAllText(cell.LegacyWBondPath, "{}");
        File.WriteAllText(cell.WBondPath, "{}");

        var (path, note) = WBondCell.Resolve(cell.ClayPath);

        Assert.Equal(cell.WBondPath, path);
        Assert.Null(note);
    }

    /// <summary>
    /// <b>An ORPHAN is reported — the price of stem pairing, not a nicety.</b> Renaming a <c>.clay</c>
    /// in Finder detaches its wires, and unlike every other Finder-edit failure mode (a "Not Found"
    /// glyph, a warning row) this one would otherwise remove wires from a simulation the user believes
    /// includes them.
    /// </summary>
    [Fact]
    public void WiresPairingWithNoClay_AreReportedRatherThanIgnored()
    {
        using var cell = new TempCell("amp");
        File.WriteAllText(Path.Combine(cell.LayoutDir, "amp_old.wBond"), "{}");

        var (path, note) = WBondCell.Resolve(cell.ClayPath);

        Assert.Null(path);
        Assert.NotNull(note);
        Assert.Contains("amp_old.wBond", note, StringComparison.Ordinal);
        Assert.Contains("amp.wBond",     note, StringComparison.Ordinal);   // …and the rename that fixes it
    }

    /// <summary>
    /// A hand-named bond list dropped in beside the artwork is a normal thing for an assembly house
    /// to send, so a single differently-named <c>.wBond</c> at the cell root still resolves through the
    /// legacy branch. TWO is ambiguous and resolves to none rather than to whichever sorts first.
    /// </summary>
    [Fact]
    public void OneOddlyNamedLegacySidecarResolves_TwoDoNot()
    {
        using var cell = new TempCell("amp");

        File.WriteAllText(Path.Combine(cell.CellDir, "bondlist.wBond"), "{}");
        Assert.Equal(Path.Combine(cell.CellDir, "bondlist.wBond"), WBondCell.FindFor(cell.ClayPath));

        File.WriteAllText(Path.Combine(cell.CellDir, "revB.wBond"), "{}");
        Assert.Null(WBondCell.FindFor(cell.ClayPath));
    }

    /// <summary>
    /// <b>Nothing new is invented.</b> <c>WireDesign</c> was already the seam and
    /// <c>WBondLayoutOverlay</c> already draws through it — attaching a cell's own sidecar is the
    /// second setter, not a second mechanism. WB23 is untouched: no wire enters the <c>.clay</c>.
    /// </summary>
    [Fact]
    public void ACellWithASidecar_OpensWithItsWiresAttached()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design(3));

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));

        Assert.NotNull(vm.WireDesign);
        Assert.NotNull(vm.WireOverlay);
        Assert.True(vm.HasWireDesign);
        Assert.Equal(3, vm.WireDesign!.WireCount);
        Assert.Empty(vm.Model.Shapes);   // WB23 — no wire entered the .clay
    }

    /// <summary>
    /// In the Layout Editor the artwork is the subject and the wires are what ride over it, so empty
    /// space stays the LAYOUT's marquee and an armed drawing tool takes every press. Both are the
    /// opposite of the wBond editor's defaults, and deliberately so.
    /// </summary>
    [Fact]
    public void ACellsOverlay_LeavesTheLayoutsOwnGesturesAlone()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design());

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));

        var overlay = vm.WireOverlay!;
        Assert.False(overlay.WireMarqueeEnabled);
        Assert.False(overlay.OnPointerPressed(9_000_000, 9_000_000, 500, Avalonia.Input.KeyModifiers.None, 1));

        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        Assert.True(overlay.LayoutToolArmed!());
    }

    /// <summary>
    /// <b>A wire edit dirties the cell, and saving the cell writes the wires back.</b> An editable
    /// overlay that silently lost its edits would be worse than a read-only one — and the write hangs
    /// off <c>MarkSaved</c> because the workspace saves sub-cell sessions with a bare
    /// <c>LayoutPersistence.SaveToFile</c>, so no single writer sees them all.
    /// </summary>
    [Fact]
    public void EditingACellsWires_DirtiesItAndSurvivesASave()
    {
        using var cell = new TempCell("amp");
        string sidecar = cell.WBondPath;
        WBondIo.WriteFile(sidecar, Design(3));

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));
        Assert.False(vm.IsDirty);

        // ONE of the three, deliberately. Deleting them ALL is a different case since 2026-08-17 —
        // an emptied layout DELETES its sidecar rather than writing a wires-less one (owner) — and it
        // has its own test in WBondRound6Tests. What this one is about is that an ordinary edit
        // reaches the file at all.
        vm.WireEditor!.Selection = new WireSelection { Wires = { 0 } };
        Assert.True(vm.WireEditor.DeleteSelectedWires() > 0);
        Assert.True(vm.IsDirty);

        vm.MarkSaved();
        Assert.False(vm.IsDirty);

        // …and the file on disk really changed.
        Assert.Equal(2, WBondIo.ReadFile(sidecar).WireCount);
    }

    /// <summary>
    /// A <c>.wBond</c> that will not parse is REPORTED and the layout still opens — WB35's "never
    /// fails, never substitutes", at the one new place a wBond file is read from. Withholding the
    /// artwork because a bond list is malformed helps nobody.
    /// </summary>
    [Fact]
    public void AnUnreadableSidecar_IsReportedAndTheLayoutStillOpens()
    {
        using var cell = new TempCell("amp");
        File.WriteAllText(cell.WBondPath, "this is not a wBond file");

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);

        string? reported = null;
        Assert.False(WBondCell.TryAttach(vm, cell.ClayPath, m => reported = m));

        Assert.NotNull(reported);
        Assert.Contains("amp.wBond", reported!, StringComparison.Ordinal);
        Assert.Null(vm.WireDesign);
        Assert.Null(vm.WireOverlay);
    }

    /// <summary>
    /// <c>&lt;cell&gt;/layout/&lt;cell&gt;.clay</c> on disk, cleaned up afterwards.
    /// </summary>
    private sealed class TempCell : IDisposable
    {
        public string Root { get; }
        public string CellDir { get; }
        public string LayoutDir { get; }
        public string ClayPath { get; }

        /// <summary>Where an attachment belongs since WB40's 2026-08-17 revision.</summary>
        public string WBondPath { get; }

        /// <summary>Where the pre-2026-08-17 sidecar sat — the legacy branch, still read.</summary>
        public string LegacyWBondPath { get; }

        public TempCell(string name)
        {
            Root = Path.Combine(Path.GetTempPath(), "crf-wb40-" + Guid.NewGuid().ToString("N")[..8]);
            CellDir = Path.Combine(Root, name);
            LayoutDir = Path.Combine(CellDir, "layout");
            Directory.CreateDirectory(LayoutDir);
            ClayPath = Path.Combine(LayoutDir, name + ".clay");
            WBondPath = Path.Combine(LayoutDir, name + ".wBond");
            LegacyWBondPath = Path.Combine(CellDir, name + ".wBond");
            File.WriteAllText(ClayPath, "{}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  7. M3 — the two panels follow the active layout
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>This is the milestone the whole phase is for.</b> §10.1: once a wirebond cell carries its own
    /// wires (WB40), the profile view and the Array Inductance panel are dock tools that follow the
    /// active layout — so "the wBond Editor" stops being a separate editor and becomes the Layout
    /// Editor with two panels open.
    /// </summary>
    [Fact]
    public void TheTwoPanels_FollowTheActiveLayout()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design(3));

        var wirebond = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(wirebond, cell.ClayPath));

        var profile = new WBondProfileTool();
        var inductance = new WBondInductanceTool();

        profile.SetActiveLayout(wirebond);
        inductance.SetActiveLayout(wirebond);

        Assert.True(profile.HasWires);
        Assert.Same(wirebond.WireEditor, profile.Editor);
        Assert.True(inductance.HasWires);
        Assert.NotEmpty(inductance.Panel.Rows);

        // …and an ordinary layout leaves both empty and saying so, rather than showing the previous
        // document's wires beside this one's artwork.
        var plain = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 });
        profile.SetActiveLayout(plain);
        inductance.SetActiveLayout(plain);

        Assert.False(profile.HasWires);
        Assert.Null(profile.Editor);
        Assert.False(inductance.HasWires);
    }

    /// <summary>
    /// §10.1's SECOND surface: a <c>.wBond</c> opened on its own. There the wires are the document
    /// rather than a property of the artwork, so the panels follow the document's own editor.
    /// </summary>
    [Fact]
    public void TheTwoPanels_AlsoFollowAWBondDocument()
    {
        var editor = new WBondViewModel(Design(2));
        var inductance = new WBondInductanceTool();

        inductance.SetActiveWBond(editor);
        Assert.True(inductance.HasWires);
        Assert.NotEmpty(inductance.Panel.Rows);

        inductance.SetActiveWBond(null);
        Assert.False(inductance.HasWires);
    }

    /// <summary>
    /// The inductance tool's rows follow the editor's own readout live — the panel is a READOUT of
    /// whatever editor is current, and an edit that did not reach it would be a number on screen that
    /// is no longer true.
    /// </summary>
    [Fact]
    public void TheInductanceTool_FollowsTheEditorsReadout()
    {
        var editor = new WBondViewModel(Design(3));
        var tool = new WBondInductanceTool();
        tool.SetActiveWBond(editor);

        var before = tool.Panel.Rows[0].Wires;

        editor.SelectAllWires();
        Assert.True(editor.DeleteSelectedWires() > 0);

        Assert.NotEqual(before, tool.Panel.Rows.Count > 0 ? tool.Panel.Rows[0].Wires : "");
    }

    /// <summary>
    /// <b>Neither panel is in a shipped default layout, deliberately</b> — most designs have no
    /// wirebonds, and a panel that would be empty for most users is one they would have to learn about
    /// only to close. They are opened from View ▸ Panels, and both ids are in
    /// <c>DockPanelIds.All</c> so a panel the user DID open is captured and restored with the rest.
    /// </summary>
    [Fact]
    public void NeitherPanelShipsOpen_ButBothAreCaptured()
    {
        Assert.Contains(DockPanelIds.WBondProfile, DockPanelIds.All);
        Assert.Contains(DockPanelIds.WBondInductance, DockPanelIds.All);

        foreach (var layout in new[] { DockLayoutDefaults.Default(), DockLayoutDefaults.ProjectTreeAndLibrary() })
        {
            Assert.DoesNotContain(layout.Panels, p => p.Id == DockPanelIds.WBondProfile);
            Assert.DoesNotContain(layout.Panels, p => p.Id == DockPanelIds.WBondInductance);
        }
    }

    /// <summary>
    /// <b>One control, two hosts</b> — §0.2's rule applied to a panel rather than to a toolbar. The
    /// wBond editor hosts both controls inline; the dock tools host the same two.
    /// </summary>
    [Fact]
    public void BothPanelsAreOneControlHostedTwice()
    {
        var editor = Read("src", "Ui", "Views", "WBond", "WBondEditorView.axaml");
        Assert.Contains("wbv:WBondInductancePanelView", editor, StringComparison.Ordinal);
        Assert.Contains("wbv:WBondProfileView", editor, StringComparison.Ordinal);

        Assert.Contains("wbv:WBondInductancePanelView",
                        Read("src", "Ui", "Views", "WBond", "WBondInductanceToolView.axaml"), StringComparison.Ordinal);
        Assert.Contains("wbv:WBondProfileView",
                        Read("src", "Ui", "Views", "WBond", "WBondProfileToolView.axaml"), StringComparison.Ordinal);

        // …and the group prompts behind the panel's double-click are shared too, since there are now
        // three ways in (both panel hosts, and the profile view's own context menu).
        foreach (var file in new[] { "WBondInductancePanelView.axaml.cs", "WBondProfileView.ContextMenu.cs" })
            Assert.Contains("WBondGroupEdits.",
                            Read("src", "Ui", "Views", "WBond", file), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  9. Dirty tracking and persistence of the wire layer
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A wire edit dirties the DOCUMENT, and an ordinary Save writes the <c>.wBond</c>.</b>
    ///
    /// <para>The separate <c>_wireDirty</c> flag is why this works: a wire edit carries no entry on the
    /// LAYOUT's undo stack (the wires have their own history), so a dirty state derived from the undo
    /// position alone would report the cell clean with unsaved wires in it.</para>
    ///
    /// <para>WB23 still holds — the <c>.clay</c>'s own content is unchanged by a wire edit. It is the
    /// document that is dirty, not the artwork.</para>
    /// </summary>
    [Fact]
    public void AWireEdit_DirtiesTheDocument_AndSaveWritesTheWBond()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design(2));

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));
        vm.MarkSaved();
        Assert.False(vm.IsDirty);

        // A structural wire edit — the same commit path an array add/rename/delete takes.
        vm.WireEditor!.Design.Arrays[0].Wires.Add(
            vm.WireEditor.Design.Profiles[0].CreateWire(
                Point3.Mils(0, 40, 4), Point3.Mils(100, 40, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        vm.WireEditor.CommitStructuralChange();

        Assert.True(vm.IsDirty);                          // …the document, not the .clay's content
        Assert.Equal(2, WBondIo.ReadFile(cell.WBondPath).WireCount);   // not yet on disk

        vm.PerformSave(cell.ClayPath);

        Assert.False(vm.IsDirty);
        Assert.Equal(3, WBondIo.ReadFile(cell.WBondPath).WireCount);   // …written by MarkSaved
    }

    /// <summary>
    /// <b>Save As carries the wires to the new stem — including when they were never edited.</b>
    ///
    /// <para>This is a REGRESSION GUARD for stem pairing itself. Under WB40's original cell-root
    /// placement Save As worked by accident: the sidecar was found one level UP from the artwork, so
    /// <c>amp_v2.clay</c> resolved to the same <c>&lt;cell&gt;.wBond</c>. Stem pairing removes that
    /// accident — without <c>RetargetWiresForSaveAs</c>, the artwork lands at the new name and the
    /// wires are written back into the OLD file, so the layout the user just created opens with no
    /// wires while their edits sit somewhere they did not ask for.</para>
    /// </summary>
    [Fact]
    public void SaveAs_CarriesTheWiresToTheNewStem_AndLeavesTheOriginalIntact()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.WBondPath, Design(2));

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));
        vm.MarkSaved();

        // No wire edit at all — the copy must still be complete.
        string clayB = Path.Combine(cell.LayoutDir, "amp_revB.clay");
        vm.PerformSave(clayB);

        string wiresB = Path.Combine(cell.LayoutDir, "amp_revB.wBond");
        Assert.True(File.Exists(wiresB));
        Assert.Equal(2, WBondIo.ReadFile(wiresB).WireCount);

        // Save As copies: the original pair is untouched and still resolves to itself.
        Assert.True(File.Exists(cell.WBondPath));
        Assert.Equal(cell.WBondPath, WBondCell.FindFor(cell.ClayPath));
        Assert.Equal(wiresB,         WBondCell.FindFor(clayB));
    }

    /// <summary>
    /// <b>An ordinary Save never retargets, so a legacy cell-root file stays where it is.</b>
    /// Migrating it silently would leave a stale duplicate at the root that then LOSES resolution to
    /// the file just written beside the artwork — the move is the user's to make, and it is reported.
    /// </summary>
    [Fact]
    public void APlainSave_WritesBackToALegacyRootFile_WithoutMigratingIt()
    {
        using var cell = new TempCell("amp");
        WBondIo.WriteFile(cell.LegacyWBondPath, Design(2));

        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000 }, cell.ClayPath);
        Assert.True(WBondCell.TryAttach(vm, cell.ClayPath));
        vm.MarkSaved();

        vm.WireEditor!.Design.Arrays[0].Wires.RemoveAt(1);
        vm.WireEditor.CommitStructuralChange();
        vm.PerformSave(cell.ClayPath);

        Assert.Equal(1, WBondIo.ReadFile(cell.LegacyWBondPath).WireCount);   // written where it lives
        Assert.False(File.Exists(cell.WBondPath));                           // and NOT duplicated
    }
}
