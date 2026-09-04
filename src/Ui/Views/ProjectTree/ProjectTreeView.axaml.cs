using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Views.ProjectTree;

public partial class ProjectTreeView : UserControl
{
    // ── Cell drag source state + double-click tracking ────────────────────────

    private PointerPressedEventArgs?     _cellPressArgs;
    private Point                        _cellPressPos;
    private const double                 DragThreshold = 5.0;

    // Manual double-click detection — avoids Avalonia's DoubleTapped ~200ms
    // single-tap confirmation delay by tracking two rapid PointerPressed events
    // on the same node ourselves.
    private ProjectTreeNodeViewModel? _lastPressVm;
    private long                      _lastPressTick;        // Environment.TickCount64
    private const int                 DoubleClickMs = 400;

    public ProjectTreeView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent,  OnFileDragOver);
        // DragENTER matters as much as DragOver here. When the resolved drag target changes — which
        // it does constantly, since every control in the tree area now accepts drops — Avalonia
        // raises DragLeave on the old target and DragEnter on the new one, and NO DragOver until the
        // pointer moves again. Handling only DragOver therefore left the highlight cleared by the
        // leave with nothing to restore it, for as long as the cursor sat still.
        AddHandler(DragDrop.DragEnterEvent, OnFileDragOver);
        AddHandler(DragDrop.DropEvent,      OnFileDrop);
        // Without this the highlight survives a drag that wandered off the panel, so the tree goes
        // on claiming a destination for a gesture that is over. See OnDragLeave for why it cannot
        // simply clear.
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);

        // Drag source: cell nodes in the tree. Subscribe on TheTreeView so only
        // drags originating from within the tree are candidates.
        TheTreeView.AddHandler(PointerPressedEvent,  OnTreePointerPressed,  handledEventsToo: true);
        TheTreeView.AddHandler(PointerMovedEvent,    OnTreePointerMoved,    handledEventsToo: false);
        TheTreeView.AddHandler(PointerReleasedEvent, OnTreePointerReleased, handledEventsToo: true);

        // Selecting a row must not drag the HORIZONTAL scroll (owner, 2026-08-25). Subscribed on the
        // TreeView rather than on this control because RequestBringIntoView only BUBBLES (probed —
        // it has no tunnel route), and the TreeView sits below TreeScroll, so a handler here runs
        // before the ScrollViewer's own.
        TheTreeView.AddHandler(RequestBringIntoViewEvent, OnTreeBringIntoView);

        // Page Up / Page Down / Home / End scroll the listing. Tunnelled, for the same reason the
        // .ctech editor's own handler is: the focused TreeViewItem would otherwise move the SELECTION
        // on Home/End first and swallow the key, and a moved selection is not what was asked for here
        // — a workspace with a board import in it is long, and paging through it must not change what
        // is selected on the way past.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);

        // Opening the search has to put the caret in the field — a magnifier button that reveals a
        // box you then have to click is two gestures for one intent.
        DataContextChanged += OnDataContextChangedForSearch;

        // …and activating the PANEL has to put focus somewhere inside it, or the scroll keys below
        // never reach this control at all. See OnActivationFocusRequested.
        DataContextChanged += OnDataContextChangedForActivation;

        // Escape closes the search (which clears it, so the tree comes back).
        //
        // handledEventsToo: true is REQUIRED, not defensive, and leaving it off is why Escape did
        // nothing at first (owner, 2026-08-25). WorkspaceWindow.axaml binds Escape to
        // DisarmPlacementCommand, and Window.KeyBindings are processed before visual-tree routing and
        // always mark the event Handled — so a handler that skips handled events never sees Escape at
        // all. SchematicView, SymbolEditorView and LayoutEditorView all carry the identical argument
        // on their own Escape handlers; this panel is the fourth to need it.
        AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // ── Activation focus (owner, 2026-08-25) ──────────────────────────────────

    private IActivatableTool? _activationTool;

    private void OnDataContextChangedForActivation(object? sender, EventArgs e)
    {
        if (_activationTool is not null) _activationTool.ActivationFocusRequested -= OnActivationFocusRequested;
        _activationTool = DataContext as IActivatableTool;
        if (_activationTool is null) return;

        _activationTool.ActivationFocusRequested += OnActivationFocusRequested;

        // The panel can be activated BEFORE this view exists — first layout, or a restored dock
        // arrangement. The request is held until a view turns up to consume it, exactly as
        // IActivatableDocument does for document tabs.
        if (_activationTool.ConsumeActivationFocus()) OnActivationFocusRequested();
    }

    /// <summary>
    /// Puts keyboard focus inside this panel when it is activated.
    ///
    /// <para><b>Owner:</b> <i>"if I click on the title bar of a window (like Project, or Library) …
    /// I am forced to click somewhere inside the window before the keystrokes will register."</i>
    /// Clicking a tool's tab leaves focus on the TAB, which is Dock's chrome and lives outside this
    /// control — so a key event's route never passes through here and
    /// <see cref="OnScrollKeyDown"/> is never called.</para>
    ///
    /// <para>The TreeView is preferred over the scroller because it takes the arrow keys too, so an
    /// activated panel is fully navigable; the scroller is the fallback for the states where the tree
    /// cannot take focus (no workspace open, so it is not even visible).</para>
    /// </summary>
    /// <para>Declined when focus already sits inside this panel — activation fires for a click
    /// ANYWHERE in it, the Search box included, and this grab is posted, so it would otherwise land
    /// after the click gave the box the caret and take it straight back (owner, 2026-08-26, against
    /// the Library palette; the same shape is here). See <see cref="PanelActivationFocus"/>.</para>
    private void OnActivationFocusRequested() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (PanelActivationFocus.AlreadyInside(this)) return;
            if (!TheTreeView.Focus()) TreeScroll.Focus();
        }, Avalonia.Threading.DispatcherPriority.Input);

    // ── Bring-into-view: vertical only ────────────────────────────────────────

    /// <summary>
    /// Clicking a row focuses it, and Avalonia's focus handling asks the nearest scroller to bring it
    /// into view — on BOTH axes. With horizontal scrolling enabled (§3.1) a row wider than the panel is
    /// never fully "in view", so simply selecting a long cell name nudged the horizontal scrollbar
    /// (owner, 2026-08-25). Vertical bring-into-view is wanted and keeps working: arrow-key navigation
    /// still scrolls the selected row into sight.
    ///
    /// <para><c>ScrollViewer.BringIntoViewOnFocusChange="False"</c> would have been the one-liner and is
    /// the wrong tool — it kills both axes, taking keyboard navigation with it.</para>
    ///
    /// <para>The rect arrives in the TARGET's coordinate space, and the transform from a tree row to the
    /// scroller is a pure translation, so placing a zero-width rect at <c>-tx</c> maps it to x = 0 in the
    /// scroller's space — the left edge of the viewport, which is by definition already visible. Nothing
    /// to scroll to horizontally; Y and Height are passed through untouched.</para>
    /// </summary>
    private void OnTreeBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not Visual target) return;
        if (target.TransformToVisual(TreeScroll) is not { } toScroll) return;

        double tx = toScroll.Transform(new Point(0, 0)).X;
        e.TargetRect = new Rect(-tx, e.TargetRect.Y, 0, e.TargetRect.Height);
    }

    // ── Search field focus + Escape ───────────────────────────────────────────

    private ProjectTreeTool? _searchTool;

    private void OnDataContextChangedForSearch(object? sender, EventArgs e)
    {
        if (_searchTool is not null) _searchTool.PropertyChanged -= OnToolPropertyChangedForSearch;
        _searchTool = DataContext as ProjectTreeTool;
        if (_searchTool is not null) _searchTool.PropertyChanged += OnToolPropertyChangedForSearch;
    }

    private void OnToolPropertyChangedForSearch(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProjectTreeTool.IsSearchOpen)) return;
        if (_searchTool is not { } tool) return;

        if (tool.IsSearchOpen)
        {
            // Posted: the box is only just becoming visible, and a control that has not been laid out
            // yet cannot take focus.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => SearchBox.Focus(), Avalonia.Threading.DispatcherPriority.Input);
            return;
        }

        // Closing leaves the caret inside a control that is no longer on screen — which both swallows
        // subsequent keystrokes and makes OnScrollKeyDown read `e.Source is TextBox` as true, so
        // Home/End would go on yielding to a field the user cannot see. Hand focus back to the tree,
        // which is where the keys are meant to land once the search is put away.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => TheTreeView.Focus(), Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not ProjectTreeTool { IsSearchOpen: true } tool) return;

        tool.IsSearchOpen = false;   // clears the query on its way (see OnIsSearchOpenChanged)
        e.Handled = true;
    }

    // ── Page Up / Page Down / Home / End over the tree ────────────────────────

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown owns all four keys — it is navigating its own items, and the list behind
        // it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        PanelScrollKeys.Apply(action.Value, TreeScroll);
        e.Handled = true;
    }

    // ── Cell drag SOURCE ─────────────────────────────────────────────────────

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _cellPressArgs = null;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var source = e.Source as Visual;

        // Clicks on the expander ToggleButton (disclosure triangle) are for expand/collapse only —
        // exclude them from double-click activation tracking so rapid expand→collapse doesn't
        // accidentally open a document.
        if (IsOnExpander(source)) return;

        // Double-click detection: two left-presses on the same node within DoubleClickMs.
        var vm   = GetAnyNodeFromSource(source);
        var tick = Environment.TickCount64;
        if (vm is not null && vm == _lastPressVm && tick - _lastPressTick <= DoubleClickMs)
        {
            _lastPressVm   = null;       // reset so a third click doesn't re-fire
            _lastPressTick = 0;
            vm.ActivateCommand.Execute(null);
            return;
        }
        _lastPressVm   = vm;
        _lastPressTick = tick;

        // Cell DnD source capture; (R-dd-3) .npy file DnD source capture — dragging a data file from
        // the tree onto a Data Display plot adds it as a dataset in one motion, the same idiom as
        // palette→schematic/palette→layout drag-drop; and (MW3 §5) any other loose file, which is
        // draggable only so it can be dropped on ANOTHER workspace's tree.
        if (GetCellNodeFromSource(source) is null
         && GetNpyFileNodeFromSource(source) is null
         && GetLooseFileNodeFromSource(source) is null
         && GetFolderNodeFromSource(source) is null) return;
        _cellPressArgs = e;
        _cellPressPos  = e.GetPosition(this);
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_cellPressArgs is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _cellPressArgs = null;
            return;
        }

        var pos   = e.GetPosition(this);
        var delta = pos - _cellPressPos;
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold) return;

        var source    = _cellPressArgs.Source as Visual;
        var cellVm    = GetCellNodeFromSource(source);
        var npyVm     = cellVm is null ? GetNpyFileNodeFromSource(source) : null;
        var fileVm    = cellVm is null && npyVm is null ? GetLooseFileNodeFromSource(source) : null;
        // TM1 R-tm1-7 — a user FOLDER is draggable only now that a drop inside its own workspace
        // does something. It carries no cross-workspace meaning and gets no other payload.
        var folderVm  = cellVm is null && npyVm is null && fileVm is null
                      ? GetFolderNodeFromSource(source) : null;
        var savedArgs = _cellPressArgs;
        _cellPressArgs = null; // clear before await to prevent re-entry
        if (cellVm is null && npyVm is null && fileVm is null && folderVm is null) return;

        // The .npy payload stays what it was: the Data Display reads that format, and giving a data
        // file a second spelling for the sake of MW3 would break the drop it already serves. The
        // tree's own drop handler accepts BOTH file payloads instead — one gesture, two readers.
        string serialized =
            cellVm   is not null ? new CellDragPayload(cellVm.AbsolutePath).Serialize()
          : npyVm    is not null ? new NpyFileDragPayload(npyVm.AbsolutePath).Serialize()
          : fileVm   is not null ? new WorkspaceFileDragPayload(fileVm.AbsolutePath).Serialize()
          :                        new FolderDragPayload(folderVm!.AbsolutePath).Serialize();

        var transferItem = new DataTransferItem();
        transferItem.Set(DataFormat.Text, serialized);
        var transfer = new DataTransfer();
        transfer.Add(transferItem);
        // Copy AND Move. `allowedEffects` is a MASK, not a default: the platform intersects it with
        // whatever the drop target answers, so a target asking for Move against a Copy-only source
        // resolves to None — no cursor badge and no drop delivered, with nothing logged anywhere.
        // TM1's in-workspace move is the first target here to answer anything but Copy.
        await DragDrop.DoDragDropAsync(
            savedArgs, transfer, DragDropEffects.Copy | DragDropEffects.Move);
    }

    private void OnTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _cellPressArgs = null;

    // Returns true when the visual is on the expander ToggleButton (the disclosure triangle),
    // so we can exclude those clicks from double-click activation tracking.
    private static bool IsOnExpander(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is ToggleButton) return true;
            if (v is TreeViewItem)  return false;   // reached row content — not the expander
            v = v.GetVisualParent();
        }
        return false;
    }

    // Walk upward to the nearest TreeViewItem with any ProjectTreeNodeViewModel.
    private static ProjectTreeNodeViewModel? GetAnyNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem { DataContext: ProjectTreeNodeViewModel vm })
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk the visual tree upward from the event source to find a cell TreeViewItem.
    private static ProjectTreeNodeViewModel? GetCellNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem item
                && item.DataContext is ProjectTreeNodeViewModel { Kind: NodeKind.Cell } vm)
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk the visual tree upward from the event source to find an .npy file TreeViewItem
    // (R-dd-3) — .npy files classify as NodeKind.OtherFile (no dedicated NodeKind exists for them).
    private static ProjectTreeNodeViewModel? GetNpyFileNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem item
                && item.DataContext is ProjectTreeNodeViewModel { Kind: NodeKind.OtherFile } vm
                && string.Equals(Path.GetExtension(vm.AbsolutePath), ".npy", StringComparison.OrdinalIgnoreCase))
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk upward to a loose FILE node — anything the tree shows that is a file and is not part of a
    // cell's own views. Draggable only for MW3 §5: dropped on another workspace's tree it is copied
    // in, and dropped on its own it does nothing, exactly as before.
    private static ProjectTreeNodeViewModel? GetLooseFileNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem { DataContext: ProjectTreeNodeViewModel vm }
                && vm.Kind is NodeKind.OtherFile or NodeKind.KnownFile or NodeKind.DataDisplayFile
                           or NodeKind.TechFile or NodeKind.ColorThemeFile or NodeKind.EmSetupFile
                           or NodeKind.WBondFile or NodeKind.HarmonicaFile
                && File.Exists(vm.AbsolutePath))
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // Walk upward to a user FOLDER node (TM1 R-tm1-7). NodeKind.UserFolder only: the workspace root,
    // a cell, a cell's view sub-folder and every synthetic group node are all excluded by
    // TreeMove.IsMovable, which is the ONE place that list is written down.
    private static ProjectTreeNodeViewModel? GetFolderNodeFromSource(Visual? source)
    {
        var v = source;
        while (v is not null)
        {
            if (v is TreeViewItem { DataContext: ProjectTreeNodeViewModel vm }
                && vm.Kind == NodeKind.UserFolder
                && TreeMove.IsMovable(vm.Kind)
                && Directory.Exists(vm.AbsolutePath))
                return vm;
            v = v.GetVisualParent();
        }
        return null;
    }

    // ── Drag-drop receive (file drop onto tree) ──────────────────────────────

    // MW3 R-mw3-3: ONE AllowDrop surface and ONE pair of handlers. The tree already answered
    // DragDropEffects.None to anything that was not an OS file list; these two learn the
    // cross-workspace payloads rather than adding a second drop path, because a second path is the
    // one that gets missed when a third payload arrives.

    private void OnFileDragOver(object? sender, DragEventArgs e)
    {
        _dragLeaveGeneration++;   // the drag is still here — cancel any pending clear (OnDragLeave)

        var intent = PayloadIntent(e);

        // TM1 R-tm1-14: a move inside one workspace says Move, and a copy across two says Copy.
        // That is free platform-native feedback — the cursor badge, on all three operating systems —
        // and it is the difference the user most needs to see.
        if (intent.Action == TreeDropAction.Move)
        {
            var move = MoveIntentFor(intent.Path, e);
            ShowDropTarget(move.Permitted ? move.DestFolder : null);
            e.DragEffects = move.Permitted ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled     = true;
            return;
        }

        ShowDropTarget(null);
        if (intent.Action != TreeDropAction.None || HasDroppedPaths(e))
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        else
            e.DragEffects = DragDropEffects.None;
    }

    /// <summary>The move rule, asked identically by <see cref="OnFileDragOver"/> and
    /// <see cref="OnFileDrop"/> so the cursor and the outcome are one decision (TM1 §8 gate 7 asserts
    /// it directly, not through this view).</summary>
    private TreeMoveIntent MoveIntentFor(string sourcePath, DragEventArgs e)
    {
        var tool = DataContext as ProjectTreeTool;
        return TreeMove.For(
            sourcePath, TreeMove.ClassifyForMove(sourcePath),
            DropFolderFor(e), tool?.WorkspaceRootDir);
    }

    /// <summary>
    /// The destination folder for a drag at its current position — the row the cursor is on, put
    /// through <see cref="FolderOf"/>. Both handlers ask through here.
    ///
    /// <para><b><c>e.Source</c> is not the row and cannot be.</b> A drag event is raised against the
    /// element carrying <c>AllowDrop</c>, which here is this whole <c>UserControl</c> — the single
    /// drop surface R-mw3-3 requires. Every drop then resolves to the workspace root: a
    /// cross-workspace copy still lands, in the wrong folder, and a MOVE of a root-level cell reads
    /// as "already there", which shows no highlight, no cursor badge and does nothing at all. It is
    /// kept only as a last resort.</para>
    /// </summary>
    private string? DropFolderFor(DragEventArgs e)
        => FolderOf(RowAt(e) ?? GetAnyNodeFromSource(e.Source as Visual));

    /// <summary>
    /// The row a drag is over, found by its VERTICAL position alone.
    ///
    /// <para><b>A hit test at the cursor is the obvious implementation and it flickers</b>
    /// (owner-reported, 2026-09-03: the destination highlight flashed while moving the cursor
    /// horizontally). §3.1 gives this tree horizontal scrolling, so a row is only as WIDE as its own
    /// label — past the end of the text there is no row under the pointer at all, and the hit lands
    /// on an ancestor or on nothing. The answer alternated between the right folder and the
    /// workspace root as the cursor moved in x, on a row that never changed. A row is picked by y
    /// because that is what actually identifies it.</para>
    ///
    /// <para><b>The deepest match wins, and "deepest" is the greatest top.</b> A
    /// <c>TreeViewItem</c>'s bounds INCLUDE its expanded children, so every ancestor of a row also
    /// spans that row's y. The row itself is the last one to start above the cursor.</para>
    /// </summary>
    private ProjectTreeNodeViewModel? RowAt(DragEventArgs e)
    {
        double y;
        try   { y = e.GetPosition(TheTreeView).Y; }
        catch { return null; }

        ProjectTreeNodeViewModel? best = null;
        double bestTop = double.NegativeInfinity;

        foreach (var item in TheTreeView.GetVisualDescendants().OfType<TreeViewItem>())
        {
            if (item.DataContext is not ProjectTreeNodeViewModel vm) continue;
            if (item.TransformToVisual(TheTreeView) is not { } toTree) continue;

            double top = toTree.Transform(new Point(0, 0)).Y;
            if (y < top || y >= top + item.Bounds.Height) continue;

            if (top > bestTop) { bestTop = top; best = vm; }
        }

        return best;
    }

    private void ShowDropTarget(string? folder)
    {
        if (DataContext is ProjectTreeTool tool) tool.DropTargetFolder = folder;
    }

    private int _dragLeaveGeneration;

    /// <summary>
    /// Clears the drop highlight when a drag really leaves this panel.
    ///
    /// <para><b>A DragLeave is not proof the drag has gone anywhere</b> (owner-reported flashing
    /// highlight, 2026-09-03). Avalonia raises one whenever the resolved drag TARGET changes,
    /// including to nothing at all — and a hit test finds nothing wherever this panel has no
    /// hit-testable surface. The AXAML side of that is fixed, but a handler that clears the moment it
    /// is told to is one unhit pixel away from flashing again, so it does not: the clear is POSTED,
    /// and the next <see cref="OnFileDragOver"/> cancels it. A real leave is a leave with no
    /// drag-over behind it, and that is what this waits to find out.</para>
    /// </summary>
    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        int generation = ++_dragLeaveGeneration;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { if (generation == _dragLeaveGeneration) ShowDropTarget(null); },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProjectTreeTool tool) return;

        ShowDropTarget(null);

        var intent = PayloadIntent(e);
        if (intent.Action == TreeDropAction.Move)
        {
            string? destFolder = DropFolderFor(e);
            string  source     = intent.Path;
            RunAfterDragCompletes(() => tool.MoveInsideWorkspaceAsync(source, destFolder));
            return;
        }

        if (intent.Action is TreeDropAction.Cell or TreeDropAction.File)
        {
            string? destFolder = DropFolderFor(e);
            RunAfterDragCompletes(intent.Action == TreeDropAction.Cell
                ? () => tool.AcceptCellFromOtherWorkspaceAsync(intent.Path, destFolder)
                : () => tool.AcceptDroppedFileAsync(intent.Path, destFolder));
            return;
        }

        foreach (var path in ExtractDroppedPaths(e))
        {
            var dropped = TreeDrop.ForDroppedPath(path);
            if (dropped.Action == TreeDropAction.OpenWorkspace)
            {
                var cws = dropped.Path;
                RunAfterDragCompletes(() =>
                {
                    ViewModels.WorkspaceViewModel.OpenDroppedWorkspace(cws);
                    return Task.CompletedTask;
                });
                continue;
            }
            tool.AddKnownFile(path);
        }
    }

    /// <summary>The shipped rule, asked identically by DragOver and Drop so the effect the cursor
    /// promises and the thing that happens cannot drift apart. <see cref="TreeDrop"/> holds it.</summary>
    private TreeDropIntent PayloadIntent(DragEventArgs e) =>
        TreeDrop.ForPayload(TextPayloadOf(e), (DataContext as ProjectTreeTool)?.WorkspaceRootDir);

    private static string? TextPayloadOf(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.Text) is string s && s.Length > 0)
                return s;
        return null;
    }

    /// <summary>The folder a drop landed on. Null falls back to the workspace root, and the
    /// receiving workspace clamps anything outside itself.</summary>
    /// <summary>The rule itself: a folder node is itself, a CELL node is its parent (a cell folder
    /// holds views, not cells), a file is its own directory.</summary>
    private static string? FolderOf(ProjectTreeNodeViewModel? node)
    {
        if (node is null) return null;
        string path = node.AbsolutePath;

        if (node.Kind == NodeKind.Cell) return Path.GetDirectoryName(path);
        if (Directory.Exists(path))     return path;
        if (File.Exists(path))          return Path.GetDirectoryName(path);
        return null;
    }

    /// <summary>
    /// R-mw3-5/-6: the target window is activated and the modal shown AFTER the drop handler has
    /// returned and the platform's drag loop has unwound. Raising a window mid-drag on macOS puts a
    /// newly-key window under the cursor and the drag can be delivered to the wrong control — the
    /// same class of problem that had Dock's restack-on-drag disabled process-wide — and showing a
    /// modal from inside the handler is how a drag-drop deadlock is written.
    /// </summary>
    private void RunAfterDragCompletes(Func<Task> action) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            (TopLevel.GetTopLevel(this) as Window)?.Activate();
            await action();
        }, Avalonia.Threading.DispatcherPriority.Background);

    // Returns true if the drag contains at least one valid file/directory path.
    private static bool HasDroppedPaths(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);
            if (RawContainsValidPath(raw)) return true;
        }
        return false;
    }

    // Extracts all valid file/directory paths from a drop event.
    // Mirrors SchematicCanvas.TryExtractImagePath but accepts any extension and directories.
    private static IEnumerable<string> ExtractDroppedPaths(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);
            foreach (var path in PathsFromRaw(raw))
                yield return path;
        }
    }

    private static bool RawContainsValidPath(object? raw)
    {
        foreach (var p in PathsFromRaw(raw))
            if (p is not null) return true;
        return false;
    }

    private static IEnumerable<string> PathsFromRaw(object? raw)
    {
        IEnumerable<string?> candidates = raw switch
        {
            IStorageItem single             => [single.Path?.LocalPath],
            IEnumerable<IStorageItem> files => System.Linq.Enumerable.Select(files, f => f.Path?.LocalPath),
            string s                        => [(string?)s],
            _                               => [],
        };
        foreach (var path in candidates)
        {
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                yield return path;
        }
    }

    // ── Context menu Opening handler (Item 4) ────────────────────────────────

    // Fires INPC on IsSaveable/SaveHeader so the menu shows the real live state.
    private void OnNodeContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: ProjectTreeNodeViewModel vm })
            vm.RefreshDynamicMenuState();
    }

    // ── On-focus refresh (Layer 4) ─────────────────────────────────────────────

    private bool _refreshPending;
    private Window? _attachedWindow;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_attachedWindow is not null)
            _attachedWindow.Activated += OnWindowActivated;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_attachedWindow is not null)
        {
            _attachedWindow.Activated -= OnWindowActivated;
            _attachedWindow = null;
        }
    }

    // Debounced re-scan on window focus: guards against rapid consecutive Activated events.
    //
    // RefreshAsync, not Refresh: this is the FREQUENT path — it fires on open, on every alt-tab back
    // and on every dialog close — and nothing waits on its result, so the filesystem walk (~92 ms on a
    // 600-cell workspace, measured) belongs off the UI thread. It also does nothing at all when the
    // scan finds the same tree that is already rendered, which is what stopped the tree flashing on
    // every window activation (owner, 2026-08-25).
    private void OnWindowActivated(object? sender, System.EventArgs e)
    {
        if (_refreshPending) return;
        _refreshPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (DataContext is ProjectTreeTool tool)
                _ = tool.RefreshAsync();
        }, Avalonia.Threading.DispatcherPriority.ApplicationIdle);
    }

}
