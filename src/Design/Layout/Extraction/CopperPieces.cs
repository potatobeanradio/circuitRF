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
public sealed class CopperPieces
{
    private readonly PieceIndex _index;
    private readonly int[] _pieceOfShape;                 // index into the shapes handed in, -1 = none
    private readonly Dictionary<int, string> _nameOfPiece; // DrcNetPiece.Net -> the stated name

    private CopperPieces(
        PieceIndex index, int pieceCount, int[] pieceOfShape,
        Dictionary<int, string> nameOfPiece, IReadOnlyList<string> refusals)
    {
        _index = index;
        Count = pieceCount;
        _pieceOfShape = pieceOfShape;
        _nameOfPiece = nameOfPiece;
        Refusals = refusals;
    }

    /// <summary>Nothing to partition — no technology, or no copper.</summary>
    public static readonly CopperPieces Empty =
        new(new PieceIndex([]), 0, [], [], []);

    /// <summary>How many galvanically-joined pieces the partition holds.</summary>
    public int Count { get; }

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

        var pieces = DrcConnectivity.Extract(layerRegions, tech);
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

        return new CopperPieces(index, pieces.Count, pieceOfShape, nameOfPiece, refusals);
    }

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
