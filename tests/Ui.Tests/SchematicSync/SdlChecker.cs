using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-0 R-sdl0-3b: the one checker every schematic-driven layout behaviour is gated through (series rule
/// S6). It plans a design from <c>testdata/schematic-to-layout/</c> onto an EMPTY layout, applies the plan to a
/// copy, and reports four things:
/// <list type="number">
///   <item><b>LVS</b> — <see cref="LvsRun.Run(string, LvsRunOptions?, RunControl?)"/> on the result: no error but
///   the ones <c>expect.txt</c> names (<c>lvs &lt;finding id&gt;</c>);</item>
///   <item><b>open nets</b> — a ratsnest line, read as the layout editor reads it, on a net the design marks
///   joinable (<c>joinable &lt;net&gt;</c>): none;</item>
///   <item><b>overlaps</b> — copper joining pins of two different nets: none, unless <c>expect.txt</c> says
///   <c>overlap</c>. Edge contact counts as joining (<see cref="CopperPieces"/>: "overlap or share an edge"),
///   which is what lets abutted parts join without overlapping;</item>
///   <item><b>report lines</b> — exactly <c>expect.txt</c>'s <c>&lt;severity&gt; &lt;schematic id&gt; &lt;text
///   fragment&gt;</c> lines, no extra and none missing. A run-level line's id is <c>-</c>; a component with no
///   layout view (the plan's <c>NoLayoutWarnings</c>) reads as a <c>Warning</c> under its own id.</item>
/// </list>
/// The design is copied into a scratch workspace first, with <c>microstrip-0508.ctech</c> as its technology, so
/// the generated cells and the written layout never land in the repository.
/// </summary>
internal static class SdlChecker
{
    public sealed record Result(IReadOnlyList<string> Problems, SchematicLayoutPlan Plan, LayoutView Layout);

    private sealed record Expected(string Severity, string Id, string Fragment);

    public static string DesignsRoot => Path.Combine(RepoRoot(), "testdata", "schematic-to-layout");

    /// <summary>The designs the manifest lists, as <c>(folder, brief)</c>.</summary>
    public static IEnumerable<(string Folder, string Brief)> Manifest() =>
        File.ReadAllLines(Path.Combine(DesignsRoot, "designs.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(p => (p[0], p.Length > 1 ? p[1] : ""));

    public static Result Check(string design, string scratchRoot)
    {
        string source = Path.Combine(DesignsRoot, design);
        string ws = Path.Combine(scratchRoot, "ws");
        string techPath = Path.Combine(ws, "tech", "microstrip-0508.ctech");
        Directory.CreateDirectory(Path.GetDirectoryName(techPath)!);
        File.Copy(Path.Combine(DesignsRoot, "microstrip-0508.ctech"), techPath, overwrite: true);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech/microstrip-0508.ctech" });

        string cellDir = Path.Combine(ws, design);
        CopyTree(source, cellDir);
        foreach (var view in new[] { ViewType.Schematic, ViewType.Symbol, ViewType.Layout })
            Directory.CreateDirectory(CellFolder.SubFolderPath(cellDir, view));

        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string cschPath = Path.Combine(schematicDir, CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName!);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string clayPath = Path.Combine(layoutDir, design + CellFolder.ViewExtension(ViewType.Layout));

        var (model, _, _) = SchematicPersistence.LoadFromFile(cschPath);
        var tech = TechPersistence.LoadFromFile(techPath);
        var target = new LayoutView { TechRef = RefPathOf(layoutDir, techPath) };

        var plan = SchematicLayoutPlanner.Plan(model, target, schematicDir, ws, layoutDir, tech, techPath,
                                               cellResolver: null, targetLayoutPath: clayPath);
        var layout = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(target));
        plan.ApplyTo(layout);
        LayoutPersistence.SaveToFile(clayPath, layout);

        var expect = ReadExpect(Path.Combine(source, "expect.txt"), out var joinable, out var lvsAllowed, out bool overlapAllowed);
        var problems = new List<string>();

        // 1. LVS.
        foreach (var f in LvsRun.Run(cellDir).Findings.Where(f => !f.Waived && f.Severity == DiagnosticSeverity.Error))
            if (!lvsAllowed.Contains(f.Id)) problems.Add($"LVS error {f.Id}: {f.Render()}");

        // 2 and 3. Open nets and overlaps, from the pins as the ratsnest reads them.
        var pins = ReadPins(layout, clayPath, cellDir, tech, PdnSchematicNets.Of(model, cschPath));
        foreach (var line in Ratsnest.Build(pins))
            if (joinable.Contains(line.Net))
                problems.Add($"open net {line.Net}: ratsnest ({line.X0}, {line.Y0}) to ({line.X1}, {line.Y1})");
        if (!overlapAllowed)
            foreach (var group in pins.GroupBy(p => p.Group).Where(g => g.Key >= 0))
                if (group.Select(p => p.Net).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 1 } nets)
                    problems.Add($"overlap: copper joins nets {string.Join(", ", nets)} at ({group.First().X}, {group.First().Y})");

        // 4. Report lines, exactly.
        var actual = plan.Lines.Select(l => (Severity: l.Severity.ToString(), Id: l.InstanceName.Length == 0 ? "-" : l.InstanceName, l.Text))
            .Concat(plan.NoLayoutWarnings.Select(w => (Severity: "Warning", Id: w.Split(' ', 2)[0], Text: w)))
            .ToList();
        var unmatched = new List<(string Severity, string Id, string Text)>(actual);
        foreach (var e in expect)
        {
            int at = unmatched.FindIndex(a => a.Severity == e.Severity && a.Id == e.Id
                                            && a.Text.Contains(e.Fragment, StringComparison.Ordinal));
            if (at < 0) problems.Add($"missing report line: {e.Severity} {e.Id} {e.Fragment}");
            else unmatched.RemoveAt(at);
        }
        foreach (var a in unmatched) problems.Add($"unexpected report line: {a.Severity} {a.Id} {a.Text}");

        return new Result(problems, plan, layout);
    }

    private static List<Expected> ReadExpect(string path, out HashSet<string> joinable, out HashSet<string> lvs, out bool overlap)
    {
        joinable = new(StringComparer.Ordinal);
        lvs = new(StringComparer.Ordinal);
        overlap = false;
        var expected = new List<Expected>();
        if (!File.Exists(path)) return expected;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (parts[0])
            {
                case "joinable": joinable.Add(parts[1]); break;
                case "lvs": lvs.Add(parts[1]); break;
                case "overlap": overlap = true; break;
                default: expected.Add(new Expected(parts[0], parts[1], parts.Length > 2 ? parts[2] : "")); break;
            }
        }
        return expected;
    }

    /// <summary>The placed pins on a net and the copper piece each lands on — the layout editor's own ratsnest read
    /// (<c>LayoutEditorViewModel.ReadRatsnestPins</c>), with no canvas.</summary>
    private static List<RatsnestPin> ReadPins(LayoutView layout, string clayPath, string cellDir,
                                                Technology tech, PdnSchematicNets nets)
    {
        if (layout.Instances.Count == 0) return [];
        var flat = LayoutDesignFlatten.Flatten(layout, cellDir, tech, null, resolvedCrossTechMappings: null);
        var copper = CopperPieces.Build(flat.Shapes, tech, layout.Shapes);
        var origins = new List<PlacedPinOrigin>();
        var placed = PlacedPins.Of(layout, clayPath, tech, PinNaming.SchematicThenArtwork,
            portNamesOf: id => nets.For(id)?.PortNames ?? [],
            portNetsOf: id => nets.For(id)?.Nets ?? [],
            stamped: copper, origins: origins, scope: PlacementScope.EveryPlacement);

        var pins = new List<RatsnestPin>();
        int unjoined = -1;
        for (int i = 0; i < placed.Count && i < origins.Count; i++)
        {
            if (placed[i].Net is not { Length: > 0 } net) continue;
            int group = copper.PieceAt(placed[i].X, placed[i].Y, origins[i].Layer);
            pins.Add(new RatsnestPin(net, placed[i].X, placed[i].Y, group >= 0 ? group : unjoined--));
        }
        return pins;
    }

    private static string RefPathOf(string fromDir, string toFile) =>
        CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(fromDir, toFile));

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
