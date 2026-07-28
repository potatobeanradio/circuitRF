using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L1f — cross-cell clipboard (docs/sonnet-briefs/brief-L1f-clipboard.md). All the logic that
/// decides what a paste MEANS (building a fragment, rescaling, reconciling layers, translating)
/// lives in <see cref="LayoutFragment"/> (pure, framework-free); this file is selection plumbing +
/// <c>Commands.Layout.ReplaceShapesCommand</c> wiring + the paste-ghost placement state machine +
/// Messages reporting, mirroring how <c>LayoutEditorViewModel.Booleans.cs</c> is organized. The
/// actual system-clipboard I/O (reading/writing <c>IClipboard</c>, rendering rich graphic formats)
/// lives in <c>LayoutClipboard.cs</c> (src/Ui/Clipboard/) and is driven by the view — this VM never
/// touches <c>IClipboard</c> directly.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    public bool CanCopySelection => ValidSelectedIndices.Count > 0 || SelectedInstanceIndices.Count > 0;
    public bool CanDuplicateSelection => ValidSelectedIndices.Count > 0 || SelectedInstanceIndices.Count > 0;

    public IRelayCommand DuplicateCommand { get; private set; } = null!;

    private void InitClipboardCommands()
    {
        DuplicateCommand = new RelayCommand(Duplicate, () => CanDuplicateSelection);
    }

    // ── Copy / Cut — pure fragment build; system-clipboard I/O happens in the view ───────────────

    /// <summary>Builds a fragment payload from the current selection, or null when nothing is
    /// selected. No model change, no undo entry — Copy itself never touches the document. The
    /// caller (<c>LayoutEditorView</c>) writes the result to the system clipboard via
    /// <c>LayoutClipboard.CopyAsync</c>.</summary>
    public LayoutFragment.Payload? BuildCopyPayload()
    {
        var indices = ValidSelectedIndices;
        var (instances, cellDirs) = BuildCopyInstancesPayload();
        if (indices.Count == 0 && instances.Count == 0) return null;
        var shapes = indices.Select(i => Model.Shapes[i]).ToList();
        return LayoutFragment.Build(shapes, instances, cellDirs, Technology, Model.DbuPerMicron);
    }

    /// <summary>Cut = Copy (the caller writes to the system clipboard BEFORE calling this) then
    /// Delete, as ONE undo entry — <see cref="DeleteSelection"/> already deletes BOTH shapes and
    /// instances together as one command (brief-L3a-followups.md §2/R-fix-2 — no longer the L3a-era
    /// "mutual exclusivity means only one kind is ever non-empty" special case).</summary>
    public void CutSelectionAfterCopy() => DeleteSelection();

    // ── Duplicate — internal copy, deliberately bypasses the system clipboard ────────────────────

    /// <summary>Clones the WHOLE current selection (shapes AND instances together — R-fix-2) and
    /// places it offset by one snap step (§4 of the brief) as ONE undo entry, then selects the new
    /// shapes/instances. Never touches the system clipboard — clobbering the user's clipboard as a
    /// side effect of Duplicate is a small betrayal people notice. Instance CellRefs never need
    /// rebasing here — the duplicate lands in the SAME document, so the original relative path is
    /// already correct.</summary>
    public void Duplicate()
    {
        long step = OneSnapStepDbu;

        var indices = ValidSelectedIndices;
        var shapes = indices.Count > 0
            ? LayoutFragment.Translate(indices.Select(i => Model.Shapes[i]).ToList(), step, step)
            : [];

        var srcInstances = SelectedInstanceIndices.Select(i => Model.Instances[i]).ToList();
        var instances = srcInstances.Count > 0 ? LayoutFragment.Translate(srcInstances, step, step) : [];

        InsertPastedMixed(shapes, instances, "Duplicate");
    }

    // ── Paste preparation — rescale + layer reconciliation (called by the view before placing) ───

    /// <summary>Rescales the fragment to this document's own <c>DbuPerMicron</c> (R-L1f-2), posting
    /// one Warning per affected shape when the ratio is non-integer or a coordinate does not divide
    /// evenly. Paste always proceeds regardless — see <see cref="LayoutFragment.Rescale"/>'s doc
    /// comment for why this deliberately differs from <see cref="LayoutScaling.TryChangeResolution"/>.</summary>
    public LayoutFragment.RescaleResult RescaleFragment(LayoutFragment.Payload payload)
    {
        var result = LayoutFragment.Rescale(payload, Model.DbuPerMicron);
        foreach (var w in result.Warnings) _messageSink?.Warning(w);
        return result;
    }

    /// <summary>
    /// Proposes a layer mapping for a fragment landing in this document's current <see cref="Technology"/>
    /// (docs/sonnet-briefs/brief-L1g-technology-retarget.md §1) — the caller (the view) shows the
    /// shared mapping dialog whenever <see cref="LayoutLayerMapping.RequiresConfirmation"/> says so
    /// (R-L1g-2), then calls <see cref="ApplyFragmentReconciliation"/> with the settled choices. This
    /// replaces L1f's <c>GetMissingLayers</c> trigger, which only asked "which keys are absent" —
    /// wrong when both technologies happen to use the same key range with different meanings (the
    /// Drill→Substrate trap).
    /// </summary>
    public IReadOnlyList<LayerMappingRow> ProposeFragmentLayerMapping(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayerDef> fragmentLayers) =>
        LayoutLayerMapping.Propose(shapes, fragmentLayers, Technology);

    /// <summary>Applies the caller-collected reconciliation choices (R-L1f-3) and, for any
    /// Add-to-technology choice, installs the fragment's <see cref="LayerDef"/>s into a live
    /// (unsaved) clone of the current technology via <see cref="RequestAddLayerToTechnology"/> — the
    /// L1-fix <c>TechnologyCache.SetLive</c> path. This never writes the <c>.ctech</c> file directly;
    /// the user still decides whether to persist it, and it is undoable in the tech editor. Offering
    /// "Add to the technology" at all requires a resolved technology — the caller only surfaces that
    /// choice when <see cref="Technology"/> is non-null.</summary>
    public IReadOnlyList<LayoutShape> ApplyFragmentReconciliation(
        IReadOnlyList<LayoutShape> shapes,
        IReadOnlyList<LayerDef> fragmentLayers,
        IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>? choices)
    {
        var result = LayoutFragment.ApplyReconciliation(shapes, fragmentLayers, choices);

        if (result.LayersToAdd.Count > 0 && Technology is { } tech && ResolvedTechPath is { } techPath)
        {
            var clone = TechPersistence.Deserialize(TechPersistence.Serialize(tech));
            foreach (var def in result.LayersToAdd)
            {
                if (clone.Layers.Any(l => l.Key == def.Key)) continue;
                clone.Layers.Add(def);
            }
            RequestAddLayerToTechnology?.Invoke(techPath, clone);
        }

        return result.Shapes;
    }

    /// <summary>Fired when a paste's "Add to the technology" reconciliation choice needs to install
    /// a live (unsaved) technology override. The host (<c>WorkspaceViewModel</c>, which owns the
    /// <c>TechnologyCache</c>) subscribes and calls <c>TechnologyCache.SetLive(path, tech)</c> —
    /// exactly mirroring <c>TechEditorViewModel.TechLiveChanged</c>/<c>WorkspaceViewModel.OnTechLiveChanged</c>.
    /// <paramref name="tech"/> is always an independent clone (see <see cref="ApplyFragmentReconciliation"/>),
    /// never a reference this VM keeps mutating.</summary>
    public event Action<string, Technology>? RequestAddLayerToTechnology;

    // ── Paste-ghost placement (Ctrl/Cmd+V) ───────────────────────────────────────────────────────
    // brief-L3a-followups.md §2/R-fix-2: a paste can now carry BOTH shapes and instances (a mixed
    // copy — BuildCopyPayload has always built a fragment with both, gate 11 already covered the
    // instance half at the VM level) and lands as ONE undo entry, together. The instances travel
    // alongside the shape ghost (translated by the SAME final delta at commit) rather than getting
    // their own visual ghost — a stated, narrow scope simplification: an instance ghost would need
    // its own placeholder-box rendering plumbed into the SAME overlay pass, and nothing in this
    // brief's gates requires a live instance preview during a paste drag, only that the final
    // placement lands correctly as one undo entry (gate 6).

    private IReadOnlyList<LayoutShape>? _pastePlacementShapes;
    private IReadOnlyList<LayoutInstance> _pastePlacementInstances = [];
    private long _pastePlacementAnchorX;
    private long _pastePlacementAnchorY;
    private long _pasteCursorX;
    private long _pasteCursorY;

    /// <summary>True while a Paste ghost is attached to the cursor, waiting for a click to commit or
    /// Escape to cancel.</summary>
    public bool IsPastePlacementActive => _pastePlacementShapes is not null;

    /// <summary>
    /// Begins the Ctrl/Cmd+V "ghost follows the cursor" placement (§3 of the brief) with already
    /// rescaled + reconciled shapes (and, per brief-L3a-followups.md §2, any rebased instances from
    /// the same mixed copy — see <see cref="RebaseFragmentInstances"/>) and their shared
    /// (destination-DBU) anchor. The ghost renders at the anchor position (zero offset) until the
    /// first pointer move arrives, then tracks the snapped cursor exactly — a click (<see
    /// cref="OnPointerPressed"/>) places both kinds there TOGETHER as one undo entry; Escape (<see
    /// cref="OnKeyDown"/>) cancels with no command pushed. <paramref name="instances"/> defaults to
    /// none so every pre-existing (shape-only) call site is unaffected.
    /// </summary>
    public void BeginPastePlacement(IReadOnlyList<LayoutShape> shapes, long anchorX, long anchorY,
        IReadOnlyList<LayoutInstance>? instances = null)
    {
        if (shapes.Count == 0 && (instances is null || instances.Count == 0)) return;
        _pastePlacementShapes = shapes;
        _pastePlacementInstances = instances ?? [];
        _pastePlacementAnchorX = anchorX;
        _pastePlacementAnchorY = anchorY;
        _pasteCursorX = anchorX;
        _pasteCursorY = anchorY;
        RebuildOverlay();
    }

    private void UpdatePastePlacementCursor(double wx, double wy, bool suspendSnap)
    {
        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, Model.SnapDbu, suspendSnap);
        _pasteCursorX = sx;
        _pasteCursorY = sy;
        RebuildOverlay();
    }

    private void CancelPastePlacement()
    {
        _pastePlacementShapes = null;
        _pastePlacementInstances = [];
        RebuildOverlay();
    }

    private void CommitPastePlacement()
    {
        if (_pastePlacementShapes is not { } shapes) return;
        if (shapes.Count == 0 && _pastePlacementInstances.Count == 0) { _pastePlacementShapes = null; return; }
        long dx = _pasteCursorX - _pastePlacementAnchorX;
        long dy = _pasteCursorY - _pastePlacementAnchorY;
        var placedShapes = LayoutFragment.Translate(shapes, dx, dy);
        var placedInstances = LayoutFragment.Translate(_pastePlacementInstances, dx, dy);
        _pastePlacementShapes = null;
        _pastePlacementInstances = [];
        InsertPastedMixed(placedShapes, placedInstances, "Paste");
    }

    /// <summary>Paste in Place (§3 of the brief; extended to a mixed copy by brief-L3a-followups.md
    /// §2) — original coordinates, no ghost, immediate; one undo entry covering both kinds.
    /// <paramref name="instances"/> defaults to none so every pre-existing (shape-only) call site is
    /// unaffected.</summary>
    public void PasteInPlace(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance>? instances = null) =>
        InsertPastedMixed(shapes, instances ?? [], "Paste in Place");

    // ── L3a instance paste (gate 11) — immediate placement, no ghost (see this file's own header
    // note on why shape paste and instance paste use different placement mechanisms) ───────────────

    /// <summary>Rebases a payload's instances' <c>CellRef</c>s to resolve correctly in THIS document
    /// (see <see cref="LayoutFragment.RebaseInstances"/>) — call before either paste variant below.
    /// <b>Known, narrow gap, named rather than silently accepted:</b> unlike shapes (<see
    /// cref="RescaleFragment"/>/<see cref="LayoutFragment.Rescale"/>), an instance's X/Y/Rows/Cols/
    /// PitchX/PitchY are NOT rescaled across a DBU-per-micron mismatch between the source and this
    /// document — <c>LayoutFragment.Rescale</c> only ever walked <c>Payload.Shapes</c>. A same-
    /// resolution paste (by far the common case — matching a single technology's own convention) is
    /// unaffected; a cross-resolution paste of an instance would land at the wrong physical scale.
    /// Not attempted here — out of brief-L3a-followups.md's stated scope (§1-4 never mention DBU
    /// rescaling), a future brief's job.</summary>
    public IReadOnlyList<LayoutInstance> RebaseFragmentInstances(LayoutFragment.Payload payload) =>
        LayoutFragment.RebaseInstances(payload.Instances, payload.InstanceCellDirs, InstanceBaseDir);

    /// <summary>Paste in Place for an instance-only selection — original (rebased-CellRef) position,
    /// immediate, one undo entry. Kept as a direct, single-kind entry point (gate 11's own tests call
    /// it this way); a MIXED copy goes through <see cref="PasteInPlace"/>'s <c>instances</c> parameter
    /// instead so both kinds land as one undo entry together.</summary>
    public void PasteInstancesInPlace(IReadOnlyList<LayoutInstance> instances) => InsertPastedMixed([], instances, "Paste in Place");

    /// <summary>Ordinary Paste for an instance-only selection — offset by one snap step so a
    /// same-document paste is visibly distinct from the original rather than landing exactly on top
    /// of it (mirrors Duplicate's own offset). Kept as a direct, single-kind entry point for the same
    /// reason as <see cref="PasteInstancesInPlace"/> above.</summary>
    public void PasteInstances(IReadOnlyList<LayoutInstance> instances)
    {
        long step = OneSnapStepDbu;
        InsertPastedMixed([], LayoutFragment.Translate(instances, step, step), "Paste");
    }

    /// <summary>Shared commit for Paste / Paste in Place / Duplicate — brief-L3a-followups.md
    /// §2/R-fix-2 generalizes this from shapes-only to BOTH kinds together: shapes are appended
    /// (topmost within their layers, §3) via an empty-removed-set <c>ReplaceShapesCommand</c>,
    /// instances via one <c>AddInstanceCommand</c> each, folded into the SAME <c>CompositeCommand</c>
    /// chain when both kinds are non-empty — ONE undo entry regardless of how many of each kind, and
    /// the newly placed shapes AND instances together become the selection (§3: "the next action
    /// operates on what was just placed"; mirrors <c>ReplaceMixedSelection</c>'s "both kinds at once"
    /// rule elsewhere in this VM).</summary>
    private void InsertPastedMixed(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, string description)
    {
        if (shapes.Count == 0 && instances.Count == 0) return;

        int shapeInsertAt = Model.Shapes.Count;
        int instanceInsertAt = Model.Instances.Count;

        IUiCommand? combined = null;
        if (shapes.Count > 0)
            combined = new Commands.Layout.ReplaceShapesCommand(Model, [], shapes, description);
        foreach (var inst in instances)
        {
            IUiCommand instCmd = new Commands.Layout.AddInstanceCommand(Model, inst);
            combined = combined is null ? instCmd : new CompositeCommand(combined, instCmd);
        }
        Execute(combined!);

        ReplaceMixedSelection(
            Enumerable.Range(shapeInsertAt, shapes.Count),
            Enumerable.Range(instanceInsertAt, instances.Count));
    }
}
