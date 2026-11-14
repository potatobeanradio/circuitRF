// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-7 R-sdl7-8 — Apply. Until applied, an optimisation's best values exist only in its result; the schematic's
// VAR block and its saved layout still hold the starting geometry. Apply writes each best value where its name is
// defined (the VAR row's expression, spelled as Push spells it, so the design keeps its own structure), runs Update
// Layout from Schematic (the planner, SDL-4's rules, so hand edits are kept), and checks that the saved layout's EM
// cache key is the key of the EM-verified best point: the layout on disk is exactly the geometry that was solved. A
// mismatch is reported, never ignored.
//
// This is the headless half — the CLI's and MCP's --apply. The GUI's write-back is Push through each document's own
// session (one undo step per document), then the same Update Layout and the same check (KeyOfLayout).

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Optimization;

/// <summary>What Apply did.</summary>
/// <param name="Written">The value keys written into the schematic.</param>
/// <param name="NotWritten">Keys with no VAR row to write to, each with why.</param>
/// <param name="LayoutKey">The saved layout's geometry key afterwards; null when the layout was not updated.</param>
/// <param name="ExpectedKey">The best point's key, as the run reported it; null when it reported none.</param>
/// <param name="Refusal">Why nothing (or not everything) was done; null when it was.</param>
/// <param name="LayoutLines">Update Layout's warnings — a loop that does not close at the applied values, say.</param>
public sealed record EmApplyResult(
    IReadOnlyList<string> Written, IReadOnlyList<string> NotWritten, string? LayoutKey, string? ExpectedKey, string? Refusal,
    IReadOnlyList<string> LayoutLines)
{
    /// <summary>Whether the saved layout is the geometry that was solved. brief-sdl-10 — a parent placing several EM
    /// components has one key per distinct geometry (<c>a+b</c>); each must be one EM verified.</summary>
    public bool KeyMatches => Refusal is null && LayoutKey is not null &&
                              (ExpectedKey is null || LayoutKey.Split('+').All(k => ExpectedKey.Split('+').Contains(k, StringComparer.Ordinal)));

    /// <summary>brief-sdl-10 D7 — a schematic placing EM components with no layout geometry to check them in: it has no layout
    /// made from it, or its layout places no schematic-driven EM component. The clause saying which; null otherwise. Apply
    /// then succeeds with nothing compared (<see cref="Succeeded"/>), rather than failing a comparison that has no layout.</summary>
    public string? NothingToVerify { get; init; }

    /// <summary>brief-sdl-10 — EM components placed inside another placed cell (<c>Y1.X1</c>): the parent's layout shows that
    /// cell's own layout, so their copper there was not checked against what EM verified.</summary>
    public IReadOnlyList<string> Unchecked { get; init; } = [];

    /// <summary>Whether Apply did what it should: nothing refused, and the saved layout is the geometry EM verified, or there
    /// was no layout geometry to check.</summary>
    public bool Succeeded => Refusal is null && (NothingToVerify is not null || KeyMatches);

    /// <summary>One sentence for a status line or the CLI.</summary>
    public string Sentence => (Refusal is { } why ? why
        : NothingToVerify is { } nothing ? $"Applied {Written.Count} value{(Written.Count == 1 ? "" : "s")}; {nothing}."
        : $"Applied {Written.Count} value{(Written.Count == 1 ? "" : "s")} and updated the layout; " +
          (KeyMatches ? $"the saved layout is the geometry EM verified ({LayoutKey})."
                      : $"the saved layout's geometry {LayoutKey} is NOT the one EM verified ({ExpectedKey}): what is on disk was " +
                        "not solved. Re-run the optimisation from these values, or simulate the analysis by EM to solve it."))
        + (Unchecked.Count > 0 ? " " + EmApply.UncheckedSentence(Unchecked) : "");
}

public static class EmApply
{
    /// <summary>
    /// <paramref name="values"/> (value key → value text, as a run's best values) written into
    /// <paramref name="model"/>'s VAR rows — the number in the row, the unit in its column, as Push writes them. Returns
    /// the keys written; a value equal to the row's is not rewritten, and a key with no VAR row is in
    /// <paramref name="notWritten"/>.
    /// </summary>
    public static IReadOnlyList<string> WriteValues(SchematicEditModel model, IReadOnlyDictionary<string, string> values,
                                                   out List<string> notWritten)
    {
        notWritten = [];
        var written = new List<string>();
        var rows = model.Components.Where(c => c.Symbol == SymbolKind.Var).SelectMany(c => c.Parameters).ToList();
        foreach (var (key, text) in values)
        {
            var row = rows.FirstOrDefault(p => p.Name.Trim() == key);
            // brief-sdl-10 D7 — an instance's own value (X1.w50) is written to that instance, never to its cell.
            if (row is null && InstanceRow(model, key) is { } own) row = own;
            if (row is null) { notWritten.Add($"{key}: no VAR row or instance of this schematic defines it"); continue; }
            if (!TunableValue.TryParse(text, out double number, out string unit, out _))
            {
                notWritten.Add($"{key}: '{text}' is not a value");
                continue;
            }
            string expr = number.ToString("G15", CultureInfo.InvariantCulture);
            string rowUnit = UnitFor(row, unit);
            if (row.Expression.Trim() == expr && row.Unit == rowUnit) continue;
            row.Expression = expr;
            row.Unit = rowUnit;
            written.Add(key);
        }
        return written;
    }

    /// <summary>
    /// brief-sdl-10 D7 — the row <c>X1.w50</c> names: instance X1's own value of w50 (a cell VAR it overrides, or a parameter),
    /// added blank when the instance does not state it yet. Null when no instance of that name exists, or the key is not
    /// of that form (a nested <c>DUT:X1.w50</c> belongs to another schematic).
    /// </summary>
    private static EditableParameter? InstanceRow(SchematicEditModel model, string key)
    {
        int dot = key.IndexOf('.');
        if (dot <= 0 || dot == key.Length - 1 || key.Contains(':')) return null;
        string inst = key[..dot], name = key[(dot + 1)..];
        if (model.Components.FirstOrDefault(c => c.InstanceName == inst && c.CellRef is { Length: > 0 }) is not { } comp) return null;
        if (comp.Parameters.FirstOrDefault(p => p.Name == name) is { } row) return row;
        var added = new EditableParameter { Name = name, Expression = "", Unit = "", ShowOnSchematic = false };
        comp.Parameters.Add(added);
        return added;
    }

    /// <summary>The row's own unit spelling when it means the same unit, else the value's — Push's rule.</summary>
    private static string UnitFor(EditableParameter row, string unit)
    {
        if (row.Unit.Length > 0 && UnitNormalizer.ToEngineUnit(row.Unit) == unit) return row.Unit;
        return row.Unit.Contains('Ω') && unit.EndsWith("Ohm", StringComparison.Ordinal) ? unit[..^3] + "Ω" : unit;
    }

    /// <summary>
    /// Apply on disk: <paramref name="values"/> into <paramref name="schematicPath"/>'s VAR block, Update Layout from
    /// Schematic on its cell's saved layout, and the key check against <paramref name="expectedKey"/>. Every other byte of
    /// the <c>.csch</c> is what its own persistence writes for it.
    /// </summary>
    public static EmApplyResult Apply(string schematicPath, IReadOnlyDictionary<string, string> values, string? expectedKey,
                                      string? emSetup = null)
    {
        string full = Path.GetFullPath(schematicPath);
        if (EmCapability.CellDirOfSchematic(full) is not { } cellDir)
            return Refused($"{Path.GetFileName(full)} is not a cell's schematic, so it has no layout to update.");
        if (EmCapability.Resolve(cellDir, emSetup, Path.GetDirectoryName(full), out var notCapable) is null)
            return ApplyToParent(full, cellDir, values, expectedKey) ?? Refused(notCapable ?? "the cell has no layout and EM setup to update.");

        var (model, view, cellName) = SchematicPersistence.LoadFromFile(full);
        var written = WriteValues(model, values, out var notWritten);
        if (written.Count > 0) SchematicPersistence.SaveToFile(full, model, cellName, view.PanX, view.PanY, view.Zoom);

        var (layoutKey, lines, refusal) = UpdateLayout(cellDir, emSetup, Path.GetDirectoryName(full));
        return new EmApplyResult(written, notWritten, layoutKey, expectedKey, refusal, lines);
    }

    /// <summary>
    /// brief-sdl-10 D7 — Apply in a schematic that PLACES EM components (Path B): each value written where it is defined — a
    /// VAR in the VAR block, an instance's own value on that instance — and never into a placed cell, which may be placed
    /// elsewhere with other values. When the schematic has a layout made from it, Update Layout from Schematic runs (each
    /// placement at its own values, D3-D4) and every placed EM component's geometry is checked against what EM verified.
    /// Null when the schematic places no EM component (the caller's own refusal then stands).
    /// </summary>
    private static EmApplyResult? ApplyToParent(string full, string cellDir, IReadOnlyDictionary<string, string> values, string? expectedKey)
    {
        var (model, view, cellName) = SchematicPersistence.LoadFromFile(full);
        model.SchematicDirectory = Path.GetDirectoryName(full);
        if (!PlacesEmComponents(model)) return null;
        var written = WriteValues(model, values, out var notWritten);
        if (written.Count > 0) SchematicPersistence.SaveToFile(full, model, cellName, view.PanX, view.PanY, view.Zoom);

        var updated = UpdateParentLayout(cellDir, full);
        return new EmApplyResult(written, notWritten, updated.Key, expectedKey, updated.Refusal, updated.Lines)
        {
            NothingToVerify = updated.NothingToVerify, Unchecked = updated.Unchecked,
        };
    }

    /// <summary>Whether <paramref name="model"/> places an instance of an EM component that varies (D1's first two kinds).</summary>
    public static bool PlacesEmComponents(SchematicEditModel model) =>
        model.Components.Any(c => c.CellRef is { Length: > 0 } r && !PdkKitRegistry.IsKitRef(r)
                                  && Workspace.ExternalCellRef.ResolveCellDir(r, model.SchematicDirectory) is { } d && Directory.Exists(d)
                                  && EmComponentKinds.Of(d).Varies);

    /// <summary>
    /// Update Layout from Schematic, headless, on a parent's saved layout — then the geometry key of every EM component it
    /// places, joined with <c>+</c>. With no layout made from the schematic, or none of its placements schematic-driven,
    /// <c>NothingToVerify</c> says so: the values are written and there is no layout geometry to check. <c>Unchecked</c>
    /// names the EM components placed inside another placed cell, which the key check does not reach.
    /// </summary>
    public static (string? Key, IReadOnlyList<string> Lines, string? Refusal, string? NothingToVerify, IReadOnlyList<string> Unchecked)
        UpdateParentLayout(string cellDir, string schematicPath)
    {
        const string NotLaidOut = "the schematic has no layout made from it, so there is no layout geometry to check against EM";
        var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(cellDir, CircuitRF.Design.Cells.ViewType.Layout);
        if (primary.State is not (CircuitRF.Design.Cells.PrimaryState.SoleFile or CircuitRF.Design.Cells.PrimaryState.NamedPresent)
            || primary.ResolvedName is null)
            return (null, [], null, NotLaidOut, []);
        string clay = Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.Layout),
                                   primary.ResolvedName);
        if (Workspace.WorkspaceRootFinder.WorkspaceDirOf(cellDir) is not { } ws) return (null, [], "the schematic belongs to no workspace.", null, []);
        try
        {
            var model = SchematicPersistence.LoadFromFile(schematicPath).model;
            model.SchematicDirectory = Path.GetDirectoryName(schematicPath);
            var layout = LayoutPersistence.LoadFromFile(clay);
            if (layout.GeneratedPlacements.Count == 0) return (null, [], null, NotLaidOut, []);
            var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, clay, Path.Combine(ws, ".cws"), new TechnologyCache());
            var plan = SchematicLayoutPlanner.Plan(model, layout, model.SchematicDirectory!, ws, Path.GetDirectoryName(clay)!,
                                                   tech.Tech, tech.ResolvedPath, DiskCellResolver.Instance,
                                                   targetLayoutPath: clay, connectParts: true);
            plan.ApplyTo(layout);
            LayoutPersistence.SaveToFile(clay, layout);
            var lines = plan.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning).Select(l => l.Text).ToList();
            var keys = PlacedKeys(layout, Path.GetDirectoryName(clay)!);
            var nested = NestedEmComponents(layout, Path.GetDirectoryName(clay)!);
            return keys.Count == 0
                ? (null, lines, null, "the layout was updated, and it places no schematic-driven EM component, so there is no " +
                                      "geometry to check against EM", nested)
                : (string.Join("+", keys), lines, null, null, nested);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return (null, [], $"the layout could not be updated: {e.Message}", null, []);
        }
    }

    /// <summary>The sentence for EM components the key check does not reach (<see cref="NestedEmComponents"/>).</summary>
    public static string UncheckedSentence(IReadOnlyList<string> nested) =>
        $"Not checked: {string.Join(", ", nested)} {(nested.Count == 1 ? "is an EM component" : "are EM components")} placed " +
        "inside another placed cell, whose own layout the parent's layout shows, so that copper was not compared with what " +
        "EM verified.";

    /// <summary>
    /// brief-sdl-10 — the EM components (D1's first two kinds) placed INSIDE a cell that <paramref name="layout"/> places, as
    /// <c>Y1.X1</c>: a placed cell that is not itself an EM component is drawn as its own layout, at its own values, so
    /// an EM component in it is not placed at the values the parent gives it and has no key in <see cref="PlacedKeys"/>.
    /// Read from each placed cell's schematic, through cells that are not EM components, to a bounded depth.
    /// </summary>
    public static IReadOnlyList<string> NestedEmComponents(LayoutView layout, string layoutDir)
    {
        var found = new List<string>();
        foreach (var inst in layout.Instances)
        {
            if (inst.SchematicId is not { Length: > 0 } id || inst.CellRef is not { Length: > 0 } r) continue;
            if (CellDirOf(r, layoutDir) is not { } d || CircuitRF.Design.Layout.PCells.GeneratedCellStore.IsUnderGeneratedCellsFolder(d)) continue;
            if (EmComponentKinds.Of(d).Kind != EmComponentKind.None) continue;   // itself an EM component: checked, or fixed
            EmInside(d, id, new HashSet<string>(StringComparer.Ordinal), 0, found);
        }
        return found;

        // seen holds the cells on the way down (a cycle guard), not every cell visited: two placements of one cell are two paths.
        static void EmInside(string cellDir, string path, HashSet<string> seen, int depth, List<string> found)
        {
            string key = Path.GetFullPath(cellDir);
            if (depth > 8 || !seen.Add(key)) return;
            try
            {
                if (EmComponentKinds.Primary(cellDir, CircuitRF.Design.Cells.ViewType.Schematic) is not { } csch) return;
                SchematicEditModel model;
                try { model = SchematicPersistence.LoadFromFile(csch).model; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                               or InvalidDataException or ArgumentException or InvalidOperationException) { return; }
                string dir = Path.GetDirectoryName(csch)!;
                foreach (var c in model.Components)
                {
                    if (c.InstanceName.Length == 0 || c.CellRef is not { Length: > 0 } r || PdkKitRegistry.IsKitRef(r)) continue;
                    if (CellDirOf(r, dir) is not { } inner) continue;
                    var kind = EmComponentKinds.Of(inner);
                    if (kind.Varies) found.Add($"{path}.{c.InstanceName}");
                    else if (kind.Kind == EmComponentKind.None) EmInside(inner, $"{path}.{c.InstanceName}", seen, depth + 1, found);
                }
            }
            finally { seen.Remove(key); }
        }

        static string? CellDirOf(string cellRef, string fromDir)
        {
            try
            {
                return Workspace.ExternalCellRef.ResolveCellDir(cellRef, fromDir) is { } d && Directory.Exists(d)
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)) : null;
            }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// brief-sdl-10 D3/D7 — the geometry key of every schematic-driven EM component <paramref name="layout"/> places: a
    /// variant's own (its folder names it), else the cell's saved layout's. Distinct, sorted.
    /// </summary>
    public static IReadOnlyList<string> PlacedKeys(LayoutView layout, string layoutDir)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var inst in layout.Instances)
        {
            if (inst.SchematicId is not { Length: > 0 } || inst.CellRef is not { Length: > 0 } r) continue;
            if (Workspace.ExternalCellRef.ResolveCellDir(r, layoutDir) is not { } d || !Directory.Exists(d)) continue;
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(d));
            if (CircuitRF.Design.Layout.PCells.GeneratedCellStore.IsVariant(d))
            {
                keys.Add(folder[(folder.LastIndexOf(CircuitRF.Design.Layout.PCells.GeneratedCellStore.VariantSeparator, StringComparison.Ordinal) + 1)..]);
                continue;
            }
            if (EmComponentKinds.Of(d).Kind != EmComponentKind.SchematicDriven) continue;
            var target = EmCapability.Resolve(d, null, null, out _)
                         ?? (EmCapability.SetupsFor(d).FirstOrDefault() is { } first ? EmCapability.Resolve(d, first, null, out _) : null);
            if (target is not null) keys.Add(KeyOfSaved(target));
        }
        return [.. keys];
    }

    /// <summary>
    /// Update Layout from Schematic, headless, on <paramref name="cellDir"/>'s saved layout at its schematic's saved
    /// values — the plan applied and the <c>.clay</c> saved — then the saved layout's key.
    /// </summary>
    public static (string? Key, IReadOnlyList<string> Lines, string? Refusal) UpdateLayout(string cellDir, string? emSetup, string? baseDir)
    {
        if (EmCapability.Resolve(cellDir, emSetup, baseDir, out var why) is not { } target) return (null, [], why);
        try
        {
            var model = SchematicPersistence.LoadFromFile(target.SchematicPath).model;
            model.SchematicDirectory = Path.GetDirectoryName(target.SchematicPath);
            var layout = LayoutPersistence.LoadFromFile(target.ClayPath);
            var (tech, _) = TechnologyResolver.ResolveForDocument(layout.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
            if (tech.Tech is null) return (null, [], $"{target.CellName}'s layout resolves no technology: {string.Join(" ", tech.Diagnostics)}");
            var plan = SchematicLayoutPlanner.Plan(model, layout, model.SchematicDirectory!, target.WorkspaceDir,
                                                   Path.GetDirectoryName(target.ClayPath)!, tech.Tech, tech.ResolvedPath,
                                                   DiskCellResolver.Instance, targetLayoutPath: target.ClayPath, connectParts: true);
            plan.ApplyTo(layout);
            LayoutPersistence.SaveToFile(target.ClayPath, layout);
            var lines = plan.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning)
                                  .Select(l => l.Text).ToList();
            return (KeyOfSaved(target), lines, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return (null, [], $"the layout could not be updated: {e.Message}");
        }
    }

    /// <summary>The EM cache key of <paramref name="target"/>'s layout AS SAVED — read back from disk, not re-planned.</summary>
    public static string KeyOfSaved(EmCellTarget target)
    {
        var saved = LayoutPersistence.LoadFromFile(target.ClayPath);
        var (tech, _) = TechnologyResolver.ResolveForDocument(saved.TechRef, target.ClayPath, target.CwsPath, new TechnologyCache());
        return KeyOfLayout(target, saved, tech.Tech);
    }

    /// <summary>The EM cache key of <paramref name="layout"/> (an open layout, saved or not) as <paramref name="target"/>'s
    /// setup would solve it — the GUI's check after its own Update Layout.</summary>
    public static string KeyOfLayout(EmCellTarget target, LayoutView layout, Technology? tech) =>
        EmCellCache.KeyOf(new CircuitRF.Design.Layout.Em.EmLayoutSource(target.ClayPath, layout, tech, layout.DbuPerMicron), target.RunSetup);

    private static EmApplyResult Refused(string why) => new([], [], null, null, why, []);
}
