using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Matching;

/// <summary>Whether Flatten to Cell can run right now, and — when it cannot — why, in one line.</summary>
/// <param name="CanRun">True when <see cref="MatchFlattenService.Run"/> would proceed.</param>
/// <param name="Reason">The tooltip and the disabled menu item's explanation. Never empty.</param>
/// <param name="ParentDir">Where the cell would be written; null when <paramref name="CanRun"/> is false.</param>
/// <param name="DefaultName">The seeded cell name, free of collisions when a parent is known.</param>
public sealed record MatchFlattenAvailability(
    bool CanRun, string Reason, string? ParentDir, string DefaultName);

/// <summary>
/// The ONE place Flatten to Cell is driven from — the Designer's footer button and the schematic's
/// context menu both come through here (brief §4), so the two cannot disagree about what flatten
/// does or about when it is offered.
/// </summary>
public static class MatchFlattenService
{
    /// <summary>Whether the given component can be flattened, and where to.</summary>
    public static MatchFlattenAvailability Availability(SchematicViewModel? vm, EditableComponent? comp)
    {
        if (vm is null || comp is null || comp.Symbol != SymbolKind.Match)
            return new(false, "Flatten to Cell acts on a Match component.", null, "MN_match");

        string seed = MatchFlatten.DefaultCellName(comp.InstanceName);

        var design = MatchFlatten.TryReadDesign(comp);
        if (design is null)
            return new(false,
                $"{comp.InstanceName}'s Design parameter could not be decoded, so there is no ladder "
                + "to write. Open the Match Designer and repair it first.", null, seed);

        var rebuild = MatchRebuild.Rebuild(design);
        if (rebuild.Network is null)
            return new(false,
                $"{comp.InstanceName} does not synthesise, so there is no ladder to write. "
                + (rebuild.Refusal?.Message ?? ""), null, seed);

        if (vm.EditModel.SchematicDirectory is null)
            return new(false,
                "Save this schematic first — a flattened cell is referenced by a path relative to the "
                + "schematic that instantiates it, and a scratch schematic has no directory.", null, seed);

        string? root = vm.WorkspaceRoot;
        if (root is null || !Directory.Exists(root))
            return new(false,
                "Flatten to Cell needs an open workspace to create the new cell in.", null, seed);

        return new(true,
            $"Write {comp.InstanceName}'s ladder as ordinary L and C components in a new cell, with "
            + "both terminations carried along disabled and the design recorded in the cell's annotation.",
            root, MatchFlatten.SuggestFreeName(root, seed));
    }

    /// <summary>
    /// The same question for a <b>standalone</b> Designer — one opened from Tools ▸ Match Designer,
    /// with no component and no schematic behind it (owner, 2026-08-20).
    /// </summary>
    /// <remarks>
    /// Two of <see cref="Availability"/>'s four refusals cannot apply here and are deliberately not
    /// re-stated: there is no schematic to have been saved, and no workspace the destination has to
    /// sit inside — the cell a standalone Designer writes is referenced by nothing, so nothing breaks
    /// when it is moved. What remains is the one that always matters: a design that does not
    /// synthesise has no ladder to write.
    /// </remarks>
    /// <param name="rebuild">The Designer's current rebuild.</param>
    /// <param name="instanceName">What the Designer calls itself — seeds the cell name.</param>
    /// <param name="startingDir">The open workspace root, or null. A starting folder only.</param>
    public static MatchFlattenAvailability StandaloneAvailability(
        MatchRebuildResult? rebuild, string instanceName, string? startingDir)
    {
        string seed = MatchFlatten.DefaultCellName(instanceName);

        if (rebuild?.Network is null)
            return new(false,
                "This design does not synthesise, so there is no ladder to write. "
                + (rebuild?.Refusal?.Message ?? ""), null, seed);

        string dir = startingDir is not null && Directory.Exists(startingDir)
            ? startingDir
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return new(true,
            "Write this ladder as ordinary L and C components in a new cell, with both terminations "
            + "carried along disabled and the design recorded in the cell's annotation. Choose where "
            + "it goes — this Designer is not attached to a schematic, so the cell is referenced by "
            + "nothing until you place it.",
            dir, MatchFlatten.SuggestFreeName(dir, seed));
    }

    /// <summary>What one Flatten did, or why it did not.</summary>
    /// <param name="Ok">True when the cell was written.</param>
    /// <param name="Message">What to post to the Messages region — a success line or a refusal.</param>
    /// <param name="CellDir">The new cell folder, when one was written.</param>
    /// <param name="Replacement">The instance that replaced the Match, when the checkbox was on.</param>
    public sealed record RunResult(bool Ok, string Message, string? CellDir, EditableComponent? Replacement);

    /// <summary>
    /// Writes the cell and (optionally) replaces the instance, as one undoable command on the owning
    /// schematic's stack.
    /// </summary>
    /// <param name="vm">The schematic that owns the <c>Match</c>.</param>
    /// <param name="comp">The <c>Match</c> being flattened.</param>
    /// <param name="parentDir">Where the cell folder goes — <see cref="Availability"/>'s ParentDir.</param>
    /// <param name="cellName">The user's chosen name.</param>
    /// <param name="replaceInPlace">The dialog's checkbox, on by default (§11.2).</param>
    /// <param name="stampedUtc">The date the annotation quotes; defaults to now.</param>
    /// <param name="significantDigits">
    /// The digit count the cell's element values are written at — the owning Designer's readout
    /// setting. The schematic's context menu has no Designer open and so takes the default.
    /// </param>
    public static RunResult Run(
        SchematicViewModel vm, EditableComponent comp, string parentDir, string cellName,
        bool replaceInPlace, DateTime? stampedUtc = null,
        int significantDigits = MatchDesignerSettings.DefaultSignificantDigits)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(comp);

        var availability = Availability(vm, comp);
        if (!availability.CanRun) return new(false, availability.Reason, null, null);

        string? reason = NameValidator.Validate(cellName);
        if (reason is not null)
            return new(false, $"Flatten to Cell: invalid cell name '{cellName}': {reason}", null, null);

        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir))
            return new(false,
                $"Flatten to Cell: a cell named '{cellName}' already exists. Choose another name — "
                + "flattening never writes over a cell that is already in the workspace.", null, null);

        var design = MatchFlatten.TryReadDesign(comp)!;
        var rebuild = MatchRebuild.Rebuild(design);
        var cellSchematic = MatchFlatten.BuildSchematic(
            rebuild, design, comp.InstanceName, stampedUtc, significantDigits);

        EditableComponent? replacement = null;
        if (replaceInPlace)
        {
            string cellRef = Path.GetRelativePath(vm.EditModel.SchematicDirectory!, cellDir);
            replacement = new EditableComponent
            {
                // "X", the prefix every other cell instance in this schematic already uses — the
                // replacement is a cell reference now, and naming it MN2 would claim a component type
                // it no longer is.
                InstanceName = SchematicEditModel.NextAvailableName(
                    vm.EditModel.Components.Where(c => c.Id != comp.Id), "X"),
                Symbol = SymbolKind.Generic,   // placeholder; rendering resolves through CellRef
                CellRef = cellRef,
                X = comp.X, Y = comp.Y,
                Rotation = comp.Rotation,
                MirrorX = comp.MirrorX,
                ShowTypeLabel = comp.ShowTypeLabel,
                ShowInstanceName = comp.ShowInstanceName,
            };
            // NO parameters. The design rides on the CELL (CcellFile.MatchDesign), not on the
            // instance: a cell's declared parameters are seeded onto every placement as overrides,
            // and an override is evaluated as an EXPRESSION at elaboration — a base64 blob is not
            // one, so a Design parameter here would make every flattened cell refuse to elaborate.
        }

        var command = new FlattenMatchCommand(
            vm.EditModel, comp, replacement, parentDir, cellName, cellSchematic, design, vm.MessageSink);

        try
        {
            vm.Execute(command);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"Flatten to Cell: {e.Message}", null, null);
        }

        int elements = cellSchematic.Components.Count(
            c => c.Disable == DisableState.None
                 && c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor);
        string tail = replacement is null
            ? $"{comp.InstanceName} was left in place."
            : $"{comp.InstanceName} was replaced by {replacement.InstanceName}; the wires are unchanged.";

        return new(true,
            $"Flatten to Cell: wrote '{cellName}' ({elements.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
            + $"elements, both terminations carried disabled). {tail}",
            cellDir, replacement);
    }

    /// <summary>
    /// Writes the cell for a <b>standalone</b> Designer: no instance to replace, no undo stack to
    /// record on, no workspace to be inside of.
    /// </summary>
    /// <remarks>
    /// It goes through <see cref="MatchFlatten.Write"/> directly rather than through
    /// <c>FlattenMatchCommand</c>, because that command's whole job is the half this case does not
    /// have — swapping the <c>Match</c> for a cell instance, and undoing both together. The write
    /// itself is <see cref="MatchFlatten.Write"/>'s either way, so the two entry points still produce
    /// the same cell from the same design; only the surrounding transaction differs.
    ///
    /// <para><b>The write is not undoable, and that is stated rather than pretended otherwise</b> —
    /// a standalone Designer has no stack, and inventing one that could delete a folder the user has
    /// since edited would be a worse answer than "the cell is on disk now".</para>
    /// </remarks>
    /// <inheritdoc cref="Run" path="/param[@name='significantDigits']"/>
    public static RunResult RunStandalone(
        MatchRebuildResult? rebuild, MatchDesign design, string instanceName,
        string parentDir, string cellName, DateTime? stampedUtc = null,
        int significantDigits = MatchDesignerSettings.DefaultSignificantDigits)
    {
        ArgumentNullException.ThrowIfNull(design);

        var availability = StandaloneAvailability(rebuild, instanceName, parentDir);
        if (!availability.CanRun) return new(false, availability.Reason, null, null);

        string? reason = NameValidator.Validate(cellName);
        if (reason is not null)
            return new(false, $"Flatten to Cell: invalid cell name '{cellName}': {reason}", null, null);

        string cellDir = Path.Combine(parentDir, cellName);
        if (Directory.Exists(cellDir))
            return new(false,
                $"Flatten to Cell: a cell named '{cellName}' already exists here. Choose another "
                + "name — flattening never writes over a cell that is already there.", null, null);

        try
        {
            var cellSchematic = MatchFlatten.BuildSchematic(
                rebuild!, design, instanceName, stampedUtc, significantDigits);
            MatchFlatten.Write(parentDir, cellName, cellSchematic, design);

            int elements = cellSchematic.Components.Count(
                c => c.Disable == DisableState.None
                     && c.Symbol is SymbolKind.Inductor or SymbolKind.Capacitor);

            return new(true,
                $"Flatten to Cell: wrote '{cellName}' ({elements.ToString(System.Globalization.CultureInfo.InvariantCulture)} "
                + "elements, both terminations carried disabled) to " + cellDir + ".",
                cellDir, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                    or ArgumentException or InvalidOperationException)
        {
            return new(false, $"Flatten to Cell: {e.Message}", null, null);
        }
    }
}
