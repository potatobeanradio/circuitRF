// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-10 D1 — an EM component is one of three kinds, by what its geometry is a function of:
//   Schematic-driven  the cell's VARs and parameters, through SDL-0's planner (SDL-6's EM-capable cell);
//   3D-parametric     a .c3d whose dimensions use VARs linked to the cell's VARs or parameters, with no schematic driving it;
//   Fixed             nothing: a drawn layout with an EM setup, or a one-off EM result (<setup>_sim).
// The first two are EM-capable (they simulate at the values an instance gives them, and an optimiser can vary those); a
// fixed one simulates at its one geometry and has no variables of its own. check, explain and the instance's properties
// all state the kind through Of, so the three cannot disagree.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

/// <summary>brief-sdl-10 D1 — what an EM component's geometry follows.</summary>
public enum EmComponentKind
{
    /// <summary>Not an EM component at all: no EM view.</summary>
    None,
    /// <summary>Its layout is made from its schematic, so its copper follows its VARs and parameters.</summary>
    SchematicDriven,
    /// <summary>A 3D view whose dimensions use VARs linked to the cell's VARs or parameters; no schematic drives it.</summary>
    ThreeDParametric,
    /// <summary>An EM view whose geometry follows nothing: it simulates at its one geometry.</summary>
    Fixed,
}

/// <summary>A cell's EM kind and how it is said.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Sentence">One line stating it — what the instance's properties, <c>check</c> and <c>explain</c> print.</param>
/// <param name="HasCircuitModel">Whether a circuit model exists for <c>em=correct</c> (D2): the cell's schematic.</param>
/// <param name="Linked">For a 3D-parametric cell, the names its 3D view takes from the cell.</param>
public sealed record EmComponentKindReport(EmComponentKind Kind, string Sentence, bool HasCircuitModel, IReadOnlyList<string> Linked)
{
    /// <summary>Whether the geometry follows the cell's variables (D1's first two kinds).</summary>
    public bool Varies => Kind is EmComponentKind.SchematicDriven or EmComponentKind.ThreeDParametric;

    /// <summary>The kind's name as a reader sees it.</summary>
    public string Label => Kind switch
    {
        EmComponentKind.SchematicDriven  => "schematic-driven",
        EmComponentKind.ThreeDParametric => "3D-parametric",
        EmComponentKind.Fixed            => "fixed",
        _                                => "not an EM component",
    };
}

public static class EmComponentKinds
{
    /// <summary>The kind of <paramref name="cellDir"/>, with the line that says it. Never throws.</summary>
    public static EmComponentKindReport Of(string cellDir)
    {
        string dir;
        try { dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir)); }
        catch (Exception) { return None(); }
        string name = Path.GetFileName(dir);
        bool hasSchematic = Primary(dir, ViewType.Schematic) is not null;

        // A one-off EM result: the cell a finished run makes (EmSnpSchematic), an SnP of the run's Touchstone.
        if (name.EndsWith(EmSnpSchematic.CellSuffix, StringComparison.Ordinal) && IsEmResultCell(dir) is { } snp)
            return new(EmComponentKind.Fixed,
                $"Fixed EM component: '{name}' is the result of one EM run ({snp}), so its geometry follows none of its " +
                "variables and it has no variables of its own to vary; the circuitry around it can still be tuned and optimised.",
                hasSchematic, []);

        var target = EmCapability.Resolve(dir, null, null, out _, out bool several);
        if (target is null && several)
            target = EmCapability.SetupsFor(dir).FirstOrDefault() is { } first ? EmCapability.Resolve(dir, first, null, out _) : null;
        if (target is { ThreeDOnly: false })
            return new(EmComponentKind.SchematicDriven,
                $"Schematic-driven EM component: {name}'s layout is made from its schematic, so its copper follows its VARs and " +
                "parameters — an instance's own values give that placement its own geometry.",
                true, []);
        if (target is { ThreeDOnly: true } t3)
        {
            var linked = EmCapability.LinkedNamesUsed(t3.C3dPath!, dir);
            string names = string.Join(", ", linked);
            return new(EmComponentKind.ThreeDParametric,
                $"3D-parametric EM component: the dimensions of {name}'s 3D view use {names}, linked to the cell's " +
                (hasSchematic
                    ? "values; no schematic drives its geometry, and its schematic is its circuit model."
                    : "parameters. It has no schematic, so no circuit model: it is simulated by EM only (em=direct)."),
                hasSchematic, linked);
        }

        // An EM view that follows nothing: a drawn layout with a .cem, or a 3D view whose dimensions use no linked VAR.
        if (HasEmView(dir) is { } view)
            return new(EmComponentKind.Fixed,
                $"Fixed EM component: '{name}' has an EM view ({view}) but its geometry follows none of its variables " +
                (Primary(dir, ViewType.Layout) is { } clay && !MadeFromSchematic(clay)
                    ? "(its layout was drawn, not made from its schematic)" : "(no linked VAR of its 3D view is used by a dimension)") +
                ", so it simulates by EM at its one geometry and has no variables of its own to vary.",
                hasSchematic, []);
        return None();

        static EmComponentKindReport None() => new(EmComponentKind.None, "Not an EM component: it has no EM view.", true, []);
    }

    /// <summary>The Touchstone file name an EM-result cell's SnP reads, or null when the cell is not one.</summary>
    private static string? IsEmResultCell(string dir)
    {
        try
        {
            if (Primary(dir, ViewType.Schematic) is not { } csch) return null;
            var model = SchematicPersistence.LoadFromFile(csch).model;
            var snp = model.Components.FirstOrDefault(c => c.Symbol == SymbolKind.Snp && c.InstanceName == EmSnpSchematic.SnpInstanceName);
            var file = snp?.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression.Trim('"');
            return file is { Length: > 0 } ? Path.GetFileName(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static string? HasEmView(string dir)
    {
        try
        {
            if (WorkspaceRootFinder.WorkspaceDirOf(dir) is not { } ws) return null;
            if (Primary(dir, ViewType.Layout) is { } clay
                && EmSetupResolver.FindSetupsForLayout(Path.Combine(ws, ".cws"), clay).FirstOrDefault() is { } cem)
                return Path.GetFileName(cem);
            if (Primary(dir, ViewType.ThreeD) is { } c3d
                && C3dSetups.Read(C3dPersistence.LoadFromFile(c3d))
                            .FirstOrDefault(r => r.Setup is { Problem3D: CircuitRF.Engine.Em3d.Em3dProblemType.Driven }) is { } s)
                return $"{Path.GetFileNameWithoutExtension(c3d)}#{s.Name}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException) { }
        return null;
    }

    private static bool MadeFromSchematic(string clay)
    {
        try { return LayoutPersistence.LoadFromFile(clay).GeneratedPlacements.Count > 0; }
        catch (Exception) { return false; }
    }

    internal static string? Primary(string cellDir, ViewType type)
    {
        var primary = CellFolder.ResolvePrimary(cellDir, type);
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null) return null;
        return Path.Combine(CellFolder.SubFolderPath(cellDir, type), primary.ResolvedName);
    }
}
