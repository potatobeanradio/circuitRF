// PARTS THAT ARE COPPER — a microstrip line, a bend, a tee, a spiral inductor.
//
// A land pattern's pads are separate pieces of metal, so reading the artwork galvanically puts its
// two terminals on two nets and that is the right answer. A line is ONE piece of metal from end to
// end, so the same reading puts both of its terminals on one net: every series line shorts the two
// nets it sits between, and every open stub shorts its open end to whatever it hangs off. On a
// microstrip board that is most of the design, and the report filled with shorts the artwork does
// not have (field report, 2026-09-24: ten of fourteen errors on a five-line filter).
//
// ── THE RULE ──────────────────────────────────────────────────────────────────────────────────
//
// A placed DEVICE whose own cell joins two of its terminals with its own copper is a body, not
// interconnect. Its copper leaves the partition, and each of its terminals is read at its pin:
//
//   on whatever other copper is under the pin      a pad, a trace, a via — exactly a pad's lookup
//   else on another body's pin at the same point   two lines abutting end to end, a bend, a tee
//   else on a net of its own                       an open end, which is what the schematic says too
//
// Decided per CELL, from the cell's own shapes and pins, so a line placed forty times is asked once.
// A cell that places other cells, or that has a schematic of its own, is never a body — it is a
// design, and its insides are the hierarchy's business (or, under --flat, one graph with the rest).
//
// ── WHAT THIS DOES NOT SEE ────────────────────────────────────────────────────────────────────
//
// A line joins only at its ENDS. A trace that overlaps a line's body without covering an end, or a
// stub that meets a line part-way along with no tee, reads as not connected. The schematic cannot
// draw that junction either, so the comparison reports it; what it cannot do is say "part-way
// along" in so many words.

using System.Linq;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>
/// The root placements whose own copper joins their terminals, and how each of their pins is read
/// once that copper has left the partition — see the file header.
/// </summary>
internal sealed class LayoutReadBodies
{
    /// <summary>How far apart two pins may be and still be one junction, DBU. A rotated placement
    /// can round its pin by one unit; nothing drawn on purpose is two units from a junction.</summary>
    private const long Coincident = 2;

    private readonly Dictionary<int, int> _groupOfPad = [];
    private readonly List<int> _groupPartition = [];   // partition net under the junction, or -1
    private readonly List<int> _groupNet = [];         // LVS net, allocated on first use
    private readonly Dictionary<int, int> _pieceOfPad = [];

    private LayoutReadBodies(HashSet<int> instances, HashSet<string> paths)
    {
        Instances = instances;
        Paths = paths;
    }

    /// <summary>Nothing is a body — every reading is exactly as it was.</summary>
    public static readonly LayoutReadBodies None = new([], []);

    /// <summary>Root instance indices read as bodies.</summary>
    public HashSet<int> Instances { get; }

    /// <summary>Their paths, as <see cref="LayoutDesignFlatten.TaggedShape.InstancePath"/> spells
    /// them — what takes their copper out of the partition.</summary>
    public HashSet<string> Paths { get; }

    public bool Any => Instances.Count > 0;

    /// <summary>
    /// Which placements are bodies. A placement read as a cell of its own (<paramref name="modules"/>)
    /// never is.
    /// </summary>
    public static LayoutReadBodies Find(
        LayoutView view, IReadOnlyList<PlacedCell> placed, IReadOnlyDictionary<int, string> modules,
        Technology? tech)
    {
        if (tech is null) return None;

        var byCell = new Dictionary<string, bool>(StringComparer.Ordinal);
        var instances = new HashSet<int>();
        var paths = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < view.Instances.Count; i++)
        {
            if (modules.ContainsKey(i)) continue;
            if (placed[i] is not { View: { } sub, CellDir: { Length: > 0 } dir }) continue;
            if (!LayoutRead.IsDevice(view.Instances[i], dir, sub)) continue;

            // A cell with a drawing of its own is a design, even when a --flat run reads it as a
            // leaf: its metal is its own interconnect, and flat means ONE graph of all of it.
            if (LvsHierarchy.IsComparableCell(dir)) continue;

            if (!byCell.TryGetValue(dir, out bool body))
                byCell[dir] = body = OwnCopperJoinsTerminals(dir, sub, tech);
            if (!body) continue;

            instances.Add(i);
            paths.Add(LayoutDesignFlatten.PathOf(view.Instances[i], i));
        }

        return instances.Count == 0 ? None : new LayoutReadBodies(instances, paths);
    }

    /// <summary>
    /// Whether the cell's own copper puts two different terminals on one net — read in the cell's
    /// own frame, with its own pins, and nothing of any design it is placed in.
    /// </summary>
    private static bool OwnCopperJoinsTerminals(string cellDir, LayoutView cell, Technology tech)
    {
        if (cell.Instances.Count > 0) return false;

        var map = TerminalMap.ResolveCell(cellDir);
        if (map.Terminals.Count < 2) return false;

        var pins = CellPins.Resolve(cell, tech);
        if (pins.Count < 2) return false;

        var own = CopperPieces.Build(cell.Shapes, tech);
        if (!own.Any) return false;

        var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pins.Count; i++) byKey[PlacedPins.PinKeyOf(pins, i)] = i;

        var portOfNet = new Dictionary<int, int>();
        foreach (var terminal in map.Terminals)
        {
            foreach (string key in terminal.LayoutPins)
            {
                if (!byKey.TryGetValue(key, out int i)) continue;

                int net = own.PieceAt(pins[i].X, pins[i].Y, pins[i].Layer);
                if (net < 0) continue;

                if (portOfNet.TryGetValue(net, out int port) && port != terminal.Port) return true;
                portOfNet[net] = terminal.Port;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads every body pin against the partition that no longer holds any body's copper, and
    /// joins the pins that meet at one point. One lookup per pin, counted as a pad's is.
    /// </summary>
    public void ReadPins(
        IReadOnlyList<PlacedPin> pads, IReadOnlyList<PlacedPinOrigin> origins, CopperPieces pieces,
        LvsHierarchyContext? hierarchy)
    {
        if (!Any) return;

        var mine = new List<int>();
        for (int p = 0; p < origins.Count; p++)
            if (Instances.Contains(origins[p].Instance)) mine.Add(p);

        // Union-find over the pins, bucketed so a board of lines is not every pin against every pin.
        var parent = new Dictionary<int, int>();
        int Root(int p) { while (parent[p] != p) p = parent[p] = parent[parent[p]]; return p; }

        var buckets = new Dictionary<(long, long), List<int>>();
        long cell = Coincident * 2;
        foreach (int p in mine)
        {
            parent[p] = p;
            long bx = Math.DivRem(pads[p].X, cell, out _), by = Math.DivRem(pads[p].Y, cell, out _);

            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            {
                if (!buckets.TryGetValue((bx + dx, by + dy), out var near)) continue;
                foreach (int q in near)
                {
                    if (origins[q].Layer != origins[p].Layer) continue;
                    if (Math.Abs(pads[q].X - pads[p].X) > Coincident
                        || Math.Abs(pads[q].Y - pads[p].Y) > Coincident) continue;
                    parent[Root(p)] = Root(q);
                }
            }

            (buckets.TryGetValue((bx, by), out var own) ? own : buckets[(bx, by)] = []).Add(p);
        }

        var groupOfRoot = new Dictionary<int, int>();
        foreach (int p in mine)
        {
            if (hierarchy is not null) hierarchy.Counters.PinQueries++;
            int piece = pieces.IndexAt(pads[p].X, pads[p].Y, origins[p].Layer);
            _pieceOfPad[p] = piece;

            int root = Root(p);
            if (!groupOfRoot.TryGetValue(root, out int group))
            {
                groupOfRoot[root] = group = _groupPartition.Count;
                _groupPartition.Add(-1);
                _groupNet.Add(-1);
            }
            _groupOfPad[p] = group;

            // The first copper any pin of the junction lands on is the junction's.
            if (piece >= 0 && _groupPartition[group] < 0) _groupPartition[group] = pieces.NetOfPiece(piece);
        }
    }

    /// <summary>
    /// The net a body pin is on, and the partition piece under it (-1 where it met only other
    /// bodies' pins, or nothing). False for a pad that is not a body's.
    /// </summary>
    public bool TryResolve(int pad, LayoutRead.NetTable nets, out int net, out int piece)
    {
        net = -1;
        piece = -1;
        if (!_groupOfPad.TryGetValue(pad, out int group)) return false;

        piece = _pieceOfPad[pad];
        net = _groupPartition[group] >= 0
            ? nets.Of(_groupPartition[group])
            : _groupNet[group] >= 0 ? _groupNet[group] : _groupNet[group] = nets.Open();
        return true;
    }
}
