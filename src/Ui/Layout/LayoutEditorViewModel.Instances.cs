using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L3a — instance selection, move, delete, properties, and the Instance-place tool (docs/sonnet-
/// briefs/brief-L3a-instances-and-arrays.md §5/§6). Mirrors how <c>.Booleans.cs</c>/<c>.Clipboard.cs</c>
/// split concerns out of the main VM file.
///
/// <b>Scope decision, stated plainly (see the L3a completion note in src/Ui/CLAUDE.md for the full
/// reasoning): instances get their OWN selection state, hit-test entry point, and command set —
/// deliberately NOT unified into the existing shape-selection machinery
/// (<c>_selectedIndices: List&lt;int&gt;</c>, overlap cycling, vertex/edge/bulge handles, boolean ops,
/// scale-mode).</b> Shape and instance selection are mutually exclusive: selecting one clears the
/// other. R-L3a-5's own wording — "selection, move, delete, copy/paste and scale then operate on the
/// instance as a unit, THROUGH THE EXISTING COMMANDS" — is read here as "reuse the established
/// architectural PATTERNS (<c>IUiCommand</c>, restore-at-original-index, drag-override live preview,
/// staged-field property commits)," not "thread a second discriminated-union case through every one
/// of the ~30 existing shape-selection call sites in the 1900-line main file." L1d's vertex/edge/bulge
/// handles, L1h's scale-mode bbox handles, and L1e's booleans remain shape-only; an instance has none
/// of those concepts (no vertices, no per-shape darkening, nothing to boolean). Scale-by-drag-handle
/// for a selected instance is not built in this phase — only the numeric Mag field in the properties
/// panel — and is named as a follow-up, not a silent gap.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>The workspace root directory, derived from <see cref="WorkspaceTechDir"/> (always
    /// <c>&lt;root&gt;/tech</c> — <c>WorkspaceViewModel.NewWorkspace</c>'s own creation code) rather
    /// than adding a third parallel workspace-path field wired the same way. Null with no workspace
    /// open (a scratch document, or one opened as a loose file) — the Instance-place tool's cell
    /// picker then has nothing to list, matching that a scratch document also cannot resolve any
    /// CellRef at all (<see cref="InstanceBaseDir"/>'s own doc comment).</summary>
    public string? WorkspaceRootDir => WorkspaceTechDir is { Length: > 0 } d ? Path.GetDirectoryName(d) : null;

    // ── Base directory for resolving CellRef ─────────────────────────────────────────────────────

    /// <summary>The directory a relative <see cref="LayoutInstance.CellRef"/> resolves against — the
    /// directory containing the currently open <c>.clay</c> (the SAME convention <c>BitmapShape.
    /// ImagePathRef</c> already documents: "relative to the containing .clay"). Empty for a
    /// not-yet-saved (scratch) document — every instance in that state resolves as NotFound, exactly
    /// like a scratch schematic cannot resolve a cell-ref symbol either; this is a documented
    /// limitation, not a bug (see <c>LayoutRenderOptions.BaseDir</c>'s own doc comment).</summary>
    public string InstanceBaseDir => CurrentLayoutPath is { Length: > 0 } p ? (Path.GetDirectoryName(p) ?? "") : "";

    // ── Selection (mutually exclusive with shape selection — see the type doc comment) ─────────────

    private readonly List<int> _selectedInstanceIndices = [];

    public IReadOnlyList<int> SelectedInstanceIndices => _selectedInstanceIndices;

    /// <summary>Mirrors <c>SetSelection</c> for instances — brief-L3a-followups.md §2/R-fix-2:
    /// <paramref name="clearOtherKind"/> is false for Shift/Ctrl add-toggle (keep whatever shapes are
    /// already selected), true for a replace (plain click, marquee-without-modifier). See
    /// <c>SetSelection</c>'s own doc comment for the full reasoning — this is its exact instance-side
    /// mirror, no longer the "always mutually exclusive" rule L3a originally shipped.</summary>
    private void SetInstanceSelection(IEnumerable<int> indices, bool clearOtherKind = true)
    {
        var distinct = new List<int>();
        foreach (var i in indices)
            if (i >= 0 && i < Model.Instances.Count && !distinct.Contains(i))
                distinct.Add(i);

        _selectedInstanceIndices.Clear();
        _selectedInstanceIndices.AddRange(distinct);

        if (clearOtherKind && _selectedIndices.Count > 0) { _selectedIndices.Clear(); _pickedVertexIndex = null; _cycleCache = null; }

        SelectionStatusText = ComputeSelectionStatus();
        RebuildOverlay();
    }

    private string ComputeInstanceSelectionStatus()
    {
        if (_selectedInstanceIndices.Count == 0) return "";
        if (_selectedInstanceIndices.Count == 1)
        {
            var inst = Model.Instances[_selectedInstanceIndices[0]];
            string cellName = string.IsNullOrEmpty(inst.CellRef) ? "(no cell)" : Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            string arraySuffix = inst.Rows > 1 || inst.Cols > 1 ? $" · {inst.Rows}×{inst.Cols}" : "";
            return $"Instance: {cellName}{arraySuffix}";
        }
        return $"{_selectedInstanceIndices.Count} instances";
    }

    private void ApplyInstanceClickSelection(int hitIndex, bool shift, bool ctrl)
    {
        if (ctrl)
        {
            SetInstanceSelection(_selectedInstanceIndices.Contains(hitIndex)
                ? _selectedInstanceIndices.Where(i => i != hitIndex)
                : _selectedInstanceIndices.Append(hitIndex), clearOtherKind: false);
        }
        else if (shift)
        {
            SetInstanceSelection(_selectedInstanceIndices.Contains(hitIndex)
                ? _selectedInstanceIndices
                : _selectedInstanceIndices.Append(hitIndex), clearOtherKind: false);
        }
        else
        {
            // brief-L3a-followups.md §2: preserve the whole MIXED selection (shapes + instances) on a
            // plain click landing inside it — not just "is this instance already part of a >1-instance
            // selection," the same widened rule ApplyClickSelection now uses for shapes — so a drag
            // started from an instance that's part of a mixed multi-selection moves everything, not
            // just the instances.
            bool totalMulti = _selectedIndices.Count + _selectedInstanceIndices.Count > 1;
            if (!(totalMulti && _selectedInstanceIndices.Contains(hitIndex)))
                SetInstanceSelection([hitIndex]);
        }
    }

    // Move drag, delete, and nudge are now UNIFIED across shapes and instances (brief-L3a-followups.md
    // §2/R-fix-2 — "Move, nudge, delete, cut/copy/paste and duplicate all apply to shapes and
    // instances together as one undo entry") — see BeginMoveDrag/CommitMoveDrag/DeleteSelection/
    // NudgeSelection in the main VM file, which build a CompositeCommand across MoveShapesCommand/
    // MoveInstancesCommand (or DeleteShapesCommand/DeleteInstancesCommand) whenever both kinds are
    // selected. The separate per-kind versions that used to live here (BeginInstanceMoveDrag,
    // CommitInstanceMoveDrag, DeleteInstanceSelection, NudgeInstanceSelection) are gone — not
    // deprecated, removed — so there is exactly one code path for each gesture, never two that could
    // drift apart.

    private static readonly IReadOnlyDictionary<int, LayoutInstance> EmptyInstanceDragOverrides = new Dictionary<int, LayoutInstance>();

    // ── Cycle rejection at edit time (R-L3a-2) ───────────────────────────────────────────────────

    /// <summary>The absolute CELL folder this document belongs to — one level above the directory a
    /// relative <see cref="LayoutInstance.CellRef"/> resolves against (<see cref="InstanceBaseDir"/>
    /// is the <c>layout/</c> sub-folder; the cell folder is its parent, matching <c>CellFolder.
    /// SubFolderPath</c>'s own convention). Null when this document has no stable cell folder yet
    /// (scratch, or a loose .clay saved outside a cell) — cycle detection is then simply skipped, the
    /// same "nothing can reference back to a path that doesn't exist yet" reasoning <see
    /// cref="CellHierarchy.WouldCreateCycle"/> already documents. Public (brief-L3a-followups.md
    /// §1/R-fix-1) so the Instance cell-picker's "exclude the parent cell only" filter can compare
    /// against it without duplicating this directory-math.</summary>
    public string? CurrentCellDir
    {
        get
        {
            if (InstanceBaseDir is not { Length: > 0 } dir) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(dir)); }
            catch { return null; }
        }
    }

    /// <summary>R-L3a-2 edit time: refuses (reports via Messages, does nothing else) an instance
    /// placement/retarget that would close a reference cycle, naming the path. Returns true when the
    /// caller should proceed (no cycle, or cycle detection could not run for this document).</summary>
    private bool CheckNotCyclic(string candidateCellRef)
    {
        if (!CellHierarchy.WouldCreateCycle(CurrentCellDir, candidateCellRef, InstanceBaseDir)) return true;
        _messageSink?.Error($"Can't add this instance — '{candidateCellRef}' would create a reference cycle back to this cell.");
        return false;
    }

    // ── Instance-place tool (§6) — reuses L1f's paste-placement ghost-follows-cursor gesture ───────

    private string? _instancePlacementCellRef;
    private (LayoutInstance Instance, Bbox Bbox)? _instancePlacementPending;

    public bool IsInstancePlacementActive => _instancePlacementCellRef is not null;

    /// <summary>Arms the Instance tool with a chosen cell reference (the view's cell-picker dialog
    /// calls this after the user chooses a cell) — sets <see cref="LayoutEditorViewModel.Tool.Instance"/>
    /// active and starts the ghost at the origin; the first pointer move positions it under the
    /// cursor exactly like <see cref="BeginPastePlacement"/>.</summary>
    public void BeginInstancePlacement(string cellRef)
    {
        if (string.IsNullOrWhiteSpace(cellRef)) return;
        // ActiveTool's setter fires OnActiveToolChanged -> CancelDrawOp -> CancelInstancePlacement,
        // which would immediately wipe _instancePlacementCellRef if it were already set — set the
        // tool FIRST (canceling whatever ELSE was in progress, while there is still nothing of ours
        // to cancel), then arm the placement.
        ActiveTool = Tool.Instance;
        _instancePlacementCellRef = cellRef;
        UpdateInstancePlacementGhost(0, 0);
        RebuildOverlay();
    }

    private void UpdateInstancePlacementCursor(double wx, double wy, bool suspendSnap)
    {
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspendSnap);
        UpdateInstancePlacementGhost(sx, sy);
        RebuildOverlay();
    }

    private void UpdateInstancePlacementGhost(long x, long y)
    {
        if (_instancePlacementCellRef is not { } cellRef) { _instancePlacementPending = null; return; }
        var inst = new LayoutInstance { CellRef = cellRef, X = x, Y = y, Mag = 1.0 };
        var bbox = CellHierarchy.InstanceBbox(inst, InstanceBaseDir);
        _instancePlacementPending = (inst, bbox);
    }

    private void CancelInstancePlacement()
    {
        _instancePlacementCellRef = null;
        _instancePlacementPending = null;
    }

    private void CommitInstancePlacement()
    {
        if (_instancePlacementPending is not { } pending) return;
        if (!TryPlaceNewInstance(pending.Instance.CellRef, pending.Instance.X, pending.Instance.Y)) return;

        // Stay armed (matches the palette-placement precedent elsewhere in this codebase: placing one
        // component doesn't disarm the tool) — the ghost continues from the same cell reference at
        // the same last cursor position, ready for the next click. Escape or switching tools disarms.
        RebuildOverlay();
    }

    /// <summary>The ONE placement-commit path for a brand-new instance — brief-L3a-followups.md
    /// §4/R-fix-6: "drop routes through the same command path as the Instance tool... one placement
    /// path, not two." Builds the exact same <see cref="LayoutInstance"/> shape (<c>CellRef</c>/X/Y,
    /// <c>Mag = 1.0</c>, everything else default) either entry point uses, runs the SAME R-L3a-2
    /// edit-time cycle check (refusing via Messages, naming the path, on a cycle), and selects the
    /// result — used by both <see cref="CommitInstancePlacement"/> (the Instance tool) and <see
    /// cref="CommitDragInstancePlacement"/> (drag-and-drop from the project tree). Returns false when
    /// refused so a caller that needs to know (neither current one does, but a future one might) can
    /// react.</summary>
    private bool TryPlaceNewInstance(string cellRef, long x, long y)
    {
        if (!CheckNotCyclic(cellRef)) return false;
        var instance = new LayoutInstance { CellRef = cellRef, X = x, Y = y, Mag = 1.0 };
        Execute(new AddInstanceCommand(Model, instance));
        int newIndex = Model.Instances.Count - 1;
        SetInstanceSelection([newIndex]);
        return true;
    }

    // ── Drag-and-drop placement from the project tree (brief-L3a-followups.md §4/R-fix-5/R-fix-6) ──
    // A SEPARATE ghost state machine from the Instance tool's above — deliberately does NOT touch
    // ActiveTool: an OS-level drag can start while any other tool (or none) is active, and changing
    // ActiveTool mid-drag would visibly clobber the user's current tool selection for no reason. The
    // COMMIT still routes through the identical TryPlaceNewInstance (R-fix-6) — "same command path,"
    // not "same UI state machine."

    private (LayoutInstance Instance, Bbox Bbox)? _dragInstancePlacementPending;

    /// <summary>Called by the canvas's DragOver handler on every drag-over tick with the snapped world
    /// point — updates the live ghost (real compiled geometry when the cell resolves, the R-L3a-1
    /// placeholder otherwise — R-fix-5's own rendering widening). Does not arm the Instance tool.</summary>
    public void UpdateDragInstanceGhost(string cellRef, long x, long y)
    {
        var inst = new LayoutInstance { CellRef = cellRef, X = x, Y = y, Mag = 1.0 };
        var bbox = CellHierarchy.InstanceBbox(inst, InstanceBaseDir);
        _dragInstancePlacementPending = (inst, bbox);
        RebuildOverlay();
    }

    /// <summary>Clears the drag ghost — called on DragLeave, or whenever a drag ends without a drop
    /// (mirrors <see cref="CancelInstancePlacement"/>'s own Escape-cancel shape for the Instance tool).</summary>
    public void CancelDragInstancePlacement()
    {
        if (_dragInstancePlacementPending is null) return;
        _dragInstancePlacementPending = null;
        RebuildOverlay();
    }

    /// <summary>R-fix-1's "exclude/refuse the parent cell only" principle, applied to drag-drop
    /// (R-fix-6): true only when <paramref name="cellAbsDir"/> IS the currently-open document's own
    /// parent cell folder — the one case obvious enough that a "no" cursor in <c>DragOver</c> needs no
    /// further explanation (mirrors <see cref="InstanceCellChoices"/>'s identical self-only exclusion
    /// for the picker list). Every OTHER cycle (a deeper A→B→A) is deliberately NOT checked here — the
    /// drag is accepted and the cycle is caught and explained by <see cref="CheckNotCyclic"/> at drop
    /// time instead, exactly like a deeper cycle chosen from the picker.</summary>
    public bool WouldDragCellBeSelfReference(string cellAbsDir)
    {
        if (CurrentCellDir is not { Length: > 0 } parent) return false;
        return string.Equals(InstanceCellChoices.NormalizeDir(cellAbsDir), InstanceCellChoices.NormalizeDir(parent),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Commits the drag ghost at the given (already-snapped) world point as a new instance —
    /// R-fix-6's "same command path as the Instance tool," via the SAME <see cref="TryPlaceNewInstance"/>
    /// both entry points share. Returns false (refused via Messages, naming the cycle path — same as
    /// <see cref="CheckNotCyclic"/> always does) when <paramref name="cellRef"/> would close a cycle
    /// that survived past the DragOver-time self-reference check.</summary>
    public bool CommitDragInstancePlacement(string cellRef, long x, long y)
    {
        _dragInstancePlacementPending = null;
        bool placed = TryPlaceNewInstance(cellRef, x, y);
        RebuildOverlay();
        return placed;
    }

    // ── Properties Inspector support (§6: "cell reference (with a re-target button), rotation,
    // mirror, magnification, array fields") — single-instance editing only in this phase, matching
    // R-L1j-1's own "effective (drag-override-aware) geometry" pattern for liveness during a drag ────

    /// <summary>The live (drag-aware) instance at <paramref name="index"/> — the move-drag preview
    /// clone when one exists, otherwise the committed instance. Mirrors <see cref="EffectiveShapeAt"/>.</summary>
    public LayoutInstance EffectiveInstanceAt(int index) =>
        Overlay.InstanceDragOverrides.TryGetValue(index, out var preview) ? preview : Model.Instances[index];

    /// <summary>Non-null only when exactly one instance is selected and not mid-drag — the Properties
    /// Inspector's single source for the instance property panel.</summary>
    public LayoutInstance? SingleSelectedInstance =>
        _selectedInstanceIndices.Count == 1 && Overlay.InstanceDragOverrides.Count == 0
            ? Model.Instances[_selectedInstanceIndices[0]] : null;

    private void ReplaceSelectedInstance(Func<LayoutInstance, LayoutInstance> build)
    {
        if (_selectedInstanceIndices.Count != 1) return;
        int index = _selectedInstanceIndices[0];
        var before = Model.Instances[index];
        var after = build(before);
        Execute(new ReplaceInstanceCommand(Model, index, before, after));
    }

    public void SetSelectedInstanceRotation(LayoutRotation rot) =>
        ReplaceSelectedInstance(src => { var c = LayoutGeometry.Clone(src); c.Rot = rot; return c; });

    public void SetSelectedInstanceMirrorX(bool mirror) =>
        ReplaceSelectedInstance(src => { var c = LayoutGeometry.Clone(src); c.MirrorX = mirror; return c; });

    public void CommitSelectedInstanceMagText(string text)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mag) || mag <= 0)
            return;
        ReplaceSelectedInstance(src => { var c = LayoutGeometry.Clone(src); c.Mag = mag; return c; });
    }

    public void CommitSelectedInstanceArray(int rows, int cols, long pitchX, long pitchY)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        ReplaceSelectedInstance(src =>
        {
            var c = LayoutGeometry.Clone(src);
            c.Rows = rows; c.Cols = cols; c.PitchX = pitchX; c.PitchY = pitchY;
            return c;
        });
    }

    /// <summary>Straight translate to an exact X/Y (mirrors <c>ApplyRectPosition</c>'s "single
    /// translate, no other field changes" semantics for a plain anchor point) — a staged-text-field
    /// commit for the Properties Inspector's instance X/Y, distinct from <see cref="MoveInstancesCommand"/>'s
    /// relative-delta drag/nudge path. Either coordinate may be left unset to leave it unchanged.</summary>
    public void CommitSelectedInstancePosition(long? newX, long? newY)
    {
        if (newX is null && newY is null) return;
        ReplaceSelectedInstance(src =>
        {
            var c = LayoutGeometry.Clone(src);
            if (newX is { } x) c.X = x;
            if (newY is { } y) c.Y = y;
            return c;
        });
    }

    /// <summary>The re-target button (§6) — changes which cell an instance references, guarded by the
    /// SAME edit-time cycle check a fresh placement gets. Geometry (position/rotation/mirror/mag/array)
    /// is preserved; only <see cref="LayoutInstance.CellRef"/> changes.</summary>
    public void RetargetSelectedInstance(string newCellRef)
    {
        if (string.IsNullOrWhiteSpace(newCellRef)) return;
        if (!CheckNotCyclic(newCellRef)) return;
        ReplaceSelectedInstance(src => { var c = LayoutGeometry.Clone(src); c.CellRef = newCellRef; return c; });
    }

    // ── Missing-instance warning — once per distinct CellRef per load (R-L3a-1) ─────────────────
    // Mirrors LayoutEditorViewModel.ReportUnknownLayers exactly.

    private readonly HashSet<string> _warnedMissingCellRefs = [];

    /// <summary>Called by the canvas after each frame with any distinct <c>CellRef</c>s that failed
    /// to resolve. Posts a Messages warning the first time each is seen for this open document —
    /// never once per placement, never inside the render loop.</summary>
    public void ReportMissingInstanceCellRefs(IReadOnlyList<string> cellRefs)
    {
        foreach (var cellRef in cellRefs)
        {
            if (!_warnedMissingCellRefs.Add(cellRef)) continue;
            _messageSink?.Warning($"Instance references '{cellRef}', which could not be resolved — showing a placeholder.");
        }
    }

    // ── Clipboard support (gate 11) ──────────────────────────────────────────────────────────────

    /// <summary>Adds the current instance selection's fragment data into an in-progress
    /// <see cref="LayoutFragment.Payload"/> build — called by <c>BuildCopyPayload</c> (Clipboard.cs)
    /// alongside the shape data. brief-L3a-followups.md §2/R-fix-2: a selection carrying both shapes
    /// and instances is now normal, not an edge case — this and the shape half both simply contribute
    /// whatever is non-empty to the SAME fragment.
    ///
    /// brief-layout-testing-fixes.md item 2/R-fix-2: also captures each resolved cell dir's path
    /// relative to THIS (source) document's own workspace root, when one resolves — the base-
    /// independent form <see cref="LayoutFragment.RebaseInstances"/> falls back to when the destination
    /// document has no stable base directory of its own to rebase against (a brand-new, unsaved
    /// document).</summary>
    internal (List<LayoutInstance> Instances, List<string?> CellDirs, List<string?> WorkspaceRelativeDirs) BuildCopyInstancesPayload()
    {
        var instances = new List<LayoutInstance>();
        var cellDirs = new List<string?>();
        var workspaceRelativeDirs = new List<string?>();
        foreach (var i in _selectedInstanceIndices)
        {
            if (i < 0 || i >= Model.Instances.Count) continue;
            var inst = Model.Instances[i];
            instances.Add(LayoutGeometry.Clone(inst));
            var res = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir);
            string? resolvedCellDir = res.State == CellLayoutState.Resolved ? res.ResolvedCellDir : null;
            cellDirs.Add(resolvedCellDir);

            string? workspaceRelative = null;
            if (resolvedCellDir is { Length: > 0 } && WorkspaceRootDir is { Length: > 0 } root)
            {
                try { workspaceRelative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(resolvedCellDir)); }
                catch { /* leave null — the absolute-path fallback still covers this instance */ }
            }
            workspaceRelativeDirs.Add(workspaceRelative);
        }
        return (instances, cellDirs, workspaceRelativeDirs);
    }
}
