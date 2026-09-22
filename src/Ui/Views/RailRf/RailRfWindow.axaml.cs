using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// The railRF window (railrf.md §11).
/// </summary>
/// <remarks>
/// <b>It is the Match Designer's window, structurally.</b> Shown UNOWNED so it can go behind the
/// workspace, cascaded off the workspace's corner by <see cref="MatchWindowPlacement"/>'s own
/// arithmetic, closed with its owner, and deduplicated per DOCUMENT rather than per window — two
/// views of one <c>.crail</c> would write it from two working copies.
///
/// <para><b>The code-behind does four things and no more</b>, which is the line that window already
/// draws: it opens and positions the window, it binds the two tab strips (a <c>ToggleButton</c> strip
/// has no single selected-value property to bind, so the grouping is done here rather than with four
/// converters), it gives the space a hidden panel released to the panels still on screen (a
/// <c>ColumnDefinition</c>'s width is not something a child's <c>IsVisible</c> can reach —
/// <see cref="SyncPanes"/>), and it gives the view model its UI-thread post. Everything else is the
/// view model's.
/// </para>
/// </remarks>
public partial class RailRfWindow : Window
{
    /// <summary>One window per DOCUMENT PATH. A standalone window (no path yet) is not
    /// deduplicated — two of those are two independent scratch documents, which is a thing a user may
    /// legitimately want side by side, exactly as the Match Designer's standalone is.</summary>
    private static readonly Dictionary<string, RailRfWindow> Open =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>This window's key in <see cref="Open"/>, or null while it is in no table at all —
    /// the standalone case, until its first save. <b>Held as a field rather than captured</b>,
    /// because Save as… moves a window from one key to another and a closure over the key it was
    /// opened with would then remove the wrong row (<see cref="AdoptPath"/>).</summary>
    private string? _openKey;

    public RailRfWindow()
    {
        InitializeComponent();

        CopperTab.Click     += (_, _) => SetOverlay(RailBoardOverlay.Copper);
        DropTab.Click       += (_, _) => SetOverlay(RailBoardOverlay.Drop);
        ImpedanceTab.Click  += (_, _) => SetOverlay(RailBoardOverlay.Impedance);
        ClassTab.Click      += (_, _) => SetOverlay(RailBoardOverlay.Class);
        DcTab.Click         += (_, _) => SetResultsTab(RailResultsTab.Dc);
        FrequencyTab.Click  += (_, _) => SetResultsTab(RailResultsTab.Frequency);

        // The four panel lamps' own arithmetic. Captured BEFORE anything can change it, because
        // the sizes below are the AXAML's and this file must not carry a second copy of them.
        _specificationColumn = PaneGrid.ColumnDefinitions[0].Width;
        _resultsColumn       = PaneGrid.ColumnDefinitions[4].Width;
        _partsGridMaxHeight  = PartsGrid.MaxHeight;
        _partsListMaxHeight  = PartsList.MaxHeight;
        SyncPanes();

        // The results plot derives its height from its width, so it needs a ceiling the moment the
        // results column can be wide — see CapResultsPlot.
        ResultsPane.SizeChanged += (_, _) => CapResultsPlot();

        WireImportButton();
        WireExportButtons();
        WireCompareButton();
        WireOpenButton();
        WireBoardCanvas();
        WireLiveArtwork();
        WireEscape();
        WireUndoKeys();

        // The railRF chapter of the reference, through the launcher every other Help button in the
        // application uses — the Match Designer's own line.
        HelpButton.Click += (_, _) => DocLauncher.Open("reference/railrf.html");

        DataContextChanged += (_, _) =>
        {
            if (Vm is not { } vm) return;

            // The view model is framework-free and defaults this to an inline call, which is what
            // lets a test drive the Fast loop with no application host. The WINDOW is what knows
            // there is a dispatcher — the same split the rest of this view model keeps.
            vm.PostToUi = a => Dispatcher.UIThread.Post(a);
            vm.RunOffThread = (work, token) => System.Threading.Tasks.Task.Run(work, token);

            // The Turn gesture edits the layout through its OWN window's command stack where one has
            // the `.clay` open — undoable there, and dirtying that document like any other edit.
            vm.EditLiveLayout = (clay, edits, description) =>
            {
                if (WorkspaceLocator.Any()?.LiveLayoutSession(clay) is not { } session) return false;
                session.ReplaceInstances(edits, description);
                return true;
            };
            InstallSaveHook(vm);
            InstallMenuHooks(vm);

            SyncTabs();
            SyncPanes();
            BindBoardOverlay(vm);
            BindImpedancePlot(vm);
            BindBoardRulerUnits();
            SyncPlotTheme();
            vm.PropertyChanged += OnVmPropertyChanged;
        };

        // ── The menu bar ──────────────────────────────────────────────────────────────────
        // SEEDED HERE as well as rebuilt on open. An Avalonia MenuItem reports HasSubMenu from its
        // item COUNT, so a Window menu that starts empty is a LEAF: clicking it opens nothing and
        // SubmenuOpened — the thing that would have filled it — can never fire. That is a
        // self-latching dead menu, and WorkspaceWindow shipped it once already.
        RebuildWindowMenu();
        Opened    += (_, _) => { EnsureWindowNativeItem(); RebuildWindowMenu(); };
        Activated += (_, _) => RebuildWindowMenu();

        // A theme change is a different event from a theme VARIANT change, and this window paints
        // its plot from the variant — see SyncPlotTheme.
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();
    }

    // ── The results plot ─────────────────────────────────────────────────────────────

    private PlotContainerViewModel? _boundPlotContainer;

    /// <summary>
    /// Gives the results <c>PlotControl</c> its host, which is what makes its markers work.
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's own <c>Bind</c>, and the same omission it was written for.</b> A
    /// <c>PlotControl</c> asks its HOST for the next marker index, the info-box view model, the
    /// container and the selected markers, and a host that is null answers "nothing" to all four —
    /// silently. Nothing in this window had ever supplied them, so a double-click on this plot did
    /// nothing at all — reported by the owner on 2026-09-19 as a double click that adds no marker,
    /// which is the same report that window got on 2026-08-20 and the same cause.
    ///
    /// <para><c>HandleDoubleTapAt</c> is documented as "called by the HOST on DoubleTapped" — it is
    /// not wired by the control — so the subscription below is the whole of the feature: a
    /// double-click near a trace adds a marker there, one on empty plot area opens Plot
    /// Properties.</para>
    ///
    /// <para>Bound once per view model. The container is the one <see cref="RailRfViewModel"/>
    /// built, never a second one: two containers over one plot would number markers
    /// independently.</para>
    /// </remarks>
    private void BindImpedancePlot(RailRfViewModel vm)
    {
        var container = vm.ImpedanceContainer;
        if (ReferenceEquals(_boundPlotContainer, container)) return;
        _boundPlotContainer = container;

        var plot = ImpedancePlotControl;

        plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
        plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
        plot.ContainerProvider           = () => container;
        plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
        plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;

        plot.DoubleTapped += (_, args) =>
        {
            plot.HandleDoubleTapAt(args.GetPosition(plot));
            args.Handled = true;
        };

        plot.PlotChanged += container.OnPlotChanged;
        plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
        plot.MarkerAdded += container.OnMarkerAdded;
        container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();

        // The container's logical rectangle must equal the PlotControl's real one, because that is
        // the coordinate space a marker info box is placed and dragged in — see MarkerInfoBoxLayer's
        // own note in the AXAML. LayoutUpdated rather than SizeChanged: the results pane's
        // ScrollViewer can MOVE this plot without resizing it, and a box that stayed behind would be
        // pointing at nothing.
        LayoutUpdated += (_, _) => SyncPlotContainer();
        SyncPlotContainer();
    }

    /// <summary>
    /// Keeps <c>ImpedanceContainer</c>'s rectangle equal to the results <c>PlotControl</c>'s, in the
    /// info-box layer's coordinates.
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's <c>SyncPlotContainers</c>, for one plot.</b> Without it the container
    /// keeps the seed rectangle <c>BuildPlotHost</c> gave it — origin (0, 0), 320 x 198 — so
    /// <c>DataDisplayViewModel.PlaceInfoBoxInLogicalCoords</c> puts a new marker's box at the top
    /// left of the WINDOW rather than beside its marker.
    ///
    /// <para><c>LayoutUpdated</c> fires on every pass, so a rectangle that has not actually moved is
    /// returned on rather than re-published: <c>NotifyViewProperties</c> walks every info box.</para>
    /// </remarks>
    private void SyncPlotContainer()
    {
        if (_boundPlotContainer is not { } container) return;

        var plot  = ImpedancePlotControl;
        var layer = MarkerInfoBoxLayer;
        if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) return;
        if (plot.TranslatePoint(default, layer) is not { } origin) return;

        if (Math.Abs(container.Left   - origin.X)           < 0.5
         && Math.Abs(container.Top    - origin.Y)           < 0.5
         && Math.Abs(container.Width  - plot.Bounds.Width)  < 0.5
         && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
            return;

        container.Left   = origin.X;
        container.Top    = origin.Y;
        container.Width  = plot.Bounds.Width;
        container.Height = plot.Bounds.Height;
        container.NotifyViewProperties();
    }

    // ── Escape: nothing is selected (owner, 2026-09-19) ──────────────────────────────

    /// <summary>
    /// Clears whichever of the four lists has a row selected — and any selected marker on the
    /// results plot — and with it the mark on the board.
    /// </summary>
    /// <remarks>
    /// <b>All four, not just the parts table</b> (owner, 2026-09-19). The keystroke worked on one
    /// list and did nothing on the next three, which is the one shape a user cannot diagnose: there
    /// is no way to tell a key that is not wired from a key that found nothing to clear.
    ///
    /// <para><b>And the results plot's marker, which is a fifth selectable thing</b> (owner,
    /// 2026-09-19) — the same shape again: the gate below asked <c>HasRowSelection</c>, a marker is
    /// not on one of the four lists, so the handler returned and Escape was inert on it. See
    /// <c>RailRfViewModel.Selection</c>'s own header.</para>
    /// </remarks>
    /// <remarks>
    /// <b>Bubbling, and it defers to anything that already handled the key.</b> Escape is the layout
    /// canvas's own disarm for the zoom box and its own cancel for a drag; this window promises that
    /// someone who has learned that canvas has learned this one (§11.6), so taking Escape away from it
    /// would be exactly the near-miss that rule is about. The canvas sees the key first because it has
    /// focus, and what arrives here unhandled is an Escape nothing else wanted.
    ///
    /// <para><b>And never while focus is in a text field</b>, where Escape belongs to the field —
    /// <see cref="RailKeyboardGate"/>'s own predicate, which is already the gate on every navigation
    /// key for the same reason: railRF's left column is editable rows.</para>
    /// </remarks>
    private void WireEscape() => AddHandler(KeyDownEvent, (_, e) =>
    {
        if (e.Handled || e.Key != Key.Escape) return;
        if (RailKeyboardGate.IsTextEntry(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
            return;
        if (Vm is not { HasSelection: true } vm) return;

        vm.ClearSelectionCommand.Execute(null);
        e.Handled = true;
    }, RoutingStrategies.Bubble);

    // ── Undo and redo (owner, 2026-09-20) ────────────────────────────────────────────

    /// <summary>
    /// Ctrl/⌘+Z and Ctrl/⌘+Shift+Z, on the view model's own two commands.
    /// </summary>
    /// <remarks>
    /// <b>A handler rather than a <c>Window.KeyBinding</c>, which is what Save and Open use.</b> A
    /// key binding fires wherever focus is, and half this window is editable rows: Ctrl+Z inside a
    /// text box belongs to the text box, and taking it would replace the edit the user is making
    /// with a document-wide undo they did not ask for. <see cref="RailKeyboardGate.IsTextEntry"/> is
    /// already this window's predicate for exactly that, on every navigation key and on Escape.
    ///
    /// <para><b>Bubbling, and it defers to anything that handled the key first</b> — Escape's own
    /// rule here, for the same reason: the board canvas is a real editor's canvas and this window
    /// promises that someone who learned it has learned this one.</para>
    /// </remarks>
    private void WireUndoKeys() => AddHandler(KeyDownEvent, (_, e) =>
    {
        if (e.Handled || e.Key != Key.Z) return;

        bool modifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                     || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!modifier) return;

        if (RailKeyboardGate.IsTextEntry(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
            return;
        if (Vm is not { } vm) return;

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? vm.RedoCommand : vm.UndoCommand;
        if (!command.CanExecute(null)) return;

        command.Execute(null);
        e.Handled = true;
    }, RoutingStrategies.Bubble);

    // ── The board view (brief 8) ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything the board panel needs that is not the view model’s: the overlay seam, the wider
    /// keyboard gate, and the four buttons.
    /// </summary>
    /// <remarks>
    /// <b>No navigation code, and that is the whole design</b> (§11.6). Every gesture is
    /// <c>LayoutCanvas</c>’s own — the wheel, the pan latch, <c>F</c>, <c>Z</c>, Ctrl/⌘ +/-, the arrow
    /// keys and Escape — so someone who has learned the layout editor has learned this window. What is
    /// added here is R-rail8-4’s WIDER gate, which is a predicate and not a gesture: railRF’s left
    /// column is editable rows, so no navigation key may fire while focus is in a text field.
    /// </remarks>
    private void WireBoardCanvas()
    {
        BoardCanvas.NavigationKeysSuppressed = RailKeyboardGate.For(this);

        // The magnifier’s lit state comes from the CANVAS, which owns the mode — including the disarm
        // it performs itself when the drag ends or Escape is pressed, neither of which this window can
        // see. LayoutEditorView’s own pattern, for its own reason.
        BoardCanvas.ZoomBoxArmedChanged += (_, _) =>
            BoardZoomBoxBtn.Classes.Set("ToolActive", BoardCanvas.ZoomBoxArmed);

        // A theme change is TWO different events and a view that paints its own colours needs both
        // (src/Ui/CLAUDE.md): this one is light-vs-dark, and ThemeService.ThemeChanged is a different
        // THEME being selected. Without the second, picking a new theme repaints the artwork and
        // leaves the map in the old colours — owner-reported twice, in different views.
        // Unsubscribe first on attach: ThemeService.ThemeChanged is a static, process-wide event and
        // a re-attach must not stack a second handler on it.
        // ── R-rail9-6: the two routes to one copy ────────────────────────────────────────────
        //
        // Ctrl/⌘+C is LayoutCanvas's OWN event — the same one the schematic, symbol and layout
        // canvases raise, reached by the same key on the same control — and the context row is the
        // overlay's, through ILayoutCanvasOverlay.BuildContextMenuItems, which is the seam's member
        // for exactly this because the canvas is shared and its ContextMenu is built once.
        //
        // The window performs the copy rather than the view model because a clipboard write needs an
        // ANCHOR control to reach the top level, and the window is the only thing here that is one.
        // Everything ABOUT the picture is RailGraphicExport's; this is dispatch.
        BoardCanvas.ClipboardCopyRequested += (_, _) => CopyBoard();

        // ── The margin rulers (owner, 2026-09-19) ───────────────────────────────────────────
        //
        // LayoutEditorView's own three calls, on the same control: it holds no state, so mirroring
        // the canvas IS the whole of driving it. The cursor line is part of it and not an extra —
        // the readout says what is under the pointer and the ruler says WHERE that is.
        BoardCanvas.ViewportChanged    += (_, _) => SyncBoardRulers();
        BoardCanvas.LayoutUpdated      += (_, _) => SyncBoardRulers();
        // The third call is the X:/Y: readout at the foot of the picture (owner, 2026-09-19) — the
        // layout editor has one and someone looking for a place on the board in this window was
        // reading the ruler ticks instead. It is the SAME view model property the layout editor
        // binds (`LayoutEditorViewModel.CursorXText`), so the two windows spell a coordinate
        // identically and in the board's own display unit, rather than this window growing a second
        // formatter that would drift from it.
        BoardCanvas.CursorWorldChanged += (_, world) =>
        {
            BoardHRuler.SetCursorWorld(world?.X);
            BoardVRuler.SetCursorWorld(world?.Y);
            Vm?.BoardLayout?.SetCursorWorld(world?.X, world?.Y);
        };

        ActualThemeVariantChanged += (_, _) => ApplyMapTheme();
        AttachedToVisualTree += (_, _) =>
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            ThemeService.ThemeChanged += OnThemeChanged;
            ApplyMapTheme();
        };
        DetachedFromVisualTree += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyMapTheme();

    private void ApplyMapTheme()
    {
        if (Vm is not { } vm) return;
        vm.BoardOverlayLayer.Theme = RailMapTheme.FromTheme(
            ThemeService.Active,
            ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light);
    }

    private RailLayoutOverlay? _boundOverlay;

    private void BindBoardOverlay(RailRfViewModel vm)
    {
        if (ReferenceEquals(_boundOverlay, vm.BoardOverlayLayer)) return;

        if (_boundOverlay is not null) _boundOverlay.OverlayChanged -= OnOverlayChanged;
        _boundOverlay = vm.BoardOverlayLayer;
        _boundOverlay.OverlayChanged += OnOverlayChanged;

        BoardCanvas.CanvasOverlay = _boundOverlay;
        ApplyMapTheme();
    }

    // ── The copy (brief-railrf-9-copy.md) ─────────────────────────────────────────────

    /// <summary>
    /// Copies the board as it is drawn, plus the document's own state (R-rail9-4).
    /// </summary>
    /// <remarks>
    /// <b>No guard, and that is R-rail9-5.</b> Not on a board-less window, not on an unsolved
    /// document, not on a rail with no result — <i>a copy that writes nothing to the system clipboard
    /// leaves the PREVIOUS copy sitting there, so the next paste produces something unrelated and
    /// nothing reports a failure.</i> The only thing checked here is that there is a view model to
    /// copy, because without one there is no document to put in the text flavour.
    ///
    /// <para><b>The scene is the overlay's own, not a freshly-built one.</b> What the user asked for
    /// is the picture they are looking at, and the overlay's scene is that picture — building a second
    /// one from the same inputs would be a second answer to a question already answered, which is how
    /// a copy comes to disagree with the window.</para>
    /// </remarks>
    private async void CopyBoard()
    {
        if (Vm is not { } vm) return;

        try
        {
            await RailGraphicExport.CopyToClipboardAsync(this, new RailGraphicExport.Request(
                Board:     vm.BoardLayout?.Model,
                Tech:      vm.Board?.Technology,
                Map:       vm.BoardOverlayLayer.Scene,
                Document:  vm.Document,
                // What the user is looking at includes the crosses over the parts that are not
                // fitted — they are document state, and a copy that dropped them would paste a
                // board claiming to carry parts this design says it does not.
                NotFitted: vm.NotFittedMarks));
        }
        catch (Exception ex)
        {
            // An async void handler that lets an exception escape takes the process down. A copy that
            // could not be written is worth one line in the log and nothing else — there is no state
            // to roll back and nothing the user can do differently.
            System.Diagnostics.Debug.WriteLine($"[railRF] Copy failed: {ex.Message}");
        }
    }

    /// <summary>
    /// railRF's OWN board menu: place a port where the click landed, and copy.
    /// </summary>
    /// <remarks>
    /// <b>It does not call <c>LayoutCanvas.BuildContextMenuItems</c>, and that was the defect.</b>
    /// This window hosts the layout editor's canvas so the board pans and zooms by exactly its
    /// gestures (§11.6) — but it is a READ-ONLY view of a <c>.clay</c> the layout editor owns, and
    /// the canvas's own menu is a list of EDITS to that document: Convert to Arc, Delete Vertex,
    /// Flatten Hierarchy, Group into Cell…, Clear All Rulers. Every one of them was on this menu
    /// (owner, 2026-09-19) and none of them belongs on a window that states in its own tooltip that
    /// the way to change the geometry is to open the layout and edit it there.
    ///
    /// <para><b>Two groups, one separator.</b> Above: what acts on the PLACE that was clicked — a
    /// source, a load, and on the class tab the copper-class rows, which are the overlay's own
    /// (<see cref="RailLayoutOverlay.BuildContextMenuItems"/>) because only the picture knows which
    /// region is under the pointer. Below: what goes to the clipboard.</para>
    ///
    /// <para><b>A pad under the pointer changes what the two place rows SAY and what they write.</b>
    /// On <c>U2.OUT</c> the rows read "Place Source at U2.OUT" and the anchor is that refdes and
    /// pin; on bare copper they read "Place Source here" and the anchor is the coordinate. That is
    /// not cosmetic: a refdes anchor resolves to every pad of a pin field and survives the artwork
    /// being re-imported at a different origin, and a coordinate does neither (§2.2,
    /// <c>PdnAttachments.Resolve</c>). The coordinate form is still always available — it is what
    /// Copy Coordinate puts on the clipboard, in exactly the spelling the anchor column parses.</para>
    ///
    /// <para>Rebuilt per opening rather than reused: re-subscribing a retained item's <c>Click</c>
    /// fires its action N times on the Nth opening, which is the mistake the single-instance rule
    /// exists to stop reintroducing.</para>
    /// </remarks>
    private void OnBoardContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (BoardCanvas.ConsumeContextMenuTarget() is not { } t) { e.Cancel = true; return; }
        if (Vm is not { } vm) { e.Cancel = true; return; }

        long x = (long)Math.Round(t.Wx), y = (long)Math.Round(t.Wy);
        long tol = BoardCanvas.ContextMenuHitTolDbu;

        var place = new List<object>();

        // A rail has to exist before a port can hang on it — with none, the two rows would make a
        // row on nothing. Disabled with the reason rather than hidden, which is the house rule for
        // a control whose absence would otherwise read as "this window cannot do that".
        var pad = vm.PadAt(x, y, tol);
        var anchor = pad is { } p
            ? new RailPortAnchor { Refdes = p.Refdes, Pin = p.Pin }
            : new RailPortAnchor { Point = (x, y) };
        string at = pad is { } q
            ? $" at {(q.Pin is { Length: > 0 } pin ? $"{q.Refdes}.{pin}" : q.Refdes)}"
            : " here";

        string? noRail = vm.SelectedRail is null
            ? "There is no rail to hang a port on yet. Pick the power net first."
            : null;

        MenuItem PlaceRow(string header, Action act)
        {
            var mi = new MenuItem { Header = header, IsEnabled = noRail is null };
            if (noRail is { } why) ToolTip.SetTip(mi, why);
            else mi.Click += (_, _) => act();
            return mi;
        }

        place.Add(PlaceRow("Place Source" + at, () => vm.PlaceSource(anchor)));
        place.Add(PlaceRow("Place Load" + at, () => vm.PlaceLoad(anchor)));

        // R-rail23-2b: Unmount / Mount, for the part under the click. THE ROW THE DESIGNER ASKED
        // FOR — they were looking at the layout when they asked how to take a part off the board, and
        // the answer was "go to the layout file and delete it". The .clay is not touched: this
        // says what is FITTED to the geometry, not what the geometry is (R-rail23-1c).
        place.AddRange(MountRowsFor(pad?.Refdes));

        // The class tab's three copper rows act on the region under the click, so they belong in
        // this group and not beside the clipboard rows.
        place.AddRange(vm.BoardOverlayLayer.BuildContextMenuItems(t.Wx, t.Wy, tol, null, BoardCanvas));

        var copy = new MenuItem { Header = "Copy" };
        copy.Click += (_, _) => CopyBoard();

        // In the board's own display unit and comma-separated, because that is what the anchor
        // column of a source or load row PARSES (RailAnchorEntry.Parse: a comma means a coordinate,
        // read through RailLengthFormat.ParsePoint). A coordinate copied in DBU would have to be
        // converted by hand before it could be pasted back into the window it came from.
        var copyCoord = new MenuItem { Header = "Copy Coordinate" };
        string coordinate = vm.BoardLengthFormat().Point(x, y);
        copyCoord.Click += async (_, _) =>
        {
            try
            {
                if (GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(coordinate);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[railRF] Copy Coordinate failed: {ex.Message}");
            }
        };

        var items = new List<object>(place.Count + 3);
        items.AddRange(place);
        items.Add(new Separator());
        items.Add(copy);
        items.Add(copyCoord);

        if (sender is ContextMenu menu) menu.ItemsSource = items;
    }

    /// <summary>
    /// A map repaint, and <b>nothing else</b>.
    /// </summary>
    /// <remarks>
    /// R-rail8-2: <c>InvalidateOverlay</c> repaints without disturbing <c>LayoutPathCache</c>, which
    /// on a real board is the difference between a repaint and a rebuild of half a million shapes.
    /// Anything that reached for <c>LayoutView</c> to make the map change would pay that cost on every
    /// frame of every solve.
    /// </remarks>
    private void OnOverlayChanged() => BoardCanvas.InvalidateOverlay();

    private void OnBoardZoomToFit(object? sender, RoutedEventArgs e)
    {
        BoardCanvas.ZoomToFit();
        BoardCanvas.Focus();
    }

    /// <summary>The magnifier ARMS and does not zoom — the box the next left-drag draws is what gets
    /// framed. Focus goes back to the canvas because the gesture, its Escape and its rubber band all
    /// live there.</summary>
    private void OnBoardZoomBoxTool(object? sender, RoutedEventArgs e)
    {
        if (BoardCanvas.ZoomBoxArmed) BoardCanvas.DisarmZoomBox(); else BoardCanvas.ArmZoomBox();
        BoardCanvas.Focus();
    }

    private void OnBoardZoomOut(object? sender, RoutedEventArgs e)
    {
        BoardCanvas.ZoomOut();
        BoardCanvas.Focus();
    }


    /// <summary>
    /// Opens the <c>.ctech</c> this board is priced against, in the workspace's own technology editor.
    /// </summary>
    /// <remarks>
    /// <b>The workspace's own command, not a second editor</b> — the same
    /// <c>OpenTechnologyDocument</c> the layout editor's "Edit…" row calls, so the file opens as the
    /// one document the application already has for it, with its own dirty state and its own save.
    /// railRF is an unowned window and the workspace is behind it, so the workspace window is brought
    /// forward too: opening a document in a window nobody can see is indistinguishable from nothing
    /// happening.
    ///
    /// <para>Nothing here writes the technology, and railRF does not have to be told when it changes —
    /// <c>AdoptLiveArtwork</c> takes the re-resolved instance on the next activation, which is the
    /// moment the user comes back from the edit.</para>
    /// </remarks>
    private void OnBoardEditTechnology(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // ── R-rail22-2a: NO SILENT RETURN FROM A VISIBLE CONTROL ──────────────────────────────
        //
        // This had two bare `return`s in it and the second is reachable in ordinary use — railRF is
        // an unowned window that outlives the workspace behind it, so WorkspaceLocator.Any() comes
        // back null the moment that workspace is closed and the button was then visible, enabled,
        // and did nothing at all. A control that is live and silent is indistinguishable from a
        // control that is broken, which is the rule CanPickSelectedNet already states. The
        // sentences are in RailTechnologyEdit, framework-free, so they are gated without a host.
        //
        // Set on Refusal rather than PendingImportRefusal deliberately: PendingImportRefusal gates
        // RUN, and a board whose technology cannot be opened for editing is still a board that
        // solves. This is PickSelectedNet's own idiom for a refusal raised by a press.
        string? why =
            vm.TechnologyPath is not { Length: > 0 } tech
                ? RailTechnologyEdit.NoTechnologyRefusal
            : RailTechnologyEdit.IsTemporary(tech)
                ? RailTechnologyEdit.TemporaryRefusal(tech)
            : WorkspaceLocator.Any() is null
                ? RailTechnologyEdit.NoWorkspaceRefusal(tech)
                : null;

        if (why is not null)
        {
            vm.Refusal = new RailRefusal(why, RailRefusalControl.None);
            return;
        }

        var workspace = WorkspaceLocator.Any()!;
        workspace.OpenTechnologyDocument(vm.TechnologyPath!);
        WorkspaceLocator.WindowFor(workspace)?.Activate();
    }

    /// <summary>The view model, or null before one is bound.</summary>
    private RailRfViewModel? Vm => DataContext as RailRfViewModel;

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RailRfViewModel.SelectedBoardOverlay)
                           or nameof(RailRfViewModel.SelectedResultsTab))
            SyncTabs();

        // The four panel lamps. The BORDERS gate themselves off the same properties in the AXAML;
        // what cannot be bound is the geometry a collapsed panel leaves behind — a fixed column is
        // still 300 px wide when the thing inside it is invisible.
        else if (e.PropertyName is nameof(RailRfViewModel.ShowSpecification)
                                or nameof(RailRfViewModel.ShowBoard)
                                or nameof(RailRfViewModel.ShowParts)
                                or nameof(RailRfViewModel.ShowResults))
            SyncPanes();

        // The readouts toggle changes the plot's CEILING, which is a code-behind number rather than
        // a binding — see CapResultsPlot. Nothing about the columns moves, so SyncPanes is not it.
        else if (e.PropertyName is nameof(RailRfViewModel.ShowResultText))
            CapResultsPlot();

        // A new board is a new LayoutEditorViewModel — and with it a new resolution and a new unit
        // for the rulers to label in.
        else if (e.PropertyName is nameof(RailRfViewModel.BoardLayout))
            BindBoardRulerUnits();

        // The pour pick armed or disarmed, and the pointer has to say so NOW — the press came from a
        // button beside the canvas, so with the pointer already over the copper there is no further
        // event to carry the change. LayoutCanvas.RefreshCursor's own note.
        else if (e.PropertyName is nameof(RailRfViewModel.IsPickingFromBoard))
            BoardCanvas.RefreshCursor();
    }

    // ── The four panel lamps (owner, 2026-09-19) ─────────────────────────────────────

    /// <summary>The widths and caps the AXAML declares, read once so they are stated once.</summary>
    /// <remarks>
    /// <b>These are also where a drag of the grippers is remembered.</b> A <c>GridSplitter</c> writes
    /// a concrete <see cref="GridLength"/> straight into the definition it resizes, so
    /// <see cref="SyncPanes"/> reads the live width back into these fields before it overwrites one —
    /// which is what makes a panel that is hidden and shown again come back the width the user left
    /// it, rather than the 300 and 340 below. It is wBond's own idiom (<c>ApplyArrangement</c>) and
    /// it needs no drag handler at all.
    /// </remarks>
    private GridLength _specificationColumn = new(300);
    private GridLength _resultsColumn       = new(340);

    /// <summary>
    /// How narrow a gripper may take each column.
    /// </summary>
    /// <remarks>
    /// <b>Applied in <see cref="SyncPanes"/> and not in the AXAML</b>, because a floor on a
    /// definition is a floor on EVERY value it is given — including the <c>GridLength(0)</c> that
    /// hides a panel, which a static <c>MinWidth</c> would silently turn back into 180 px. So the
    /// floor goes on with the panel and comes off with it.
    ///
    /// <para>Without one, a gripper dragged to the edge leaves a panel a few pixels wide and its own
    /// button cannot recover it — the width read back above is the few pixels, so toggling the panel
    /// off and on restores exactly the state that is unusable.</para>
    /// </remarks>
    private const double SpecificationMinWidth = 180;
    private const double ResultsMinWidth       = 240;

    /// <summary>
    /// The floor under the board column, which is the one that carries a table of FIXED columns.
    /// </summary>
    /// <remarks>
    /// <b>It is not the table's own width and cannot be.</b> The parts table wants 530 px before its
    /// part-number column takes any, and a pane spends 30 on margin, border and padding — so a floor
    /// that kept it whole would be 560, and 300 + 560 + 340 + 8 is 1208 against a window whose own
    /// <c>MinWidth</c> is 1080. Floors that cannot all be honoured are worse than none: the grid hands
    /// each column its minimum anyway and the surplus goes off the right edge of the window.
    ///
    /// <para>So this is the largest floor that still leaves slack at that 1080 — where the board
    /// column is ~432 — and the residue is a clip rather than a bleed: below about 560 the parts
    /// HEADER is cut at its card's edge, in the same place the rows have always been cut. See the
    /// card's own note in the AXAML.</para>
    /// </remarks>
    private const double BoardMinWidth = 360;
    private double _partsGridMaxHeight = double.PositiveInfinity;
    private double _partsListMaxHeight = double.PositiveInfinity;

    /// <summary>
    /// Gives the space a hidden panel released to the panels that are still on screen.
    /// </summary>
    /// <remarks>
    /// <b>The visibility is bound and only the GEOMETRY is here.</b> Each panel's
    /// <c>IsVisible</c> comes off the view model in the AXAML, but an invisible child does not
    /// shrink the column it sits in: <c>ColumnDefinitions[0]</c> is a fixed 300 whether anything is
    /// drawn in it or not, so hiding the specification panel without this would leave a 300 px hole
    /// where it had been — which is the whole of what the button was asked for.
    ///
    /// <para><b>The specification column is fixed-or-gone and never star</b> (see
    /// <c>RailRfViewModel.Panes.cs</c>). The results column is fixed BESIDE the board and star
    /// WITHOUT it, which is the one case where a panel grows sideways rather than just taller.</para>
    ///
    /// <para>The centre column's two rows are the same statement vertically: the board holds the
    /// star row and the parts table sits under it at its own capped height, and with the board gone
    /// the parts table takes the star row and both caps come off — a table pinned at 170 px in an
    /// otherwise empty column is not what "give parts the space" means.</para>
    ///
    /// <para><b>It also owns the two grippers</b>, and this is the only place that can: a panel's
    /// width is now either the AXAML's, the user's last drag or zero, and those three are decided
    /// together. The read-back at the top is what makes a dragged width survive its panel being
    /// hidden; the floors under it are what keep a drag from leaving a panel too narrow for its own
    /// button to recover.</para>
    /// </remarks>
    private void SyncPanes()
    {
        bool specification = Vm?.ShowSpecification ?? true;
        bool board         = Vm?.ShowBoard         ?? true;
        bool parts         = Vm?.ShowParts         ?? true;
        bool results       = Vm?.ShowResults       ?? true;
        bool centre        = board || parts;

        var specificationColumn = PaneGrid.ColumnDefinitions[0];
        var boardColumn         = PaneGrid.ColumnDefinitions[2];
        var resultsColumn       = PaneGrid.ColumnDefinitions[4];

        // Where the grippers were left, before anything below overwrites it. A star width is the
        // results column standing in for a hidden board and is nobody's drag, so it is not a width
        // to come back to.
        if (!specificationColumn.Width.IsStar && specificationColumn.Width.Value > 0)
            _specificationColumn = specificationColumn.Width;
        if (!resultsColumn.Width.IsStar && resultsColumn.Width.Value > 0)
            _resultsColumn = resultsColumn.Width;

        specificationColumn.Width = specification ? _specificationColumn : new GridLength(0);
        boardColumn.Width = centre ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        resultsColumn.Width =
            !results ? new GridLength(0)
            : centre ? _resultsColumn
                     : new GridLength(1, GridUnitType.Star);

        specificationColumn.MinWidth = specification ? SpecificationMinWidth : 0;
        boardColumn.MinWidth         = centre         ? BoardMinWidth        : 0;
        resultsColumn.MinWidth       = results        ? ResultsMinWidth      : 0;

        // A gripper is shown only where there are two panels for it to trade space between. With the
        // centre column collapsed the results column is star and takes what the specification panel
        // does not use, which is the same answer a drag would have given.
        SpecificationSplitter.IsVisible = specification && centre;
        ResultsSplitter.IsVisible       = centre && results;

        BoardPaneGrid.RowDefinitions[1].Height = board ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BoardPaneGrid.RowDefinitions[2].Height = board ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        PartsGrid.RowDefinitions[2].Height = board ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        PartsGrid.MaxHeight = board ? _partsGridMaxHeight : double.PositiveInfinity;
        PartsList.MaxHeight = board ? _partsListMaxHeight : double.PositiveInfinity;
    }

    /// <summary>
    /// A gripper was released — restate the one rule a drag can rewrite.
    /// </summary>
    /// <remarks>
    /// <b>The board column is this window's slack and must stay star.</b> A <c>GridSplitter</c>
    /// rewrites BOTH definitions it sits between, and a star one coming back as a pixel width would
    /// be invisible at the moment it happened and obvious later: the window would stop giving a
    /// resize to the board, and every extra pixel of a widened window would go to the gap instead.
    /// Restoring star here costs nothing when the drag already left it alone.
    ///
    /// <para>The layout is unchanged by this — the two outer columns are pinned at the widths they
    /// were just dragged to, which is what they already measured — so nothing jumps on release.
    /// <see cref="SyncPanes"/> reads those widths back the next time a panel is toggled.</para>
    /// </remarks>
    private void OnPaneSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var specification = PaneGrid.ColumnDefinitions[0];
        var board         = PaneGrid.ColumnDefinitions[2];
        var results       = PaneGrid.ColumnDefinitions[4];

        if (board.Width.IsStar) return;

        if (!specification.Width.IsStar) specification.Width = new GridLength(specification.ActualWidth);
        if (!results.Width.IsStar)       results.Width       = new GridLength(results.ActualWidth);
        board.Width = new GridLength(1, GridUnitType.Star);
    }

    /// <summary>The share of the results pane the plot may take. The rest is the cards.</summary>
    private const double ResultsPlotHeightShare = 0.45;

    /// <summary>
    /// The share it may take with the readouts hidden (owner, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>Not 1.0, and the remainder is not slack.</b> The pane's own bounds include its padding and
    /// the header row with the tab strip in it; a ceiling of the full height would let the panel
    /// measure taller than the space actually under that header and clip its own lower edge, which
    /// is the exact failure the cap was written for in the first place. What is left over is those
    /// two, measured on the shipping window.
    ///
    /// <para>It is still a CEILING: an <c>AspectRatioPanel</c> takes the height its WIDTH divides
    /// to, so on a narrow column the plot is smaller than this and nothing sits under it. That is
    /// the same letterboxing it does in any bounded row.</para>
    /// </remarks>
    private const double ResultsPlotHeightShareTextHidden = 0.88;

    /// <summary>
    /// Keeps the results plot from growing taller than the pane that has to show it AND the cards
    /// under it.
    /// </summary>
    /// <remarks>
    /// <b>An <c>AspectRatioPanel</c> in an <c>Auto</c> row is offered an infinite height</b>, which
    /// is exactly what that panel's own note says makes the height follow the width. That was right
    /// while this column was a fixed 340 px; with a panel toggle able to hand the column the whole
    /// window it became a 680 px plot in a 620 px pane, which took the drop, the breakdown and the
    /// via check off the bottom along with the plot's own lower half (owner, 2026-09-19).
    ///
    /// <para><b>A ceiling rather than a star row.</b> A star row would reserve the share whether the
    /// plot could use it or not, so a tall NARROW window — where the plot is already capped by its
    /// width — would sit under a band of empty space the cards used to have. A MaxHeight changes
    /// nothing in that case and binds only in the one that was broken; the panel letterboxes, as it
    /// does in any bounded row.</para>
    /// </remarks>
    private void CapResultsPlot()
    {
        double pane = ResultsPane.Bounds.Height;
        if (pane <= 0) return;

        // WITH THE READOUTS OFF THE CEILING LIFTS, which is the whole of what that toggle buys: the
        // cards' own row collapses to nothing and the plot is the only thing left to fill the pane.
        // Re-run on the toggle as well as on a resize — see OnVmPropertyChanged.
        double share = Vm?.ShowResultText == false
            ? ResultsPlotHeightShareTextHidden
            : ResultsPlotHeightShare;

        double cap = pane * share;
        if (Math.Abs(ImpedancePlotHost.MaxHeight - cap) > 0.5) ImpedancePlotHost.MaxHeight = cap;
    }

    // ── The margin rulers ────────────────────────────────────────────────────────────

    private void SyncBoardRulers()
    {
        BoardHRuler.SetViewport(BoardCanvas.CurrentPanX, BoardCanvas.CurrentPanY, BoardCanvas.CurrentZoom,
                                BoardCanvas.Bounds.Width, BoardCanvas.Bounds.Height);
        BoardVRuler.SetViewport(BoardCanvas.CurrentPanX, BoardCanvas.CurrentPanY, BoardCanvas.CurrentZoom,
                                BoardCanvas.Bounds.Width, BoardCanvas.Bounds.Height);
    }

    private LayoutEditorViewModel? _rulerUnitsFrom;

    /// <summary>
    /// Labels both rulers in the board's own unit, and keeps them there.
    /// </summary>
    /// <remarks>
    /// <b>Subscribed, not read once</b> — <c>LayoutEditorView</c>'s own shape, for a reason this
    /// window feels harder: the unit can change in the OTHER window, on the model both are bound to,
    /// and the view model this one holds is told about it by <c>RefreshIfUnitChanged</c>. A ruler
    /// labelled in millimetres beside rows labelled in micrometres is worse than either.
    /// </remarks>
    private void BindBoardRulerUnits()
    {
        if (_rulerUnitsFrom is not null)
            _rulerUnitsFrom.PropertyChanged -= OnBoardLayoutPropertyChanged;

        _rulerUnitsFrom = Vm?.BoardLayout;
        if (_rulerUnitsFrom is null) return;

        _rulerUnitsFrom.PropertyChanged += OnBoardLayoutPropertyChanged;
        ApplyBoardRulerUnits(_rulerUnitsFrom);
        SyncBoardRulers();
    }

    private void OnBoardLayoutPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is LayoutEditorViewModel vm && e.PropertyName is nameof(LayoutEditorViewModel.DisplayUnit))
            ApplyBoardRulerUnits(vm);
    }

    private void ApplyBoardRulerUnits(LayoutEditorViewModel vm)
    {
        BoardHRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
        BoardVRuler.SetUnits(vm.Model.DbuPerMicron, vm.DisplayUnit);
    }

    private void SetOverlay(RailBoardOverlay overlay)
    {
        if (Vm is { } vm) vm.SelectedBoardOverlay = overlay;
        SyncTabs();
    }

    private void SetResultsTab(RailResultsTab tab)
    {
        if (Vm is { } vm) vm.SelectedResultsTab = tab;
        SyncTabs();
    }

    /// <summary>
    /// Keeps the two strips showing what the view model says.
    /// </summary>
    /// <remarks>
    /// <b>Re-asserted rather than left to the control</b>: a <c>ToggleButton</c> toggles itself on
    /// click, so clicking the already-selected tab would otherwise turn the strip OFF and leave the
    /// board showing an overlay no tab claims. Setting all of them from the one property means the
    /// strip cannot get into a state the view model does not name — and it is why the four map
    /// buttons are ordinary <c>Button</c>s wearing <c>Button.ToolActive</c>'s lamp rather than
    /// toggles: there is no self-toggle to re-assert against, only a lamp to set.
    /// </remarks>
    private void SyncTabs()
    {
        var overlay = Vm?.SelectedBoardOverlay ?? RailBoardOverlay.Copper;
        CopperTab.Classes.Set("ToolActive",    overlay == RailBoardOverlay.Copper);
        DropTab.Classes.Set("ToolActive",      overlay == RailBoardOverlay.Drop);
        ImpedanceTab.Classes.Set("ToolActive", overlay == RailBoardOverlay.Impedance);
        ClassTab.Classes.Set("ToolActive",     overlay == RailBoardOverlay.Class);

        var tab = Vm?.SelectedResultsTab ?? RailResultsTab.Dc;
        DcTab.Classes.Set("ToolActive",        tab == RailResultsTab.Dc);
        FrequencyTab.Classes.Set("ToolActive", tab == RailResultsTab.Frequency);
    }

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Opens (or raises) the window for one <c>.crail</c>.</summary>
    /// <param name="notes">Everything the document's own references had to say that the caller should
    /// post — an artwork that has moved, a part library that did not read, a technology warning.
    /// Empty on the ordinary path, and empty for a document that names no artwork at all.</param>
    public static RailRfWindow Show(
        CircuitRF.Design.RailRf.RailDocument document, string path, Window? owner,
        out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(document);

        string key = Path.GetFullPath(path);
        if (Open.TryGetValue(key, out var existing))
        {
            existing.Activate();
            notes = [];
            return existing;
        }

        var vm = new RailRfViewModel(document, key);

        // WHAT THE DOCUMENT NAMES IS LOADED HERE, before the window is shown — the artwork, its
        // stackup and the part library. Opening used to construct the view model and stop, so a
        // document whose board was on disk came up saying "import one"; see
        // RailRfViewModel.Open.cs for the whole of it.
        notes = vm.LoadDocumentReferences();

        var window = new RailRfWindow { DataContext = vm };
        window.AdoptLiveArtwork();   // prefer the shared session's model where the .clay is open
        window._openKey = key;
        Open[key] = window;
        window.Closed += (_, _) =>
        {
            if (window._openKey is { } k) Open.Remove(k);
            vm.Dispose();
        };

        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>Opens a window bound to nothing — Tools ▸ railRF, before an import.</summary>
    /// <remarks>
    /// <b>Not deduplicated</b>, unlike <see cref="Show"/>. That method keeps one window per document
    /// because two views of one <c>.crail</c> would write it from two working copies; a standalone
    /// window writes no document at all until it is saved.
    /// </remarks>
    public static RailRfWindow ShowStandalone(Window? owner)
    {
        var vm = new RailRfViewModel();
        var window = new RailRfWindow { DataContext = vm };
        window.Closed += (_, _) =>
        {
            // It may have acquired one by being saved — see AdoptPath.
            if (window._openKey is { } k) Open.Remove(k);
            vm.Dispose();
        };

        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>The result a railRF window currently shows for that document, or null when none is
    /// open. What the Properties panel's summary reads — it runs nothing of its own.</summary>
    public static RailResultView? ResultFor(string path) =>
        ViewModelFor(path)?.Current;

    /// <summary>
    /// The live view model for one <c>.crail</c>, or null when no window has it open.
    /// </summary>
    /// <remarks>
    /// <b><see cref="ResultFor"/>'s own lookup, widened.</b> Two things outside this window need the
    /// SESSION rather than the file: the bill of materials a design was imported with, which no
    /// <c>.crail</c> carries a reference to and which therefore exists nowhere else
    /// (brief-authored-board-4 R-ab4-3a), and the working copy of the document itself, so that
    /// creating a part library for a design that is open on screen does not write the file behind
    /// the window that is editing it (R-ab4-4b).
    /// </remarks>
    public static RailRfViewModel? ViewModelFor(string path) =>
        Open.TryGetValue(Path.GetFullPath(path), out var window)
            ? window.DataContext as RailRfViewModel
            : null;

    /// <summary>
    /// Shows the window as an INDEPENDENT top-level, positioned over <paramref name="owner"/> but not
    /// owned by it.
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's own method, for its own reason.</b> An owned window is not merely
    /// non-modal — every platform keeps it above its owner for as long as it exists, so clicking the
    /// workspace raises the workspace UNDERNEATH and the window never goes behind. Dropping the owner
    /// costs placement and lifetime, so both are done explicitly: cascaded off the owner's top-left
    /// through <see cref="MatchWindowPlacement.Cascade"/> (set before <c>Show</c>, because a window
    /// positioned only once it is on screen is a visible jump, and re-asserted after, because whether
    /// a platform honours a Move on an unshown window is the platform's business), and closed with
    /// the owner.
    /// </remarks>
    private static void ShowUnowned(RailRfWindow window, Window? owner)
    {
        if (owner is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        void CloseWithOwner(object? _, EventArgs __) => window.Close();
        owner.Closed += CloseWithOwner;
        window.Closed += (_, _) => owner.Closed -= CloseWithOwner;

        var at = MatchWindowPlacement.Cascade(
            owner.Position,
            owner.RenderScaling,
            owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);

        window.Position = at;
        window.Show();
        window.Position = at;
    }
}
