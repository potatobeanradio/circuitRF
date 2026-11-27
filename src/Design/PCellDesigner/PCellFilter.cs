// Owner: PCD-12 (brief-pcd-12). Framework-free.
//
// A realised filter as a designed cell: the Filter Designer's realisation made into a cell through the designer's own
// functions (PCellDesign.Create, CellNetlistMerge, the Save the designer writes), its synthesis dimensions the cell's
// parameters; the cell's filter metrics read back by FilterExtraction; and FilterPolish on its parameters.

using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;
using RfCore;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>What Make PCell made: the cell folder and its parameters, with the design check's findings; or why not.</summary>
public sealed record PCellFilterOutcome(string? CellDir, IReadOnlyList<CcellParameter> Parameters,
                                        IReadOnlyList<Diagnostic> Findings, string? Refusal);

/// <summary>A designed filter cell's metrics: the response's prototype read back with each check's pass or fail
/// (<see cref="FilterExtraction"/>), whose response it is, or why there are none.</summary>
public sealed record PCellFilterMetrics(ExtractedFilter? Filter, string Source, string? Refusal)
{
    public int Failing => Filter?.Checks.Count(c => !c.Pass) ?? 0;
    public bool Passes => Filter is not null && Failing == 0;
}

/// <summary>What a polish did: the worst return loss across the band before and after, and the cell's new defaults; or
/// why it could not.</summary>
public sealed record PCellFilterPolishOutcome(double BeforeDb, double AfterDb, IReadOnlyList<CcellParameter> Parameters, string? Refusal);

/// <summary>brief-pcd-12: filter synthesis on the PCell Designer.</summary>
public static class PCellFilter
{
    /// <summary>The realised part's designator in the cell, as the Filter Designer's bench names it.</summary>
    public const string PartName = "F1";

    /// <summary>
    /// R-pcd12-2: <paramref name="realisation"/> (by <paramref name="realiser"/>, for <paramref name="spec"/>) as the
    /// designed cell <paramref name="cellName"/> under <paramref name="parentDir"/> (the workspace root, or a folder in a
    /// workspace): the part, its site parts and two
    /// Ports, each of <see cref="Dimensions"/> a parameter bound into the part, the spec kept (D7), saved as the designer
    /// saves (D5), then <see cref="PCellDesignCheck.Run"/>. What the Filter Designer's Make PCell… and
    /// <c>circuitrf filter --pcell</c> call.
    /// </summary>
    public static PCellFilterOutcome Make(string parentDir, string cellName, FilterSpec spec, FilterRealisation realisation,
                                          string realiser)
    {
        parentDir = Path.GetFullPath(parentDir);
        string workspaceRoot = WorkspaceRootFinder.WorkspaceDirOf(parentDir) ?? parentDir;
        CellCreateResult created;
        try { created = PCellDesign.Create(parentDir, cellName); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new PCellFilterOutcome(null, [], [], e.Message);
        }

        var dims = Dimensions(realisation);
        var byRow = dims.ToDictionary(d => d.Row, d => d.Parameter);
        var bound = realisation with
        {
            Parameters = realisation.Parameters.ToDictionary(e => e.Key, e => byRow.TryGetValue(e.Key, out var p) ? p : e.Value),
        };
        var (tech, _) = Technology(created.CellDir, workspaceRoot);
        string text = FilterSynthesisService.Circuit(spec, bound, realiser, null, tech, []);

        var model = SchematicPersistence.LoadFromFile(created.SchematicPath!).model;
        model.SchematicDirectory = Path.GetDirectoryName(created.SchematicPath!);
        var merged = CellNetlistMerge.Merge(model, text);
        if (merged.Schematic is not { } schematic)
        {
            try { Directory.Delete(created.CellDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new PCellFilterOutcome(null, [], [], $"{cellName}'s schematic was refused: {string.Join(" ", merged.Refusals)}");
        }

        var cell = CellFolder.ReadCcell(created.CellDir)!;
        cell.Parameters = [.. dims.Select(d => new CcellParameter
        {
            Name = d.Parameter, DefaultExpression = d.Number, Unit = d.Unit, Dimension = UnitDimension.Length,
            Minimum = Si(d.Value * (1 - FilterTuning.GeometryRange), d.Unit), Maximum = Si(d.Value * (1 + FilterTuning.GeometryRange), d.Unit),
            Description = $"{d.Row} of the {realisation.PartType}, as synthesised",
        })];
        var f = FilterSynthesisService.Frequencies(spec, FilterPolish.Points);
        cell.PCellDesign = new CcellPCellDesign
        {
            Sweep = string.Create(CultureInfo.InvariantCulture, $"start={f[0]:R} stop={f[^1]:R} npts={f.Length} Unit=Hz"),
            Filter = new CcellFilter
            {
                Response = spec.Response, Band = spec.Band, Order = spec.Order, F1Hz = spec.F1Hz, F2Hz = spec.F2Hz,
                RippleDb = spec.RippleDb, StopAttenuationDb = spec.StopAttenuationDb, Z0 = spec.Z0,
                TransmissionZerosHz = spec.TransmissionZerosHz is { Count: > 0 } z ? [.. z] : null, Realiser = realiser,
            },
        };
        if (Save(created.CellDir, workspaceRoot, schematic, cell) is { } why)
            return new PCellFilterOutcome(created.CellDir, cell.Parameters, [], why);
        return new PCellFilterOutcome(created.CellDir, cell.Parameters, PCellDesignCheck.Run(created.CellDir), null);
    }

    /// <summary>
    /// brief-pcd-12 D4: the realisation's rows that are synthesis dimensions — one positive number with a length unit,
    /// glued (<c>12.3mm</c>) or spaced (<c>0.5 mm</c>) — each with the parameter it becomes (the row's name, brackets
    /// dropped), the number as written, its unit and its value in that unit.
    /// </summary>
    public static IReadOnlyList<(string Row, string Parameter, string Number, string Unit, double Value)> Dimensions(FilterRealisation realisation)
    {
        var list = new List<(string, string, string, string, double)>();
        foreach (var (row, text) in realisation.Parameters)
        {
            if (!Units.TrySplitGluedNumber(text.Trim(), out string number, out string unit))
                (number, unit) = Units.SplitTrailingUnit(text) is (var e, { } u) ? (e.Trim(), u) : (text, "");
            if (Units.BaseUnit(unit) != "metre"
                || !double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !(v > 0)) continue;
            list.Add((row, new string([.. row.Where(char.IsLetterOrDigit)]), number, unit, v));
        }
        return list;
    }

    /// <summary>The filter spec a designed cell keeps (<see cref="CcellPCellDesign.Filter"/>), or null.</summary>
    public static FilterSpec? SpecOf(string cellDir) => CellFolder.ReadCcell(cellDir)?.PCellDesign?.Filter is { } x
        ? new FilterSpec(x.Response, x.Band, x.Order, x.F1Hz, x.F2Hz, x.RippleDb, x.StopAttenuationDb, x.Z0, x.TransmissionZerosHz)
        : null;

    /// <summary>R-pcd12-3: any two-port's metrics against <paramref name="spec"/> (<see cref="FilterSynthesisService.Extract"/>:
    /// the spec's checks and the target matrix's); <paramref name="source"/> names whose response it is (an EM run's).</summary>
    public static PCellFilterMetrics Metrics(FilterSpec spec, SNP s, string source)
    {
        try { return new PCellFilterMetrics(FilterSynthesisService.Extract(spec, s), source, null); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new PCellFilterMetrics(null, source, e.Message); }
    }

    /// <summary>
    /// R-pcd12-3: the designed cell's own circuit response (<see cref="PCellDesignResponse.Run"/> at
    /// <paramref name="values"/>, its <c>.ccell</c> sweep) read back against the spec it keeps. <paramref name="schematic"/>
    /// is the designer's in-memory one; null reads the cell's primary.
    /// </summary>
    public static PCellFilterMetrics CircuitMetrics(string cellDir, string workspaceRoot,
        IReadOnlyList<(string Name, string Expression)>? values = null, SchematicEditModel? schematic = null, CancellationToken cancel = default)
    {
        const string source = "Circuit response";
        if (SpecOf(cellDir) is not { } spec)
            return new PCellFilterMetrics(null, source, $"{Path.GetFileName(cellDir)} keeps no filter specification: it was not made by Make PCell….");
        if (schematic is null)
        {
            string path = SchematicPath(cellDir);
            if (!File.Exists(path)) return new PCellFilterMetrics(null, source, $"{Path.GetFileName(cellDir)} has no schematic.");
            schematic = SchematicPersistence.LoadFromFile(path).model;
            schematic.SchematicDirectory = Path.GetDirectoryName(path);
        }
        var run = PCellDesignResponse.Run(schematic, values ?? [], CellFolder.ReadCcell(cellDir)?.PCellDesign?.Sweep, workspaceRoot, cancel);
        return run.S is { } s ? Metrics(spec, s, source) : new PCellFilterMetrics(null, source, run.Refusal);
    }

    /// <summary>
    /// R-pcd12-4: <see cref="FilterPolish.Run"/> on the realisation the cell is at its parameters' defaults, the polished
    /// dimensions written back as the defaults (the ranges as made), and the cell saved again, its layout with them.
    /// </summary>
    public static PCellFilterPolishOutcome Polish(string cellDir, string workspaceRoot, int passes = 3, CancellationToken cancel = default)
    {
        cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        string name = Path.GetFileName(cellDir);
        PCellFilterPolishOutcome No(string why) => new(double.NaN, double.NaN, [], why);
        if (CellFolder.ReadCcell(cellDir) is not { PCellDesign.Filter: not null } cell || SpecOf(cellDir) is not { } spec)
            return No($"{name} keeps no filter specification: it was not made by Make PCell….");
        string path = SchematicPath(cellDir);
        var schematic = SchematicPersistence.LoadFromFile(path).model;
        schematic.SchematicDirectory = Path.GetDirectoryName(path);
        if (schematic.Components.FirstOrDefault(c => c.InstanceName == PartName) is not { } part)
            return No($"{name} has no {PartName}, the part Make PCell… placed.");
        var (tech, _) = Technology(cellDir, workspaceRoot);
        if (FilterSynthesisService.Substrate(tech, null, null, out string? noSubstrate) is not { } substrate)
            return No($"{name}'s technology has no substrate to polish on: {noSubstrate}");

        // The realisation the part is at the defaults: a bound row at its parameter's default, every other row the
        // realiser set (one that differs from the part's own default) as it stands.
        var parameters = cell.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var defaults = ComponentTypeRegistry.DefaultParameters(part.Symbol, 0).ToDictionary(d => d.Name, d => (d.Expression, d.Unit));
        var rows = new Dictionary<string, string>();
        var binding = new Dictionary<string, string>();   // row → parameter
        foreach (var p in part.Parameters.Where(p => p.Expression.Trim().Length > 0))
        {
            string e = p.Expression.Trim();
            if (parameters.TryGetValue(e, out var cp)) { rows[p.Name] = cp.DefaultExpression + cp.Unit; binding[p.Name] = e; }
            else if (!defaults.TryGetValue(p.Name, out var d) || d.Expression != p.Expression || d.Unit != p.Unit)
                rows[p.Name] = p.Unit.Length > 0 ? $"{e} {p.Unit}" : e;
        }
        if (!ComponentTypeRegistry.TryParseCode(ComponentModelFactory.CoupledResonatorTypeName, out var mcrc, out _) || part.Symbol != mcrc)
            return No($"{name}'s {PartName} is not an MCRC: polish corrects hairpins and open loops in a row.");
        var realisation = new FilterRealisation(ComponentModelFactory.CoupledResonatorTypeName, rows, [], []);
        if (FilterPolish.Refusal(realisation) is { } refusal) return No(refusal.Message);

        var (polished, history) = FilterPolish.Run(spec, realisation, substrate, passes, cancel: cancel);
        var best = history.First(h => ReferenceEquals(h.Parameters, polished.Parameters));
        foreach (var (row, parameter) in binding)
            if (polished.Parameters.TryGetValue(row, out string? text) && Units.TrySplitGluedNumber(text, out string number, out _))
                parameters[parameter].DefaultExpression = number;   // FilterPolish stores lengths in mm, as Make wrote them
        if (Save(cellDir, workspaceRoot, schematic, cell) is { } why) return No(why);
        return new PCellFilterPolishOutcome(history[0].Extraction.ReturnLossDb, best.Extraction.ReturnLossDb, cell.Parameters, null);
    }

    // ── The designer's Save, for a caller with no window (brief-pcd-12 D5; PCellDesignerViewModel.Save) ──────────────

    private static string? Save(string cellDir, string workspaceRoot, SchematicEditModel schematic, CcellFile cell)
    {
        string name = Path.GetFileName(cellDir);
        string schematicPath = SchematicPath(cellDir), clayPath = ClayPath(cellDir);
        cell.PCellDesign ??= new CcellPCellDesign();
        cell.LayoutGenerator = PCellDesign.GeneratorIdFor(name);
        cell.EmCopper = true;
        cell.Terminals = PCellDesign.TerminalsOf(schematic);
        cell.NumPorts = cell.Terminals.Count;
        cell.PrimarySchematic ??= Path.GetFileName(schematicPath);
        cell.PrimaryLayout ??= Path.GetFileName(clayPath);
        string symbolDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        string symbolPath = Path.Combine(symbolDir,
            CellFolder.ResolvePrimary(cellDir, ViewType.Symbol).ResolvedName ?? name + CellFolder.ViewExtension(ViewType.Symbol));
        cell.PrimarySymbol ??= Path.GetFileName(symbolPath);
        try
        {
            if (!File.Exists(symbolPath) || SymbolPersistence.LoadFromFile(symbolPath).Pins.Count != cell.Terminals.Count)
            {
                Directory.CreateDirectory(symbolDir);
                SymbolPersistence.SaveToFile(symbolPath, AutoSymbolGenerator.Generate(name, cell.Terminals.Count));
            }
            CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), cell);
            Directory.CreateDirectory(Path.GetDirectoryName(schematicPath)!);
            SchematicPersistence.SaveToFile(schematicPath, schematic, name);
            var (tech, techId) = Technology(cellDir, workspaceRoot);
            if (tech is null) return $"Saved {name}'s schematic and parameters; the layout resolves no technology, so it was not written.";
            var at = DesignedCellLayout.At(cellDir, new Dictionary<string, Value>(), workspaceRoot, tech, techId, schematic);
            if (at.Layout is null) return $"Saved {name}'s schematic and parameters; the layout was not written: {at.Refusal}";
            Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
            LayoutPersistence.SaveToFile(clayPath, at.Layout);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{name} could not be saved: {e.Message}";
        }
    }

    private static string SchematicPath(string cellDir) => Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
        CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName ?? Path.GetFileName(cellDir) + CellFolder.ViewExtension(ViewType.Schematic));

    private static string ClayPath(string cellDir) => Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout),
        CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName ?? Path.GetFileName(cellDir) + CellFolder.ViewExtension(ViewType.Layout));

    /// <summary>The cell's technology as the designer resolves it: its layout's reference, else the workspace's.</summary>
    private static (Technology? Tech, string? Identity) Technology(string cellDir, string workspaceRoot)
    {
        string clay = ClayPath(cellDir);
        string? techRef = File.Exists(clay) ? LayoutPersistence.LoadFromFile(clay).TechRef : null;
        var (tech, _) = TechnologyResolver.ResolveForDocument(techRef, clay, Path.Combine(workspaceRoot, ".cws"), new TechnologyCache());
        return (tech.Tech, tech.ResolvedPath);
    }

    /// <summary><paramref name="v"/> in <paramref name="unit"/> as base SI, to fifteen digits as the designer writes a range.</summary>
    private static double Si(double v, string unit)
        => double.Parse((v * (Units.Scale(unit) ?? 1.0)).ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
