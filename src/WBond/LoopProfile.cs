namespace CircuitRF.WBond;

/// <summary>
/// One control point of a loop profile, in <b>normalised</b> coordinates.
/// </summary>
/// <param name="Span">Position along the chord, 0 at the input foot and 1 at the output foot.</param>
/// <param name="Height">
/// Height above the chord, normalised so the peak is 1. Both feet are 0, which is what makes them
/// immovable under height scaling (WB24a).
/// </param>
public readonly record struct ProfilePoint(double Span, double Height);

/// <summary>
/// A named, shared loop shape that wires bind to (wbond.md §6.2, WB2).
///
/// <h3>Why the shape is normalised</h3>
/// <para>Storing height against <b>normalised span</b> rather than against absolute position is what
/// makes wire angle and wire length stop being profile differences at all. A wire at 37° and a wire
/// at 90°, 60 mil and 140 mil long, have the same <i>profile</i> if they have the same z-vs-span
/// shape — which is exactly what a packaging engineer means by "the same loop". The only genuine
/// odd-balls left are differing point counts and XY backtracking.</para>
///
/// <h3>Why the feet cannot move</h3>
/// <para>Height is measured <b>above the chord</b>, not above a flat baseline. The endpoints have
/// height 0, so scaling multiplies them by zero and they provably stay put — which matters because in
/// chip-and-wire the two feet are usually at different z (die surface to package lead), and scaling
/// about a single baseline would drag one foot off its pad.</para>
///
/// <para>A profile is a <b>generator</b>, exactly as a PCell is: it writes a wire's points, and
/// breaking the binding leaves those points untouched (WB2 / D1).</para>
/// </summary>
public sealed class LoopProfile
{
    public required string Name { get; set; }

    /// <summary>
    /// The loop height a wire built from this profile will have: <b>its maximum z minus its minimum
    /// z</b> (wbond.md §3.1a, and see <see cref="Wire.LoopHeightNm"/>, which is the definition).
    ///
    /// <para><b>This is not the amplitude added above the chord</b> — <see cref="ApplyTo"/> solves
    /// for that amplitude so the wire it writes actually measures this. When the two feet are at the
    /// same z the two numbers coincide; when they are not (the ordinary chip-and-wire case) the
    /// amplitude is smaller, because part of the height is already supplied by the foot drop.</para>
    /// </summary>
    public long LoopHeightNm { get; set; } = WBondUnits.ToNm(20.0, WBondUnit.Mil);

    /// <summary>The normalised shape, input foot first. First and last must have Height 0.</summary>
    public List<ProfilePoint> Shape { get; init; } = [];

    /// <summary>Number of points a wire generated from this profile will have.</summary>
    public int PointCount => Shape.Count;

    /// <summary>
    /// Regenerates a wire's polyline between two feet, so that the result measures
    /// <see cref="LoopHeightNm"/> from its lowest point to its highest.
    ///
    /// <para>The feet are written exactly as given — not interpolated — so a wire landing on a snapped
    /// bond pad stays on it to the nanometre. What flexes to hit the requested loop height is the
    /// AMPLITUDE above the chord, solved for in <see cref="SolveAmplitudeNm"/>.</para>
    /// </summary>
    public void ApplyTo(Wire wire, Point3 start, Point3 end)
    {
        ArgumentNullException.ThrowIfNull(wire);
        Validate();

        double amplitude = SolveAmplitudeNm(start.Z, end.Z);

        wire.Points.Clear();
        for (int i = 0; i < Shape.Count; i++)
        {
            var point = Shape[i];

            // Exact feet: the first and last points are the pads themselves, with no rounding from
            // the interpolation.
            if (i == 0) { wire.Points.Add(start); continue; }
            if (i == Shape.Count - 1) { wire.Points.Add(end); continue; }

            double s = point.Span;
            long x = start.X + (long)Math.Round((end.X - start.X) * s);
            long y = start.Y + (long)Math.Round((end.Y - start.Y) * s);
            long z = start.Z + (long)Math.Round((end.Z - start.Z) * s)
                   + (long)Math.Round(amplitude * point.Height);

            wire.Points.Add(new Point3(x, y, z));
        }

        wire.ProfileBinding = Name;
    }

    /// <summary>
    /// The amplitude above the chord that makes a wire between feet at <paramref name="startZ"/> and
    /// <paramref name="endZ"/> measure exactly <see cref="LoopHeightNm"/> from its lowest point to its
    /// highest.
    ///
    /// <para><b>Closed form, not a search.</b> Every point's z is <c>chord(s) + A·height(s)</c>, so
    /// the wire's maximum is the maximum of a family of lines in A — non-decreasing and piecewise
    /// linear. Requiring that maximum to equal <c>min z + LoopHeightNm</c> means every point must sit
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
    public double SolveAmplitudeNm(long startZ, long endZ)
    {
        double minFoot = Math.Min(startZ, endZ);
        double target = minFoot + LoopHeightNm;

        double amplitude = double.PositiveInfinity;

        for (int i = 0; i < Shape.Count; i++)
        {
            double h = Shape[i].Height;
            if (h <= 0.0) continue;   // a point that does not rise places no bound on A

            double chord = startZ + (endZ - startZ) * Shape[i].Span;
            amplitude = Math.Min(amplitude, (target - chord) / h);
        }

        // A flat shape (nothing rises) has no amplitude to solve for; the wire is its own chord.
        if (double.IsPositiveInfinity(amplitude)) return 0.0;

        return Math.Max(0.0, amplitude);
    }

    /// <summary>Creates a wire from this profile between two feet.</summary>
    public Wire CreateWire(Point3 start, Point3 end, long diameterNm, string material)
    {
        var wire = new Wire { DiameterNm = diameterNm, Material = material };
        ApplyTo(wire, start, end);
        return wire;
    }

    public void Validate()
    {
        if (Shape.Count < 2)
            throw new InvalidOperationException(
                $"Loop profile '{Name}' has {Shape.Count} control point(s); a profile needs at least 2.");

        if (Shape[0].Height != 0.0 || Shape[^1].Height != 0.0)
            throw new InvalidOperationException(
                $"Loop profile '{Name}' must have zero height at both feet — that is what pins them " +
                "under height scaling. Got " +
                $"{Shape[0].Height} and {Shape[^1].Height}.");

        for (int i = 1; i < Shape.Count; i++)
        {
            if (Shape[i].Span <= Shape[i - 1].Span)
                throw new InvalidOperationException(
                    $"Loop profile '{Name}' control points must have strictly increasing span; " +
                    $"point {i} is at {Shape[i].Span} after {Shape[i - 1].Span}.");
        }
    }

    /// <summary>
    /// A ball bond: a fast rise from the ball, a peak early in the span, then a long shallow descent
    /// to the stitch. The asymmetry is real — a ball bond is not a symmetric catenary.
    /// </summary>
    public static LoopProfile BallBond(long loopHeightNm, int points = 7) =>
        Peaked("ball", loopHeightNm, points, peakSpan: 0.30);

    /// <summary>A wedge bond: a symmetric arc between two wedges.</summary>
    public static LoopProfile WedgeBond(long loopHeightNm, int points = 7) =>
        Peaked("wedge", loopHeightNm, points, peakSpan: 0.50);

    /// <summary>
    /// A profile peaking at <paramref name="peakSpan"/>, built from two quarter-sine arcs meeting at
    /// the peak. The derivative discontinuity there is deliberate: a ball bond really does have a
    /// kink at the neck.
    /// </summary>
    public static LoopProfile Peaked(string name, long loopHeightNm, int points, double peakSpan)
    {
        if (points < 3)
            throw new ArgumentOutOfRangeException(nameof(points), points,
                "A peaked profile needs at least 3 points — two feet and an apex.");
        if (peakSpan is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(peakSpan), peakSpan, "Peak span must lie strictly inside (0,1).");

        var shape = new List<ProfilePoint>(points);
        for (int i = 0; i < points; i++)
        {
            double s = (double)i / (points - 1);
            double h = s <= peakSpan
                ? Math.Sin(0.5 * Math.PI * s / peakSpan)
                : Math.Sin(0.5 * Math.PI * (1.0 - s) / (1.0 - peakSpan));

            // Force the feet exactly to zero rather than relying on sin(0) being exactly 0.
            if (i == 0 || i == points - 1) h = 0.0;

            shape.Add(new ProfilePoint(s, h));
        }

        return new LoopProfile { Name = name, LoopHeightNm = loopHeightNm, Shape = shape };
    }

    /// <summary>
    /// Scales the peak height, preserving the shape exactly (WB24a — alt + vertical drag).
    /// </summary>
    public void ScaleHeight(double factor)
    {
        if (factor <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Height scale must be positive.");
        LoopHeightNm = (long)Math.Round(LoopHeightNm * factor);
    }

    /// <summary>
    /// Mirrors the shape end-for-end: a ball bond peaking at 30% of the span becomes one peaking at
    /// 70%. Heights and the loop height are untouched.
    ///
    /// <para><b>This is NOT the same operation as reversing a wire, and the two must not be
    /// conflated.</b> Reversing (WB26b) swaps which foot is the INPUT — it changes the wire's
    /// direction and therefore the sign of that wire's mutual coupling to every other. Flipping
    /// changes only where the loop's crest sits along the span; direction, feet, and every mutual
    /// sign are unaffected. In the profile view they look superficially similar, which is exactly why
    /// they are separate menu items.</para>
    /// </summary>
    public void Flip()
    {
        if (Shape.Count < 2) return;

        var flipped = new List<ProfilePoint>(Shape.Count);
        for (int i = Shape.Count - 1; i >= 0; i--)
            flipped.Add(new ProfilePoint(1.0 - Shape[i].Span, Shape[i].Height));

        // Reversing the list restores ascending span order, so the result is still a valid shape
        // with the input foot first — the invariant ApplyTo and Validate both rely on.
        Shape.Clear();
        Shape.AddRange(flipped);
    }
}
