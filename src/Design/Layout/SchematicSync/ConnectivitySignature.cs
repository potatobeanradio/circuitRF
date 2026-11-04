// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-sdl-7 D9 — which pieces of metal touch, as the schematic sees it. A geometry optimisation changes lengths, widths
// and gaps; it must never change connectivity. The signature is the PLANNER's own findings on a planned layout: every
// joint that does not close (and every connection it could not join), and every pair of parts that share no net whose
// copper overlaps or touches — the latter measured by PlacementOverlaps on the copper partition DrcConnectivity makes.
//
// WHY NOT ONE PARTITION OF THE WHOLE LAYOUT. In a distributed circuit a line's own copper joins its two nets, so a whole
// microstrip circuit is a single piece of copper carrying every net on it: "do nets a and b touch" is always yes, and a
// ring with one joint open is still one piece. Connectivity is a property of the JOINTS and of parts that should not
// meet, and those are exactly what the planner reports, part by part, for every plan it makes (SDL-1 S1, SDL-3).
//
// A gap by design between two parts (a series gap's two sides) is not reported while they stay apart, so a variable
// driving it is free until they meet. A gap INSIDE one part — a coupled line's spacing — is the other half of the
// signature: for every placed part, which of its own pins its own copper joins (a coupled pair joins 1 with 2 and 3 with
// 4, never across the gap). A spacing driven to zero merges the two groups, which the start did not have. A joint the user opened by hand (SDL-4 D6) is
// reported as information, not a warning: it is part of the start signature and held like any other. Shared with SDL-8.

using CircuitRF.Design.Layout.Extraction;
using static CircuitRF.Design.Layout.SchematicSync.SchematicLayoutPlanner;

namespace CircuitRF.Design.Layout.SchematicSync;

/// <summary>
/// The connectivity of one planned layout (brief-sdl-7 D9).
/// </summary>
/// <param name="Open">Joints that do not close and connections not joined, each the parts it involves (<c>MT4+ML2</c>);
/// sorted. A joint the user opened by hand is here too, marked <c>opened:</c>.</param>
/// <param name="Touching">Pairs of parts that share no net and whose copper touches (<c>ML7|MT2</c>), sorted.</param>
/// <param name="Lines">The planner's warnings behind those facts, as it worded them — what a refusal or an infeasible
/// point quotes. Not part of the comparison: their numbers change with the geometry.</param>
/// <param name="Parts">Each placed part whose own copper does NOT join all its pins, with the groups it does join
/// (<c>CL2:1+2|3+4</c>), sorted — a gap by design inside one part.</param>
public sealed record ConnectivitySignature(
    IReadOnlyList<string> Open, IReadOnlyList<string> Touching, IReadOnlyList<string> Lines,
    IReadOnlyList<string>? Parts = null)
{
    /// <summary>brief-sdl-8 D6 — for a cell solved through its 3D view, each conductor body after elaboration that holds a body
    /// of the view's own (a bond wire, a connector pin, a lid), by its members joined with <c>+</c>, sorted. Null for a
    /// planar setup.</summary>
    public IReadOnlyList<string>? Bodies { get; init; }

    /// <summary>The signature as one comparable string.</summary>
    public string Text => "open:" + string.Join(",", Open) + ";touch:" + string.Join(",", Touching) +
                          ";parts:" + string.Join(",", Parts ?? []) + (Bodies is null ? "" : ";bodies:" + string.Join(",", Bodies));

    /// <summary>Whether two signatures say the same about every joint and every pair of parts.</summary>
    public bool SameAs(ConnectivitySignature other) => Text == other.Text;

    /// <summary>
    /// What differs from <paramref name="start"/>, as a sentence fragment: the planner's own line when this layout has one
    /// the start had not (<c>ML7 overlaps MT2 (nets n4, n9)</c>), else the parts. Null when nothing differs.
    /// </summary>
    public string? DifferenceFrom(ConnectivitySignature start)
    {
        if (SameAs(start)) return null;
        string? line = Lines.Except(start.Lines, StringComparer.Ordinal).FirstOrDefault();
        if (line is not null) return line.TrimEnd('.');
        var parts = new List<string>();
        foreach (var pair in Touching.Except(start.Touching, StringComparer.Ordinal)) parts.Add($"{pair.Replace("|", " and ")} touch");
        foreach (var pair in start.Touching.Except(Touching, StringComparer.Ordinal)) parts.Add($"{pair.Replace("|", " and ")} no longer touch");
        foreach (var gap in Open.Except(start.Open, StringComparer.Ordinal)) parts.Add($"the joint at {gap.Replace("+", ", ")} opens");
        foreach (var gap in start.Open.Except(Open, StringComparer.Ordinal)) parts.Add($"the joint at {gap.Replace("+", ", ")} closes");
        var mine = (Parts ?? []).ToDictionary(p => p[..p.IndexOf(':')], StringComparer.Ordinal);
        foreach (var held in start.Parts ?? [])
        {
            string id = held[..held.IndexOf(':')];
            if (!mine.TryGetValue(id, out var now))
                parts.Add($"{id}'s own copper joins pins it kept apart at the start (its gap closed)");
            else if (now != held)
                parts.Add($"{id}'s own copper joins pins {now[(now.IndexOf(':') + 1)..].Replace("|", " / ")}, not {held[(held.IndexOf(':') + 1)..].Replace("|", " / ")}");
        }
        foreach (var added in (Parts ?? []).Where(p => !(start.Parts ?? []).Contains(p, StringComparer.Ordinal)
                                                       && !(start.Parts ?? []).Any(h => h[..h.IndexOf(':')] == p[..p.IndexOf(':')])))
            parts.Add($"{added[..added.IndexOf(':')]}'s own copper no longer joins all its pins");
        parts.AddRange(BodyChanges(start.Bodies, Bodies));
        return string.Join("; ", parts);
    }

    /// <summary>brief-sdl-8 D6 — for every member of a body at the start, what it no longer touches and what it touches now.</summary>
    private static IEnumerable<string> BodyChanges(IReadOnlyList<string>? start, IReadOnlyList<string>? now)
    {
        if (start is null || now is null) yield break;
        static Dictionary<string, HashSet<string>> ByMember(IEnumerable<string> bodies)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var b in bodies)
            {
                var members = b.Split('+').ToHashSet(StringComparer.Ordinal);
                foreach (var m in members) map[m] = members;
            }
            return map;
        }
        var was = ByMember(start);
        var isNow = ByMember(now);
        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in start)
        {
            // The body is named by its first member that is not a placed layout's piece, else its first.
            var members = body.Split('+');
            string who = members.FirstOrDefault(m => !m.Contains('/')) ?? members[0];
            var nowSet = isNow.GetValueOrDefault(who) ?? [who];
            var lost = members.Where(m => m != who && !nowSet.Contains(m)).ToList();
            if (lost.Count > 0 && said.Add(who + "-"))
                yield return $"'{who}' no longer touches {Names(lost)}";
        }
        foreach (var body in now)
        {
            var members = body.Split('+');
            string who = members.FirstOrDefault(m => !m.Contains('/')) ?? members[0];
            var wasSet = was.GetValueOrDefault(who) ?? [who];
            var gained = members.Where(m => m != who && !wasSet.Contains(m)).ToList();
            if (gained.Count > 0 && said.Add(who + "+"))
                yield return $"'{who}' now touches {Names(gained)}";
        }

        static string Names(List<string> n) =>
            string.Join(", ", n.Take(3).Select(x => $"'{x}'")) + (n.Count > 3 ? $" and {n.Count - 3} more" : "");
    }

    /// <summary>
    /// Where the copper already disagrees with the schematic — a loop that does not close, a connection not joined, an
    /// overlap the schematic does not imply — quoting the planner's report; null when it agrees. A joint opened by hand
    /// is the user's design and is not a disagreement.
    /// </summary>
    public string? Disagreement => Lines.Count == 0 ? null : string.Join(" ", Lines);

    /// <summary>The signature of a plan, from its report lines and — when given — the planned layout's parts.</summary>
    public static ConnectivitySignature Of(IEnumerable<ReportLine> planLines, IReadOnlyList<string>? parts = null)
    {
        var open = new SortedSet<string>(StringComparer.Ordinal);
        var touching = new SortedSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (var l in planLines)
        {
            bool warning = l.Severity == ReportSeverity.Warning;
            string ids = string.Join("+", (l.Ids.Count > 0 ? l.Ids : [l.InstanceName]).Where(i => i.Length > 0).Order(StringComparer.Ordinal));
            switch (l.Kind)
            {
                case ReportKind.LoopGap:
                    // Information is a joint the user opened (SDL-4 D6): held, never a disagreement.
                    open.Add(warning ? ids : "opened:" + ids);
                    break;
                case ReportKind.Unjoined when warning:
                    open.Add("unjoined:" + ids);
                    break;
                case ReportKind.Overlap when warning:
                    touching.Add(ids.Replace('+', '|'));
                    break;
                default:
                    continue;
            }
            if (warning) lines.Add(l.Text);   // the planner's sentence already names its parts
        }
        return new ConnectivitySignature([.. open], [.. touching], lines.Distinct(StringComparer.Ordinal).ToList(), parts);
    }

    /// <summary>
    /// For every placed part of <paramref name="layout"/> (a planned layout, in memory) linked to the schematic: which of
    /// its own pins its own copper joins, through the copper partition (<see cref="CopperPieces"/>, DrcConnectivity's) of
    /// that part's shapes alone. Only parts whose pins fall in two or more groups are listed. One flatten for the whole
    /// layout; the partition per part is a handful of shapes.
    /// </summary>
    public static IReadOnlyList<string> PartGroups(LayoutView layout, string clayPath, Technology tech)
    {
        if (layout.Instances.Count == 0) return [];
        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(clayPath))!)!;
        var tagged = LayoutDesignFlatten.FlattenTagged(layout, cellDir, tech, null, resolvedCrossTechMappings: null);
        var shapesOf = tagged.Shapes.GroupBy(t => t.InstancePath, StringComparer.Ordinal)
                             .ToDictionary(g => g.Key, g => g.Select(t => t.Shape).ToList(), StringComparer.Ordinal);
        var origins = new List<PlacedPinOrigin>();
        var pins = PlacedPins.Of(layout, clayPath, tech, PinNaming.ArtworkOnly, origins: origins, scope: PlacementScope.EveryPlacement);

        var result = new List<string>();
        foreach (var byInstance in Enumerable.Range(0, Math.Min(pins.Count, origins.Count)).GroupBy(i => origins[i].Instance))
        {
            int index = byInstance.Key;
            if (index < 0 || index >= layout.Instances.Count) continue;
            var inst = layout.Instances[index];
            if (inst.SchematicId is not { Length: > 0 } id || byInstance.Count() < 2) continue;
            if (!shapesOf.TryGetValue(LayoutDesignFlatten.PathOf(inst, index), out var shapes)) continue;
            var copper = CopperPieces.Build(shapes, tech);
            var groups = new SortedDictionary<int, SortedSet<string>>();
            int alone = -1;
            foreach (int i in byInstance)
            {
                int piece = copper.PieceAt(pins[i].X, pins[i].Y, origins[i].Layer);
                int group = piece >= 0 ? copper.NetOfPiece(piece) : alone--;
                if (!groups.TryGetValue(group, out var set)) groups[group] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(pins[i].Pin ?? origins[i].PinKey);
            }
            if (groups.Count < 2) continue;
            result.Add(id + ":" + string.Join("|", groups.Values.Select(s => string.Join("+", s)).Order(StringComparer.Ordinal)));
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
