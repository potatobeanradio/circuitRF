namespace CircuitRF.WBond;

/// <summary>
/// One control point of a loop shape, in <b>normalised</b> coordinates.
/// </summary>
/// <param name="Span">Position along the chord, 0 at the input foot and 1 at the output foot.</param>
/// <param name="Height">
/// Height above the chord, normalised so the peak is 1. Both feet are 0, which is what makes them
/// immovable under height scaling (WB24a).
/// </param>
public readonly record struct ShapePoint(double Span, double Height);

/// <summary>
/// The arithmetic of an arched wire — <b>and nothing else</b> (wbond.md §6.2, revised 2026-08-18).
///
/// <h3>There is no stored shape object, and that is the point</h3>
/// <para>This class replaced <c>LoopProfile</c>, a NAMED shape that wires bound to. Owner,
/// 2026-08-18: <i>"User would never share one loop shape for multiple arrays and want to edit it in
/// one place. Each array is generally its own shape and I want flexibility for user to change each
/// wire within the array."</i> Shared-shape propagation was the only thing a binding bought, so the
/// object had no remaining purpose and the designation — ball versus wedge — was only ever a seed.
/// <b>A wire's <see cref="Wire.Points"/> are the only truth about its shape</b> (D1), in the model,
/// in the file and in the UI.</para>
///
/// <para>Everything here is therefore stateless: <see cref="Seed"/> makes an arch,
/// <see cref="Read"/> measures one back off a wire, <see cref="Write"/> stamps one onto a wire, and
/// nothing is stored between those calls.</para>
///
/// <h3>Why the shape is normalised</h3>
/// <para>Storing height against <b>normalised span</b> rather than against absolute position is what
/// makes wire angle and wire length stop being shape differences at all. A wire at 37° and a wire
/// at 90°, 60 mil and 140 mil long, have the same <i>loop</i> if they have the same z-vs-span
/// shape — which is exactly what a packaging engineer means by "the same loop". The only genuine
/// odd-balls left are differing point counts and XY backtracking.</para>
///
/// <h3>Why the feet cannot move</h3>
/// <para>Height is measured <b>above the chord</b>, not above a flat baseline. The endpoints have
/// height 0, so scaling multiplies them by zero and they provably stay put — which matters because in
/// chip-and-wire the two feet are usually at different z (die surface to package lead), and scaling
/// about a single baseline would drag one foot off its pad.</para>
/// </summary>
public static class LoopShape
{
    /// <summary>
    /// Where the seed arch crests, as a fraction of the span.
    ///
    /// <para>0.30 is the value the old ball-bond seed used, kept exactly so that no shipped default
    /// geometry moves and no golden test shifts when the profile object goes away. It is a one-line
    /// knob if a symmetric seed is ever wanted instead — nothing else reads it.</para>
    /// </summary>
    public const double SeedPeakSpan = 0.30;

    /// <summary>
    /// The seed arch: <paramref name="points"/> control points peaking at <paramref name="peakSpan"/>,
    /// built from two quarter-sine arcs meeting at the crest.
    ///
    /// <para>The derivative discontinuity at the crest is deliberate: a ball bond really does have a
    /// kink at the neck. This is a STARTING shape the user then edits — it carries no identity and
    /// nothing records that a wire came from it.</para>
    /// </summary>
    public static IReadOnlyList<ShapePoint> Seed(int points = 7, double peakSpan = SeedPeakSpan)
    {
        if (points < 3)
            throw new ArgumentOutOfRangeException(nameof(points), points,
                "A seed arch needs at least 3 points — two feet and an apex.");
        if (peakSpan is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(peakSpan), peakSpan, "Peak span must lie strictly inside (0,1).");

        var shape = new List<ShapePoint>(points);
        for (int i = 0; i < points; i++)
        {
            double s = (double)i / (points - 1);
            double h = s <= peakSpan
                ? Math.Sin(0.5 * Math.PI * s / peakSpan)
                : Math.Sin(0.5 * Math.PI * (1.0 - s) / (1.0 - peakSpan));

            // Force the feet exactly to zero rather than relying on sin(0) being exactly 0.
            if (i == 0 || i == points - 1) h = 0.0;

            shape.Add(new ShapePoint(s, h));
        }

        return shape;
    }

    /// <summary>
    /// Writes an arched polyline between two feet, so that the result measures
    /// <paramref name="loopHeightNm"/> from its lowest point to its highest.
    ///
    /// <para>The feet are written exactly as given — not interpolated — so a wire landing on a snapped
    /// bond pad stays on it to the nanometre. What flexes to hit the requested loop height is the
    /// AMPLITUDE above the chord, solved for in <see cref="SolveAmplitudeNm"/>.</para>
    ///
    /// <para><b>X and Y are written by linear interpolation between the feet</b>, so this straightens
    /// any lateral routing the wire had. That is correct for the operations that ARE X-Y operations —
    /// a span change, a flip, a pasted shape — and wrong for a pure loop-height change, which is why
    /// that one goes through <see cref="WireEdits.SetLoopHeightPreservingPath"/> instead.</para>
    /// </summary>
    public static void Write(Wire wire, Point3 start, Point3 end,
                             IReadOnlyList<ShapePoint> shape, long loopHeightNm)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(shape);
        Validate(shape);

        double amplitude = SolveAmplitudeNm(shape, loopHeightNm, start.Z, end.Z);

        wire.Points.Clear();
        for (int i = 0; i < shape.Count; i++)
        {
            var point = shape[i];

            // Exact feet: the first and last points are the pads themselves, with no rounding from
            // the interpolation.
            if (i == 0) { wire.Points.Add(start); continue; }
            if (i == shape.Count - 1) { wire.Points.Add(end); continue; }

            double s = point.Span;
            long x = start.X + (long)Math.Round((end.X - start.X) * s);
            long y = start.Y + (long)Math.Round((end.Y - start.Y) * s);
            long z = start.Z + (long)Math.Round((end.Z - start.Z) * s)
                   + (long)Math.Round(amplitude * point.Height);

            wire.Points.Add(new Point3(x, y, z));
        }
    }

    /// <summary>
    /// The amplitude above the chord that makes a wire between feet at <paramref name="startZ"/> and
    /// <paramref name="endZ"/> measure exactly <paramref name="loopHeightNm"/> from its lowest point
    /// to its highest.
    ///
    /// <para><b>Closed form, not a search.</b> Every point's z is <c>chord(s) + A·height(s)</c>, so
    /// the wire's maximum is the maximum of a family of lines in A — non-decreasing and piecewise
    /// linear. Requiring that maximum to equal <c>min z + loopHeightNm</c> means every point must sit
    /// at or below that target, which bounds A by <c>(target − chordᵢ) / heightᵢ</c> for each point
    /// that rises at all; the tightest of those bounds is the answer, and it is attained. One pass,
    /// exact, no iteration and nothing to converge.</para>
    ///
    /// <para>The lowest point of the finished wire is always the lower FOOT: every interior point
    /// sits at its chord value plus a non-negative rise, and the chord itself never dips below the
    /// lower foot. That is what makes <c>min z</c> knowable before the points are written.</para>
    ///
    /// <para><b>Clamped at zero.</b> A requested loop height below the feet's own z separation is not
    /// achievable by any shape — a dead-straight wire already measures that much — so the amplitude
    /// bottoms out at 0 and the wire comes back at its floor. Reporting that as an error would refuse
    /// a perfectly drawable wire; silently arching it upward to fake the number would be worse.</para>
    /// </summary>
    public static double SolveAmplitudeNm(IReadOnlyList<ShapePoint> shape, long loopHeightNm,
                                          long startZ, long endZ)
    {
        ArgumentNullException.ThrowIfNull(shape);

        double minFoot = Math.Min(startZ, endZ);
        double target = minFoot + loopHeightNm;

        double amplitude = double.PositiveInfinity;

        for (int i = 0; i < shape.Count; i++)
        {
            double h = shape[i].Height;
            if (h <= 0.0) continue;   // a point that does not rise places no bound on A

            double chord = startZ + (endZ - startZ) * shape[i].Span;
            amplitude = Math.Min(amplitude, (target - chord) / h);
        }

        // A flat shape (nothing rises) has no amplitude to solve for; the wire is its own chord.
        if (double.IsPositiveInfinity(amplitude)) return 0.0;

        return Math.Max(0.0, amplitude);
    }

    /// <summary>
    /// A wire's own geometry read back as a normalised shape: span as the fraction along its chord,
    /// height as the fraction of its own peak rise above that chord.
    ///
    /// <para>Reading rather than looking a shape up is what makes Copy Coordinates, a span change and
    /// a flip work on a hand-drawn or imported wire — which, with no stored shapes left, is every
    /// wire.</para>
    ///
    /// <para>The feet are forced to exactly (0,0) and (1,0): rounding in
    /// <see cref="WireEdits.ChordParameter"/> can otherwise leave a hair of height there, and
    /// <see cref="Validate"/> rejects a shape whose ends are not on the chord.</para>
    /// </summary>
    public static IReadOnlyList<ShapePoint> Read(Wire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (wire.Points.Count < 2) return [];

        var start = wire.Points[0];
        var end = wire.Points[^1];

        var spans = new List<double>(wire.Points.Count);
        var heights = new List<double>(wire.Points.Count);

        foreach (var p in wire.Points)
        {
            double t = WireEdits.ChordParameter(start, end, p);
            spans.Add(Math.Clamp(t, 0.0, 1.0));

            // Height above the straight chord between the feet — the same quantity Write consumes,
            // so a shape read back and re-written gives what was read.
            double chordZ = start.Z + t * (end.Z - start.Z);
            heights.Add(p.Z - chordZ);
        }

        double peak = heights.Max();

        var shape = new List<ShapePoint>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
            shape.Add(new ShapePoint(spans[i], peak > 0 ? Math.Clamp(heights[i] / peak, 0.0, 1.0) : 0.0));

        shape[0] = new ShapePoint(0.0, 0.0);
        shape[^1] = new ShapePoint(1.0, 0.0);

        return shape;
    }

    /// <summary>
    /// Mirrors a shape end-for-end: a crest at 30% of the span becomes one at 70%. Heights are
    /// untouched.
    ///
    /// <para><b>This is NOT the same operation as reversing a wire, and the two must not be
    /// conflated.</b> Reversing (WB26b) swaps which foot is the INPUT — it changes the wire's
    /// direction and therefore the sign of that wire's mutual coupling to every other. Flipping
    /// changes only where the loop's crest sits along the span; direction, feet, and every mutual
    /// sign are unaffected. In the profile view they look superficially similar, which is exactly why
    /// they are separate menu items.</para>
    /// </summary>
    public static IReadOnlyList<ShapePoint> Flip(IReadOnlyList<ShapePoint> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Count < 2) return shape;

        var flipped = new List<ShapePoint>(shape.Count);

        // Walking backwards restores ascending span order after the 1−s mirror, so the result is
        // still a valid shape with the input foot first — the invariant Write and Validate rely on.
        for (int i = shape.Count - 1; i >= 0; i--)
            flipped.Add(new ShapePoint(1.0 - shape[i].Span, shape[i].Height));

        return flipped;
    }

    /// <summary>Refuses a shape <see cref="Write"/> cannot stamp out.</summary>
    public static void Validate(IReadOnlyList<ShapePoint> shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (shape.Count < 2)
            throw new InvalidOperationException(
                $"A loop shape has {shape.Count} control point(s); it needs at least 2.");

        if (shape[0].Height != 0.0 || shape[^1].Height != 0.0)
            throw new InvalidOperationException(
                "A loop shape must have zero height at both feet — that is what pins them under " +
                $"height scaling. Got {shape[0].Height} and {shape[^1].Height}.");

        for (int i = 1; i < shape.Count; i++)
        {
            if (shape[i].Span <= shape[i - 1].Span)
                throw new InvalidOperationException(
                    "A loop shape's control points must have strictly increasing span; " +
                    $"point {i} is at {shape[i].Span} after {shape[i - 1].Span}.");
        }
    }

    /// <summary>Creates a wire between two feet, arched to <paramref name="shape"/>.</summary>
    public static Wire CreateWire(Point3 start, Point3 end, long diameterNm, string material,
                                  IReadOnlyList<ShapePoint> shape, long loopHeightNm)
    {
        var wire = new Wire { DiameterNm = diameterNm, Material = material };
        Write(wire, start, end, shape, loopHeightNm);
        return wire;
    }

    /// <summary>
    /// Creates a wire on the seed arch — what every "make me a wire here" path wants.
    /// </summary>
    public static Wire CreateSeedWire(Point3 start, Point3 end, long diameterNm, string material,
                                      long loopHeightNm, int points = 7) =>
        CreateWire(start, end, diameterNm, material, Seed(points), loopHeightNm);
}
