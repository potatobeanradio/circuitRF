// The technology a traced picture is recognised with — brief-img-4-layout-image-to-schematic.md R-im4-1, R-im4-3 (AS D6,
// D7, D8).
//
// The stackup is the user's (D8), but a picture states only what it draws. Two things a technology declares are drawn in
// almost no picture, and the recognition reads both from the TECHNOLOGY:
//
//   - The reference plane. A picture usually shows the top copper only. That is not a board with no ground: it is the case
//     AS-3 already reads when a stackup DECLARES a reference conductor the artwork does not draw — the plane is metal at
//     ground, and every via carried down to it reaches ground (BoardCopper's UndrawnReference; the trace review reads a
//     microstrip over it the same way). So a reference conductor the picture puts nothing on is IMPLIED: the recognition
//     reads a copy of the technology in which it draws on no layer.
//   - Solder mask and paste. Where a technology has them, AS-4 reads land patterns from their openings — and a picture
//     with no mask drawn has no openings, so it would find no part at all. A layer the picture puts nothing on is left out
//     of the copy (the stackup's own conductor and via layers excepted), and AS-4 reads the pads from the copper itself,
//     as it does for a technology with no mask.
//   - The board's edge. Without an outline AS-3 takes the copper's extent for it, and a line lying ALONG that extent —
//     the first line of a board, running beside its bottom edge — is an edge launch at every point. A picture shows a
//     board: where the picture (or the part of it traced) stops is where the board is known to stop. So unless the
//     picture maps a colour to the board outline, the recognition reads the traced frame as the outline, a rectangle
//     on the technology's Outline layer (one added to the copy when it has none) — in the recognition's input only,
//     never in the written layout. A line that runs off the picture is then an edge launch, and one beside its edge
//     is not.
//
// Ground is then as AS D6 orders it: the largest top-side pour — copper wider than any trace the review reads there, its
// own IsWide rule — handed to the ground reading as the point a user would have clicked, joined by every via carried down
// to the plane; else the plane alone, reached through the drill circles, each a via to it (AS D7's VIAGND). A ground the
// user named wins over both. Nothing in AS-3 … AS-6 is told it is reading a picture: the copy is an ordinary technology.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Extraction;

namespace CircuitRF.Design.Layout.Recognition.Image;

/// <summary>What the copy left out.</summary>
/// <param name="Technology">The technology the recognition reads: the user's unchanged when the picture draws on every
/// layer it declares, else the copy.</param>
/// <param name="Implied">The reference conductors implied, by stackup name.</param>
/// <param name="Undrawn">The drawing layers left out, by name.</param>
/// <param name="Shapes">The artwork the recognition reads: the traced shapes, and the frame's outline when it was added.</param>
/// <param name="PourAt">A point on the largest top-side pour, DBU, when ground was read from one.</param>
internal sealed record PictureTechnologyReading(Technology Technology, IReadOnlyList<string> Implied, IReadOnlyList<string> Undrawn,
                                                IReadOnlyList<LayoutShape> Shapes, (long X, long Y)? PourAt);

internal static class PictureTechnology
{
    /// <summary>The name AS-3 reads a board outline from.</summary>
    internal const string OutlineLayerName = "Outline";

    /// <summary>Reads <paramref name="shapes"/> against what <paramref name="technology"/> declares.</summary>
    /// <param name="frame">The traced part of the picture, DBU.</param>
    /// <param name="outlineDrawn">The picture maps a colour to the board outline.</param>
    public static PictureTechnologyReading Of(IReadOnlyList<LayoutShape> shapes, Technology technology, int dbuPerMicron,
                                              RecognitionOptions options, Bbox frame, bool outlineDrawn)
    {
        var copy = TechPersistence.Clone(technology);
        var read = new List<LayoutShape>(shapes);
        if (!outlineDrawn && !frame.IsEmpty)
        {
            var outline = copy.Layers.FirstOrDefault(l => string.Equals(l.Name, OutlineLayerName, StringComparison.OrdinalIgnoreCase));
            if (outline is null)
            {
                outline = new LayerDef { Key = new LayerKey(copy.Layers.Select(l => l.Key.Layer).DefaultIfEmpty(0).Max() + 1, 0), Name = OutlineLayerName };
                copy.Layers.Add(outline);
            }
            read.Add(new RectShape { Layer = outline.Key, X1 = frame.MinX, Y1 = frame.MinY, X2 = frame.MaxX, Y2 = frame.MaxY });
        }

        var drawn = read.Where(s => s is not (BitmapShape or LabelShape)).Select(s => s.Layer).ToHashSet();
        var stackupLayers = copy.Stackup.Layers.SelectMany(l => l.DrawingLayers).ToHashSet();
        var implied = copy.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Conductor && l.IsGroundReference && l.Name.Length > 0 && l.DrawingLayers.Count > 0
                        && !l.DrawingLayers.Any(drawn.Contains))
            .Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList();
        foreach (var l in copy.Stackup.Layers)
            if (l.Kind == StackupKind.Conductor && l.IsGroundReference && implied.Contains(l.Name)) l.DrawingLayers = [];
        var undrawn = copy.Layers.Where(l => !drawn.Contains(l.Key) && !stackupLayers.Contains(l.Key)).ToList();
        copy.Layers = [.. copy.Layers.Except(undrawn)];

        bool named = options.GroundAt is not null || options.GroundNet is { Length: > 0 };
        return new PictureTechnologyReading(copy, implied, [.. undrawn.Select(l => l.Name ?? $"{l.Key.Layer}/{l.Key.Datatype}")], read,
                                            named || implied.Count == 0 ? null : LargestTopPour(read, copy, dbuPerMicron));
    }

    /// <summary>A point on the largest piece of top-side copper wider than any trace the review reads there, or null.</summary>
    private static (long X, long Y)? LargestTopPour(IReadOnlyList<LayoutShape> shapes, Technology tech, int dbu)
    {
        var top = Conductors.Of(tech).Where(c => c.DrawingLayers.Count > 0).Select(c => c.DrawingLayers).FirstOrDefault();
        if (top is null) return null;
        var pieces = CopperPieces.Build(shapes, tech);
        if (!pieces.Any) return null;
        var board = new BoardCopper(pieces, tech, shapes, dbu, TraceImpedanceAnalysis.WidestTraceDbu(tech, shapes, dbu));
        int best = -1;
        double bestArea = 0;
        for (int p = 0; p < pieces.Count; p++)
        {
            if (!board.IsConductor[p] || !top.Contains(pieces.LayerOfPiece(p)) || !board.IsWide(p)) continue;
            double area = board.Area(p);
            if (area > bestArea) (best, bestArea) = (p, area);
        }
        return best < 0 ? null : board.ProbeOf(best);
    }
}
