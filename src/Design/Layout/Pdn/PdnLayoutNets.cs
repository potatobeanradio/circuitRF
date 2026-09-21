// What a pad is CONNECTED TO on a board somebody drew — brief-authored-board-2-net-identity.md.
//
// ── THE RULE, IN ONE LINE (the owner's, 2026-09-20) ────────────────────────────────────────────
//
//     net(pad) = the schematic's own binding for that refdes and that pin, where a schematic
//                resolves; else the net stated on the copper the pad lands on
//
// RE-DERIVED, WITH A STAMPED FALLBACK — DisplayRefDes' own shape, chosen for that record's reason: a
// re-derived value cannot go stale, and the stored one is what a board with no schematic behind it
// needs.
//
// ── NOTHING HERE WRITES `Net`, AND THAT IS THE PART THAT SURPRISES ────────────────────────────
//
// Update Layout does not stamp it. There is no forward annotation, no back annotation and no
// migration. A stamp is a USER'S OWN ACT on a board they drew by hand, exactly as RefDes is for a
// hand-placed instance — and if a schematic later resolves, the schematic wins and the stamp is
// simply not consulted.
//
// ── THE PARTITION IS DrcConnectivity'S, AND THERE IS NO SECOND ONE ────────────────────────────
//
// PdnRailRegions' own header says it and the reasoning carries verbatim: a board whose island
// structure the DRC and railRF disagree about is a bug neither of them reports. So a stated net
// names its WHOLE connected piece through the partition that already exists, which is what buys the
// whole point of the gesture — naming one trace names the pour, the vias and everything they reach,
// and a user does not annotate 80 shapes.
//
// ── IT IS NOT LVS, AND MUST NOT BE NAMED LIKE ONE (R-ab2-5d) ──────────────────────────────────
//
// PdnDivergence below compares TWO FILES THAT BOTH CLAIM TO DESCRIBE ONE BOARD. It extracts no
// device and compares no design. DrcConnectivity's header already draws this line for its own class
// and naming either of them anything LVS-flavoured invites the assumption that they answer a
// question neither asks.

using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>Which of the three claims a board's net names came from — R-ab2-4d.</summary>
/// <remarks>
/// Flags rather than a choice, because a board can legitimately carry an <c>.ipc</c>, resolve a
/// schematic and have copper somebody stamped, all at once. Reporting one of the three as though it
/// were the only one is the false sentence R-ab2-4b is about, in miniature.
/// </remarks>
[Flags]
public enum PdnNetOrigin
{
    /// <summary>Nothing named a net.</summary>
    None = 0,

    /// <summary>The schematic beside the artwork.</summary>
    Schematic = 1,

    /// <summary>A <c>Net</c> a user stamped on the copper itself.</summary>
    Artwork = 2,

    /// <summary>A companion board netlist.</summary>
    BoardNetlist = 4,
}

/// <summary>
/// One divergence between the board netlist and the artwork — R-ab2-5.
///
/// <para><b>Reported, never resolved, never ranked, never a refusal</b> (R-ab2-5c). The run proceeds
/// on R-ab1-3a's precedence. This is a stale export or a hand edit, which are ordinary mid-design
/// states, and a tool that refused to solve on one would be a tool nobody runs mid-design.</para>
/// </summary>
/// <param name="Refdes">The part both files name.</param>
/// <param name="Pin">The pin, for a net disagreement; null where the whole part moved.</param>
/// <param name="Sentence">What to print.</param>
public sealed record PdnDivergence(string Refdes, string? Pin, string Sentence);

/// <summary>
/// What one schematic says about one placed part: its component's ports, in port order, and the net
/// each of them binds.
/// </summary>
/// <param name="PortNames">The ports' own names, in order. Empty entries are ordinary — a primitive
/// declares none — and an empty LIST means nothing named them at all.</param>
/// <param name="Nets">The nets those ports bind, in the same order. <see cref="Instance.NetBindings"/>
/// verbatim.</param>
public sealed record PdnSchematicPart(IReadOnlyList<string> PortNames, IReadOnlyList<string> Nets);

/// <summary>
/// The schematic beside a <c>.clay</c>, extracted — R-ab2-1.
/// </summary>
/// <param name="SchematicPath">Where it was found, or null where the cell holds none.</param>
/// <param name="ByInstance">Keyed on <see cref="Instance.InstanceName"/>, which is what
/// <see cref="LayoutInstance.SchematicId"/> holds. Empty whenever this path contributes nothing.</param>
/// <param name="Notes">What went wrong, if anything. <b>Returned, never posted</b>.</param>
public sealed record PdnSchematicNets(
    string? SchematicPath,
    IReadOnlyDictionary<string, PdnSchematicPart> ByInstance,
    IReadOnlyList<string> Notes)
{
    /// <summary>The answer for a board with no schematic behind it.</summary>
    public static readonly PdnSchematicNets None =
        new(null, new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase), []);

    /// <summary>True once at least one placed part can be looked up here.</summary>
    public bool Any => ByInstance.Count > 0;

    /// <summary>What the schematic says about <paramref name="schematicId"/>, or null.</summary>
    public PdnSchematicPart? For(string? schematicId) =>
        schematicId is { Length: > 0 } id && ByInstance.TryGetValue(id, out var part) ? part : null;

    /// <summary>Every net the schematic named, in a stable order.</summary>
    public IReadOnlyList<string> Nets
    {
        get
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var part in ByInstance.Values)
                foreach (string net in part.Nets)
                    if (net is { Length: > 0 }) set.Add(net);
            return [.. set];
        }
    }

    /// <summary>
    /// The schematic that is this artwork's own sibling, read and extracted.
    /// </summary>
    /// <param name="clayPath">The artwork. <b>The walk starts from here and not from whatever
    /// workspace is open</b> (R-ab2-1a) — the <c>.clay</c> lives in a cell's <c>layout/</c> folder
    /// and its sibling is the same cell's primary schematic. That is the walk <c>RailArtwork</c>
    /// already performs for the technology, and it is the same rule for the same reason: a board
    /// drawn in one workspace answers against the design that drew it, not against whichever
    /// workspace happens to be open.</param>
    /// <remarks>
    /// <b>A <c>.csch</c> reaches this through <see cref="SchematicPersistence"/>, NOT through the
    /// <c>.cnl</c> round trip</b> (R-ab2-1f). That rule — CLAUDE.md's, for <c>check</c> and
    /// <c>explain</c> — is about the ELABORATOR, whose reader disagrees with the schematic's about
    /// bare words. <see cref="NetExtractor"/> takes the edit model directly; it is the front half of
    /// the very round trip in question, and routing it through <c>CnlWriter</c> would be a second
    /// extraction of the thing being extracted.
    ///
    /// <para><b>Conflicts suppress the WHOLE path, not part of it</b> (R-ab2-1e).
    /// <c>ExtractionResult.Conflicts</c> means the extraction disagreed with itself somewhere, and
    /// taking the uncontested half of a contested extraction is how a board ends up half-named with
    /// nothing to say which half.</para>
    /// </remarks>
    public static PdnSchematicNets Resolve(string? clayPath)
    {
        if (clayPath is not { Length: > 0 }) return None;

        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";
        string cellDir = Path.GetDirectoryName(layoutDir) ?? layoutDir;
        if (!Directory.Exists(cellDir)) return None;

        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.ResolvedName is not { Length: > 0 } name) return None;

        string schPath = Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        if (!File.Exists(schPath)) return None;

        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(schPath); }
        catch (Exception ex)
        {
            return new PdnSchematicNets(schPath, None.ByInstance,
                [$"The schematic beside this artwork, '{Path.GetFileName(schPath)}', did not read: " +
                 $"{ex.Message}. Its pads take whatever net is stated on the copper they land on."]);
        }

        NetExtractor.ExtractionResult extracted;
        // DiskCellResolver, never null — Check.cs' own note says why: a null resolver is how
        // NetExtractor is told the caller is flat, and it then skips every cell instance WITHOUT a
        // conflict note. A hierarchical design would come back clean with its parts silently removed.
        try { extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance); }
        catch (Exception ex)
        {
            return new PdnSchematicNets(schPath, None.ByInstance,
                [$"The schematic beside this artwork, '{Path.GetFileName(schPath)}', did not " +
                 $"extract: {ex.Message}. Its pads take whatever net is stated on the copper they " +
                 "land on."]);
        }

        if (extracted.Conflicts.Count > 0)
        {
            var notes = new List<string>
            {
                $"'{Path.GetFileName(schPath)}' extracted with {extracted.Conflicts.Count} naming " +
                "conflict(s), so no pad on this board took a net from it — the uncontested half of " +
                "a contested extraction would leave the board half-named with nothing to say which " +
                "half. Its pads take whatever net is stated on the copper they land on.",
            };
            notes.AddRange(extracted.Conflicts);
            return new PdnSchematicNets(schPath, None.ByInstance, notes);
        }

        var byInstance = new Dictionary<string, PdnSchematicPart>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in extracted.TestBench.Instances)
        {
            if (inst.InstanceName is not { Length: > 0 } who) continue;

            // A CELL's ports are named and a primitive's are not. Both cases are joined by INDEX at
            // the far end (R-ab1-4b), so an empty name list is an answer and not a failure: what
            // matters is that PortNames and Nets are the same length and in the same order.
            var ports = extracted.Library.Find(inst.Reference)?.Ports;
            byInstance[who] = new PdnSchematicPart(
                ports is { Count: > 0 } p && p.Count == inst.NetBindings.Count ? [.. p] : [],
                inst.NetBindings);
        }

        return new PdnSchematicNets(schPath, byInstance, []);
    }
}

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
public sealed class PdnCopperPieces
{
    private readonly IReadOnlyList<DrcNetPiece> _pieces;
    private readonly int[] _pieceOfShape;                 // index into the shapes handed in, -1 = none
    private readonly Dictionary<int, string> _nameOfPiece; // DrcNetPiece.Net -> the stated name

    private PdnCopperPieces(
        IReadOnlyList<DrcNetPiece> pieces, int[] pieceOfShape,
        Dictionary<int, string> nameOfPiece, IReadOnlyList<string> refusals)
    {
        _pieces = pieces;
        _pieceOfShape = pieceOfShape;
        _nameOfPiece = nameOfPiece;
        Refusals = refusals;
    }

    /// <summary>Nothing to partition — no technology, or no copper.</summary>
    public static readonly PdnCopperPieces Empty =
        new([], [], [], []);

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
    public bool Any => _pieces.Count > 0;

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
    public static PdnCopperPieces Build(
        IReadOnlyList<LayoutShape> copper, Technology? tech,
        IReadOnlyList<LayoutShape>? stamps = null)
    {
        ArgumentNullException.ThrowIfNull(copper);
        if (tech is null || copper.Count == 0) return Empty;

        var layerRegions = PdnMeshExtractor.BuildLayerRegions(copper, tech);
        if (layerRegions.Count == 0) return Empty;

        var pieces = DrcConnectivity.Extract(layerRegions, tech);
        if (pieces.Count == 0) return Empty;

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
            if (layer is { } probeLayer) pieceOfShape[i] = PieceAt(pieces, px, py, probeLayer);
        }

        // And what each piece is CALLED, read only off the shapes that may state it.
        foreach (var shape in stamps ?? copper)
        {
            if (shape.Net is not { Length: > 0 } net) continue;

            var (px, py, layer) = ProbePointOf(shape, tech);
            if (layer is not { } probeLayer) continue;

            int piece = PieceAt(pieces, px, py, probeLayer);
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
                refusals.Add(
                    $"One connected piece of copper carries two different net names: '{first.Net}' " +
                    $"at ({first.X}, {first.Y}) and '{net}' at ({px}, {py}), both in DBU. Either the " +
                    "artwork shorts those two nets or one of the labels is wrong. Nothing on that " +
                    "piece took a name.");
            }
        }

        return new PdnCopperPieces(pieces, pieceOfShape, nameOfPiece, refusals);
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
        int piece = PieceAt(_pieces, x, y, layer);
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
    /// <b>A vertex rather than a centroid.</b> <c>PdnRailRegions.Contains</c> clips against a 2 DBU
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

    private static int PieceAt(
        IReadOnlyList<DrcNetPiece> pieces, long x, long y, LayerKey? layer)
    {
        foreach (var piece in pieces)
        {
            if (layer is { } only && piece.Layer != only) continue;
            if (!piece.Bounds.Contains(x, y)) continue;
            if (PdnRailRegions.Contains(piece.Paths, x, y)) return piece.Net;
        }
        return -1;
    }
}

/// <summary>
/// The two files that both claim to describe one board, compared — R-ab2-5. <b>Not LVS</b>; see this
/// file's header.
/// </summary>
public static class PdnBoardDivergence
{
    /// <summary>
    /// Where the board netlist and the artwork name the same refdes and pin and say different
    /// things.
    /// </summary>
    /// <param name="netlistPads">What the <c>.ipc</c> stated.</param>
    /// <param name="artworkPads">What the artwork measures, INCLUDING the parts the netlist also
    /// names — R-ab1-3a discards those pads and this comparison is the reason they are computed at
    /// all.</param>
    /// <param name="extents">Each artwork pad's own extent, DBU. A position difference smaller than
    /// this is a rounding difference between two coordinate systems and is not worth a line
    /// (R-ab2-5b); where the footprint states no extent there is nothing to compare against and no
    /// line is printed.</param>
    public static IReadOnlyList<PdnDivergence> Compare(
        IReadOnlyList<PdnPad> netlistPads,
        IReadOnlyList<PdnPad> artworkPads,
        IReadOnlyDictionary<PdnPad, long>? extents = null)
    {
        ArgumentNullException.ThrowIfNull(netlistPads);
        ArgumentNullException.ThrowIfNull(artworkPads);

        if (netlistPads.Count == 0 || artworkPads.Count == 0) return [];

        var byKey = new Dictionary<(string, string), PdnPad>();
        foreach (var pad in netlistPads)
            if (pad.Refdes is { Length: > 0 } r && pad.Pin is { Length: > 0 } p)
                byKey.TryAdd((r.ToUpperInvariant(), p.ToUpperInvariant()), pad);

        var found = new List<PdnDivergence>();
        var reported = new HashSet<(string, string)>();

        foreach (var art in artworkPads)
        {
            if (art.Refdes is not { Length: > 0 } refdes || art.Pin is not { Length: > 0 } pin) continue;
            var key = (refdes.ToUpperInvariant(), pin.ToUpperInvariant());
            if (!byKey.TryGetValue(key, out var stated)) continue;

            // R-ab2-5a: once per PAIR, not once per pad. An array placement puts several pads on one
            // (refdes, pin) and the netlist states it once; three identical lines say nothing the
            // first did not.
            if (!reported.Add(key)) continue;

            if (art.Net is { Length: > 0 } artNet && stated.Net is { Length: > 0 } statedNet
                && !string.Equals(artNet, statedNet, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(new PdnDivergence(refdes, pin,
                    $"{refdes}.{pin} is on '{statedNet}' according to the board netlist and on " +
                    $"'{artNet}' according to the artwork. The run used the board netlist's, which " +
                    "is the statement of record; this is a stale export or a hand edit, not an error."));
            }

            long extent = extents is not null && extents.TryGetValue(art, out long e) ? e : 0;
            if (extent <= 0) continue;

            double dx = art.X - stated.X, dy = art.Y - stated.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= extent) continue;

            found.Add(new PdnDivergence(refdes, pin,
                $"{refdes}.{pin} stands at ({stated.X}, {stated.Y}) according to the board netlist " +
                $"and at ({art.X}, {art.Y}) according to the artwork — {d:F0} DBU apart, which is " +
                $"more than the pad's own {extent} DBU extent. The run used the board netlist's."));
        }

        return found;
    }
}

/// <summary>How a board's net names read on a status strip — R-ab2-4d.</summary>
/// <remarks>
/// <b>Said once, here</b>, on <c>PdnPadSummary</c>'s terms: the window's strip and the verb's banner
/// report the same board, and two spellings of one claim is the divergence nobody notices until they
/// are compared.
/// </remarks>
public static class PdnNetSummary
{
    /// <summary>The sentence, or empty where nothing named a net.</summary>
    public static string Describe(PdnNetOrigin origin)
    {
        var parts = new List<string>(3);
        if (origin.HasFlag(PdnNetOrigin.BoardNetlist)) parts.Add("from the board netlist");
        if (origin.HasFlag(PdnNetOrigin.Schematic))    parts.Add("from the schematic");
        if (origin.HasFlag(PdnNetOrigin.Artwork))      parts.Add("stated on the artwork");

        return parts.Count switch
        {
            0 => "",
            1 => $"nets {parts[0]}",
            2 => $"nets {parts[0]} and {parts[1]}",
            _ => $"nets {parts[0]}, {parts[1]} and {parts[2]}",
        };
    }
}
