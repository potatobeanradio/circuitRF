using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.PCells;

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
}
