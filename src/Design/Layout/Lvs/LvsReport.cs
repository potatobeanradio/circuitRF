// Typed divergences become a report a designer can act on — brief-lvs-8-findings.md,
// docs/design/lvs.md §6.5.
//
//   comparison + extraction notes + the artwork's geometry ──► LvsFinding[]
//
// ── WHY THE MARKERS ARE ATTACHED HERE AND NOT WHERE THE DIAGNOSTICS ARE MADE ──────────────────
//
// Briefs 3, 4, 6 and 7 each produce diagnostics, and not one of them may see the artwork's
// coordinates: the extraction must not know what the comparison will conclude, and the comparison
// must not know where anything is drawn (R-lvs3-1a — two identical netlists have to compare
// identically whether or not one of them was drawn somewhere). So the producers state WHAT, and
// this file — the one place that holds the answer and the geometry at the same time — states
// WHERE.
//
// The cost is the table below: one entry per id saying which of a diagnostic's typed arguments
// names an object and on which side it lives. That table is the catalogue made executable, and
// `tests/Ui.Tests/Lvs/FindingsTests.cs` asserts both directions of it — an id nothing produces is
// dead, and a finding with an id the catalogue does not list breaks `--json` silently.
//
// ── ONE CAP, AND IT SAYS SO (R-lvs8-6a) ───────────────────────────────────────────────────────
//
// A pour accidentally joined to forty nets is 780 pairs. Every one of them is true and a report of
// 780 lines is a report nobody reads, so each id is capped and the cap says how many it left out.
// Capping SILENTLY would be worse than not capping: the report would look complete.

using System.Linq;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

internal static class LvsReport
{
    /// <summary>How many findings of one id are listed before the trailing count (R-lvs8-6a).</summary>
    public const int MaxPerId = 20;

    /// <summary>The smallest marker a pad gets where its land pattern states no width — 10 µm, so
    /// a ring exists to click on.</summary>
    private const int MinPadMicrons = 10;

    /// <summary>How many pin pairs one short is measured over before the search stops.</summary>
    private const int MaxShortProbes = 16;

    /// <summary>
    /// The whole report, in R-lvs8-1b's order.
    /// </summary>
    /// <param name="comparison">What brief 7 concluded.</param>
    /// <param name="schematic">The schematic netlist as compared.</param>
    /// <param name="layout">The layout netlist as compared.</param>
    /// <param name="geometry">Where the layout's objects are.</param>
    /// <param name="notes">Both extractions' notes and both reduction summaries, in the order the
    /// run assembled them.</param>
    /// <param name="counts">Both sides, before and after the collapse.</param>
    /// <param name="technologyName">Which process the layout was read against.</param>
    /// <param name="reduction">Whether the collapse ran.</param>
    public static IReadOnlyList<LvsFinding> Build(
        LvsComparison comparison,
        LvsNetlist schematic, LvsNetlist layout, LvsGeometry geometry,
        IReadOnlyList<Diagnostic> notes,
        LvsCounts counts, string? technologyName, ReductionMode reduction)
    {
        var found = new List<LvsFinding>();
        var context = new Context(comparison, schematic, layout, geometry);

        // Everything the extraction and the collapse had to say, given a place to look.
        foreach (var note in notes) found.Add(context.Locate(note));

        // Everything the comparison concluded, except the two it concluded structurally — those
        // are rebuilt below, with the route and the islands the pass could not know about.
        foreach (var finding in comparison.Findings)
        {
            if (finding.Id is "lvs.net.short" or "lvs.net.open") continue;
            found.Add(context.Locate(finding));
        }

        found.AddRange(Shorts(context));
        found.AddRange(Opens(context));
        found.AddRange(FloatingCopper(context));

        // R-lvs8-6b. Always, including on a run that found nothing — a run that deliberately
        // concluded "these match" and said nothing is indistinguishable from a broken command.
        found.Add(LvsMarker.RunLevel(LvsDiagnostics.RunSummary(
            counts.Describe(), technologyName is { Length: > 0 } t ? $"'{t}'" : "no technology",
            reduction)));

        return LvsMarker.Ordered(Cap(found));
    }

    // ── Shorts: the route (R-lvs8-4) ─────────────────────────────────────────────────────────

    private static IEnumerable<LvsFinding> Shorts(Context context)
    {
        foreach (var (layoutNet, a, b) in context.Comparison.Shorts)
        {
            var step = Narrowest(context, a, b);

            // R-lvs8-4b's own sentence: the NARROWEST thing on the route, named and located.
            string through = step is { } s
                ? s.Kind == JoinKind.Via
                    ? $" They are joined through a {context.Geometry.Format.Length(s.WidthDbu)} via at "
                      + $"{context.Geometry.Format.Point(s.X, s.Y)} on "
                      + $"{context.Geometry.NameOfLayer(s.Layer)}."
                    : $" They are joined through a {context.Geometry.Format.Length(s.WidthDbu)} "
                      + $"{(s.Kind is null ? "neck" : "touch")} of "
                      + $"{context.Geometry.NameOfLayer(s.Layer)} at "
                      + $"{context.Geometry.Format.Point(s.X, s.Y)}."
                : "";

            var diagnostic = LvsDiagnostics.NetShort(
                $"{context.SchematicNet(a)}, {context.SchematicNet(b)}", 2,
                context.LayoutNet(layoutNet),
                through, step?.WidthDbu ?? 0, step?.X ?? 0, step?.Y ?? 0);

            string[] objects = [context.SchematicNet(a), context.SchematicNet(b)];

            // R-lvs8-4c. The JOIN geometry, never the whole net — a marker covering a board-wide
            // pour is a marker that points at nothing.
            yield return step is { } located
                ? LvsMarker.Of(diagnostic, objects, located.Rings)
                : LvsMarker.Of(diagnostic, objects, Bbox.Empty);
        }
    }

    /// <summary>
    /// The narrowest metal joining the two nets, over every pin of one against every pin of the
    /// other — <b>not the first pair</b>.
    /// </summary>
    /// <remarks>
    /// <b>Two reasons the first pair is the wrong answer, and the six-fault board shows both.</b>
    /// A pin pair may not be joined at all: F5 leaves one ground pad on an island of its own, so
    /// the first ground pin the walk reaches has no route to the input and the whole short goes
    /// unlocated. And where several routes exist the narrowest is the one a designer has to look
    /// at, not whichever pin came first.
    ///
    /// <para>Bounded at <see cref="MaxShortProbes"/> pairs: on a pour joined to a hundred pads the
    /// answer stops improving long before the arithmetic does.</para>
    /// </remarks>
    private static LvsShortStep? Narrowest(Context context, int a, int b)
    {
        LvsShortStep? best = null;
        int probes = 0;

        foreach (var (pieceA, ax, ay) in context.PinsOn(a))
        {
            foreach (var (pieceB, bx, by) in context.PinsOn(b))
            {
                if (++probes > MaxShortProbes) return best;

                var step = LvsShortPath.Narrowest(context.Geometry, pieceA, ax, ay, pieceB, bx, by);
                if (step is { } s && (best is not { } prior || s.WidthDbu < prior.WidthDbu)) best = s;
            }
        }

        return best;
    }

    // ── Opens: the island structure (R-lvs8-5) ───────────────────────────────────────────────

    private static IEnumerable<LvsFinding> Opens(Context context)
    {
        foreach (var open in context.Comparison.Opens)
        {
            string pins = string.Join("; ", open.LayoutNets.Select(context.DescribeIsland));
            var diagnostic = LvsDiagnostics.NetOpen(
                context.SchematicNet(open.SchematicNet), open.LayoutNets.Count, pins);

            // R-lvs8-5a: a marker PER ISLAND, which is what makes the finding readable — three
            // rings on the canvas is the sentence, drawn.
            var rings = new List<long[]>();
            foreach (int island in open.LayoutNets)
                foreach (int piece in context.Geometry.PiecesOfNet(island))
                    rings.AddRange(context.Geometry.RingsOfPiece(piece));

            var objects = new List<string> { context.SchematicNet(open.SchematicNet) };
            objects.AddRange(open.LayoutNets.SelectMany(context.PinNamesOn));

            yield return LvsMarker.Of(diagnostic, objects, rings);
        }
    }

    // ── Copper nothing lands on (R-lvs8-5c) ──────────────────────────────────────────────────

    private static IEnumerable<LvsFinding> FloatingCopper(Context context)
    {
        var geometry = context.Geometry;
        var byNet = new SortedDictionary<int, List<int>>();
        for (int piece = 0; piece < geometry.Pieces.Count; piece++)
        {
            if (geometry.NetOfPiece(piece) >= 0) continue;
            int partition = geometry.Pieces.NetOfPiece(piece);
            (byNet.TryGetValue(partition, out var list) ? list : byNet[partition] = []).Add(piece);
        }

        foreach (var (partition, pieces) in byNet)
        {
            var box = LvsMarker.Union(pieces.Select(geometry.BoundsOfPiece));
            string named = geometry.Pieces.NameOfNet(partition) ?? "";
            var rings = pieces.SelectMany(geometry.RingsOfPiece).ToList();

            yield return LvsMarker.Of(
                LvsDiagnostics.NetFloatingCopper(
                    named, pieces.Count,
                    geometry.Format.Point((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2)),
                [named.Length > 0 ? named : $"copper at {geometry.Format.Point(box.MinX, box.MinY)}"],
                rings);
        }
    }

    // ── The cap (R-lvs8-6a) ──────────────────────────────────────────────────────────────────

    private static List<LvsFinding> Cap(List<LvsFinding> found)
    {
        var kept = new List<LvsFinding>();
        foreach (var group in LvsMarker.Ordered(found).GroupBy(f => f.Id, StringComparer.Ordinal))
        {
            var all = group.ToList();
            if (all.Count <= MaxPerId) { kept.AddRange(all); continue; }

            kept.AddRange(all.Take(MaxPerId));
            kept.Add(LvsMarker.RunLevel(LvsDiagnostics.Capped(group.Key, MaxPerId, all.Count)));
        }
        return kept;
    }

    // ── Which objects an id names, and where they are ────────────────────────────────────────

    private sealed class Context(
        LvsComparison comparison, LvsNetlist schematic, LvsNetlist layout, LvsGeometry geometry)
    {
        private readonly Dictionary<string, int> _layoutByPath =
            layout.Devices.Select((d, i) => (d.Path, i))
                  .ToDictionary(e => e.Path, e => e.i, StringComparer.Ordinal);

        private readonly Dictionary<string, int> _schematicByPath =
            schematic.Devices.Select((d, i) => (d.Path, i))
                     .ToDictionary(e => e.Path, e => e.i, StringComparer.Ordinal);

        private readonly Dictionary<int, int> _layoutOfSchematic =
            comparison.Devices.ToDictionary(p => p.Schematic, p => p.Layout);

        public LvsComparison Comparison => comparison;

        public LvsGeometry Geometry => geometry;

        private long PadFloor => Math.Max(1, (long)MinPadMicrons * geometry.Format.DbuPerMicron);

        /// <summary>
        /// One diagnostic, given the objects it is about and somewhere to look.
        /// </summary>
        /// <remarks>
        /// <b>Keyed on the id, which is the contract</b> (R-lvs8-2a). Reading a coordinate out of a
        /// rendered sentence would be the other way round, and it would break the first time
        /// somebody reworded a template — which the brief explicitly says is allowed.
        /// </remarks>
        public LvsFinding Locate(Diagnostic diagnostic)
        {
            switch (diagnostic.Id)
            {
                // Named by the LAYOUT's own path: the marker is that placement's pads.
                case "lvs.device.unmatched-layout":
                case "lvs.device.dangling-schematic-id":
                case "lvs.terminal.split-across-nets":
                    return OfLayoutDevice(diagnostic, Text(diagnostic, "path"));

                // The same, and the marker is narrowed to the pads that landed on nothing.
                case "lvs.pin.no-copper":
                {
                    int device = Layout(Text(diagnostic, "path"));
                    if (device < 0) return LvsMarker.RunLevel(diagnostic);
                    var bare = geometry.PadsOfDevice(device)
                                       .Select(i => geometry.Pads[i])
                                       .Where(p => p.Piece < 0)
                                       .Select(p => LvsMarker.Pad(p, PadFloor));
                    return LvsMarker.Of(diagnostic, Group(layout, device), LvsMarker.Union(bare));
                }

                // Named by the SCHEMATIC's own path. No marker, and none is missing: the artwork
                // does not have this part, which is the finding (see LvsFinding.HasMarker).
                case "lvs.device.unmatched-schematic":
                    return LvsMarker.Of(
                        diagnostic, Group(schematic, Schematic(Text(diagnostic, "path"))), Bbox.Empty);

                // One line naming both sides (R-lvs7-5d) — and every property finding, which names
                // both by construction (brief 10): the marker is the artwork's pads, because that is
                // the half a user can go and look at, and the objects are both devices' un-reduced
                // groups so a merged four-finger group names all four.
                case "lvs.device.type-mismatch":
                case "lvs.anchor.contradicted":
                case "lvs.property.mismatch":
                case "lvs.property.derived-differs":
                case "lvs.property.unread-differs":
                case "lvs.property.missing":
                case "lvs.property.multiplicity":
                case "lvs.reduce.multiplicity-unstated":
                {
                    var objects = new List<string>();
                    objects.AddRange(Group(schematic, Schematic(Text(diagnostic, "schematicPath"))));
                    int device = Layout(Text(diagnostic, "layoutPath"));
                    objects.AddRange(Group(layout, device));
                    return LvsMarker.Of(diagnostic, objects, BoundsOfDevice(device));
                }

                // R-lvs9-3b. The CONTACT itself, at the coordinate the extraction measured — not
                // the module's pads, which are the one part of it that is declared and correct.
                // The box is the contact's own extent where it has one, and a small square where
                // the graze is a hair's width, so there is always a ring to click on.
                case "lvs.hierarchy.undeclared-contact":
                {
                    int device = Layout(Text(diagnostic, "path"));
                    long half = Math.Max(Number(diagnostic, "widthDbu"), PadFloor) / 2;
                    long cx = Number(diagnostic, "x"), cy = Number(diagnostic, "y");
                    return LvsMarker.Of(
                        diagnostic,
                        device < 0 ? [Text(diagnostic, "path")] : Group(layout, device),
                        new Bbox(cx - half, cy - half, cx + half, cy + half));
                }

                // Every placement claiming the designator, because which one is meant is the
                // question the finding is about.
                case "lvs.device.duplicate-designator":
                {
                    string designator = Text(diagnostic, "designator");
                    var claimants = Enumerable.Range(0, layout.Devices.Count)
                        .Where(i => string.Equals(layout.Devices[i].Designator, designator,
                                                  StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    return LvsMarker.Of(
                        diagnostic,
                        [.. claimants.SelectMany(i => Group(layout, i))],
                        LvsMarker.Union(claimants.Select(BoundsOfDevice)));
                }

                // The one terminal, on the layout side — the pad a user has to go and look at.
                case "lvs.terminal.wrong-net":
                {
                    int s = Schematic(Text(diagnostic, "path"));
                    var objects = new List<string>(Group(schematic, s));
                    var box = Bbox.Empty;

                    if (s >= 0 && _layoutOfSchematic.TryGetValue(s, out int l))
                    {
                        objects.AddRange(Group(layout, l));
                        int port = diagnostic.Arguments.TryGetValue("port", out object? p) && p is int n ? n : 0;
                        int terminal = IndexOfPort(layout.Devices[l], port);
                        if (terminal >= 0)
                            box = LvsMarker.Union(
                                geometry.PadsOf(l, terminal).Select(pad => LvsMarker.Pad(pad, PadFloor)));
                    }

                    return LvsMarker.Of(diagnostic, objects, box);
                }

                // Everything else is about the RUN: the reduction summary, the ground reminder, a
                // cell TYPE rather than a placement, how the correspondence was arrived at, and
                // every refusal. None of them has one place on the board to point at.
                default:
                    return LvsMarker.RunLevel(diagnostic);
            }
        }

        /// <summary>A schematic net's name, as the designer spells it.</summary>
        public string SchematicNet(int index) => NameOf(schematic, index);

        /// <summary>A layout net's name.</summary>
        public string LayoutNet(int index) => NameOf(layout, index);

        /// <summary>One island, as the open's sentence names it: its pins, or that it has none.</summary>
        public string DescribeIsland(int net)
        {
            var pins = PinNamesOn(net);
            return pins.Count == 0
                ? $"{NameOf(layout, net)} (no pins)"
                : $"{NameOf(layout, net)} ({string.Join(", ", pins)})";
        }

        /// <summary>Every pin on a layout net, by the designer's own name for the part — un-reduced
        /// (R-lvs8-2b).</summary>
        public IReadOnlyList<string> PinNamesOn(int net)
        {
            if (net < 0 || net >= layout.Nets.Count) return [];
            var names = new List<string>();
            foreach (var (device, terminal) in layout.Nets[net].Pins)
            {
                var d = layout.Devices[device];
                string port = terminal < d.Terminals.Count
                    ? (d.Terminals[terminal].Name is { Length: > 0 } n ? n : d.Terminals[terminal].Port.ToString())
                    : "?";
                foreach (string member in d.Group) names.Add($"{member}.{port}");
            }
            return names;
        }

        /// <summary>
        /// Every place one schematic net reaches the copper: the pieces under the layout pads of
        /// the devices paired with the ones it touches.
        /// </summary>
        /// <remarks>
        /// <b>Through the CORRESPONDENCE, not through the layout net's own pin list.</b> The two
        /// schematic nets of a short share one layout net, so its pins say nothing about which of
        /// them a pad belongs to; the device pairing does.
        /// </remarks>
        public IReadOnlyList<(int Piece, long X, long Y)> PinsOn(int schematicNet)
        {
            var found = new List<(int, long, long)>();
            foreach (var pair in comparison.Devices.OrderBy(p => p.Schematic))
            {
                var ds = schematic.Devices[pair.Schematic];
                var dl = layout.Devices[pair.Layout];
                foreach (var ts in ds.Terminals.OrderBy(t => t.Port))
                {
                    if (ts.NetIndex != schematicNet) continue;
                    int terminal = IndexOfPort(dl, ts.Port);
                    if (terminal < 0) continue;
                    foreach (var pad in geometry.PadsOf(pair.Layout, terminal))
                        if (pad.Piece >= 0) found.Add((pad.Piece, pad.X, pad.Y));
                }
            }
            return found;
        }

        private LvsFinding OfLayoutDevice(Diagnostic diagnostic, string path)
        {
            int device = Layout(path);
            return device < 0
                ? LvsMarker.RunLevel(diagnostic)
                : LvsMarker.Of(diagnostic, Group(layout, device), BoundsOfDevice(device));
        }

        private Bbox BoundsOfDevice(int device)
            => device < 0
                ? Bbox.Empty
                : LvsMarker.Union(geometry.PadsOfDevice(device)
                                          .Select(i => LvsMarker.Pad(geometry.Pads[i], PadFloor)));

        private int Layout(string path) => _layoutByPath.GetValueOrDefault(path, -1);

        private int Schematic(string path) => _schematicByPath.GetValueOrDefault(path, -1);

        /// <summary>R-lvs8-2b: what a collapsed device stands for, always read and never asked
        /// about first — the branch that gets forgotten.</summary>
        private static IReadOnlyList<string> Group(LvsNetlist netlist, int device)
            => device < 0 || device >= netlist.Devices.Count ? [] : netlist.Devices[device].Group;

        private static int IndexOfPort(LvsDevice device, int port)
        {
            for (int i = 0; i < device.Terminals.Count; i++)
                if (device.Terminals[i].Port == port) return i;
            return -1;
        }

        private static string NameOf(LvsNetlist netlist, int index)
            => index >= 0 && index < netlist.Nets.Count && netlist.Nets[index].Label is { Length: > 0 } l
                ? l
                : $"net {index}";

        private static string Text(Diagnostic diagnostic, string argument)
            => diagnostic.Arguments.TryGetValue(argument, out object? value) ? value?.ToString() ?? "" : "";

        /// <summary>A DBU-valued typed argument, or zero — the coordinate half of <see cref="Text"/>.</summary>
        private static long Number(Diagnostic diagnostic, string argument)
            => diagnostic.Arguments.TryGetValue(argument, out object? value) && value is long n ? n : 0;
    }
}
