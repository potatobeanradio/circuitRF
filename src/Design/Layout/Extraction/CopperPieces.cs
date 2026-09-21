// The board's copper, partitioned and joined to whatever names its shapes state —
// brief-authored-board-2-net-identity.md R-ab2-2, promoted by brief-lvs-2-shared-extraction.md
// R-lvs2-1.
//
// Was `PdnCopperPieces` in Layout/Pdn/PdnLayoutNets.cs. It is the same partition, read by the same
// walk, with two additions: a broad phase in front of the point lookup (R-lvs2-3, PieceIndex) and
// a namespace that does not belong to one of its readers.
//
// ── THE PARTITION IS DrcConnectivity'S, AND THERE IS NO SECOND ONE ────────────────────────────
//
// Regions' own header says it and the reasoning carries verbatim: a board whose island structure
// the DRC, railRF and LVS disagree about is a bug none of them reports. So a stated net names its
// WHOLE connected piece through the partition that already exists, which is what buys the whole
// point of the gesture — naming one trace names the pour, the vias and everything they reach, and
// a user does not annotate 80 shapes.
//
// ── IT IS NOT LVS (R-ab2-5d) ──────────────────────────────────────────────────────────────────
//
// Nothing here extracts a device or compares a design. Being in the namespace LVS reads through
// does not make it LVS: this answers "which copper is joined, and what is it called", which is the
// input to LVS and to railRF alike.

using Clipper2Lib;
using System.Linq;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// The board's copper, partitioned into electrically-joined pieces and joined to whatever names its
/// shapes state — R-ab2-2.
/// </summary>
/// <remarks>
/// <b>A stated net names its whole connected piece.</b> <c>DrcConnectivity.Extract</c> answers
/// <i>which shapes are electrically joined</i> from geometry and the stackup, with integer indices
/// and no names; <c>PdnRailRegions</c> already joins that partition to a name from a net POINT. This
/// joins it to a name from a SHAPE, which is the same move from the other side.
/// </remarks>
/// <summary>
/// What the stackup's ground reference contributed to a <see cref="CopperPieces"/> partition —
/// <c>brief-lvs-3-layout-netlist.md</c> R-lvs3-6, and the public face of <c>DrcConnectivity</c>'s
/// own <c>GroundReach</c>.
/// </summary>
/// <remarks>
/// <b>Only an UNDRAWN reference is inferred</b>, and the design note's R-lvs-15 had to be narrowed
/// to get there — three of the four shipped PCB technologies flag their bottom copper, which is a
/// routing layer. <c>DrcConnectivity.GroundReach</c>'s own remarks carry the reasoning; it is not
/// repeated here, because a rule stated twice is a rule that drifts.
/// </remarks>
/// <param name="ReferenceName">The flagged conductor, or null when the technology flags none.</param>
/// <param name="ReferenceDraws">Whether it has drawing layers. True means nothing was inferred.</param>
/// <param name="Nets">The net indices read as reaching it.</param>
/// <param name="ViasReached">How many via barrels reached an undrawn reference — R-lvs3-6e's count.</param>
public readonly record struct GroundReading(
    string? ReferenceName, bool ReferenceDraws, IReadOnlySet<int> Nets, int ViasReached)
{
    /// <summary>Nothing to report.</summary>
    public static readonly GroundReading None =
        new(null, ReferenceDraws: false, new HashSet<int>(), 0);

    /// <summary>Whether this run relied on metal the artwork does not draw — R-lvs3-6e's
    /// condition, asked in one place.</summary>
    public bool ReliesOnUndrawnMetal => ReferenceName is not null && !ReferenceDraws && ViasReached > 0;
}

public sealed class CopperPieces
{
    private readonly PieceIndex _index;
    private readonly IReadOnlyList<DrcNetPiece> _pieces;
    private readonly int[] _pieceOfShape;                 // index into the shapes handed in, -1 = none
    private readonly Dictionary<int, string> _nameOfPiece; // DrcNetPiece.Net -> the stated name

    private readonly GroundReading _ground;

    private CopperPieces(
        PieceIndex index, IReadOnlyList<DrcNetPiece> pieces, int[] pieceOfShape,
        Dictionary<int, string> nameOfPiece, IReadOnlyList<string> refusals,
        GroundReading ground, IReadOnlyList<PieceJoin> joins)
    {
        _index = index;
        _pieces = pieces;
        Count = pieces.Count;
        _pieceOfShape = pieceOfShape;
        _nameOfPiece = nameOfPiece;
        Refusals = refusals;
        _ground = ground;
        Joins = joins;
    }

    /// <summary>Nothing to partition — no technology, or no copper.</summary>
    public static readonly CopperPieces Empty =
        new(new PieceIndex([]), [], [], [], [], GroundReading.None, []);

    /// <summary>
    /// The spanning forest the partition was built from — R-lvs2-4, <b>retained rather than
    /// recomputed</b>, because it is the only thing that can say WHY two pins are one net
    /// (<c>brief-lvs-8-findings.md</c> R-lvs8-4a).
    /// </summary>
    /// <remarks>
    /// <b>Indices are PIECES, not nets.</b> <see cref="PieceAt"/> answers with a net because that
    /// is what every existing reader asks for; <see cref="IndexAt"/> is the same lookup answering
    /// with the node these edges connect.
    /// </remarks>
    public IReadOnlyList<PieceJoin> Joins { get; }

    /// <summary>How many galvanically-joined pieces the partition holds.</summary>
    public int Count { get; }

    /// <summary>
    /// How many point-in-piece lookups this partition has answered — <c>brief-lvs-9-hierarchy.md</c>
    /// R-lvs9-5a, and <c>PieceIndex.Queries</c> exposed so a gate can read it.
    /// </summary>
    /// <remarks>
    /// <b>It counts every lookup, not only a pin's.</b> The build itself locates each shape and each
    /// stamp, so this is always larger than the pin count; what it is for is the shape of the growth,
    /// which is what an accidental per-shape probe would change.
    /// </remarks>
    public int Lookups => _index.Queries;

    /// <summary>Every piece on one drawing layer, ascending — the narrow phase
    /// <c>SubCellContact</c> intersects against, so it never unions the parent's copper a second
    /// time.</summary>
    internal IEnumerable<int> PiecesOn(LayerKey layer)
    {
        for (int i = 0; i < _pieces.Count; i++) if (_pieces[i].Layer == layer) yield return i;
    }

    /// <summary>
    /// <b>Two names on one piece of metal, named and located</b> (R-ab2-2c). Either the artwork
    /// shorts two nets or one of the labels is wrong, and both readings are things a user must see —
    /// so that piece contributes NO name at all. Picking one (first, longest, most frequent) produces
    /// a plausible board and buries a short.
    /// </summary>
    public IReadOnlyList<string> Refusals { get; }

    /// <summary>Every distinct name stated on this board's copper, in a stable order.</summary>
    public IReadOnlyList<string> Nets =>
        [.. _nameOfPiece.Values.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>True once the partition has something in it.</summary>
    public bool Any => Count > 0;

    /// <summary>
    /// Partitions <paramref name="copper"/> and reads the names its shapes state.
    /// </summary>
    /// <param name="copper">Everything electrically present. For railRF that is the FLATTENED
    /// artwork — a board whose parts are footprint cells keeps every land inside an instance, and a
    /// pad that landed on nothing would take no name at all.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <param name="stamps">
    /// The only shapes whose <see cref="LayoutShape.Net"/> is READ. Null means <paramref name="copper"/>
    /// itself.
    ///
    /// <para><b>A separate LIST rather than a filter over <paramref name="copper"/></b>, because a
    /// flatten hands back CLONES of the root's own shapes and a reference test against them finds
    /// nothing — silently, which is the whole failure mode this brief is about. Each stamp is
    /// located in the partition by its own probe point instead, which is exact: a root shape is in
    /// the root's coordinate frame either way.</para>
    ///
    /// <para><b>This is the trap and it is worth being explicit</b> (R-ab2-2a): a land pattern is ONE
    /// CELL SHARED BY EVERY PLACEMENT OF IT, so a <c>Net</c> stamped inside a <c>C0402</c>'s
    /// <c>.clay</c> would put thirteen capacitors on one net. A sub-cell's pad shapes carry
    /// <c>Pin</c> and that is all they may carry — pin is a property of the pattern, net is a
    /// property of the board.</para>
    /// </param>
    public static CopperPieces Build(
        IReadOnlyList<LayoutShape> copper, Technology? tech,
        IReadOnlyList<LayoutShape>? stamps = null, RailRf.RailLengthFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(copper);
        if (tech is null || copper.Count == 0) return Empty;

        var layerRegions = LayerRegions.Build(copper, tech);
        if (layerRegions.Count == 0) return Empty;

        var pieces = DrcConnectivity.ExtractWithGround(layerRegions, tech, out var reach, out var joins);
        if (pieces.Count == 0) return Empty;

        // R-lvs2-3. Built once per run and dropped with the answer — never maintained
        // incrementally, for WirePairSweep's reason.
        var index = new PieceIndex(pieces);

        var pieceOfShape = new int[copper.Count];
        Array.Fill(pieceOfShape, -1);

        // Where a piece's name came from, so the refusal can say WHERE the two labels are rather
        // than only that they disagree.
        var stated = new Dictionary<int, (string Net, long X, long Y)>();
        var refusals = new List<string>();
        var nameOfPiece = new Dictionary<int, string>();
        var contested = new HashSet<int>();

        // Where every shape of `copper` sits — what ShapesJoinedTo and NameOfShape are indexed by.
        for (int i = 0; i < copper.Count; i++)
        {
            var (px, py, layer) = ProbePointOf(copper[i], tech);
            if (layer is { } probeLayer) pieceOfShape[i] = index.PieceAt(px, py, probeLayer);
        }

        // And what each piece is CALLED, read only off the shapes that may state it.
        foreach (var shape in stamps ?? copper)
        {
            if (shape.Net is not { Length: > 0 } net) continue;

            var (px, py, layer) = ProbePointOf(shape, tech);
            if (layer is not { } probeLayer) continue;

            int piece = index.PieceAt(px, py, probeLayer);
            if (piece < 0) continue;

            if (!stated.TryGetValue(piece, out var first))
            {
                stated[piece] = (net, px, py);
                nameOfPiece[piece] = net;
                continue;
            }

            if (string.Equals(first.Net, net, StringComparison.OrdinalIgnoreCase)) continue;

            // R-ab2-2c. Same class as the Excellon suppression refusal: the two readings differ by
            // something nothing downstream can detect, so nothing here picks one. It is scoped to
            // the PIECE rather than to the board — the rest of the artwork is unaffected by a short
            // in one corner of it, and a refusal that hid every other correct name would be one a
            // user could not act on.
            if (contested.Add(piece))
            {
                nameOfPiece.Remove(piece);
                var fmt = format ?? RailRf.RailLengthFormat.Dbu;
                refusals.Add(
                    $"One connected piece of copper carries two different net names: '{first.Net}' " +
                    $"at {fmt.Point(first.X, first.Y)} and '{net}' at {fmt.Point(px, py)}. Either " +
                    "the artwork shorts those two nets or one of the labels is wrong. Nothing on " +
                    "that piece took a name.");
            }
        }

        return new CopperPieces(
            index, pieces, pieceOfShape, nameOfPiece, refusals,
            new GroundReading(reach.ReferenceName, reach.ReferenceDraws, reach.Nets, reach.ViasReached),
            joins);
    }

    /// <summary>
    /// The <b>piece</b> covering (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/>, or <c>-1</c> — the node a <see cref="PieceJoin"/> names, where
    /// <see cref="PieceAt"/> gives the net it belongs to.
    /// </summary>
    public int IndexAt(long x, long y, LayerKey? layer) => _index.IndexAt(x, y, layer);

    /// <summary>Which drawing layer a piece is on.</summary>
    public LayerKey LayerOfPiece(int piece) => _pieces[piece].Layer;

    /// <summary>A piece's bounding box, DBU.</summary>
    public Bbox BoundsOfPiece(int piece) => _pieces[piece].Bounds;

    /// <summary>The net a piece belongs to — the number <see cref="PieceAt"/> answers with.</summary>
    public int NetOfPiece(int piece) => _pieces[piece].Net;

    /// <summary>A piece's geometry. Internal because <c>Paths64</c> is Clipper2's and the
    /// partition does not put it on this project's public face.</summary>
    internal Paths64 PathsOfPiece(int piece) => _pieces[piece].Paths;

    /// <summary>
    /// The <b>net index</b> of the piece covering (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/>, or <c>-1</c> where no piece does — R-lvs3-5c.
    /// </summary>
    /// <remarks>
    /// <b>Identity, where <see cref="NameAt"/> is a name.</b> Unnamed copper is still a net, and it
    /// is the ordinary case on artwork nobody has stamped — so the reading that decides whether two
    /// terminals are connected has to be this one. It is the same number
    /// <see cref="IsGround(int)"/> and <see cref="NameAt"/> are keyed on.
    /// </remarks>
    /// <param name="layer">R-ab2-2d, exactly as <see cref="NameAt"/>: a pin lands on the piece under
    /// it ON ITS OWN LAYER. Null searches every layer, which is what a via wants.</param>
    public int PieceAt(long x, long y, LayerKey? layer) => _index.PieceAt(x, y, layer);

    /// <summary>The name stated on the net at <paramref name="netIndex"/>, or null where nothing
    /// named it — which is the ordinary case on artwork nobody has stamped. The same answer
    /// <see cref="NameAt"/> gives, asked by net rather than by point.</summary>
    public string? NameOfNet(int netIndex) =>
        netIndex >= 0 && _nameOfPiece.TryGetValue(netIndex, out string? net) ? net : null;

    /// <summary>What the stackup's ground reference contributed — R-lvs3-6.</summary>
    public GroundReading Ground => _ground;

    /// <summary>Whether the net at <paramref name="netIndex"/> reaches the stackup's ground
    /// reference. False for every net on a technology that flags none, and false for every net on
    /// one whose reference DRAWS — see <see cref="GroundReading"/>.</summary>
    public bool IsGround(int netIndex) => netIndex >= 0 && _ground.Nets.Contains(netIndex);

    /// <summary>
    /// The net of the piece covering (<paramref name="x"/>, <paramref name="y"/>) on
    /// <paramref name="layer"/>, or null.
    /// </summary>
    /// <param name="layer">R-ab2-2d: a pad takes the name of the piece it lands on, ON ITS OWN
    /// LAYER. Null searches every layer, which is what a via wants — a via IS the thing that joins
    /// them, so restricting it to one is asking the wrong question.</param>
    public string? NameAt(long x, long y, LayerKey? layer)
    {
        int piece = _index.PieceAt(x, y, layer);
        return piece >= 0 && _nameOfPiece.TryGetValue(piece, out string? net) ? net : null;
    }

    /// <summary>The net stated on, or reached by, the shape at <paramref name="shapeIndex"/>.</summary>
    public string? NameOfShape(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < _pieceOfShape.Length
        && _pieceOfShape[shapeIndex] is var p and >= 0
        && _nameOfPiece.TryGetValue(p, out string? net) ? net : null;

    /// <summary>
    /// Every shape galvanically joined to any of <paramref name="shapeIndices"/>, themselves
    /// included — what "names this piece and the 34 shapes joined to it" is counted from
    /// (R-ab2-3c).
    /// </summary>
    public IReadOnlyList<int> ShapesJoinedTo(IEnumerable<int> shapeIndices)
    {
        ArgumentNullException.ThrowIfNull(shapeIndices);

        var wanted = new HashSet<int>();
        var loose = new List<int>();
        foreach (int i in shapeIndices)
        {
            if (i < 0 || i >= _pieceOfShape.Length) continue;
            if (_pieceOfShape[i] >= 0) wanted.Add(_pieceOfShape[i]);
            else loose.Add(i);          // not on any piece — it still names itself
        }

        var reached = new List<int>();
        for (int i = 0; i < _pieceOfShape.Length; i++)
            if (_pieceOfShape[i] >= 0 && wanted.Contains(_pieceOfShape[i])) reached.Add(i);

        foreach (int i in loose) if (!reached.Contains(i)) reached.Add(i);
        reached.Sort();
        return reached;
    }

    /// <summary>The distinct names already carried by the pieces <paramref name="shapeIndices"/>
    /// sit on — R-ab2-3d's "asks, naming the existing one".</summary>
    public IReadOnlyList<string> NamesOn(IEnumerable<int> shapeIndices)
    {
        ArgumentNullException.ThrowIfNull(shapeIndices);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (int i in shapeIndices)
            if (NameOfShape(i) is { Length: > 0 } net) names.Add(net);
        return [.. names];
    }

    /// <summary>
    /// A point that is certainly ON <paramref name="shape"/>, and the layer it is on — or a null
    /// layer where the shape contributes no checkable geometry at all.
    /// </summary>
    /// <remarks>
    /// <b>A vertex rather than a centroid.</b> <c>Regions.Contains</c> clips against a 2 DBU
    /// square straddling the point, so a point ON the boundary still meets the piece — the property
    /// that file's own note records for a drilled via centre — and it makes this work for every
    /// shape kind with no per-kind arithmetic at all.
    ///
    /// <para>The FIRST contribution, for a <see cref="ViaShape"/>, is its barrel on its own layer.
    /// The barrel and its landing pad are the same piece by construction, so which one is probed
    /// does not matter.</para>
    /// </remarks>
    private static (long X, long Y, LayerKey? Layer) ProbePointOf(LayoutShape shape, Technology tech)
    {
        if (!DrcRegions.IsCheckable(shape)) return (0, 0, null);

        long px = 0, py = 0;
        LayerKey? found = null;
        DrcRegions.Expand(shape, tech, _ => long.MaxValue, (layer, _, paths) =>
        {
            if (found is not null || paths.Count == 0 || paths[0].Count == 0) return;
            px = paths[0][0].X; py = paths[0][0].Y; found = layer;
        });
        return (px, py, found);
    }

}
