using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Layout;

/// <summary>
/// Code-behind for the .ctech editor. Every editable cell across the three sections (layer
/// table, stackup, DRC rules) commits through one of two generic dispatchers keyed by the
/// control's <see cref="Control.Tag"/> and its DataContext's row-VM type — avoids one handler
/// method per field across three different row VMs.
/// </summary>
public partial class TechEditorView : UserControl
{
    public TechEditorView()
    {
        InitializeComponent();

        // TUNNELLING, not bubbling: a ListBox handles Page Up/Down and Home/End itself (moving the
        // selection), and these lists are flattened so that selection is invisible — the keystroke
        // would appear to do nothing while quietly changing what is selected. Getting there first is
        // what makes the key scroll the pane instead.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);

        // R-stk7-4 — Ctrl/Cmd+C copies the cross-section as a picture.
        //
        // BUBBLING, and the only one of the three handlers here that is: the brief asks for the
        // keystroke "while the drawing has focus", and the drawing CANNOT have focus — R-stk2-10
        // keeps StackupCanvas non-focusable on purpose, because a focusable control inside the
        // drawing's ScrollViewer re-points Page Up/Down at the drawing. So focus cannot be the
        // discriminator, and a tunnelling handler would take Ctrl+C away from every text box in the
        // tab before the box ever saw it. Bubbling gives exactly the rule the brief asked for by a
        // different route: a control that has its own meaning for the keystroke — a card's text
        // box, the filter box, the open inline editor — handles it first and this is never called.
        // What is left over is a Ctrl+C nothing in the tab claimed, and on the Stackup tab that means
        // the picture. See OnCopyKeyDown.
        AddHandler(KeyDownEvent, OnCopyKeyDown);

        // Delete removes the selected stackup entry (owner, 2026-09-13).
        //
        // BUBBLING, for exactly the reason the Ctrl+C handler above bubbles and the two Esc/scroll
        // handlers tunnel: a control with its own meaning for the key — a card's text box, the open
        // inline editor, the filter box — must get it first, and a tunnelling handler would take
        // Delete away from every field in the tab before the field ever saw it. What is left over is
        // a Delete nothing in the tab claimed, and on the Stackup tab that means the selection.
        //
        // No handledEventsToo, and none is needed: WorkspaceWindow binds no gesture for this key
        // (its KeyBindings were read before this was taken), so unlike Escape it is not already
        // marked Handled by the time it reaches this view.
        AddHandler(KeyDownEvent, OnDeleteKeyDown);

        // …and the half that makes it reachable. A keystroke goes to whatever holds focus, and the
        // drawing cannot hold it (R-stk2-10, non-focusable by design) — so after clicking a card's
        // text box and then clicking a band, focus is still in that text box and a Delete typed next
        // would edit the text rather than remove the band the user is looking at. Taking focus to the
        // VIEW on a press over the drawing is what puts the keystroke back where the selection is; it
        // is the same target StackupInlineEditor.Closed and FocusForScrollingDeferred already use.
        //
        // handledEventsToo, because StackupCanvas marks a left press handled once it has acted on it
        // — and AFTER it, not tunnelling before it, so the selection is made against the scene the
        // user clicked on rather than one a commit triggered here has just rebuilt.
        StackupDrawing.AddHandler(PointerPressedEvent, OnStackupDrawingPressed,
                                  RoutingStrategies.Bubble, handledEventsToo: true);

        // R-stk3-9 — Esc clears the stackup selection, and R-stk4-6 — Esc reverts an open inline
        // editor before it does.
        //
        // TUNNELLING FROM THE VIEW, and not a key handler on the drawing, which is what brief 3
        // sketched. The canvas is deliberately NOT focusable (R-stk2-10: a focusable control inside
        // the drawing's ScrollViewer re-points Page Up/Down at the drawing, because TargetScrollViewer
        // below walks up from whatever holds focus) — and the owner's ask is "pressing Esc will
        // unselect", not "pressing Esc while the drawing happens to have focus", so the handler had to
        // cover the card list as well either way. One handler covers both surfaces.
        //
        // handledEventsToo: TRUE, and WITHOUT IT THIS HANDLER NEVER RUNS AT ALL. A docked document
        // sits inside WorkspaceWindow, which carries `<KeyBinding Gesture="Escape" …/>` — and a
        // Window's KeyBindings are evaluated BEFORE visual-tree routing begins, so Escape arrives at
        // this view already marked Handled and an ordinary handler is skipped. Owner-reported twice
        // here (2026-09-13), and the third instance in this application: SchematicView's
        // OnViewKeyDownTunnel and ReadoutStripView's OnStripKeyDownTunnel each hit it for their own
        // inline editor and each names the mechanism in a comment. Page Up/Down above needs no such
        // flag because the window binds no gesture for them, which is exactly why that handler works
        // and this one did not.
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The scroll handler above is TUNNELLING FROM THIS CONTROL, so it only ever sees a keystroke
        // that is already routing through this view — which means something inside the view has to
        // hold focus for Page Up/Down to work at all. On first open nothing does: the tab is
        // activated before the view is bound, so focus is still wherever it was and the keystroke
        // routes somewhere else entirely. The three other document views already take focus on
        // activation through this same hook; this one never subscribed, which is the whole bug.
        DataContextChanged += OnDataContextChanged;

        // R-stk4-2. The drawing and the box are siblings in one Panel; this is what joins them. The
        // canvas owns the half that needs the scene (which label was hit, what it seeds from, where
        // exactly it goes) and the box stays the host's, because the three-key contract below is
        // wired to it here.
        StackupInlineEditor = new StackupInlineEditor(StackupInlineEdit);
        StackupDrawing.InlineEditor = StackupInlineEditor;

        // Focus needs a visual root, so the editor raises this rather than taking focus itself.
        // Posted at Input priority for the same reason SchematicView posts its own: the box has just
        // been made visible and is not yet realised, and Focus() on an unrealised control does
        // nothing at all.
        StackupInlineEditor.Opened += () =>
            Dispatcher.UIThread.Post(() => StackupInlineEdit.Focus(), DispatcherPriority.Input);

        // …and the other half. Hiding the focused box drops keyboard focus OUT of this view, so the
        // next keystroke routes nowhere near it — which is why a second Esc did not clear the band
        // selection (owner, 2026-09-13) and why Page Up/Down went dead after any committed edit.
        // Focusing the VIEW is the same target FocusForScrollingDeferred already uses and lands on
        // TargetScrollViewer's documented fallback, the visible tab's own row list.
        //
        // Synchronous, unlike Opened's: Avalonia clears focus inside the IsVisible assignment itself,
        // so by the time this runs the cascade is over and there is nothing to race. Deferring it
        // would leave a window in which a fast second Esc still found no focused element.
        StackupInlineEditor.Closed += () => Focus();

        // The destination follows the visible tab (TechEditorViewModel.HelpDestinationFor) — this
        // window edits four unrelated things and no one chapter covers all of them.
        HelpButton.Click += (_, _) =>
        {
            var (page, anchor) = (DataContext as TechDocument)?.ViewModel.HelpDestination
                                 ?? TechEditorViewModel.HelpDestinationFor(0);
            DocLauncher.Open(page, anchor.Length == 0 ? null : anchor);
        };
    }

    private TechDocument? _subscribedDoc;
    private TechEditorViewModel? _subscribedVm;

    /// <summary>The one inline editor over the cross-section (brief 4). Internal for the gate, which
    /// has no application host to raise a real double-click in.</summary>
    internal StackupInlineEditor StackupInlineEditor { get; }

    // ── The three-key contract (R-stk4-6) ─────────────────────────────────────────────────────────
    //
    // The host's to wire, and stated here for both surfaces that host this box: SchematicInlineEditBox
    // raises nothing and handles no key itself, which is exactly what lets two hosts adopt it without
    // either changing behaviour.
    //
    //   Return    commits, and marks the key handled.
    //   LostFocus commits. This is the one that costs the user an edit if it is missed.
    //   Escape    reverts — closes the box, writes nothing.

    private void OnStackupInlineEditKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return or Key.Enter:
                StackupInlineEditor.Commit();
                e.Handled = true;
                break;
            case Key.Escape:
                StackupInlineEditor.Revert();
                e.Handled = true;
                break;
        }
    }

    private void OnStackupInlineEditLostFocus(object? sender, RoutedEventArgs e)
        => StackupInlineEditor.Commit();

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedDoc is not null) _subscribedDoc.ActivationFocusRequested -= OnActivationFocusRequested;
        if (_subscribedVm is not null) _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm = null;

        _subscribedDoc = DataContext as TechDocument;
        if (_subscribedDoc is null) return;

        _subscribedVm = _subscribedDoc.ViewModel;
        _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;

        // The technology carries the two expanders' state, so the panes have to be sized the moment
        // one is bound — not only when a button is pressed.
        ApplyStackupPaneLayout();

        _subscribedDoc.ActivationFocusRequested += OnActivationFocusRequested;
        // Activated BEFORE the view bound — the first-open case — so the request is sitting pending.
        if (_subscribedDoc.ConsumeActivationFocus()) FocusForScrollingDeferred();
    }

    // ── The stackup selection scrolls the card list to its card (R-stk3-5) ─────

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TechEditorViewModel.SelectedStackupLayerName))
            ScrollStackupSelectionIntoView();
        else if (e.PropertyName is nameof(TechEditorViewModel.StackupDrawingExpanded)
                               or nameof(TechEditorViewModel.StackupCardsExpanded))
            ApplyStackupPaneLayout();
    }

    // ── The two pane expanders (owner, 2026-09-13) ────────────────────────────

    /// <summary>The card pane's height while both panes are open — remembered across a collapse, so
    /// re-expanding puts the splitter back where the user dragged it rather than at the opening
    /// split. Null until it has been open once.</summary>
    private GridLength? _cardPaneHeight;

    /// <summary>
    /// Resizes the two panes for the expanders' current state.
    ///
    /// <para><b>In code, and it has to be.</b> An Avalonia <c>RowDefinition</c> is not in the logical
    /// tree and inherits no DataContext, so <c>Height</c> cannot be bound to the view model; the
    /// .axaml carries the both-expanded state and this rewrites it.</para>
    ///
    /// <para><b>Exactly one row is starred at a time when a pane is collapsed.</b> The collapsed pane
    /// goes to <c>Auto</c> — which is its expander button and nothing else, since the .axaml hides the
    /// pane's own content — and the surviving pane takes the star, or the space the collapse freed
    /// would simply be left empty at the bottom of the tab. With both collapsed neither is starred and
    /// the tab is two buttons and the header, which is what asking for that means.</para>
    /// </summary>
    private void ApplyStackupPaneLayout()
    {
        if (StackupTabGrid is null || _subscribedVm is null) return;

        bool drawing = _subscribedVm.StackupDrawingExpanded;
        bool cards   = _subscribedVm.StackupCardsExpanded;

        var rows = StackupTabGrid.RowDefinitions;
        if (rows.Count < 4) return;

        // Captured BEFORE anything is rewritten, and only from the state it is meaningful in: while
        // both panes are open the card row is an absolute height the splitter owns.
        if (rows[3].Height.IsAbsolute) _cardPaneHeight = rows[3].Height;

        rows[1].Height    = drawing ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        rows[1].MinHeight = drawing ? TechEditorMetrics.StackupDrawingMinHeight : 0;

        rows[3].Height = (drawing, cards) switch
        {
            (_,     false) => GridLength.Auto,
            (false, true)  => new GridLength(1, GridUnitType.Star),
            _              => _cardPaneHeight ?? TechEditorMetrics.StackupCardPaneOpeningHeight,
        };
        rows[3].MinHeight = cards ? TechEditorMetrics.StackupCardPaneMinHeight : 0;

        // A splitter between a collapsed pane and a starred one moves nothing, and a grab strip that
        // does nothing is one users pull at.
        if (StackupSplitter is not null) StackupSplitter.IsVisible = drawing && cards;
    }

    /// <summary>
    /// <b>R-stk8-5 — the split a DOCUMENTATION CAPTURE needs, which is not the interactive one.</b>
    ///
    /// <para>The tab opens with the drawing starred and the card pane at a stated height, so in a
    /// window of ordinary size the drawing gets a sensible share and the splitter is there for anyone
    /// who wants it the other way. A figure is captured in a window <em>2,000 px tall</em>, because a
    /// reader cannot scroll a picture and every entry has to be in the frame — and at that height the
    /// starred row hands the drawing about 1,800 px, nearly all of it empty, while the cards keep
    /// their 140 and the other eight stay below the fold. The capture would show a split the
    /// interactive default would never produce.</para>
    ///
    /// <para>So the roles are swapped for the capture: the drawing takes the height it actually
    /// measured — <see cref="StackupScene"/>'s own intrinsic height, which is exactly the whole
    /// cross-section with nothing to scroll — and the CARDS take the star, which is what the catalog
    /// row's height is then chosen to fill. Neither number is typed: one is the scene's and the other
    /// is whatever is left.</para>
    ///
    /// <para>Called from <c>DocTechEditorFixtures</c> through <c>FigureScene.AfterLayout</c>, which
    /// runs after the window has been measured and arranged — before that there is no desired size to
    /// read. It is a capture-time arrangement and nothing in the application calls it.</para>
    /// </summary>
    internal void ApplyStackupSplitForCapture()
    {
        if (StackupTabGrid is null || StackupDrawing is null) return;

        var rows = StackupTabGrid.RowDefinitions;
        if (rows.Count < 4) return;

        rows[1].Height    = new GridLength(StackupDrawing.DesiredSize.Height);
        rows[1].MinHeight = 0;
        rows[3].Height    = new GridLength(1, GridUnitType.Star);
        rows[3].MinHeight = TechEditorMetrics.StackupCardPaneMinHeight;
    }

    /// <summary>
    /// Brings the selected entry's card into view, so a click on a band lands on its fields.
    ///
    /// <para><b>Posted at Background priority, and not run inline.</b> The view model may have just
    /// cleared the stackup filter (R-stk3-5 — a selection the filter would hide clears it, because a
    /// click that appears to do nothing is worse than a filter the user has to re-type), which
    /// rebuilds <c>FilteredStackupLayers</c> SYNCHRONOUSLY but leaves the <c>ListBox</c> to arrange
    /// later. <c>ScrollIntoView</c> against containers that have not been re-materialised scrolls to
    /// the wrong place or to nothing. This is the same deferral
    /// <see cref="FocusForScrollingDeferred"/> already makes in this file for the same reason — and
    /// deliberately not a timer.</para>
    /// </summary>
    private void ScrollStackupSelectionIntoView()
    {
        var row = _subscribedVm?.SelectedStackupLayerRow;
        if (row is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            // Re-read rather than closing over `row`: at Background priority an edit, an undo or a
            // second click may have landed in between, and every one of those replaces the row VMs.
            if (_subscribedVm?.SelectedStackupLayerRow is not { } current) return;
            if (StackupList is null) return;

            var scroll = StackupScroller();
            if (scroll is null) { StackupList.ScrollIntoView(current); return; }

            // A card that is already wholly on screen is left exactly where the user has it. Clicking
            // a band whose card is right there and having the list yank it to the top is movement for
            // its own sake — and with the ListBox's own auto-scroll off (see the .axaml), this is the
            // only thing that decides not to move.
            if (IsFullyVisible(current, scroll)) return;

            // ── NOTHING JUMPS, and that is the point ────────────────────────────────────────────
            //
            // This used to call ScrollIntoView to REALISE the card's container, measure off it, put
            // the offset straight back and then ease from there — all in one dispatcher frame, so the
            // jump itself was never painted. It still flashed, going DOWN (owner, 2026-09-13): the
            // jump de-realises everything at the origin, and the restored offset gets rendered before
            // the virtualizing panel has realised it again. What flashes is not the wrong position,
            // it is a blank viewport.
            //
            // So the list is never moved anywhere it is not going. The scroll sets out on an ESTIMATE
            // when the card is not realised yet, and the animator asks TopOffsetOf for the truth on
            // every frame — which starts answering the moment the scroll gets close enough to realise
            // it, and re-aims the ease without restarting it.
            double target = TopOffsetOf(current, scroll) ?? EstimatedOffsetOf(current, scroll);
            _stackupScroll.AnimateTo(scroll, target, () => TopOffsetOf(current, scroll));
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Roughly where a card that has not been realised yet will turn out to be: its share of the
    /// list's own extent.
    ///
    /// <para>It is the same estimate the virtualizing panel is itself using — <c>Extent.Height</c> for
    /// a list of unrealised, variable-height items IS an estimate — so it is close, and being close is
    /// all it has to be: the ease is re-aimed from the realised container as soon as there is one.</para>
    /// </summary>
    private double EstimatedOffsetOf(StackupLayerRowViewModel row, ScrollViewer scroll)
    {
        var rows = _subscribedVm?.FilteredStackupLayers;
        int i = rows?.IndexOf(row) ?? -1;
        if (i < 0 || rows!.Count == 0) return scroll.Offset.Y;

        return Math.Clamp(scroll.Extent.Height * i / rows.Count, 0,
                          Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    /// <summary>The one eased scroll of the card list. Held by the view so a second selection cancels
    /// the first one's scroll rather than racing it.</summary>
    private readonly ScrollOffsetAnimator _stackupScroll = new();

    private ScrollViewer? StackupScroller()
        => StackupList?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    /// <summary>Whether <paramref name="row"/>'s card is realised and wholly inside the viewport.
    /// False for a card taller than the pane, which can never be wholly inside one and therefore
    /// always wants its top put at the top.</summary>
    private bool IsFullyVisible(StackupLayerRowViewModel row, ScrollViewer scroll)
    {
        if (StackupList?.ContainerFromItem(row) is not Control container) return false;
        if (container.TranslatePoint(default, scroll) is not { } p) return false;

        return p.Y >= -0.5 && p.Y + container.Bounds.Height <= scroll.Viewport.Height + 0.5;
    }

    /// <summary>
    /// The offset that puts <paramref name="row"/>'s card's own TOP edge at the top of the pane, or
    /// null when its container has not been realised.
    ///
    /// <para><b><c>ScrollIntoView</c> alone is not enough, and the case it gets wrong is the common
    /// one</b> (owner, 2026-09-13: click the bottom entry, then the top one, and only half the card
    /// is showing). <c>ScrollIntoView</c> brings an item MINIMALLY into view, so for an item taller
    /// than the viewport — which a conductor card is, against a pane that now opens at four field rows
    /// (<see cref="TechEditorMetrics.StackupCardPaneOpeningHeight"/>) — it lands the item's BOTTOM at
    /// the viewport's bottom when scrolling up, and the fields at the top of the card are the ones
    /// scrolled away. Clicking a band has to land on that band's fields, so the top is the edge that
    /// matters.</para>
    /// </summary>
    private double? TopOffsetOf(StackupLayerRowViewModel row, ScrollViewer scroll)
    {
        if (StackupList?.ContainerFromItem(row) is not Control container) return null;

        // The container's offset within the SCROLLER's viewport; adding the scroller's own offset
        // gives the position in the content, which is what Offset is measured in.
        if (container.TranslatePoint(default, scroll) is not { } p) return null;

        return Math.Clamp(scroll.Offset.Y + p.Y, 0,
                          Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    /// <summary>
    /// R-stk3-9. Clears the selection — which clears the drawing's outline, the card's shading and
    /// the <c>ListBox</c>'s own selection together, because all three read the one property.
    ///
    /// <para>It does NOT put back a filter the selection cleared, and it scrolls nowhere. Undoing the
    /// filter clear on <c>Esc</c> would make <c>Esc</c> a second undo, which it is not.</para>
    ///
    /// <para><b>Precedence, for brief 4 (R-stk4-6):</b> while an inline editor is open, <c>Esc</c>
    /// reverts the edit and the selection stands; a second <c>Esc</c>, with no editor open, clears the
    /// selection. This handler tunnels, so it gets there first, and it takes BOTH jobs itself.</para>
    /// </summary>
    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;

        // R-stk4-6's first job. THE REVERT IS DONE HERE, not left to the box's own KeyDown handler:
        // Escape is already marked Handled by WorkspaceWindow's KeyBinding by the time it reaches
        // this view (see the registration above), and an ordinary bubbling handler on the box never
        // sees a handled event. The box's own handler stays wired as the documented three-key
        // contract and is a no-op second path — Revert() returns at once when the box is shut, so one
        // Esc reverts once.
        if (StackupDrawing?.InlineEditIsOpen == true)
        {
            StackupInlineEditor.Revert();
            e.Handled = true;
            return;
        }

        // R-stk5-3's third job for this key, between the two above it. A live drag outranks the
        // clear-selection below — Esc mid-drag means "forget this gesture", and clearing the
        // selection as well would throw away the entry the user is still looking at. It cannot
        // collide with the box: a drag and an open editor cannot both be live, because opening the
        // box rebuilds nothing and starting a drag needs a press the box has swallowed.
        if (StackupDrawing?.CancelDrag() == true) { e.Handled = true; return; }

        if (DataContext is not TechDocument doc) return;
        if (doc.ViewModel.SelectedStackupLayerName is null) return;

        doc.ViewModel.ClearStackupSelection();
        e.Handled = true;
    }

    // ── Copy the cross-section (R-stk7-4) ─────────────────────────────────────────────────────────

    /// <summary>The Stackup tab's index in <c>SectionTabs</c> — the same 1 that
    /// <see cref="TargetScrollViewer"/> resolves to <c>StackupList</c>.</summary>
    private const int StackupTabIndex = 1;

    /// <summary>
    /// R-stk7-4's keystroke. Copies the whole cross-section as a picture, exactly as the right-click
    /// menu's Copy does — one implementation, reached two ways.
    ///
    /// <para><b>It only ever sees a keystroke nothing else wanted</b> (see the handler registration
    /// for why this one bubbles while the other two tunnel), and it takes that keystroke only on the
    /// STACKUP tab: this editor has four tabs and the other three have no picture to copy, so
    /// claiming Ctrl+C there would break the plain text copy for no gain.</para>
    ///
    /// <para>The <c>TextBox</c> check is belt and braces rather than a duplicate of the routing rule.
    /// Avalonia's text box only marks Ctrl+C handled when it actually copied something, so a Ctrl+C
    /// typed into a field with nothing selected reaches here — and putting a picture on the clipboard
    /// because the user's selection was empty is not what they asked for.</para>
    /// </summary>
    private async void OnCopyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || StackupDrawing is null) return;
        if (!CopyKeystrokeTakes(e.Key, e.KeyModifiers, SectionTabs?.SelectedIndex ?? -1, e.Source)) return;

        // BEFORE the await, not after: the keystroke has finished routing by the time the copy
        // completes, and marking it handled then would mark nothing.
        e.Handled = true;
        await StackupDrawing.CopyPictureAsync();
    }

    /// <summary>
    /// The decision above, without the event — the same kind of seam <c>StackupCanvas.PressAt</c> and
    /// <c>RightClickAt</c> are, and for the same reason: this test project has no application host to
    /// route a real keystroke through, and the routing is not the part that could be got wrong.
    /// </summary>
    internal static bool CopyKeystrokeTakes(Key key, KeyModifiers modifiers, int tabIndex, object? source)
    {
        if (key != Key.C) return false;
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0) return false;

        // Ctrl+Shift+C and Ctrl+Alt+C are other gestures in other applications and are not this one.
        if ((modifiers & (KeyModifiers.Shift | KeyModifiers.Alt)) != 0) return false;

        // This editor has four tabs and only one of them has a picture to copy. Claiming Ctrl+C on
        // the other three would break the plain text copy there for no gain.
        if (tabIndex != StackupTabIndex) return false;

        return source is not (TextBox or SelectableTextBlock);
    }

    /// <summary>Focus follows a click on the cross-section, so that the keys this view handles reach
    /// it. It changes nothing about the click itself — the canvas has already selected, armed its
    /// drag and marked the event handled by the time this runs.</summary>
    private void OnStackupDrawingPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(StackupDrawing).Properties.IsLeftButtonPressed) return;

        // Not while the inline editor is open: the box is the one focusable thing inside the drawing
        // and it was just given focus for the user to type into. Its own LostFocus commit is what a
        // click elsewhere means, and the canvas raises that by hiding the box, not by this.
        if (StackupDrawing.InlineEditIsOpen) return;

        Focus();
    }

    // ── Delete removes the selected stackup entry (owner, 2026-09-13) ─────────────────────────────

    /// <summary>
    /// The keystroke half of a deletion that already exists. <c>TechEditorViewModel</c>'s
    /// <c>RemoveStackupLayer</c> is what the card's ✕ and the drawing's <b>Delete Conductor /
    /// Dielectric / Via</b> already call, and it is what this reaches — through
    /// <c>DeleteSelectedStackupLayer</c>, which is only the by-name lookup a keystroke needs and a
    /// pointer gesture does not. One deletion, one undo entry, three ways in; no second path to
    /// drift.
    ///
    /// <para><b>It acts on the SELECTION, wherever the selection was made</b> — the drawing or a
    /// card. The two surfaces share one selection held by the view model (R-stk3-1/R-stk3-7), so
    /// "the selected entry" is a single unambiguous thing, and the Esc handler below covers both
    /// surfaces for the same reason.</para>
    ///
    /// <para>Nothing selected is NOT handled: the key falls through unmarked rather than being
    /// silently swallowed by a view with nothing to delete.</para>
    /// </summary>
    private void OnDeleteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (!DeleteKeystrokeTakes(e.Key, e.KeyModifiers, SectionTabs?.SelectedIndex ?? -1, e.Source)) return;

        // An open inline editor owns both keys outright — inside a value being typed they are text
        // editing. The box is a TextBox and the source test above already turns it away; this is the
        // belt to that brace, and it is the same thing OnEscapeKeyDown checks first.
        if (StackupDrawing?.InlineEditIsOpen == true) return;

        // Mid-drag the key means nothing yet: the entry under the pointer has not landed anywhere,
        // and deleting it out from under a gesture the user is still making is not what they asked
        // for. Esc is how a drag is abandoned (R-stk5-3); this simply stays out of its way.
        if (StackupDrawing is { DragKind: not StackupDragKind.None }) return;

        if (DataContext is not TechDocument doc) return;
        if (!doc.ViewModel.DeleteSelectedStackupLayer()) return;

        e.Handled = true;
    }

    /// <summary>
    /// The decision above without the event — the same seam <see cref="CopyKeystrokeTakes"/> is, and
    /// for the same reason: this test project has no application host to route a real keystroke
    /// through, and the routing is not the part that could be got wrong.
    /// </summary>
    internal static bool DeleteKeystrokeTakes(Key key, KeyModifiers modifiers, int tabIndex, object? source)
    {
        // BOTH spellings, as every other editor in this application takes them (SchematicViewModel,
        // SymbolEditorViewModel, LayoutEditorViewModel, HarmonicaCanvas, WBondLayoutOverlay): the key
        // a Mac keyboard labels "delete" is Back, and Key.Delete is the forward-delete above it. On
        // that keyboard, taking only Key.Delete would mean the feature did nothing for the key the
        // owner actually asked about.
        if (key is not (Key.Delete or Key.Back)) return false;

        // Bare only. Ctrl/Meta/Alt/Shift+Delete are other gestures in other applications — and a
        // deletion with no confirmation dialog is not one to take on a near miss.
        if (modifiers != KeyModifiers.None) return false;

        // This editor has four tabs and only one of them has a stackup. The layer table and the DRC
        // rules have their own rows and their own selection, and claiming the key there would delete
        // something the user was not looking at.
        if (tabIndex != StackupTabIndex) return false;

        // A field editing text owns both keys, and — the reason this check is not merely a duplicate
        // of the routing rule — Avalonia's TextBox does not always mark them handled: Back with the
        // caret at the start and nothing selected, or Delete at the end, deletes no character and
        // leaves the key to bubble. Routing alone would therefore delete a stackup entry while
        // someone was typing in a card.
        return source is not (TextBox or SelectableTextBlock);
    }

    // ── The cross-section's context menu (R-stk6-1) ───────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the ONE menu's items, fresh, on every opening.
    ///
    /// <para><b>Fresh, and never reused.</b> Reusing item instances across openings and re-subscribing
    /// their <c>Click</c> would fire an action N times on the Nth opening — the exact mistake the
    /// one-instance pattern exists to prevent, and the one the Layout Editor already paid for.</para>
    ///
    /// <para>No pending target means no right-click reached the drawing (the menu was opened some
    /// other way, or a target was already consumed), so the opening is CANCELLED rather than shown
    /// with whatever the previous one built.</para>
    /// </summary>
    private void OnStackupContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (StackupDrawing?.ConsumeContextMenuTarget() is not { } p) { e.Cancel = true; return; }
        if (sender is ContextMenu menu) menu.ItemsSource = StackupDrawing.BuildContextMenuItems(p);
    }

    private void OnActivationFocusRequested()
    {
        _subscribedDoc?.ConsumeActivationFocus();
        FocusForScrollingDeferred();
    }

    /// <summary>
    /// Takes keyboard focus for the editor as a whole, so Page Up/Down reach
    /// <see cref="OnScrollKeyDown"/>.
    ///
    /// <para><b>The view itself, not the visible tab's list</b>, and not a field in a row. Focusing
    /// the list would make the FIRST thing the user sees a control with a selection, in lists whose
    /// rows are deliberately flattened so selection is invisible; focusing a row's text box would put
    /// a caret in an editable process value nobody asked to edit. Focusing the view lands on
    /// <see cref="TargetScrollViewer"/>'s own documented fallback — the visible tab's row list —
    /// which already resolves correctly for whichever tab is showing, including after a tab change.</para>
    ///
    /// <para>Deferred to Background priority for the same reason every other view here defers it: on
    /// first open the visual tree is still being realized and a synchronous Focus() lands on a
    /// control that has not been attached yet. <c>IsTabStop="False"</c> keeps this out of the Tab
    /// order — it is a programmatic focus target, never a stop the user cycles through.</para>
    /// </summary>
    /// <summary>
    /// Undocking is the same dead keyboard by a different route.
    ///
    /// <para>Floating the editor builds a NEW window around this view, and a new window's activation
    /// is not the dock's activation — no <c>IActivatableDocument</c> request fires, so the hook above
    /// never runs, nothing inside the view holds focus, and Page Up/Down are dead again until
    /// something is clicked. Attaching to a visual tree is the one event both routes share.</para>
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusForScrollingDeferred(onlyIfUnclaimed: true);
    }

    /// <param name="onlyIfUnclaimed">Take focus only when nothing else already holds it. The
    /// activation hook may pass false because an explicit activation IS the claim; an attach may
    /// not, because a view can be re-attached by an ordinary dock rearrangement while the user is
    /// typing somewhere else entirely, and yanking the caret out of another panel would be a worse
    /// bug than the one being fixed.</param>
    private void FocusForScrollingDeferred(bool onlyIfUnclaimed = false) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Evaluated inside the posted action, not before it: on the undock path the view is
            // still moving between windows when the attach fires, so the top level asked any earlier
            // is the one being left rather than the one being entered.
            if (onlyIfUnclaimed && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is { } held
                && held is not TopLevel)
                return;

            Focus();
        }, DispatcherPriority.Background);

    // ── Page Up / Page Down / Home / End over the row lists ────────────────────

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown owns all four keys — it is navigating its own items, and the list behind
        // it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        // R-stk2-10, which brief 4 is the first thing to test. The inline edit box is the ONLY
        // focusable control inside the drawing's ScrollViewer, so without this line Page Up/Down
        // typed into an open editor would resolve to the DRAWING's scroller and scroll the label the
        // box is sitting on out of the pane — the exact quiet re-pointing R-stk2-10 exists to
        // prevent. Home and End are already excused for any text input by PanelScrollKeys.ActionFor;
        // these two are not, because a row's text box is not something anyone pages through.
        if (ReferenceEquals(e.Source, StackupInlineEdit)) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        var scroll = TargetScrollViewer(e.Source);
        if (scroll is null) return;

        PanelScrollKeys.Apply(action.Value, scroll);
        e.Handled = true;
    }

    /// <summary>
    /// The scroller the keystroke belongs to: the one the focused control is INSIDE, if any — which
    /// is the row list when focus is in a row, and the Stackup tab's own drawing-layer picker when
    /// focus is in that — falling back to the visible tab's row list, which is where focus sits when
    /// the user has just typed in the filter box (that box is deliberately outside the list).
    /// </summary>
    private ScrollViewer? TargetScrollViewer(object? source)
    {
        if (source is Visual v && v.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() is { } inner)
            return inner;

        var list = SectionTabs?.SelectedIndex switch
        {
            0 => LayersList,
            1 => StackupList,
            2 => DrcRulesList,
            3 => InterchangeList,
            _ => null,
        };
        return list?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e) => CommitField(sender);

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitField(sender);
            e.Handled = true;
        }
    }

    private static void CommitField(object? sender)
    {
        if (sender is not Control c) return;
        var tag = c.Tag as string;

        switch (c.DataContext)
        {
            case LayerRowViewModel lr:
                switch (tag)
                {
                    case "Name":        lr.CommitName();        break;
                    case "LayerNumber": lr.CommitLayerNumber(); break;
                    case "Datatype":    lr.CommitDatatype();    break;
                    case "FillOpacity": lr.CommitFillOpacity(); break;
                    case "ZOrder":      lr.CommitZOrder();      break;
                    case "Purpose":     lr.CommitPurpose();     break;
                    case "GdsiiLayer":         lr.CommitGdsiiLayer();         break;
                    case "GdsiiDatatype":      lr.CommitGdsiiDatatype();      break;
                    case "DxfLayerName":       lr.CommitDxfLayerName();       break;
                    case "GerberSuffix":       lr.CommitGerberSuffix();       break;
                    case "GerberFileFunction": lr.CommitGerberFileFunction(); break;
                }
                break;

            case StackupLayerRowViewModel sr:
                switch (tag)
                {
                    case "Name":      sr.CommitName();      break;
                    case "Thickness": sr.CommitThickness(); break;
                    case "Epsr":      sr.CommitEpsr();      break;
                    case "TanD":      sr.CommitTanD();      break;
                    case "Mur":       sr.CommitMur();       break;
                    case "Sigma":     sr.CommitSigmaSm();   break;
                    case "WallThickness": sr.CommitWallThickness(); break;
                }
                break;

            case DrcRuleRowViewModel dr:
                switch (tag)
                {
                    case "Name":     dr.CommitName();     break;
                    case "Value":    dr.CommitValue();    break;
                    case "RegionA":  dr.CommitRegionA();  break;
                    case "RegionB":  dr.CommitRegionB();  break;
                    case "Window":   dr.CommitWindow();   break;
                    case "MinRatio": dr.CommitMinRatio(); break;
                    case "MaxRatio": dr.CommitMaxRatio(); break;
                }
                break;
        }
    }

    // ── The Name column's width (owner, 2026-09-17) ──────────────────────────
    // Dragging the grip in the Name header (see the `Border.colgrip` style) sets the first column
    // of all three grids that make up the layer table — the filter row, the header row and every
    // row of the list — to the same explicit width, which is what carries the columns beside it
    // left and right. The width is deliberately VIEW state and nothing else: it is not on the view
    // model, not in the document and not saved, because it is a way to read a long name for a
    // moment rather than a property of the technology. Closing the editor forgets it, which is the
    // same answer double-clicking the grip gives.
    //
    // Null means the DEFAULT — the star width the column has today, whatever the window is
    // currently giving it. It is kept as "no width" rather than as the number that star resolves
    // to, so that until someone drags the grip the column still grows and shrinks with the window.

    /// <summary>The column's own MinWidth. Below this the Grid would clamp anyway; clamping here as
    /// well is what stops the drag accumulating a width the user then has to drag back through.</summary>
    private const double LayerNameMinWidth = 110;

    /// <summary>Far wider than any layer name, and short of the point where the drag would push the
    /// whole table off the right edge with nothing left on screen to drag back.</summary>
    private const double LayerNameMaxWidth = 600;

    private static readonly GridLength LayerNameDefaultWidth = new(1, GridUnitType.Star);

    private double? _layerNameWidth;
    private double? _gripDragOriginX;
    private double  _gripDragBaseWidth;

    private void OnLayerNameGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border grip) return;

        // Double-click restores the default. Taken here rather than through DoubleTapped because the
        // press below captures the pointer, and a gesture that needs a second press to arrive at the
        // same control is the kind of thing capture quietly changes.
        if (e.ClickCount >= 2)
        {
            _gripDragOriginX = null;
            _layerNameWidth  = null;
            ApplyLayerNameColumnWidth();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        // Measured against the HEADER GRID, not the grip: the grip moves as the column widens, so a
        // delta read in its own coordinate space would be measuring itself.
        _gripDragOriginX   = e.GetPosition(LayerHeaderColumns).X;
        _gripDragBaseWidth = LayerHeaderColumns.ColumnDefinitions[0].ActualWidth;
        e.Pointer.Capture(grip);
        e.Handled = true;
    }

    private void OnLayerNameGripMoved(object? sender, PointerEventArgs e)
    {
        if (_gripDragOriginX is not { } origin) return;

        var width = _gripDragBaseWidth + (e.GetPosition(LayerHeaderColumns).X - origin);
        _layerNameWidth = Math.Clamp(width, LayerNameMinWidth, LayerNameMaxWidth);
        ApplyLayerNameColumnWidth();
        e.Handled = true;
    }

    private void OnLayerNameGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_gripDragOriginX is null) return;
        _gripDragOriginX = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>A row realized after the drag — the list is virtualized, so scrolling builds rows
    /// that were never on screen while the grip was moving — is born at the current width rather
    /// than at the default the template declares.</summary>
    private void OnLayerRowColumnsLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Grid g) SetNameColumnWidth(g, CurrentLayerNameWidth);
    }

    private GridLength CurrentLayerNameWidth =>
        _layerNameWidth is { } w ? new GridLength(w, GridUnitType.Pixel) : LayerNameDefaultWidth;

    private void ApplyLayerNameColumnWidth()
    {
        var width = CurrentLayerNameWidth;
        SetNameColumnWidth(LayerFilterColumns, width);
        SetNameColumnWidth(LayerHeaderColumns, width);

        // Every realized row, found by the tag its template carries. Only the rows on screen exist,
        // so this is a few dozen grids however many layers the technology has.
        foreach (var row in LayersList.GetVisualDescendants().OfType<Grid>())
            if (row.Tag as string == "LayerRowColumns")
                SetNameColumnWidth(row, width);
    }

    private static void SetNameColumnWidth(Grid grid, GridLength width)
    {
        if (grid.ColumnDefinitions.Count > 0) grid.ColumnDefinitions[0].Width = width;
    }

    private void OnComboSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Control c) return;
        var tag = c.Tag as string;

        if (c.DataContext is DrcRuleRowViewModel dr)
        {
            switch (tag)
            {
                case "Kind":     dr.CommitKind();     break;
                case "Layer":    dr.CommitLayer();    break;
                case "Severity": dr.CommitSeverity(); break;
                case "NetScope": dr.CommitNetScope(); break;
            }
        }
    }
}
