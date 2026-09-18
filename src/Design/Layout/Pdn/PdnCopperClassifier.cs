// Which copper is trace-shaped and which is not — and, just as much, WHY
// (docs/sonnet-briefs/brief-railrf-4-fast-extractor.md R-rail4-3, railrf.md §2.9 rule 2, §9).
//
// ── THIS IS THE ONE FAILURE MODE THAT WOULD NOT ANNOUNCE ITSELF ────────────────────────────────
//
// §9, in its own words: "a wide supply polygon treated as a trace is OPTIMISTIC, and the number
// looks entirely ordinary." There is no error, no warning and no odd-looking curve — just a drop
// that is smaller than the board's. Every other risk in this design at least produces something a
// reader could notice.
//
// So this file does NOT return a boolean per region. It returns a DECISION WITH ITS REASON, and the
// decision is drawable (brief 8's class tab) and overridable (the overrides live on the
// RailDocument, keyed by region identity, so they survive a re-import — which is exactly when a
// classification would otherwise silently change). §2.9: "Drawing it is what makes the failure mode
// neither silent nor invisible."
//
// ── THE MEASUREMENT, AND WHY IT IS AREA AND PERIMETER ──────────────────────────────────────────
//
// A ribbon of width W and centreline length L — straight, bent, L-shaped, it does not matter — has
// area A = W·L and perimeter P ≈ 2(L + W). Those two equations invert in closed form: L and W are
// the roots of x² − (P/2)·x + A = 0. So one area and one perimeter, both exact from Clipper, give
// the region's equivalent width and equivalent length WITHOUT a medial axis, without an orientation
// and without any assumption that the copper is axis-aligned.
//
// The ratio L/W is then the NUMBER OF SQUARES, which is the same quantity §2.8's whole table is
// built from — so the classification is measured in the units the answer is measured in.
//
// A shape too compact to be a ribbon at all (a disc, a pad, a square pour) makes the discriminant
// NEGATIVE — (P/2)² < 4A — and there is no ribbon reading of it to have. That is not a threshold
// being crossed; it is the equations having no real solution, and it is the cleanest possible
// statement that the shape is not a trace.
//
// ── WHERE THE THRESHOLD COMES FROM ─────────────────────────────────────────────────────────────
//
// See PdnCopperClassifier.TraceSquaresThreshold. It is derived from the size of the term the closed
// form OMITS, not chosen for looking about right.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>What the fast model does with one piece of copper.</summary>
public enum PdnCopperClass
{
    /// <summary>Current fills the conductor's width along its length: the closed form
    /// <c>R = ρ·L/(W·T)</c> applies, and the piece becomes sections between junctions.</summary>
    Trace,

    /// <summary>Current spreads: a pour, a plane, the fan-out under a BGA. Meshed, and coarsely
    /// (§2.9). The closed form has no bounded error here, which is why R-rail4-5's gate requires a
    /// REFUSAL on a pour-dominated path rather than a slightly different number.</summary>
    Spreading,
}

/// <summary>
/// One piece of copper, identified in a way that survives a re-import.
///
/// <para><b>The identity is geometric on purpose.</b> An index into a region list changes the moment
/// the artwork gains a shape; a refdes does not exist for a pour. So a region is named by its drawing
/// layer and by the lowest-then-leftmost vertex of its own outer boundary — a point the geometry
/// itself determines, identical on every re-import of the same artwork, and computed the same way
/// twice because <see cref="DrcRegions.Components"/> is deterministic.</para>
///
/// <para>Copper that MOVED gets a different identity, and that is correct rather than a limitation:
/// an override says "the current through <i>this</i> polygon follows a path", and a polygon that has
/// been re-laid out is not the one the user looked at.</para>
/// </summary>
/// <param name="Layer">The drawing layer the copper is on.</param>
/// <param name="X">The identifying vertex, DBU.</param>
/// <param name="Y">DBU.</param>
public readonly record struct PdnRegionRef(LayerKey Layer, long X, long Y)
{
    /// <summary>How it reads on a report row and in the class tab's readout.</summary>
    public string Describe() => $"layer {Layer.Layer}/{Layer.Datatype} at ({X}, {Y}) DBU";
}

/// <summary>
/// The decision about one piece of copper, and the reason for it.
///
/// <para><b>"Trust me" is not correctable</b> — which is the whole of §2.9's rule 2. The class tab
/// shows <see cref="Reason"/> beside the region, and a region can be forced either way from there; a
/// forced region is drawn differently from an inferred one, because the user needs to see what they
/// have overridden.</para>
/// </summary>
/// <param name="Region">Which piece of copper.</param>
/// <param name="Class">What the fast model does with it.</param>
/// <param name="Reason">Why — the aspect ratio, the width variation, the branch count and the port
/// count on the region, in the region's own units.</param>
/// <param name="Forced">Set where the user forced this region either way.</param>
public sealed record PdnClassification(
    PdnRegionRef Region,
    PdnCopperClass Class,
    string Reason,
    bool Forced)
{
    /// <summary>The class that would have been inferred had nothing forced it. Equal to
    /// <see cref="Class"/> on an inferred region; on a forced one it is what the geometry said, so
    /// the class tab can show both and the user can see what they overrode.</summary>
    public PdnCopperClass Inferred { get; init; }

    /// <summary>The copper itself, so brief 8 draws the decision rather than re-deriving it and
    /// risking a different answer from the one the extraction actually used.</summary>
    public Paths64 Copper { get; init; } = [];

    /// <summary>Its bounds, DBU.</summary>
    public Bbox Bounds { get; init; }

    /// <summary>The equivalent rectangle's length, in metres — see this file's header.</summary>
    public double EquivalentLengthMetres { get; init; }

    /// <summary>The equivalent rectangle's width, in metres.</summary>
    public double EquivalentWidthMetres { get; init; }

    /// <summary>L/W — the number of squares, and the quantity the decision is taken on. Zero where
    /// the shape is too compact to have a ribbon reading at all.</summary>
    public double Squares { get; init; }

    /// <summary>True on the reference conductor rather than the rail's own copper.</summary>
    public bool IsReference { get; init; }
}

/// <summary>Copper in, a decision and its reason out. It measures; it does not extract.</summary>
public static class PdnCopperClassifier
{
    /// <summary>
    /// How many squares a piece must be worth before the closed form is allowed to price it — TEN,
    /// and the number is derived from the term the closed form leaves out.
    ///
    /// <para><c>R = ρ·L/(W·T)</c> assumes the current already fills the width. Where it does not —
    /// at a port, at a via land, at a step in width — there is a constriction term on top, and for a
    /// contact of size <c>d</c> on a conductor of width <c>W</c> it is about
    /// <c>(1/π)·ln(2W/πd)</c> squares: a little under half a square for a pad a quarter of the
    /// width, so about ONE SQUARE for a section with a boundary at each end.</para>
    ///
    /// <para>One square out of ten is 10 %, and out of twenty is 5 % — §7's own gate. Ten is the
    /// point at which the omitted term is small rather than the point at which it vanishes, which is
    /// why the gate that matters is fast-against-accurate on the user's own board (§2.9 rule 4) and
    /// not this constant.</para>
    /// </summary>
    public const double TraceSquaresThreshold = 10.0;

    /// <summary>
    /// Splits <paramref name="copper"/> into its connected pieces and classifies each.
    /// </summary>
    /// <param name="layer">The drawing layer this copper is on.</param>
    /// <param name="copper">Unioned copper on that layer, DBU.</param>
    /// <param name="isReference">True on the reference conductor.</param>
    /// <param name="dbuPerMetre">The artwork's scale.</param>
    /// <param name="overrides">What the user forced, keyed by region identity. A region not named
    /// here is inferred.</param>
    /// <param name="squaresThreshold">See <see cref="TraceSquaresThreshold"/>.</param>
    /// <param name="portsAt">How many attachments land on a given piece — reported in the reason,
    /// because a pour with thirty ports on it reads differently from a trace with two.</param>
    public static List<PdnClassification> Classify(
        LayerKey layer,
        Paths64 copper,
        bool isReference,
        double dbuPerMetre,
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass>? overrides,
        double squaresThreshold,
        Func<Paths64, int>? portsAt = null)
    {
        var result = new List<PdnClassification>();

        foreach (var piece in DrcRegions.Components(copper))
        {
            if (piece.Count == 0) continue;
            result.Add(ClassifyPiece(layer, piece, isReference, dbuPerMetre, overrides,
                                     squaresThreshold, portsAt?.Invoke(piece) ?? 0));
        }

        // Deterministic: by the identifying vertex, so the class tab lists the same regions in the
        // same order twice and an override written on one run keys the same piece on the next.
        result.Sort((a, b) =>
        {
            int byY = a.Region.Y.CompareTo(b.Region.Y);
            return byY != 0 ? byY : a.Region.X.CompareTo(b.Region.X);
        });
        return result;
    }

    /// <summary>The identity of one connected piece — see <see cref="PdnRegionRef"/>.</summary>
    public static PdnRegionRef RefOf(LayerKey layer, Paths64 piece)
    {
        long bx = long.MaxValue, by = long.MaxValue;
        var outer = piece.Count > 0 ? piece[0] : [];
        foreach (var pt in outer)
            if (pt.Y < by || (pt.Y == by && pt.X < bx)) { by = pt.Y; bx = pt.X; }
        return new PdnRegionRef(layer, bx == long.MaxValue ? 0 : bx, by == long.MaxValue ? 0 : by);
    }

    private static PdnClassification ClassifyPiece(
        LayerKey layer, Paths64 piece, bool isReference, double dbuPerMetre,
        IReadOnlyDictionary<PdnRegionRef, PdnCopperClass>? overrides,
        double squaresThreshold, int ports)
    {
        var id = RefOf(layer, piece);

        double areaDbu = Math.Abs(Clipper.Area(piece));
        double perimeterDbu = 0;
        foreach (var ring in piece) perimeterDbu += RingLength(ring);

        // The equivalent rectangle. x² − (P/2)x + A = 0; a negative discriminant means the shape is
        // too compact to be a ribbon at all and there is no L and W to have.
        double half = perimeterDbu / 2.0;
        double disc = half * half - 4.0 * areaDbu;
        bool ribbon = disc >= 0 && areaDbu > 0;

        double lengthDbu = 0, widthDbu = 0, squares = 0;
        if (ribbon)
        {
            double root = Math.Sqrt(disc);
            lengthDbu = (half + root) / 2.0;
            widthDbu = (half - root) / 2.0;
            squares = widthDbu > 0 ? lengthDbu / widthDbu : 0;
        }

        long minFeature = PdnMeshExtractor.MinimumFeatureWidthDbu(piece);
        double variation = minFeature > 0 && widthDbu > 0 ? widthDbu / minFeature : 0;

        var inferred = ribbon && squares >= squaresThreshold
            ? PdnCopperClass.Trace
            : PdnCopperClass.Spreading;

        var chosen = PdnCopperClass.Trace;
        bool forced = overrides is not null && overrides.TryGetValue(id, out chosen);
        var final = forced ? chosen : inferred;

        string measured = ribbon
            ? $"{lengthDbu / dbuPerMetre * 1e3:0.###} mm long by " +
              $"{widthDbu / dbuPerMetre * 1e3:0.###} mm wide from its own area and perimeter — " +
              $"{squares:0.#} squares. Its narrowest copper is " +
              $"{minFeature / dbuPerMetre * 1e3:0.###} mm, so its width varies by {variation:0.##}×. " +
              $"{ports} attachment(s) land on it."
            : $"{areaDbu / (dbuPerMetre * dbuPerMetre) * 1e6:0.##} mm² of copper with a " +
              $"{perimeterDbu / dbuPerMetre * 1e3:0.#} mm perimeter, which is too compact to have a " +
              $"length and a width at all — a ribbon of that area and that perimeter does not exist. " +
              $"{ports} attachment(s) land on it.";

        string verdict = inferred == PdnCopperClass.Trace
            ? $"Trace-shaped: {squares:0.#} squares is at or above the {squaresThreshold:0.#} the " +
              "closed form needs before the constriction it omits is a small term."
            : ribbon
                ? $"Spreading: {squares:0.#} squares is below {squaresThreshold:0.#}, so the current " +
                  "does not fill the width along the length and the closed form would be optimistic."
                : "Spreading: current fans out across it.";

        string reason = forced && final != inferred
            ? $"{measured} {verdict} FORCED to {final.ToString().ToLowerInvariant()} on the class tab, " +
              "so this region is priced against what was stated rather than what was measured."
            : forced
                ? $"{measured} {verdict} Forced to the same class it was inferred as."
                : $"{measured} {verdict}";

        return new PdnClassification(id, final, reason, forced)
        {
            Inferred = inferred,
            Copper = piece,
            Bounds = DrcRegions.BoundsOf(piece),
            EquivalentLengthMetres = lengthDbu / dbuPerMetre,
            EquivalentWidthMetres = widthDbu / dbuPerMetre,
            Squares = squares,
            IsReference = isReference,
        };
    }

    private static double RingLength(Path64 ring)
    {
        double total = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            total += Math.Sqrt(dx * dx + dy * dy);
        }
        return total;
    }
}
