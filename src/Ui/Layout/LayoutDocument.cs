using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Dock Document representing an open Layout Editor session.
/// A scratch document has <see cref="FilePath"/> == null: it is in-memory only,
/// starts clean, and is invisible to the project tree until saved.
/// A materialized document has a real on-disk .clay path (set at save time).
///
/// A document holds a navigation stack of frames (Phase L3b, brief-L3b-hierarchy-navigation.md §1) —
/// the view renders the active (top) frame's session VM. Push In / Pop Out change which frame is
/// active without opening a new tab. Mirrors <c>CircuitRF.Ui.Schematic.SchematicDocument</c> exactly,
/// retargeted from <c>SchematicViewModel</c> to <see cref="LayoutEditorViewModel"/>.
/// </summary>
public sealed class LayoutDocument : Document, IUndoableDocument, IActivatableDocument, IFileBackedDocument
{
    // ── Activation focus — view grabs keyboard focus on tab-switch ────────────
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    // ── Canvas interaction — the OTHER direction (brief-layout-testing-fixes.md item 3/R-fix-3) ──────
    // Clicking the project tree (or any other tool dock) can change what the Properties panel shows
    // WITHOUT this document ever leaving the DocumentDock's own ActiveDockable slot (the tree lives in a
    // different dock region entirely) — so WorkspaceViewModel's OnDocumentDockPropertyChanged, which
    // only fires on an actual ActiveDockable change, never re-fires just because the user clicks back
    // onto this document's own canvas. Raised by the view on GotFocus (a click into the canvas always
    // re-focuses it, since the tree click moved focus away) so WorkspaceViewModel can re-assert
    // Properties/undo/save-scope routing for THIS document explicitly, regardless of whether Dock's own
    // ActiveDockable tracking considers anything to have "changed."
    public event Action? CanvasInteracted;
    public void NotifyCanvasInteracted() => CanvasInteracted?.Invoke();

    // ── Export requests from the File menu (brief-layout-testing-fixes.md item 8) ────────────────
    // GDSII/DXF export logic lives entirely in LayoutEditorView's own code-behind (file picking, the
    // fidelity/options dialogs) — a File-menu item is bound to WorkspaceViewModel, which has no view
    // reference to call that logic on directly. Mirrors CanvasInteracted's own shape exactly: the VM
    // layer only RAISES the request; the view (already subscribed for CanvasInteracted/activation
    // focus) is what actually runs the export.
    public event Action? ExportGdsiiRequested;
    public event Action? ExportDxfRequested;
    public event Action? ExportGerberRequested;
    public event Action? ExportBoardRequested;
    public void RequestExportGdsii() => ExportGdsiiRequested?.Invoke();
    public void RequestExportDxf() => ExportDxfRequested?.Invoke();
    public void RequestExportGerber() => ExportGerberRequested?.Invoke();
    public void RequestExportBoard() => ExportBoardRequested?.Invoke();

    // ── Zoom To Fit request (View->Zoom to Fit) — same shape as the export requests above: this VM
    // layer has no canvas reference, so it raises the request for the already-subscribed view to run.
    public event Action? ZoomToFitRequested;
    public void RequestZoomToFit() => ZoomToFitRequested?.Invoke();

    // ── Toolbar Cut/Copy/Paste — the workspace toolbar's Cut/Copy/Paste buttons have no direct
    // reference to this document's canvas; the view (already wired to the canvas's own
    // ClipboardCopy/Cut/PasteRequested events) runs the real operation.
    public event Action? CutRequested;
    public event Action? CopyRequested;
    public event Action? PasteRequested;
    public void RequestCut()   => CutRequested?.Invoke();
    public void RequestCopy()  => CopyRequested?.Invoke();
    public void RequestPaste() => PasteRequested?.Invoke();

    // ── Navigation frame ──────────────────────────────────────────────────────

    /// <summary><see cref="Viewport"/> is the last viewport CAPTURED for this frame (by the view,
    /// right before navigating away from it) — null until that has happened at least once, in which
    /// case the view falls back to fitting the sub-cell's own extent (mirrors a freshly-opened
    /// document's own initial-fit behaviour). Deliberately per-frame, not per-VM: unlike selection
    /// (which lives on the VM itself and is therefore already free), pan/zoom is owned by the CANVAS
    /// CONTROL, which is shared across every frame of one open tab — this is the one piece of state
    /// L3b's own nav-frame model has to carry explicitly that the schematic's mirror-target does not
    /// (see the L3b completion note in src/Ui/CLAUDE.md for why: the schematic's push-in/pop-out does
    /// not restore viewport at all today).</summary>
    /// <summary><see cref="Instance"/> is the placement that was pushed INTO — null for the base frame,
    /// and null for any push that did not record one (every pre-existing caller). It exists so a
    /// consumer that draws something in world coordinates over this canvas — wbond.md WB27's wire
    /// overlay is the first — can walk the transform chain down to the frame currently on screen.
    /// The layout editor itself does not read it: a pushed-in session already holds the sub-cell's own
    /// geometry in its own frame.</summary>
    private readonly record struct NavFrame(
        LayoutEditorViewModel Session, string Label, LayoutViewport? Viewport = null,
        LayoutInstance? Instance = null, int Row = 0, int Col = 0);

    private readonly List<NavFrame> _frames;

    /// <summary>The current navigation depth: 0 = at the base cell, N = N levels pushed in.</summary>
    public int NavDepth => _frames.Count - 1;

    /// <summary>True when there is at least one level to pop back to.</summary>
    public bool CanPopOut => NavDepth > 0;

    /// <summary>
    /// The session VM the canvas should render and bind to.
    /// Equals <see cref="ViewModel"/> when at the base level; advances into sub-cells on Push In.
    /// </summary>
    public LayoutEditorViewModel ActiveViewModel => _frames[^1].Session;

    /// <summary>Read-only view of the frame stack; index 0 = base. Used by the breadcrumb bar.</summary>
    public IReadOnlyList<(LayoutEditorViewModel Session, string Label)> NavFrames
        => _frames.Select(f => (f.Session, f.Label)).ToList();

    /// <summary>
    /// Ordered breadcrumb items for the layout editor's breadcrumb bar. Rebuilt on every
    /// <see cref="PushIn"/>, <see cref="PopOut"/>, or <see cref="PopTo"/>. The last item has
    /// <see cref="LayoutBreadcrumbItem.IsCurrent"/> = true; all others are clickable.
    /// </summary>
    public IReadOnlyList<LayoutBreadcrumbItem> Breadcrumbs
    {
        get
        {
            var items = new List<LayoutBreadcrumbItem>(_frames.Count);
            for (int i = 0; i < _frames.Count; i++)
                items.Add(new LayoutBreadcrumbItem(i, _frames[i].Label, i == _frames.Count - 1));
            return items;
        }
    }

    /// <summary>Raised whenever the active frame changes (push, pop, popTo).</summary>
    public event EventHandler? ActiveViewModelChanged;

    // ── Active-VM subscriptions ────────────────────────────────────────────────

    private LayoutEditorViewModel? _activeSubscribedVm;

    private void OnActiveVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutEditorViewModel.IsDirty))
            IsDirty = ActiveViewModel.IsDirty;
    }

    private void RebindActiveVm(LayoutEditorViewModel newVm)
    {
        if (_activeSubscribedVm is not null)
            _activeSubscribedVm.PropertyChanged -= OnActiveVmPropertyChanged;

        _activeSubscribedVm = newVm;
        newVm.PropertyChanged += OnActiveVmPropertyChanged;

        // Recompute dirty and title from the new active session.
        _isDirty = newVm.IsDirty;
        UpdateTitle();
    }

    // ── Navigation ops ────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a sub-cell session onto the navigation stack; the tab now renders it.
    /// <paramref name="label"/> is the instance designator shown in the breadcrumb.
    /// </summary>
    /// <param name="instance">
    /// The placement being descended into. Optional, and unused by the layout editor itself — it is
    /// recorded only so <see cref="DescentChain"/> can offer the transform chain to an overlay that
    /// draws world-coordinate geometry over this canvas (wbond.md WB27).
    /// </param>
    public void PushIn(LayoutEditorViewModel session, string label,
                       LayoutInstance? instance = null, int row = 0, int col = 0)
    {
        _frames.Add(new NavFrame(session, label, Viewport: null, instance, row, col));
        RebindActiveVm(session);
        RaiseRetargetEvents();
    }

    /// <summary>
    /// The instances descended through to reach the frame currently on screen, outermost first.
    ///
    /// <para><b>Shorter than <see cref="NavDepth"/> is a real answer, not an error:</b> a frame pushed
    /// without an instance (any pre-existing caller) contributes nothing, so a chain shorter than the
    /// depth means the transform down to the current frame is not fully known. A consumer that needs
    /// an exact frame — the wire overlay does — must treat that as "cannot place geometry here" rather
    /// than composing a partial chain, which would silently draw at the wrong offset.</para>
    /// </summary>
    public IReadOnlyList<(LayoutInstance Instance, int Row, int Col)> DescentChain
        => _frames.Skip(1)
                  .Where(f => f.Instance is not null)
                  .Select(f => (f.Instance!, f.Row, f.Col))
                  .ToList();

    /// <summary>
    /// True when every pushed frame recorded its instance, so <see cref="DescentChain"/> describes the
    /// complete transform from the base cell down to what is on screen.
    /// </summary>
    public bool DescentChainIsComplete => DescentChain.Count == NavDepth;

    /// <summary>
    /// Pops the top frame and returns the popped session (for retirement).
    /// Returns null when already at the base level.
    /// </summary>
    public LayoutEditorViewModel? PopOut()
    {
        if (!CanPopOut) return null;
        var popped = _frames[^1].Session;
        _frames.RemoveAt(_frames.Count - 1);
        RebindActiveVm(ActiveViewModel);
        RaiseRetargetEvents();
        return popped;
    }

    /// <summary>
    /// Pops down to <paramref name="frameIndex"/> (clamped; 0 = base) and returns the popped
    /// sessions in pop order (outermost first). Used by the breadcrumb bar.
    /// </summary>
    public IReadOnlyList<LayoutEditorViewModel> PopTo(int frameIndex)
    {
        frameIndex = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        var popped = new List<LayoutEditorViewModel>();
        while (_frames.Count - 1 > frameIndex)
        {
            popped.Add(_frames[^1].Session);
            _frames.RemoveAt(_frames.Count - 1);
        }
        if (popped.Count > 0)
        {
            RebindActiveVm(ActiveViewModel);
            RaiseRetargetEvents();
        }
        return popped;
    }

    // ── Per-frame viewport (see NavFrame's own doc comment for why this exists) ─

    /// <summary>Captures <paramref name="viewport"/> onto the CURRENTLY active frame — called by the
    /// view immediately before any navigation (push/pop/popTo), so the frame being LEFT remembers
    /// exactly where the user was looking. A frame with no captured viewport yet (e.g. a sub-cell
    /// just pushed into for the first time) reports null from <see cref="ActiveFrameSavedViewport"/>;
    /// the view treats that as "fit the new content," matching a freshly-opened document's own
    /// initial-fit convention.</summary>
    public void CaptureActiveViewport(LayoutViewport viewport)
        => _frames[^1] = _frames[^1] with { Viewport = viewport };

    /// <summary>The active frame's own last-captured viewport, or null if none was ever captured for
    /// it (see <see cref="CaptureActiveViewport"/>).</summary>
    public LayoutViewport? ActiveFrameSavedViewport => _frames[^1].Viewport;

    private void RaiseRetargetEvents()
    {
        OnPropertyChanged(nameof(ActiveViewModel));
        OnPropertyChanged(nameof(NavDepth));
        OnPropertyChanged(nameof(CanPopOut));
        OnPropertyChanged(nameof(NavFrames));
        OnPropertyChanged(nameof(Breadcrumbs));
        // The foreign-workspace band reads these off the ACTIVE frame, and a push-in/pop-out changes
        // which frame that is — so they belong in this set. Without them, descending into a cell that
        // lives in another workspace (an ordinary cross-workspace CellRef) leaves the band showing the
        // PARENT's answer, in either direction: absent when it should name the sub-cell's workspace,
        // or still naming a workspace after popping back out to a document that is not foreign at all.
        OnPropertyChanged(nameof(IsForeign));
        OnPropertyChanged(nameof(SourceWorkspaceName));
        OnPropertyChanged(nameof(SourceWorkspaceCwsPath));
        ActiveViewModelChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Base title + dirty ───────────────────────────────────────────────────

    private string _baseTitle;

    /// <summary>The base session VM (what the document was opened on); never changes.</summary>
    public LayoutEditorViewModel ViewModel { get; }

    /// <summary>
    /// The undo stack the workspace's global Undo/Redo routes through. Must follow the ACTIVE frame,
    /// not the base — otherwise Undo/Redo would operate on the top-level cell while the user is
    /// pushed into a sub-cell. Changes on every Push In / Pop Out / Pop To.
    /// </summary>
    public UndoRedoStack UndoRedo => ActiveViewModel.UndoRedo;

    // WB40 — a layout showing a wirebond cell has TWO edit histories: this stack, and the wires' own
    // snapshot stack. Ctrl+Z asks one question, so the ACTIVE SESSION answers it (see
    // LayoutEditorViewModel.UndoLast, and EditSequence for how the answer is made total). Everything
    // else about undo routing is unchanged: a layout with no wires behaves exactly as it always did,
    // because that is what UndoLast falls through to.

    /// <inheritdoc/>
    public void UndoLast() => ActiveViewModel.UndoLast();

    /// <inheritdoc/>
    public void RedoLast() => ActiveViewModel.RedoLast();

    /// <inheritdoc/>
    public bool CanUndoLast => ActiveViewModel.CanUndoLast;

    /// <inheritdoc/>
    public bool CanRedoLast => ActiveViewModel.CanRedoLast;

    /// <inheritdoc/>
    public string UndoLastDescription => ActiveViewModel.UndoLastDescription;

    /// <inheritdoc/>
    public string RedoLastDescription => ActiveViewModel.RedoLastDescription;

    /// <summary>Workspace-level hierarchy service for Push In / Pop Out / Open Cell in New Tab.
    /// Injected at creation; null in tests, in the standalone wBond binary, and for any document the
    /// host has not adopted yet.
    ///
    /// <para>Settable rather than <c>init</c>-only because a wBond editor's layout document is created
    /// by its own view model the moment a reference layout appears (WB39a) — before the workspace has
    /// had a chance to hand it anything. <c>WBondDocumentViewModel.LayoutHierarchy</c> is the one
    /// place that assigns it late.</para></summary>
    public ILayoutHierarchyHost? Hierarchy { get; set; }

    // ── Scratch / dirty identity ─────────────────────────────────────────────

    /// <summary>Absolute on-disk path of the .clay file, or null for a scratch document.</summary>
    public string? FilePath { get; private set; }

    /// <summary>True when this document has no on-disk path yet (scratch mode).</summary>
    public bool IsScratch => FilePath is null;

    private bool _isDirty;

    /// <summary>
    /// True when the ACTIVE session has unsaved content — the active frame's dirty state, not
    /// necessarily the base's. Recomputed on every retarget.
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        string activeLabel = NavDepth == 0 ? _baseTitle : _frames[^1].Label;
        string body = _isDirty ? $"• {activeLabel}" : activeLabel;
        // brief-foreign-documents.md §4: title bar names the source workspace, dirty bullet preserved
        // (never an asterisk, which already reads as "unsaved" elsewhere in this app).
        Title = ActiveViewModel.IsForeign && ActiveViewModel.SourceWorkspaceName is { } wsName
            ? $"{body} — [{wsName}]"
            : body;
    }

    /// <summary>brief-foreign-documents.md §4: true when the ACTIVE frame's document does not belong
    /// to the currently open workspace. Delegates to <see cref="LayoutEditorViewModel.IsForeign"/>.</summary>
    public bool IsForeign => ActiveViewModel.IsForeign;

    /// <summary>The source workspace's name for marking, or null ("Not part of any workspace"). See
    /// <see cref="IsForeign"/>.</summary>
    public string? SourceWorkspaceName => ActiveViewModel.SourceWorkspaceName;

    /// <summary>The source workspace's own <c>.cws</c> path, for the edge band's "open it" affordance.
    /// Null exactly when <see cref="SourceWorkspaceName"/> is null.</summary>
    public string? SourceWorkspaceCwsPath => ActiveViewModel.SourceWorkspaceCwsPath;

    /// <summary>
    /// Re-raises <see cref="IsForeign"/>/<see cref="SourceWorkspaceName"/>/<see cref="SourceWorkspaceCwsPath"/>/
    /// <see cref="Title"/> change notifications. Call after the CURRENTLY open workspace changes — those
    /// are computed live from a <see cref="LayoutEditorViewModel.CurrentWorkspaceRootDirProvider"/>
    /// callback, which has no PropertyChanged mechanism of its own to notify this document automatically.
    /// </summary>
    internal void RefreshForeignMarking()
    {
        OnPropertyChanged(nameof(IsForeign));
        OnPropertyChanged(nameof(SourceWorkspaceName));
        OnPropertyChanged(nameof(SourceWorkspaceCwsPath));
        UpdateTitle();
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    /// <param name="title">Display name / base title for the tab.</param>
    /// <param name="viewModel">The layout editor view model (base session).</param>
    /// <param name="filePath">Absolute path of the .clay file on disk, or null for a scratch document.</param>
    public LayoutDocument(string title, LayoutEditorViewModel viewModel, string? filePath = null)
    {
        _baseTitle = title;
        Id         = title;
        FilePath   = filePath;
        ViewModel  = viewModel;

        // Initialize the frame stack with the base frame.
        _frames = new List<NavFrame> { new(viewModel, title) };

        _isDirty = false;
        Title    = _baseTitle;

        // FilePath/title sync must always follow the BASE frame's own CurrentLayoutPath — Save/Save
        // As always act on the base .clay, never a pushed-in sub-cell's.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LayoutEditorViewModel.CurrentLayoutPath))
                SyncTitleToPath();
        };

        RebindActiveVm(viewModel);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SyncTitleToPath()
    {
        if (ViewModel.CurrentLayoutPath is not { } path) return;
        FilePath   = path;
        _baseTitle = Path.GetFileName(path);
        UpdateTitle();
    }

    // ── Materialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this scratch document to materialized: sets its on-disk path and clears the
    /// dirty flag (on both the document and the base VM). Must only be called once per document
    /// (scratch → materialized is one-way).
    /// </summary>
    internal void Materialize(string filePath)
    {
        FilePath                    = filePath;
        ViewModel.CurrentLayoutPath = filePath;
        ViewModel.MarkSaved();   // clean baseline -> IsDirty clears via the subscription above
    }

    /// <summary>
    /// Updates path and title after a "Save As" on an already-materialized document.
    /// Unlike <see cref="Materialize"/>, this may be called repeatedly.
    /// </summary>
    internal void OnSavedAs(string filePath, string cellName)
    {
        FilePath                    = filePath;
        ViewModel.CurrentLayoutPath = filePath;
        _baseTitle                  = cellName;
        Id                          = cellName;
        ViewModel.MarkSaved();
        // Through UpdateTitle() (not a direct Title= assignment) so §4's foreign-workspace suffix is
        // recomputed for the NEW path too — a Save As can change IsForeign either direction ("adopts"
        // into the current workspace, or moves a workspace-bound document out to a loose file).
        UpdateTitle();
    }
}

/// <summary>
/// Single item in the hierarchy breadcrumb bar shown by <see cref="LayoutDocument.Breadcrumbs"/>.
/// A layout-local mirror of the schematic's own <c>BreadcrumbItem</c> — same shape, deliberately its
/// own type rather than a shared reference: "Layout borrows patterns from Schematic, not types"
/// (<c>LayoutModel.cs</c>'s own header, an established convention in this codebase).
/// </summary>
public sealed record LayoutBreadcrumbItem(int FrameIndex, string Text, bool IsCurrent)
{
    /// <summary>True for all crumbs except the first (base) one; drives separator glyph visibility.</summary>
    public bool IsNotFirst => FrameIndex > 0;
}
