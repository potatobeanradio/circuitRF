// A part on the bottom of the board (owner, 2026-09-24).
//
// ── WHY PADS NEEDED A SIDE AT ALL ──────────────────────────────────────────────────────────────
//
// A pad attaches on its own land's layer (R-rail34-1): a top pad over the rail's far-layer trace is
// NOT that trace. The land came from the footprint cell — the layer its pins are drawn on, the top,
// for every generated footprint — and nothing turned it over for a part fitted to the bottom. So a
// bottom-side capacitor's pads were looked for on top copper, found nothing there, and the part was
// left out as "not on this rail's copper": a decoupling network missing a part, with a note nobody
// reads. The board exporters already read a MIRRORED footprint as a bottom-side one and flip its
// layers (PcbWriter); RailArtwork.PadsFor now does the same, and a row's BoardSide overrides it.
//
// ── TOP AND BOTTOM ARE THE STACKUP'S OUTER COPPER ──────────────────────────────────────────────
//
// The first and last Conductor with a drawing layer. A pad on neither — an inner land, a via's —
// is left where it is: "the bottom" names a solder side, and only outer copper has one.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.RailRf;

/// <summary>Which copper a part's pads are on, top or bottom — see this file's header.</summary>
public static class RailPartSides
{
    /// <summary>
    /// The drawing layers of the stackup's top and bottom copper, or null where it has fewer than two
    /// copper layers — a board with one side has no side to choose.
    /// </summary>
    public static (LayerKey Top, LayerKey Bottom)? OuterCopper(Technology? technology)
    {
        if (technology is null) return null;
        var copper = technology.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Count > 0)
            .ToList();
        if (copper.Count < 2) return null;

        var top = copper[0].DrawingLayers[0];
        var bottom = copper[^1].DrawingLayers[0];
        return top == bottom ? null : (top, bottom);
    }

    /// <summary>The side a land on <paramref name="layer"/> is on, or null where it is not outer copper.</summary>
    public static RailBoardSide? SideOf(LayerKey? layer, (LayerKey Top, LayerKey Bottom) outer) =>
        layer == outer.Top ? RailBoardSide.Top
        : layer == outer.Bottom ? RailBoardSide.Bottom
        : null;

    /// <summary>
    /// The same land turned over — top copper to bottom and bottom to top — for a footprint placed
    /// MIRRORED. Any other layer is returned as it is.
    /// </summary>
    public static LayerKey Flip(LayerKey layer, (LayerKey Top, LayerKey Bottom) outer) =>
        layer == outer.Top ? outer.Bottom : layer == outer.Bottom ? outer.Top : layer;

    /// <summary>
    /// Every side a row of <paramref name="document"/> STATES, by refdes. The first row stating one
    /// wins: the side is a fact about the part on the board, so two rails naming one part name one
    /// side, and a second that disagrees is reported by <see cref="Disagreements"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, RailBoardSide> Stated(RailDocument document)
    {
        var sides = new Dictionary<string, RailBoardSide>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in document.Rails.SelectMany(r => r.Parts))
            if (part.BoardSide is { } side && part.Refdes.Length > 0)
                sides.TryAdd(part.Refdes, side);
        return sides;
    }

    /// <summary>The parts two rails put on different sides — each is a sentence.</summary>
    public static IReadOnlyList<string> Disagreements(RailDocument document)
    {
        var first = Stated(document);
        return [.. document.Rails
            .SelectMany(r => r.Parts.Select(p => (Rail: r.Name, Part: p)))
            .Where(x => x.Part.BoardSide is { } s && first.TryGetValue(x.Part.Refdes, out var f) && f != s)
            .Select(x => $"{x.Part.Refdes} is on the {Word(first[x.Part.Refdes])} on one rail and on the " +
                         $"{Word(x.Part.BoardSide!.Value)} on rail '{x.Rail}'. The first is used; a part is " +
                         "soldered to one side, so correct the other row.")
            .Distinct()];
    }

    /// <summary>
    /// <paramref name="pads"/> with every stated part's lands moved to the copper of its side, and
    /// <paramref name="netPoints"/> moved with them — a net point is a pad's XY and land, and one
    /// left on the old side would seed the walk on copper the part is not soldered to.
    /// </summary>
    /// <remarks>Returns the inputs themselves where the document states no side, which is every
    /// document written before this existed.</remarks>
    public static (IReadOnlyList<PlacedPin> Pads, IReadOnlyList<PdnNetPoint> NetPoints) Apply(
        IReadOnlyList<PlacedPin> pads, IReadOnlyList<PdnNetPoint> netPoints,
        RailDocument document, Technology? technology)
    {
        var stated = Stated(document);
        if (stated.Count == 0 || OuterCopper(technology) is not { } outer) return (pads, netPoints);

        var moved = new Dictionary<(long X, long Y, LayerKey? From), LayerKey>();
        var outPads = new List<PlacedPin>(pads.Count);
        foreach (var pad in pads)
        {
            if (pad.Refdes is { Length: > 0 } r && stated.TryGetValue(r, out var side)
                && (pad.Layer is null || SideOf(pad.Layer, outer) is not null))
            {
                var land = side == RailBoardSide.Top ? outer.Top : outer.Bottom;
                if (pad.Layer != land)
                {
                    moved.TryAdd((pad.X, pad.Y, pad.Layer), land);
                    outPads.Add(pad with { Layer = land });
                    continue;
                }
            }
            outPads.Add(pad);
        }
        if (moved.Count == 0) return (pads, netPoints);

        var outPoints = netPoints
            .Select(p => moved.TryGetValue((p.X, p.Y, p.Layer), out var land) ? p with { Layer = land } : p)
            .ToList();
        return (outPads, outPoints);
    }

    /// <summary>The side a part's pads are on as <paramref name="pads"/> have them — the artwork's
    /// reading, before any row states one — or null where none of its lands is outer copper.</summary>
    public static RailBoardSide? FromPads(string refdes, IReadOnlyList<PlacedPin> pads, Technology? technology)
    {
        if (OuterCopper(technology) is not { } outer) return null;
        foreach (var pad in pads)
            if (string.Equals(pad.Refdes, refdes, StringComparison.OrdinalIgnoreCase)
                && SideOf(pad.Layer, outer) is { } side)
                return side;
        return null;
    }

    private static string Word(RailBoardSide side) => side == RailBoardSide.Top ? "top" : "bottom";
}
