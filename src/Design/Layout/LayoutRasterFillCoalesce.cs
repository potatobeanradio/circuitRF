// Turning a PAINTED pour back into the region it paints
// (docs/sonnet-briefs/brief-rasterfill-3-coalesce-raster-fill-on-import.md).
//
// ── What this exists for ────────────────────────────────────────────────────────────────────────
//
// A CAM tool may express a solid copper pour by RASTER FILL: instead of emitting a filled region, it
// paints the pour with thousands of abutting parallel scanline strokes. The readers are right to keep
// a stroke a stroke (GerberReader.Regions.cs states that rule), and every consumer downstream is then
// looking at tens of thousands of overlapping stroked outlines where the artwork has ONE region — the
// renderer rasterizes all of them every frame, the planar extractor meshes all of them, the DRC engine
// measures clearances against all of them, and the writers write them all back out.
//
// This turns such a group back into PolygonShapes with holes. It is not a rendering trick and it does
// not live in the renderer: the point is that the STORED geometry is the shape every consumer wants.
//
// ── The three things that are easy to get wrong ─────────────────────────────────────────────────
//
//  * THE UNION IS ONE PASS, NEVER PAIRWISE (R-rf3-1). Pairwise booleans over 29,000 shapes are
//    quadratic and will not finish. Every candidate's stroked outline goes into ONE Paths64 and one
//    Clipper2 union resolves the lot. Clipper2 rather than the brief's suggested SKPath.Simplify:
//    LayoutClipper is already this repo's single boolean seam, its arithmetic is EXACT on our DBU
//    integers with no scaling step, and its PolyTree64 output already carries the hole nesting
//    R-rf3-2 asks for. Simplify would have meant a second geometry pipeline in float.
//
//  * NEVER ACROSS POLARITY, NEVER ACROSS LAYERS (R-rf3-3). A CLEAR (%LPC*%) stroke REMOVES copper;
//    unioning it with a dark one paints the hole solid and the picture still looks plausible. The
//    callers hand over only groups that are interchangeable in every respect — see Candidate.GroupKey.
//
//  * THE DECISION IS COUNTED, NOT GUESSED (R-rf3-4). Stroke width, aperture name and layer name are
//    all NOT evidence: width is how the raster was painted, not what it means, and names are not
//    portable. What is measurable is how many strokes collapse into how many regions, and whether the
//    result is simpler than what it replaces. Both are asserted below and both are reported.

using Clipper2Lib;

namespace CircuitRF.Design.Layout;

public static class LayoutRasterFillCoalesce
{
    /// <summary>
    /// How many strokes one resulting region must have absorbed, on average, before the group counts
    /// as painted rather than drawn.
    ///
    /// <para><b>It is a RATIO, not the stroke-count threshold R-rf3-4 forbids.</b> A 300-stroke pour
    /// collapsing to one region scores 300 and is treated exactly like a 30,000-stroke one; a
    /// 30,000-trace board whose traces meet at pads collapses to roughly one region per net and scores
    /// single digits, so none of it changes. The degenerate case the rule exists for is a single trace
    /// — one stroke, one region, a score of 1 — which stays a <c>PathShape</c> (gate 6).</para>
    ///
    /// <para>Measured rather than assumed: see <c>src/Design/RESOLVED.md</c> for the scores the two
    /// classes actually produce, which are three orders of magnitude apart.</para>
    /// </summary>
    public const int MinStrokesPerRegion = 50;

    /// <summary>
    /// The flattening tolerance R-rf3-5 requires be STATED and carried — 0.1 µm at whatever resolution
    /// the document is in. It is what a round end cap's arc is flattened to on the way into the union,
    /// and therefore the only place the result is not exact.
    ///
    /// <para>Not <see cref="LayoutFlattener.DefaultTolDbu"/> (1 µm): on a 1-mil scanline that is a
    /// 4% chord error at the pour boundary, which is where a DRC clearance is measured. Not Clipper2's
    /// own default either — that resolves to roughly 1 DBU on a 12,700 DBU offset, i.e. ~250 segments
    /// per cap circle, which turns a 29,000-stroke pour into a seven-million-point union input for
    /// accuracy nothing downstream can use.</para>
    /// </summary>
    public static long DefaultToleranceDbu(int dbuPerMicron) => Math.Max(1, dbuPerMicron / 10);

    /// <summary>
    /// One stroke offered for coalescing, with the key that says what it is interchangeable WITH.
    ///
    /// <para><b>The caller owns the key and must put everything into it</b> (R-rf3-3): the layer, the
    /// polarity, the net, and any per-object attribute the shape carries. Two strokes with the same key
    /// are two strokes whose union is indistinguishable from the pair; two with different keys are not,
    /// however identical the geometry looks.</para>
    /// </summary>
    public readonly record struct Candidate(LayoutShape Shape, string GroupKey, string LayerName);

    /// <summary>One group that passed, with the counts the import report is built from.</summary>
    public sealed record Replacement(
        string LayerName,
        IReadOnlyList<LayoutShape> Replaced,
        IReadOnlyList<LayoutShape> Regions,
        int StrokeOutlineVertices,
        int RegionVertices);

    /// <summary>
    /// What coalescing WOULD do, computed but not yet applied — so a caller can apply it to its own
    /// list (which is usually a list of wrapper records, not of bare shapes) by reference identity,
    /// rather than having this rebuild a list it does not own.
    /// </summary>
    public sealed record Plan(IReadOnlyList<Replacement> Replacements, long ToleranceDbu)
    {
        public bool IsEmpty => Replacements.Count == 0;

        /// <summary>Every stroke this plan consumes, by REFERENCE — the set a caller filters its own
        /// list against.</summary>
        public HashSet<LayoutShape> Replaced
        {
            get
            {
                var set = new HashSet<LayoutShape>(ReferenceEqualityComparer.Instance);
                foreach (var r in Replacements) foreach (var s in r.Replaced) set.Add(s);
                return set;
            }
        }

        /// <summary>Where each group's regions go: keyed by the group's FIRST stroke, so a caller
        /// splicing its own list keeps the document's shape order rather than moving every coalesced
        /// pour to the end.</summary>
        public Dictionary<LayoutShape, IReadOnlyList<LayoutShape>> RegionsByFirstReplaced
        {
            get
            {
                var map = new Dictionary<LayoutShape, IReadOnlyList<LayoutShape>>(ReferenceEqualityComparer.Instance);
                foreach (var r in Replacements)
                    if (r.Replaced.Count > 0) map[r.Replaced[0]] = r.Regions;
                return map;
            }
        }

        /// <summary>Applies the plan to a plain shape list, preserving order: each group's regions are
        /// emitted where that group's FIRST stroke was, and the rest of its strokes drop out.</summary>
        public IReadOnlyList<LayoutShape> Apply(IReadOnlyList<LayoutShape> shapes)
        {
            if (IsEmpty) return shapes;

            var regionsByFirst = RegionsByFirstReplaced;
            var replaced = Replaced;

            var result = new List<LayoutShape>(shapes.Count);
            foreach (var s in shapes)
            {
                if (regionsByFirst.TryGetValue(s, out var regions)) result.AddRange(regions);
                else if (!replaced.Contains(s)) result.Add(s);
            }
            return result;
        }

        /// <summary>R-rf3-6: one line per layer, naming the layer and both counts. A silent structural
        /// change to someone's artwork is not acceptable even when it is an improvement, and this is
        /// the line that makes the feature diagnosable from a log when a board comes back looking
        /// wrong.</summary>
        public IEnumerable<string> Notes(int dbuPerMicron)
        {
            foreach (var r in Replacements)
                yield return
                    $"{r.LayerName}: {r.Replaced.Count:N0} painted stroke(s) coalesced into " +
                    $"{r.Regions.Count:N0} filled region(s) — that layer was raster fill, and the region " +
                    $"is what it paints. Round end caps were flattened to " +
                    $"{(double)ToleranceDbu / dbuPerMicron:0.###} µm, which is the only approximation " +
                    "in it. Import with coalescing turned off to get the strokes exactly as authored.";
        }
    }

    public static readonly Plan Nothing = new([], 0);

    /// <summary>
    /// The decision and the geometry, for every group in <paramref name="candidates"/>.
    ///
    /// <para>Non-<see cref="PathShape"/> candidates are ignored: a flash is not a scanline, and leaving
    /// the pads alone is also what keeps via pairing able to find them. A group smaller than
    /// <see cref="MinStrokesPerRegion"/> is rejected without computing its union at all — it could not
    /// pass, since a union is at least one region — which is what keeps this free on an ordinary
    /// board.</para>
    /// </summary>
    public static Plan Build(IReadOnlyList<Candidate> candidates, long toleranceDbu)
    {
        if (candidates.Count < MinStrokesPerRegion) return Nothing;

        // Deterministic: groups are visited in first-appearance order, never in hash order (R-L1c-1's
        // determinism discipline — two runs of one import must produce the same .clay bytes).
        var order = new List<string>();
        var groups = new Dictionary<string, List<PathShape>>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (c.Shape is not PathShape p || p.Width <= 0) continue;
            if (!groups.TryGetValue(c.GroupKey, out var list))
            {
                groups[c.GroupKey] = list = [];
                order.Add(c.GroupKey);
            }
            list.Add(p);
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in candidates) names.TryAdd(c.GroupKey, c.LayerName);

        var replacements = new List<Replacement>();
        foreach (string key in order)
        {
            var group = groups[key];
            if (group.Count < MinStrokesPerRegion) continue;

            var outlines = new Paths64();
            foreach (var p in group) outlines.AddRange(LayoutClipper.ToClipperPaths(p, toleranceDbu, toleranceDbu));
            if (outlines.Count == 0) continue;

            int strokeVertices = 0;
            foreach (var path in outlines) strokeVertices += path.Count;

            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Union, outlines, new Paths64(), tree, LayoutClipper.Rule);
            var regions = LayoutClipper.FromClipperTree(tree, group[0].Layer, group[0].Net);
            if (regions.Count == 0) continue;

            int regionVertices = 0;
            foreach (var r in regions) regionVertices += VertexCount(r);

            // R-rf3-4's two counted conditions. Neither is a heuristic about what the artwork MEANS.
            //
            //  1. The strokes genuinely collapse: this is the one that separates a painted region from
            //     a network of drawn traces, and it is the one that keeps a single trace a PathShape.
            //  2. The result is not more complex than what it replaces, compared LIKE FOR LIKE — the
            //     union's rings against the stroked OUTLINES, which is the geometry the renderer, the
            //     mesher, the DRC engine and the writers each derive from these strokes today. (The
            //     brief's literal wording compares against the strokes' STORED vertices, which is a
            //     two-point centreline against a capped outline: that comparison can never be won and
            //     would switch the whole tier off. See src/Design/RESOLVED.md.)
            if (group.Count < MinStrokesPerRegion * regions.Count) continue;
            if (regionVertices > strokeVertices) continue;

            foreach (var r in regions)
            {
                r.Component = group[0].Component;
                r.Pin = group[0].Pin;
            }
            replacements.Add(new Replacement(
                names.TryGetValue(key, out string? n) ? n : "",
                [.. group], regions, strokeVertices, regionVertices));
        }

        return replacements.Count == 0 ? Nothing : new Plan(replacements, toleranceDbu);
    }

    /// <summary>
    /// The union alone, with no decision attached — the entry point the area/hole/polarity gates
    /// assert against, and the one place the geometry lives.
    /// </summary>
    public static IReadOnlyList<LayoutShape> Union(
        IReadOnlyList<LayoutShape> strokes, long toleranceDbu, LayerKey layer, string? net)
    {
        var outlines = new Paths64();
        foreach (var s in strokes) outlines.AddRange(LayoutClipper.ToClipperPaths(s, toleranceDbu, toleranceDbu));
        if (outlines.Count == 0) return [];

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, outlines, new Paths64(), tree, LayoutClipper.Rule);
        return LayoutClipper.FromClipperTree(tree, layer, net);
    }

    private static int VertexCount(LayoutShape shape)
    {
        switch (shape)
        {
            case PolygonShape p:
            {
                int n = p.Xy.Length / 2;
                if (p.Holes is { } holes) foreach (var h in holes) n += h.Length / 2;
                return n;
            }
            case CurveShape c:
            {
                int n = c.Xy.Length / 2;
                if (c.Holes is { } holes) foreach (var h in holes) n += h.Length / 2;
                return n;
            }
            case PathShape path: return path.Xy.Length / 2;
            default: return 4;
        }
    }
}
