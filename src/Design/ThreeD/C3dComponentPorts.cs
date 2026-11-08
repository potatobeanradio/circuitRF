// brief-emb-1 R-emb1-2 … R-emb1-6 — component ports: a lumped gap port across each placed part's own pads, built on the
// lowered 3D problem and verified before anything is meshed.
//
// WHY A CONSTRUCTION AND NOT A DRAWN RECTANGLE. A drawn lumped port is inferred from what its four edges touch to within
// one DBU. On imported artwork the copper bounding a part's gap is round trace caps, zone-fill arcs and short segments, so
// almost no rectangle touches it, and every way a hand-placed sheet fails — reaching into copper (a surface triangle that is
// no face of any tetrahedron), stopped short of it (an open circuit, or slivers) — is a sheet edge short of or inside
// copper. The construction here is the one that measured clean on a real board:
//
//   1. The part's two pads come from the board projection `netlist --placement` uses (BoardCompanions.Project), so a refdes
//      means here what it means there; each pad's outline is its footprint shape through the instance transform, and it
//      lands on the conductor of the lowered problem that holds its centre on its own copper layer.
//   2. The gap runs along the axis joining the two pads; the port is as wide as the two pads overlap across it.
//   3. Across that width, every 10 µm, the closest copper of each pad's conductor facing the gap is found. Where it is one
//      straight face, the sheet's edge is that face. Where it is not (a cap's arc, a zone's scallop), a LANDING rectangle is
//      added to that conductor: its face at the closest copper, reaching back until its back edge lies in solid copper across
//      the whole width (a rectangle resting on a cap's tip leaves an air slot beside it). The landing is unioned INTO the
//      conductor's outline, so the conductor keeps its name and count; it exists only in the lowered problem.
//   4. The sheet is flat, at mid-copper height, from face to face, both ends stated.
//
// VERIFIED BEFORE MESHING (R-emb1-4), in check as well as the run: the landings change no conductor count (no net bridged),
// each sheet's end edges touch exactly its two conductors and its side edges touch none, no copper lies inside the sheet
// (sampled on a 2 µm grid), and the two conductors are different nets. A failure is a refusal naming the refdes, the edge
// and the conductor found.
//
// GAP PORTS, NEVER GROUND-REFERENCED PAIRS (R-emb1-5). A two-terminal part is one gap port between its pads; a part with
// more pads is one gap port per signal pad against the part's own ground pad. A part between two pad-to-plane ports carries
// its current, as far as the EM is concerned, down one sheet, along the plane and up the other — a loop that is not on the
// board.
//
// A CONNECTOR'S SHELL (R-emb1-6). A part with ground pads on two copper layers that lie over each other is a coaxial launch:
// its shell clamps the two grounds together at the launch, so the lowered problem bridges each such pair with a copper block
// through the substrate, and the port's negative end is that bridged ground.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using Pad = CircuitRF.Design.Layout.Em.PartPad;

namespace CircuitRF.Design.ThreeD;

/// <summary>A landing rectangle added to a conductor in the lowered problem only (never written to the <c>.clay</c>), world metres.</summary>
/// <param name="Port">The component port it is an end of (its refdes, or refdes.pin).</param>
/// <param name="Conductor">The conductor it is unioned into.</param>
/// <param name="Box">Its plan extent: x0, y0, x1, y1.</param>
/// <param name="AlongX">True when the gap it faces runs along x.</param>
public sealed record C3dLanding(string Port, string Conductor, (double X0, double Y0, double X1, double Y1) Box, bool AlongX)
{
    /// <summary>The ends of the side facing the gap, on the DBU grid: the port's sheet has these two corners there.</summary>
    public (Point2 A, Point2 B) Face { get; init; }

    /// <summary>How far it reaches along the gap, metres.</summary>
    public double DepthM => AlongX ? Box.X1 - Box.X0 : Box.Y1 - Box.Y0;

    /// <summary>How wide it is across the gap, metres.</summary>
    public double WidthM => AlongX ? Box.Y1 - Box.Y0 : Box.X1 - Box.X0;
}

/// <summary>R-emb1-6 — a copper block bridging a connector's top and bottom ground pads through the substrate.</summary>
public sealed record C3dShellBridge(string Name, string Top, string Bottom, (double X0, double Y0, double X1, double Y1) Box);

/// <summary>One component port, built — or why it could not be.</summary>
/// <param name="Refdes">The part.</param>
/// <param name="Pin">The positive pad's pin.</param>
/// <param name="ReferencePin">The negative pad's pin.</param>
/// <param name="Port">The sheet, as a document port with both ends stated; null when refused.</param>
/// <param name="GapM">The sheet's length along the gap, metres.</param>
/// <param name="WidthM">Its width across the gap, metres.</param>
/// <param name="AlongX">Whether the gap runs along x (else y).</param>
/// <param name="Landings">The landing rectangles its two ends needed.</param>
/// <param name="Refusal">Why it is not built, naming the refdes and what was found.</param>
/// <param name="Connector">True when the part is a coaxial launch (its ground pads bridged) or its refdes is a J.</param>
public sealed record C3dComponentPort(
    string Refdes, string? Pin, string? ReferencePin, C3dPort? Port, double GapM, double WidthM, bool AlongX,
    IReadOnlyList<C3dLanding> Landings, string? Refusal, bool Connector)
{
    /// <summary>The verification's sentence, once it ran: <c>verified</c> or why not.</summary>
    public string Verdict { get; init; } = "";

    /// <summary>R-emb1-7 — every pin of the part, in its footprint's pin order: a multi-pad part's model terminals map onto them.</summary>
    public IReadOnlyList<string> PartPins { get; init; } = [];

    /// <summary>The placement's <c>SchematicId</c>: the board schematic's instance the part's model is read from.</summary>
    public string? SchematicId { get; init; }

    /// <summary>The layout the part is placed on.</summary>
    public string? ClayPath { get; init; }

    /// <summary>The nets of its two conductors, positive first, where the board states them.</summary>
    public (string? Positive, string? Negative) Nets { get; init; }

    public bool Ok => Port is not null && Refusal is null;
}

/// <summary>The component ports a setup asks for, built against one elaboration.</summary>
/// <param name="Ports">Each port, in number order (refused ones included, with their refusal).</param>
/// <param name="Elaboration">The elaboration with every landing and shell bridge applied: what the run solves and what the
/// ports are resolved against.</param>
/// <param name="Bridges">The connector shell bridges added.</param>
/// <param name="Notes">What a run notes about them.</param>
/// <param name="Warnings">What <c>check</c> warns about: a part with no model on the board's schematic (R-emb1-7).</param>
public sealed record C3dComponentPortBuild(
    IReadOnlyList<C3dComponentPort> Ports, C3dElaboration Elaboration, IReadOnlyList<C3dShellBridge> Bridges,
    IReadOnlyList<string> Notes, IReadOnlyList<string> Warnings, string? Refusal)
{
    /// <summary>The first refusal — the whole build's, or a port's.</summary>
    public string? FirstRefusal => Refusal ?? Ports.FirstOrDefault(p => p.Refusal is not null)?.Refusal;

    /// <summary>Every part built: no port is left out. A run proceeds with the ones that were (<see cref="Warnings"/> names the
    /// rest) unless <see cref="Refusal"/> is set.</summary>
    public bool Ok => FirstRefusal is null;

    /// <summary>The built ports as document ports, for <see cref="C3dPorts.Resolve(C3dDocument, C3dPortContext)"/>.</summary>
    public IReadOnlyList<C3dPort> DocumentPorts => [.. Ports.Where(p => p.Port is not null).Select(p => p.Port!)];
}

public static class C3dComponentPorts
{
    /// <summary>How finely the copper facing a gap is scanned across the port's width.</summary>
    public const double ScanStepM = 10e-6;

    /// <summary>How finely a sheet is sampled for copper inside it (R-emb1-4).</summary>
    public const double SampleStepM = 2e-6;

    /// <summary>How far past the closest copper a landing's back edge first tries to reach, and the step it reaches back by.</summary>
    public const double LandingStepM = 10e-6;

    /// <summary>How far a connector's shell bridge is kept inside the ground pads it joins.</summary>
    public const double ShellInsetM = 25e-6;

    /// <summary>The integer scale polygon unions are made at: 0.1 nm.</summary>
    private const double Scale = 1e10;

    /// <summary>The nets read as a part's ground pin when nothing else says (case-insensitive).</summary>
    private static IReadOnlyList<string> GroundNames => EmPartPads.GroundNames;

    /// <summary>
    /// R-emb1-2/3/4 — the component ports <paramref name="setup"/> names, built on <paramref name="drawn"/> (the document's
    /// elaboration as the run makes it) and verified. Numbered from <paramref name="firstNumber"/> in list order. Null when the
    /// setup names none. Reads the placed layouts' files, or the layout <paramref name="layoutsInMemory"/> holds for a path (the
    /// elaborator's own lookup, so the pads come from the same layout as the copper); writes nothing.
    /// </summary>
    public static C3dComponentPortBuild? Build(EmSetup setup, C3dDocument document, C3dElaboration drawn, int firstNumber,
                                               TechnologyCache? cache = null,
                                               IReadOnlyDictionary<string, LayoutView>? layoutsInMemory = null)
    {
        if (setup.ComponentPorts.Count == 0) return null;
        cache ??= new TechnologyCache();
        var notes = new List<string>();
        var warnings = new List<string>();
        C3dComponentPortBuild Refuse(string why) => new([], drawn, [], notes, warnings, why);

        var frames = drawn.Instances.Where(f => f.Layout && File.Exists(f.DocumentPath)).ToList();
        if (frames.Count == 0)
            return Refuse("This setup names component ports, which are built on the parts placed on a layout, and this 3D view " +
                          "places no layout. Place the board's layout as an instance with View Layout (new view3d <cell> --from-layout).");

        // ── The parts, by refdes, from each placed layout's board projection ───────────────────
        var parts = new Dictionary<string, Part>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in frames)
        {
            if (Board(frame, cache, notes, layoutsInMemory) is not { } board) continue;
            if (board.Refusal is { } br) return Refuse($"The board '{Path.GetFileName(frame.DocumentPath)}' cannot be projected: {br}");
            foreach (var part in board.Parts)
                if (!parts.TryAdd(part.Refdes, part)) ambiguous.Add(part.Refdes);
        }

        var groundNets = new HashSet<string>(GroundNames, StringComparer.OrdinalIgnoreCase);
        groundNets.UnionWith(Em3dGroundList.Names(setup.Ground3D));
        foreach (string o in drawn.GroundBandObjects)
            if (drawn.ObjectNets.TryGetValue(o, out var ns)) groundNets.UnionWith(ns);

        // ── Which parts ───────────────────────────────────────────────────────────────────────
        // R-lcp1-1: the rule a layout's .cem follows too (EmPartPads.Select).
        var selection = EmPartPads.Select(setup, parts.ToDictionary(kv => kv.Key, kv => kv.Value.Placed, StringComparer.OrdinalIgnoreCase),
                                          "this 3D view's layout");
        if (selection.Refusal is { } selectWhy) return Refuse(selectWhy);
        var wanted = selection.Wanted;

        // ── Build each part's ports ─────────────────────────────────────────────────────────────
        double dbu = C3dLowering.Metres(1, document.DbuPerMicron);
        var conductors = drawn.Solids.Where(s => s.Role == Em3dRole.Conductor && !drawn.NotModelled.Contains(s.Name)).ToList();
        var built = new List<Draft>();
        var bridges = new List<C3dShellBridge>();
        var results = new List<C3dComponentPort>();
        var noModel = new List<string>();
        int number = firstNumber;
        foreach (var entry in wanted)
        {
            string rd = entry.Refdes;
            if (ambiguous.Contains(rd))
            {
                results.Add(Refused(rd, $"{rd} is placed on more than one layout of this 3D view, so which one is meant is not known."));
                continue;
            }
            if (!parts.TryGetValue(rd, out var part))
            {
                results.Add(Refused(rd, $"{rd} is no part placed on this 3D view's layout" + EmPartPads.KnownParts(parts.Keys) + "."));
                continue;
            }
            if (part.Why is { } why) { results.Add(Refused(rd, why)); continue; }

            // Each pad on its conductor.
            var lands = new List<(Pad Pad, Em3dSolid Solid)>();
            string? padWhy = null;
            foreach (var pad in part.Pads)
            {
                if (LandOf(pad, part.Frame, conductors, drawn) is { } solid) lands.Add((pad, solid));
                else padWhy ??= $"{rd}'s pad {pad.Pin} at ({Mm(pad.Cx)}, {Mm(pad.Cy)}) mm lands on no conductor of the 3D model on its own " +
                                $"copper layer: its layer {(pad.Layer is { } lk ? $"{lk.Layer}/{lk.Datatype}" : "(unknown)")} is not a conductor of the " +
                                "stackup, or the copper under it is not modelled.";
            }
            if (padWhy is not null) { results.Add(Refused(rd, padWhy)); continue; }

            // R-emb1-6 — a connector's shell: ground pads on two copper layers, over each other.
            bool isGround((Pad Pad, Em3dSolid Solid) l) =>
                l.Pad.Net is { } n && groundNets.Contains(n) || drawn.GroundBandObjects.Contains(l.Solid.Name) ||
                l.Pad.Net is null && l.Pad.Pin is { } pn && groundNets.Contains(pn);
            var shell = ShellBridges(rd, lands.Where(isGround).ToList(), drawn, bridges.Count);
            bridges.AddRange(shell);
            bool connector = shell.Count > 0 || IsConnectorRefdes(rd);

            // Which pads pair: R-emb1-5, the rule a layout's .cem follows too (EmPartPads.Pair).
            string? pairWhy = EmPartPads.Pair(rd, lands, l => l.Pad, l => l.Solid.Name, isGround,
                                              (a, b) => Math.Abs(Top(a.Solid) - Top(b.Solid)) < 1e-9, entry.Reference, out var pairs);
            if (pairWhy is not null) { results.Add(Refused(rd, pairWhy)); continue; }

            foreach (var (pos, neg) in pairs)
            {
                string name = pairs.Count == 1 ? rd : $"{rd}.{pos.Pad.Pin}";
                double? widthM = entry.WidthUm is > 0 and var w ? w * 1e-6 : null;
                var draft = Construct(name, rd, pos, neg, dbu, out string? constructWhy, widthM);
                if (draft is null)
                {
                    results.Add(Refused(rd, constructWhy!) with { Pin = pos.Pad.Pin, ReferencePin = neg.Pad.Pin, Connector = connector });
                    continue;
                }
                draft = draft with { Number = number++, Z0 = entry.Z0, Connector = connector, PosNet = pos.Pad.Net, NegNet = neg.Pad.Net };
                built.Add(draft);
                results.Add(new C3dComponentPort(rd, pos.Pad.Pin, neg.Pad.Pin, null, draft.GapM, draft.WidthM, draft.AlongX, draft.Landings,
                                                 null, connector)
                {
                    PartPins = [.. part.Pads.Select(pd => pd.Pin)], SchematicId = part.SchematicId, ClayPath = part.Frame.DocumentPath,
                });
            }
            if (part.HasModel == false && !connector) noModel.Add(rd);
        }

        // ── Apply every landing and bridge, then verify ────────────────────────────────────────
        // A port whose landing cannot be applied, or which fails verification, is left out and the rest are built: one part's
        // copper never costs every other part its port. Leaving one out takes its landings out of the geometry too, so the
        // others are applied and verified again without them, until nothing more is left out.
        var dropped = new Dictionary<string, string>(StringComparer.Ordinal);     // by port name
        C3dElaboration applied;
        while (true)
        {
            var live = built.Where(d => !dropped.ContainsKey(d.Name)).ToList();
            var (app, applyWhy, culprits) = Apply(drawn, live.SelectMany(d => d.Landings).ToList(), bridges, dbu);
            if (applyWhy is { } aw)
            {
                var named = live.Where(d => culprits.Contains(d.Name)).ToList();
                if (named.Count == 0) return new C3dComponentPortBuild(results, drawn, bridges, notes, warnings, aw);
                foreach (var d in named) dropped[d.Name] = aw;
                continue;
            }
            applied = app;
            var context = new C3dPortContext(applied, document.DbuPerMicron, null, []);
            bool more = false;
            // Each port's verification only reads the applied geometry and its own sheet, so they run side by side; the verdicts
            // land in list order, as one after another would leave them.
            var verdicts = new string?[live.Count];
            Parallel.For(0, live.Count, i => verdicts[i] = Verify(live[i], applied, context, dbu));
            for (int i = 0; i < live.Count; i++)
                if (verdicts[i] is { } v) { dropped[live[i].Name] = v; more = true; }
            if (!more) break;
        }
        // The ports left in are numbered without a gap, in list order.
        int next = firstNumber;
        for (int i = 0; i < built.Count; i++)
            if (!dropped.ContainsKey(built[i].Name)) built[i] = built[i] with { Number = next++ };
        var final = new List<C3dComponentPort>();
        foreach (var r in results)
        {
            if (r.Refusal is not null) { final.Add(r); continue; }
            var d = built.First(b => b.Refdes == r.Refdes && (b.PosPin == r.Pin));
            string? verdict = dropped.GetValueOrDefault(d.Name);
            var port = new C3dPort
            {
                Number = d.Number,
                Name = d.Name,
                Kind = Em3dPortKind.Lumped,
                Plane = C3dPlane.XY,
                Offset = (long)Math.Round(d.ZMid / dbu, MidpointRounding.AwayFromZero),
                Rect = new C3dRect
                {
                    Min = new C3dPoint2((long)Math.Round(d.X0 / dbu), (long)Math.Round(d.Y0 / dbu)),
                    Size = new C3dPoint2((long)Math.Round((d.X1 - d.X0) / dbu), (long)Math.Round((d.Y1 - d.Y0) / dbu)),
                },
                Z0 = d.Z0 is { Length: > 0 } z ? z : "50",
                Positive = d.Positive,
                Negative = d.Negative,
            };
            final.Add(r with
            {
                Port = verdict is null ? port : null,
                Refusal = verdict,
                Verdict = verdict ?? "verified",
                Nets = (d.PosNet, d.NegNet),
                Landings = verdict is null ? r.Landings : [],
            });
        }
        foreach (var r in final.Where(p => p.Refusal is not null))
            warnings.Add($"{r.Refusal} {r.Refdes} is left out of this run; the other component ports are built.");
        if (noModel.Count > 0)
            warnings.Add($"{string.Join(", ", noModel)} {(noModel.Count == 1 ? "has" : "have")} no model on the board's schematic, so the " +
                         $"co-simulation netlist will carry a placeholder for {(noModel.Count == 1 ? "it" : "each")}.");
        if (bridges.Count > 0)
            notes.Add($"{bridges.Count} copper block(s) bridge connector ground pads through the substrate, as a connector's shell does: " +
                      string.Join(", ", bridges.Select(b => $"'{b.Name}' ({b.Top} to {b.Bottom})")) + ".");
        int landings = final.Sum(p => p.Landings.Count);
        if (landings > 0)
            notes.Add($"{landings} landing rectangle(s) extend the copper facing {final.Count(p => p.Landings.Count > 0)} component port(s) " +
                      "to a straight face; they are in the solved problem only and are not written to the layout.");
        return new C3dComponentPortBuild(final, applied, bridges, notes, warnings, null);

        static C3dComponentPort Refused(string rd, string why) => new(rd, null, null, null, 0, 0, false, [], why, false);
    }

    /// <summary>R-emb1-8 — <c>"C1,C2,L2"</c> or <c>"*"</c> as setup entries; empty entries are dropped.</summary>
    public static List<EmComponentPort> ParseList(string text)
        => [.. text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(r => new EmComponentPort(r))];

    /// <summary>R-emb1-8 — every part placed on the elaboration's layouts, by refdes in reading order, with its pad count and why
    /// it could not be built on, when that is already known: what the setup panel's list offers.</summary>
    public static IReadOnlyList<(string Refdes, int Pads, string? Why)> PlacedParts(C3dElaboration e, TechnologyCache? cache = null)
    {
        cache ??= new TechnologyCache();
        var list = new List<(string, int, string?)>();
        foreach (var frame in e.Instances.Where(f => f.Layout && File.Exists(f.DocumentPath)))
            if (Board(frame, cache, []) is { Refusal: null } board)
                list.AddRange(board.Parts.Select(p => (p.Refdes, p.Pads.Count, p.Why)));
        return [.. list.DistinctBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Item1, NaturalOrder.Instance)];
    }

    /// <summary>A refdes that names a connector by the usual letter: J followed by a digit.</summary>
    public static bool IsConnectorRefdes(string refdes)
        => refdes.Length >= 2 && char.ToUpperInvariant(refdes[0]) == 'J' && char.IsDigit(refdes[1]);

    // ── The board, projected ─────────────────────────────────────────────────────────────────────

    /// <summary>A placed part (R-lcp1-1's projection) and the 3D instance frame its layout is placed by.</summary>
    private sealed record Part(PlacedPart Placed, C3dInstanceFrame Frame)
    {
        public string Refdes => Placed.Refdes;
        public IReadOnlyList<Pad> Pads => Placed.Pads;
        public string? Why => Placed.Why;
        public bool? HasModel => Placed.HasModel;
        public string? SchematicId => Placed.SchematicId;
    }

    private sealed record BoardParts(IReadOnlyList<Part> Parts, string? Refusal);

    /// <summary>One placed layout's parts: the projection's pads, each with its footprint shape's outline, in world metres.</summary>
    private static BoardParts? Board(C3dInstanceFrame frame, TechnologyCache cache, List<string> notes,
                                     IReadOnlyDictionary<string, LayoutView>? layoutsInMemory = null)
    {
        LayoutView view;
        if (layoutsInMemory?.GetValueOrDefault(Path.GetFullPath(frame.DocumentPath)) is { } inMemory) view = inMemory;
        else try { view = LayoutPersistence.LoadFromFile(frame.DocumentPath); }
        catch (Exception e) { return new BoardParts([], $"it cannot be read: {e.Message}"); }
        var (tech, _) = TechnologyResolver.ResolveForDocument(view.TechRef, frame.DocumentPath, null, cache);
        var placed = EmPartPads.Project(view, frame.DocumentPath, tech.Tech, frame.World);
        if (placed.Refusal is { } refusal) return new BoardParts([], refusal);
        notes.AddRange(placed.Notes.Select(n => $"{Path.GetFileName(frame.DocumentPath)}: {n}"));
        return new BoardParts([.. placed.Parts.Select(p => new Part(p, frame))], null);
    }

    /// <summary>The conductor a pad lands on: a flat conductor of its layout instance, built from its own copper layer, holding
    /// its centre.</summary>
    private static Em3dSolid? LandOf(Pad pad, C3dInstanceFrame frame, IReadOnlyList<Em3dSolid> conductors, C3dElaboration e)
    {
        string prefix = frame.Path + "/";
        var holding = conductors.Where(s => s.Name.StartsWith(prefix, StringComparison.Ordinal) && s.Primitive is Em3dExtrudedPolygon ep &&
                                            Holds(ep, pad.Cx, pad.Cy, 0)).ToList();
        if (holding.Count == 0) return null;
        var onLayer = holding.Where(s => e.Origins.TryGetValue(s.Name, out var o) && o.Kind == Em3dObjectKind.Conductor &&
                                         pad.Layer is { } lk && o.DrawingLayer == lk).ToList();
        if (onLayer.Count > 0) return onLayer.OrderByDescending(s => ((Em3dExtrudedPolygon)s.Primitive).ZTop).First();
        // No origin to say: the pad's own face — the topmost conductor for a pad on top, which is every pad a layer could not name.
        return pad.Layer is null ? holding.OrderByDescending(s => ((Em3dExtrudedPolygon)s.Primitive).ZTop).First() : null;
    }

    // ── The construction (R-emb1-3) ─────────────────────────────────────────────────────────────

    private sealed record Draft(
        string Name, string Refdes, string? PosPin, string Positive, string Negative,
        double X0, double Y0, double X1, double Y1, double ZMid, double GapM, double WidthM, bool AlongX,
        IReadOnlyList<C3dLanding> Landings)
    {
        public int Number { get; init; }
        public string? Z0 { get; init; }
        public bool Connector { get; init; }
        public string? PosNet { get; init; }
        public string? NegNet { get; init; }
    }

    private static (bool AlongX, double W0, double W1)? Facing(Pad a, Pad b) => EmPartPads.Facing(a, b);

    private static double Top(Em3dSolid s) => s.Primitive is Em3dExtrudedPolygon p ? p.ZTop : double.NaN;


    /// <param name="widthM">The port's width across the gap when its entry states one, centred on the width the pads share;
    /// null: that shared width.</param>
    private static Draft? Construct(string name, string rd, (Pad Pad, Em3dSolid Solid) pos, (Pad Pad, Em3dSolid Solid) neg, double dbu,
                                    out string? why, double? widthM = null)
    {
        why = null;
        string label = name == rd ? rd : $"{rd} (pad {pos.Pad.Pin})";
        if (pos.Solid.Name == neg.Solid.Name)
        {
            why = $"{label}: pads {pos.Pad.Pin} and {neg.Pad.Pin} land on one conductor, '{pos.Solid.Name}', so the part is shorted by the copper " +
                  "and a port across it measures nothing.";
            return null;
        }
        if (Facing(pos.Pad, neg.Pad) is not { } face)
        {
            why = EmPartPads.NotFacing(label, pos.Pad, neg.Pad);
            return null;
        }
        var pp = (Em3dExtrudedPolygon)pos.Solid.Primitive;
        var np = (Em3dExtrudedPolygon)neg.Solid.Primitive;
        if (Math.Abs(pp.ZBottom - np.ZBottom) > dbu || Math.Abs(pp.ZTop - np.ZTop) > dbu)
        {
            why = $"{label}: its pads land on copper at two heights ('{pos.Solid.Name}' and '{neg.Solid.Name}'); a gap port lies between two " +
                  "pads on one copper layer.";
            return null;
        }
        bool ax = face.AlongX;
        // The low pad is the one at the smaller coordinate along the gap.
        bool posLow = ax ? pos.Pad.Cx < neg.Pad.Cx : pos.Pad.Cy < neg.Pad.Cy;
        var (low, high) = posLow ? (pos, neg) : (neg, pos);
        double lowEdge = ax ? low.Pad.X1 : low.Pad.Y1, highEdge = ax ? high.Pad.X0 : high.Pad.Y0;
        var (fw0, fw1) = widthM is { } wm ? ((face.W0 + face.W1) / 2 - wm / 2, (face.W0 + face.W1) / 2 + wm / 2) : (face.W0, face.W1);
        double w0 = Math.Ceiling(fw0 / dbu - 1e-6) * dbu, w1 = Math.Floor(fw1 / dbu + 1e-6) * dbu;
        if (w1 - w0 < 2 * dbu)
        {
            why = $"{label}: pads {pos.Pad.Pin} and {neg.Pad.Pin} share no width across the gap.";
            return null;
        }
        // The middle of the gap is where the two conductors' copper leaves off on the centre line, not the middle between the
        // pads: a ground pour often fills most of the space between a part's pads, up to its clearance round the other pad, and
        // the pads' own middle then lies in the pour. The pads' middle stays when the centre line does not show both.
        double mid = (lowEdge + highEdge) / 2;
        {
            double vc = (w0 + w1) / 2, lc = ax ? low.Pad.Cx : low.Pad.Cy, hc = ax ? high.Pad.Cx : high.Pad.Cy;
            var lowRun = Intervals((Em3dExtrudedPolygon)low.Solid.Primitive, vc, ax).Where(t => t.A <= lc && lc <= t.B).ToList();
            var highRun = Intervals((Em3dExtrudedPolygon)high.Solid.Primitive, vc, ax).Where(t => t.A <= hc && hc <= t.B).ToList();
            if (lowRun.Count == 1 && highRun.Count == 1 && highRun[0].A > lowRun[0].B) mid = (lowRun[0].B + highRun[0].A) / 2;
        }
        // Scan across the width, inset a little from the ends so a corner is not read as a face.
        double inset = Math.Min(2 * dbu, (w1 - w0) / 4);
        int n = Math.Clamp((int)Math.Ceiling((w1 - w0 - 2 * inset) / ScanStepM) + 1, 5, 4001);
        // Every 10 µm, and through every vertex of either conductor that faces the gap: the tip of a cap's arc lies on a
        // vertex, and a scan line passing beside it would put the sheet's edge inside the copper it missed.
        var lines = Enumerable.Range(0, n).Select(i => w0 + inset + (w1 - w0 - 2 * inset) * i / (n - 1)).ToList();
        void Vertices((Pad Pad, Em3dSolid Solid) side, bool isLow)
        {
            var ep = (Em3dExtrudedPolygon)side.Solid.Primitive;
            double far = isLow ? (ax ? side.Pad.X0 : side.Pad.Y0) : (ax ? side.Pad.X1 : side.Pad.Y1);
            foreach (var q in ep.Outline.Concat(ep.Holes.SelectMany(h => h)))
            {
                double u = ax ? q.X : q.Y, v = ax ? q.Y : q.X;
                if (v > w0 + inset && v < w1 - inset && (isLow ? u >= far && u <= mid : u <= far && u >= mid)) lines.Add(v);
            }
        }
        Vertices(low, true);
        Vertices(high, false);
        var vs = lines.Distinct().Order().ToArray();
        n = vs.Length;
        var lowIntervals = vs.Select(v => Intervals((Em3dExtrudedPolygon)low.Solid.Primitive, v, ax)).ToArray();
        var highIntervals = vs.Select(v => Intervals((Em3dExtrudedPolygon)high.Solid.Primitive, v, ax)).ToArray();
        var lowFace = new double[n];
        var highFace = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (lowIntervals[i].Any(t => t.A < mid && t.B > mid) || highIntervals[i].Any(t => t.A < mid && t.B > mid))
            {
                why = $"{label}: copper of '{(lowIntervals[i].Any(t => t.A < mid && t.B > mid) ? low.Solid.Name : high.Solid.Name)}' runs across the " +
                      $"middle of the gap at {Axis(!ax)} = {Mm(vs[i])} mm, so the two pads have no gap between them there.";
                return null;
            }
            var lo = lowIntervals[i].Where(t => t.B <= mid).ToList();
            var hi = highIntervals[i].Where(t => t.A >= mid).ToList();
            if (lo.Count == 0 || hi.Count == 0)
            {
                why = $"{label}: across the gap at {Axis(!ax)} = {Mm(vs[i])} mm no copper of '{(lo.Count == 0 ? low.Solid.Name : high.Solid.Name)}' " +
                      "faces it.";
                return null;
            }
            lowFace[i] = lo.Max(t => t.B);
            highFace[i] = hi.Min(t => t.A);
        }
        var landings = new List<C3dLanding>();
        double? lf = End(low.Solid, lowIntervals, lowFace, -1, low.Pad, out string? lowWhy);
        double? hf = End(high.Solid, highIntervals, highFace, +1, high.Pad, out string? highWhy);
        if (lf is null || hf is null)
        {
            why = $"{label}: {lowWhy ?? highWhy}";
            return null;
        }
        if (hf.Value - lf.Value < 2 * dbu)
        {
            why = $"{label}: the copper facing its gap leaves {Um(hf.Value - lf.Value)} µm between '{low.Solid.Name}' and '{high.Solid.Name}'.";
            return null;
        }
        double zMid = Math.Round((pp.ZBottom + pp.ZTop) / 2 / dbu, MidpointRounding.AwayFromZero) * dbu;
        var (x0, y0, x1, y1) = ax ? (lf.Value, w0, hf.Value, w1) : (w0, lf.Value, w1, hf.Value);
        return new Draft(name, rd, pos.Pad.Pin, pos.Solid.Name, neg.Solid.Name, x0, y0, x1, y1, zMid, hf.Value - lf.Value, w1 - w0, ax, landings);

        // One end: its face on the DBU grid, and a landing where the copper is not already that one straight face.
        // side −1: the low conductor (its face is its largest coordinate); +1: the high one.
        double? End(Em3dSolid solid, List<(double A, double B)>[] intervals, double[] faces, int side, Pad pad, out string? endWhy)
        {
            endWhy = null;
            double closest = side < 0 ? faces.Max() : faces.Min();
            double farthest = side < 0 ? faces.Min() : faces.Max();
            double grid = side < 0 ? Math.Ceiling(closest / dbu - 1e-6) * dbu : Math.Floor(closest / dbu + 1e-6) * dbu;
            bool straight = Math.Abs(farthest - closest) <= 1e-3 * dbu && Math.Abs(grid - closest) <= 1e-3 * dbu;
            if (straight) return closest;
            // Reach back until the back edge lies in solid copper across the whole width: at every scan line, inside an interval
            // of this conductor that runs on to its face there.
            double padDepth = ax ? pad.X1 - pad.X0 : pad.Y1 - pad.Y0;
            for (double back = LandingStepM; back <= padDepth + 1e-12; back += LandingStepM)
            {
                double edge = side < 0 ? farthest - back : farthest + back;
                bool solidAcross = true;
                for (int i = 0; i < n && solidAcross; i++)
                    solidAcross = intervals[i].Any(t => t.A <= edge && edge <= t.B &&
                                                        (side < 0 ? Math.Abs(t.B - faces[i]) < 1e-12 : Math.Abs(t.A - faces[i]) < 1e-12));
                if (!solidAcross) continue;
                double e0 = Math.Min(edge, grid), e1 = Math.Max(edge, grid);
                landings.Add(new C3dLanding(name, solid.Name, ax ? (e0, w0, e1, w1) : (w0, e0, w1, e1), ax)
                {
                    Face = ax ? (new Point2(grid, w0), new Point2(grid, w1)) : (new Point2(w0, grid), new Point2(w1, grid)),
                });
                return grid;
            }
            endWhy = $"the copper of '{solid.Name}' facing its gap is not one straight face, and no landing rectangle reaching back from it " +
                     $"within pad {pad.Pin}'s own depth ({Um(padDepth)} µm) lies in solid copper across the port's whole width.";
            return null;
        }
    }

    /// <summary>The stretches of a line across the gap's width that lie in a conductor's copper: the line at <paramref name="v"/>
    /// across the axis (y when <paramref name="alongX"/>), as (from, to) along the gap's axis.</summary>
    internal static List<(double A, double B)> Intervals(Em3dExtrudedPolygon p, double v, bool alongX)
    {
        var xs = new List<double>();
        void Ring(IReadOnlyList<Point2> ring)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double au = alongX ? ring[j].X : ring[j].Y, av = alongX ? ring[j].Y : ring[j].X;
                double bu = alongX ? ring[i].X : ring[i].Y, bv = alongX ? ring[i].Y : ring[i].X;
                if ((av > v) != (bv > v)) xs.Add(au + (bu - au) * (v - av) / (bv - av));
            }
        }
        Ring(p.Outline);
        foreach (var h in p.Holes) Ring(h);
        xs.Sort();
        // A line through a vertex the outline only touches (both its edges on one side) crosses there twice at one point. Those
        // two hold no copper between them: as an interval of their own they were read as a face 0.6 µm short of the real one,
        // and inside copper they split one interval in two. Cancel them in pairs, which keeps every other crossing's parity.
        for (int k = xs.Count - 2; k >= 0; k--)
            if (k + 1 < xs.Count && xs[k + 1] - xs[k] <= 1e-12) { xs.RemoveRange(k, 2); k--; }
        var list = new List<(double, double)>();
        for (int k = 0; k + 1 < xs.Count; k += 2) list.Add((xs[k], xs[k + 1]));
        return list;
    }

    private static bool Holds(Em3dExtrudedPolygon p, double x, double y, double tol)
        => C3dThermal.Inside(p, new Point3(x, y, (p.ZBottom + p.ZTop) / 2), tol);

    // ── R-emb1-6: a connector's shell ───────────────────────────────────────────────────────────

    private static List<C3dShellBridge> ShellBridges(string rd, List<(Pad Pad, Em3dSolid Solid)> grounds, C3dElaboration e, int already)
    {
        var list = new List<C3dShellBridge>();
        var flat = grounds.Where(g => g.Solid.Primitive is Em3dExtrudedPolygon).ToList();
        foreach (var top in flat)
            foreach (var bottom in flat)
            {
                var tp = (Em3dExtrudedPolygon)top.Solid.Primitive;
                var bp = (Em3dExtrudedPolygon)bottom.Solid.Primitive;
                if (!(bp.ZTop < tp.ZBottom)) continue;          // bottom lies below top
                double x0 = Math.Max(top.Pad.X0, bottom.Pad.X0), x1 = Math.Min(top.Pad.X1, bottom.Pad.X1);
                double y0 = Math.Max(top.Pad.Y0, bottom.Pad.Y0), y1 = Math.Min(top.Pad.Y1, bottom.Pad.Y1);
                if (!(x1 > x0 && y1 > y0)) continue;
                // Kept off the pads' own faces, where a port's edge lands: a block flush with one would touch that edge too.
                double ix = Math.Min(ShellInsetM, (x1 - x0) / 4), iy = Math.Min(ShellInsetM, (y1 - y0) / 4);
                (x0, x1, y0, y1) = (x0 + ix, x1 - ix, y0 + iy, y1 - iy);
                if (list.Any(b => b.Top == top.Solid.Name && b.Bottom == bottom.Solid.Name && b.Box == (x0, y0, x1, y1))) continue;
                string prefix = top.Solid.Name.Contains('/') ? top.Solid.Name[..(top.Solid.Name.IndexOf('/') + 1)] : "";
                list.Add(new C3dShellBridge($"{prefix}{rd} shell {already + list.Count + 1}", top.Solid.Name, bottom.Solid.Name, (x0, y0, x1, y1)));
            }
        return list;
    }

    // ── Applying landings and bridges ───────────────────────────────────────────────────────────

    /// <summary>The elaboration with every landing unioned into its conductor and every shell bridge added. A landing that would
    /// touch another conductor (other than one already part of its own body) is a refusal: it would bridge two nets.</summary>
    /// <remarks>The third item names the ports whose landings the refusal is about, so the build can leave them out.</remarks>
    private static (C3dElaboration Elaboration, string? Refusal, IReadOnlySet<string> Ports) Apply(C3dElaboration e,
        IReadOnlyList<C3dLanding> landings, IReadOnlyList<C3dShellBridge> bridges, double dbu)
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        if (landings.Count == 0 && bridges.Count == 0) return (e, null, none);
        var solids = e.Solids.ToList();
        var nets = e.ObjectNets.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var group in landings.GroupBy(l => l.Conductor))
        {
            int at = solids.FindIndex(s => s.Name == group.Key);
            var own = (Em3dExtrudedPolygon)solids[at].Primitive;
            // No other conductor within a DBU of a landing, unless it is already part of this conductor's body (a via in the pad).
            foreach (var l in group)
                foreach (var other in solids.Where(s => s.Role == Em3dRole.Conductor && s.Name != group.Key && !e.NotModelled.Contains(s.Name)))
                {
                    if (!Touches(other.Primitive, l.Box, own.ZBottom, own.ZTop, dbu)) continue;
                    if (other.Primitive is Em3dCylinder c && Holds(own, c.AxisStart.X, c.AxisStart.Y, 0)) continue;
                    bool sameNet = nets.TryGetValue(group.Key, out var a) && nets.TryGetValue(other.Name, out var b) && a.Overlaps(b);
                    if (sameNet && other.Primitive is not Em3dExtrudedPolygon) continue;
                    return (e, $"{l.Port}: a landing rectangle {Um(l.DepthM)} × {Um(l.WidthM)} µm on '{group.Key}' at ({Mm(l.Box.X0)}, {Mm(l.Box.Y0)}) mm " +
                               $"would touch '{other.Name}', bridging two conductors{NetsOf(nets, group.Key, other.Name)}. The part's gap is too " +
                               "close to other copper for a landing; move the copper, or draw this port by hand.", new HashSet<string>([l.Port], StringComparer.Ordinal));
                }
            var paths = new Paths64 { Path(own.Outline) };
            var holes = new Paths64(own.Holes.Select(Path));
            var region = holes.Count > 0 ? Clipper.Difference(Positive(paths), Positive(holes), FillRule.NonZero) : Positive(paths);
            var rects = new Paths64(group.Select(l => Path([new(l.Box.X0, l.Box.Y0), new(l.Box.X1, l.Box.Y0), new(l.Box.X1, l.Box.Y1), new(l.Box.X0, l.Box.Y1)])));
            var tree = new PolyTree64();
            var clipper = new Clipper64();
            clipper.AddSubject(region);
            clipper.AddClip(Positive(rects));
            clipper.Execute(ClipType.Union, FillRule.NonZero, tree);
            if (tree.Count != 1)
                return (e, $"{string.Join(", ", group.Select(l => l.Port).Distinct())}: the landing rectangles on '{group.Key}' do not join its copper into one piece ({tree.Count} pieces).", group.Select(l => l.Port).ToHashSet(StringComparer.Ordinal));
            var outer = tree[0];
            var newHoles = new List<IReadOnlyList<Point2>>();
            for (int k = 0; k < outer.Count; k++)
            {
                if (outer[k].Count > 0)
                    return (e, $"{string.Join(", ", group.Select(l => l.Port).Distinct())}: the landing rectangles on '{group.Key}' leave copper islands inside its holes; it cannot be lowered as one outline.", group.Select(l => l.Port).ToHashSet(StringComparer.Ordinal));
                newHoles.Add(Ring(outer[k].Polygon!));
            }
            var joined = own with { Outline = Ring(outer.Polygon!), Holes = newHoles };
            // The face is picked on scan lines inset from the width's ends and rounded to the grid, so a slanted copper edge can cross it
            // a fraction of a DBU from the sheet's corner. Left there, that corner stands a sliver out of the copper's side, far below
            // anything a mesher can resolve: a surface inside the metal, which no element has for a face. So the copper's side takes
            // the sheet's two corners exactly, as a turned port's does. A corner it does not come near keeps the outline as it is.
            foreach (var l in group)
                joined = C3dTurnedPorts.Seam(joined, l.Face.A, l.Face.B, 2 * dbu + e.OutlineMoveM) ?? joined;
            solids[at] = solids[at] with { Primitive = joined };
        }
        int order = solids.Count == 0 ? 0 : solids.Max(s => s.Order);
        foreach (var b in bridges)
        {
            var top = solids.First(s => s.Name == b.Top);
            var tp = (Em3dExtrudedPolygon)top.Primitive;
            var bp = (Em3dExtrudedPolygon)solids.First(s => s.Name == b.Bottom).Primitive;
            var (x0, y0, x1, y1) = b.Box;
            solids.Add(new Em3dSolid(b.Name, top.Material, Em3dRole.Conductor,
                new Em3dExtrudedPolygon([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], [], (bp.ZBottom + bp.ZTop) / 2, (tp.ZBottom + tp.ZTop) / 2),
                ++order));
            var joined = new HashSet<string>(StringComparer.Ordinal);
            if (nets.TryGetValue(b.Top, out var tn)) joined.UnionWith(tn);
            if (nets.TryGetValue(b.Bottom, out var bn)) joined.UnionWith(bn);
            nets[b.Name] = joined;
        }
        var origins = e.Origins.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var b in bridges)
            if (origins.TryGetValue(b.Top, out var o)) origins[b.Name] = o;
        var ground = e.GroundBandObjects.Concat(bridges.Where(b => e.GroundBandObjects.Contains(b.Top) || e.GroundBandObjects.Contains(b.Bottom))
                                                       .Select(b => b.Name)).ToList();
        return (e with { Solids = solids, ObjectNets = nets, Origins = origins, GroundBandObjects = ground }, null, none);

        static Paths64 Positive(Paths64 ps) => new(ps.Select(p => Clipper.IsPositive(p) ? p : Reversed(p)));
        static Path64 Reversed(Path64 p) { var r = new Path64(p); r.Reverse(); return r; }
        static Path64 Path(IReadOnlyList<Point2> ring)
            => new(ring.Select(q => new Point64((long)Math.Round(q.X * Scale), (long)Math.Round(q.Y * Scale))));
        static IReadOnlyList<Point2> Ring(Path64 p) => [.. p.Select(q => new Point2(q.X / Scale, q.Y / Scale))];
    }

    private static string NetsOf(Dictionary<string, HashSet<string>> nets, string a, string b)
    {
        string N(string o) => nets.TryGetValue(o, out var s) && s.Count > 0 ? string.Join("/", s.OrderBy(x => x, StringComparer.Ordinal)) : "no net";
        return $" ({N(a)} and {N(b)})";
    }

    /// <summary>Whether a primitive comes within <paramref name="tol"/> of a plan box over a z range.</summary>
    private static bool Touches(Em3dPrimitive p, (double X0, double Y0, double X1, double Y1) box, double z0, double z1, double tol)
    {
        var (bx0, by0, bz0, bx1, by1, bz1) = Em3dProblem.Bounds(p);
        if (bx0 > box.X1 + tol || bx1 < box.X0 - tol || by0 > box.Y1 + tol || by1 < box.Y0 - tol || bz0 > z1 + tol || bz1 < z0 - tol) return false;
        switch (p)
        {
            case Em3dExtrudedPolygon ep:
            {
                var rect = new Path64
                {
                    new((long)((box.X0 - tol) * Scale), (long)((box.Y0 - tol) * Scale)), new((long)((box.X1 + tol) * Scale), (long)((box.Y0 - tol) * Scale)),
                    new((long)((box.X1 + tol) * Scale), (long)((box.Y1 + tol) * Scale)), new((long)((box.X0 - tol) * Scale), (long)((box.Y1 + tol) * Scale)),
                };
                var outline = new Path64(ep.Outline.Select(q => new Point64((long)Math.Round(q.X * Scale), (long)Math.Round(q.Y * Scale))));
                var holes = new Paths64(ep.Holes.Select(h => new Path64(h.Select(q => new Point64((long)Math.Round(q.X * Scale), (long)Math.Round(q.Y * Scale))))));
                var region = holes.Count > 0 ? Clipper.Difference(new Paths64 { outline }, holes, FillRule.EvenOdd) : new Paths64 { outline };
                return Clipper.Intersect(region, new Paths64 { rect }, FillRule.EvenOdd).Any(r => Math.Abs(Clipper.Area(r)) > 0);
            }
            case Em3dCylinder c:
            {
                double cx = Math.Clamp(c.AxisStart.X, box.X0, box.X1), cy = Math.Clamp(c.AxisStart.Y, box.Y0, box.Y1);
                return Math.Sqrt((cx - c.AxisStart.X) * (cx - c.AxisStart.X) + (cy - c.AxisStart.Y) * (cy - c.AxisStart.Y)) <= c.Radius + tol;
            }
            default:
                return true;
        }
    }

    // ── Verification (R-emb1-4) ─────────────────────────────────────────────────────────────────

    /// <summary>The checks one built port must pass, against the elaboration with every landing applied. Null when it passes;
    /// otherwise the sentence naming the refdes, the edge and the conductor found.</summary>
    private static string? Verify(Draft d, C3dElaboration e, C3dPortContext ctx, double dbu)
    {
        string who = d.Name;
        // The two conductors are different nets.
        if (e.ObjectNets.TryGetValue(d.Positive, out var pn) && e.ObjectNets.TryGetValue(d.Negative, out var nn) && pn.Overlaps(nn))
            return $"{who}: '{d.Positive}' and '{d.Negative}' are both on net {string.Join("/", pn.Intersect(nn))}, so the port is across one net.";

        // Each end edge touches exactly its own conductor within 1 DBU; each side edge touches none.
        double inset = 2 * dbu;
        Point3 P(double x, double y) => new(x, y, d.ZMid);
        (string Name, Point3 A, Point3 B, string? Own)[] edges = d.AlongX
            ? [("left", P(d.X0, d.Y0 + inset), P(d.X0, d.Y1 - inset), LowName()), ("right", P(d.X1, d.Y0 + inset), P(d.X1, d.Y1 - inset), HighName()),
               ("bottom", P(d.X0 + inset, d.Y0), P(d.X1 - inset, d.Y0), null), ("top", P(d.X0 + inset, d.Y1), P(d.X1 - inset, d.Y1), null)]
            : [("bottom", P(d.X0 + inset, d.Y0), P(d.X1 - inset, d.Y0), LowName()), ("top", P(d.X0 + inset, d.Y1), P(d.X1 - inset, d.Y1), HighName()),
               ("left", P(d.X0, d.Y0 + inset), P(d.X0, d.Y1 - inset), null), ("right", P(d.X1, d.Y0 + inset), P(d.X1, d.Y1 - inset), null)];
        foreach (var (edge, a, b, own) in edges)
        {
            var touching = ctx.Conductors.Where(c => ctx.Distance(c, a, b) <= dbu).Select(c => c.Name).ToList();
            if (own is not null && (touching.Count != 1 || touching[0] != own))
                return $"{who}: its {edge} edge should touch '{own}' alone within 1 DBU, and touches " +
                       (touching.Count == 0 ? "nothing" : string.Join(", ", touching.Select(t => $"'{t}'"))) + ".";
            if (own is null && touching.Count > 0)
                return $"{who}: its {edge} edge, a side of the gap, touches {string.Join(", ", touching.Select(t => $"'{t}'"))}.";
        }

        // No copper inside the sheet: sampled on a 2 µm grid, a DBU in from its edges.
        double u0 = d.X0 + dbu, u1 = d.X1 - dbu, v0 = d.Y0 + dbu, v1 = d.Y1 - dbu;
        var near = e.Solids.Where(s => s.Role == Em3dRole.Conductor && !e.NotModelled.Contains(s.Name))
                           .Where(s => { var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(s.Primitive);
                                         return x0 <= u1 && x1 >= u0 && y0 <= v1 && y1 >= v0 && z0 <= d.ZMid && z1 >= d.ZMid; }).ToList();
        int nu = Math.Max(2, (int)Math.Ceiling((u1 - u0) / SampleStepM) + 1), nv = Math.Max(2, (int)Math.Ceiling((v1 - v0) / SampleStepM) + 1);
        double[] vs = [.. Enumerable.Range(0, nv).Select(j => v0 + (v1 - v0) * j / (nv - 1))];
        // An extruded polygon is tested through a SheetSampler (C3dThermal.Inside's own rule, its rows' crossings found once);
        // anything else through Inside itself. The same samples in the same order, so the same first hit.
        var samplers = near.Select(s => s.Primitive is Em3dExtrudedPolygon ep ? new SheetSampler(ep, vs, u0, u1, -0.5 * dbu) : null).ToList();
        for (int i = 0; i < nu && near.Count > 0; i++)
            for (int j = 0; j < nv; j++)
            {
                var q = new Point3(u0 + (u1 - u0) * i / (nu - 1), vs[j], d.ZMid);
                for (int k = 0; k < near.Count; k++)
                    if (samplers[k] is { } sp ? sp.Inside(q, j) : C3dThermal.Inside(near[k].Primitive, q, -0.5 * dbu))
                        return $"{who}: copper of '{near[k].Name}' lies inside the port's sheet at ({Mm(q.X)}, {Mm(q.Y)}) mm.";
            }
        return null;

        string LowName() => LowOf(d);
        string HighName() => LowOf(d) == d.Positive ? d.Negative : d.Positive;
        string LowOf(Draft x)
        {
            // The low end is the conductor at the sheet's smaller coordinate: whichever holds a point just beyond it.
            double probeX = x.AlongX ? x.X0 - dbu : (x.X0 + x.X1) / 2, probeY = x.AlongX ? (x.Y0 + x.Y1) / 2 : x.Y0 - dbu;
            var pos = e.Solids.First(s => s.Name == x.Positive);
            return C3dThermal.Inside(pos.Primitive, new Point3(probeX, probeY, x.ZMid), 0.5 * dbu) ? x.Positive : x.Negative;
        }
    }

    /// <summary>
    /// <see cref="C3dThermal.Inside(Em3dPrimitive, Point3, double)"/>'s rule for an extruded polygon, for the samples of one
    /// sheet: rows at <c>vs</c>, columns within [<c>u0</c>, <c>u1</c>]. A board's ground pour is one polygon of a thousand
    /// edges or more, and asking Inside of every sample measured each sample's distance to every edge — about 100 s for one
    /// board's fourteen ports. Here each row's ray crossings are found once (the same expression, so the same parity), and only
    /// the edges that can lie within the tolerance of the sheet are measured (an edge farther away never decides a sample).
    /// </summary>
    internal sealed class SheetSampler
    {
        private readonly Em3dExtrudedPolygon _p;
        private readonly double _tol;
        private readonly Ring _outline;
        private readonly Ring[] _holes;

        public SheetSampler(Em3dExtrudedPolygon p, double[] vs, double u0, double u1, double tol)
        {
            _p = p;
            _tol = tol;
            double vMin = vs.Min(), vMax = vs.Max(), reach = 2 * Math.Abs(tol);
            _outline = new Ring(p.Outline, vs, u0 - reach, u1 + reach, vMin - reach, vMax + reach, reach);
            // A hole whose box does not reach the sheet can neither hold a sample nor come within reach of one: left out.
            _holes = [.. p.Holes.Where(h => h.Count > 0 && h.Max(q => q.X) >= u0 - reach && h.Min(q => q.X) <= u1 + reach
                                            && h.Max(q => q.Y) >= vMin - reach && h.Min(q => q.Y) <= vMax + reach)
                                .Select(h => new Ring(h, vs, u0 - reach, u1 + reach, vMin - reach, vMax + reach, reach))];
        }

        /// <summary>Inside(p, q, tol), for a sample on row <paramref name="row"/>.</summary>
        public bool Inside(Point3 q, int row)
        {
            if (q.Z < _p.ZBottom - _tol || q.Z > _p.ZTop + _tol) return false;
            if (!_outline.Contains(q.X, q.Y, row, _tol)) return false;
            foreach (var h in _holes) if (h.Contains(q.X, q.Y, row, -_tol)) return false;
            return true;
        }

        private sealed class Ring
        {
            /// <summary>Per row, the edges that come within reach of it, with their bounds: an edge farther than its bounds'
            /// reach from a sample never decides it, so a sample measures only these (a pour's thousand edges become a few).</summary>
            private readonly (Point2 A, Point2 B, double X0, double X1)[][] _rowEdges;
            private readonly double _reach;
            private readonly double[][] _crossings;

            public Ring(IReadOnlyList<Point2> ring, double[] vs, double x0, double x1, double y0, double y1, double reach)
            {
                _reach = reach;
                var near = new List<(Point2 A, Point2 B, double X0, double X1, double Y0, double Y1)>();
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (Math.Max(a.X, b.X) >= x0 && Math.Min(a.X, b.X) <= x1 && Math.Max(a.Y, b.Y) >= y0 && Math.Min(a.Y, b.Y) <= y1)
                        near.Add((a, b, Math.Min(a.X, b.X), Math.Max(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                }
                _rowEdges = new (Point2, Point2, double, double)[vs.Length][];
                _crossings = new double[vs.Length][];
                var xs = new List<double>();
                for (int r = 0; r < vs.Length; r++)
                {
                    double y = vs[r];
                    _rowEdges[r] = [.. near.Where(e => y >= e.Y0 - reach && y <= e.Y1 + reach).Select(e => (e.A, e.B, e.X0, e.X1))];
                    xs.Clear();
                    for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    {
                        var a = ring[i];
                        var b = ring[j];
                        if ((a.Y > y) != (b.Y > y)) xs.Add((b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X);
                    }
                    xs.Sort();
                    _crossings[r] = [.. xs];
                }
            }

            /// <summary>C3dThermal's InPolygon: within |tol| of an edge is <c>tol ≥ 0</c>; otherwise the parity of crossings right of x.
            /// <paramref name="tol"/>'s size is never more than the reach the ring was built with.</summary>
            public bool Contains(double x, double y, int row, double tol)
            {
                foreach (var (a, b, ex0, ex1) in _rowEdges[row])
                    if (x >= ex0 - _reach && x <= ex1 + _reach && SegmentDistance(a, b, x, y) <= Math.Abs(tol)) return tol >= 0;
                var c = _crossings[row];
                int lo = 0, hi = c.Length;                       // the first crossing strictly greater than x
                while (lo < hi) { int m = (lo + hi) / 2; if (c[m] > x) hi = m; else lo = m + 1; }
                return ((c.Length - lo) & 1) == 1;
            }

            private static double SegmentDistance(Point2 a, Point2 b, double x, double y)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double l2 = dx * dx + dy * dy;
                double t = l2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1);
                double px = a.X + t * dx - x, py = a.Y + t * dy - y;
                return Math.Sqrt(px * px + py * py);
            }
        }
    }

    // ── Words ──────────────────────────────────────────────────────────────────────────────────

    private static string Axis(bool x) => x ? "x" : "y";
    public static string Mm(double m) => (m * 1e3).ToString("0.####", CultureInfo.InvariantCulture);
    public static string Um(double m) => (m * 1e6).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>R-emb1-8 — one port's line for <c>explain --component-ports</c> and the setup panel's list.</summary>
    public static string Describe(C3dComponentPort p)
    {
        if (p.Port is not { } port) return $"{p.Refdes}: refused — {p.Refusal}";
        string landings = p.Landings.Count == 0
            ? "no landing"
            : string.Join(", ", p.Landings.Select(l => $"landing {Um(l.DepthM)} × {Um(l.WidthM)} µm on '{l.Conductor}'"));
        return $"{port.Name}: port {port.Number}, pad {p.Pin} on '{port.Positive}' to pad {p.ReferencePin} on '{port.Negative}', " +
               $"gap {Um(p.GapM)} µm along {Axis(p.AlongX)}, {Um(p.WidthM)} µm wide; {landings}{(p.Connector ? "; connector" : "")}; {p.Verdict}.";
    }
}

/// <summary>Reference designators in the order a person reads them: C2 before C10.</summary>
internal sealed class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                int c = long.Parse(a[si..i], CultureInfo.InvariantCulture).CompareTo(long.Parse(b[sj..j], CultureInfo.InvariantCulture));
                if (c != 0) return c;
                continue;
            }
            int d = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
            if (d != 0) return d;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
