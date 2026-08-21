using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Match;

/// <summary>
/// The Match Designer window (match.md §9). Non-modal, resizable, <b>one per instance</b>: invoking
/// it again on a component that already has one raises the existing window rather than opening a
/// second view of the same design, which would let two windows write the same <c>Design</c> parameter
/// from two different working copies.
/// </summary>
public partial class MatchDesignerWindow : Window, ICrfMenuWindow
{
    /// <summary>
    /// What this window is called in the application's <b>Window</b> menu (owner, 2026-08-20: "have it
    /// show up in the circuitRF Window menu, just like any other window").
    /// </summary>
    /// <remarks>
    /// The view-model's own <c>Title</c> — "Match — MN1" — so the menu entry, the title bar and the OS
    /// window title are one string. A Designer whose target has been deleted keeps its entry: the
    /// window is still open and still readable, which is the whole point of leaving it that way.
    /// </remarks>
    public string WindowMenuHeader => Vm?.Title ?? "Match Designer";

    // Keyed on the component, held weakly: a Designer must not be the reason a component the user
    // deleted stays alive, and closing the window removes the entry anyway.
    private static readonly ConditionalWeakTable<EditableComponent, MatchDesignerWindow> Open = new();

    /// <summary>Designer-only constructor; the AXAML designer needs a parameterless one.</summary>
    public MatchDesignerWindow()
    {
        InitializeComponent();

        // TUNNELLING, and on the window rather than on any one pane: a press that lands on a plot, a
        // slider, a specification field or the network canvas is handled by that control and never
        // bubbles anywhere a deselect could hang off. See OnWindowPointerPressed.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// <b>A press anywhere that is not a marker's info box clears the marker selection.</b>
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"clicking away from a marker needs to deselect it (even
    /// clicking in another panel or opening the inline text editor)."</i> Nothing ever deselected,
    /// because the Data Display does it from a control this window does not have: a press on the
    /// empty PLOT CANVAS background (<c>PlotCanvasView.OnCanvasPointerPressed</c> →
    /// <c>SelectOnly((PlotContainerViewModel?)null)</c>). The Designer lays its two plots out itself
    /// and has no canvas, so a marker selected by clicking its info box stayed selected for the rest
    /// of the session — visibly highlighted, and still the target of Delete and the arrow keys.
    ///
    /// <para>The rule is deliberately the WHOLE WINDOW rather than the response pane: the owner's
    /// "even clicking in another panel" is the point, and a selection that survives a trip to the
    /// specification pane is the same bug in a smaller area. Two things are excluded — a press inside
    /// a <c>MarkerInfoBoxView</c>, which IS the selecting gesture, and a Ctrl/Meta press, which is the
    /// additive one the info box implements. Both would otherwise undo themselves in the same
    /// event.</para>
    /// </remarks>
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;

        if (e.Source is Visual v
            && v.FindAncestorOfType<Views.DataDisplay.MarkerInfoBoxView>(includeSelf: true) is not null)
            return;

        Vm.PlotHost.SelectOnly((MarkerInfoBoxViewModel?)null);
    }

    /// <summary>The view-model, or null before one is bound.</summary>
    private MatchDesignerViewModel? Vm => DataContext as MatchDesignerViewModel;

    /// <summary>
    /// Opens (or raises) the Designer for one placed <c>Match</c>.
    /// </summary>
    public static MatchDesignerWindow Show(
        CircuitRF.Ui.ViewModels.SchematicViewModel schematicVm, EditableComponent comp, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(schematicVm);
        ArgumentNullException.ThrowIfNull(comp);

        if (Open.TryGetValue(comp, out var existing))
        {
            existing.Activate();
            return existing;
        }

        var vm = new MatchDesignerViewModel();
        vm.SetTarget(schematicVm, comp);

        var window = new MatchDesignerWindow { DataContext = vm };
        Open.Add(comp, window);
        window.Closed += (_, _) =>
        {
            Open.Remove(comp);
            vm.Dispose();
        };

        ShowUnowned(window, owner);
        return window;
    }

    /// <summary>
    /// Shows the Designer as an <b>independent</b> top-level window, positioned over
    /// <paramref name="owner"/> but not owned by it.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"the Match Designer window is always in front. I can't
    /// get back to the workspace with the designer window open."</i>
    ///
    /// <para>That is what <c>Show(owner)</c> means. An OWNED window is not merely non-modal — every
    /// platform keeps it above its owner in the z-order for as long as it exists, so clicking the
    /// workspace raises the workspace <i>underneath</i> the Designer and the Designer never goes
    /// behind. It is the right relationship for the <c>MatchFlattenDialog</c> this window opens (a
    /// prompt that belongs to one window and must not be lost behind it) and the wrong one for a
    /// Designer the user works alongside their schematic.</para>
    ///
    /// <para>Dropping the owner costs the two things the owner was providing, so both are done here
    /// instead. <b>Placement</b>: the Designer opens <b>cascaded off the workspace's top-left corner</b>
    /// (owner, 2026-08-20: "needs to open slightly down and to the right of the parent window that
    /// opened it") — computed before <c>Show</c>, off the DECLARED size, because a window that has not
    /// been shown yet reports no frame and positioning it afterwards is a visible jump, and clamped
    /// into the owner's screen so an owner dragged half off one does not put the Designer entirely off
    /// it. <b>Lifetime</b>: an owned window closes with its owner, so that is wired explicitly — a
    /// Designer outliving the workspace it edits would be a window with nothing behind it.</para>
    /// </remarks>
    private static void ShowUnowned(MatchDesignerWindow window, Window? owner)
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

        // Set BEFORE and re-asserted AFTER. Before, because a window positioned only once it is on
        // screen is a visible jump; after, because whether a platform honours a Move on a window it
        // has not shown yet is a platform's business, and the second assignment is a no-op when the
        // first one took. Manual startup location means Show() does not overwrite it either way.
        var at = CascadedFrom(owner);
        window.Position = at;
        window.Show();
        window.Position = at;
    }

    /// <summary>
    /// Where the Designer sits, cascaded off <paramref name="owner"/>'s top-left.
    /// </summary>
    /// <remarks>
    /// The arithmetic is <see cref="MatchWindowPlacement.Cascade"/>'s — pure, and asserted by test,
    /// because placement is otherwise only checkable by opening the application and looking. All this
    /// adds is reading the three things off a live window.
    /// </remarks>
    private static PixelPoint CascadedFrom(Window owner) =>
        MatchWindowPlacement.Cascade(
            owner.Position,
            owner.RenderScaling,
            owner.Screens?.ScreenFromWindow(owner)?.WorkingArea);

    // ── Field commits ─────────────────────────────────────────────────────────

    // The termination R/X fields, the band edges, the order and the ripple are all InlineEditText now
    // (owner, 2026-08-19). That control owns the whole three-key contract and commits through the
    // view-model's own *Entry properties, so the LostFocus handlers and the BindNumericBox plumbing
    // those fields used to need are gone rather than left wired to controls that no longer exist.

    private void OnTransformNLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchTransformRowViewModel r) r.CommitN();
    }

    // ── The slider gesture ────────────────────────────────────────────────────
    //
    // Begin/end around the pointer, not around each value change: the ladder and every element value
    // track the slider live, and only the response PLOTS are held for the gesture (brief §5).

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => Vm?.BeginTransformDrag();

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e) => Vm?.EndTransformDrag();

    // ── Buttons ───────────────────────────────────────────────────────────────

    // ── Flatten to Cell (match.md §11) ────────────────────────────────────────

    /// <summary>
    /// The footer's <c>Flatten to Cell…</c>. Only the VIEW knows which window owns this panel and
    /// how to show a dialog; the decision, the writing and the undo entry all live in
    /// <see cref="MatchDesignerViewModel.Flatten"/>, which the schematic's context menu calls too.
    /// </summary>
    private async void OnFlattenToCell(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        var availability = Vm.FlattenAvailability;
        if (!availability.CanRun || availability.ParentDir is null) return;

        var dialog = new Dialogs.MatchFlattenDialog(
            Vm.InstanceName, availability.DefaultName, availability.ParentDir);
        var choice = await dialog.ShowDialog<Dialogs.MatchFlattenChoice?>(this);
        if (choice is null) return;

        Vm.Flatten(choice.ParentDir, choice.CellName, choice.ReplaceInPlace);
    }

    private void OnApplySolution(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is MatchSolutionRowViewModel row) row.Apply();
    }

    private void OnSortGrid(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string column) Vm?.SortElements(column);
    }

    /// <inheritdoc/>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is null) return;

        // There is no CloseButton to wire (owner, 2026-08-20: "remove the close button from the top
        // of window") — the title bar's own is the one that closes this window.
        WireButton("ApplyButton", (_, _) => Vm.Apply());
        WireButton("RevertButton", (_, _) => Vm.Revert());
        WireElementsContextMenu();
        WireSchematicContextMenu();
        WireSchematicInlineEditor();
        WirePlotHost();
        WireButton("RemoveTransformButton", (_, _) => Vm.RemoveLastTransform());
        WireButton("AddTransformButton", (s, _) => ShowAddTransformMenu(s as Control));
        WireButton("ExportButton", (s, _) => ShowExportMenu(s as Control));
        WireButton("SettingsButton", (s, _) => ShowSettingsMenu(s as Control));
        // Same deep link the Parameter Editor's own Help button uses, to the same Reference page.
        // The Match CHAPTER, not the components page's one-paragraph entry (brief-user-docs-content
        // §10 gave Match a chapter of its own, and this window is what it documents). Listed in
        // DocAnchors.WholePages, so the docs build fails if that page stops being emitted.
        WireButton("HelpButton", (_, _) => DocLauncher.Open("reference/match.html"));

        BindNumericBox(
            "BandFractionBox",
            () => (Vm.PlotBandFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
            t => Vm.CommitPlotWindow(MatchDesignerViewModel.ParseBandPercent(t), null));
        BindNumericBox(
            "PlotPointsBox",
            () => Vm.PlotPoints.ToString(CultureInfo.InvariantCulture),
            t => Vm.CommitPlotWindow(null, MatchDesignerViewModel.ParsePlotPoints(t)));
    }

    private void WireButton(string name, EventHandler<RoutedEventArgs> handler)
    {
        if (this.FindControl<Button>(name) is { } b) b.Click += handler;
    }

    /// <summary>
    /// <b>Copy as CSV</b>, on the network listing's own context menu (owner, 2026-08-20: "remove the
    /// Copy as CSV button — instead make a context menu for the entire grid view with a Copy as CSV
    /// menu that performs the same operation").
    /// </summary>
    /// <remarks>
    /// Built here rather than declared in the AXAML for the reason every other menu in this window is:
    /// a <c>ContextMenu</c> is a popup with its own visual root, so a <c>MenuItem</c> declared inside
    /// one is not reliably reachable by <c>FindControl</c> from the window, and a handler that
    /// silently never attaches is a menu entry that does nothing. The listing itself IS in the name
    /// scope, so the menu is attached to it here and the item is wired in the same breath.
    /// </remarks>
    private void WireElementsContextMenu()
    {
        if (this.FindControl<ItemsControl>("ElementsList") is not { } list) return;

        var csv = new MenuItem { Header = "Copy as CSV" };
        csv.Click += async (_, _) =>
        {
            var clipboard = Clipboard;
            if (Vm is not null && clipboard is not null) await clipboard.SetTextAsync(Vm.ElementsCsv);
        };

        // "Copy" ABOVE "Copy as CSV" (owner, 2026-08-20). The two views are two views of ONE network,
        // so the grid offers the same picture the schematic does — the schematic's own item, built by
        // the same method, not a second implementation of it.
        list.ContextMenu = new ContextMenu { ItemsSource = new[] { CopySchematicMenuItem(), csv } };
    }

    /// <summary>
    /// <b>Copy</b> — the network as a real schematic selection, on the clipboard in every format
    /// <c>SchematicClipboard</c> writes: circuitRF JSON (so it pastes into a schematic page), SVG and
    /// PDF, a PNG, and on Windows CF_ENHMETAFILE, which is the vector form PowerPoint pastes.
    /// </summary>
    /// <remarks>
    /// The projection is <see cref="MatchSchematicCopy"/>'s and the clipboard write is the schematic
    /// editor's own — this method is only the menu entry and the two things a VIEW knows: which
    /// clipboard, and (on Windows) which window handle owns it.
    /// </remarks>
    private MenuItem CopySchematicMenuItem()
    {
        var item = new MenuItem { Header = "Copy" };
        item.Click += async (_, _) => await CopySchematicAsync();
        return item;
    }

    private async Task CopySchematicAsync()
    {
        var clipboard = Clipboard;
        if (Vm is null || clipboard is null) return;

        var model = MatchSchematicCopy.Build(Vm.Ladder);
        if (model.Components.Count == 0) return;   // a refused design has no drawing to copy

        IntPtr ownerHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        await SchematicClipboard.CopyAsync(
            clipboard, model.Components, model.Wires, model.CanvasObjects, model.GridSize,
            netLabels: null, schematicDirectory: null, ownerHwnd: ownerHwnd);
    }

    /// <summary>
    /// The network pane's own one-item context menu.
    /// </summary>
    /// <remarks>
    /// Attached here rather than declared in the AXAML for the reason every other menu in this window
    /// is: a <c>ContextMenu</c> is a popup with its own visual root, so a <c>MenuItem</c> declared
    /// inside one is not reliably reachable by <c>FindControl</c>, and a handler that silently never
    /// attaches is a menu entry that does nothing.
    /// </remarks>
    private void WireSchematicContextMenu()
    {
        if (this.FindControl<MatchSchematicCanvas>("NetworkSchematic") is not { } canvas) return;
        canvas.ContextMenu = new ContextMenu { ItemsSource = new[] { CopySchematicMenuItem() } };
    }

    // ── The network pane's inline value editor ────────────────────────────────

    private SchematicInlineEditBox? _labelEditor;
    private MatchInlineEditTarget?  _labelEditTarget;
    private (string Id, int Row)?   _labelEditAnchor;

    /// <summary>
    /// Puts <b>the schematic editor's own inline text box</b> over the network pane and connects it to
    /// the canvas's label hit-test.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"Allow user to double click on the TermG to give the schematic
    /// editor's inline text editor to allow user to change the R for the termination… Similarly, allow
    /// user to use inline text editor to change any value in the schematic… Can you reuse the exact
    /// same inline text editor from the regular schematic? That is the preferred solution."</i>
    ///
    /// <para>It IS the same control — <see cref="SchematicInlineEditBox"/>, which
    /// <c>SchematicView</c> now hosts too — carrying the same padding, the same font size rule, the
    /// same baseline arithmetic and the same three-key contract (Return commits, LostFocus commits,
    /// Escape reverts). Only the two ends differ, and they have to: WHAT was hit is the canvas's
    /// answer, and what a typed value MEANS is the view-model's. On a schematic page a value is
    /// written to a parameter; here it is a target the Norton transforms are searched for.</para>
    ///
    /// <para>The box is created here rather than declared in the AXAML so that the control's own
    /// constructor stays the single definition of how one looks. It is added as a SIBLING of the
    /// canvas — <c>MatchSchematicCanvas</c> renders through a Skia draw operation and has no visual
    /// children — inside a <c>Panel</c> that shares the canvas's coordinate space exactly, which is
    /// what lets the hit's screen numbers be used unchanged.</para>
    /// </remarks>
    private void WireSchematicInlineEditor()
    {
        if (this.FindControl<Panel>("NetworkSchematicHost") is not { } host) return;
        if (this.FindControl<MatchSchematicCanvas>("NetworkSchematic") is not { } canvas) return;
        if (_labelEditor is not null) return;

        _labelEditor = new SchematicInlineEditBox();
        host.Children.Add(_labelEditor);

        canvas.LabelDoubleTapped += OnSchematicLabelDoubleTapped;

        // A pan or a zoom moves the label the box is sitting on. Following it is what the schematic
        // page does (SchematicView.OnViewportChanged); the alternative — leaving the box behind over
        // a different component — is worse than dismissing it.
        canvas.ViewportChanged += (_, _) => RepositionLabelEditor();

        _labelEditor.KeyDown += OnLabelEditorKeyDown;
        // Deferred, so a click that moves focus INSIDE the box (its own context menu, a drag-select)
        // is not read as leaving it — the same guard SchematicView's MaybeDismissInlineEdit makes.
        _labelEditor.LostFocus += (_, _) =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_labelEditor is { IsVisible: true } b && !b.IsKeyboardFocusWithin)
                        CommitLabelEdit();
                },
                DispatcherPriority.Background);
    }

    private void OnSchematicLabelDoubleTapped(object? sender, MatchSchematicLabelHit hit)
    {
        if (Vm is null || _labelEditor is null) return;

        // An editor already open is COMMITTED, not abandoned. The deferred LostFocus check below
        // cannot do it: focus is posted at Input priority and the check runs at Background, so by the
        // time it looks the new box already has the focus and the first edit would be dropped in
        // silence — which is the one outcome the three-key contract exists to prevent.
        if (_labelEditor.IsVisible) CommitLabelEdit();

        // WHICH row was hit is not the question — WHICH COMPONENT is. Only the value row is editable,
        // and at this pane's zoom the type and name rows above it are two thirds of a 16-pixel label
        // block with nothing to do; a double-click anywhere on the component (its glyph included)
        // therefore lands on its value. The view-model decides what that value is, and says which row
        // to open the editor on so the user can see what they are editing.
        if (Vm.ResolveInlineEdit(hit.ComponentId) is not { } target)
        {
            DismissLabelEditor();
            return;
        }

        // Re-anchored onto the VALUE row, which is not necessarily the row under the pointer.
        var at = (this.FindControl<MatchSchematicCanvas>("NetworkSchematic")?
                      .AnchorFor(hit.ComponentId, target.Row)) ?? hit;

        _labelEditTarget = target;
        _labelEditAnchor = (hit.ComponentId, target.Row);
        _labelEditor.Open(at.Text, at.ScreenX, at.ScreenY, at.FontSize);

        Dispatcher.UIThread.Post(
            () =>
            {
                _labelEditor?.Focus();
                _labelEditor?.SelectValueOnly();
            },
            DispatcherPriority.Input);
    }

    private void RepositionLabelEditor()
    {
        if (_labelEditor is not { IsVisible: true }) return;
        if (_labelEditAnchor is not { } anchor) return;
        if (this.FindControl<MatchSchematicCanvas>("NetworkSchematic") is not { } canvas) return;

        if (canvas.AnchorFor(anchor.Id, anchor.Row) is not { } at)
        {
            DismissLabelEditor();      // the element it was about is no longer in the drawing
            return;
        }
        _labelEditor.MoveTo(at.ScreenX, at.ScreenY, at.FontSize);
    }

    private void OnLabelEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            CommitLabelEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DismissLabelEditor();
            e.Handled = true;
        }
    }

    private void CommitLabelEdit()
    {
        if (Vm is null || _labelEditor is null) return;
        string text = _labelEditor.Text ?? "";
        var target = _labelEditTarget;
        // Dismiss FIRST, so the LostFocus this triggers sees a hidden box and returns — the same
        // belt-and-braces order SchematicView.CommitAndDismissInlineEdit uses.
        DismissLabelEditor();
        if (target is not null) Vm.CommitInlineEdit(target, text);
    }

    private void DismissLabelEditor()
    {
        _labelEditTarget = null;
        _labelEditAnchor = null;
        if (_labelEditor is not null) _labelEditor.IsVisible = false;
    }

    // ── The response plots' Data Display host ─────────────────────────────────

    /// <summary>
    /// Connects the two <c>PlotControl</c>s to the view-model's <c>PlotHost</c> — the wiring
    /// <c>PlotContainerView</c> does for a plot on the Data Display canvas, done here because these
    /// two live in a fixed pane instead.
    /// </summary>
    /// <remarks>
    /// <b>Every provider below is a question a PlotControl asks its host, and a null host answers all
    /// of them with silence.</b> That was the shape of four owner-reported 2026-08-20 bugs at once: no
    /// marker info box appeared (nobody created one on <c>MarkerAdded</c>), <c>Copy</c> did nothing
    /// (<c>PlotExporter.CopyPlotToClipboardAsync</c> returns immediately on a null container), marker
    /// selection never highlighted, and arrow keys did not step a marker.
    /// </remarks>
    private void WirePlotHost()
    {
        if (Vm is null) return;

        // Cached, because SyncPlotContainers runs on every layout pass — a name-scope lookup per
        // frame for two controls that cannot change is the kind of cost that only shows up later.
        _magnitudePlot   = this.FindControl<PlotControl>("MagnitudePlotControl");
        _phasePlot       = this.FindControl<PlotControl>("PhasePlotControl");
        _markerInfoLayer = this.FindControl<ItemsControl>("MarkerInfoBoxLayer");

        Bind(_magnitudePlot, Vm.MagnitudeContainer);
        Bind(_phasePlot,     Vm.PhaseContainer);

        SyncPlotTheme();
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();

        // The containers' logical rectangles must equal the PlotControls' real ones, because that is
        // the coordinate space a marker info box is placed and dragged in (see the overlay's own note
        // in the AXAML). LayoutUpdated rather than SizeChanged: the pane's ScrollViewer can move a
        // plot without resizing it, and a box that stayed behind would be pointing at nothing.
        LayoutUpdated += (_, _) => SyncPlotContainers();
        SyncPlotContainers();

        void Bind(PlotControl? plot, PlotContainerViewModel container)
        {
            if (plot is null) return;

            plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
            plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
            plot.ContainerProvider           = () => container;
            plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
            plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;

            // A double-click near a trace adds a marker there, and one on empty plot area opens the
            // Plot Properties flyout. Both live in PlotControl.HandleDoubleTapAt, which is documented
            // as "called by the HOST on DoubleTapped" — and this window is the host. Nobody called it
            // (owner-reported, 2026-08-20: "double-clicking on a plot trace does not create a marker
            // at the spot it was clicked; this already works in a true Data Display plot"), which is
            // the same class of omission as the four null providers below.
            plot.DoubleTapped += (_, args) =>
            {
                plot.HandleDoubleTapAt(args.GetPosition(plot));
                args.Handled = true;
            };

            plot.PlotChanged += container.OnPlotChanged;
            plot.MarkerMoved += (_, _) => container.OnMarkerMoved();
            plot.MarkerAdded += container.OnMarkerAdded;
            container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();

            // DeletePlotRequested is deliberately NOT handled — the menu item is disabled in the
            // AXAML (CanDeletePlot="False"), so it can never fire, and an unhandled event is the
            // honest reading of "this host does not delete plots".
        }
    }

    private PlotControl?  _magnitudePlot;
    private PlotControl?  _phasePlot;
    private ItemsControl? _markerInfoLayer;

    private void SyncPlotTheme()
    {
        if (Vm is null) return;
        Vm.PlotHost.Theme = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;
        _magnitudePlot?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
        _phasePlot?.SetValue(PlotControl.PlotThemeProperty, Vm.PlotHost.Theme);
    }

    private void SyncPlotContainers()
    {
        if (Vm is null || _markerInfoLayer is not { } layer) return;
        Place(_magnitudePlot, Vm.MagnitudeContainer);
        Place(_phasePlot,     Vm.PhaseContainer);

        void Place(PlotControl? plot, PlotContainerViewModel container)
        {
            if (plot is null || plot.Bounds.Width < 1 || plot.Bounds.Height < 1) return;
            if (plot.TranslatePoint(default, layer) is not { } origin) return;

            if (Math.Abs(container.Left - origin.X) < 0.5
                && Math.Abs(container.Top - origin.Y) < 0.5
                && Math.Abs(container.Width - plot.Bounds.Width) < 0.5
                && Math.Abs(container.Height - plot.Bounds.Height) < 0.5)
                return;   // LayoutUpdated fires constantly; only a real move is worth notifying

            container.Left   = origin.X;
            container.Top    = origin.Y;
            container.Width  = plot.Bounds.Width;
            container.Height = plot.Bounds.Height;
            container.NotifyViewProperties();
        }
    }

    /// <summary>
    /// A small numeric box that commits on <b>Return as well as on focus loss</b>.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"committing using return key on band and points textedit
    /// boxes does not update the frequency response plots."</i> They had only a <c>LostFocus</c>
    /// handler, so a user who typed a number and pressed Return got no movement and no reason to
    /// believe the value had been rejected. Return is the third key of this application's inline-edit
    /// contract everywhere else; these two boxes were simply missing it.
    /// </remarks>
    private void BindNumericBox(string name, Func<string> read, Action<string> write)
    {
        if (this.FindControl<TextBox>(name) is not { } box) return;
        box.Text = read();
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Return or Key.Enter)) return;
            Commit();
            e.Handled = true;
        };

        void Commit()
        {
            write(box.Text ?? "");
            box.Text = read();
        }
    }

    // ── Menus ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>+ add</c> menu: every currently transformable pair, <b>by element name</b> (§9.4).
    /// Recomputed each time it opens, against the ladder as it stands.
    /// </summary>
    private void ShowAddTransformMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;
        var pairs = Vm.AvailablePairs();

        var menu = new ContextMenu();
        if (pairs.Count == 0)
        {
            menu.ItemsSource = new[]
            {
                new MenuItem { Header = "No transformable pair in this ladder", IsEnabled = false },
            };
        }
        else
        {
            var items = new List<MenuItem>();
            foreach (var pair in pairs)
            {
                var item = new MenuItem { Header = pair.Display };
                var captured = pair;
                item.Click += (_, _) => Vm.AddTransform(captured);
                items.Add(item);
            }
            menu.ItemsSource = items;
        }
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    private void ShowExportMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;

        var touchstone = new MenuItem { Header = "Touchstone (.s2p) of the design response" };
        touchstone.Click += async (_, _) => await SaveAsync("s2p", "Touchstone", w => Vm.ExportTouchstone(w));

        var listing = new MenuItem { Header = "Component listing (.csv)" };
        listing.Click += async (_, _) => await SaveAsync("csv", "CSV", w => w.Write(Vm.ComponentListingCsv()));

        var gvalues = new MenuItem { Header = "Prototype g-values (.csv)" };
        gvalues.Click += async (_, _) => await SaveAsync("csv", "CSV", w => w.Write(Vm.PrototypeGValuesCsv()));

        var menu = new ContextMenu { ItemsSource = new[] { touchstone, listing, gvalues } };
        menu.PlacementTarget = anchor;
        menu.Open(anchor);
    }

    /// <summary>
    /// Settings (§9.9): display units per dimension, significant digits, Qmin, and whether Q-adjusted
    /// solutions are offered. <b>Nothing else</b> — and in particular no standard-value series.
    /// </summary>
    private void ShowSettingsMenu(Control? anchor)
    {
        if (Vm is null || anchor is null) return;
        var s = Vm.Settings;

        var items = new List<MenuItem>
        {
            UnitMenu("Inductance", MatchDesignerSettings.InductanceUnitOptions,
                     () => s.InductanceUnit, v => s.InductanceUnit = v),
            UnitMenu("Capacitance", MatchDesignerSettings.CapacitanceUnitOptions,
                     () => s.CapacitanceUnit, v => s.CapacitanceUnit = v),
            UnitMenu("Resistance", MatchDesignerSettings.ResistanceUnitOptions,
                     () => s.ResistanceUnit, v => s.ResistanceUnit = v),
            DigitsMenu(),
            QMinMenu(),
        };

        var offer = new MenuItem
        {
            Header = "Offer Q-adjusted solutions",
            Icon = s.OfferQAdjustedSolutions ? new TextBlock { Text = "✓" } : null,
        };
        offer.Click += (_, _) => s.OfferQAdjustedSolutions = !s.OfferQAdjustedSolutions;
        items.Add(offer);

        var menu = new ContextMenu { ItemsSource = items };
        menu.PlacementTarget = anchor;
        menu.Open(anchor);

        MenuItem UnitMenu(string header, IReadOnlyList<string> options, Func<string> read, Action<string> write)
        {
            var parent = new MenuItem { Header = header };
            var children = new List<MenuItem>();
            foreach (string option in options)
            {
                var item = new MenuItem
                {
                    Header = option,
                    Icon = read() == option ? new TextBlock { Text = "✓" } : null,
                };
                string captured = option;
                item.Click += (_, _) => write(captured);
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }

        MenuItem DigitsMenu()
        {
            var parent = new MenuItem { Header = "Significant digits" };
            var children = new List<MenuItem>();
            for (int d = 3; d <= 9; d++)
            {
                int captured = d;
                var item = new MenuItem
                {
                    Header = d.ToString(CultureInfo.InvariantCulture),
                    Icon = s.SignificantDigits == d ? new TextBlock { Text = "✓" } : null,
                };
                item.Click += (_, _) => s.SignificantDigits = captured;
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }

        MenuItem QMinMenu()
        {
            var parent = new MenuItem { Header = "Q-adjust floor (Qmin)" };
            var children = new List<MenuItem>();
            foreach (double q in new[] { 1.0, 1.5, 2.0, 3.0, 5.0 })
            {
                double captured = q;
                var item = new MenuItem
                {
                    Header = q.ToString("0.#", CultureInfo.InvariantCulture),
                    Icon = Math.Abs(s.QMin - q) < 1e-9 ? new TextBlock { Text = "✓" } : null,
                };
                item.Click += (_, _) => s.QMin = captured;
                children.Add(item);
            }
            parent.ItemsSource = children;
            return parent;
        }
    }

    private async Task SaveAsync(string extension, string label, Action<TextWriter> write)
    {
        var sp = StorageProvider;
        if (sp is null || Vm is null) return;

        // The suggested name carries NO extension (owner-reported, 2026-08-20: "Export Touchstone
        // file picker shows .s2p twice in its suggested file name"). Avalonia's storage provider
        // appends DefaultExtension to SuggestedFileName itself when the name has none, so supplying
        // both spelled the extension twice — "MN1.s2p.s2p". Dropping DefaultExtension instead would
        // be the wrong half to drop: it is what makes a name the user types WITHOUT an extension come
        // back with one.
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export {label}",
            SuggestedFileName = Vm.InstanceName,
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(label) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            write(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Vm.ResponseError = $"Export failed: {ex.Message}";
        }
    }
}
