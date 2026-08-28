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

        // Page Up/Down, Home and End, on the way DOWN to whatever has focus. See
        // OnPanelScrollKeyDown for why the window and not the list.
        AddHandler(KeyDownEvent, OnPanelScrollKeyDown, RoutingStrategies.Tunnel);
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

        // ── An open inline editor is committed by a press anywhere outside it ──
        //
        // Owner-reported, 2026-08-20: "if I click away from the inline text editor in the schematic,
        // it does not close." The box dismisses itself on LostFocus, and LostFocus never came: almost
        // nothing in this window is FOCUSABLE — the schematic canvas is a plain Control, and so are
        // the pane backgrounds, the TextBlocks and the borders — so a click on any of them moves
        // focus nowhere and the box is never told. The schematic page does not have this problem
        // because its canvas takes focus for its own keyboard tools.
        //
        // Checked BEFORE the left-button and modifier guards below, and on the whole window: a
        // right-click or a Ctrl-click away from the box is just as much "away" as a plain one. The
        // press that OPENS an editor is unaffected — it lands on the canvas, commits whatever was
        // open (which OnSchematicLabelDoubleTapped did anyway) and the double-tap then opens the new
        // one against a hidden box.
        if (_labelEditor is { IsVisible: true }
            && (e.Source is not Visual from
                || from.FindAncestorOfType<Controls.SchematicInlineEditBox>(includeSelf: true) is null))
            CommitLabelEdit();

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
    /// Opens a Designer bound to <b>nothing</b> — Tools ▸ Match Designer.
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-20:</b> <i>"add a Match Designer to the circuitRF Tools menu. When selected,
    /// an 'orphaned' Designer window appears that still allows user to author a design and Flatten to
    /// Cell."</i>
    ///
    /// <para><b>Not deduplicated, unlike <see cref="Show"/>.</b> That method keeps one window per
    /// component because two views of one <c>Design</c> parameter would write it from two working
    /// copies. A standalone Designer writes no component at all, so two of them are two independent
    /// scratch designs — which is a thing a user may legitimately want open side by side.</para>
    /// </remarks>
    /// <param name="workspaceRoot">The open workspace's root, or null — a starting folder for Flatten.</param>
    /// <param name="owner">The window to cascade off, and to close with. May be null.</param>
    public static MatchDesignerWindow ShowStandalone(string? workspaceRoot, Window? owner)
    {
        var vm = new MatchDesignerViewModel();
        vm.SetStandalone(workspaceRoot);

        var window = new MatchDesignerWindow { DataContext = vm };
        window.Closed += (_, _) => vm.Dispose();

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

    // The transform rack's N is an InlineEditText now, which owns the whole three-key contract and
    // commits through MatchTransformRowViewModel.NEntry — so the LostFocus handler that used to
    // parse the old TextBox is gone rather than left wired to a control that no longer exists.

    // ── The slider gesture ────────────────────────────────────────────────────
    //
    // Begin/end around the pointer, not around each value change: the ladder and every element value
    // track the slider live, and only the response PLOTS are held for the gesture (brief §5).

    /// <summary>
    /// Wires one transform slider's drag gesture. <b>Tunnelling, and registered here rather than as a
    /// XAML <c>PointerPressed=</c> attribute.</b>
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-20:</b> <i>"when I change Transforms with the slider UI controls,
    /// the plot's render glitches."</i>
    ///
    /// <para>The plots are supposed to be HELD for the duration of a drag and run once on release
    /// (<c>MatchDesignerViewModel.BeginTransformDrag</c>) — an S-parameter sweep at 401 points per
    /// mouse-move is the one part of the chain that cannot keep up, and each sweep re-autoscales both
    /// axes, which is the movement the owner is describing. The hold never happened: a XAML
    /// <c>PointerPressed=</c> attribute subscribes to the BUBBLING route with
    /// <c>handledEventsToo: false</c>, and Avalonia's <c>Thumb</c> marks both the press and the
    /// release handled before either reaches the Slider. So grabbing the thumb — the ordinary way to
    /// drag a slider — never started a gesture, never ended one, and every intermediate value ran a
    /// full sweep. Clicking the slider's TRACK did work, which is why this survived review.</para>
    ///
    /// <para>Tunnelling handlers run from the root DOWN, so they reach the Slider before the Thumb
    /// gets to mark anything handled. <c>handledEventsToo</c> is set as well, belt and braces, and
    /// costs nothing: <c>BeginTransformDrag</c> is idempotent and <c>EndTransformDrag</c> returns
    /// immediately when no gesture is running.</para>
    ///
    /// <para>The table is keyed weakly on the Slider because an <c>ItemsControl</c> builds a new one
    /// per row per rebuild, and <c>Loaded</c> fires again on every re-attach — subscribing twice would
    /// be harmless but unbounded.</para>
    /// </remarks>
    private void OnSliderLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (_wiredSliders.TryGetValue(slider, out _)) return;
        _wiredSliders.Add(slider, this);

        const RoutingStrategies both = RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
        slider.AddHandler(PointerPressedEvent, OnSliderPressed, both, handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent, OnSliderReleased, both, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, OnSliderCaptureLost, both, handledEventsToo: true);
    }

    private readonly ConditionalWeakTable<Slider, MatchDesignerWindow> _wiredSliders = new();

    private void OnSliderPressed(object? sender, PointerPressedEventArgs e) => Vm?.BeginTransformDrag();

    private void OnSliderReleased(object? sender, PointerReleasedEventArgs e) => Vm?.EndTransformDrag();

    // A capture lost without a release — the window losing focus mid-drag, say — must still end the
    // gesture, or the plots stay held for the rest of the session and stop tracking anything.
    private void OnSliderCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        Vm?.EndTransformDrag();

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

        // A standalone Designer has no instance to replace and no workspace to stay inside of, so it
        // gets the dialog without the checkbox and without the confinement — see the dialog's own
        // standalone constructor.
        var dialog = Vm.IsStandalone
            ? new Dialogs.MatchFlattenDialog(availability.DefaultName, availability.ParentDir)
            : new Dialogs.MatchFlattenDialog(
                Vm.InstanceName, availability.DefaultName, availability.ParentDir);

        var choice = await dialog.ShowDialog<Dialogs.MatchFlattenChoice?>(this);
        if (choice is null) return;

        var result = Vm.Flatten(choice.ParentDir, choice.CellName, choice.ReplaceInPlace);

        // A schematic-bound Designer posts through the schematic's own MessageSink; a standalone one
        // has none, so the outcome is said where the user asked for it.
        if (Vm.IsStandalone) Vm.SetFlattenOutcome(result.Message, result.Ok);
    }

    /// <summary>
    /// <b>Selecting a solution applies it.</b>
    /// </summary>
    /// <remarks>
    /// <b>Owner, 2026-08-28:</b> the Apply / Applied button comes off the card, and a solution is
    /// applied as soon as the user clicks the card. A Click handler on the card was the obvious shape
    /// and is the wrong one: the owner asked in the same breath for the up and down arrows to move
    /// between solutions, and a <c>ListBox</c> already turns BOTH a click and an arrow key into the
    /// same thing — a selection. Hanging the apply off selection rather than off the pointer is what
    /// makes the keyboard gesture free, and it is why there is no second code path to diverge from.
    ///
    /// <para>The double-tap handler is gone with the button, and nothing is lost: a double-click
    /// selects on its first press, so it applies exactly as it did.</para>
    ///
    /// <para><b>The guard is not decoration.</b> <c>Apply</c> re-badges every row and refreshes the
    /// list, and <see cref="SyncSolutionSelection"/> writes <c>SelectedItem</c> straight back —
    /// either of which raises this event again. Re-entering would apply a solution twice and, worse,
    /// could apply a row the list had merely re-selected on the user's behalf.</para>
    /// </remarks>
    private void OnSolutionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSolutionSelection) return;
        if (_solutionsList?.SelectedItem is not MatchSolutionRowViewModel row) return;
        if (row.IsCurrent) return;

        _syncingSolutionSelection = true;
        try { row.Apply(); }
        finally { _syncingSolutionSelection = false; }

        SyncSolutionSelection();
    }

    /// <summary>
    /// Points the list's selection at the applied row, without that write being read back as a new
    /// choice by the user.
    /// </summary>
    /// <remarks>
    /// The selection has to FOLLOW the applied row as well as cause it, for two reasons: the arrow
    /// keys step from wherever the selection is, and the applied row changes from paths that are not
    /// clicks at all — the termination auto-solve, an undo, a filter that hides the row that was
    /// selected. Without this the first arrow key after any of those would jump somewhere the user
    /// was not.
    /// </remarks>
    private void SyncSolutionSelection()
    {
        if (_solutionsList is null || Vm is null) return;

        var applied = Vm.Solutions.FirstOrDefault(r => r.IsCurrent);
        if (ReferenceEquals(_solutionsList.SelectedItem, applied)) return;

        _syncingSolutionSelection = true;
        try { _solutionsList.SelectedItem = applied; }
        finally { _syncingSolutionSelection = false; }
    }

    private bool _syncingSolutionSelection;

    /// <summary>
    /// Moves the solutions selection one row, which applies that solution.
    /// </summary>
    /// <remarks>
    /// <b>The scroll is EXPLICIT here and nowhere else.</b> The list has
    /// <c>AutoScrollToSelectedItem</c> off, because a selection made by clicking must not move the
    /// viewport (owner, same round) — but a selection made by an arrow key must, or the user walks
    /// the list straight off the bottom of it. Doing it from this one method is what separates the
    /// two gestures; a property could not, since both arrive as the same selection change.
    ///
    /// <para>Returns true at the ends of the list as well, so holding an arrow down does not start
    /// scrolling the pane underneath once the selection can go no further.</para>
    /// </remarks>
    private bool MoveSolutionSelection(int delta)
    {
        if (_solutionsList is null || Vm is null || Vm.Solutions.Count == 0) return false;

        int from = _solutionsList.SelectedIndex;
        if (from < 0)
        {
            var applied = Vm.Solutions.FirstOrDefault(r => r.IsCurrent);
            from = applied is null ? -1 : Vm.Solutions.IndexOf(applied);
        }

        int to = Math.Clamp(from + delta, 0, Vm.Solutions.Count - 1);
        if (to != from) _solutionsList.SelectedIndex = to;
        _solutionsList.ScrollIntoView(to);
        return true;
    }

    // ── The solutions list scrolls to the applied row ─────────────────────────

    private ListBox? _solutionsList;
    private bool _scrolledToApplied;

    /// <summary>
    /// Brings the applied solution into view the first time the list has one to show (owner,
    /// 2026-08-28: "when Solutions scroll view first appears, it needs to be scrolled to the
    /// currently applied solution").
    /// </summary>
    /// <remarks>
    /// <b>Once per list, not once per change.</b> Re-scrolling whenever the applied row moves would
    /// yank the panel away from whatever the user was reading every time they clicked Apply — which
    /// is the opposite of the ask, since after an Apply the row they want is the one already under
    /// their pointer. The flag is re-armed on a Reset (the collection is cleared when a specification
    /// change starts a fresh search), because at that point the scroll position means nothing anyway.
    ///
    /// <para>Posted at <c>Background</c> priority rather than called inline: the row arrives as the
    /// search publishes its first cell, and a virtualized container for it does not exist until the
    /// next layout pass — <c>ScrollIntoView</c> on a row with no container scrolls nowhere and
    /// reports nothing.</para>
    /// </remarks>
    private void WireSolutionsList()
    {
        if (Vm is null) return;
        _solutionsList = this.FindControl<ListBox>("SolutionsList");
        if (_solutionsList is null) return;

        _solutionsList.SelectionChanged += OnSolutionSelectionChanged;

        var solutions = Vm.Solutions;
        solutions.CollectionChanged += OnSolutionsCollectionChanged;
        Closed += (_, _) => solutions.CollectionChanged -= OnSolutionsCollectionChanged;
        ScrollToAppliedOnce();
        SyncSolutionSelection();
    }

    private void OnSolutionsCollectionChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _scrolledToApplied = false;
            SyncSolutionSelection();
            return;
        }
        ScrollToAppliedOnce();
        SyncSolutionSelection();
    }

    private void ScrollToAppliedOnce()
    {
        if (_scrolledToApplied || _solutionsList is null || Vm is null) return;
        if (!Vm.Solutions.Any(r => r.IsCurrent)) return;

        _scrolledToApplied = true;
        ScrollToApplied();
    }

    /// <summary>
    /// Brings the applied solution into view — the header's own button, and what the once-only
    /// automatic scroll calls (owner, 2026-08-28: "add a button next to the filter button that will
    /// auto-scroll to the current solution card").
    /// </summary>
    /// <remarks>
    /// The automatic scroll happens once, when the list first has an applied row to show, and after
    /// that the panel stays where the user left it. That is the right default and it leaves them no
    /// way back once they have scrolled a long list looking for something better — which is what this
    /// button is. Both go through here, so there is one behaviour and not two.
    /// </remarks>
    private void ScrollToApplied()
    {
        if (_solutionsList is null || Vm is null) return;

        var applied = Vm.Solutions.FirstOrDefault(r => r.IsCurrent);
        if (applied is null) return;

        Dispatcher.UIThread.Post(() => _solutionsList?.ScrollIntoView(applied), DispatcherPriority.Background);
    }

    private void OnSortGrid(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string column) Vm?.SortElements(column);
    }

    /// <inheritdoc/>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (Vm is not { } vm) return;

        // ONCE. Loaded fires again on every re-attach, and everything below is a subscription — a
        // second pass would run each button's handler twice and hold two view-model subscriptions.
        if (_wired) return;
        _wired = true;

        // There is no CloseButton to wire (owner, 2026-08-20: "remove the close button from the top
        // of window") — the title bar's own is the one that closes this window.
        // No Apply button to wire (owner, 2026-08-20: "the Apply button is always disabled, even
        // after I make changes. What does apply do?"). It never sent the design anywhere — every
        // edit already commits as it is made — and the half-typed-field state it flushed stopped
        // existing when the last box became an InlineEditText. See MatchDesignerViewModel's own note.
        WireButton("RevertButton", (_, _) => Vm.Revert());
        WireElementsContextMenu();
        WireSchematicContextMenu();
        WireSchematicInlineEditor();
        WirePlotHost();
        WireSolutionsList();
        WireButton("ScrollToAppliedButton", (_, _) => ScrollToApplied());
        WireButton("RemoveTransformButton", (_, _) => Vm.RemoveLastTransform());
        WireButton("AddTransformButton", (s, _) => ShowAddTransformMenu(s as Control));
        WireButton("ExportButton", (s, _) => ShowExportMenu(s as Control));
        WireButton("SettingsButton", (s, _) => ShowSettingsMenu(s as Control));
        // No wiring for SolutionsFilterButton: it carries a Button.Flyout of CheckBoxes bound
        // straight to Filter, exactly as the Project Tree's category filter does (owner, 2026-08-28).
        // A code-behind menu was the first shape of it and is gone — see the AXAML for why.
        // Same deep link the Parameter Editor's own Help button uses, to the same Reference page.
        // The Match CHAPTER, not the components page's one-paragraph entry (brief-user-docs-content
        // §10 gave Match a chapter of its own, and this window is what it documents). Listed in
        // DocAnchors.WholePages, so the docs build fails if that page stops being emitted.
        WireButton("HelpButton", (_, _) => DocLauncher.Open("reference/match.html"));
        WireButton("NetworkZoomFitButton",
                   (_, _) => this.FindControl<MatchSchematicCanvas>("NetworkSchematic")?.ZoomToFit());

        // The two pane expanders move COLUMN WIDTHS, which no binding can reach — see SyncPaneLayout.
        // The view-model is captured rather than re-read on Closed: DataContext can already be gone
        // by then, and an unsubscribe that quietly does nothing is a leak with no symptom.
        vm.PropertyChanged += OnVmPropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmPropertyChanged;
        SyncPaneLayout();

        BindNumericBox(
            "BandFractionBox",
            () => (Vm.PlotBandFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%",
            t => Vm.CommitPlotWindow(MatchDesignerViewModel.ParseBandPercent(t), null));
        BindNumericBox(
            "PlotPointsBox",
            () => Vm.PlotPoints.ToString(CultureInfo.InvariantCulture),
            t => Vm.CommitPlotWindow(null, MatchDesignerViewModel.ParsePlotPoints(t)));
    }

    private bool _wired;

    private void WireButton(string name, EventHandler<RoutedEventArgs> handler)
    {
        if (this.FindControl<Button>(name) is { } b) b.Click += handler;
    }

    // ── Pane expansion (owner, 2026-08-20) ────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MatchDesignerViewModel.NetworkExpanded)
                           or nameof(MatchDesignerViewModel.ResponseExpanded))
            SyncPaneLayout();
    }

    /// <summary>
    /// Gives one of the two right-hand panes the other's column, or puts both back.
    /// </summary>
    /// <remarks>
    /// <b>From code, not from a <c>{Binding}</c> on the <c>ColumnDefinition</c>.</b> A
    /// <c>ColumnDefinition</c> is an <c>AvaloniaObject</c> that is not in the logical tree, so no
    /// DataContext reaches it and a binding on its <c>Width</c> silently resolves to nothing — the
    /// column would simply keep whatever the AXAML gave it, with no error to notice.
    ///
    /// <para>Hiding the pane is not enough on its own and is done as well, in the AXAML: a collapsed
    /// pane with its column still standing leaves a 380 px hole where the response used to be. The
    /// two together are what "expand over it" means.</para>
    /// </remarks>
    private void SyncPaneLayout()
    {
        if (Vm is null || this.FindControl<Grid>("PaneGrid") is not { } grid) return;
        if (grid.ColumnDefinitions.Count < 3) return;

        var star = new GridLength(1, GridUnitType.Star);
        var zero = new GridLength(0, GridUnitType.Pixel);

        grid.ColumnDefinitions[1].Width = Vm.ResponseExpanded ? zero : star;
        grid.ColumnDefinitions[2].Width =
            Vm.NetworkExpanded  ? zero
            : Vm.ResponseExpanded ? star
            : new GridLength(ResponseColumnWidth, GridUnitType.Pixel);
    }

    /// <summary>The response pane's resting width. Matches the AXAML's own literal.</summary>
    private const double ResponseColumnWidth = 380;

    /// <summary>
    /// <b>F re-frames the network schematic</b>, the same key the schematic editor uses for the same
    /// thing (owner, 2026-08-20: "make sure the keystroke &lt;F&gt; will zoom the schematic to fit").
    /// </summary>
    /// <remarks>
    /// Handled here rather than as a <c>Window.KeyBindings</c> entry, and the reason is the one thing
    /// a bare-letter gesture always gets wrong: a <c>TextBox</c> does not mark <c>KeyDown</c> handled
    /// for an ordinary character — it consumes <c>TextInput</c> — so a window-level <c>F</c> binding
    /// would re-frame the drawing every time the user typed an "f" into a field. This is
    /// <c>SchematicView</c>'s own guard, in the shape this window needs: nothing happens while a text
    /// box owns the keyboard, and that covers both the schematic's inline label editor and every
    /// <c>InlineEditText</c> in the specification pane and the transform rack, since each opens a real
    /// <c>TextBox</c> while it is being typed into.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key != Key.F || e.KeyModifiers != KeyModifiers.None) return;
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        this.FindControl<MatchSchematicCanvas>("NetworkSchematic")?.ZoomToFit();
        e.Handled = true;
    }

    // ── Page Up / Page Down / Home / End ──────────────────────────────────────

    /// <summary>
    /// Routes the four scrolling keys to the Solutions list.
    /// </summary>
    /// <remarks>
    /// <b>Owner-reported, 2026-08-28:</b> Page Up, Page Down, Home and End are unreliable — the same
    /// keystroke sometimes moves the list and sometimes does nothing.
    ///
    /// <para><b>"Sometimes" was exactly right, and it was about FOCUS.</b> Nothing in this window
    /// bound those keys at all: the only thing that ever answered them was the <c>ListBox</c>'s own
    /// built-in navigation, which runs on <c>KeyDown</c> and therefore only when the keyboard focus is
    /// already inside the list. Click a card and it works; scroll the list with the wheel, or arrive
    /// from a field in the specification pane, or open the window and press Home before touching
    /// anything, and the key reaches a focused element somewhere else entirely and the list does not
    /// move. The same keystroke, two outcomes, decided by something the user has no way to see.</para>
    ///
    /// <para><b>Tunnelling, from the window.</b> A tunnel handler sees the key on its way DOWN to the
    /// focused element, so it runs wherever the focus is — which is the whole fix. It is also what
    /// takes the keys off the ListBox's own navigation, and that is wanted: the built-in behaviour
    /// moves the SELECTION, and selection is invisible in this list by design (see the AXAML's row
    /// styles). Home moving a mark nobody can see, while the viewport stays put, is not what the
    /// owner asked Home to do.</para>
    ///
    /// <para><b>The rule itself is <see cref="PanelScrollKeys"/>'s</b>, the one the Project Tree, the
    /// Library palette and the .ctech editor's row lists already share: Page Up/Down are always taken,
    /// Home and End are yielded to a <c>TextBox</c> because there they mean "caret to the start/end of
    /// this field". An open <c>ComboBox</c> dropdown owns all four for the same reason it does in the
    /// Project Tree — it is navigating its own items.</para>
    ///
    /// <para><b>Which scroller.</b> The one the focus is already inside, when there is one — the
    /// specification cards and the component listing are scrollers too, and a Page Down typed with
    /// the caret in a transform's field should page what the user is looking at. Otherwise the
    /// Solutions list, because it is the long list this window is read from and the one the four keys
    /// are worth having. Both cases are deterministic, which is what the report was really
    /// about.</para>
    /// </remarks>
    private void OnPanelScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        if (e.Key is Key.Up or Key.Down)
        {
            if (!SolutionsTakeTheArrowKeys(e.Source)) return;
            if (!MoveSolutionSelection(e.Key == Key.Up ? -1 : +1)) return;
            e.Handled = true;
            return;
        }

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        if (ScrollerForKeys() is not { } scroll) return;

        PanelScrollKeys.Apply(action.Value, scroll);
        e.Handled = true;
    }

    /// <summary>
    /// Whether an Up/Down keystroke belongs to the solutions list, or to whatever it landed on.
    /// </summary>
    /// <remarks>
    /// <b>Up and Down step through the solutions</b> (owner, 2026-08-28: the arrow keys select the
    /// card above or below the current one). Selecting a card applies it, so this really is the
    /// gesture for auditioning solutions one at a time — which is the point.
    ///
    /// <para><b>THE MARKER WINS, and the owner said so</b> in the same breath as asking for the
    /// gesture: <i>a marker up/down keystroke for a plot may conflict; give priority to the
    /// marker.</i> A selected marker steps by one x-axis sample on Up/Down
    /// (<c>PlotControl.OnKeyDown</c> and <c>MarkerInfoBoxView.OnKeyDown</c>, both bubbling — so a
    /// tunnel handler like this one would otherwise take the key off them before they ever saw it).
    /// The test is the same one <c>DeleteSelectedMarkers</c> in this window already uses for "is a
    /// marker selected", so the two gestures cannot disagree about what a selected marker is. When
    /// there is one, this yields and touches nothing, and the marker path runs exactly as it does
    /// today.</para>
    ///
    /// <para><b>The three controls that own their own arrows keep them</b> — a slider (the transform
    /// rack is full of them, and Up/Down is how an N is nudged), a text box, and a combo. This is a
    /// tunnel handler, so without naming them it would silently take the arrows off all three; the
    /// four scrolling keys below do not need the same list because none of those three does anything
    /// with Page Up/Down, and Home/End are already yielded to a field by
    /// <see cref="PanelScrollKeys"/>'s own rule.</para>
    /// </remarks>
    private bool SolutionsTakeTheArrowKeys(object? source)
    {
        if (Vm is null) return false;
        if (Vm.PlotHost.MarkerInfoBoxes.Any(b => b.IsSelected)) return false;
        return source is not (TextBox or Slider or ComboBox);
    }

    /// <summary>
    /// The scroller the four keys act on: the nearest one above the focused element, or the Solutions
    /// list's own when the focus is not inside a scroller at all.
    /// </summary>
    private ScrollViewer? ScrollerForKeys()
    {
        if (FocusManager?.GetFocusedElement() is Visual focused)
        {
            for (var v = focused; v is not null; v = v.GetVisualParent())
            {
                if (v is ScrollViewer sv) return sv;
                if (ReferenceEquals(v, this)) break;
            }
        }

        // The ListBox's own — a ListBox IS a scroller, but through the ScrollViewer in its template,
        // so the control itself has no PageDown to call.
        return _solutionsList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
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
        // The ANCHOR comes from the drawing; the TEXT does not. A label may carry an annotation the
        // parser would refuse — a termination's "(target 50 Ω)" is the one that bit
        // (MatchInlineEditTarget.SeedText) — so the editor is seeded with the value the view-model
        // resolved, formatted exactly as the canvas formats a value.
        _labelEditor.Open(target.SeedText, at.ScreenX, at.ScreenY, at.FontSize);

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
    /// Settings (§9.9): display units per dimension, significant digits and Qmin. <b>Nothing else</b>
    /// — and in particular no standard-value series.
    /// </summary>
    /// <remarks>
    /// <b>"Offer Q-adjusted solutions" was the fourth entry and is gone</b> (owner, 2026-08-28). It
    /// switched off a whole class of solution before the search ran; the same choice is now a line in
    /// the solutions panel's own filter, made in front of the list it changes and costing nothing to
    /// flip because the candidates are always computed.
    /// </remarks>
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
