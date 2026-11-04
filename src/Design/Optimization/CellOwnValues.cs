// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-10 D7 — Make These the Cell's Own Values. Apply writes an optimised value where it is defined: an instance's
// own value stays on that instance, because the cell may be placed elsewhere with other values. This separate command
// moves one instance's overrides INTO the cell — its VAR block for a cell VAR, its .ccell default for a declared
// parameter — and clears them from the instance. Before anything is written it lists every other placement of the cell in
// the workspace and what each will change to: a placement that states its own value keeps it, one that does not follows
// the cell. Each value is written as a number in the override's own unit, evaluated in the parent's scope, so an override
// that names a parent VAR does not become a name the cell cannot see.

using System.Globalization;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Optimization;

/// <summary>One value moving into the cell: its name, the cell's value now, the value it takes, and where it lives.</summary>
public sealed record CellOwnValue(string Name, string From, string To, string Unit, bool IsParameter);

/// <summary>Another placement of the cell, and what each moved value becomes for it.</summary>
/// <param name="SchematicPath">The schematic that places it.</param>
/// <param name="Instance">The instance.</param>
/// <param name="Lines">One per moved value: <c>w50: 280 um → 300 um</c>, or <c>w50: keeps its own 310 um</c>.</param>
public sealed record CellPlacementChange(string SchematicPath, string Instance, IReadOnlyList<string> Lines);

/// <summary>What the command would do, before anything is written.</summary>
public sealed record CellOwnValuesPlan(
    string SchematicPath, string Instance, string CellDir, string CellName, IReadOnlyList<CellOwnValue> Values,
    IReadOnlyList<CellPlacementChange> Others, string? Refusal)
{
    /// <summary>The confirmation's text: what moves, then every other placement and what it will change to.</summary>
    public string Summary()
    {
        if (Refusal is { } why) return why;
        var lines = new List<string>
        {
            $"{Instance}'s own values become {CellName}'s own, and {Instance} stops stating them:",
        };
        lines.AddRange(Values.Select(v => $"  {v.Name}: {v.From} → {v.To}" + (v.IsParameter ? " (parameter default)" : "")));
        lines.Add(Others.Count == 0
            ? $"{CellName} is placed nowhere else in this workspace."
            : $"{CellName} is placed {Others.Count} other time{(Others.Count == 1 ? "" : "s")} in this workspace:");
        foreach (var o in Others)
        {
            lines.Add($"  {o.Instance} in {Path.GetFileName(o.SchematicPath)}:");
            lines.AddRange(o.Lines.Select(l => "    " + l));
        }
        return string.Join("\n", lines);
    }
}

public static class CellOwnValues
{
    /// <summary>
    /// What Make These the Cell's Own Values would do for <paramref name="instance"/> in <paramref name="schematicPath"/>,
    /// read from disk; nothing is written. <paramref name="model"/> is the parent as it is open, when it is (its unsaved
    /// edits are what the command acts on); null reads it from the file.
    /// </summary>
    public static CellOwnValuesPlan Plan(string schematicPath, string instance, SchematicEditModel? model = null)
    {
        string full = Path.GetFullPath(schematicPath);
        model ??= SchematicPersistence.LoadFromFile(full).model;
        model.SchematicDirectory ??= Path.GetDirectoryName(full);
        CellOwnValuesPlan Refused(string why) => new(full, instance, "", "", [], [], why);

        if (model.Components.FirstOrDefault(c => c.InstanceName == instance) is not { CellRef: { Length: > 0 } cellRef } comp)
            return Refused($"'{instance}' is not a placed cell in {Path.GetFileName(full)}.");
        if (ExternalCellRef.ResolveCellDir(cellRef, model.SchematicDirectory) is not { } cellDir || !Directory.Exists(cellDir))
            return Refused($"{instance}'s cell '{cellRef}' does not resolve.");
        cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        string cellName = Path.GetFileName(cellDir);

        var vars = CellVariables.Of(cellDir).ToDictionary(v => v.Name, StringComparer.Ordinal);
        var parameters = C3dCellParameters(cellDir);
        var overrides = comp.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Expression)
                                                   && (vars.ContainsKey(p.Name) || parameters.ContainsKey(p.Name))
                                                   && !IsReserved(p.Name)).ToList();
        if (overrides.Count == 0)
            return Refused($"'{instance}' states none of {cellName}'s values: there is nothing to make the cell's own.");

        var scope = ParentScope(model);
        var evaluator = new Evaluator();
        var moved = new List<CellOwnValue>();
        foreach (var p in overrides)
        {
            bool isParam = !vars.ContainsKey(p.Name);
            string unit = p.Unit.Length > 0 ? p.Unit : isParam ? parameters[p.Name].Unit : vars[p.Name].Unit;
            string engine = UnitNormalizer.ToEngineUnit(unit);
            double si;
            try
            {
                si = evaluator.Eval(p.Expression, scope, UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null).AsReal();
            }
            catch (Exception e) when (e is ExpressionException or InvalidCastException or InvalidOperationException or ArgumentException)
            {
                return Refused($"{instance}'s {p.Name} = {p.Expression} does not evaluate to a number here ({e.Message}).");
            }
            double scale = engine.Length > 0 && Units.Scale(engine) is { } sc && sc != 0 ? sc : 1.0;
            string to = (si / scale).ToString("G12", CultureInfo.InvariantCulture);
            string from = isParam ? parameters[p.Name].Expr : vars[p.Name].Expression;
            string fromUnit = isParam ? parameters[p.Name].Unit : vars[p.Name].Unit;
            moved.Add(new CellOwnValue(p.Name, Shown(from, fromUnit), to, unit, isParam));
        }

        var others = new List<CellPlacementChange>();
        void Add(string path, EditableComponent other)
        {
            var lines = moved.Select(v => other.Parameters.FirstOrDefault(q => q.Name == v.Name) is { Expression.Length: > 0 } own
                                              && !string.IsNullOrWhiteSpace(own.Expression)
                ? $"{v.Name}: keeps its own {Shown(own.Expression, own.Unit)}"
                : $"{v.Name}: {v.From} → {Shown(v.To, v.Unit)}").ToList();
            others.Add(new CellPlacementChange(path, other.InstanceName, lines));
        }
        // The parent's own other placements as it stands (its unsaved edits included), then every other schematic in the
        // parent's workspace and in the cell's: a cell taken from another workspace is placed in both.
        foreach (var other in model.Components.Where(c => c.InstanceName != instance && c.CellRef is { Length: > 0 }))
            if (SameCell(other.CellRef!, model.SchematicDirectory!, cellDir)) Add(full, other);
        var roots = new[] { WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(full)!), WorkspaceRootFinder.WorkspaceDirOf(cellDir) }
            .OfType<string>().Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).Distinct(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        foreach (string ws in roots)
            foreach (var (path, other) in Placements(ws, cellDir))
            {
                string at = Path.GetFullPath(path);
                if (string.Equals(at, full, StringComparison.Ordinal) || !seen.Add((at, other.InstanceName))) continue;
                Add(path, other);
            }
        moved = moved.Select(v => v with { To = Shown(v.To, v.Unit) }).ToList();
        return new CellOwnValuesPlan(full, instance, cellDir, cellName, moved, others, null);
    }

    /// <summary>
    /// The cell half of the command: each moved value written into <paramref name="plan"/>'s cell — its VAR row, or its
    /// <c>.ccell</c> parameter default — on disk. The instance half (clearing its overrides) is
    /// <see cref="ClearInstance"/>, which the GUI runs through the open schematic's own session so it is one undo step there.
    /// The GUI writes the VAR rows through the cell schematic's session instead (<see cref="VarChanges"/>), so the copy it
    /// holds in memory, which its next Simulate reads, is the one that changes. Returns the names written.
    /// </summary>
    public static IReadOnlyList<string> WriteCell(CellOwnValuesPlan plan)
    {
        if (plan.Refusal is not null) return [];
        var written = new List<string>();
        if (plan.Values.Any(v => !v.IsParameter) && EmComponentKinds.Primary(plan.CellDir, ViewType.Schematic) is { } csch)
        {
            var (cellModel, view, name) = SchematicPersistence.LoadFromFile(csch);
            var (changes, names) = VarChanges(cellModel, plan);
            foreach (var (row, after) in changes)
            {
                row.Parameters.Clear();
                foreach (var p in after) row.Parameters.Add(p);
            }
            written.AddRange(names);
            SchematicPersistence.SaveToFile(csch, cellModel, name, view.PanX, view.PanY, view.Zoom);
        }
        written.AddRange(WriteCellParameters(plan));
        return written.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The VAR components of <paramref name="cellModel"/> (the cell's schematic) that the moved VARs change, each with the
    /// whole parameter list it has after; and the names changed. Nothing is changed: the caller applies each list, on disk
    /// (<see cref="WriteCell"/>) or as a command in an open session.
    /// </summary>
    public static (IReadOnlyList<(EditableComponent Var, List<EditableParameter> After)> Changes, IReadOnlyList<string> Names)
        VarChanges(SchematicEditModel cellModel, CellOwnValuesPlan plan)
    {
        var changes = new List<(EditableComponent, List<EditableParameter>)>();
        var names = new List<string>();
        var asVars = plan.Values.Where(v => !v.IsParameter).ToList();
        if (plan.Refusal is not null || asVars.Count == 0) return (changes, names);
        foreach (var comp in cellModel.Components.Where(c => c.Symbol == SymbolKind.Var))
        {
            var after = comp.Parameters.Select(p => p.Clone()).ToList();
            bool any = false;
            foreach (var row in after)
                if (asVars.FirstOrDefault(v => v.Name == row.Name.Trim()) is { } v)
                {
                    (row.Expression, row.Unit) = (Number(v.To), v.Unit);
                    names.Add(v.Name);
                    any = true;
                }
            if (any) changes.Add((comp, after));
        }
        return (changes, names.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>The moved parameters written as the <c>.ccell</c>'s defaults, on disk. Returns the names written.</summary>
    public static IReadOnlyList<string> WriteCellParameters(CellOwnValuesPlan plan)
    {
        var written = new List<string>();
        var asParams = plan.Values.Where(v => v.IsParameter).ToList();
        string ccellPath = Path.Combine(plan.CellDir, CellFolder.CcellFileName);
        if (plan.Refusal is not null || asParams.Count == 0 || !File.Exists(ccellPath)) return written;
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        foreach (var v in asParams)
            if (ccell.Parameters.FirstOrDefault(p => p.Name == v.Name) is { } p)
            {
                p.DefaultExpression = Number(v.To);
                p.Unit = v.Unit;
                written.Add(v.Name);
            }
        CellPersistence.SaveToFile(ccellPath, ccell);
        return written;
    }

    /// <summary>The instance half: the moved values cleared from <paramref name="model"/>'s instance — blank, which is the
    /// cell's own value (SDL-6 D7). Returns how many were cleared.</summary>
    public static int ClearInstance(SchematicEditModel model, CellOwnValuesPlan plan)
    {
        if (model.Components.FirstOrDefault(c => c.InstanceName == plan.Instance) is not { } comp) return 0;
        int n = 0;
        foreach (var v in plan.Values)
            foreach (var p in comp.Parameters.Where(p => p.Name == v.Name && p.Expression.Length > 0))
            {
                p.Expression = "";
                p.Unit = "";
                n++;
            }
        return n;
    }

    /// <summary>The whole command on disk (the CLI's and a test's form): the cell written, then the instance cleared in its
    /// schematic.</summary>
    public static IReadOnlyList<string> Apply(CellOwnValuesPlan plan)
    {
        var written = WriteCell(plan);
        if (plan.Refusal is not null) return written;
        var (model, view, name) = SchematicPersistence.LoadFromFile(plan.SchematicPath);
        if (ClearInstance(model, plan) > 0) SchematicPersistence.SaveToFile(plan.SchematicPath, model, name, view.PanX, view.PanY, view.Zoom);
        return written;
    }

    /// <summary>Every placement of <paramref name="cellDir"/> in the workspace: each schematic under it that places the
    /// cell, and each instance doing so. The generated-cells folder is not searched.</summary>
    public static IEnumerable<(string Path, EditableComponent Instance)> Placements(string workspaceDir, string cellDir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(workspaceDir, "*.csch", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            if (ReservedFolders.IsUnderGeneratedCells(file)) continue;
            SchematicEditModel m;
            try { m = SchematicPersistence.LoadFromFile(file).model; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or InvalidDataException or ArgumentException or InvalidOperationException) { continue; }
            string dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            foreach (var c in m.Components.Where(c => c.CellRef is { Length: > 0 }))
            {
                string? d;
                try { d = ExternalCellRef.ResolveCellDir(c.CellRef, dir); } catch (Exception) { continue; }
                if (d is not null && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)), cellDir, StringComparison.Ordinal))
                    yield return (file, c);
            }
        }
    }

    private static bool SameCell(string cellRef, string fromDir, string cellDir)
    {
        try
        {
            return ExternalCellRef.ResolveCellDir(cellRef, fromDir) is { } d
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)), cellDir, StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    private static bool IsReserved(string name) =>
        name is ParametricModelRegistry.ModelParameter or ParametricModelRegistry.SetupParameter or ParametricModelRegistry.CellParameter;

    private static Dictionary<string, (string Expr, string Unit)> C3dCellParameters(string cellDir)
    {
        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        try
        {
            return File.Exists(path)
                ? CellPersistence.LoadFromFile(path).Parameters.ToDictionary(p => p.Name, p => (p.DefaultExpression, p.Unit ?? ""), StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static Scope ParentScope(SchematicEditModel model)
    {
        var scope = new Scope("global");
        try
        {
            foreach (var v in NetExtractor.Extract(model, "tb", DiskCellResolver.Instance).TestBench.GlobalVariables)
                scope.Bind(v.Name, v.Expression, v.Unit);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        return scope;
    }

    private static string Shown(string expr, string unit) => unit.Length > 0 ? $"{expr.Trim()} {unit}" : expr.Trim();

    private static string Number(string shown) => shown.Split(' ', 2)[0];
}
