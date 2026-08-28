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
    /// <summary>§9B.6 R-rul-11: rulers are copyable, and that is the reason they are SELECTABLE at all
    /// — this property is selection-driven and the clipboard graphic renders the copied fragment, so
    /// "rulers work with copy and paste" is not expressible unless a ruler can be selected.</summary>
    public bool CanCopySelection => ValidSelectedIndices.Count > 0 || SelectedInstanceIndices.Count > 0
                                    || SelectedRulerIndices.Count > 0;
    public bool CanDuplicateSelection => ValidSelectedIndices.Count > 0 || SelectedInstanceIndices.Count > 0
                                         || SelectedRulerIndices.Count > 0;

    /// <summary>The same test as <see cref="CanDuplicateSelection"/>, carrying the reason a disabled
    /// menu item should state (R-L1h-3's "always present, disabled with a reason" rule). Wires are
    /// deliberately absent from it, unlike <c>RotateAvailability</c>: <see cref="Duplicate(long,long)"/>
    /// clones shapes and instances only, so offering it for a wire-only selection would be a no-op
    /// dressed as a command.</summary>
    public LayoutCommandAvailability DuplicateAvailability =>
        CanDuplicateSelection
            ? new LayoutCommandAvailability(true, null)
            : new LayoutCommandAvailability(false, "Select geometry or an instance to duplicate.");

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
        var (instances, cellDirs, workspaceRelativeDirs) = BuildCopyInstancesPayload();
        var rulers = SelectedRulers();
        if (indices.Count == 0 && instances.Count == 0 && rulers.Count == 0) return null;
        var shapes = indices.Select(i => Model.Shapes[i]).ToList();
        return LayoutFragment.Build(shapes, instances, cellDirs, workspaceRelativeDirs, rulers, Technology,
                                    Model.DbuPerMicron, Model.DisplayUnit);
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
    public void Duplicate() => Duplicate(OneSnapStepDbu, OneSnapStepDbu);

    /// <summary>Duplicate at a CALLER-CHOSEN offset, in DBU — the "Duplicate with Offset" prompt
    /// (owner, 2026-08-27), whose default is (0,0), i.e. a copy exactly on top of the original. Both
    /// UI surfaces (Ctrl+D and the context menu) go through the dialog and then through here, so the
    /// two can never disagree about what Duplicate does; <see cref="Duplicate()"/> keeps the
    /// one-snap-step nudge for programmatic callers.</summary>
    public void Duplicate(long dxDbu, long dyDbu)
    {
        var indices = ValidSelectedIndices;
        var shapes = indices.Count > 0
            ? LayoutFragment.Translate(indices.Select(i => Model.Shapes[i]).ToList(), dxDbu, dyDbu)
            : [];

        var srcInstances = SelectedInstanceIndices.Select(i => Model.Instances[i]).ToList();
        var instances = srcInstances.Count > 0 ? LayoutFragment.Translate(srcInstances, dxDbu, dyDbu) : [];

        var srcRulers = SelectedRulers();
        var rulers = srcRulers.Count > 0 ? LayoutFragment.Translate(srcRulers, dxDbu, dyDbu) : [];

        InsertPastedMixed(shapes, instances, "Duplicate", rulers);
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
    private IReadOnlyList<RulerAnnotation> _pastePlacementRulers = [];

    /// <summary>Per pasted instance: is its resolved geometry small enough to draw live? Decided ONCE,
    /// when the placement is armed — see <see cref="LayoutOverlay.GhostInstance.BoxOnly"/>.</summary>
    private bool[] _pastePlacementInstanceBoxOnly = [];

    /// <summary>
    /// How many flattened shapes a pasted instance may resolve to before its ghost degrades to a
    /// plain box (owner's own rule: "for small amounts of geometry it should render live; if the
    /// geometry is too complicated for live rendering, then just render a box").
    ///
    /// <para>Counted with <see cref="LayoutFlatten.CountResultingShapes"/>, which is exactly the
    /// right instrument: it honours the array-multiplies-a-level and depth-cap rules WITHOUT ever
    /// materializing more than one array cell's worth of shapes, so asking the question is cheap even
    /// when the answer is "millions".</para>
    /// </summary>
    internal const long PasteGhostLiveShapeBudget = 5_000;
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
        IReadOnlyList<LayoutInstance>? instances = null, IReadOnlyList<RulerAnnotation>? rulers = null)
    {
        if (shapes.Count == 0 && (instances is null || instances.Count == 0) && (rulers is null || rulers.Count == 0)) return;
        _pastePlacementShapes = shapes;
        _pastePlacementInstances = instances ?? [];
        _pastePlacementRulers = rulers ?? [];
        _pastePlacementInstanceBoxOnly = new bool[_pastePlacementInstances.Count];
        for (int i = 0; i < _pastePlacementInstances.Count; i++)
        {
            // CountResultingShapes returns NEGATIVE when it exceeds the ceiling it was given — it
            // stops counting rather than finishing an arithmetic nobody needs the answer to. Reading
            // that as "small" is exactly backwards, and is what the box-only gate caught.
            long n = LayoutFlatten.CountResultingShapes(
                _pastePlacementInstances[i], InstanceBaseDir, PasteGhostLiveShapeBudget);
            _pastePlacementInstanceBoxOnly[i] = n < 0 || n > PasteGhostLiveShapeBudget;
        }
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
        _pastePlacementInstanceBoxOnly = [];
        _pastePlacementRulers = [];
        RebuildOverlay();
    }

    private void CommitPastePlacement()
    {
        if (_pastePlacementShapes is not { } shapes) return;
        if (shapes.Count == 0 && _pastePlacementInstances.Count == 0 && _pastePlacementRulers.Count == 0)
        { _pastePlacementShapes = null; return; }
        long dx = _pasteCursorX - _pastePlacementAnchorX;
        long dy = _pasteCursorY - _pastePlacementAnchorY;
        var placedShapes = LayoutFragment.Translate(shapes, dx, dy);
        var placedInstances = LayoutFragment.Translate(_pastePlacementInstances, dx, dy);
        var placedRulers = LayoutFragment.Translate(_pastePlacementRulers, dx, dy);
        _pastePlacementShapes = null;
        _pastePlacementInstances = [];
        _pastePlacementInstanceBoxOnly = [];
        _pastePlacementRulers = [];
        InsertPastedMixed(placedShapes, placedInstances, "Paste", placedRulers);
    }

    /// <summary>Paste in Place (§3 of the brief; extended to a mixed copy by brief-L3a-followups.md
    /// §2) — original coordinates, no ghost, immediate; one undo entry covering both kinds.
    /// <paramref name="instances"/> defaults to none so every pre-existing (shape-only) call site is
    /// unaffected.</summary>
    public void PasteInPlace(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance>? instances = null,
                             IReadOnlyList<RulerAnnotation>? rulers = null) =>
        InsertPastedMixed(shapes, instances ?? [], "Paste in Place", rulers ?? []);

    // ── L3a instance paste (gate 11) — immediate placement, no ghost (see this file's own header
    // note on why shape paste and instance paste use different placement mechanisms) ───────────────

    /// <summary>Rebases a payload's instances' <c>CellRef</c>s to resolve correctly in THIS document
    /// (see <see cref="LayoutFragment.RebaseInstances"/>) — call before either paste variant below.
    /// brief-layout-testing-fixes.md item 2/R-fix-2: passes THIS document's own <see
    /// cref="WorkspaceRootDir"/> alongside <see cref="InstanceBaseDir"/> so a paste into a brand-new,
    /// never-saved document (no stable base directory yet) can still resolve — via the payload's
    /// workspace-relative or absolute cell-dir fallbacks — instead of silently keeping the source's own
    /// relative <c>CellRef</c> string, which resolves against nothing meaningful there.
    /// <b>Known, narrow gap, named rather than silently accepted:</b> unlike shapes (<see
    /// cref="RescaleFragment"/>/<see cref="LayoutFragment.Rescale"/>), an instance's X/Y/Rows/Cols/
    /// PitchX/PitchY are NOT rescaled across a DBU-per-micron mismatch between the source and this
    /// document — <c>LayoutFragment.Rescale</c> only ever walked <c>Payload.Shapes</c>. A same-
    /// resolution paste (by far the common case — matching a single technology's own convention) is
    /// unaffected; a cross-resolution paste of an instance would land at the wrong physical scale.
    /// Not attempted here — out of brief-L3a-followups.md's stated scope (§1-4 never mention DBU
    /// rescaling), a future brief's job.</summary>
    public IReadOnlyList<LayoutInstance> RebaseFragmentInstances(LayoutFragment.Payload payload) =>
        LayoutFragment.RebaseInstances(
            payload.Instances, payload.InstanceCellDirs, payload.InstanceWorkspaceRelativeDirs,
            InstanceBaseDir, WorkspaceRootDir);

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
    private void InsertPastedMixed(IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances,
                                   string description, IReadOnlyList<RulerAnnotation>? rulers = null)
    {
        rulers ??= [];
        if (shapes.Count == 0 && instances.Count == 0 && rulers.Count == 0) return;

        ResolvePortNumbers(shapes);

        int shapeInsertAt = Model.Shapes.Count;
        int instanceInsertAt = Model.Instances.Count;
        int rulerInsertAt = Model.Rulers.Count;

        IUiCommand? combined = null;
        if (shapes.Count > 0)
            combined = new Commands.Layout.ReplaceShapesCommand(Model, [], shapes, description);
        foreach (var inst in instances)
        {
            IUiCommand instCmd = new Commands.Layout.AddInstanceCommand(Model, inst);
            combined = combined is null ? instCmd : new CompositeCommand(combined, instCmd);
        }
        // §9B.9: the third kind joins the SAME composite, so a mixed paste is still one undo entry.
        foreach (var ruler in rulers)
        {
            IUiCommand rulerCmd = new Commands.Layout.AddRulerCommand(Model, ruler);
            combined = combined is null ? rulerCmd : new CompositeCommand(combined, rulerCmd);
        }
        Execute(combined!);

        ReplaceMixedSelection(
            Enumerable.Range(shapeInsertAt, shapes.Count),
            Enumerable.Range(instanceInsertAt, instances.Count),
            Enumerable.Range(rulerInsertAt, rulers.Count));
    }

    /// <summary>
    /// Give every pasted EM port a free number (owner request, 2026-08-09). A port number indexes the
    /// s-parameter matrix, so two ports naming the same one is not a cosmetic clash —
    /// <c>EmPortExtraction</c> refuses the whole extraction by name — and copy/paste is the one
    /// gesture that produces it by construction.
    ///
    /// <para><b>This is the same shape as the schematic's own <c>SchematicPasteCommand.ResolveNums</c></b>
    /// and for the same reason: the used set is seeded from the DESTINATION and updated between
    /// pasted ports, so an intra-batch collision (two ports copied together) is prevented as well as a
    /// collision with what was already there. The lowest free number is taken, matching
    /// <see cref="NextPortName"/> — a pasted port and a freshly placed one number identically.</para>
    ///
    /// <para>The user's own naming is preserved where it can be: the digit run inside the existing
    /// text is substituted, so <c>"Port 3"</c> becomes <c>"Port 5"</c> rather than <c>"P5"</c>. An
    /// UNNUMBERED port label is left alone — the extractor already assigns it the lowest free number,
    /// so it cannot collide and rewriting it would be inventing a name the user did not type.</para>
    ///
    /// <para>Called from <see cref="InsertPastedMixed"/>, the one funnel Paste, Paste in Place and
    /// Duplicate all go through, so the three cannot disagree. Safe to mutate in place: every caller
    /// hands over fresh clones (<c>LayoutFragment.Translate</c> / the fragment reader), never a shape
    /// the model still holds.</para>
    /// </summary>
    private void ResolvePortNumbers(IReadOnlyList<LayoutShape> shapes)
    {
        var pasted = shapes.OfType<LabelShape>().Where(l => l.IsPort).ToList();
        if (pasted.Count == 0) return;

        var used = new HashSet<int>();
        foreach (var s in Model.Shapes)
            if (s is LabelShape { IsPort: true } l && EmPortExtraction.TryParseNumber(l.Text, out int n))
                used.Add(n);

        int next = 1;
        foreach (var port in pasted)
        {
            if (!EmPortExtraction.TryParseNumber(port.Text, out int number)) continue;
            if (used.Add(number)) continue;                 // free in the destination — keep it

            while (!used.Add(next)) next++;
            port.Text = SubstitutePortNumber(port.Text, next);
        }
    }

    /// <summary>Replace the digit run in a port label's text, preserving whatever prefix the user
    /// typed. Falls back to the canonical <c>P{n}</c> form only when there is no digit run to
    /// substitute, which <see cref="ResolvePortNumbers"/>'s own caller has already ruled out.</summary>
    internal static string SubstitutePortNumber(string text, int number)
    {
        int start = -1, end = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i])) continue;
            if (start < 0) start = i;
            end = i;
        }
        return start < 0
            ? $"P{number}"
            : text[..start] + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + text[(end + 1)..];
    }
}
