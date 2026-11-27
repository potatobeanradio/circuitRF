// Owner: PCD-5 (brief-pcd-0 §5, seam 7; written by PCD-1's seams commit). Framework-free.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 R-pcd5-7: the design check of a designed cell.</summary>
public static class PCellDesignCheck
{
    /// <summary>
    /// Every finding about cellDir as a designed cell, ids "pcell.*": S1 (every part joined or offset-placed, every
    /// anchor resolves, every expression evaluates at the defaults and at each parameter's Minimum and Maximum),
    /// D-numbers, a parameter that is also a VAR, MSHAPE pins and Model, and what Export needs. schematic: the
    /// designer's in-memory one; null reads the cell's primary.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Run(string cellDir, SchematicEditModel? schematic = null)
    {
        cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        string name = Path.GetFileName(cellDir);
        var findings = new List<Diagnostic>();
        void Say(string id, DiagnosticSeverity severity, string text) => findings.Add(new Diagnostic(id, severity, text));

        if (CellFolder.ReadCcell(cellDir) is not { } ccell)
        {
            Say("pcell.not-a-cell", DiagnosticSeverity.Error, $"{cellDir} is not a cell folder: it has no readable .ccell.");
            return findings;
        }
        if (ccell.PCellDesign is null)
            Say("pcell.not-designed", DiagnosticSeverity.Error, $"{name} is not a designed cell: its .ccell has no PCellDesign.");
        if (ccell.LayoutGenerator != PCellDesign.GeneratorIdFor(name))
            Say("pcell.generator", DiagnosticSeverity.Warning,
                $"{name}'s .ccell names the generator \"{ccell.LayoutGenerator}\", not \"{PCellDesign.GeneratorIdFor(name)}\"; " +
                "it is placed by that name only once it is saved so (Export writes it so).");

        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        if (schematic is null)
        {
            if (CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName is not { } csch)
            {
                Say("pcell.no-schematic", DiagnosticSeverity.Error, $"{name} has no primary schematic: a designed cell is its schematic.");
                return findings;
            }
            try { schematic = SchematicPersistence.LoadFromFile(Path.Combine(schematicDir, csch)).model; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                Say("pcell.no-schematic", DiagnosticSeverity.Error, $"{name}'s schematic could not be read: {e.Message}");
                return findings;
            }
        }

        // D-numbers, ranges, and a name used twice.
        var vars = schematic.Components.Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
                            .SelectMany(c => c.Parameters).Select(p => p.Name.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var p in ccell.Parameters)
        {
            if (p.Choices is { Count: > 0 })
                Say("pcell.choices", DiagnosticSeverity.Error, $"Parameter {p.Name} offers choices; a designed cell's parameters are numbers.");
            if (vars.Contains(p.Name))
                Say("pcell.parameter-is-var", DiagnosticSeverity.Error,
                    $"{p.Name} is both a parameter of {name} and a VAR of its schematic; remove one, or the VAR hides the value an instance sets.");
            if (p.Minimum is { } lo && p.Maximum is { } hi && lo > hi)
                Say("pcell.range", DiagnosticSeverity.Error, $"Parameter {p.Name}'s Minimum is above its Maximum.");
        }

        // The interface: Ports with distinct whole numbers.
        var ports = PCellDesign.PortsOf(schematic);
        if (ports.Count == 0)
            Say("pcell.no-ports", DiagnosticSeverity.Error, $"{name} has no Port: its Ports are the cell's pins, numbered by Num.");
        foreach (var dup in ports.GroupBy(p => p.Num).Where(g => g.Count() > 1))
            Say("pcell.port-num", DiagnosticSeverity.Error,
                $"Ports {string.Join(" and ", dup.Select(p => p.Port.InstanceName))} share Num = {dup.Key}.");
        foreach (var c in schematic.Components.Where(c => PortPlacement.IsPort(c) && !ports.Any(p => ReferenceEquals(p.Port, c))
                                                          && c.Disable is not (DisableState.Open or DisableState.Short)))
            Say("pcell.port-num", DiagnosticSeverity.Error, $"Port {c.InstanceName}'s Num is not a whole number above zero.");

        // MSHAPE: pins and Model.
        foreach (var c in schematic.Components.Where(DrawnMetal.Is))
        {
            if (!c.Parameters.Any(p => p.Name == DrawnMetal.PinX(1)))
                Say("pcell.mshape-pins", DiagnosticSeverity.Error, $"{c.InstanceName} has no pin: a drawn shape joins the cell through its pins.");
            string model = c.Parameters.FirstOrDefault(p => p.Name == DrawnMetal.Model)?.Expression.Trim() ?? DrawnMetal.ModelNone;
            if (model.Length == 0) model = DrawnMetal.ModelNone;
            if (model is not (DrawnMetal.ModelNone or DrawnMetal.ModelOpen))
                Say("pcell.mshape-model", DiagnosticSeverity.Error, $"{c.InstanceName}'s Model is \"{model}\"; it is none or open.");
            else if (model == DrawnMetal.ModelNone)
                Say("pcell.no-circuit-model", DiagnosticSeverity.Info,
                    $"{c.InstanceName} has Model = none, so {name} has no circuit model: it simulates by EM only and optimises em=direct.");
        }

        // What Export needs: a symbol that agrees with the Ports, and parts every recipient can draw.
        if (CellFolder.ResolvePrimary(cellDir, ViewType.Symbol).ResolvedName is { } symName)
        {
            try
            {
                int pins = SymbolPersistence.LoadFromFile(Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), symName)).Pins.Count;
                if (pins > 0 && pins != ports.Count)
                    Say("pcell.symbol-pins", DiagnosticSeverity.Error,
                        $"{name}'s symbol has {pins} pins and its schematic {ports.Count} Ports; they must agree.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
        }
        foreach (string g in PCellPackage.GeneratorsOf(schematic, schematicDir).Where(g => !PCellRegistry.IsBuiltIn(g)))
            Say("pcell.foreign-generator", DiagnosticSeverity.Warning,
                $"{name} places a part drawn by \"{g}\"; its package does not carry it, so a workspace without it cannot draw {name}.");

        PlacementFindings(cellDir, name, ccell, schematic, Say);
        return findings;
    }

    /// <summary>S1: the cell planned at the defaults and at each parameter's Minimum and Maximum — each refusal and planner
    /// warning an error at that point, and every part either joined to the rest or offset-placed from it.</summary>
    private static void PlacementFindings(string cellDir, string name, CcellFile ccell, SchematicEditModel schematic,
                                          Action<string, DiagnosticSeverity, string> say)
    {
        if (WorkspaceRootFinder.FindAncestorCws(cellDir) is not { } cws)
        {
            say("pcell.no-workspace", DiagnosticSeverity.Warning, $"{name} is in no workspace, so its placement was not checked.");
            return;
        }
        string root = Path.GetDirectoryName(cws)!;
        var layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var (tech, _) = TechnologyResolver.ResolveForDocument(null, Path.Combine(layoutDir, name + ".clay"), cws, new TechnologyCache());
        if (tech.Tech is not { } technology)
        {
            say("pcell.no-technology", DiagnosticSeverity.Warning, $"No technology resolves for {name}, so its placement was not checked.");
            return;
        }

        var points = new List<(string At, Dictionary<string, Value> Values)> { ("at the defaults", []) };
        foreach (var p in ccell.Parameters)
            foreach (var (bound, which) in new[] { (p.Minimum, "Minimum"), (p.Maximum, "Maximum") })
                if (bound is { } v)
                    points.Add(($"at {p.Name} = its {which}",
                                new() { [p.Name] = new Value(DesignedPCellResolver.ToEngine(p.Unit, v)) }));

        foreach (var (at, values) in points)
        {
            var result = DesignedCellLayout.At(cellDir, values, root, technology, tech.ResolvedPath, schematic);
            if (result.Layout is not { } layout)
            {
                say("pcell.plan", DiagnosticSeverity.Error, $"{name} could not be planned {at}: {result.Refusal}");
                continue;
            }
            foreach (var line in result.Lines.Where(l => l.Severity == SchematicLayoutPlanner.ReportSeverity.Warning))
                say("pcell.placement", DiagnosticSeverity.Error, $"{at}: {line.Text}");
            foreach (string loose in Unjoined(schematic, layout, layoutDir, technology))
                say("pcell.unjoined", DiagnosticSeverity.Error,
                    $"{at}: {loose} is neither joined to the rest of {name} nor offset-placed from it, so where it goes is no " +
                    "function of the parameters; join it by a net or give it an offset.");
        }
    }

    /// <summary>The parts outside the largest group of parts that meet pin to pin or are offset-placed from one another.</summary>
    private static IEnumerable<string> Unjoined(SchematicEditModel schematic, LayoutView layout, string layoutDir, Technology technology)
    {
        var parts = layout.Instances.Where(i => i.SchematicId is { Length: > 0 }).ToList();
        if (parts.Count < 2) return [];
        var group = Enumerable.Range(0, parts.Count).ToArray();
        int Find(int i) => group[i] == i ? i : group[i] = Find(group[i]);
        void Join(int a, int b) => group[Find(a)] = Find(b);

        var pins = new List<(int Part, double X, double Y)>();
        for (int i = 0; i < parts.Count; i++)
            if (CellLayoutResolver.Resolve(parts[i], layoutDir, technology).View is { } view)
                foreach (var pin in view.Pins)
                {
                    var (x, y, _) = ChainPose.Of(parts[i]).Apply(pin);
                    pins.Add((i, x, y));
                }
        for (int a = 0; a < pins.Count; a++)
            for (int b = a + 1; b < pins.Count; b++)
                if (pins[a].Part != pins[b].Part && Math.Abs(pins[a].X - pins[b].X) <= ChainPlacer.CloseDbu
                    && Math.Abs(pins[a].Y - pins[b].Y) <= ChainPlacer.CloseDbu)
                    Join(pins[a].Part, pins[b].Part);

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < parts.Count; i++) index.TryAdd(parts[i].SchematicId!, i);
        foreach (var c in schematic.Components.Where(c => c.Offset is not null))
            if (index.TryGetValue(c.InstanceName, out int me)
                && index.TryGetValue(c.Offset!.Anchor.Split('.')[0], out int anchor))
                Join(me, anchor);

        int main = Enumerable.Range(0, parts.Count).GroupBy(Find).OrderByDescending(g => g.Count()).ThenBy(g => g.Min()).First().Key;
        return Enumerable.Range(0, parts.Count).Where(i => Find(i) != main).Select(i => parts[i].SchematicId!)
                         .Order(StringComparer.Ordinal);
    }
}
