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
///   <item><b>overlaps</b> — the copper of two parts that share no net touching: none, unless <c>expect.txt</c>
///   says <c>overlap</c>. Edge contact counts as touching (<see cref="CopperPieces"/>: "overlap or share an edge"),
///   which is what lets abutted parts join without overlapping. It is asked PART BY PART, not piece by piece: a
///   line's own copper joins its two nets, so a chain of distributed parts is one piece carrying every net on it
///   (brief-sdl-1);</item>
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

    /// <summary>A design copied into a scratch workspace, read, and ready to plan onto <see cref="Target"/> (empty).</summary>
    public sealed record Prepared(
        SchematicEditModel Model, LayoutView Target, string Workspace, string CellDir, string SchematicDir,
        string SchematicPath, string LayoutDir, string ClayPath, Technology Tech, string TechPath)
    {
        public SchematicLayoutPlan Plan(bool connectParts = true, IReadOnlySet<string>? resnap = null, bool restoreRemoved = false) =>
            SchematicLayoutPlanner.Plan(Model, Target, SchematicDir, Workspace, LayoutDir, Tech, TechPath,
                                        cellResolver: null, targetLayoutPath: ClayPath, connectParts: connectParts,
                                        resnap: resnap, restoreRemoved: restoreRemoved);

        /// <summary><paramref name="plan"/> applied to a copy of the target, as the checker applies it.</summary>
        public LayoutView Applied(SchematicLayoutPlan plan)
        {
            var layout = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(Target));
            plan.ApplyTo(layout);
            return layout;
        }
    }

    public static Prepared Prepare(string design, string scratchRoot)
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
        return new Prepared(model, target, ws, cellDir, schematicDir, cschPath, layoutDir, clayPath, tech, techPath);
    }

    public static Result Check(string design, string scratchRoot)
    {
        var p = Prepare(design, scratchRoot);
        var plan = p.Plan();
        var layout = p.Applied(plan);
        var problems = Verify(p, layout, out var expect);

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

    /// <summary>
    /// Checks 1-3 on <paramref name="layout"/> — any layout of the prepared design, a re-run's included (brief-sdl-4):
    /// it is saved as the design's <c>.clay</c> and read back through LVS, the ratsnest and the overlap rule.
    /// <paramref name="openAllowed"/> names joinable nets a scenario expects left open (a gap it reports).
    /// </summary>
    /// <paramref name="lvsAllowed"/> names LVS finding ids it expects (an open net at that gap, a connector placed with
    /// no schematic part).
    public static List<string> Verify(Prepared p, LayoutView layout, IEnumerable<string>? openAllowed = null,
                                      IEnumerable<string>? lvsAllowed = null)
    {
        // The schematic as the scenario changed it is what LVS compares against.
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, Path.GetFileNameWithoutExtension(p.SchematicPath));
        return Verify(p, layout, out _, openAllowed, lvsAllowed);
    }

    private static List<string> Verify(Prepared p, LayoutView layout, out List<Expected> expect,
                                       IEnumerable<string>? openAllowed = null, IEnumerable<string>? lvsAllowedToo = null)
    {
        var (model, cellDir, cschPath, layoutDir, clayPath, tech) = (p.Model, p.CellDir, p.SchematicPath, p.LayoutDir, p.ClayPath, p.Tech);
        string source = Path.Combine(DesignsRoot, Path.GetFileName(cellDir));
        LayoutPersistence.SaveToFile(clayPath, layout);

        expect = ReadExpect(Path.Combine(source, "expect.txt"), out var joinable, out var lvsAllowed, out bool overlapAllowed,
                            out var ports);
        if (openAllowed is not null) joinable.ExceptWith(openAllowed);
        if (lvsAllowedToo is not null) lvsAllowed.UnionWith(lvsAllowedToo);
        var problems = new List<string>();

        // 1. LVS.
        foreach (var f in LvsRun.Run(cellDir).Findings.Where(f => !f.Waived && f.Severity == DiagnosticSeverity.Error))
            if (!lvsAllowed.Contains(f.Id)) problems.Add($"LVS error {f.Id}: {f.Render()}");

        // 2 and 3. Open nets and overlaps, from the pins as the ratsnest reads them.
        var nets = PdnSchematicNets.Of(model, cschPath);
        var pins = ReadPins(layout, clayPath, cellDir, tech, nets, out var netsByInstance);
        foreach (string net in joinable.Where(n => !pins.Any(p => p.Net == n)).Order(StringComparer.Ordinal))
            problems.Add($"joinable net {net} has no placed pin: the design names a net the layout does not carry");
        foreach (var line in Ratsnest.Build(pins))
            if (joinable.Contains(line.Net))
                problems.Add($"open net {line.Net}: ratsnest ({line.X0}, {line.Y0}) to ({line.X1}, {line.Y1})");
        if (!overlapAllowed)
            foreach (var (a, b) in TouchingStrangers(layout, cellDir, layoutDir, tech, netsByInstance))
                problems.Add($"overlap: {layout.Instances[a].SchematicId} and {layout.Instances[b].SchematicId} share no net " +
                             "and their copper touches");

        // 5. Ports (brief-sdl-5): exactly the expected port labels, each an edge port at the face of the metal it names.
        problems.AddRange(CheckPorts(layout, cellDir, tech, ports));
        return problems;
    }

    /// <summary>brief-sdl-5: every <c>port &lt;id&gt; &lt;number&gt;</c> line names a port label linked to that Port, with
    /// that text, that resolves against the copper an EM run meshes (the layout flattened) to an edge port whose
    /// reference plane is the label itself and whose arrow points into the metal; and no other label is linked.
    /// The flattened copper rather than the editor's lookup, which answers a footprint pad that names no pin at the
    /// label with the whole part's box.</summary>
    private static IEnumerable<string> CheckPorts(LayoutView layout, string cellDir, Technology tech,
                                                  IReadOnlyDictionary<string, string> expected)
    {
        var flat = LayoutDesignFlatten.Flatten(layout, cellDir, tech, null, resolvedCrossTechMappings: null);
        var lookup = LayoutPortDirection.LookupFor(flat.Shapes.Where(s => s is not LabelShape).ToList());
        var linked = layout.Shapes.OfType<LabelShape>().Where(l => l is { IsPort: true, SchematicId: { Length: > 0 } }).ToList();
        foreach (var (id, text) in expected.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var label = linked.FirstOrDefault(l => l.SchematicId == id);
            if (label is null) { yield return $"port {id}: no port label"; continue; }
            if (label.Text != text) yield return $"port {id}: label reads '{label.Text}', expected '{text}'";
            if (LayoutPortDirection.Resolve(lookup, label) is not { } hint)
            {
                yield return $"port {id}: label at ({label.X}, {label.Y}) is on no conductor";
                continue;
            }
            if (LayoutPortDirection.KindOf(lookup, label) != CircuitRF.Engine.Mom.PlanarPortKind.Edge)
                yield return $"port {id}: inferred as {LayoutPortDirection.KindOf(lookup, label)}, not an edge port";
            if (hint.Direction != label.PortDirection)
                yield return $"port {id}: points {label.PortDirection}, the metal runs {hint.Direction}";
            if (Math.Abs(hint.PlaneX - label.X) > 1 || Math.Abs(hint.PlaneY - label.Y) > 1)
                yield return $"port {id}: label at ({label.X}, {label.Y}), the metal's face at ({hint.PlaneX}, {hint.PlaneY})";
        }
        foreach (var extra in linked.Where(l => !expected.ContainsKey(l.SchematicId!)))
            yield return $"port {extra.SchematicId}: placed, but expect.txt names no such port";
    }

    private static List<Expected> ReadExpect(string path, out HashSet<string> joinable, out HashSet<string> lvs, out bool overlap,
                                             out Dictionary<string, string> ports)
    {
        ports = new(StringComparer.Ordinal);
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
                case "port": ports[parts[1]] = parts.Length > 2 ? parts[2] : ""; break;
                default: expected.Add(new Expected(parts[0], parts[1], parts.Length > 2 ? parts[2] : "")); break;
            }
        }
        return expected;
    }

    /// <summary>The placed pins on a net and the copper piece each lands on — the layout editor's own ratsnest read
    /// (<c>LayoutEditorViewModel.ReadRatsnestPins</c>), with no canvas.</summary>
    private static List<RatsnestPin> ReadPins(LayoutView layout, string clayPath, string cellDir,
                                                Technology tech, PdnSchematicNets nets,
                                                out Dictionary<int, HashSet<string>> netsByInstance)
    {
        netsByInstance = [];
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
            if (!netsByInstance.TryGetValue(origins[i].Instance, out var set)) netsByInstance[origins[i].Instance] = set = [];
            set.Add(net);
            int group = copper.PieceAt(placed[i].X, placed[i].Y, origins[i].Layer);
            pins.Add(new RatsnestPin(net, placed[i].X, placed[i].Y, group >= 0 ? group : unjoined--));
        }
        return pins;
    }

    /// <summary>Every pair of placed parts with no net in common whose copper touches: flattened two at a time, the
    /// pair's copper is fewer pieces than the two parts' own counted apart. Pairs whose boxes are apart are skipped, and
    /// so are two parts joined to one part between them: the lines on a tee's two arms meet corner to corner at its
    /// junction, which is copper they are both joined to anyway.</summary>
    private static List<(int A, int B)> TouchingStrangers(LayoutView layout, string cellDir, string layoutDir,
                                                          Technology tech, Dictionary<int, HashSet<string>> netsByInstance)
    {
        int Pieces(params int[] which)
        {
            var view = new LayoutView { DbuPerMicron = layout.DbuPerMicron, TechRef = layout.TechRef };
            foreach (int i in which) view.Instances.Add(layout.Instances[i]);
            var flat = LayoutDesignFlatten.Flatten(view, cellDir, tech, null, resolvedCrossTechMappings: null);
            return CopperPieces.Build(flat.Shapes, tech).Count;
        }

        var alone = new Dictionary<int, int>();
        int Alone(int i) => alone.TryGetValue(i, out int n) ? n : alone[i] = Pieces(i);
        long reach = layout.DbuPerMicron;   // the partition reads copper closer than a micrometre as joined
        var boxes = layout.Instances.Select(inst => CellHierarchy.InstanceBbox(inst, layoutDir)).ToList();
        var found = new List<(int, int)>();
        for (int a = 0; a < layout.Instances.Count; a++)
            for (int b = a + 1; b < layout.Instances.Count; b++)
            {
                // A placement no schematic part names (a connector placed by hand) is the user's copper: touching a
                // part is what it is for (brief-sdl-4).
                if (layout.Instances[a].SchematicId is not { Length: > 0 } || layout.Instances[b].SchematicId is not { Length: > 0 }) continue;
                var (ba, bb) = (boxes[a], boxes[b]);
                if (ba.IsEmpty || bb.IsEmpty || ba.MaxX + reach < bb.MinX || bb.MaxX + reach < ba.MinX
                    || ba.MaxY + reach < bb.MinY || bb.MaxY + reach < ba.MinY) continue;
                var na = netsByInstance.GetValueOrDefault(a) ?? [];
                var nb = netsByInstance.GetValueOrDefault(b) ?? [];
                if (na.Overlaps(nb)) continue;
                if (netsByInstance.Any(e => e.Key != a && e.Key != b && e.Value.Overlaps(na) && e.Value.Overlaps(nb))) continue;
                if (Pieces(a, b) < Alone(a) + Alone(b)) found.Add((a, b));
            }
        return found;
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
