// L8e — D3: PORTS COME FROM THE LAYOUT'S OWN `IsPort` LABELS. THERE IS NO NEW SHAPE TYPE.
//
// The obvious answer is a new shape type, and the code already says why it is wrong. `LabelShape`
// has carried an `IsPort` flag since L0a; it persists, survives copy/paste and flatten, is excluded
// from every boolean operation and from Flatten-to-Polygon, and the Label tool sets it to `false`
// with the comment "port placement belongs with the EM work, not here". `LayoutModel.cs` carries a
// whole paragraph explaining why a port is a label with a flag rather than its own type — a port
// label is TEXT the user sees, whereas a `LayoutPin` is CONNECTIVITY (an edge, with a width and an
// outward direction). THIS FILE IS WHAT THAT PROVISION WAS FOR. Do not add a PortShape.
//
// R-em-1 — framework-free. No Avalonia, no SkiaSharp, no document, no canvas.
//
// R-res-5 — THE SIDE IS INFERRED FROM GEOMETRY, THE INFERENCE IS REPORTED, AND AN AMBIGUOUS PORT IS
// REFUSED BY NAME. A PlanarPort needs to know which END of the conductor it is (PlanarPortSide);
// a label carries only a position. Inferring silently and being wrong reverses the direction of
// current into the structure — a hard π in S₂₁, smooth and plausible and invisible in a magnitude
// plot, which is exactly the failure mode L8d's own D1 warns about. So: infer from the nearest
// conductor boundary, SAY what was inferred, and refuse when two boundaries are equally close.
//
// R-res-4 — nothing here writes to the layout. `.clay`'s schema is untouched; a port label is an
// ordinary LabelShape that already round-trips.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Layout.Em;

/// <summary>Ports resolved from a layout's port labels, or a refusal that names the offending
/// label — the same R-mom-17 shape every other refusal in this area uses.</summary>
public sealed record EmPortExtractionResult(
    IReadOnlyList<PlanarPort> Ports,
    string?                   Refusal,
    IReadOnlyList<string>     Notes)
{
    public bool Ok => Refusal is null && Ports.Count > 0;

    public static EmPortExtractionResult No(string refusal, IEnumerable<string>? notes = null)
        => new([], refusal, notes is null ? [] : [.. notes]);
}

public static class EmPortExtraction
{
    /// <summary>
    /// How close a second conductor boundary has to be, as a fraction of <b>the width of the end the
    /// port sits on</b>, before "which end is this?" stops having one answer.
    ///
    /// <para>A label at the exact corner of a rectangle is equidistant from two edges and is genuinely
    /// ambiguous; a label at the middle of one end is not. Five percent is deliberately tight — the
    /// cost of refusing a placeable port is one nudge, and the cost of guessing wrong is a wrong
    /// answer that looks right.</para>
    ///
    /// <para><b>The fraction was originally taken of the conductor's smaller BOUNDING dimension, and
    /// L8's own phase gate is what caught it.</b> For an L-shaped bend the bounding box is
    /// arm × arm, so on the MMIC starter the threshold came out at 5% of 0.995 mm = 49.8 µm — and a
    /// port at the exact centre of the 72 µm line end is 36 µm from the flanking edge, so a correctly
    /// placed port was refused as ambiguous. The scale has to be LOCAL to the end, not global to the
    /// shape; see <see cref="TryInferSide"/> for how it is estimated.</para>
    /// </summary>
    private const double AmbiguityFraction = 0.05;

    /// <summary>
    /// Port labels → <see cref="PlanarPort"/>s, in port-number order.
    /// </summary>
    /// <param name="shapes">The layout's own shapes. Only <see cref="LabelShape"/>s with
    /// <see cref="LabelShape.IsPort"/> are read; everything else is artwork and is ignored here.</param>
    /// <param name="problem">The already-extracted planar problem — its conductor polygons are what
    /// the side inference measures against, in the SAME metre coordinates
    /// <c>PlanarExtractor</c> produced (no translation, no centring: see that file's header).</param>
    /// <param name="dbuPerMicron">The LAYOUT's own resolution, for the label coordinates.</param>
    /// <param name="z0For">Reference impedance for the port at a given 0-based index in the final
    /// ordered list. <b>The impedance lives in the <c>.cem</c> (<c>EmSetup.PortZ0s</c>), never on the
    /// shape</b> — R-cpl-6's list is already per-port and already additive.</param>
    public static EmPortExtractionResult Extract(
        IReadOnlyList<LayoutShape> shapes,
        PlanarProblem              problem,
        int                        dbuPerMicron,
        Func<int, Complex>?        z0For = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(problem);

        if (dbuPerMicron <= 0)
            return EmPortExtractionResult.No(
                $"The layout's resolution is {dbuPerMicron} DBU per micron, which is not a usable scale.");

        var labels = new List<LabelShape>();
        foreach (var s in shapes)
            if (s is LabelShape { IsPort: true } l) labels.Add(l);

        if (labels.Count == 0)
            return EmPortExtractionResult.No(
                "This layout has no port labels, so the full-wave planar kernel has nothing to " +
                "drive. Use the Port tool to click each conductor end you want a port on — a port " +
                "label is an ordinary label with its port flag set, so it saves, copies and " +
                "flattens like any other.");

        double perDbu = 1.0 / (dbuPerMicron * 1e6);

        // Numbering (D3): a label whose text names a number keeps it; everything else takes the
        // lowest free one, in document order. Auto-numbering from the EXISTING labels is what makes
        // "click an edge, get P1" true without the tool having to know what is already there.
        var explicitNumbers = new Dictionary<int, LabelShape>();
        var pending         = new List<LabelShape>();
        foreach (var l in labels)
        {
            if (TryParseNumber(l.Text, out int n))
            {
                if (explicitNumbers.TryGetValue(n, out var first))
                    return EmPortExtractionResult.No(
                        $"Two port labels both name port {n}: '{first.Text}' at " +
                        $"{Coord(first.X, first.Y, dbuPerMicron)} and '{l.Text}' at " +
                        $"{Coord(l.X, l.Y, dbuPerMicron)}. Port numbers index the s-parameter matrix, " +
                        "so they have to be distinct — renumber one of them.");
                explicitNumbers[n] = l;
            }
            else pending.Add(l);
        }

        int next = 1;
        var numbered = new List<(int Number, LabelShape Label)>(labels.Count);
        foreach (var kv in explicitNumbers) numbered.Add((kv.Key, kv.Value));
        foreach (var l in pending)
        {
            while (explicitNumbers.ContainsKey(next)) next++;
            explicitNumbers[next] = l;
            numbered.Add((next, l));
            next++;
        }
        numbered.Sort((a, b) => a.Number.CompareTo(b.Number));

        // ── Side inference, per label ─────────────────────────────────────────────────────────
        var notes = new List<string>();
        var ports = new List<PlanarPort>(numbered.Count);

        for (int i = 0; i < numbered.Count; i++)
        {
            var (number, label) = numbered[i];
            double x = label.X * perDbu, y = label.Y * perDbu;

            var (poly, level, containing) = NearestPolygon(problem, x, y);
            if (poly is not null && containing > 1)
                return EmPortExtractionResult.No(
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron)} " +
                    $"sits on metal on {containing} of this EM setup's {problem.Layers.Count} conductor " +
                    $"levels (" + string.Join(", ", problem.Layers.Select(l => $"'{l.Name}'")) + "). A " +
                    "port's LEVEL is part of its identity: driving the wrong one drives a different " +
                    "conductor with the same footprint, which produces a complete and plausible " +
                    "answer for a structure that was not drawn. Move the label to a point where only " +
                    "the level you mean carries metal, or narrow this setup's analysis levels.", notes);

            if (poly is null)
                return EmPortExtractionResult.No(
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron)} " +
                    "is not on any conductor the EM setup's signal layer. Move it onto the metal, or " +
                    "check that the artwork it names is on a layer bound to a signal conductor in the " +
                    "technology's stackup.", notes);

            // The label's OWN direction wins when it has one, and it says so in the note. A port
            // placed by the Port tool has carried one since 2026-08-09 (seeded from the artwork,
            // then the user's to rotate); a null one is every .clay written before that field
            // existed, and still means "infer it from the geometry" — the R-res-5 path below,
            // unchanged, ambiguity refusal included.
            PlanarPortSide side;
            bool stated = label.PortDirection is not null;
            if (stated)
            {
                side = SideFromDirection(label.PortDirection!.Value);
            }
            else if (!TryInferSide(poly, x, y, out side, out string? ambiguity))
            {
                return EmPortExtractionResult.No(
                    $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron)} " +
                    $"{ambiguity} Which end of the conductor a port names decides which way current " +
                    "flows into the structure, so it is never guessed. Rotate the port to point the " +
                    "way current should flow into the structure, or move the label to the middle of " +
                    "the conductor end you mean, clear of the corner.", notes);
            }

            var z0 = z0For?.Invoke(i) ?? new Complex(50, 0);
            ports.Add(new PlanarPort(number, new EmPoint(x, y), side, z0,
                                     problem.Layers.Count > 1 ? level : null));

            notes.Add(
                $"Port {number} ('{Describe(label)}') at {Coord(label.X, label.Y, dbuPerMicron)} was " +
                $"taken to be on the conductor's {SideName(side)} end " +
                (stated ? "(the port's own direction)" : "(inferred from the nearest conductor boundary)") +
                (problem.Layers.Count > 1 ? $" of level {level} ('{problem.Layers[level].Name}')" : "") +
                $", driving current {CurrentDirection(side)}, at " +
                $"{FormatOhms(z0)}. The de-embedding reference plane is fixed one mesh cell in from " +
                "the drawn metal edge and is not adjustable.");
        }

        return new EmPortExtractionResult(ports, null, notes);
    }

    // ── Numbering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accepts <c>1</c>, <c>P1</c>, <c>p1</c>, <c>#1</c>, <c>Port 1</c>. Anything else is a name
    /// rather than a number and is auto-numbered instead — a user who labels a port "gate" gets a
    /// port, not a refusal.
    /// </summary>
    internal static bool TryParseNumber(string? text, out int number)
    {
        number = 0;
        if (text is null) return false;

        string t = text.Trim();
        if (t.StartsWith("port", StringComparison.OrdinalIgnoreCase)) t = t[4..].TrimStart();
        else if (t.Length > 0 && (t[0] is 'P' or 'p' or '#'))         t = t[1..].TrimStart();

        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
               && number > 0;
    }

    // ── Side inference ────────────────────────────────────────────────────────────────────────

    /// <summary>The conductor polygon the label sits in, or — when it sits in none — the nearest one
    /// by bounding box. Null when the geometry is empty.</summary>
    /// <summary>
    /// The conductor a port label names, and — L9d/D2 — WHICH LEVEL it is on.
    ///
    /// <para>A port's level is part of its identity, and with more than one level a label can sit on
    /// metal on several of them. The level is inferred from the polygon the label actually lands on
    /// rather than from the label's own drawing layer, because a port label is annotation and is
    /// routinely drawn on a marker layer that names no conductor at all. Landing on more than one
    /// level is genuinely ambiguous and is reported here rather than picked.</para>
    /// </summary>
    private static (PlanarPolygon? Poly, int Level, int Containing) NearestPolygon(
        PlanarProblem problem, double x, double y)
    {
        PlanarPolygon? best = null, contained = null;
        int bestLevel = 0, containedLevel = 0, containing = 0;
        double bestD = double.PositiveInfinity;

        for (int li = 0; li < problem.Layers.Count; li++)
            foreach (var p in problem.Layers[li].Polygons)
            {
                if (p.Contains(x, y))
                {
                    containing++;
                    if (contained is null) { contained = p; containedLevel = li; }
                    continue;
                }
                var (x0, y0, x1, y1) = p.Bounds();
                double dx = x < x0 ? x0 - x : x > x1 ? x - x1 : 0;
                double dy = y < y0 ? y0 - y : y > y1 ? y - y1 : 0;
                double d  = Math.Sqrt(dx * dx + dy * dy);
                if (d < bestD) { bestD = d; best = p; bestLevel = li; }
            }

        if (contained is not null) return (contained, containedLevel, containing);
        if (best is null) return (null, 0, 0);

        // A label a long way off the metal is not "nearly on" it. The mesher would place the port on
        // whatever cell the transverse coordinate happens to fall in, which is a silent wrong answer.
        var (bx0, by0, bx1, by1) = best.Bounds();
        double reach = 0.5 * Math.Min(bx1 - bx0, by1 - by0);
        return bestD <= Math.Max(reach, 0) ? (best, bestLevel, 0) : (null, 0, 0);
    }

    /// <summary>
    /// Which end of the conductor the label names, from its distance to the four sides of that
    /// conductor's own bounding box. Returns false — with the ambiguity worded — when the two
    /// nearest sides are within <see cref="AmbiguityFraction"/> of each other.
    /// </summary>
    internal static bool TryInferSide(PlanarPolygon poly, double x, double y,
                                      out PlanarPortSide side, out string? ambiguity)
    {
        var (x0, y0, x1, y1) = poly.Bounds();

        Span<double> d =
        [
            Math.Abs(x - x0),   // MinX
            Math.Abs(x1 - x),   // MaxX
            Math.Abs(y - y0),   // MinY
            Math.Abs(y1 - y),   // MaxY
        ];
        PlanarPortSide[] sides =
            [PlanarPortSide.MinX, PlanarPortSide.MaxX, PlanarPortSide.MinY, PlanarPortSide.MaxY];

        int best = 0, runner = -1;
        for (int i = 1; i < 4; i++) if (d[i] < d[best]) best = i;
        for (int i = 0; i < 4; i++) if (i != best && (runner < 0 || d[i] < d[runner])) runner = i;

        // The scale is the width of the END the port sits on, not the size of the whole conductor:
        // twice the label's distance to the nearer of the two edges FLANKING the chosen side. For a
        // port at the centre of a line end that is exactly the line width, which is the dimension the
        // question "is this at a corner?" is actually asked in. A label sitting exactly on a flanking
        // edge gives a scale of zero — it IS at a corner — and falls through to the tie below.
        double flankA = best <= 1 ? d[2] : d[0];        // best is MinX/MaxX → flanks are MinY/MaxY
        double flankB = best <= 1 ? d[3] : d[1];
        double scale  = 2.0 * Math.Min(flankA, flankB);
        double tie    = AmbiguityFraction * scale;

        if (d[runner] - d[best] <= tie)
        {
            side = sides[best];
            ambiguity =
                $"sits about equally close to the conductor's {SideName(sides[best])} and " +
                $"{SideName(sides[runner])} edges, so which end it names is ambiguous.";
            return false;
        }

        side = sides[best];
        ambiguity = null;
        return true;
    }

    /// <summary>
    /// A port's stated DIRECTION — the way current flows INTO the structure — is the same quantity
    /// <see cref="PlanarPortSide"/> carries, expressed the way a user points at it. A port whose
    /// current flows +x̂ sits on the conductor's LOW-x end, so the two are inverses of each other and
    /// this is the one place that inversion is written down.
    /// </summary>
    internal static PlanarPortSide SideFromDirection(LayoutRotation direction) => direction switch
    {
        LayoutRotation.R0   => PlanarPortSide.MinX,   // current +x̂ -> low-x end
        LayoutRotation.R90  => PlanarPortSide.MinY,   // current +ŷ -> low-y end
        LayoutRotation.R180 => PlanarPortSide.MaxX,   // current −x̂ -> high-x end
        _                   => PlanarPortSide.MaxY,   // current −ŷ -> high-y end
    };

    private static string SideName(PlanarPortSide s) => s switch
    {
        PlanarPortSide.MinX => "low-x (left)",
        PlanarPortSide.MaxX => "high-x (right)",
        PlanarPortSide.MinY => "low-y (bottom)",
        _                   => "high-y (top)",
    };

    private static string CurrentDirection(PlanarPortSide s) => s switch
    {
        PlanarPortSide.MinX => "in the +x direction",
        PlanarPortSide.MaxX => "in the −x direction",
        PlanarPortSide.MinY => "in the +y direction",
        _                   => "in the −y direction",
    };

    private static string Describe(LabelShape l) => l.Text is { Length: > 0 } t ? t : "unnamed";

    private static string Coord(long x, long y, int dbuPerMicron)
        => $"({LayoutUnits.Format(x, LayoutUnit.Um, dbuPerMicron)}, " +
           $"{LayoutUnits.Format(y, LayoutUnit.Um, dbuPerMicron)} µm)";

    private static string FormatOhms(Complex z)
        => z.Imaginary == 0
            ? $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)} Ω"
            : $"{z.Real.ToString("G6", CultureInfo.InvariantCulture)}" +
              $"{(z.Imaginary < 0 ? "-" : "+")}" +
              $"{Math.Abs(z.Imaginary).ToString("G6", CultureInfo.InvariantCulture)}j Ω";
}
