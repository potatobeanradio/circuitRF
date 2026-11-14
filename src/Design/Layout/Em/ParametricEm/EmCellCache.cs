// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-6 D3 — the EM results of an EM cell at the geometries it has been evaluated at, keyed on the GEOMETRY, never
// on the values: two values that round to the same copper share one result, and anything that changes the copper (a
// generator change, a hand edit) misses by construction. Plain files under <workspace>/results/em-cache/<cell>/; Layout ▸
// Clear EM Cache removes them, and nothing is evicted automatically.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Results;

namespace CircuitRF.Design.Layout.Em.ParametricEm;

public static class EmCellCache
{
    /// <summary>The cache's folder under a workspace's <c>results/</c>.</summary>
    public const string FolderName = "em-cache";

    /// <summary>Bumped when the canonical form below changes shape, so an old key cannot match a new-scheme one.</summary>
    private const string Scheme = "crf-emcell-1";

    /// <summary>Hex characters kept of the SHA-256: a file name a person can read, and far past collision relevance.</summary>
    private const int KeyLength = 16;

    /// <summary>The sidecar beside each result naming what first produced it.</summary>
    public const string ProvenanceSuffix = ".provenance.txt";

    public static string Root(string workspaceDir) => Path.Combine(ResultsWriter.ResultsDirectory(workspaceDir), FolderName);

    public static string CellFolder(string workspaceDir, string cellName) =>
        Path.Combine(Root(workspaceDir), ResultsWriter.SanitizeFileNameComponent(cellName));

    /// <summary>
    /// The key of <paramref name="layout"/> as <paramref name="setup"/> would solve it: the flattened shapes on every layer
    /// the stackup solves, every port label (the layout's own and the ones placed components declare), the setup's
    /// solve-affecting settings, the stackup, the database unit and the solver's identity.
    /// </summary>
    public static string KeyOf(EmLayoutSource source, EmSetup setup)
    {
        var sb = new StringBuilder(Scheme).Append('\n');
        var tech = source.Technology;

        sb.Append("dbu\t").Append(source.DbuPerMicron.ToString(CultureInfo.InvariantCulture)).Append('\n');

        AppendLayout(sb, source, setup);

        sb.Append("setup\n").Append(SolveSettings(setup)).Append('\n');
        sb.Append("stackup\n").Append(Stackup(tech)).Append('\n');
        sb.Append("solver\t").Append(SolverIdentity(setup)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..KeyLength];
    }

    /// <summary>Bumped when the 3D canonical form changes shape.</summary>
    private const string Scheme3D = "crf-emcell3d-2";

    /// <summary>
    /// brief-sdl-8 D5 — the key of a cell solved through its 3D view: the regenerated layout's copper and port labels (as
    /// <see cref="KeyOf"/> reads them), the 3D document AFTER its VARs are evaluated — every body by its numbers, the
    /// ports, mesh regions, face boundaries and the document's air-box fill, with drawing-only state and the VAR block itself
    /// left out, so a VAR change that moves nothing still hits — the technologies' physics, the held setup's
    /// solve-affecting settings and the solver's identity and version.
    /// </summary>
    /// <param name="inputs">Every other file the elaboration read — a placed 3D view, another cell's layout, the wire-bond
    /// file paired with a layout, a technology or library a placed part resolves — each keyed by its CONTENT, so an edit of
    /// one of them misses where the top document's text alone would not move. The caller leaves out what is keyed above
    /// it (the regenerated layout, which is in memory, and the top document, its cell and its technology).</param>
    /// <param name="workspaceDir">What each input's path is spelled relative to, so a moved workspace keeps its keys.</param>
    public static string KeyOf3D(EmLayoutSource source, CircuitRF.Design.ThreeD.C3dDocument resolved, EmSetup setup,
                                 Technology? documentTech, string solverVersion,
                                 IEnumerable<string>? inputs = null, string? workspaceDir = null)
    {
        var sb = new StringBuilder(Scheme3D).Append('\n');
        sb.Append("dbu\t").Append(source.DbuPerMicron.ToString(CultureInfo.InvariantCulture)).Append('\n');
        AppendLayout(sb, source, setup);
        sb.Append("c3d\n").Append(ResolvedText(resolved)).Append('\n');
        sb.Append("inputs\n").Append(InputsText(inputs ?? [], workspaceDir)).Append('\n');
        sb.Append("setup\n").Append(SolveSettings(setup)).Append('\n');
        sb.Append("stackup\n").Append(Stackup(source.Technology)).Append('\n');
        sb.Append("tech3d\n").Append(documentTech is null ? "none" : CircuitRF.Design.ThreeD.C3dRunDocument.PhysicsText(documentTech)).Append('\n');
        sb.Append("solver\t").Append(SolverIdentity(setup)).Append('|').Append(solverVersion).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..KeyLength];
    }

    /// <summary>brief-sdl-8 D5 — one line per input file, sorted: its path (workspace-relative where it can be) and the
    /// SHA-256 of its bytes, or <c>-</c> when it cannot be read.</summary>
    internal static string InputsText(IEnumerable<string> files, string? workspaceDir)
    {
        var lines = new List<string>();
        foreach (string f in files.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            string name = workspaceDir is { Length: > 0 } ws ? Path.GetRelativePath(ws, f).Replace('\\', '/') : f;
            lines.Add(name + "\t" + (ContentHash(f) ?? "-"));
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    /// <summary>The SHA-256 of a file's bytes, memoised on its length and write time (a sweep reads the same files at every
    /// point); null when it cannot be read.</summary>
    private static string? ContentHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string stamp = path + "|" + info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                           info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            lock (_hashGate) if (_hashes.TryGetValue(stamp, out var hit)) return hit;
            string hash;
            using (var s = File.OpenRead(path)) hash = Convert.ToHexStringLower(SHA256.HashData(s));
            lock (_hashGate) _hashes[stamp] = hash;
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private static readonly Lock _hashGate = new();

    /// <summary>
    /// <paramref name="resolved"/> as its numbers: a file spells a bound field as its EXPRESSION and never writes the number
    /// (C3dBindings), so every expression is set aside while the document is written, then put back. The VAR block and the
    /// embedded setups are left out (the setup is keyed on its own), with every drawing-only field the run form drops.
    /// </summary>
    internal static string ResolvedText(CircuitRF.Design.ThreeD.C3dDocument resolved)
    {
        var owners = CircuitRF.Design.ThreeD.C3dBindings.ItemsOf(resolved)
            .SelectMany(it => CircuitRF.Design.ThreeD.C3dBindings.OwnersOf(it.Item)).Select(o => o.Owner)
            .Where(o => o.Exprs is not null).Distinct().ToList();
        var exprs = owners.Select(o => o.Exprs).ToList();
        var (vars, setups) = (resolved.Variables, resolved.Setups);
        try
        {
            foreach (var o in owners) o.Exprs = null;
            (resolved.Variables, resolved.Setups) = ([], []);
            return CircuitRF.Design.ThreeD.C3dPersistence.SerializeForRun(resolved);
        }
        finally
        {
            for (int i = 0; i < owners.Count; i++) owners[i].Exprs = exprs[i];
            (resolved.Variables, resolved.Setups) = (vars, setups);
        }
    }

    /// <summary>The copper on every solved layer, flattened, then the port labels — the part of a key a layout makes.</summary>
    private static void AppendLayout(StringBuilder sb, EmLayoutSource source, EmSetup setup)
    {
        var tech = source.Technology;
        var solved = SolvedLayers(tech);
        // brief-lcp-1 R-lcp1-4: a planar setup's component ports are ports, and their strips are copper of the solved problem.
        var collected = EmComponentPorts.Collect(source, setup);
        // The copper: top-level shapes, then each instance flattened at every level, in the layout's own order (the
        // planner is deterministic, SDL-0 S5). A label is never copper; a port label is listed below.
        var copper = new LayoutView { DbuPerMicron = source.DbuPerMicron };
        if (collected.ComponentBuild?.Cut is { } cut)
        {
            // brief-icp-8 R-icp8-3 — with instances cut out, the copper is what the solver is handed (the cut's geometry, ICP-2),
            // so what changes the solved problem changes the key, and a cut instance's own shapes — a device's parameter that moves
            // its artwork and nothing else — do not. A setup that cuts nothing keys exactly as before.
            foreach (var s in cut.Shapes)
                if (s is not LabelShape && (solved is null || solved.Contains(s.Layer))) copper.Shapes.Add(s);
        }
        else
        {
            foreach (var s in source.View.Shapes)
                if (s is not LabelShape && (solved is null || solved.Contains(s.Layer))) copper.Shapes.Add(s);
            string baseDir = Path.GetDirectoryName(source.AbsolutePath) ?? "";
            foreach (var inst in source.View.Instances)
                foreach (var s in LayoutFlatten.FlattenAllLevels(inst, baseDir, tech).Shapes)
                    if (s is not LabelShape && (solved is null || solved.Contains(s.Layer))) copper.Shapes.Add(s);
        }
        sb.Append("copper\n").Append(LayoutPersistence.Serialize(copper)).Append('\n');

        var ports = new LayoutView { DbuPerMicron = source.DbuPerMicron };
        foreach (var s in collected.Shapes)
            if (s is LabelShape { IsPort: true }) ports.Shapes.Add(s);
        if (collected.ComponentBuild is { Strips.Count: > 0 } components) ports.Shapes.AddRange(components.Strips);
        sb.Append("ports\n").Append(LayoutPersistence.Serialize(ports)).Append('\n');
    }

    /// <summary>The cached Touchstone for <paramref name="key"/>, or null when this geometry has not been solved.</summary>
    public static string? Find(string workspaceDir, string cellName, string key)
    {
        string dir = CellFolder(workspaceDir, cellName);
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, key + ".s*p")
            .FirstOrDefault(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-f]+\.s\d+p$"));
    }

    /// <summary>Writes the provenance sidecar beside <paramref name="snpPath"/>: the cell, setup and layout, and the values
    /// that first produced this geometry. Never throws — a sidecar that cannot be written costs the record, not the result.</summary>
    public static void WriteProvenance(string snpPath, EmCellTarget target, IReadOnlyDictionary<string, Value> values)
    {
        try
        {
            var lines = new List<string>
            {
                "# EM cache entry (brief-sdl-6): the geometry this file was solved at, keyed by its shape, not its values.",
                "cell = " + target.CellDir,
                "setup = " + target.CemPath,
                "layout = " + target.ClayPath,
                "solved = " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                "# first produced at:",
            };
            foreach (var (name, v) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                lines.Add($"{name} = {Format(v)}");
            File.WriteAllLines(Path.ChangeExtension(snpPath, null) + ProvenanceSuffix, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes every cached result in the workspace; returns how many files went. Never throws.</summary>
    public static int Clear(string workspaceDir)
    {
        string root = Root(workspaceDir);
        if (!Directory.Exists(root)) return 0;
        int n = 0;
        try
        {
            n = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count();
            Directory.Delete(root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return n;
    }

    /// <summary>How many results the workspace's cache holds.</summary>
    public static int Count(string workspaceDir)
    {
        string root = Root(workspaceDir);
        if (!Directory.Exists(root)) return 0;
        try
        {
            return Directory.EnumerateFiles(root, "*.s*p", SearchOption.AllDirectories)
                .Count(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^[0-9a-f]+\.s\d+p$"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    internal static string Format(Value v) => v.Kind switch
    {
        ValueKind.Real => v.AsReal().ToString("R", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>The drawing layers a solve reads artwork on: every layer a stackup entry is drawn on (its conductors and
    /// vias above all), and every MASK layer a patterned dielectric is tied to — whether the layout draws on that mask
    /// decides whether the film is in the run's medium (<see cref="PatternedDielectric"/>), so two layouts that differ only
    /// there must not share a result. Null when there is no stackup to ask (every layer then counts, which can only make
    /// the key stricter).</summary>
    private static HashSet<LayerKey>? SolvedLayers(Technology? tech)
    {
        if (tech?.Stackup.Layers is not { Count: > 0 } layers) return null;
        var set = new HashSet<LayerKey>();
        foreach (var l in layers)
            foreach (var k in l.DrawingLayers) set.Add(k);
        foreach (var (_, film) in ResistiveFilms.All(tech)) set.Add(film.Layer);
        foreach (var k in PatternedDielectric.MaskLayers(tech).Keys) set.Add(k);
        return set.Count == 0 ? null : set;
    }

    /// <summary>The setup with everything that names or places the result blanked — what remains is what the solve reads.</summary>
    private static string SolveSettings(EmSetup setup)
    {
        var c = setup.Clone();
        c.Name = "";
        c.LayoutRef = "";
        c.SnpOutputPathOverride = "";
        c.CreateSnpSchematic = false;
        c.IncludeSchematicComponents = false;
        c.SchematicPortZ0s = [];
        return EmSetupPersistence.Serialize(c);
    }

    private static string Stackup(Technology? tech)
    {
        if (tech is null) return "none";
        try
        {
            // Every material the stackup can name, a library's included: a library edited after a solve is a different
            // dielectric, and keying on the technology's own list alone would hand the old result back.
            var only = new Technology { Stackup = tech.Stackup, Materials = [.. tech.ResolvedMaterials] };
            return TechPersistence.Serialize(only);
        }
        catch (Exception) { return TechPersistence.Serialize(tech); }
    }

    private static string SolverIdentity(EmSetup setup) =>
        $"{setup.AnalysisKind}|{setup.Solver3D}|{typeof(CircuitRF.Engine.Mom.SurfaceMesher).Assembly.GetName().Version}";
}
