using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Schematic;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Update Layout from Schematic, as the GUI runs it: <see cref="SchematicLayoutPlanner.Plan"/> decides what to
/// place where, and this turns the plan into ONE undoable command (R-L5-12: "the whole re-run is one undoable
/// action"). Since brief-sdl-0 R-sdl0-1 it holds no placement arithmetic of its own — no packing, no transform
/// composition, no resolution — only plan-to-command translation, so the headless callers that plan through the
/// same function cannot come to place parts differently from the menu command. A comment-stripped source scan
/// (<c>SchematicLayoutPlannerTests</c>) holds that shut.
///
/// <para>The plan's BOOKKEEPING (the parameter snapshots, the regeneration records, the orientation baselines of
/// parts that did not otherwise change) is written straight into the target here, outside the command, exactly
/// as it was before the planner moved: it is not itself undoable, matching the R-L5-13/14 report's convention
/// that what is reported is what happened. Every instance add, replacement and deletion, every ground tie and
/// pour, and every placement record is inside the returned command, never executed here.</para>
/// </summary>
public static class SchematicToLayoutGenerator
{
    /// <summary>
    /// <paramref name="Command"/> is null when nothing changed at all (R-L5-14: "say nothing when
    /// nothing changed" — the caller posts no Messages entry in that case). <paramref name="Lines"/>
    /// is per-instance, in schematic-component order; the caller applies R-L5-13's cap and trailing
    /// summary. <paramref name="NoLayoutWarnings"/> lists components with no resolvable layout view
    /// (§9 step 2), reported unconditionally (not counted against the cap — there is no realistic
    /// schematic with hundreds of un-laid-out component TYPES, only hundreds of instances).
    /// </summary>
    public sealed record GenerationResult(
        IUiCommand? Command,
        IReadOnlyList<ReportLine> Lines,
        int AddedCount,
        int UpdatedCount,
        int UnchangedCount,
        int RemovedCount,
        int OverwrittenParameterCount,
        IReadOnlyList<string> NoLayoutWarnings,
        Bbox AddedRegion = default,
        int DeletedCount = 0,
        bool OrientationLinksRecorded = false,
        GroundArtworkPlan? Ground = null)
    {
        public bool NothingChanged => Command is null && NoLayoutWarnings.Count == 0;

        /// <summary>The ground vias and pours this run draws, moves or removes (<see cref="GroundArtwork"/>) — already
        /// inside <see cref="Command"/>; the caller reports it with <see cref="SchematicLayoutPlanner.GroundArtworkReport"/>.</summary>
        public GroundArtworkPlan GroundArtwork => Ground ?? GroundArtworkPlan.None;
    }

    /// <summary>
    /// brief-L5-followups-3.md §2 (R-L5h-3): FORMERLY the reserved (Layer, Datatype) key a §9 step 4
    /// ratsnest pass emitted real, persisted <c>PathShape</c> geometry onto — a connectivity GUIDE
    /// treated as real artwork, the identical error already fixed once for pins (R-L5g-13/14). This
    /// generator no longer emits anything on this layer at all; the constant is kept SOLELY as the
    /// identity <see cref="RemoveRatsnestShapes"/> sweeps — no starter technology in this app uses
    /// layer 0 (both ship starting at 1, docs/design/layout-view.md §2.4), so sweeping it is safe:
    /// nothing legitimate is ever there.
    /// </summary>
    internal static readonly LayerKey RatsnestLayer = new(0, 900);

    /// <summary>
    /// brief-L5-followups-3.md §2 (R-L5h-4): strips any already-persisted ratsnest shapes from
    /// <paramref name="view"/> — this generator no longer emits them (R-L5h-3), so any that remain
    /// came from a <c>.clay</c> written before this fix. Returns the count removed (0 = nothing to
    /// clean). Pure/framework-free; the caller (<c>WorkspaceViewModel.GetOrCreateLayoutSession</c>,
    /// the ONE funnel every layout load — open-as-tab and push-in alike — goes through) decides what
    /// to do with a non-zero count (mark the session dirty so the cleanup actually persists on the
    /// next save, and report it).
    /// </summary>
    public static int RemoveRatsnestShapes(LayoutView view) => view.Shapes.RemoveAll(s => s.Layer == RatsnestLayer);

    public static GenerationResult Run(
        SchematicEditModel model,
        LayoutView target,
        string schematicDir,
        string workspaceRootDir,
        string targetLayoutBaseDir,
        Technology? technology,
        string? techIdentity,
        ICellResolver? cellResolver,
        string? targetLayoutPath = null,
        bool linkOrientation = true,
        double pourMarginHeights = GroundPourPlanner.DefaultMarginHeights,
        bool connectParts = true)
    {
        var plan = SchematicLayoutPlanner.Plan(
            model, target, schematicDir, workspaceRootDir, targetLayoutBaseDir, technology, techIdentity,
            cellResolver, targetLayoutPath, linkOrientation, pourMarginHeights,
            kitLayoutGenerator: KitLayoutGenerators.For, connectParts: connectParts);

        plan.ApplyBookkeeping(target);

        // A placement's record rides in one command with it, record FIRST: a composite takes its second command's
        // description, and the run's Undo entry still reads as the placement ("Place Instance"), as it always has.
        IUiCommand? chain = null;
        foreach (var edit in plan.Edits)
            chain = Chain(chain, edit switch
            {
                SchematicLayoutEdit.AddInstance add => new CompositeCommand(
                    new PlacementRecordCommand(target, add.Instance.SchematicId, add.Placement, null),
                    new AddInstanceCommand(target, add.Instance)),
                SchematicLayoutEdit.ReplaceInstance { Placement: { } placement } rep => new CompositeCommand(
                    new PlacementRecordCommand(target, rep.After.SchematicId, placement, rep.RenamedFrom),
                    new ReplaceInstanceCommand(target, rep.Index, rep.Before, rep.After)),
                SchematicLayoutEdit.ReplaceInstance rep => new ReplaceInstanceCommand(target, rep.Index, rep.Before, rep.After),
                SchematicLayoutEdit.DeleteInstances del => new DeleteInstancesCommand(target, del.Indices),
                SchematicLayoutEdit.AddShape shape => new AddShapeCommand(target, shape.Shape),
                _ => throw new InvalidOperationException($"Unknown layout edit {edit.GetType().Name}"),
            });
        if (GroundArtworkCommand(target, plan.Ground) is { } groundCommand) chain = Chain(chain, groundCommand);

        return new GenerationResult(chain, plan.Lines, plan.AddedCount, plan.UpdatedCount, plan.UnchangedCount,
                                    plan.RemovedCount, plan.OverwrittenParameterCount, plan.NoLayoutWarnings,
                                    plan.AddedRegion, plan.DeletedCount, plan.OrientationLinksRecorded, plan.Ground);
    }

    /// <summary>The plan as one undoable command — the shapes out and in, and the ground-via keys the view remembers —
    /// or null when it changes nothing. Shared with Draw Ground Pour.</summary>
    internal static IUiCommand? GroundArtworkCommand(LayoutView target, GroundArtworkPlan plan)
    {
        if (plan.IsEmpty) return null;
        IUiCommand? chain = null;
        var indices = SchematicLayoutPlanner.GroundRemovalIndices(target, plan);
        if (indices.Count > 0) chain = Chain(chain, new DeleteShapesCommand(target, indices));
        foreach (var shape in plan.Add) chain = Chain(chain, new AddShapeCommand(target, shape));
        if (plan.KeysAdded.Count > 0 || plan.KeysRemoved.Count > 0)
            chain = Chain(chain, new GroundViaKeysCommand(target, plan.KeysAdded, plan.KeysRemoved));
        return chain;
    }

    /// <summary>Records and forgets ground-via keys inside the run's undo entry, so an Undo of the run also forgets
    /// it ever placed them — and the next run places them again rather than reading them as deleted.</summary>
    private sealed class GroundViaKeysCommand(LayoutView view, IReadOnlyList<string> added, IReadOnlyList<string> removed) : IUiCommand
    {
        public string Description => "Ground vias";
        public void Execute()
        {
            foreach (string k in added) view.GroundViaKeys.Add(k);
            foreach (string k in removed) view.GroundViaKeys.Remove(k);
        }
        public void Undo()
        {
            foreach (string k in added) view.GroundViaKeys.Remove(k);
            foreach (string k in removed) view.GroundViaKeys.Add(k);
        }
    }

    /// <summary>brief-sdl-0 R-sdl0-2d: a placement record written inside the same command as the placement it
    /// describes, so an Undo of the run restores the table as it was — including the record a rename moved.</summary>
    private sealed class PlacementRecordCommand(LayoutView view, string? schematicId, GeneratedPlacement placement,
                                                string? renamedFrom) : IUiCommand
    {
        private GeneratedPlacement? _was, _wasRenamed;
        private bool _captured;

        public string Description => "Placement record";

        public void Execute()
        {
            if (!_captured)
            {
                _was = schematicId is { Length: > 0 } ? view.GeneratedPlacements.GetValueOrDefault(schematicId) : null;
                _wasRenamed = renamedFrom is not null ? view.GeneratedPlacements.GetValueOrDefault(renamedFrom) : null;
                _captured = true;
            }
            SchematicLayoutPlan.RecordPlacement(view, schematicId, placement, renamedFrom);
        }

        public void Undo()
        {
            if (schematicId is { Length: > 0 })
            {
                if (_was is null) view.GeneratedPlacements.Remove(schematicId);
                else view.GeneratedPlacements[schematicId] = _was;
            }
            if (renamedFrom is not null && _wasRenamed is not null) view.GeneratedPlacements[renamedFrom] = _wasRenamed;
        }
    }

    private static IUiCommand Chain(IUiCommand? existing, IUiCommand next)
        => existing is null ? next : new CompositeCommand(existing, next);
}
