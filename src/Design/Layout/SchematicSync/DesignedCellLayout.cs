// Owner: PCD-2 (brief-pcd-0 §5, seam 10; real body written by PCD-1's seams commit). Framework-free.
//
// The planner on an EMPTY layout at the values, as EmEvaluation.SchematicDisagreements already plans one: what a designed
// cell's copper is at a point, with no hand edit in the way (S1). Nothing is written but the planner's generated cells.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>A designed cell's layout at some values. Layout null with Refusal when it could not be planned.</summary>
public sealed record DesignedCellLayoutResult(LayoutView? Layout,
                                              IReadOnlyList<SchematicLayoutPlanner.ReportLine> Lines, string? Refusal);

/// <summary>brief-pcd-0 §5 seam 10: the one way a designed cell's layout is made at values.</summary>
public static class DesignedCellLayout
{
    /// <summary>
    /// The cell's layout at values (base SI, by parameter or VAR name): a copy of the schematic (or of
    /// <paramref name="schematic"/>, the designer's in-memory one), EmEvaluation.Apply(copy, values),
    /// SchematicLayoutPlanner.Plan onto a new empty LayoutView with connectParts: true and the cell's layout folder as
    /// base, ApplyTo; Lines = plan.Lines plus NoLayoutWarnings as warnings. Registers DesignedPCellResolver for
    /// workspaceRootDir while it plans.
    /// </summary>
    /// <remarks>The empty layout takes its database unit, display unit and <c>TechRef</c> from the cell's primary
    /// <c>.clay</c> when it has one, else the default database unit and the technology's display unit.</remarks>
    public static DesignedCellLayoutResult At(string cellDir, IReadOnlyDictionary<string, Value> values,
                                              string workspaceRootDir, Technology technology, string? techIdentity,
                                              SchematicEditModel? schematic = null)
    {
        string cellName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir)));
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string? clayPath = CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName is { } clay
            ? Path.Combine(layoutDir, clay) : null;
        try
        {
            SchematicEditModel model;
            if (schematic is not null)
            {
                // A copy: Apply rewrites VAR rows, and the designer's model is the user's.
                model = SchematicPersistence.Deserialize(SchematicPersistence.Serialize(schematic), schematicDir).model;
            }
            else if (CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName is { } csch)
            {
                model = SchematicPersistence.LoadFromFile(Path.Combine(schematicDir, csch)).model;
            }
            else return new DesignedCellLayoutResult(null, [], $"{cellName} has no primary schematic.");
            model.SchematicDirectory = schematicDir;

            EmEvaluation.Apply(model, values);

            var saved = clayPath is not null && File.Exists(clayPath) ? LayoutPersistence.LoadFromFile(clayPath) : null;
            var layout = new LayoutView
            {
                DbuPerMicron = saved?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron,
                TechRef      = saved?.TechRef,
                DisplayUnit  = saved?.DisplayUnit ?? technology.DefaultDisplayUnit,
            };

            using var registration = DesignedPCellResolver.Register(workspaceRootDir);
            var plan = SchematicLayoutPlanner.Plan(model, layout, schematicDir, workspaceRootDir, layoutDir, technology,
                                                   techIdentity, DiskCellResolver.Instance,
                                                   targetLayoutPath: clayPath ?? Path.Combine(layoutDir, cellName + ".clay"),
                                                   connectParts: true);
            plan.ApplyTo(layout);

            var lines = new List<SchematicLayoutPlanner.ReportLine>(plan.Lines);
            lines.AddRange(plan.NoLayoutWarnings.Select(w => new SchematicLayoutPlanner.ReportLine(
                "", w, SchematicLayoutPlanner.ReportSeverity.Warning)));
            return new DesignedCellLayoutResult(layout, lines, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new DesignedCellLayoutResult(null, [], $"{cellName}'s layout could not be planned at these values: {e.Message}");
        }
    }
}
