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
    /// Absolute path of the workspace's <c>tech/</c> folder, or null when no workspace is open. Set
    /// once by <c>WorkspaceViewModel</c> at document-open time (mirrors <see cref="RequestAddLayerToTechnology"/>'s
    /// wiring) so the Change Technology picker can enumerate available <c>.ctech</c> files without
    /// this VM depending on <c>WorkspaceViewModel</c> directly.
    /// </summary>
    public string? WorkspaceTechDir { get; internal set; }

    /// <summary>
    /// Resolves the workspace-default technology (as if <c>TechRef</c> were null) — set once by
    /// <c>WorkspaceViewModel</c> at document-open time, same seam as <see cref="WorkspaceTechDir"/>.
    /// Null when this VM was never wired to a workspace (should not happen in practice, since even a
    /// scratch layout is opened through <c>WorkspaceViewModel</c>).
    /// </summary>
    public Func<TechResolution>? ResolveWorkspaceDefaultTech { get; internal set; }

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
