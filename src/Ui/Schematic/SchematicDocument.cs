using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Dock Document representing an open schematic in a Content tab.
/// A scratch document has <see cref="FilePath"/> == null: it is in-memory only,
/// dirty from creation, and invisible to the project tree.
/// A materialized document has a real on-disk path (set at save time, step 2+).
///
/// A document holds a navigation stack of frames; the view renders the active (top) frame's
/// session VM.  Push In / Pop Out change which frame is active without opening a new tab.
/// </summary>
public sealed class SchematicDocument : Document, IUndoableDocument, IActivatableDocument
{
    // ── Activation focus — view grabs keyboard focus on tab-switch (Select All etc. without a click) ──
    private bool _activationFocusPending;
    public event Action? ActivationFocusRequested;
    public void RequestActivationFocus() { _activationFocusPending = true; ActivationFocusRequested?.Invoke(); }
    public bool ConsumeActivationFocus() { var p = _activationFocusPending; _activationFocusPending = false; return p; }

    // ── Navigation frame ──────────────────────────────────────────────────────

    private readonly record struct NavFrame(SchematicViewModel Session, string Label);

    private readonly List<NavFrame> _frames;

    /// <summary>
    /// The current navigation depth: 0 = at the base cell, N = N levels pushed in.
    /// </summary>
    public int NavDepth => _frames.Count - 1;

    /// <summary>True when there is at least one level to pop back to.</summary>
    public bool CanPopOut => NavDepth > 0;

    /// <summary>
    /// The session VM the canvas should render and bind to.
    /// Equals <see cref="ViewModel"/> when at the base level; advances into sub-cells on Push In.
    /// </summary>
    public SchematicViewModel ActiveViewModel => _frames[^1].Session;

    /// <summary>Read-only view of the frame stack; index 0 = base. Used by the breadcrumb (hier4).</summary>
    public IReadOnlyList<(SchematicViewModel Session, string Label)> NavFrames
        => _frames.Select(f => (f.Session, f.Label)).ToList();

    /// <summary>
    /// Ordered breadcrumb items for the hier4 breadcrumb bar.
    /// Rebuilt on every <see cref="PushIn"/>, <see cref="PopOut"/>, or <see cref="PopTo"/>.
    /// The last item has <see cref="BreadcrumbItem.IsCurrent"/> = true; all others are clickable.
    /// </summary>
    public IReadOnlyList<BreadcrumbItem> Breadcrumbs
    {
        get
        {
            var items = new List<BreadcrumbItem>(_frames.Count);
            for (int i = 0; i < _frames.Count; i++)
                items.Add(new BreadcrumbItem(i, _frames[i].Label, i == _frames.Count - 1));
            return items;
        }
    }

    /// <summary>Raised whenever the active frame changes (push, pop, popTo).</summary>
    public event EventHandler? ActiveViewModelChanged;

    // ── Active-VM subscriptions ────────────────────────────────────────────────

    private SchematicViewModel? _activeSubscribedVm;

    private void OnActiveUndoRedoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UndoRedoStack.IsModified))
            IsDirty = ActiveViewModel.UndoRedo.IsModified;
    }

    private void OnActiveVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SchematicViewModel.RenderModel))
            OnPropertyChanged(nameof(Model));
    }

    private void RebindActiveVm(SchematicViewModel newVm)
    {
        if (_activeSubscribedVm is not null)
        {
            _activeSubscribedVm.UndoRedo.PropertyChanged -= OnActiveUndoRedoChanged;
            _activeSubscribedVm.PropertyChanged           -= OnActiveVmPropertyChanged;
        }

        _activeSubscribedVm = newVm;
        newVm.UndoRedo.PropertyChanged += OnActiveUndoRedoChanged;
        newVm.PropertyChanged           += OnActiveVmPropertyChanged;

        // Recompute dirty and title from the new active session.
        _isDirty = newVm.UndoRedo.IsModified;
        UpdateTitle();
    }

    // ── Navigation ops ────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a sub-cell session onto the navigation stack; the tab now renders it.
    /// <paramref name="label"/> is the instance designator (e.g. "X1") shown in the breadcrumb.
    /// </summary>
    public void PushIn(SchematicViewModel session, string label)
    {
        _frames.Add(new NavFrame(session, label));
        RebindActiveVm(session);
        RaiseRetargetEvents();
    }

    /// <summary>
    /// Pops the top frame and returns the popped session (for retirement).
    /// Returns null when already at the base level.
    /// </summary>
    public SchematicViewModel? PopOut()
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
    /// sessions in pop order (outermost first).  Used by the breadcrumb (hier4).
    /// </summary>
    public IReadOnlyList<SchematicViewModel> PopTo(int frameIndex)
    {
        frameIndex = Math.Clamp(frameIndex, 0, _frames.Count - 1);
        var popped = new List<SchematicViewModel>();
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

    private void RaiseRetargetEvents()
    {
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ActiveViewModel));
        OnPropertyChanged(nameof(NavDepth));
        OnPropertyChanged(nameof(CanPopOut));
        OnPropertyChanged(nameof(NavFrames));
        OnPropertyChanged(nameof(Breadcrumbs));
        ActiveViewModelChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Base title + dirty ───────────────────────────────────────────────────

    private string _baseTitle;

    /// <summary>The base session VM (what the document was opened on); never changes.</summary>
    public SchematicViewModel ViewModel { get; }

    /// <summary>
    /// The undo stack the workspace's global Undo/Redo routes through. Must follow the ACTIVE
    /// frame, not the base — otherwise Undo/Redo operate on the top-level cell while the user is
    /// pushed into a sub-cell (the reported bug). Changes on every Push In / Pop Out / Pop To.
    /// </summary>
    public UndoRedoStack UndoRedo => ActiveViewModel.UndoRedo;

    /// <summary>Message sink for posting save/error messages; null if no sink was provided at construction.</summary>
    public IMessageSink? Messages { get; init; }

    /// <summary>Workspace-level hierarchy service for Push In / Pop Out / Open Cell in New Tab. Injected at creation; null in tests.</summary>
    public IHierarchyHost? Hierarchy { get; init; }

    /// <summary>Current render snapshot from the active session (convenience alias for canvas binding).</summary>
    public SchematicModel? Model => ActiveViewModel.RenderModel;

    // ── Scratch / dirty identity ───────────────────────────────────────────────

    /// <summary>
    /// Absolute on-disk path of the .csch file, or null for a scratch document.
    /// Set once at materialization (step 2); null = scratch.
    /// </summary>
    public string? FilePath { get; private set; }

    /// <summary>True when this document has no on-disk path yet (scratch mode).</summary>
    public bool IsScratch => FilePath is null;

    private bool _isDirty;

    /// <summary>
    /// True when the active session has unsaved content.
    /// Driven by the active frame's <c>UndoRedoStack.CanUndo</c>; recomputed on every retarget.
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
        Title = _isDirty ? $"• {activeLabel}" : activeLabel;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="cellName">Display name / base title for the tab.</param>
    /// <param name="viewModel">The schematic view model (base session).</param>
    /// <param name="filePath">
    /// Absolute path of the .csch file on disk, or null for a scratch (in-memory) document.
    /// </param>
    public SchematicDocument(string cellName, SchematicViewModel viewModel, string? filePath = null)
    {
        _baseTitle = cellName;
        Id         = cellName;
        FilePath   = filePath;
        ViewModel  = viewModel;

        // Initialize the frame stack with the base frame.
        _frames = new List<NavFrame> { new(viewModel, cellName) };

        // Start clean; first undoable edit on the active VM makes this dirty.
        _isDirty = false;
        Title    = _baseTitle;

        // Wire up the initial active-VM subscriptions.
        RebindActiveVm(viewModel);
    }

    // ── Materialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Transitions this scratch document to materialized: sets its on-disk path and
    /// clears the dirty flag. Called by the plan executor (L2) after the .csch is written.
    /// After this call IsScratch is false and the tab title loses its bullet.
    /// Must only be called once per document (from scratch to materialized is one-way).
    /// </summary>
    internal void Materialize(string filePath, string? cellName = null)
    {
        FilePath = filePath;
        if (cellName is not null && cellName != _baseTitle)
        {
            _baseTitle = cellName;
            Id         = cellName;
        }
        IsDirty = false; // triggers UpdateTitle() which now uses the updated _baseTitle
    }

    /// <summary>
    /// Updates path and title after "Save As" on an already-materialized document.
    /// Unlike <see cref="Materialize"/>, this may be called repeatedly.
    /// </summary>
    internal void OnSavedAs(string filePath, string cellName)
    {
        FilePath   = filePath;
        _baseTitle = cellName;
        Id         = cellName;
        UpdateTitle();
    }
}

/// <summary>
/// Single item in the hierarchy breadcrumb bar shown by <see cref="SchematicDocument.Breadcrumbs"/>.
/// Promoted to top-level so Avalonia compiled bindings can reference it via <c>x:DataType</c>.
/// </summary>
public sealed record BreadcrumbItem(int FrameIndex, string Text, bool IsCurrent)
{
    /// <summary>True for all crumbs except the first (base) one; drives separator glyph visibility.</summary>
    public bool IsNotFirst => FrameIndex > 0;
}
