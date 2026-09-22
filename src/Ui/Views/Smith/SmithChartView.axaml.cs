using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.Controls;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Smith;

/// <summary>
/// The Smith Chart document's view — the generator panel, the chrome and the status strip
/// (brief-smith-4-document-window.md; <c>docs/design/smith-chart.md</c> §5.2, §5.3).
/// </summary>
/// <remarks>
/// <b>This is a <c>UserControl</c> and there is deliberately no <c>Window</c> subclass in this
/// folder.</b> <c>R-smith4-1</c> makes the Smith Chart a docked document rather than an application:
/// the tab, the dirty mark, Save / Save All / close-time prompting, Ctrl/Cmd+Z, tear-off, the Window
/// Layout and restore-on-reopen are all the shell's, inherited and not re-implemented. A
/// <c>Window</c> appearing here would mean that requirement had been missed.
///
/// <para>What is left for the code-behind is what a view model must not do: take keyboard focus when
/// the tab is activated, open a file picker, and hand the chart's <c>PlotControl</c> its host, its
/// container and its overlay (<c>R-smith5-5</c>, <c>BindChartPlot</c>). The import's READ is
/// <see cref="SmithGeneratorImport"/>'s, below the firewall, reached through
/// <see cref="SmithChartViewModel.ImportGeneratorFrom"/> — which takes a resolved path, so the whole
/// import is drivable by the gate with no display.</para>
/// </remarks>
public partial class SmithChartView : UserControl
{
    private SmithChartDocument? _doc;

    public SmithChartView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // A theme change is a different event from a theme VARIANT change, and this view paints its
        // chart from the variant — see SyncPlotTheme.
        ActualThemeVariantChanged += (_, _) => SyncPlotTheme();

        // LayoutUpdated rather than SizeChanged: the chart pane can MOVE without resizing — a
        // splitter drag on the generator column does exactly that — and a marker info box that
        // stayed behind would be pointing at nothing.
        LayoutUpdated += (_, _) => SyncPlotContainer();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_doc is not null)
        {
            _doc.ActivationFocusRequested -= OnActivationFocusRequested;
            _doc.CopyRequested            -= OnEditCopy;
            _doc.PasteRequested           -= OnEditPaste;
        }

        _doc = DataContext as SmithChartDocument;
        if (_doc is null) return;

        _doc.ActivationFocusRequested += OnActivationFocusRequested;
        _doc.CopyRequested            += OnEditCopy;
        _doc.PasteRequested           += OnEditPaste;
        ApplySplitFractions();
        BindChartPlot(_doc.ViewModel);
        BuildElementMenus(_doc.ViewModel);
        BindClipboard(_doc.ViewModel);
        RefreshPasteState();

        // The picker is the view's; the refusal that follows a cancelled one is the view model's.
        // THE BUTTON FOLLOWS THE CANVAS, not the other way round: Z on the canvas and Esc both
        // change the armed state without going through the button, and a toolbar that only lit up
        // when it was clicked would be wrong exactly when the keyboard was used. The layout
        // editor's own wiring.
        NetworkCanvas.ZoomBoxArmedChanged -= OnZoomBoxArmedChanged;
        NetworkCanvas.ZoomBoxArmedChanged += OnZoomBoxArmedChanged;
        OnZoomBoxArmedChanged(null, EventArgs.Empty);

        _doc.ViewModel.TouchstoneFileChooser = PickTouchstoneFile;
        _doc.ViewModel.OverlayFileChooser    = PickOverlayFile;

        // The request may have been made BEFORE this view existed — a document that is already the
        // active dockable at the instant its view is first realized. ConsumeActivationFocus is what
        // closes that race; see IActivatableDocument.
        if (_doc.ConsumeActivationFocus()) FocusSelf();
    }

    private void OnActivationFocusRequested() => FocusSelf();

    private void OnZoomBoxArmedChanged(object? sender, EventArgs e)
        => ZoomBoxToolBtn.Classes.Set("ToolActive", NetworkCanvas.ZoomBoxArmed);

    // ── Is there anything to paste? (owner instruction, 2026-09-22) ──────────

    private Window? _attachedWindow;

    /// <summary>
    /// Re-reads the clipboard when the host window is activated, which is what keeps the network
    /// strip's <b>Paste</b> button greyed or live.
    /// </summary>
    /// <remarks>
    /// <b>Activation is the whole trigger, and that is deliberate.</b> The contents of a platform
    /// clipboard can only change behind this application's back while another one has the focus, so
    /// coming back to this window is exactly the moment — and it costs one cross-process read at a
    /// moment the user is not drawing. A timer, or a probe per property read, would pay that cost
    /// over and over for an answer that cannot have changed.
    ///
    /// <para><c>SymbolEditorView</c>'s own wiring, for the same reason it has it: a torn-off document
    /// is in a DIFFERENT window, so the window is found on attach rather than assumed to be the
    /// shell's. Both halves are needed — a subscription left on a window this view has left is a leak
    /// that also fires into a dead document.</para>
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_attachedWindow is not null) _attachedWindow.Activated += OnHostWindowActivated;

        // The first read: the document may be opened into a window that is already active, and no
        // activation follows that.
        RefreshPasteState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_attachedWindow is not null)
        {
            _attachedWindow.Activated -= OnHostWindowActivated;
            _attachedWindow = null;
        }
    }

    private void OnHostWindowActivated(object? sender, EventArgs e) => RefreshPasteState();

    /// <summary>
    /// Fires the view model's clipboard probe. Fire-and-forget on purpose: the answer is a button's
    /// enabled state, and there is nothing to do with the task.
    /// </summary>
    private void RefreshPasteState()
    {
        if (_doc?.ViewModel is { } vm) _ = vm.RefreshPasteStateAsync();
    }

    /// <summary>
    /// <b>Escape disarms the zoom box wherever the focus is</b> (owner report, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// The network canvas already handled Escape for itself, and that only ever worked while the
    /// canvas held the keyboard. Arming the box is a TOOLBAR click, and the very next thing a user
    /// does with a tool they did not mean to arm is look away from it — click the chart, a generator
    /// cell, a slider — and by then Escape reached the document's own <c>KeyBinding</c> instead,
    /// which clears the selections and says nothing about the tool. So the box stayed armed, the
    /// crosshair stayed on, and the next left-drag on the strip zoomed instead of selecting.
    ///
    /// <para><b>Before <c>base.OnKeyDown</c>, deliberately.</b> <c>InputElement.OnKeyDown</c> is what
    /// runs this control's <c>KeyBindings</c>, so anything that must pre-empt the Escape binding has
    /// to be said above that call. Marking it handled is the other half: disarming the tool AND
    /// dropping both selections on one press would make Escape do two things at once, and the tool
    /// is the more recent gesture — the strip's own Escape already reads it that way.</para>
    ///
    /// <para>A canvas that had the keyboard has already handled the key by the time it bubbles here,
    /// so this never runs twice; <c>DisarmZoomBox</c> is idempotent either way.</para>
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape && NetworkCanvas.ZoomBoxArmed)
        {
            NetworkCanvas.DisarmZoomBox();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>The Smith Chart chapter, through the launcher every other Help button in the
    /// application uses.</summary>
    private void OnHelp(object? sender, RoutedEventArgs e)
        => DocLauncher.Open("reference/smith-chart.html");

    /// <summary>
    /// The preferred-value list editor (§5.6a) — the discrete-values button's context menu.
    /// </summary>
    /// <remarks>
    /// <b>Modeless, and it re-snaps the open design when it applies.</b> A list edit that left the
    /// document alone would leave the toggle claiming every value is on the ladder while the ladder
    /// had just been replaced under it; <c>ReapplyPreferredValues</c> is that re-snap, and it is one
    /// undo entry with the toggle on and nothing at all with it off.
    /// </remarks>
    private void OnEditPreferredValues(object? sender, RoutedEventArgs e)
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not Window owner) return;

        var dialog = new Dialogs.SmithPreferredValuesDialog
        {
            DataContext = new SmithPreferredValuesViewModel(_doc.ViewModel.ReapplyPreferredValues),
        };
        dialog.Show(owner);
    }

    /// <summary>
    /// Takes the keyboard for this document.
    /// </summary>
    /// <remarks>
    /// The control focused is this one rather than the chart, because the network pane is brief 6's
    /// and will want the focus for its own shortcuts — and because the chart takes the keyboard on
    /// its own first click either way. Focusing the document root is what makes the shell's own
    /// accelerators work on a freshly-activated tab without a preliminary click, which is the whole
    /// point of <c>IActivatableDocument</c>.
    /// </remarks>
    private void FocusSelf() => Focus(NavigationMethod.Tab);

    // ── the two splitters (R-smith4-3) ───────────────────────────────────────

    /// <summary>
    /// Puts the document's stored split fractions onto the grid definitions.
    /// </summary>
    /// <remarks>
    /// <b>In code, not as a binding, and the reason is a silent failure rather than a preference.</b>
    /// A <c>RowDefinition</c> is not in the logical tree: it inherits no DataContext, so a binding on
    /// its <c>Height</c> resolves against nothing and simply never applies — the splitter would move
    /// and the document would remember nothing, with no error anywhere. Nothing else in this
    /// application binds a definition's size, and this window is not the place to find out why.
    /// </remarks>
    private void ApplySplitFractions()
    {
        if (_doc is null) return;

        double side = _doc.ViewModel.SplitterSide;
        double main = _doc.ViewModel.SplitterMain;

        TopRegion.ColumnDefinitions[0].Width = new GridLength(side,       GridUnitType.Star);
        TopRegion.ColumnDefinitions[2].Width = new GridLength(1.0 - side, GridUnitType.Star);
        RootGrid.RowDefinitions[0].Height    = new GridLength(main,       GridUnitType.Star);
        RootGrid.RowDefinitions[2].Height    = new GridLength(1.0 - main, GridUnitType.Star);
    }

    /// <summary>
    /// A splitter was released — record where it was left.
    /// </summary>
    /// <remarks>
    /// <b>On release, not on every frame of the drag.</b> One handler for both splitters, because
    /// the honest answer to "where are the dividers" is read off the definitions rather than
    /// accumulated from deltas — a <c>GridSplitter</c> rewrites BOTH definitions it sits between, and
    /// a star value is only meaningful against its neighbour.
    ///
    /// <para>This writes into the document's <c>View</c> block and pushes NO undo entry and no dirty
    /// mark; the position rides along on the next real save. See
    /// <see cref="SmithChartViewModel.SplitterSide"/>.</para>
    /// </remarks>
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_doc is null) return;

        _doc.ViewModel.SplitterSide = Fraction(TopRegion.ColumnDefinitions[0].Width,
                                               TopRegion.ColumnDefinitions[2].Width);
        _doc.ViewModel.SplitterMain = Fraction(RootGrid.RowDefinitions[0].Height,
                                               RootGrid.RowDefinitions[2].Height);

        static double Fraction(GridLength first, GridLength second)
        {
            double a = first.IsStar  ? first.Value  : 0.0;
            double b = second.IsStar ? second.Value : 0.0;
            return a + b > 0 ? a / (a + b) : 0.5;
        }
    }

    /// <summary>
    /// Import <c>.s1p</c>… — the picker, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A one-port file only</b>, which is the filter's own statement of what the generator's
    /// impedance comes from; an <c>.s2p</c> handed to this dialog is far more likely to be the wrong
    /// file than a deliberate request for its input reflection, and
    /// <see cref="SmithGeneratorImport.Read"/> refuses it by name rather than reading S₁₁ out of the
    /// corner of it.
    ///
    /// <para>The refusal lands in the status strip, with the file's name in it. That is the house
    /// rule (§5.3) and it is also the only place a background tab could report one.</para>
    /// </remarks>
    private async void OnImportS1pClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import generator impedance",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("One-port Touchstone") { Patterns = ["*.s1p", "*.S1P"] },
                new FilePickerFileType("All files")           { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        string? error = _doc.ViewModel.ImportGeneratorFrom(files[0].Path.LocalPath);
        _doc.ViewModel.ImportFailed = error;
    }

    // ── Save / Save As (owner instruction, 2026-09-19) ───────────────────────

    /// <summary>
    /// The chart strip's <b>Save</b> and <b>Save As</b>.
    /// </summary>
    /// <remarks>
    /// <b>Both go through <see cref="SmithChartDocumentSave"/>, which is the ONE implementation of
    /// "a <c>.csmith</c> is written like this"</b> — the shell's own tab menu calls the same static,
    /// and so does the gate, with no display. A second route from the view would be a second answer
    /// to what a save does, and the one that drifts is always the one nobody is looking at.
    ///
    /// <para><b>The workspace is OPTIONAL and is looked up rather than required.</b> A scratch
    /// <c>.csmith</c> opens with no workspace at all (<c>R-smith4-1</c>); what a workspace supplies
    /// is the project-tree node a newly-saved file appears in and a starting folder for the picker,
    /// so a null one costs the save nothing.</para>
    ///
    /// <para><b>A refusal goes to the Messages panel, not to a dialog</b> — the document's own rule:
    /// <see cref="SmithDesign.Refusal"/>'s sentence is about the design, and it is raised BEFORE
    /// anything reaches the disk. With no workspace to report into there is nowhere for it to go and
    /// the strip's own notice carries it instead.</para>
    /// </remarks>
    private void OnSave(object? sender, RoutedEventArgs e)   => _ = SaveAsync(saveAs: false);

    /// <inheritdoc cref="OnSave"/>
    private void OnSaveAs(object? sender, RoutedEventArgs e) => _ = SaveAsync(saveAs: true);

    private async Task SaveAsync(bool saveAs)
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not { } top) return;

        var workspace = WorkspaceLocator.For(this);
        if (await SmithChartDocumentSave.RunAsync(_doc, top, workspace, saveAs) is not { } error)
            return;

        if (workspace is not null) workspace.Messages.Error(error);
        else                       _doc.ViewModel.StripNotice = error;
    }

    // ── the chart (brief-smith-5-chart.md R-smith5-5) ────────────────────────

    private SmithChartViewModel? _boundChartVm;

    /// <summary>
    /// Gives the chart's <c>PlotControl</c> its host, its overlay and its container.
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's own <c>Bind</c>, and the same omission it was written for — twice.</b>
    /// A <c>PlotControl</c> asks its HOST for the next marker index, the info-box view model, the
    /// container and the selected markers, and a host that is null answers "nothing" to all four —
    /// silently. That was reported against the Match Designer on 2026-08-20 and against railRF on
    /// 2026-09-19, both times as "a double click adds no marker", both times the same cause.
    ///
    /// <para><b><c>ContainerProvider</c> is the one that bites LAST.</b>
    /// <c>PlotExporter.CopyPlotToClipboardAsync</c> opens with <c>if (container is null) return;</c>,
    /// so a plot hosted without a container produces NO clipboard content, raises nothing, and looks
    /// exactly like a successful copy. Brief 7 writes the copy; the WIRING belongs with the hosting
    /// and is here, and brief 7's gate is written to fail if it is missing.</para>
    ///
    /// <para><c>HandleDoubleTapAt</c> is documented as "called by the HOST on DoubleTapped" — it is
    /// not wired by the control — so the subscription below is the whole of that feature: a
    /// double-click near a trace adds a marker there, one on empty chart opens Plot Properties.</para>
    ///
    /// <para>Bound once per view model. The container is the one <see cref="SmithChartViewModel"/>
    /// built, never a second one: two containers over one plot would number markers
    /// independently.</para>
    /// </remarks>
    private void BindChartPlot(SmithChartViewModel vm)
    {
        if (ReferenceEquals(_boundChartVm, vm)) return;
        _boundChartVm = vm;

        var plot      = ChartPlotControl;
        var container = vm.ChartContainer;

        plot.NextMarkerIndexProvider     = container.GetNextMarkerIndex;
        plot.FindMarkerInfoBoxVmProvider = container.FindMarkerInfoBoxVm;
        plot.ContainerProvider           = () => container;
        plot.SelectedMarkersProvider     = container.GetSelectedMarkers;
        plot.StepSelectedMarkersHandler  = container.StepSelectedMarkers;

        // THE OVERLAY — the grippers (R-smith5-6). Everything about the gesture is the overlay's;
        // the control only asks it first and falls through when it answers null, which is what keeps
        // pan, zoom and marker drag exactly as they were.
        //
        // BOTH HALVES, and the second is brief 7's. The control's copy reaches the on-screen frame;
        // the CONTAINER's reaches the export path, which composes from containers and would
        // otherwise drop the arrowheads, the load-frequency labels and the grippers out of every
        // copied picture — silently, with the picture still produced and still looking correct.
        plot.Overlay = vm.ChartOverlay;
        container.Overlay = vm.ChartOverlay;

        plot.DoubleTapped += (_, args) =>
        {
            plot.HandleDoubleTapAt(args.GetPosition(plot));
            args.Handled = true;
        };

        // A pan or a zoom is what makes the chart's window the USER's; from then on the document
        // carries it and the chart stops re-fitting under every edit. It is not an edit: no undo
        // entry, no dirty mark — the splitters' own rule.
        plot.PlotChanged += (_, _) =>
        {
            vm.CaptureChartWindow();
            container.OnPlotChanged(this, EventArgs.Empty);

            // A REMOVAL has no event of its own — PlotControl's marker menu takes the marker off its
            // trace and raises PlotChanged — so the harvest hangs off all three and decides for
            // itself what is an undo entry. It is cheap and it no-ops when nothing changed, which is
            // what makes it safe on a pan.
            vm.HarvestMarkers();
        };
        // A REMOVAL from the marker context menu: two of that menu's three paths go through the
        // CONTAINER's view model and never raise PlotChanged at all, so the harvest above missed
        // them and the deleted marker was re-attached from the document on the next rebuild — the
        // owner's report of 2026-09-19, a marker that came back whenever a component value changed.
        plot.MarkerRemoved += (_, _) => vm.HarvestMarkers();

        plot.MarkerMoved += (_, _) => { container.OnMarkerMoved(); vm.HarvestMarkers(); };
        plot.MarkerAdded += (marker, trace) => { container.OnMarkerAdded(marker, trace); vm.HarvestMarkers(); };
        container.PlotNeedsRedraw += (_, _) => plot.InvalidateVisual();

        SyncPlotTheme();
        SyncPlotContainer();
    }

    /// <summary>
    /// Keeps the chart container's rectangle equal to the <c>PlotControl</c>'s, in the info-box
    /// layer's coordinates, and reports the canvas size to the view model.
    /// </summary>
    /// <remarks>
    /// <b>Two things, because both are the same measurement.</b> The rectangle is what
    /// <c>DataDisplayViewModel.PlaceInfoBoxInLogicalCoords</c> puts a new marker's box against —
    /// without it every box lands at the top left of the document rather than beside its marker. The
    /// canvas size is what the adaptive trajectory sampler measures its chord error in, so a curve is
    /// as smooth as the size it is actually drawn at deserves.
    ///
    /// <para><c>LayoutUpdated</c> fires on every pass, so a rectangle that has not moved is returned
    /// on rather than re-published: <c>NotifyViewProperties</c> walks every info box.</para>
    /// </remarks>
    private void SyncPlotContainer()
    {
        if (_boundChartVm is not { } vm) return;

        var plot  = ChartPlotControl;
        var layer = MarkerInfoBoxLayer;
        if (plot.Bounds.Width < 1 || plot.Bounds.Height < 1) return;
        if (plot.TranslatePoint(default, layer) is not { } origin) return;

        vm.ChartCanvasSize = (plot.Bounds.Width, plot.Bounds.Height);

        PlaceLabelStrips(vm, plot.Bounds.Width, plot.Bounds.Height);

        var container = vm.ChartContainer;
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

    /// <summary>
    /// Puts the two Y-axis label strips against the chart disc's own edges, spanning its height.
    /// </summary>
    /// <remarks>
    /// <b>The disc, not the canvas.</b> A Smith chart's plotting area is square and centred in
    /// whatever rectangle it is given (<c>PlotRenderer.ComputeViewport</c>), and this window's chart
    /// region is a wide one — so the canvas's left edge can be a long empty band away from the chart,
    /// and a label put there reads as a stray piece of text rather than as an axis name. That is the
    /// owner's report of 2026-09-19, seen in a copied picture because on screen there was no strip at
    /// all. <c>PlotCanvasGeometry.ChartRect</c> is where the rectangle comes from — the same
    /// viewport the grid is drawn from, asked rather than re-derived — and
    /// <c>PlotComposer</c> now places the exported strips from the same two edges.
    ///
    /// <para><b>Unconditional, and above <see cref="SyncPlotContainer"/>'s own early return.</b> That
    /// return fires whenever the container's RECTANGLE is unchanged, which is exactly what happens
    /// when a trace is added: the chart is the same size and the strip set is not. Placing below it
    /// would leave a newly-added label at the last position, or at none.</para>
    ///
    /// <para>A left strip whose column is wider than the band available is clamped to the canvas edge
    /// rather than pushed off it — it then overlaps the disc a little, which is legible, where half a
    /// label outside the control is not.</para>
    /// </remarks>
    private void PlaceLabelStrips(SmithChartViewModel vm, double canvasW, double canvasH)
    {
        var container = vm.ChartContainer;
        var chart     = PlotCanvasGeometry.ChartRect(vm.ChartPlot, canvasW, canvasH);

        double stripW = container.LabelStripViewWidth;

        LeftStripHost.Height  = chart.Height;
        LeftStripHost.Margin  = new Thickness(
            Math.Max(0.0, chart.Left - container.LeftLabelStrips.Count * stripW), chart.Top, 0, 0);

        RightStripHost.Height = chart.Height;
        RightStripHost.Margin = new Thickness(chart.Left + chart.Width, chart.Top, 0, 0);
    }

    /// <summary>
    /// Puts the chart, its markers and their info boxes into the application's own light or dark
    /// palette.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, because they reach different things</b> (railRF's own finding).
    /// <c>DataDisplayViewModel.Theme</c> repaints every marker info box;
    /// <c>PlotControl.PlotTheme</c> is what the control draws the grid, the arcs, the labels, the
    /// markers and the OVERLAY with. Setting only the first leaves the chart in the light palette and
    /// setting only the second leaves the info boxes in it.
    ///
    /// <para><b>No palette of its own</b> (§6 of the brief). harmonicaRF has a phosphor-green theme
    /// because it is a standalone instrument; this is a document, and a document that ignores the
    /// user's theme is a document that looks broken in dark mode.</para>
    /// </remarks>
    private void SyncPlotTheme()
    {
        if (_boundChartVm is not { } vm) return;

        vm.PlotHost.Theme = ActualThemeVariant == ThemeVariant.Dark
            ? RenderTheme.Dark
            : RenderTheme.Light;

        ChartPlotControl.SetValue(PlotControl.PlotThemeProperty, vm.PlotHost.Theme);
    }

    // ── The network strip (brief-smith-6-network-strip.md) ───────────────────

    /// <summary>
    /// Builds the Add and Insert menus from <see cref="SmithChartViewModel.ElementMenuGroups"/>.
    /// </summary>
    /// <remarks>
    /// <b>In code and not in XAML, and from ONE list</b> (<c>R-smith6-2</c>: "one command, two
    /// surfaces"). The vocabulary is <c>SmithComponentMap.AllKinds</c>'s, expanded by
    /// <c>SmithComponentMap.AllowedPlacement</c> into the placements each kind is legal in — so a kind
    /// added to the map appears in both menus with nothing to keep in step, and the shell's own Insert
    /// menu can be filled from the same list when brief 4's <c>R-smith4-9</c> surface is built.
    ///
    /// <para><c>WorkspaceViewModel.BuildExampleMenuItems</c> is the precedent for filling a menu from
    /// code; what is different here is that the list is a CONSTANT, so the flyouts are built once per
    /// view rather than rebuilt per open.</para>
    ///
    /// <para><b>The one SUBMENU is the view model's shape, not this file's</b> (owner, 2026-09-20).
    /// A group carrying a <c>Title</c> becomes a <c>MenuItem</c> with children and a group without
    /// one becomes the row it always was — so the decision about what is grouped lives beside the
    /// vocabulary, where a second surface building these menus reads the same answer.</para>
    /// </remarks>
    private void BuildElementMenus(SmithChartViewModel vm)
    {
        AddElementButton.Flyout = Menu(vm.AddElementCommand);

        // INSERT HAS NO BUTTON HERE ANY MORE (owner instruction, 2026-09-22) — it is redundant with
        // the drag that moves an element along the strip. The COMMAND and the one list both stay, so
        // the shell's own Insert menu is still `Menu(vm.InsertElementCommand)` and nothing has to be
        // rebuilt to put it back.

        static MenuFlyout Menu(System.Windows.Input.ICommand command)
        {
            var flyout = new MenuFlyout();
            foreach (var group in SmithChartViewModel.ElementMenuGroups)
                flyout.Items.Add(Row(group, command));
            return flyout;
        }

        static MenuItem Row(SmithElementMenuGroup group, System.Windows.Input.ICommand command)
        {
            if (group.Title is null)
                return new MenuItem
                {
                    Header           = ElementRow(group.Entries[0]),
                    Command          = command,
                    CommandParameter = group.Entries[0],
                };

            // A SUBMENU carries no command of its own — a parent MenuItem that both opens children
            // and invokes something is a control whose click does two things, and one of them by
            // accident. Its glyph is the family's archetype, so the row still reads as a picture in
            // the same column as every other row rather than as a bare word among drawings.
            var parent = new MenuItem
            {
                Header = LabelledGlyph(group.GlyphKind, SmithPlacement.Series, group.Title),
            };

            foreach (var entry in group.Entries)
                parent.Items.Add(new MenuItem
                {
                    Header           = ElementRow(entry),
                    Command          = command,
                    CommandParameter = entry,
                });

            return parent;
        }
    }

    /// <summary>
    /// One row of the Add / Insert menus: <b>the part's picture and its name, as the row's own
    /// Header</b>.
    /// </summary>
    /// <remarks>
    /// <b>The glyph is not the <c>MenuItem</c>'s <c>Icon</c>, and that is the whole point of this
    /// method</b> (owner instruction, 2026-09-19: "almost twice as large"). Fluent's
    /// <c>MenuItem</c> template hosts <c>Icon</c> inside a fixed-size presenter
    /// (<c>PART_IconPresenter</c>, a <c>Viewbox</c> sized from the theme rather than from the
    /// content), so a size asked for above the icon column's own is scaled back down to fit it and a
    /// request for a bigger glyph can produce no visible change at all — silently. Putting the glyph
    /// in the HEADER takes it out of that presenter entirely, so the size below is the size drawn
    /// whatever the theme does with icons.
    ///
    /// <para>Every row carries one, so nothing is lost by giving up the icon column's alignment: the
    /// glyphs are a fixed width and line up as a column of their own.</para>
    /// </remarks>
    private static Control ElementRow(SmithElementMenuEntry entry)
        => LabelledGlyph(entry.Kind, entry.Placement, entry.Header);

    /// <summary>A glyph and a caption in one row — what both an element row and the RLC submenu's
    /// own parent row are, so the two line up in the same column.</summary>
    private static Control LabelledGlyph(SmithElementKind kind, SmithPlacement placement, string text)
    {
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing     = 10,
        };
        row.Children.Add(ElementGlyph(kind, placement));
        row.Children.Add(new TextBlock
        {
            Text              = text,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>
    /// A menu row's picture: <b>the part's own symbol, turned the way the strip will draw it</b>
    /// (owner instruction, 2026-09-19) — horizontal for a series element, vertical for a shunt one.
    /// </summary>
    /// <remarks>
    /// <b>The palette's glyph control, not a second drawing of the same parts.</b> It renders through
    /// <c>SchematicRenderer.DrawSymbol</c>, which is what the network strip below and the schematic
    /// editor both draw with, so a menu row cannot come to disagree with the thing it places. The
    /// rotation is <see cref="SmithNetworkModel.RotationFor"/>'s — the one rule, asked without an
    /// element — rather than a second table that would have to be kept in step; the two-terminal
    /// lumped glyphs are drawn upright natively and the rest horizontally, which is why "series" is
    /// not simply "R0".
    /// </remarks>
    private static Control ElementGlyph(SmithElementKind kind, SmithPlacement placement)
    {
        var binding = SmithComponentMap.Component(kind);
        return new PaletteGlyphControl
        {
            Kind      = binding.SymbolKind,
            PortCount = binding.NumPorts,
            Rotation  = SmithNetworkModel.RotationFor(kind, placement),
            // THREE QUARTERS OF 64x52 (owner instruction, 2026-09-19). A menu row's job here is
            // to be recognised at a glance — an R from an L from a shunt C — which is why 22x18 was
            // abandoned; 64x52 overshot, so the glyph is scaled back by 0.75 rather than back to the
            // 33x27 that could not be told apart. ElementRow is what makes a number above the icon
            // column's own size take effect at all.
            Width     = 48,
            Height    = 39,
            // Centred against the name beside it rather than stretched to the row's height.
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// The file picker an <c>S1P</c>/<c>S2P</c> opens on placement (<c>R-smith6-3</c>).
    /// </summary>
    /// <remarks>
    /// <b>The picker is the view's and the decision is not.</b> The view model refuses to create a
    /// file-less file element — a null answer here places nothing — which is what lets the whole
    /// placement path be driven by the gate with no display.
    ///
    /// <para>The filter names the element's OWN port count, because an <c>.s2p</c> dropped on an S1P is
    /// far more likely to be the wrong file than a request for the corner of its matrix — the same rule
    /// the generator's own import follows, and the same one <c>SmithCascade</c> refuses by.</para>
    /// </remarks>
    private async Task<string?> PickTouchstoneFile(SmithElementKind kind)
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;

        string ext = kind == SmithElementKind.S1P ? "s1p" : "s2p";

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = $"Choose the {ext.ToUpperInvariant()} file for this element",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType($"{ext[1]}-port Touchstone")
                    { Patterns = [$"*.{ext}", $"*.{ext.ToUpperInvariant()}"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return null;

        // Relative to the document where that is possible — the `.cdd` convention, and the one that
        // survives an archived or moved workspace. An absolute path is kept when the file lives
        // somewhere a relative reference could not reach.
        string full = files[0].Path.LocalPath;
        string? dir = _doc?.ViewModel.DocumentDirectory;
        if (dir is not { Length: > 0 }) return full;

        string relative = Path.GetRelativePath(dir, full);
        return relative.StartsWith("..", StringComparison.Ordinal) ? full : relative;
    }

    // ── the overlays (brief-smith-12-overlays-via-the-inspector.md R-smith12-3) ──

    /// <summary>
    /// The file picker the chart strip's data-source combo opens for <b>Add from file…</b>.
    /// </summary>
    /// <remarks>
    /// <b>The picker brief 8's <c>Add overlay</c> used, wired to the data-source library</b> — so a
    /// Touchstone can still be loaded and drawn with no workspace open, which is a scratch
    /// `.csmith`'s whole case.
    ///
    /// <para><b>It returns the ABSOLUTE path</b>, because that is what
    /// <c>DataSourceLibraryViewModel.LoadFileAsync</c> takes. The document-relative reference — brief
    /// 8's convention, and the one that survives an archived or moved workspace — is computed on the
    /// way out, where the trace's config is written (<c>SmithChartViewModel.ToStoredOverlay</c>).
    /// Handing a relative path to the library instead would resolve it against the process working
    /// directory, which is not the document's folder and very rarely anything.</para>
    ///
    /// <para><b>Any port count</b>, unlike the element pickers. An overlay is reference material and
    /// its quantity is chosen on the trace card afterwards, so an <c>.s2p</c> here is an ordinary
    /// thing to want — a two-port part whose S₁₁ is what the match is being checked against.</para>
    /// </remarks>
    private async Task<string?> PickOverlayFile()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Add an overlay",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Touchstone")
                    { Patterns = ["*.s1p", "*.s2p", "*.s3p", "*.s4p", "*.snp",
                                  "*.S1P", "*.S2P", "*.S3P", "*.S4P", "*.SNP"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    /// <summary>Zoom to Fit on the strip — the button, and the F key the canvas handles itself.</summary>
    private void OnNetworkZoomToFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => NetworkCanvas.ZoomToFit();

    /// <summary>
    /// The network strip's <b>Zoom Box</b> — arms the canvas for one marquee.
    /// </summary>
    /// <remarks>
    /// <b>It ARMS; it does not zoom</b> (the layout editor's own wording, and its own behaviour):
    /// the click takes the left button for one drag, and the box that drag draws is what gets
    /// framed. Pressing it again disarms, which is what makes a mis-click recoverable without
    /// having to draw a box somewhere harmless.
    /// </remarks>
    private void OnNetworkZoomBox(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (NetworkCanvas.ZoomBoxArmed) NetworkCanvas.DisarmZoomBox();
        else                            NetworkCanvas.ArmZoomBox();

        // The canvas takes the keyboard so Esc and Z reach it without a preliminary click on the
        // drawing — which would itself be a press the armed box would swallow.
        NetworkCanvas.Focus();
    }

    /// <summary>
    /// A slider row's parameter label was clicked — make it the element's active parameter.
    /// </summary>
    /// <remarks>
    /// A <c>Click</c> handler rather than a command binding, because reaching the view model from
    /// inside a <c>DataTemplate</c> whose <c>DataContext</c> is the ROW needs a cast through an
    /// ancestor's <c>DataContext</c> — three ways to spell it and two of them fail silently at runtime
    /// with the button simply doing nothing.
    /// </remarks>
    private void OnParameterLabelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_doc is null) return;
        if ((sender as Button)?.DataContext is not SmithSliderRowViewModel row) return;

        _doc.ViewModel.MakeParameterActiveCommand.Execute(row);
    }

    /// <summary>
    /// Wires one slider's press and release so the whole drag is <b>one undo entry</b>
    /// (<c>R-smith6-4</c>).
    /// </summary>
    /// <remarks>
    /// <b>The Match Designer's own <c>OnSliderLoaded</c>, and it exists for the defect that window
    /// shipped</b>: a two-way-bound slider writes its value on every step, so an edit pushed from the
    /// setter is one undo entry per pixel of travel — and its coercing write-back reached the same
    /// setter during <c>Undo</c>, which made every undo ADD an entry and cost the user their redo
    /// stack. The setter here mutates and redraws and pushes nothing; the entry is pushed on release,
    /// carrying the state captured on press.
    ///
    /// <para>Tunnel AND bubble with <c>handledEventsToo</c>, because a <c>Slider</c>'s own thumb marks
    /// the press handled before it reaches the control. A <c>ConditionalWeakTable</c> guards against
    /// double-wiring a slider Avalonia re-raises <c>Loaded</c> on after a virtualization pass.</para>
    ///
    /// <para>A capture lost without a release — the window losing focus mid-drag — must still END the
    /// gesture, or the next edit joins a drag that started minutes ago.</para>
    /// </remarks>
    private void OnNetworkSliderLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (_wiredSliders.TryGetValue(slider, out _)) return;
        _wiredSliders.Add(slider, this);

        const RoutingStrategies both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        slider.AddHandler(PointerPressedEvent,     OnSliderPressed,     both, handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent,    OnSliderReleased,    both, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, OnSliderCaptureLost, both, handledEventsToo: true);
    }

    private readonly ConditionalWeakTable<Slider, SmithChartView> _wiredSliders = new();

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e)
        => _doc?.ViewModel.BeginSliderDrag();

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e)
        => _doc?.ViewModel.EndSliderDrag();

    private void OnSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _doc?.ViewModel.EndSliderDrag();

    // ── The clipboard (brief-smith-7-clipboard.md) ───────────────────────────

    /// <summary>
    /// Gives the view model its three clipboard seams, and puts <b>Copy</b> and <b>Paste</b> on the
    /// network strip.
    /// </summary>
    /// <remarks>
    /// <b>Every one of the three is a single call into code that already exists</b>
    /// (<c>R-smith7-1</c>): the schematic editor's <c>SchematicClipboard</c> for the network, the Data
    /// Display's <c>PlotExporter</c> for the chart. No format, no P/Invoke and no second Avalonia
    /// session is written here — the Windows path in particular must be one <c>SetClipboard</c>
    /// session, because Avalonia's <c>SetDataAsync</c> empties the clipboard and keeps ownership, and
    /// that is already paid for.
    ///
    /// <para>The menu is attached in code rather than declared in the AXAML for the reason every
    /// other context menu in this application is: a <c>ContextMenu</c> is a popup with its own visual
    /// root, so a <c>MenuItem</c> declared inside one is not reliably reachable by
    /// <c>FindControl</c>, and a handler that silently never attaches is a menu entry that does
    /// nothing. The CHART gets none — see below.</para>
    /// </remarks>
    private void BindClipboard(SmithChartViewModel vm)
    {
        vm.NetworkCopySink = async model =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

            // The owner window's handle, for the Windows CF_ENHMETAFILE session. Zero elsewhere, and
            // zero is what SchematicClipboard's own non-Windows path expects.
            IntPtr owner = (TopLevel.GetTopLevel(this) as Window)?.TryGetPlatformHandle()?.Handle
                           ?? IntPtr.Zero;

            await SchematicClipboard.CopyAsync(
                clipboard, model.Components, model.Wires, model.CanvasObjects, model.GridSize,
                netLabels: null, schematicDirectory: vm.DocumentDirectory, ownerHwnd: owner);
        };

        vm.NetworkPasteSource = async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;

            // Offset ZERO, unlike a schematic paste: this one does not land beside what is already
            // there, it REPLACES it, and the recognizer reads the drawn x of each end to decide which
            // is the generator. A nudge would be a nudge of the whole cascade and would mean nothing.
            var payload = await SchematicClipboard.PasteAsync(clipboard, offsetX: 0, offsetY: 0);
            return payload is { } p ? (p.Comps, (IReadOnlyList<EditableWire>)p.Wires) : null;
        };

        vm.ChartCopySink = container => PlotExporter.CopyPlotToClipboardAsync(
            ChartPlotControl, vm.ChartPlot, vm.PlotHost.Theme,
            showFilePrefix: false, container: container);

        // NO CONTEXT MENU IS ATTACHED TO THE CHART, and that is the requirement rather than a gap.
        // R-smith7-5 asks for "right-click the chart ▸ Copy"; PlotControl has had exactly that item
        // for as long as it has had a menu, alongside Plot Properties, Axes Limits, Autoscale, Add
        // Marker and Export — and it reaches the same PlotExporter call through the
        // ContainerProvider brief 5 set. Assigning ContextMenu here would REPLACE all of it with one
        // item, which is a Copy that works and five things that silently stopped existing.
        //
        // What is left for this view is Edit ▸ Copy with the chart focused (OnEditCopy), which no
        // context menu can answer because it is the shell's.

        NetworkCanvas.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item("Copy",  () => vm.CopyNetworkCommand.Execute(null)),
                Item("Paste", () => vm.PasteNetworkCommand.Execute(null)),
            },
        };

        static MenuItem Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
    }

    /// <summary>
    /// Edit ▸ Copy and Edit ▸ Paste, <b>routed by focus</b> (<c>R-smith7-9</c>).
    /// </summary>
    /// <remarks>
    /// Copy means the chart when the chart has focus and the network when the network does. There is
    /// no third meaning and no ambiguity to resolve at the command — the routing is the focused pane's
    /// — and with neither focused the network is the answer, because that is the half a selection can
    /// come from and go back to.
    ///
    /// <para><b>Paste has only one meaning</b>: a chart is not something a schematic selection can be
    /// pasted into, so Edit ▸ Paste is the network's wherever the focus is. Saying so is cheaper than
    /// a second command that refuses.</para>
    /// </remarks>
    private void OnEditCopy()
    {
        if (_doc?.ViewModel is not { } vm) return;

        if (ChartPlotControl.IsKeyboardFocusWithin) vm.CopyChartCommand.Execute(null);
        else                                        vm.CopyNetworkCommand.Execute(null);
    }

    private void OnEditPaste() => _doc?.ViewModel.PasteNetworkCommand.Execute(null);
}
