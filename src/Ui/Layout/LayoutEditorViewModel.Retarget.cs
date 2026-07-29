using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L1g — technology retargeting (docs/sonnet-briefs/brief-L1g-technology-retarget.md). All the
/// logic that decides WHERE each shape's layer goes lives in <see cref="LayoutLayerMapping"/> (pure,
/// framework-free, shared with cross-technology paste); this file is selection/undo/Messages plumbing
/// + <c>Commands.Layout.RetargetTechnologyCommand</c> wiring, mirroring how
/// <c>LayoutEditorViewModel.Clipboard.cs</c> and <c>.Booleans.cs</c> are organized.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// brief-foreign-documents.md R-fgn-3: fallback for a SCRATCH document (no path of its own yet) —
    /// tracks whichever workspace is CURRENTLY open, since a scratch layout has no "own" workspace
    /// until it is saved somewhere. Updated by <c>WorkspaceViewModel</c> whenever the current workspace
    /// changes (mirrors the old <c>WorkspaceTechDir</c> setter's own wiring). Once
    /// <see cref="CurrentLayoutPath"/> is set, <see cref="WorkspaceTechDir"/> ignores this entirely.
    /// </summary>
    internal string? FallbackWorkspaceTechDir { private get; set; }

    /// <summary>Root directory of the document's OWN ancestor workspace (nearest ancestor <c>.cws</c>
    /// walking up from <see cref="CurrentLayoutPath"/>), or null for a materialized document with none
    /// at all. Null for a scratch document too (it has no path to walk up from) — callers needing the
    /// scratch fallback read <see cref="FallbackWorkspaceTechDir"/> directly, since that already means
    /// "the tech/ folder," not the bare root.</summary>
    private string? OwnAncestorWorkspaceRootDir
    {
        get
        {
            if (CurrentLayoutPath is not { } path) return null;
            var cws = CircuitRF.Ui.Schematic.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path));
            return cws is null ? null : Path.GetDirectoryName(cws);
        }
    }

    /// <summary>
    /// Absolute path of the workspace <c>tech/</c> folder this document's OWN technology resolves
    /// against, or null when there is none. Computed LIVE on every access (never snapshotted) — for a
    /// materialized document, walks up from <see cref="CurrentLayoutPath"/>'s own directory to the
    /// nearest ancestor <c>.cws</c> (R-fgn-3: the document's OWN parent workspace, which may not be
    /// whichever workspace is currently open), so a Save-As to a different workspace is picked up
    /// automatically with nothing to re-wire; for a scratch document (no path yet) falls back to
    /// <see cref="FallbackWorkspaceTechDir"/> — whichever workspace is currently open.
    /// </summary>
    public string? WorkspaceTechDir
    {
        get
        {
            if (CurrentLayoutPath is not null)
                return OwnAncestorWorkspaceRootDir is { } root ? Path.Combine(root, "tech") : null;
            return FallbackWorkspaceTechDir;
        }
    }

    /// <summary>
    /// brief-foreign-documents.md §4: resolves the CURRENTLY open workspace's root directory (or null
    /// when none is open) — set once by <c>WorkspaceViewModel</c> at document-open time, same seam as
    /// <see cref="FallbackWorkspaceTechDir"/>. Read live (not snapshotted), so switching workspaces is
    /// picked up by <see cref="IsForeign"/>/<see cref="SourceWorkspaceName"/> automatically.
    /// </summary>
    internal Func<string?>? CurrentWorkspaceRootDirProvider { get; set; }

    /// <summary>
    /// brief-foreign-documents.md R-fgn-1/§4: true when this document's file does not belong to the
    /// CURRENTLY open workspace — determined purely from its own path, never from how it was opened or
    /// whether it is docked or torn off. A scratch document (no path yet) is never foreign — it belongs
    /// to whichever workspace is currently open, same as every other scratch-document convention in
    /// this codebase. A materialized document with NO ancestor workspace at all is foreign to
    /// whichever workspace (if any) is currently open — <see cref="SourceWorkspaceName"/> is null in
    /// that case ("Not part of any workspace").
    /// </summary>
    public bool IsForeign
    {
        get
        {
            if (CurrentLayoutPath is null) return false; // scratch — belongs to whatever's open
            var own = OwnAncestorWorkspaceRootDir;
            var current = CurrentWorkspaceRootDirProvider?.Invoke();
            if (own is null) return true; // a loose file — foreign to any/no currently-open workspace
            return !string.Equals(own, current, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The source workspace's name (its folder name) for marking (§4), or null when this
    /// materialized document has no ancestor workspace at all ("Not part of any workspace"). Only
    /// meaningful when <see cref="IsForeign"/> is true; computed the same live way either way.</summary>
    public string? SourceWorkspaceName
        => OwnAncestorWorkspaceRootDir is { } dir ? Path.GetFileName(dir) : null;

    /// <summary>The source workspace's own <c>.cws</c> path — for the edge band's "open it" affordance
    /// (§4 item 2). Null exactly when <see cref="SourceWorkspaceName"/> is null.</summary>
    public string? SourceWorkspaceCwsPath
        => OwnAncestorWorkspaceRootDir is { } dir ? Path.Combine(dir, ".cws") : null;

    /// <summary>
    /// Resolves the workspace-default technology (as if <c>TechRef</c> were null) — set once by
    /// <c>WorkspaceViewModel</c> at document-open time, same seam as <see cref="WorkspaceTechDir"/>.
    /// Null when this VM was never wired to a workspace (should not happen in practice, since even a
    /// scratch layout is opened through <c>WorkspaceViewModel</c>).
    /// </summary>
    public Func<TechResolution>? ResolveWorkspaceDefaultTech { get; internal set; }

    /// <summary>
    /// Resolves an ARBITRARY cell's technology given its own <c>TechRef</c> (may be null, meaning "use
    /// the workspace default" — the same convention every layout's own top-level resolution already
    /// follows) and the directory containing its <c>.clay</c>. Set once by <c>WorkspaceViewModel</c>,
    /// same seam as <see cref="WorkspaceTechDir"/>/<see cref="ResolveWorkspaceDefaultTech"/> — needed
    /// by brief-L3c-flatten-and-group.md's R-L3c-3 (cross-technology Flatten Hierarchy), the first
    /// place this codebase resolves a SUB-cell's own technology rather than always inheriting the
    /// embedding document's (L3a's own stated simplification, named as a future gap at the time).
    /// </summary>
    public Func<string?, string, TechResolution>? ResolveTechAt { get; internal set; }

    /// <summary>Snapshot of everything a retarget can change, for the command's Undo/Redo. Deliberately
    /// NOT the <see cref="LayoutView"/>'s <c>DbuPerMicron</c> — retargeting never touches it (§4 of the
    /// brief: resolution is a property of the layout, not of the technology).</summary>
    internal readonly record struct RetargetState(
        string? TechRef, Technology? Technology, string? ResolvedTechPath, LayoutUnit DisplayUnit, long SnapDbu);

    internal RetargetState CaptureRetargetState() => new(Model.TechRef, Technology, ResolvedTechPath, DisplayUnit, SnapDbu);

    /// <summary>Applies a captured (or newly resolved) state — the one place both
    /// <c>RetargetTechnologyCommand.Execute</c> and <c>.Undo</c> route through, so the two directions
    /// can never drift. Unit fields go through the ordinary <see cref="DisplayUnit"/>/<see cref="SnapDbu"/>
    /// setters (their existing side effects — writing <see cref="Model"/>, marking prefs-dirty — are
    /// exactly what a retarget's OPT-IN unit adoption should do too; see §4 point 3 of the brief).</summary>
    internal void ApplyRetargetState(RetargetState state)
    {
        Model.TechRef = state.TechRef;
        ResolvedTechPath = state.ResolvedTechPath;
        Technology = state.Technology;
        if (DisplayUnit != state.DisplayUnit) DisplayUnit = state.DisplayUnit;
        if (SnapDbu != state.SnapDbu) SnapDbu = state.SnapDbu;
    }

    /// <summary>
    /// Executes a confirmed retarget as ONE undoable command (§4 of the brief). <paramref name="mapping"/>
    /// is the user-settled (or, when <see cref="LayoutLayerMapping.RequiresConfirmation"/> was false,
    /// silently-accepted) layer mapping from <see cref="LayoutLayerMapping.Propose"/>; <paramref name="adoptUnits"/>
    /// gates whether <see cref="Technology.DefaultDisplayUnit"/>/<see cref="Technology.DefaultSnapDbu"/>
    /// are adopted — default OFF, opt-in only. Returns a summary for the caller to report via
    /// <see cref="ReportMessage"/> (§5), or null if there was nothing to do (target equals current
    /// state and no shape needs to move).
    /// </summary>
    public LayoutLayerMapping.Summary RetargetTo(
        string? newTechRef, TechResolution target, bool adoptUnits, IReadOnlyList<LayerMappingRow> mapping)
    {
        var sourceLayers = Technology?.Layers ?? (IReadOnlyList<LayerDef>)[];

        var keyMap = new Dictionary<LayerKey, LayerKey>();
        foreach (var row in mapping)
        {
            if (row.Choice is { Action: LayoutFragment.LayerReconciliationAction.MapToExisting, MapTarget: { } t })
                keyMap[row.Source] = t;
        }

        var layerChanges = new List<(int Index, LayerKey Before, LayerKey After)>();
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            var key = Model.Shapes[i].Layer;
            if (keyMap.TryGetValue(key, out var mapped) && mapped != key)
                layerChanges.Add((i, key, mapped));
        }

        var before = CaptureRetargetState();
        var after = new RetargetState(
            newTechRef,
            target.Tech,
            target.ResolvedPath,
            adoptUnits && target.Tech is not null ? target.Tech.DefaultDisplayUnit : DisplayUnit,
            adoptUnits && target.Tech is not null ? target.Tech.DefaultSnapDbu : SnapDbu);

        Execute(new Commands.Layout.RetargetTechnologyCommand(
            this, before, after, layerChanges, $"Change Technology to {target.Tech?.Name ?? "(no technology)"}"));

        ApplyAddToTechnologyChoices(mapping, sourceLayers, target.ResolvedPath);

        return new LayoutLayerMapping.Summary(target.Tech?.Name, Model.Shapes.Count, mapping);
    }

    /// <summary>Mirrors <see cref="ApplyFragmentReconciliation"/>'s Add-to-technology branch exactly —
    /// installs the source layer's own <see cref="LayerDef"/> into a live (unsaved) clone of the just-
    /// applied destination technology, via the same <see cref="RequestAddLayerToTechnology"/> seam
    /// paste already uses. Never writes the <c>.ctech</c> file directly.</summary>
    private void ApplyAddToTechnologyChoices(
        IReadOnlyList<LayerMappingRow> mapping, IReadOnlyList<LayerDef> sourceLayers, string? resolvedTechPath)
    {
        var toAdd = mapping.Where(r => r.Choice.Action == LayoutFragment.LayerReconciliationAction.AddToTechnology).ToList();
        if (toAdd.Count == 0 || resolvedTechPath is null || Technology is not { } destTech) return;

        var clone = TechPersistence.Deserialize(TechPersistence.Serialize(destTech));
        foreach (var row in toAdd)
        {
            if (clone.Layers.Any(l => l.Key == row.Source)) continue;
            var def = sourceLayers.FirstOrDefault(l => l.Key == row.Source)
                      ?? FallbackPalette.For(row.Source);
            clone.Layers.Add(def);
        }
        RequestAddLayerToTechnology?.Invoke(resolvedTechPath, clone);
    }
}
