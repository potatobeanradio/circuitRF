using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>How a run is laid (<see cref="PlanRoute"/>): one straight line; two legs and one 90° corner; or a meander of
/// parallel legs joined by 180° turns.</summary>
public enum PlanRouteStyle { Straight, Corner, Meander }

/// <summary>A run of line, laid: its sections, the next free plan node, where it ends and which way it heads there, its
/// copper's extent (centre lines widened by W/2) and what the model leaves out.</summary>
public sealed record PlanRouteResult(
    IReadOnlyList<PlanSection> Sections, int NextNode,
    double EndX, double EndY, double EndRotationDeg,
    double MinX, double MinY, double MaxX, double MaxY,
    IReadOnlyList<Diagnostic> Warnings);

/// <summary>
/// A run of width W that is θ long at F, laid as existing sections from a start pose (docs/design/rf-passives.md, "Route
/// helper"). Straight is one <see cref="PlanLine"/>; Corner is two equal legs and one 90° corner; Meander is k + 1 equal
/// parallel legs joined by k 180° turns, stacked <c>Pitch</c> apart centre to centre towards <c>Side</c> (+1 left of the
/// heading, −1 right), each turn back the other way, so k odd ends heading back the way it started.
///
/// <para><b>The length.</b> The legs share what θ leaves after the corners: θ at F in MLIN's dispersive εeff, less each
/// corner's own insertion phase at F as a length of the line (<see cref="PlanSection.LineLength"/>, as the cornered
/// MWPD and MRRC rings count theirs). A Curve corner is an MCURVE (of <c>Radius</c>, 3W when none is given; a meander's
/// half-turn has radius Pitch/2), a Miter or Square corner an MMITER or MBEND; a meander's Miter or Square turn is two
/// corners and the line between them. Every dimension is on <see cref="PassivePlan.OnGrid"/>.</para>
///
/// <para><b>Limits.</b> The sections are stamped as separate lines, so adjacent legs do not couple in the model: a
/// meander whose legs are under 3W apart edge to edge says so (<c>device.route.runs-coupled</c>). The start heading is a
/// multiple of 90°, as every section's pose is.</para>
/// </summary>
public static class PlanRoute
{
    /// <param name="x">Where the run starts (m), in the passive's frame.</param>
    /// <param name="rotationDeg">The way it heads from there, counter-clockwise from +x, a multiple of 90.</param>
    /// <param name="from">The plan node the run starts on.</param>
    /// <param name="to">The plan node it ends on.</param>
    /// <param name="next">The first free internal node; the result's <see cref="PlanRouteResult.NextNode"/> follows its own.</param>
    public static PlanRouteResult Build(
        double x, double y, double rotationDeg, double w, double thetaRad, double fHz, MicrostripSubstrate s,
        PlanRouteStyle style = PlanRouteStyle.Straight, PlanCorner corner = PlanCorner.Curve,
        int turns = 1, double pitch = 0.0, int side = 1, double radius = 0.0, int from = 1, int to = 2, int next = 3)
    {
        foreach (var (name, value) in new[] { ("W", w), ("θ", thetaRad), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.route.spec-not-positive",
                    DiagnosticSeverity.Error, "Route: {name} = {value} must be positive.", ("name", name), ("value", value)));
        side = side < 0 ? -1 : 1;

        var line = new MicrostripLineModel(w, 1.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "(route)",
            s.RoughnessMeters).LineParameters(fHz);
        double total = PlanarLineAngle.LengthMeters(line.Eeff, thetaRad, fHz);
        double quarter = PlanarLineAngle.LengthMeters(line.Eeff, Math.PI / 2.0, fHz);
        var warnings = new List<Diagnostic>();

        // The run in its own frame: from the origin heading +x. Each piece is posed where the last one ended.
        var pieces = new List<PlanSection>();
        var points = new List<(double X, double Y)> { (0.0, 0.0) };
        double px = 0.0, py = 0.0;
        int heading = 0, node = from;
        void Add(PlanSection piece, bool last)
        {
            int end = last ? to : next++;
            pieces.Add(piece with { Nodes = [node, end], X = px, Y = py, RotationDeg = 90.0 * heading });
            node = end;
            // Where its pin 2 lands and which way it then heads, in the piece's own frame (exact for quarter turns).
            (double X, double Y, int Quarters, (double X, double Y)? Beyond) to2 = piece switch
            {
                PlanLine l => (l.L, 0.0, 0, null),
                PlanBend b => (b.Arm, Math.Sign(b.AngleDeg) * b.Arm, Math.Sign(b.AngleDeg), (b.Arm, 0.0)),
                PlanCurve c when Math.Abs(c.AngleDeg) == 180.0
                    => (0.0, Math.Sign(c.AngleDeg) * 2.0 * c.Radius, 2 * Math.Sign(c.AngleDeg), (c.Radius, Math.Sign(c.AngleDeg) * c.Radius)),
                PlanCurve c => (c.Radius, Math.Sign(c.AngleDeg) * c.Radius, Math.Sign(c.AngleDeg), null),
                _ => throw new NotSupportedException(piece.GetType().Name),
            };
            // A bend's corner and a half-turn's crest reach beyond its pins.
            if (to2.Beyond is { } e) points.Add(Turn(e.X, e.Y, heading, px, py));
            (px, py) = Turn(to2.X, to2.Y, heading, px, py);
            heading = (heading + to2.Quarters + 4) & 3;
            points.Add((px, py));
        }

        double? miter(double width) => PlanBend.MiterFor(corner, width, s);
        PlanSection Bend(int sign) => new PlanBend([], 0, 0, 0, w, 90.0 * sign, miter(w));
        double Length(PlanSection piece) => piece.LineLength(s, fHz, line.Z0, quarter);
        PlanLine Line(double l) => new([], 0, 0, 0, w, l);

        switch (style)
        {
            case PlanRouteStyle.Straight:
                Add(Line(PassivePlan.OnGrid(total)), last: true);
                break;

            case PlanRouteStyle.Corner:
            {
                var turn = corner == PlanCorner.Curve
                    ? new PlanCurve(w, 90.0 * side, PassivePlan.OnGrid(radius > 0 ? radius : 3.0 * w), [], 0, 0, 0)
                    : Bend(side);
                double leg = PassivePlan.OnGrid((total - Length(turn)) / 2.0);
                Refuse(leg, thetaRad);
                Add(Line(leg), last: false);
                Add(turn, last: false);
                Add(Line(leg), last: true);
                break;
            }

            case PlanRouteStyle.Meander:
            {
                if (turns < 1 || !(pitch > w))
                    throw new ParameterRefusalException(Diagnostic.Create("device.route.pitch-too-small",
                        DiagnosticSeverity.Error,
                        "Route: a meander needs at least one turn and a pitch wider than the line ({turns} turns, pitch {pitch} m, "
                      + "W {w} m).", ("turns", turns), ("pitch", pitch), ("w", w)));
                // A turn back: one half-turn of radius p/2, or two corners and the line across between them.
                double half = PassivePlan.OnGrid(pitch / 2.0), p = corner == PlanCorner.Curve ? 2.0 * half : PassivePlan.OnGrid(pitch);
                double across = 0.0, turnLength;
                if (corner == PlanCorner.Curve)
                {
                    double l = Length(new PlanCurve(w, 180.0, half, [], 0, 0, 0));
                    // The phase reads modulo a wavelength; the half-turn is about π·p/2 long.
                    turnLength = l + Math.Round((Math.PI * p / 2.0 - l) / (4.0 * quarter)) * 4.0 * quarter;
                }
                else
                {
                    var b = (PlanBend)Bend(1);
                    across = p - 2.0 * b.Arm;
                    if (across < 0.0)
                        throw new ParameterRefusalException(Diagnostic.Create("device.route.pitch-too-small",
                            DiagnosticSeverity.Error,
                            "Route: a pitch of {pitch} m leaves no room for two corners of {arm} m each. Widen the pitch.",
                            ("pitch", p), ("arm", b.Arm)));
                    turnLength = 2.0 * Length(b) + across;
                }
                double leg = PassivePlan.OnGrid((total - turns * turnLength) / (turns + 1));
                Refuse(leg, thetaRad);
                if (p - w < 3.0 * w)
                    warnings.Add(Diagnostic.Create("device.route.runs-coupled", DiagnosticSeverity.Warning,
                        "Route: the meander's legs are {gap} m apart edge to edge, under 3·W ({limit} m); the model takes them "
                      + "as separate lines and leaves out their coupling.", ("gap", p - w), ("limit", 3.0 * w)));

                for (int k = 0; k <= turns; k++)
                {
                    Add(Line(leg), last: k == turns);
                    if (k == turns) break;
                    int sign = k % 2 == 0 ? side : -side;            // every turn stacks the next leg the same way
                    if (corner == PlanCorner.Curve) Add(new PlanCurve(w, 180.0 * sign, half, [], 0, 0, 0), last: false);
                    else
                    {
                        Add(Bend(sign), last: false);
                        if (across > 0.0) Add(Line(across), last: false);
                        Add(Bend(sign), last: false);
                    }
                }
                break;
            }
        }

        // Into the passive's frame: turned by the start heading about the start, then moved there.
        int q = (int)Math.Round(rotationDeg / 90.0) & 3;
        var sections = pieces.Select(piece =>
        {
            var (gx, gy) = Turn(piece.X, piece.Y, q, x, y);
            return piece with { X = gx, Y = gy, RotationDeg = (piece.RotationDeg + 90.0 * q) % 360.0 };
        }).ToList();
        var corners = points.Select(pt => Turn(pt.X, pt.Y, q, x, y)).ToList();
        var (endX, endY) = corners[^1];
        double hw = w / 2.0;
        return new PlanRouteResult(sections, next, endX, endY, 90.0 * ((heading + q) & 3),
            corners.Min(c => c.X) - hw, corners.Min(c => c.Y) - hw, corners.Max(c => c.X) + hw, corners.Max(c => c.Y) + hw,
            warnings);
    }

    /// <summary>A leg with no length left: θ is too short for the corners the style asks for.</summary>
    private static void Refuse(double leg, double thetaRad)
    {
        if (!(leg > 0.0))
            throw new ParameterRefusalException(Diagnostic.Create("device.route.too-short", DiagnosticSeverity.Error,
                "Route: θ = {theta}° at F is used up by the corners, leaving no straight leg. Use fewer turns or a straight run.",
                ("theta", thetaRad * 180.0 / Math.PI)));
    }

    /// <summary>(<paramref name="lx"/>, <paramref name="ly"/>) turned <paramref name="quarters"/> quarter turns
    /// counter-clockwise and moved to (<paramref name="ox"/>, <paramref name="oy"/>), exactly.</summary>
    private static (double X, double Y) Turn(double lx, double ly, int quarters, double ox, double oy)
        => (quarters & 3) switch
        {
            1 => (ox - ly, oy + lx),
            2 => (ox - lx, oy - ly),
            3 => (ox + ly, oy - lx),
            _ => (ox + lx, oy + ly),
        };
}
