// Framework-free. No Avalonia, no SkiaSharp.

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// brief-dfr26-7 R-dfr26-7-3 — the port labels an EM setup reads: the layout's own top-level ones, plus every
/// port a placed COMPONENT declares (<see cref="LabelShape.EmPort"/>), transformed to top-level coordinates.
///
/// <para><b>Why not hand the extractor the flattened shapes.</b> <see cref="EmPortExtraction.Extract"/> infers an
/// unstated port's type from the shapes it is given, and every port drawn before this existed was inferred
/// against the TOP-LEVEL shapes alone; handing it the flattened artwork would quietly change what those ports
/// resolve to. So the top level is passed as it always was, and only the component ports are added beside it —
/// each states its own type, so nothing about them is inferred.</para>
///
/// <para><b>A number used twice is a refusal naming both</b> — a top-level label and a component, or two
/// components — raised here, where the instance each came from is still known, rather than by the extractor,
/// which could only name two labels.</para>
/// </summary>
public static class EmComponentPorts
{
    /// <summary>The labels to extract ports from, the refusal when two name one number, and one note per
    /// component port.</summary>
    public sealed record Result(IReadOnlyList<LayoutShape> Shapes, string? Refusal, IReadOnlyList<string> Notes)
    {
        /// <summary>brief-lcp-1 — what the run warns about: a component port left out, with its reason.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];

        /// <summary>brief-lcp-1 — the setup's component ports on a planar run, when it names any.</summary>
        public EmPlanarComponentPortBuild? ComponentBuild { get; init; }

        /// <summary>R-lcp1-5 — the reference impedance by slot: a component port's own Z0 for its slot, the setup's elsewhere.</summary>
        public Func<int, System.Numerics.Complex> Z0For(EmSetup setup)
            => ComponentBuild is { } b ? b.Z0For(setup) : setup.ResolvePortZ0;
    }

    /// <summary>
    /// brief-lcp-2 R-lcp2-1 — why <paramref name="setup"/> cannot take component ports at all, or null when it can: a thermal
    /// setup and an electrostatic one have no ports to add them to. A magnetostatic setup takes them: its ports are its current
    /// sources. brief-lcp-5 — a 3D setup on a layout takes them too, built in an in-memory 3D view of it (Em3dSetupProblem). The
    /// one rule <c>em --component-ports</c>, <c>explain</c>, <c>check</c> and the setup panel read.
    /// </summary>
    public static string? Inapplicable(EmSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        string name = setup.Name is { Length: > 0 } n ? $"Setup '{n}'" : "This setup";
        if (setup.IsThermal)
            return $"{name} is a thermal setup, which has no ports: component ports apply to an EM setup.";
        if (setup.Problem3D == CircuitRF.Engine.Em3d.Em3dProblemType.Electrostatic)
            return $"{name} is an electrostatic solve, which has no ports: component ports apply to a driven, eigenmode or " +
                   "magnetostatic setup.";
        return null;
    }

    /// <summary>
    /// brief-lcp-1 R-lcp1-4 — <see cref="Collect(EmLayoutSource)"/>, plus the delta-gap label of every component port
    /// <paramref name="setup"/> names on a planar run (<see cref="EmPlanarComponentPorts"/>). The one entry point every caller
    /// that extracts ports on a setup's behalf takes, so the run, the preflight, the panel's Refresh and <c>explain</c> read one
    /// list. A setup with no component ports — or a 3D one, which builds a sheet rather than a strip — gets exactly
    /// <see cref="Collect(EmLayoutSource)"/>.
    /// </summary>
    public static Result Collect(EmLayoutSource source, EmSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        // brief-icp-3 — a 3D setup's cut labels too (EmPlanarComponentPorts.Of): the generator builds a sheet at each.
        var build = EmPlanarComponentPorts.Of(setup, source);
        // brief-icp-2 — a cut instance's own declared ports leave the problem with it.
        var found = Collect(source, build?.Cut?.Excluded);
        if (found.Refusal is not null || build is null || setup.Is3D && build.Cut is null && build.Refusal is null) return found;
        var notes = new List<string>(found.Notes);
        notes.AddRange(build.Notes);
        if (build.Refusal is { } refusal)
            return new Result(found.Shapes, refusal, notes) { Warnings = build.Warnings, ComponentBuild = build };
        return new Result([.. found.Shapes, .. build.Labels], null, notes) { Warnings = build.Warnings, ComponentBuild = build };
    }

    /// <summary>One component-declared port: its label in top-level coordinates and the instance it came from.</summary>
    public sealed record ComponentPort(LabelShape Label, string Owner);

    /// <summary>
    /// <see cref="EmLayoutSource.View"/>'s top-level shapes plus every component-declared EM port inside its
    /// placed instances, at any depth.
    /// </summary>
    public static Result Collect(EmLayoutSource source) => Collect(source, (IReadOnlySet<string>?)null);

    /// <summary>brief-icp-2 — <see cref="Collect(EmLayoutSource)"/> with the cut instances at <paramref name="exclude"/> left out.</summary>
    public static Result Collect(EmLayoutSource source, IReadOnlySet<string>? exclude)
    {
        ArgumentNullException.ThrowIfNull(source);
        var top = source.View.Shapes;
        var found = Find(source.View, source.AbsolutePath, source.Technology, exclude);
        if (found.Count == 0) return new Result(top, null, []);

        string At(LabelShape l) => $"({LayoutUnits.Format(l.X, source.View.DisplayUnit, source.DbuPerMicron)}, " +
                                   $"{LayoutUnits.Format(l.Y, source.View.DisplayUnit, source.DbuPerMicron)} " +
                                   $"{LayoutUnits.Suffix(source.View.DisplayUnit)})";

        var owners = new Dictionary<int, string>();
        foreach (var s in top)
            if (s is LabelShape { IsPort: true } l && EmPortExtraction.TryParseNumber(l.Text, out int n))
                owners.TryAdd(n, $"the port label '{l.Text}' at {At(l)}");

        var notes = new List<string>();
        foreach (var p in found)
        {
            string what = $"the EM port instance '{p.Owner}' declares at {At(p.Label)}";
            if (EmPortExtraction.TryParseNumber(p.Label.Text, out int n))
            {
                if (owners.TryGetValue(n, out var first))
                    return new Result(top,
                        $"Port {n} is named twice: by {first} and by {what}. Port numbers index the s-parameter matrix, " +
                        "so they have to be distinct — renumber one of them (a component's port number is its PortNum).",
                        notes);
                owners[n] = what;
                notes.Add($"Port {n} is {what}.");
            }
            else notes.Add($"A port with no number of its own is {what}; it takes the next free number.");
        }
        return new Result([.. top, .. found.Select(p => p.Label)], null, notes);
    }

    /// <summary>Every component-declared EM port inside <paramref name="view"/>'s placed instances, in top-level
    /// coordinates, in placement order. An instance whose cell carries none (and places nothing of its own) is not
    /// flattened, so a board of ordinary lines costs one cached cell read per instance.</summary>
    public static IReadOnlyList<ComponentPort> Find(LayoutView view, string clayPath, Technology? technology)
        => Find(view, clayPath, technology, null);

    /// <summary>brief-icp-2 R-icp2-1 — <see cref="Find(LayoutView, string, Technology?)"/> with the instances at the paths in
    /// <paramref name="exclude"/> left out: a cut instance's own declared ports leave the problem with it.</summary>
    public static IReadOnlyList<ComponentPort> Find(LayoutView view, string clayPath, Technology? technology, IReadOnlySet<string>? exclude)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Instances.Count == 0) return [];
        string baseDir = Path.GetDirectoryName(clayPath) ?? "";
        var ports = new List<ComponentPort>();
        for (int i = 0; i < view.Instances.Count; i++)
        {
            var inst = view.Instances[i];
            string path = LayoutDesignFlatten.PathOf(inst, i);
            if (exclude is not null && exclude.Contains(path)) continue;
            var res = CellLayoutResolver.Resolve(inst, baseDir, technology);
            if (res.State != CellLayoutState.Resolved || res.View is not { } cell) continue;
            if (cell.Instances.Count == 0 && !cell.Shapes.Any(IsComponentPort)) continue;

            string owner = inst.SchematicId is { Length: > 0 } id ? id : Path.GetFileName(inst.CellRef.TrimEnd('/', '\\'));
            var flat = exclude is { Count: > 0 } ? LayoutFlatten.FlattenAllLevels(inst, baseDir, technology, path, exclude)
                                                 : LayoutFlatten.FlattenAllLevels(inst, baseDir, technology);
            foreach (var s in flat.Shapes)
                if (s is LabelShape l && IsComponentPort(l)) ports.Add(new ComponentPort(l, owner));
        }
        return ports;
    }

    private static bool IsComponentPort(LayoutShape s) => s is LabelShape { IsPort: true, EmPort: true };
}
