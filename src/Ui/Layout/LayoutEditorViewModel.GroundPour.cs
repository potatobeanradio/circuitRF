using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Draw Ground Pour (designer feedback rounds 10 and 11): the generated ground artwork — the pour under the microstrip
/// lines and Update Layout's ground vias, and those vias themselves — redrawn from where the parts are NOW, as one undo
/// entry. Update Layout from Schematic does the same as part of its run; this is the command for after the designer
/// has rearranged the board. It adds or removes no ground via (it has no schematic to ask which pins are on ground):
/// the ones already placed follow their pads. The geometry is <see cref="GroundArtwork"/>'s; this file is the undo and
/// the Messages.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>What <see cref="DrawGroundPour"/> would do now.</summary>
    internal GroundArtworkPlan PlanGroundArtwork()
        => GroundArtwork.Plan(Model, Technology, InstanceBaseDir, Model.Instances, grounded: null, inSchematic: null,
                              ViewModels.WorkspaceViewModel.ReadGroundPourMarginHeights(
                                  WorkspaceRootDir is { Length: > 0 } root ? Path.Combine(root, ".cws") : null));

    /// <summary>Redraws the generated ground artwork as ONE undo entry and says what it did; says why when there is
    /// nothing to draw.</summary>
    public void DrawGroundPour()
    {
        if (Technology is null)
        {
            ReportError("Draw Ground Pour: this layout has no technology, so there is no stackup to name a ground plane.");
            return;
        }
        var plan = PlanGroundArtwork();
        if (SchematicToLayoutGenerator.GroundArtworkCommand(Model, plan) is { } command) Execute(command);

        var lines = SchematicLayoutPlanner.GroundArtworkReport(plan, Model, DisplayUnit);
        if (lines.Count == 0)
        {
            _messageSink?.Info("Draw Ground Pour: nothing to draw or redraw. It pours the plane placed microstrip lines " +
                               "(MLIN, MBEND, MTEE, MCROSS, MTAPER, MKLOPF) and Update Layout's ground vias return " +
                               "through, on a ground layer carrying no copper you drew yourself, and redraws a pour it " +
                               "drew before when the parts have moved.");
            return;
        }
        foreach (var line in lines)
        {
            if (line.Severity == SchematicLayoutPlanner.ReportSeverity.Warning) _messageSink?.Warning("Draw Ground Pour: " + line.Text);
            else _messageSink?.Success("Draw Ground Pour: " + line.Text);
        }
    }

    // ── R-dfr34-5-4: a ground via moves with its part ────────────────────────────────────────────
    //
    // Update Layout places a ground via beside a part's grounded pad; until the next update it stayed where it was put,
    // so dragging a part onto the board left its via 160 mm behind. A move of an instance now carries the vias whose
    // tag names it, and re-records each via's placed-at so the next Update Layout still treats it as its own.

    /// <summary>The move drag's following ground-via shapes, read once per drag for the live preview.</summary>
    private IReadOnlyList<int>? _groundViaFollowers;

    private HashSet<string> SelectedInstanceSchematicIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (int i in _selectedInstanceIndices)
            if (i >= 0 && i < Model.Instances.Count && Model.Instances[i].SchematicId is { Length: > 0 } sid) ids.Add(sid);
        return ids;
    }

    /// <summary>The ground-via shapes a move of the selected instances carries, not counting those the selection
    /// already moves.</summary>
    private IReadOnlyList<int> GroundViaFollowersOfDrag()
    {
        if (_groundViaFollowers is not null) return _groundViaFollowers;
        if (_selectedInstanceIndices.Count == 0) return _groundViaFollowers = [];
        var moving = new HashSet<int>(MovableSelectedIndices(_selectedIndices));
        return _groundViaFollowers = GroundArtwork.Followers(Model, SelectedInstanceSchematicIds(), 0, 0).Shapes
                                                  .Where(i => !moving.Contains(i)).ToList();
    }

    /// <summary>The command that moves the selected instances' ground vias with them by (dx, dy) — joined to the move's
    /// own composite, so it is the same undo entry — or null when no via follows.</summary>
    private IUiCommand? GroundViasFollowing(IReadOnlyList<int> movingShapes, long dx, long dy)
    {
        _groundViaFollowers = null;
        if ((dx == 0 && dy == 0) || _selectedInstanceIndices.Count == 0) return null;
        var (shapes, retag) = GroundArtwork.Followers(Model, SelectedInstanceSchematicIds(), dx, dy);
        if (shapes.Count == 0) return null;
        var moving = new HashSet<int>(movingShapes);
        var extra = shapes.Where(i => !moving.Contains(i)).ToList();
        IUiCommand? combined = extra.Count > 0 ? new Commands.Layout.MoveShapesCommand(Model, extra, dx, dy) : null;
        foreach (var (index, tag) in retag)
        {
            var shape = Model.Shapes[index];
            IUiCommand cmd = new Commands.Layout.SetShapeFieldCommand<string?>(
                Model, "Move", shape.Generated, tag, v => shape.Generated = v, LayoutChangeInfo.Updated([index]));
            combined = combined is null ? cmd : new CompositeCommand(combined, cmd);
        }
        return combined;
    }
}
