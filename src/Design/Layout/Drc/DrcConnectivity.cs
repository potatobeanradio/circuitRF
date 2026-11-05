// Which drawn metal is electrically one net (docs/design/layout-view.md §9A.3).
//
// <b>This is the capability the net-aware rules were blocked on.</b> A real deck states a large
// share of its spacing rules twice — once for metal on the SAME net and once for DIFFERENT nets,
// with different values, because two pieces of one net may sit closer than two that could short.
// Without net identity a checker has to pick one of the two values: the same-net value passes
// genuine shorts, and the different-net value fails correct artwork. Neither is acceptable, which
// is why v1 read those rules and did not enforce them.
//
// <b>What this is NOT.</b> It is not LVS. It answers "which shapes are electrically joined to each
// other", from geometry and the stackup alone. It does not know what any net is CALLED, does not
// compare against a schematic, and does not extract devices. Those are separate questions and
// naming this class anything LVS-flavoured would invite the assumption that it answers them.

using Clipper2Lib;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Drc;

/// <summary>
/// One electrically-connected piece of metal: a region, the layer it sits on, and the net it
/// belongs to.
/// </summary>
/// <param name="Layer">The drawing layer this piece is on.</param>
/// <param name="Paths">Its geometry, Clipper2 form, DBU.</param>
/// <param name="Bounds">Bounding box, so a pairwise sweep can reject most pairs without work.</param>
/// <param name="Net">Net index. Two pieces with the same index are electrically the same net.</param>
internal sealed record DrcNetPiece(LayerKey Layer, Paths64 Paths, Bbox Bounds, int Net);

/// <summary>What joined two pieces — brief-lvs-2-shared-extraction.md R-lvs2-4a.</summary>
public enum JoinKind
{
    /// <summary>Two pieces of metal meeting on one drawing layer.</summary>
    SameLayerTouch,

    /// <summary>A via barrel bridging a piece on one conductor to a piece on another.</summary>
    Via,
}

/// <summary>
/// One edge of the spanning forest the net partition was built from — <b>WHY two pieces are one
/// net</b>, which is the only question a designer looking at a short actually has (R-lvs2-4).
/// </summary>
/// <remarks>
/// <b>Membership cannot answer it.</b> <see cref="DrcConnectivity.Extract(IReadOnlyDictionary{LayerKey, Paths64}, Technology)"/>
/// unions and renumbers, and what survives is which pieces share a number. That answers <i>are
/// these two pins one net</i> and nothing else; <i>"joined through a via at (1.204 mm, 3.881 mm)"</i>
/// is the whole difference between a report a user can act on and one they cannot.
///
/// <para><b>ONE EDGE PER UNION, NOT EVERY TOUCHING PAIR</b> (R-lvs2-4c). A spanning forest is all a
/// path walk needs, and recording every adjacency would make the structure quadratic in the thing
/// it exists to make cheap.</para>
/// </remarks>
/// <param name="PieceA">Index into the piece list <c>Extract</c> returned.</param>
/// <param name="PieceB">The other one.</param>
/// <param name="Kind">Measured from the two pieces, not from which loop produced the union.</param>
/// <param name="X">WHERE the join happens, DBU — the via's own position for a via, a point inside
/// the intersection for a same-layer touch (R-lvs2-4d). This is the coordinate brief 8 puts a
/// marker on.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The layer the join is observed on — the via's own drawing layer for a via.</param>
public readonly record struct PieceJoin(
    int PieceA, int PieceB, JoinKind Kind, long X, long Y, LayerKey Layer);

/// <summary>
/// What the stackup's own ground reference contributed to the partition — <c>brief-lvs-3-layout-netlist.md</c>
/// R-lvs3-6, and the second reader of <see cref="StackupLayer.IsGroundReference"/> the design note
/// (R-lvs-15) asks for.
/// </summary>
/// <remarks>
/// <b>ONLY AN UNDRAWN REFERENCE IS INFERRED, AND THE NOTE'S OWN RULE HAD TO BE NARROWED TO GET
/// THERE.</b> R-lvs-15 reads "every piece on a ground-reference conductor's drawing layers, if it
/// has any, is net 0". Three of the four shipped PCB technologies flag their BOTTOM COPPER as the
/// reference — correctly, because the flag was added so a microstrip's substrate resolution would
/// find the right plane — and a two-layer board routes signals on that layer. Read literally, every
/// bottom-side trace on the most common shipped technology becomes net 0 and the whole board reads
/// as one short; the shipped four-layer technology flags Bottom Copper too, beside its real inner
/// plane, so no rule keyed on the flag alone can tell the two apart.
///
/// <para>So the inference is confined to the case that actually needs one and is the gap the note
/// is about (G4): a reference conductor that <b>draws nothing</b>. Drawn copper is read from the
/// artwork, by the partition, exactly as every other piece is — there is something on the screen to
/// look at and nothing to infer. See <c>src/Design/RESOLVED.md</c>.</para>
///
/// <para><b>Additive, and it changes the partition for nobody</b> — the same shape brief 2 gave
/// <see cref="PieceJoin"/>. Nothing here unions anything: the grounded nets are REPORTED and the one
/// reader that wants them as a single net (LVS) merges them on its own account, so the DRC's
/// net-aware rules and railRF's island count are bit-for-bit what they were.</para>
/// </remarks>
/// <param name="ReferenceName">The <see cref="StackupLayer.Name"/> of the conductor flagged
/// <see cref="StackupLayer.IsGroundReference"/>, or null when the technology flags none — which is
/// R-lvs3-6d's warning, and is a real and correct design (a die grounded only through bondwires).</param>
/// <param name="ReferenceDraws">Whether that conductor has any drawing layers. True means nothing
/// was inferred: see the remarks.</param>
/// <param name="Nets">Partition net indices — the same numbers <see cref="DrcNetPiece.Net"/> carries
/// — read as reaching the reference.</param>
/// <param name="ViasReached">How many via barrels reached an undrawn reference. This is the count
/// R-lvs3-6e's sentence states, and it is the part a user cannot see on their own screen.</param>
internal readonly record struct GroundReach(
    string? ReferenceName, bool ReferenceDraws, IReadOnlySet<int> Nets, int ViasReached)
{
    /// <summary>The reference conductor's drawing layers — empty when it draws nothing. Where
    /// <c>on_ground</c> looks for the plane on a drawn reference (brief-review-3); the partition
    /// itself still infers nothing there.</summary>
    public IReadOnlyList<LayerKey> ReferenceLayers { get; init; } = [];

    /// <summary>A technology that flags no ground reference at all.</summary>
    public static readonly GroundReach None =
        new(null, ReferenceDraws: false, new HashSet<int>(), 0);
}

/// <summary>
/// brief-railrf-36 — what one connectivity extraction COST, counted rather than timed (a timing test
/// measures the machine). Collected only inside <see cref="Begin"/>'s scope, which flows into the
/// tasks it starts, so parallel tests each see their own numbers.
/// </summary>
internal sealed class ConnectivityCounters
{
    private static readonly AsyncLocal<ConnectivityCounters?> Current = new();

    /// <summary>Partitions actually computed — a cache hit is not one.</summary>
    public int Extractions;

    /// <summary>Partitions answered from <see cref="ConnectivityCache"/>.</summary>
    public int CacheHits;

    /// <summary>Barrel-against-piece questions that survived the bounding-box rejection.</summary>
    public long TouchTests;

    /// <summary>Clipper boolean operations those questions ran.</summary>
    public long Clips;

    /// <summary>Piece vertices handed to those operations — the number that was tens of thousands
    /// per barrel when every barrel was clipped against a whole plane.</summary>
    public long VerticesClipped;

    internal static ConnectivityCounters? Active => Current.Value;

    /// <summary>Counts every extraction made on this logical call path until disposed.</summary>
    public static Scope Begin()
    {
        var counters = new ConnectivityCounters();
        var previous = Current.Value;
        Current.Value = counters;
        return new Scope(counters, previous);
    }

    internal sealed class Scope(ConnectivityCounters counters, ConnectivityCounters? previous) : IDisposable
    {
        public ConnectivityCounters Counters { get; } = counters;
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Extracts net identity from flat geometry plus the technology's own stackup.
///
/// <para>Two pieces of metal are the same net when they touch on one layer, or when a via joins
/// them across layers. The stackup is what says which layers a via joins — that is exactly what
/// <see cref="StackupLayer.SpanFromLayer"/>/<see cref="StackupLayer.SpanToLayer"/> have carried
/// since the via primitive landed, unread until now.</para>
/// </summary>
internal static class DrcConnectivity
{
    /// <summary>
    /// Two regions count as joined when they overlap or share an edge. Same reasoning, and the same
    /// number, as the topological selections: Clipper2 reports an edge-only intersection as zero
    /// area, and a via landing exactly on a metal edge is a real connection.
    /// </summary>
    private const double TouchDilationDbu = 1.0;

    /// <summary>
    /// Builds the net partition.
    /// </summary>
    /// <param name="layerRegions">Per-layer unioned geometry, as the DRC run already built it.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <returns>
    /// Every connected piece, with a net index. Layers the stackup does not describe still yield
    /// pieces — each simply forms its own net, since nothing states what it connects to.
    /// </returns>
    public static IReadOnlyList<DrcNetPiece> Extract(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech) => ConnectivityCache.Get(layerRegions, tech).Pieces;

    /// <summary>
    /// The same partition, and <b>the spanning forest it was built from</b> — R-lvs2-4b.
    /// </summary>
    /// <param name="joins">One record per successful union.</param>
    /// <remarks>
    /// <b>An additive OVERLOAD, not a change to the existing return.</b> Since brief-railrf-36 the
    /// forest is recorded on every extraction, because one extraction now serves every question
    /// asked of the same artwork (<see cref="ConnectivityCache"/>). It costs nearly nothing: a join
    /// between two different layers is located at the via, with no clip at all, and only a
    /// same-layer join clips — locally.
    /// </remarks>
    public static IReadOnlyList<DrcNetPiece> Extract(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        out IReadOnlyList<PieceJoin> joins)
    {
        var partition = ConnectivityCache.Get(layerRegions, tech);
        joins = partition.Joins;
        return partition.Pieces;
    }

    /// <summary>
    /// The same partition, and <b>which of its nets the stackup's ground reference reaches</b> —
    /// R-lvs3-6. See <see cref="GroundReach"/> for why only an UNDRAWN reference is inferred.
    /// </summary>
    /// <remarks>
    /// <b>An additive form, for <see cref="PieceJoin"/>'s reason.</b> The walk is the same walk,
    /// and what this form keeps is a set of integers the other forms discard.
    ///
    /// <para><b>A different NAME rather than a third overload</b>, because two <c>Extract</c>
    /// overloads differing only in their out-parameter type make <c>out var</c> ambiguous, and
    /// <c>out var</c> is how every existing caller is written.</para>
    /// </remarks>
    public static IReadOnlyList<DrcNetPiece> ExtractWithGround(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        out GroundReach ground)
    {
        var partition = ConnectivityCache.Get(layerRegions, tech);
        ground = partition.Ground;
        return partition.Pieces;
    }

    /// <summary>
    /// The partition, the ground reading <b>and</b> the spanning forest — the one form
    /// <c>brief-lvs-8-findings.md</c> R-lvs8-4a needs, because a short has to be reported with its
    /// PATH and an LVS run needs the ground reading in the same walk.
    /// </summary>
    /// <remarks>
    /// <b>A fourth parameter rather than a fourth name.</b> The two existing additive forms each
    /// keep half of what this walk already computes and throw the other half away; asking for both
    /// is not a third question, and a second <c>Extract</c> overload differing only in its
    /// out-parameters would make <c>out var</c> ambiguous at every existing call site.
    /// </remarks>
    public static IReadOnlyList<DrcNetPiece> ExtractWithGround(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        out GroundReach ground,
        out IReadOnlyList<PieceJoin> joins)
    {
        var partition = ConnectivityCache.Get(layerRegions, tech);
        ground = partition.Ground;
        joins = partition.Joins;
        return partition.Pieces;
    }

    /// <summary>
    /// The whole cached answer — pieces, joins, ground reach and <b>every barrel's touches</b> —
    /// for a reader that needs all of it from one walk (<c>brief-artsch-3-board-graph.md</c>: a via
    /// is classified by the pieces at its ends, which only <see cref="ConnectivityPartition.Barrels"/>
    /// records). The same <see cref="ConnectivityCache"/> read every other form makes.
    /// </summary>
    internal static ConnectivityPartition Partition(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech) => ConnectivityCache.Get(layerRegions, tech);

    /// <summary>A piece with fewer vertices than this is clipped whole, exactly as before
    /// brief-railrf-36 — a grid over a via pad or a trace costs more than the clip it saves.</summary>
    private const int LocalTestMinVertices = 64;

    /// <summary>
    /// The partition itself — every form above reads it through <see cref="ConnectivityCache"/>.
    /// Always records the joins and the ground reading, because the cached answer serves every form.
    /// </summary>
    internal static ConnectivityPartition Compute(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech)
    {
        var counters = ConnectivityCounters.Active;
        if (counters is not null) Interlocked.Increment(ref counters.Extractions);

        var joins = new List<PieceJoin>();

        // ── Every connected piece on every layer, before any via is considered ──────────────────
        var pieces = new List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)>();
        var byLayer = new Dictionary<LayerKey, List<int>>();

        foreach (var (layer, region) in layerRegions)
        {
            foreach (var component in DrcRegions.Components(region))
            {
                if (!byLayer.TryGetValue(layer, out var list)) byLayer[layer] = list = [];
                list.Add(pieces.Count);
                pieces.Add((layer, component, DrcRegions.BoundsOf(component)));
            }
        }

        if (pieces.Count == 0) return new ConnectivityPartition([], joins, GroundReach.None);

        var uf = new UnionFind(pieces.Count);
        var meets = new Dictionary<int, Paths64>();
        var local = new LocalTouch(pieces, counters);

        // ── R-lvs3-6: the ground reference, collected as the via walk goes past it ──────────────
        //
        // The flagged conductor, its drawing layers, and the PIECES an undrawn one was reached
        // from. Nothing is unioned here: see GroundReach's own remarks.
        var groundLayer = tech.Stackup.Layers.FirstOrDefault(
            l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.Name.Length > 0);
        string? groundName = groundLayer?.Name;
        bool groundDraws = groundLayer is { DrawingLayers.Count: > 0 };
        var groundPieces = new HashSet<int>();
        int groundVias = 0;
        var barrels = new List<BarrelTouch>();

        // ── Vias join pieces across layers ──────────────────────────────────────────────────────
        // A via's own geometry is the bridge: a piece on the layer below and a piece on the layer
        // above are one net when BOTH touch the same via. Testing "does the via touch each side"
        // rather than "do the two sides overlap each other" is what makes a staircase of offset
        // metal connect correctly — the two metal pieces need never overlap one another.
        // Ordered top to bottom, which is what Stackup.Layers is (R-em-3) — so the conductors a via
        // passes THROUGH are the ones between its two span ends in this list. The enumeration is
        // Conductors.Of's, written once (R-lvs2-1); duplicates are preserved because the order is
        // read by INDEX and collapsing them would move every index after the collapse.
        var stackupConductors = Conductors.Of(tech);
        var conductorLayers = new Dictionary<string, IReadOnlyList<LayerKey>>(StringComparer.Ordinal);
        var conductorOrder = new List<string>(stackupConductors.Count);
        foreach (var c in stackupConductors)
        {
            conductorLayers[c.StackupName] = c.DrawingLayers;
            conductorOrder.Add(c.StackupName);
        }

        foreach (var via in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via))
        {
            if (via.SpanFromLayer is not { Length: > 0 } from ||
                via.SpanToLayer is not { Length: > 0 } to) continue;
            if (!conductorLayers.ContainsKey(from) || !conductorLayers.ContainsKey(to)) continue;

            // ── EVERY CONDUCTOR THE BARREL PASSES, NOT ONLY ITS TWO ENDS ────────────────────────
            //
            // A plated barrel shorts every layer it passes through that has copper at that point,
            // and the ANTI-PAD is how the artwork says which those are: copper right up to the
            // barrel is a connection, a clearance round it is not. Joining only the span's two ends
            // read a four-layer board with a power plane on an inner layer as though the plane were
            // not on the board — the plane came back a galvanically separate island, carrying no
            // current and contributing nothing, with the picture showing it plainly connected.
            //
            // It could not fail loudly, either: Regions reports islands as ORDINARY on
            // imported artwork ("the copper stops at every pad"), so the count went up by one and
            // read as the thing that note is about.
            int a = conductorOrder.IndexOf(from), b = conductorOrder.IndexOf(to);
            var spannedNames = conductorOrder.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
            var spanned = spannedNames.Select(name => conductorLayers[name]).ToList();

            // R-lvs3-6c. A barrel that REACHES the reference terminates on net 0 even where that
            // conductor draws nothing, because the stackup is the statement that the metal is
            // there. Only where it draws nothing: a drawn reference is ordinary copper and the
            // partition already says what touches it (GroundReach's remarks).
            bool spansUndrawnGround =
                !groundDraws && groundName is not null && spannedNames.Contains(groundName, StringComparer.Ordinal);

            // TFR-V — a via ending on a film level has one conductor to touch, because the level draws no metal and a film is not
            // copper. The via is part of the metal at its other end, and the film beneath it is a device between two such nets.
            bool endsOnFilm = tech.Stackup.Layers.Any(l => ResistiveFilms.IsFilmLevel(l)
                && (string.Equals(l.Name, from, StringComparison.Ordinal) || string.Equals(l.Name, to, StringComparison.Ordinal)));

            foreach (var viaLayer in via.DrawingLayers)
            {
                if (!byLayer.TryGetValue(viaLayer, out var viaPieces)) continue;

                foreach (int v in viaPieces)
                {
                    var touched = new List<int>();
                    foreach (var layers in spanned)
                        if (FirstTouching(pieces, byLayer, layers, v, local, out var meet) is { } hit)
                        {
                            touched.Add(hit);
                            meets[hit] = meet;
                        }

                    // brief-railrf-38: what the rule found, kept whole — see ConnectivityPartition.Barrels.
                    if (touched.Count >= 2) barrels.Add(new BarrelTouch(v, touched));

                    // R-lvs3-6c, and it must be read BEFORE the bail-out below: the whole point of
                    // an undrawn reference is that the barrel touches exactly ONE drawn conductor —
                    // the other end is the metal nobody drew — so the common shape here is the one
                    // the next line skips.
                    if (spansUndrawnGround)
                    {
                        groundVias++;
                        groundPieces.Add(v);
                        foreach (int hit in touched) groundPieces.Add(hit);
                    }

                    // A via touching only one conductor is a real, common state mid-edit — it
                    // connects nothing yet. It is not an error here; a rule about it is a rule's
                    // business. Unchanged: what widened is WHICH conductors are candidates.
                    if (touched.Count < (endsOnFilm ? 1 : 2)) continue;

                    foreach (int hit in touched)
                    {
                        // R-lvs2-4c: the record is written only where the union actually MERGED
                        // two sets, so what is retained is a spanning forest rather than every
                        // adjacency.
                        if (!uf.Union(v, hit)) continue;
                        joins.Add(JoinOf(pieces, v, hit, meets.GetValueOrDefault(hit)));
                    }
                }
            }
        }

        var result = new List<DrcNetPiece>(pieces.Count);
        foreach (var (layer, paths, bounds) in pieces)
            result.Add(new DrcNetPiece(layer, paths, bounds, 0));

        // Renumber to dense, ascending net indices so the numbers are stable and readable rather
        // than being whatever the union-find happened to leave as a root.
        var netOf = new Dictionary<int, int>();
        for (int i = 0; i < result.Count; i++)
        {
            int root = uf.Find(i);
            if (!netOf.TryGetValue(root, out int net))
            {
                net = netOf.Count;
                netOf[root] = net;
            }
            result[i] = result[i] with { Net = net };
        }

        var groundNets = new HashSet<int>();
        foreach (int piece in groundPieces) groundNets.Add(result[piece].Net);

        return new ConnectivityPartition(
            result, joins, new GroundReach(groundName, groundDraws, groundNets, groundVias)
            {
                ReferenceLayers = groundLayer is null ? [] : [.. groundLayer.DrawingLayers],
            })
            { Barrels = barrels };
    }

    /// <summary>The first piece on any of <paramref name="layers"/> that touches the piece at
    /// <paramref name="probe"/>, or null.</summary>
    /// <param name="meet">The two pieces' overlap — computed only where they are on ONE layer,
    /// because that is the only join <see cref="JoinOf"/> reads it for.</param>
    private static int? FirstTouching(
        List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> pieces,
        Dictionary<LayerKey, List<int>> byLayer,
        IReadOnlyList<LayerKey> layers,
        int probe,
        LocalTouch local,
        out Paths64 meet)
    {
        meet = [];
        var grown = Clipper.InflatePaths(pieces[probe].Paths, TouchDilationDbu, JoinType.Miter, EndType.Polygon, 2.0);
        var probeBounds = DrcRegions.Grow(pieces[probe].Bounds, (long)Math.Ceiling(TouchDilationDbu));
        var reach = DrcRegions.BoundsOf(grown);

        foreach (var layer in layers)
        {
            if (!byLayer.TryGetValue(layer, out var candidates)) continue;

            foreach (int i in candidates)
            {
                if (!probeBounds.Intersects(pieces[i].Bounds)) continue;   // cheap rejection first

                bool wantMeet = pieces[i].Layer == pieces[probe].Layer;
                if (local.Touches(i, grown, reach, wantMeet, out var hit)) { meet = hit; return i; }
            }
        }

        return null;
    }

    /// <summary>
    /// R-rail36-2 — "does this grown barrel meet this piece", with the answer a whole-piece
    /// <c>Clipper.BooleanOp(Intersection)</c> gives and the cost of the barrel's neighbourhood.
    /// </summary>
    /// <remarks>
    /// <para><b>The same answer, including at the dilation boundary.</b> A barrel exactly
    /// <see cref="TouchDilationDbu"/> from a piece's edge meets it along a line, which Clipper
    /// reports as nothing, and a barrel on the edge meets it with area — so the decision is Clipper's
    /// and must stay Clipper's wherever an edge is near. <see cref="PieceEdgeGrid"/> says which of the
    /// piece's rings have an edge within the grown barrel's box; the rest have a constant winding
    /// there, read by one exact ray cast. Where no ring is near, that winding IS the answer — inside
    /// means the whole grown barrel is covered. Otherwise the near rings, unmodified, plus the
    /// constant as a rectangle just larger than the box, are clipped: the same point set within the
    /// box, from the same edges, so the same answer.</para>
    ///
    /// <para><b>What it replaced</b> clipped every barrel against the whole of every candidate: on
    /// the six-layer field board every plane is a candidate for every barrel, 15,831 clips over
    /// 631 million vertices, 224 s for one extraction.</para>
    /// </remarks>
    /// <para><b>Also the evaluator's</b> (brief-review-3): <c>same_net</c> and <c>on_ground</c> ask
    /// which pieces a pad or a barrel touches, and against a pour or a plane that is this question.</para>
    internal sealed class LocalTouch(
        IReadOnlyList<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> pieces, ConnectivityCounters? counters)
    {
        private readonly PieceEdgeGrid?[] _grids = new PieceEdgeGrid?[pieces.Count];
        private readonly int[] _vertices = new int[pieces.Count];
        private readonly List<int> _near = [];

        public bool Touches(int piece, Paths64 grown, Bbox reach, bool wantMeet, out Paths64 meet)
        {
            meet = [];
            if (counters is not null) Interlocked.Increment(ref counters.TouchTests);

            var paths = pieces[piece].Paths;
            if (_vertices[piece] == 0) foreach (var p in paths) _vertices[piece] += p.Count;

            if (_vertices[piece] < LocalTestMinVertices || reach.IsEmpty)
                return Clip(grown, paths, _vertices[piece], out meet);

            var grid = _grids[piece] ??= PieceEdgeGrid.Build(paths);
            grid.Local(reach, _near, out int winding);

            // Nothing of the piece's outline is anywhere near: the grown barrel is wholly inside the
            // piece, or wholly outside it.
            if (_near.Count == 0 && winding == 0) return false;
            if (_near.Count == 0 && !wantMeet) return true;

            var subject = new Paths64(_near.Count + Math.Abs(winding));
            long vertices = 0;
            foreach (int r in _near) { subject.Add(paths[r]); vertices += paths[r].Count; }

            // The rest's constant winding, as a rectangle a little larger than the box so none of
            // its edges meets the grown barrel. Counter-clockwise is +1, Sunday's sign.
            var box = DrcRegions.Grow(reach, 2);
            for (int k = 0; k < Math.Abs(winding); k++)
            {
                var rect = new Path64
                {
                    new Point64(box.MinX, box.MinY), new Point64(box.MaxX, box.MinY),
                    new Point64(box.MaxX, box.MaxY), new Point64(box.MinX, box.MaxY),
                };
                if (winding < 0) rect.Reverse();
                subject.Add(rect);
                vertices += 4;
            }

            return Clip(grown, subject, vertices, out meet);
        }

        private bool Clip(Paths64 grown, Paths64 subject, long vertices, out Paths64 meet)
        {
            if (counters is not null)
            {
                Interlocked.Increment(ref counters.Clips);
                Interlocked.Add(ref counters.VerticesClipped, vertices);
            }

            meet = Clipper.BooleanOp(ClipType.Intersection, grown, subject, LayoutClipper.Rule);
            return meet.Count > 0;
        }
    }

    /// <summary>
    /// Where a join happens and what kind it is — R-lvs2-4d.
    /// </summary>
    /// <remarks>
    /// <b>The kind is MEASURED from the two pieces, not assumed from the loop.</b> Today every
    /// union this walk performs bridges a via barrel to a conductor, so every join it records is a
    /// <see cref="JoinKind.Via"/>: two pieces of metal meeting on ONE layer were already unioned
    /// into a single component by <see cref="DrcRegions.Components"/> before the union-find saw
    /// them, so there is no same-layer edge left to record. Classifying by measurement rather than
    /// by provenance means the answer stays right when a caller unions on its own account — brief
    /// 9's boundary stitching is that caller.
    /// </remarks>
    private static PieceJoin JoinOf(
        List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> pieces, int via, int hit, Paths64? meet)
    {
        bool sameLayer = pieces[via].Layer == pieces[hit].Layer;

        // A via's OWN position, which is the centre of its barrel — the thing a user is being asked
        // to look at. For a same-layer touch there is no such thing, so it is a point inside the
        // intersection: "joined through a 0.2 mm neck of Metal1 at (1.204 mm, 3.881 mm)".
        var box = sameLayer && meet is { Count: > 0 } ? DrcRegions.BoundsOf(meet) : pieces[via].Bounds;
        long x = box.IsEmpty ? 0 : (box.MinX + box.MaxX) / 2;
        long y = box.IsEmpty ? 0 : (box.MinY + box.MaxY) / 2;

        return new PieceJoin(
            via, hit, sameLayer ? JoinKind.SameLayerTouch : JoinKind.Via, x, y, pieces[via].Layer);
    }

    /// <summary>
    /// Union-find with path compression and union by size.
    ///
    /// <para>Local rather than shared: the schematic side's own connectivity uses a different
    /// keying (integer grid cells, exact coincidence) and unifying them would couple two
    /// correctness-critical mechanisms that happen to share an algorithm and nothing else.</para>
    /// </summary>
    private sealed class UnionFind
    {
        private readonly int[] _parent;
        private readonly int[] _size;

        public UnionFind(int n)
        {
            _parent = new int[n];
            _size = new int[n];
            for (int i = 0; i < n; i++) { _parent[i] = i; _size[i] = 1; }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];   // path halving
                x = _parent[x];
            }
            return x;
        }

        /// <summary>True where the two sets were distinct and have now been merged — which is what
        /// makes the retained edges a spanning forest (R-lvs2-4c).</summary>
        public bool Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra == rb) return false;
            if (_size[ra] < _size[rb]) (ra, rb) = (rb, ra);
            _parent[rb] = ra;
            _size[ra] += _size[rb];
            return true;
        }
    }
}
