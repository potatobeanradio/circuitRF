using System;
using System.Collections.Generic;
using System.Linq;
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
    public bool CanCopySelection => ValidSelectedIndices.Count > 0;
    public bool CanDuplicateSelection => ValidSelectedIndices.Count > 0;

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
        if (indices.Count == 0) return null;
        var shapes = indices.Select(i => Model.Shapes[i]).ToList();
        return LayoutFragment.Build(shapes, Technology, Model.DbuPerMicron);
    }

    /// <summary>Cut = Copy (the caller writes to the system clipboard BEFORE calling this) then
    /// Delete, as ONE undo entry — <see cref="DeleteSelection"/> already is exactly that one
    /// command.</summary>
    public void CutSelectionAfterCopy() => DeleteSelection();

    // ── Duplicate — internal copy, deliberately bypasses the system clipboard ────────────────────

    /// <summary>Clones the selection and places it offset by one snap step (§4 of the brief) as ONE
    /// undo entry, then selects the new shapes. Never touches the system clipboard — clobbering the
    /// user's clipboard as a side effect of Duplicate is a small betrayal people notice.</summary>
    public void Duplicate()
    {
        var indices = ValidSelectedIndices;
        if (indices.Count == 0) return;
        var shapes = indices.Select(i => Model.Shapes[i]).ToList();
        long step = OneSnapStepDbu;
        var placed = LayoutFragment.Translate(shapes, step, step);
        InsertPastedShapes(placed, "Duplicate");
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

    /// <summary>Layer keys in <paramref name="shapes"/> the current <see cref="Technology"/> does
    /// not define (R-L1f-3) — the caller (the view) prompts once per key, offering Keep-as-unknown /
    /// Map to existing / Add to the technology, before calling
    /// <see cref="ApplyFragmentReconciliation"/>.</summary>
    public IReadOnlyList<LayerKey> GetMissingFragmentLayers(IReadOnlyList<LayoutShape> shapes) =>
        LayoutFragment.GetMissingLayers(shapes, Technology);

    /// <summary>Display name for a fragment layer key, preferring the fragment's own captured
    /// <see cref="LayerDef"/> (the layer's name AS the source technology called it) over the raw
    /// numeric key — used by the reconciliation prompt.</summary>
    public static string FragmentLayerDisplayName(LayerKey key, IReadOnlyList<LayerDef> fragmentLayers) =>
        fragmentLayers.FirstOrDefault(l => l.Key == key)?.Name ?? $"{key.Layer}/{key.Datatype}";

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

    private IReadOnlyList<LayoutShape>? _pastePlacementShapes;
    private long _pastePlacementAnchorX;
    private long _pastePlacementAnchorY;
    private long _pasteCursorX;
    private long _pasteCursorY;

    /// <summary>True while a Paste ghost is attached to the cursor, waiting for a click to commit or
    /// Escape to cancel.</summary>
    public bool IsPastePlacementActive => _pastePlacementShapes is not null;

    /// <summary>
    /// Begins the Ctrl/Cmd+V "ghost follows the cursor" placement (§3 of the brief) with already
    /// rescaled + reconciled shapes and their (destination-DBU) anchor. The ghost renders at the
    /// anchor position (zero offset) until the first pointer move arrives, then tracks the snapped
    /// cursor exactly — a click (<see cref="OnPointerPressed"/>) places it there as one undo entry;
    /// Escape (<see cref="OnKeyDown"/>) cancels with no command pushed.
    /// </summary>
    public void BeginPastePlacement(IReadOnlyList<LayoutShape> shapes, long anchorX, long anchorY)
    {
        if (shapes.Count == 0) return;
        _pastePlacementShapes = shapes;
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
        RebuildOverlay();
    }

    private void CommitPastePlacement()
    {
        if (_pastePlacementShapes is not { Count: > 0 } shapes) return;
        long dx = _pasteCursorX - _pastePlacementAnchorX;
        long dy = _pasteCursorY - _pastePlacementAnchorY;
        var placed = LayoutFragment.Translate(shapes, dx, dy);
        _pastePlacementShapes = null;
        InsertPastedShapes(placed, "Paste");
    }

    /// <summary>Paste in Place (§3 of the brief) — original coordinates, no ghost, immediate; one
    /// undo entry.</summary>
    public void PasteInPlace(IReadOnlyList<LayoutShape> shapes) => InsertPastedShapes(shapes, "Paste in Place");

    /// <summary>Shared commit for Paste / Paste in Place / Duplicate: appended (topmost within their
    /// layers, §3) via an empty-removed-set <c>ReplaceShapesCommand</c> — one undo entry, and the
    /// newly placed shapes become the selection (§3: "the next action operates on what was just
    /// placed").</summary>
    private void InsertPastedShapes(IReadOnlyList<LayoutShape> shapes, string description)
    {
        if (shapes.Count == 0) return;
        int insertAt = Model.Shapes.Count;
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, [], shapes, description));
        SetSelection(Enumerable.Range(insertAt, shapes.Count));
    }
}
