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
        var solved = SolvedLayers(tech);

        sb.Append("dbu\t").Append(source.DbuPerMicron.ToString(CultureInfo.InvariantCulture)).Append('\n');

        // The copper: top-level shapes, then each instance flattened at every level, in the layout's own order (the
        // planner is deterministic, SDL-0 S5). A label is never copper; a port label is listed below.
        var copper = new LayoutView { DbuPerMicron = source.DbuPerMicron };
        foreach (var s in source.View.Shapes)
            if (s is not LabelShape && (solved is null || solved.Contains(s.Layer))) copper.Shapes.Add(s);
        string baseDir = Path.GetDirectoryName(source.AbsolutePath) ?? "";
        foreach (var inst in source.View.Instances)
            foreach (var s in LayoutFlatten.FlattenAllLevels(inst, baseDir, tech).Shapes)
                if (s is not LabelShape && (solved is null || solved.Contains(s.Layer))) copper.Shapes.Add(s);
        sb.Append("copper\n").Append(LayoutPersistence.Serialize(copper)).Append('\n');

        var ports = new LayoutView { DbuPerMicron = source.DbuPerMicron };
        foreach (var s in EmComponentPorts.Collect(source).Shapes)
            if (s is LabelShape { IsPort: true }) ports.Shapes.Add(s);
        sb.Append("ports\n").Append(LayoutPersistence.Serialize(ports)).Append('\n');

        sb.Append("setup\n").Append(SolveSettings(setup)).Append('\n');
        sb.Append("stackup\n").Append(Stackup(tech)).Append('\n');
        sb.Append("solver\t").Append(SolverIdentity(setup)).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..KeyLength];
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

    /// <summary>The drawing layers the stackup's conductors and vias are drawn on; null when there is no stackup to ask
    /// (every layer then counts, which can only make the key stricter).</summary>
    private static HashSet<LayerKey>? SolvedLayers(Technology? tech)
    {
        if (tech?.Stackup.Layers is not { Count: > 0 } layers) return null;
        var set = new HashSet<LayerKey>();
        foreach (var l in layers)
            if (l.Kind is StackupKind.Conductor or StackupKind.Via)
                foreach (var k in l.DrawingLayers) set.Add(k);
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
        c.SchematicPortZ0s = [];
        return EmSetupPersistence.Serialize(c);
    }

    private static string Stackup(Technology? tech)
    {
        if (tech is null) return "none";
        try
        {
            var only = new Technology { Stackup = tech.Stackup, Materials = tech.Materials };
            return TechPersistence.Serialize(only);
        }
        catch (Exception) { return TechPersistence.Serialize(tech); }
    }

    private static string SolverIdentity(EmSetup setup) =>
        $"{setup.AnalysisKind}|{setup.Solver3D}|{typeof(CircuitRF.Engine.Mom.SurfaceMesher).Assembly.GetName().Version}";
}
