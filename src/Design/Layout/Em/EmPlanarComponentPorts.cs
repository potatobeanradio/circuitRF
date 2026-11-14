// brief-lcp-1 R-lcp1-2 … R-lcp1-5 — a component port on the Planar solver: a strip across the part's gap, cut by a delta gap.
//
// WHY A STRIP. The planar solver cannot span a slot: every polygon edge is a hard gridline and a cell exists only where a grid
// row's centre is inside metal, so a gap between two pads always owns an empty row, and refining adds rows. A port across a
// part needs metal to cut. So the solved problem gets a strip joining the two pads across the width they share, and an
// internal delta-gap port label across the strip in the middle of the air between the two conductors. Shorted in a circuit
// the pair equals solid metal exactly, and the polarity agrees with a 3D lumped port across the same pads (LCP-0 §Measured).
//
// IN THE SOLVED PROBLEM ONLY (LCP-0 D1). The strip and the label are returned, never written: EmGeometry.ForSetup adds the
// strips to what an extractor sees, EmComponentPorts.Collect(source, setup) adds the labels to what port extraction reads.
// The .clay is not touched.
//
// WHERE THE GAP GOES. At the middle of what is left of the air across the strip's width, not at the pads' midpoint: copper
// reaches into a part's gap (a trace's cap, a pour), and the cut needs metal of the strip on both sides of it.
//
// THE ROW OF AN INTERNAL GAP (R-lcp1-3's first finding, PlanarPort.TryResolveOnLayer). An internal gap's transverse walk has
// no end-face test: it takes every contiguous row where the two cells either side of the cut are metal and paired. On the
// strip the cut lies in the air between the conductors, where only the strip is metal, so the row stops at the strip's sides
// — the strip's long edges are hard gridlines. It would widen only where copper lies against the strip's side at the cut;
// that is refused here, before anything is meshed.
//
// ITS NUMBER IS NOT ITS TEXT. The label's text is the port's name (C1, U1.IN), which the Touchstone header and the .cemr
// carry; its number is stated beside it (StatedNumber) and read by EmPortExtraction ahead of the text, so a part named P1
// is never read as port 1, and the numbers follow every number the layout's own ports use (C3dProblemAssembly's rule).

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Clipper2Lib;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One component port on a planar run, built — or why it could not be.</summary>
/// <param name="Refdes">The part.</param>
/// <param name="Name">The port's name: the refdes, or refdes.pin for a part with several signal pads.</param>
/// <param name="Pin">The positive pad's pin.</param>
/// <param name="ReferencePin">The negative pad's pin.</param>
/// <param name="Strip">The strip joining the two pads, in layout DBU; null when refused.</param>
/// <param name="Gap">The internal delta-gap port label across the strip; null when refused.</param>
/// <param name="Number">The port number (0 when refused).</param>
/// <param name="Z0">The entry's own reference impedance, or null to take the setup's.</param>
/// <param name="GapM">The air between the two conductors across the strip, metres.</param>
/// <param name="WidthM">The strip's width, metres.</param>
/// <param name="AlongX">Whether the gap runs along x (else y).</param>
/// <param name="Refusal">Why it is not built, naming the refdes and what was found.</param>
public sealed record EmPlanarComponentPort(
    string Refdes, string Name, string? Pin, string? ReferencePin, RectShape? Strip, LabelShape? Gap, int Number,
    Complex? Z0, double GapM, double WidthM, bool AlongX, string? Refusal)
{
    public bool Ok => Refusal is null && Gap is not null;

    /// <summary>brief-lcp-2 R-lcp2-2 — the stackup conductor both pads are on; null when refused.</summary>
    public string? Conductor { get; init; }

    /// <summary>brief-lcp-2 R-lcp2-2 — the positive and negative pads' nets, as the board projection reads them.</summary>
    public (string? Positive, string? Negative) Nets { get; init; }

    /// <summary>brief-lcp-7 R-lcp7-3 — every pin of the part, in its footprint's pin order: a multi-pad part's model terminals map
    /// onto them in the co-simulation netlist.</summary>
    public IReadOnlyList<string> PartPins { get; init; } = [];

    /// <summary>brief-lcp-7 — the placement's <c>SchematicId</c>: the board schematic's instance the part's model is read from.</summary>
    public string? SchematicId { get; init; }

    /// <summary>brief-lcp-7 — the layout the part is placed on.</summary>
    public string? ClayPath { get; init; }

    /// <summary>brief-icp-2 — a cut instance's pin port: an Uncalibrated Port at the cut face (<see cref="Gap"/> is its edge label,
    /// <see cref="Strip"/> null), its <see cref="Refdes"/> the instance's path and its <see cref="Name"/> <c>path.pin</c>.</summary>
    public bool Cutout { get; init; }

    /// <summary>brief-icp-2 — a cut instance's cell, as a sentence names it (the generator for a generated cell); null otherwise.</summary>
    public string? Cell { get; init; }

    /// <summary>brief-icp-5 R-icp5-1 — a cut instance's pin port: the instance as the co-simulation netlist reads it (its path, schematic
    /// chain and every pin by class), shared by its ports; null otherwise.</summary>
    public EmCoSimCut? CutPart { get; init; }

    /// <summary>brief-lcp-7 — a connector by its refdes (<see cref="C3dComponentPorts.IsConnectorRefdes"/>): a Port line of the
    /// co-simulation netlist, not a part across its port. A layout has no 3D shell to bridge, so the refdes is all that says so.</summary>
    public bool Connector { get; init; }
}

/// <summary>The component ports a planar setup asks for, built against one layout.</summary>
/// <param name="Ports">Each wanted part's ports in list order, refused ones included with their reason.</param>
/// <param name="Notes">One line per built port.</param>
/// <param name="Warnings">One line per part left out.</param>
/// <param name="Refusal">Why the setup's list as a whole cannot be built (a part named twice, nothing to build).</param>
public sealed record EmPlanarComponentPortBuild(
    IReadOnlyList<EmPlanarComponentPort> Ports, IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings, string? Refusal)
{
    /// <summary>The strips to add to the geometry.</summary>
    public IReadOnlyList<RectShape> Strips => [.. Ports.Where(p => p.Ok && p.Strip is not null).Select(p => p.Strip!)];

    /// <summary>brief-icp-2 — the setup's cut instances applied to the layout's geometry, or null when it cuts none. Its shapes are
    /// what <see cref="EmGeometry.ForSetup"/> hands every extractor in place of the plain flatten.</summary>
    public EmCellCutoutResult? Cut { get; init; }

    /// <summary>brief-icp-3 — the layout's cell instances the build projected (<see cref="EmCellInstanceSet.Empty"/> when it
    /// placed none, or for a refusal reached before the projection).</summary>
    public EmCellInstanceSet Instances { get; init; } = EmCellInstanceSet.Empty;

    /// <summary>brief-icp-3 R-icp3-4 — the setup's entries the cut read (<see cref="EmPartPads.Selection.InstanceEntries"/>), as
    /// written: what a 3D view's footprint build leaves out of its own list.</summary>
    public IReadOnlyList<EmComponentPort> InstanceEntries { get; init; } = [];

    /// <summary>brief-icp-2 — the gap ports alone: what the co-simulation netlist puts a part across (a cut instance's model goes
    /// back on its pins in ICP-5).</summary>
    public bool AnyGapPort => Ports.Any(p => p.Ok && !p.Cutout);

    /// <summary>brief-icp-5 — any component port built, a cut instance's pin included: what makes a run write its co-simulation
    /// netlist, now that a cut instance's model goes back on its pins.</summary>
    public bool AnyPort => Ports.Any(p => p.Ok);

    /// <summary>The port labels to add to what port extraction reads.</summary>
    public IReadOnlyList<LabelShape> Labels => [.. Ports.Where(p => p.Ok).Select(p => p.Gap!)];

    /// <summary>
    /// R-lcp1-5 — the reference impedance by slot (<c>portNumber - 1</c>): a built port's entry Z0 wins for its own slot, and
    /// every other slot is <paramref name="setup"/>'s <see cref="EmSetup.ResolvePortZ0"/>. <see cref="EmSetup.PortZ0s"/> is not
    /// written.
    /// </summary>
    public Func<int, Complex> Z0For(EmSetup setup)
    {
        var own = Ports.Where(p => p.Ok && p.Z0 is not null).ToDictionary(p => p.Number - 1, p => p.Z0!.Value);
        return slot => own.TryGetValue(slot, out var z) ? z : setup.ResolvePortZ0(slot);
    }
}

public static class EmPlanarComponentPorts
{
    /// <summary>How finely the copper facing a gap is scanned across the strip's width.</summary>
    public const double ScanStepM = C3dComponentPorts.ScanStepM;

    private sealed class CacheEntry(string key, EmPlanarComponentPortBuild? build)
    {
        public string Key { get; } = key;
        public EmPlanarComponentPortBuild? Build { get; } = build;
    }

    private static readonly ConditionalWeakTable<EmLayoutSource, CacheEntry> Cache = new();
    private static readonly ConditionalWeakTable<LabelShape, StrongBox<int>> Numbers = new();
    private static readonly Lock CacheGate = new();

    /// <summary>The number a built port label states apart from its text, read by <see cref="EmPortExtraction"/> first.</summary>
    internal static bool StatedNumber(LabelShape label, out int number)
    {
        if (Numbers.TryGetValue(label, out var box)) { number = box.Value; return true; }
        number = 0;
        return false;
    }

    /// <summary>
    /// The component ports <paramref name="setup"/> names, built on <paramref name="source"/>'s layout and verified. Null when
    /// the setup names none, or is a 3D setup (a 3D run builds a sheet, not a strip). Built once per setup and source and
    /// reused: a board has dozens of parts and every caller of <see cref="EmGeometry.ForSetup"/> and
    /// <see cref="EmComponentPorts.Collect(EmLayoutSource, EmSetup)"/> asks. Reads the footprints' files; writes nothing.
    /// </summary>
    public static EmPlanarComponentPortBuild? Build(EmSetup setup, EmLayoutSource source)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        if (setup.ComponentPorts.Count == 0 || setup.Is3D) return null;
        return Cached(setup, source);
    }

    /// <summary>
    /// brief-icp-3 R-icp3-1 — what <paramref name="setup"/> makes of <paramref name="source"/>'s layout for the geometry and the
    /// port labels, on either solver: a Planar setup's <see cref="Build"/>; a 3D setup's cut alone — the cell instances it names cut
    /// out of the layout and an edge label at each routed pin face, with ICP-2's verdicts, names and numbers — since a 3D view
    /// builds its footprint parts' ports itself (<see cref="C3dComponentPorts"/>). Null for a 3D setup that names no cell instance
    /// (or whose list is refused on a layout of footprint parts only, which the footprint build then refuses in its own words).
    /// What <see cref="EmGeometry.ForSetup"/> and <see cref="EmComponentPorts.Collect(EmLayoutSource, EmSetup)"/> read.
    /// </summary>
    public static EmPlanarComponentPortBuild? Of(EmSetup setup, EmLayoutSource source)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        if (!setup.Is3D) return Build(setup, source);
        if (setup.ComponentPorts.Count == 0) return null;
        return Cached(setup, source);
    }

    /// <summary>brief-icp-3 — the setup terms a build reads, for a cache keyed by them (the elaborator's per-layout cut).</summary>
    public static string SetupKey(EmSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        string key = KeyOf(setup, null);
        return key[..key.LastIndexOf('#')];
    }

    private static EmPlanarComponentPortBuild? Cached(EmSetup setup, EmLayoutSource source)
    {
        string key = KeyOf(setup, source);
        lock (CacheGate)
            if (Cache.TryGetValue(source, out var hit) && hit.Key == key) return hit.Build;
        // brief-icp-2 R-icp2-8 — one build per (layout content, setup): the panel's Refresh, Check, the layout's marks and the mesh
        // preview each hold their own copy of the layout, and on a chip the build is a walk of every cell; they share it here.
        string content = ContentKeyOf(setup, source);
        EmPlanarComponentPortBuild? built;
        bool shared;
        lock (CacheGate) shared = Shared.TryGetValue(content, out built);
        if (!shared)
        {
            built = BuildCore(setup, source);
            lock (CacheGate)
            {
                if (!Shared.ContainsKey(content)) { SharedOrder.Enqueue(content); if (SharedOrder.Count > SharedCapacity) Shared.Remove(SharedOrder.Dequeue()); }
                Shared[content] = built;
            }
        }
        lock (CacheGate) Cache.AddOrUpdate(source, new CacheEntry(key, built));
        return built;
    }

    /// <summary>brief-icp-2 R-icp2-8 — whether <see cref="Build"/> (<see cref="Of"/> for a 3D setup) on this setup and layout would return at once (built already, by
    /// any caller holding a copy of the same layout): what the panel asks before deciding to build off its thread.</summary>
    public static bool IsBuilt(EmSetup setup, EmLayoutSource source)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        // brief-icp-3 — a 3D setup's build is the cut alone (Of), cached on the same terms.
        if (setup.ComponentPorts.Count == 0) return true;
        string key = KeyOf(setup, source);
        lock (CacheGate)
            if (Cache.TryGetValue(source, out var hit) && hit.Key == key) return true;
        string content = ContentKeyOf(setup, source);
        lock (CacheGate) return Shared.ContainsKey(content);
    }

    private const int SharedCapacity = 8;
    private static readonly Dictionary<string, EmPlanarComponentPortBuild?> Shared = new(StringComparer.Ordinal);
    private static readonly Queue<string> SharedOrder = new();

    /// <summary>What a build reads, by content: the layout's text and every cell file its flatten reads (by write time), the
    /// technology, the layout's path (cell references resolve against it), and the setup's component ports, levels and region.</summary>
    private static string ContentKeyOf(EmSetup setup, EmLayoutSource source)
    {
        var sb = new System.Text.StringBuilder();
        string setupKey = KeyOf(setup, source);    // its last term is the view's object state, which content replaces
        sb.Append(setupKey[..setupKey.LastIndexOf('#')]).Append('#').Append(source.DbuPerMicron).Append('#').Append(source.AbsolutePath).Append('#');
        sb.Append(source.Technology?.Name).Append('/').Append(source.Technology is { } t ? RuntimeHelpers.GetHashCode(t) : 0).Append('#');
        sb.Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LayoutPersistence.Serialize(source.View)))));
        foreach (var f in LayoutFlatten.FilesRead(source.View, source.AbsolutePath))
            sb.Append('|').Append(f).Append('@').Append(File.GetLastWriteTimeUtc(f).Ticks);
        return sb.ToString();
    }

    /// <summary>What the build depends on beyond the source: the list, the levels and the region — and enough of the view to
    /// notice an edit made to the same object.</summary>
    private static string KeyOf(EmSetup setup, EmLayoutSource? source)
        => string.Join("|", setup.ComponentPorts.Select(c => $"{c.Refdes}\u001f{c.Z0}\u001f{c.Reference}\u001f{c.WidthUm?.ToString("R", CultureInfo.InvariantCulture)}" +
                                                             $"\u001f{c.Cell}\u001f{c.PortAt}\u001f{c.KeepOutUm}\u001f{c.Construction}\u001f{c.Keep}\u001f{c.Pins}\u001f{c.Ground}"))
           + "#" + string.Join(",", setup.AnalysisLevelNames) + "#" + setup.SolveRegion?.Describe() + (setup.Is3D ? "#3d" : "")
           + "#" + (source is null ? "" : source.View.Shapes.Count + "/" + source.View.Instances.Count + "/" + source.View.EditCount + "/" + source.DbuPerMicron);

    private sealed record Region(int Index, LayerKey Layer, Em3dExtrudedPolygon Poly, Paths64 Dbu);

    private static EmPlanarComponentPortBuild? BuildCore(EmSetup setup, EmLayoutSource source)
    {
        var notes = new List<string>();
        var warnings = new List<string>();
        var view = source.View;
        int dbuPerMicron = source.DbuPerMicron > 0 ? source.DbuPerMicron : view.DbuPerMicron;
        double dbu = C3dLowering.Metres(1, dbuPerMicron);
        EmPlanarComponentPortBuild Refuse(string why) => new([], notes, warnings, why);

        // ── The parts, by refdes ───────────────────────────────────────────────────────────────────
        var placed = EmPartPads.Project(view, source.AbsolutePath, source.Technology, C3dTransform.Identity);
        if (placed.Refusal is { } pr) return Refuse($"The board '{Path.GetFileName(source.AbsolutePath)}' cannot be projected: {pr}");
        var parts = new Dictionary<string, PlacedPart>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in placed.Parts)
            if (!parts.TryAdd(part.Refdes, part)) ambiguous.Add(part.Refdes);
        // brief-icp-2 — the cell instances a component port can cut out, projected once per build, over the analysis levels' layers.
        var levelLayers = source.Technology?.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && !l.IsGroundReference
                        && (setup.AnalysisLevelNames.Count == 0 || setup.AnalysisLevelNames.Contains(l.Name, StringComparer.Ordinal)))
            .SelectMany(l => l.DrawingLayers).ToHashSet();
        var instances = view.Instances.Count > 0
            ? EmCellInstances.Project(view, source.AbsolutePath, source.Technology, C3dTransform.Identity, levelLayers)
            : EmCellInstanceSet.Empty;
        var selection = EmPartPads.Select(setup, parts, "this layout", instances);
        // brief-icp-3 — a 3D setup's footprint parts are the 3D view's to build (C3dComponentPorts), so here it takes the cut alone. A
        // list refused on a layout of footprint parts only is refused there in its own words; one refused over cell instances is this
        // build's, and the 3D view refuses with it.
        bool threeD = setup.Is3D;
        bool cellInstances = instances.Instances.Any(i => i.Kind != EmInstanceKind.Footprint);
        if (threeD && (selection.Refusal is not null ? !cellInstances : selection.InstanceEntries.Count == 0 && selection.Instances.Count == 0))
            return null;
        if (selection.Refusal is { } selectWhy) return Refuse(selectWhy) with { Instances = instances, InstanceEntries = selection.InstanceEntries };
        foreach (var (entry, why) in selection.Unplaced)
            warnings.Add($"{why} That entry is left out of this run; the other component ports are built.");

        // brief-icp-2 R-icp2-1 — the cut, in the order the cut instances' ports are numbered (R-icp2-5): by the entry that took each,
        // in list order, then in the hierarchy's walk order.
        var cuts = selection.Instances.Select((c, i) => (c, i)).OrderBy(t => EntryRank(setup, t.c.Instance)).ThenBy(t => t.i).Select(t => t.c).ToList();
        var cut = cuts.Count > 0 ? EmCellCutout.Apply(setup, source, cuts, instances) : null;

        // ── The copper, one region per connected piece on each layer a pad is on ──────────────────────
        var flat = cut?.Shapes ?? EmGeometry.Flatten(view, source.AbsolutePath, source.Technology).Shapes;
        var padLayers = placed.Parts.SelectMany(p => p.Pads).Select(p => p.Layer).OfType<LayerKey>().ToHashSet();
        // brief-icp-4 — a pin-referenced cut port is LCP's strip and delta gap between two faces, read against the same copper.
        if (cut is not null && !threeD) padLayers.UnionWith(cut.Pins.Where(p => p.HasPort && p.Reference is not null).Select(p => p.Layer));
        var regions = new List<Region>();
        foreach (var layer in padLayers)
        {
            // Every drawing layer bound to the pad's conductor is that conductor's copper: a second layer drawn onto the same
            // metal joins the pads, touches the strip or lies against the cut exactly as copper on the pad's own layer does.
            var same = source.Technology?.Stackup.Layers
                           .FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(layer))?.DrawingLayers;
            var onConductor = same is { Count: > 0 } ? same.ToHashSet() : [layer];
            regions.AddRange(Regions(flat.Where(s => onConductor.Contains(s.Layer) && s is not LabelShape and not BitmapShape), layer, dbu,
                                     regions.Count));
        }

        var groundNets = new HashSet<string>(EmPartPads.GroundNames, StringComparer.OrdinalIgnoreCase);
        bool IsGround(PartPad p) => p.Net is { } n ? groundNets.Contains(n) : groundNets.Contains(p.Pin);

        // ── Numbering: after every number the layout's own ports and its placed parts' declared ports use ──
        int next = EmPortExtraction.NumberPorts(EmComponentPorts.Collect(source, cut?.Excluded).Shapes).Select(p => p.Number).DefaultIfEmpty(0).Max() + 1;

        var results = new List<EmPlanarComponentPort>();
        var noModel = new List<string>();
        foreach (var entry in threeD ? Array.Empty<EmComponentPort>() : selection.Wanted)
        {
            string rd = entry.Refdes;
            if (ambiguous.Contains(rd)) { results.Add(Refused(rd, EmPartPads.PlacedMoreThanOnce(rd))); continue; }
            if (!parts.TryGetValue(rd, out var part))
            {
                results.Add(Refused(rd, $"{rd} is no part placed on this layout" + EmPartPads.KnownParts(parts.Keys) + "."));
                continue;
            }
            if (part.Why is { } why) { results.Add(Refused(rd, why)); continue; }

            Complex? z0 = null;
            if (entry.Z0 is { Length: > 0 } zText)
            {
                if (!C3dPorts.TryParseZ0(zText, out var z) || !(z.Real > 0))
                {
                    results.Add(Refused(rd, $"{rd}'s Z0 '{zText}' is not a reference impedance: write a number of ohms with a positive " +
                                            "real part, or a complex one such as 25+j10."));
                    continue;
                }
                z0 = z;
            }

            // Each pad on the copper of its own layer.
            var lands = new List<(PartPad Pad, Region Region)>();
            string? padWhy = null;
            foreach (var pad in part.Pads)
            {
                var region = pad.Layer is { } lk
                    ? regions.FirstOrDefault(r => r.Layer == lk && C3dThermal.Inside(r.Poly, new Point3(pad.Cx, pad.Cy, 0.5), 0))
                    : null;
                if (region is not null) lands.Add((pad, region));
                else padWhy ??= $"{rd}'s pad {pad.Pin} at ({Mm(pad.Cx)}, {Mm(pad.Cy)}) mm lands on no copper of its own layer " +
                                $"{(pad.Layer is { } l ? $"{l.Layer}/{l.Datatype}" : "(unknown)")}.";
            }
            if (padWhy is not null) { results.Add(Refused(rd, padWhy)); continue; }

            if (EmPartPads.Pair(rd, lands, l => l.Pad, l => l.Pad.Net ?? "", l => IsGround(l.Pad),
                                (a, b) => a.Pad.Layer == b.Pad.Layer, entry.Reference, out var pairs) is { } pairWhy)
            {
                results.Add(Refused(rd, pairWhy));
                continue;
            }

            bool connector = C3dComponentPorts.IsConnectorRefdes(rd);
            bool anyBuilt = false;
            foreach (var (pos, neg) in pairs)
            {
                string name = pairs.Count == 1 ? rd : $"{rd}.{pos.Pad.Pin}";
                double? widthM = entry.WidthUm is > 0 and var w ? w * 1e-6 : null;
                var built = Construct(name, rd, pos, neg, regions, setup, source, dbu, widthM, out string? constructWhy);
                if (built is null)
                {
                    results.Add(Refused(rd, constructWhy!) with { Name = name, Pin = pos.Pad.Pin, ReferencePin = neg.Pad.Pin });
                    continue;
                }
                int number = next++;
                Numbers.AddOrUpdate(built.Value.Gap, new StrongBox<int>(number));
                results.Add(new EmPlanarComponentPort(rd, name, pos.Pad.Pin, neg.Pad.Pin, built.Value.Strip, built.Value.Gap, number, z0,
                                                      built.Value.GapM, built.Value.WidthM, built.Value.AlongX, null)
                {
                    Conductor = source.Technology?.Stackup.Layers
                        .FirstOrDefault(l => l.Kind == StackupKind.Conductor && pos.Pad.Layer is { } pl && l.DrawingLayers.Contains(pl))?.Name,
                    Nets = (pos.Pad.Net, neg.Pad.Net),
                    PartPins = [.. part.Pads.Select(pd => pd.Pin)], SchematicId = part.SchematicId,
                    ClayPath = source.AbsolutePath, Connector = connector,
                });
                anyBuilt = true;
            }
            if (anyBuilt && part.HasModel == false && !connector) noModel.Add(rd);
        }

        // ── brief-icp-2 — the cut instances' pins: an Uncalibrated Port at each routed pin's face, numbered after the gap ports ──
        if (cut is not null)
        {
            if (cut.Notes.Count > 0) notes.AddRange(cut.Notes.Where(n => !notes.Contains(n)));
            var coSim = EmCoSimCut.Of(cut, instances, cuts);
            foreach (var pin in cut.Pins)
            {
                var entry = cuts.FirstOrDefault(c => c.Instance.Path.Equals(pin.Path, StringComparison.OrdinalIgnoreCase))?.Entry;
                var part = coSim.GetValueOrDefault(pin.Path);
                if (pin.Why is { } why)
                {
                    results.Add(Refused(pin.Path, why) with { Name = pin.Name, Pin = pin.Pin.Length > 0 ? pin.Pin : null, Cutout = true, Cell = pin.Cell, CutPart = part });
                    continue;
                }
                if (pin.Class == EmPinClass.Floating)
                {
                    warnings.Add(pin.Unreturned
                        ? $"{pin.Name} has Port: false and no port of {pin.Path} returns through it, so {pin.Path}'s model sees an open there."
                        : $"{pin.Name} has nothing connected to it, so it gets no port and {pin.Path}'s model sees an open there.");
                    continue;
                }
                // brief-icp-4 R-icp4-5 / R-icp4-1 — a grounded pin and a pin that is only a return carry no port.
                if (pin.Class == EmPinClass.Grounded)
                {
                    notes.Add(pin.Face is null
                        ? $"{pin.Name} is tied to ground and nothing meets its face: no port, and its terminal goes to the N-port's reference."
                        : $"{pin.Name} is tied to ground at its face by a ground via {Um(pin.WidthM)} µm wide: no port.");
                    continue;
                }
                if (pin.Class == EmPinClass.Return)
                {
                    var through = cut.Pins.Where(o => o.Path == pin.Path && o.HasPort && string.Equals(o.Reference, pin.Pin, StringComparison.OrdinalIgnoreCase))
                                         .Select(o => o.Name).ToList();
                    notes.Add($"{pin.Name} is the return of {string.Join(" and ", through)}: no port of its own.");
                    continue;
                }
                if (pin.Class == EmPinClass.Abutting)
                {
                    notes.Add($"{pin.Name} touches only {pin.Abuts}, which is cut out too: no port; the two terminals join directly.");
                    continue;
                }
                if (!pin.HasPort) continue;
                Complex? z0 = null;
                if (entry?.Z0 is { Length: > 0 } zText)
                {
                    if (!C3dPorts.TryParseZ0(zText, out var z) || !(z.Real > 0))
                    {
                        results.Add(Refused(pin.Path, $"{pin.Path}'s Z0 '{zText}' is not a reference impedance: write a number of ohms with a " +
                                                     "positive real part, or a complex one such as 25+j10.") with { Name = pin.Name, Pin = pin.Pin, Cutout = true, Cell = pin.Cell, CutPart = part });
                        continue;
                    }
                    z0 = z;
                }
                // brief-icp-4 R-icp4-2 — referenced to another pin: on Planar, LCP's strip across the vacated space from this pin's face to
                // its reference's, cut by a delta gap (Construct, the one a footprint part's port is), verified as that one is.
                RectShape? strip = null;
                var portLabel = pin.Label!;
                double gapM = 0, widthM = pin.WidthM;
                bool alongX = pin.OutwardDeg is 0 or 180;
                if (pin.Reference is { } refPin && !threeD)
                {
                    var rPin = cut.Pins.FirstOrDefault(o => o.Path == pin.Path && o.Pin.Equals(refPin, StringComparison.OrdinalIgnoreCase));
                    var pos = FacePad(pin, dbu);
                    var neg = rPin?.Face is not null ? FacePad(rPin, dbu) : null;
                    var posRegion = regions.FirstOrDefault(rg => rg.Layer == pin.Layer && C3dThermal.Inside(rg.Poly, new Point3(pos.Cx, pos.Cy, 0.5), 0));
                    var negRegion = neg is null ? null
                        : regions.FirstOrDefault(rg => rg.Layer == rPin!.Layer && C3dThermal.Inside(rg.Poly, new Point3(neg.Cx, neg.Cy, 0.5), 0));
                    string? gapWhy = posRegion is null || negRegion is null ? "the copper at its face or at its Reference's face was not found." : null;
                    var made = gapWhy is null
                        ? Construct(pin.Name, pin.Path, (pos, posRegion!), (neg!, negRegion!), regions, setup, source, dbu,
                                    entry?.WidthUm is > 0 and var ew ? ew * 1e-6 : null, out gapWhy)
                        : null;
                    if (made is null)
                    {
                        results.Add(Refused(pin.Path, gapWhy!.StartsWith(pin.Name, StringComparison.Ordinal) ? gapWhy : $"{pin.Name}: {gapWhy}")
                                    with { Name = pin.Name, Pin = pin.Pin, ReferencePin = refPin, Cutout = true, Cell = pin.Cell, CutPart = part });
                        continue;
                    }
                    (strip, portLabel, gapM, widthM, alongX) = made.Value;
                }
                int number = next++;
                Numbers.AddOrUpdate(portLabel, new StrongBox<int>(number));
                results.Add(new EmPlanarComponentPort(pin.Path, pin.Name, pin.Pin, pin.Reference, strip, portLabel, number, z0, gapM, widthM,
                                                      alongX, null)
                {
                    Conductor = source.Technology?.Stackup.Layers
                        .FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(pin.Layer))?.Name,
                    ClayPath = source.AbsolutePath, Cutout = true, Cell = pin.Cell, CutPart = part,
                    SchematicId = instances.At(pin.Path)?.SchematicId,
                });
            }
        }

        foreach (var r in results.Where(p => p.Refusal is not null))
            warnings.Add($"{r.Refusal} {(r.Cutout && r.Pin is not null ? r.Name : r.Refdes)} is left out of this run; the other component ports are built.");
        // A 3D run's cut ports are sheets, described by the 3D view that builds them; the notes here are the cut's own.
        if (!threeD) foreach (var r in results.Where(p => p.Ok)) notes.Add(Describe(r, source));
        // R-lcp7-2 — the .c3d's sentence (C3dComponentPorts.Build), word for word: the co-simulation netlist stands a placeholder there.
        if (noModel.Count > 0)
            warnings.Add($"{string.Join(", ", noModel)} {(noModel.Count == 1 ? "has" : "have")} no model on the board's schematic, so the " +
                         $"co-simulation netlist will carry a placeholder for {(noModel.Count == 1 ? "it" : "each")}.");

        // A part that cannot be built is left out and the others run; the list refuses only when nothing at all is left to drive.
        // A 3D view decides that over its footprint parts and drawn ports too (C3dProblemAssembly).
        if (threeD)
            return new EmPlanarComponentPortBuild(results, notes, warnings, null)
                { Cut = cut, Instances = instances, InstanceEntries = selection.InstanceEntries };
        if (!results.Any(p => p.Ok) && !EmComponentPorts.Collect(source, cut?.Excluded).Shapes.Any(s => s is LabelShape { IsPort: true }))
            return new EmPlanarComponentPortBuild(results, notes, [],
                "None of the component ports this setup names could be built, and the layout has no other port to drive: " +
                (results.FirstOrDefault(p => p.Refusal is not null)?.Refusal
                 ?? warnings.FirstOrDefault() ?? "every pin of every cut instance is unconnected.")) { Cut = cut };
        return new EmPlanarComponentPortBuild(results, notes, warnings, null) { Cut = cut };

        static EmPlanarComponentPort Refused(string rd, string why) => new(rd, rd, null, null, null, null, 0, null, 0, 0, false, why);
    }

    /// <summary>brief-icp-4 — a cut pin's face as the pad LCP's construction faces: a sliver of its conductor just beyond the face,
    /// across the face's width, in metres.</summary>
    private static PartPad FacePad(EmCutPin pin, double dbu)
    {
        var (x0, y0, x1, y1) = pin.Face!.Value;
        bool normalX = x0 == x1;
        int q = (int)Math.Round(pin.OutwardDeg / 90.0) & 3;
        double sign = q is 0 or 1 ? 1 : -1;                          // the conductor lies beyond the face, outward of the instance
        double at = (normalX ? x0 : y0) * dbu, lo = (normalX ? y0 : x0) * dbu, hi = (normalX ? y1 : x1) * dbu;
        double depth = Math.Min(hi - lo, 5e-6);
        double n0 = Math.Min(at, at + sign * depth), n1 = Math.Max(at, at + sign * depth);
        return normalX ? new PartPad(pin.Path, pin.Pin, null, pin.Layer, (n0 + n1) / 2, (lo + hi) / 2, n0, lo, n1, hi)
                       : new PartPad(pin.Path, pin.Pin, null, pin.Layer, (lo + hi) / 2, (n0 + n1) / 2, lo, n0, hi, n1);
    }

    /// <summary>The connected pieces of <paramref name="shapes"/>' copper, each an outline with its holes, in metres and DBU.</summary>
    private static IEnumerable<Region> Regions(IEnumerable<LayoutShape> shapes, LayerKey layer, double dbu, int firstIndex)
    {
        var subject = new Paths64();
        foreach (var s in shapes) subject.AddRange(LayoutClipper.ToClipperPaths(s, 1));
        if (subject.Count == 0) yield break;
        var tree = new PolyTree64();
        var clipper = new Clipper64();
        clipper.AddSubject(subject);
        clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
        var outers = new List<PolyPath64>();
        void Walk(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (!child.IsHole) outers.Add(child);
                Walk(child);
            }
        }
        Walk(tree);
        int index = firstIndex;
        foreach (var o in outers)
        {
            var holes = new List<Path64>();
            for (int k = 0; k < o.Count; k++) holes.Add(o[k].Polygon!);
            IReadOnlyList<Point2> Ring(Path64 p) => [.. p.Select(q => new Point2(q.X * dbu, q.Y * dbu))];
            var poly = new Em3dExtrudedPolygon(Ring(o.Polygon!), [.. holes.Select(Ring)], 0, 1);
            yield return new Region(index++, layer, poly, [o.Polygon!, .. holes]);
        }
    }

    // ── The construction (R-lcp1-2) and its checks (R-lcp1-3) ──────────────────────────────────────

    private static (RectShape Strip, LabelShape Gap, double GapM, double WidthM, bool AlongX)? Construct(
        string name, string rd, (PartPad Pad, Region Region) pos, (PartPad Pad, Region Region) neg, IReadOnlyList<Region> regions,
        EmSetup setup, EmLayoutSource source, double dbu, double? widthM, out string? why)
    {
        why = null;
        string label = name == rd ? rd : $"{rd} (pad {pos.Pad.Pin})";
        var (pp, np) = (pos.Pad, neg.Pad);

        // The pads are already one conductor: a strip would close a loop, not make a port.
        if (ReferenceEquals(pos.Region, neg.Region))
        {
            why = $"{label}: pads {pp.Pin} and {np.Pin} lie on one piece of copper already, so the part is shorted by the copper and " +
                  "a strip between them would close a loop rather than make a port.";
            return null;
        }
        if (pp.Net is { Length: > 0 } pn && string.Equals(pn, np.Net, StringComparison.OrdinalIgnoreCase))
        {
            why = $"{label}: pads {pp.Pin} and {np.Pin} are both on net {pn}, so a strip between them would close a loop rather than make a port.";
            return null;
        }
        if (EmPartPads.Facing(pp, np) is not { } face)
        {
            why = EmPartPads.NotFacing(label, pp, np);
            return null;
        }
        if (pp.Layer != np.Layer || pp.Layer is not { } layer)
        {
            why = $"{label}: its pads are on two copper layers; a component port lies between two pads on one copper layer.";
            return null;
        }

        // On an analysis level of this setup.
        var conductor = source.Technology?.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(layer));
        if (conductor is null)
        {
            why = $"{label}: its pads' layer {layer.Layer}/{layer.Datatype} is not bound to a conductor of the stackup, so the planar run " +
                  "does not mesh it.";
            return null;
        }
        if (conductor.IsGroundReference)
        {
            why = $"{label}: its pads are on '{conductor.Name}', the ground reference plane, which the planar run does not mesh.";
            return null;
        }
        if (setup.AnalysisLevelNames.Count > 0 && !setup.AnalysisLevelNames.Contains(conductor.Name, StringComparer.Ordinal))
        {
            why = $"{label}: its pads are on '{conductor.Name}', which is not one of this setup's analysis levels " +
                  $"({string.Join(", ", setup.AnalysisLevelNames.Select(n => $"'{n}'"))}).";
            return null;
        }

        // Inside the solve region.
        if (setup.SolveRegion is { } solveRegion)
        {
            bool In(PartPad p) => p.X0 * 1e6 >= solveRegion.XMinUm - 1e-9 && p.X1 * 1e6 <= solveRegion.XMaxUm + 1e-9 &&
                                  p.Y0 * 1e6 >= solveRegion.YMinUm - 1e-9 && p.Y1 * 1e6 <= solveRegion.YMaxUm + 1e-9;
            if (!In(pp) || !In(np))
            {
                why = $"{label}: the part lies outside this EM setup's solve region {solveRegion.Describe()}.";
                return null;
            }
        }

        bool ax = face.AlongX;
        bool posLow = ax ? pp.Cx < np.Cx : pp.Cy < np.Cy;
        var (low, high) = posLow ? (pos, neg) : (neg, pos);
        var (fw0, fw1) = widthM is { } wm ? ((face.W0 + face.W1) / 2 - wm / 2, (face.W0 + face.W1) / 2 + wm / 2) : (face.W0, face.W1);
        double w0 = Math.Ceiling(fw0 / dbu - 1e-6) * dbu, w1 = Math.Floor(fw1 / dbu + 1e-6) * dbu;
        if (w1 - w0 < 2 * dbu)
        {
            why = $"{label}: pads {pp.Pin} and {np.Pin} share no width across the gap.";
            return null;
        }
        var lowPoly = low.Region.Poly;
        var highPoly = high.Region.Poly;
        double lc = ax ? low.Pad.Cx : low.Pad.Cy, hc = ax ? high.Pad.Cx : high.Pad.Cy;

        // The middle of the gap is where the two conductors' copper leaves off on the centre line, not the middle between the
        // pads (C3dComponentPorts' rule): a pour often fills most of the space between a part's pads.
        double mid = ((ax ? low.Pad.X1 : low.Pad.Y1) + (ax ? high.Pad.X0 : high.Pad.Y0)) / 2;
        {
            double vc = (w0 + w1) / 2;
            var lowRun = C3dComponentPorts.Intervals(lowPoly, vc, ax).Where(t => t.A <= lc && lc <= t.B).ToList();
            var highRun = C3dComponentPorts.Intervals(highPoly, vc, ax).Where(t => t.A <= hc && hc <= t.B).ToList();
            if (lowRun.Count == 1 && highRun.Count == 1 && highRun[0].A > lowRun[0].B) mid = (lowRun[0].B + highRun[0].A) / 2;
        }

        // Across the width, every 10 µm and through every vertex of either conductor that faces the gap.
        double inset = Math.Min(2 * dbu, (w1 - w0) / 4);
        int n = Math.Clamp((int)Math.Ceiling((w1 - w0 - 2 * inset) / ScanStepM) + 1, 5, 4001);
        var lines = Enumerable.Range(0, n).Select(i => w0 + inset + (w1 - w0 - 2 * inset) * i / (n - 1)).ToList();
        foreach (var (poly, isLow) in new[] { (lowPoly, true), (highPoly, false) })
            foreach (var q in poly.Outline.Concat(poly.Holes.SelectMany(h => h)))
            {
                double u = ax ? q.X : q.Y, v = ax ? q.Y : q.X;
                if (v > w0 + inset && v < w1 - inset && (isLow ? u >= lc && u <= mid : u <= hc && u >= mid)) lines.Add(v);
            }
        double airLow = double.NegativeInfinity, airHigh = double.PositiveInfinity;
        foreach (double v in lines.Distinct().Order())
        {
            var lo = C3dComponentPorts.Intervals(lowPoly, v, ax);
            var hi = C3dComponentPorts.Intervals(highPoly, v, ax);
            if (lo.Any(t => t.A < mid && t.B > mid) || hi.Any(t => t.A < mid && t.B > mid))
            {
                why = $"{label}: copper of pad {(lo.Any(t => t.A < mid && t.B > mid) ? low.Pad.Pin : high.Pad.Pin)}'s conductor runs across " +
                      $"the middle of the gap at {(ax ? "y" : "x")} = {Mm(v)} mm, so the two pads have no gap between them there.";
                return null;
            }
            var loFace = lo.Where(t => t.B <= mid).ToList();
            var hiFace = hi.Where(t => t.A >= mid).ToList();
            if (loFace.Count == 0 || hiFace.Count == 0)
            {
                why = $"{label}: across the gap at {(ax ? "y" : "x")} = {Mm(v)} mm no copper of pad " +
                      $"{(loFace.Count == 0 ? low.Pad.Pin : high.Pad.Pin)}'s conductor faces it.";
                return null;
            }
            airLow = Math.Max(airLow, loFace.Max(t => t.B));
            airHigh = Math.Min(airHigh, hiFace.Min(t => t.A));
        }
        if (airHigh - airLow < 2 * dbu)
        {
            why = $"{label}: the copper facing its gap leaves {Um(airHigh - airLow)} µm of air between its two pads' conductors.";
            return null;
        }
        double g = Math.Round((airLow + airHigh) / 2 / dbu, MidpointRounding.AwayFromZero) * dbu;

        // The strip, from pad centre to pad centre across the width: it overlaps each pad's copper and spans the air.
        long D(double m) => (long)Math.Round(m / dbu, MidpointRounding.AwayFromZero);
        long u0 = D(lc), u1 = D(hc), v0 = D(w0), v1 = D(w1);
        var strip = ax
            ? new RectShape { Layer = layer, X1 = u0, Y1 = v0, X2 = u1, Y2 = v1 }
            : new RectShape { Layer = layer, X1 = v0, Y1 = u0, X2 = v1, Y2 = u1 };

        // It touches no third conductor.
        var stripPaths = Clipper.InflatePaths(LayoutClipper.ToClipperPaths(strip, 1), 1, JoinType.Miter, EndType.Polygon);
        foreach (var other in regions.Where(r => r.Layer == layer && !ReferenceEquals(r, low.Region) && !ReferenceEquals(r, high.Region)))
        {
            var touch = Clipper.Intersect(other.Dbu, stripPaths, FillRule.EvenOdd);
            if (touch.Count == 0 || touch.All(t => Math.Abs(Clipper.Area(t)) <= 0)) continue;
            var at = Clipper.GetBounds(touch);
            why = $"{label}: its strip would touch other copper at ({Mm((at.left + at.right) / 2.0 * dbu)}, {Mm((at.top + at.bottom) / 2.0 * dbu)}) mm, " +
                  "joining a third conductor to the part's two pads.";
            return null;
        }

        // The gap's row stays on the strip: no copper lies against the strip's sides at the cut (the file header says why).
        foreach (double side in new[] { w0 - dbu, w1 + dbu })
        {
            double x = ax ? g : side, y = ax ? side : g;
            if (regions.FirstOrDefault(r => r.Layer == layer && C3dThermal.Inside(r.Poly, new Point3(x, y, 0.5), 0)) is not { } beside) continue;
            string whose = ReferenceEquals(beside, low.Region) ? $"pad {low.Pad.Pin}'s conductor"
                         : ReferenceEquals(beside, high.Region) ? $"pad {high.Pad.Pin}'s conductor" : "other copper";
            why = $"{label}: copper of {whose} lies against its strip at the gap ({Mm(x)}, {Mm(y)}) mm, so the port's cut would reach " +
                  "past the strip into that metal.";
            return null;
        }

        // The label: across the strip at the gap, pointing AT the positive pad. A delta gap's direction is the way positive port
        // current crosses the cut, and positive port current enters the part's circuit at its + terminal, as an edge port's
        // arrow points into the line it drives. Pointing it at the negative pad put + on the wrong pad: every component port
        // came out with its sign reversed against the 3D build and against the circuit (S31 = -0.75 where a hand analysis of a
        // shunt part on the input node gives +0.75).
        var direction = ax ? (posLow ? LayoutRotation.R180 : LayoutRotation.R0) : (posLow ? LayoutRotation.R270 : LayoutRotation.R90);
        long gx = ax ? D(g) : (v0 + v1) / 2, gy = ax ? (v0 + v1) / 2 : D(g);
        var gap = new LabelShape
        {
            Layer = layer, PortLayer = layer, X = gx, Y = gy, Text = name, Height = Math.Max(1, (v1 - v0) / 2), IsPort = true,
            PortKind = PlanarPortKind.InternalDeltaGap, PortDirection = direction,
        };
        return (strip, gap, airHigh - airLow, w1 - w0, ax);
    }

    /// <summary>brief-icp-2 R-icp2-5 — the position in the setup's list of the entry that takes <paramref name="i"/>: a named path, else
    /// its cell's entry, else the <c>*</c> or <c>X/*</c> that reached it.</summary>
    private static int EntryRank(EmSetup setup, EmCellInstance i)
    {
        var list = setup.ComponentPorts;
        for (int k = 0; k < list.Count; k++)
            if (list[k].Cell is null && list[k].Refdes.Trim().Trim('/').Equals(i.Path, StringComparison.OrdinalIgnoreCase)) return k;
        for (int k = 0; k < list.Count; k++)
            if (list[k].Cell is { } cell && i.IsCell(cell)) return k;
        for (int k = 0; k < list.Count; k++)
        {
            string r = list[k].Refdes.Trim();
            if (list[k].Cell is null && (r == EmSetup.AllComponents
                || r.EndsWith("/" + EmSetup.AllComponents, StringComparison.Ordinal) && EmCellInstanceSet.IsInside(i.Path, r[..^2].Trim('/'))))
                return k;
        }
        return list.Count;
    }

    // ── Words ──────────────────────────────────────────────────────────────────────────────────────

    private static string Mm(double m) => C3dComponentPorts.Mm(m);
    private static string Um(double m) => C3dComponentPorts.Um(m);

    /// <summary>One built port's line: what the run notes and the setup's list show.</summary>
    public static string Describe(EmPlanarComponentPort p, EmLayoutSource source)
    {
        if (!p.Ok) return $"{(p.Cutout ? p.Name : p.Refdes)}: refused — {p.Refusal}";
        double dbu = C3dLowering.Metres(1, source.DbuPerMicron);
        if (p.Cutout)
        {
            string facing = p.Gap!.PortDirection switch
            {
                LayoutRotation.R0 => "+x", LayoutRotation.R90 => "+y", LayoutRotation.R180 => "−x", _ => "−y",
            };
            if (p.ReferencePin is { } rp)
                return $"{p.Name}: component port {p.Number}, a delta gap across a strip where {p.Refdes}{(p.Cell is { } cr ? $" ({cr})" : "")} is cut " +
                       $"out, from pin {p.Pin}'s face to pin {rp}'s, {Um(p.GapM)} µm of air along {(p.AlongX ? "x" : "y")}, {Um(p.WidthM)} µm wide" +
                       $"{(p.Conductor is { Length: > 0 } cnr ? $" on '{cnr}'" : "")}, cut at ({Mm(p.Gap.X * dbu)}, {Mm(p.Gap.Y * dbu)}) mm; verified; " +
                       $"{p.Refdes} and the strip are in the solved problem only, not the layout.";
            return $"{p.Name}: component port {p.Number}, an Uncalibrated Port at the face where {p.Refdes}{(p.Cell is { } cl ? $" ({cl})" : "")} " +
                   $"is cut out, pin {p.Pin}, {Um(p.WidthM)} µm wide, driving {facing}{(p.Conductor is { Length: > 0 } cn ? $" on '{cn}'" : "")}, " +
                   $"at ({Mm(p.Gap.X * dbu)}, {Mm(p.Gap.Y * dbu)}) mm; {p.Refdes} is left out of the solved problem only, not the layout.";
        }
        static string Pad(string? pin, string? net) => net is { Length: > 0 } n ? $"pad {pin} (net {n})" : $"pad {pin}";
        string on = p.Conductor is { Length: > 0 } c ? $" on '{c}'" : "";
        return $"{p.Name}: component port {p.Number}, a delta gap across a strip, {Pad(p.Pin, p.Nets.Positive)} to " +
               $"{Pad(p.ReferencePin, p.Nets.Negative)}{on}, gap {Um(p.GapM)} µm of air along {(p.AlongX ? "x" : "y")}, " +
               $"{Um(p.WidthM)} µm wide, cut at ({Mm(p.Gap!.X * dbu)}, {Mm(p.Gap.Y * dbu)}) mm; verified; " +
               "in the solved problem only, not written to the layout.";
    }
}
