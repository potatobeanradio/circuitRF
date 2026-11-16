using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>A stop on one of a ring's two straight runs, left to right: an MTEE whose branch leaves the ring on a plan
/// node (<see cref="RingTee"/>), or an MLIN (<see cref="RingLine"/>).</summary>
public abstract record RingStop;

/// <summary>An MTEE on the ring: its branch, one W0 of Z0 line, ends on plan node <paramref name="Branch"/>, and its
/// through arms take the widths of the lines either side of it.</summary>
public sealed record RingTee(int Branch) : RingStop;

/// <summary>An MLIN on the ring between two tees: its width and its length between their reference planes.</summary>
public sealed record RingLine(double W, double L) : RingStop;

/// <summary>How a ring turns, for the readout: with half-turns their centre-line radius, with corners each end's straight
/// side and the length of line each corner stands for at F; and the straight line either run carries beyond its last
/// tees to make the two runs equal.</summary>
public sealed record RingShape(PlanCorner Corner, double RadiusMeters, double SideMeters, double CornerMeters,
    double TopRunOnMeters, double BottomRunOnMeters);

/// <summary>
/// A ring of two straight runs joined by two turns, as the rat-race's (<see cref="RatRacePlan"/>): the stops of each run
/// in a row, the upper run's tees with their branches up and the lower run's down, and a turn at each end — a 180°
/// MCURVE, or two 90° corners and a straight side (<see cref="PlanCorner"/>). Shared by the Gysel divider
/// (<see cref="GyselPlan"/>) and the Bagley polygon (<see cref="BagleyPlan"/>), whose rings are left-right symmetric.
///
/// <para>Every tee is W0 long, its through reference planes at its Z0 branch's half width, so a line's length between
/// two tees is its electrical length. The shorter run carries the difference as a line at each end, of the turn's
/// width, so the runs are equal; each turn, with those lines, makes up <c>Span</c> between the reference planes of the
/// two tees it joins: the half-turn's effective length π·(R − 0.2·W) (MCURVE's), or two corners at their own
/// electrical length at F (<see cref="PlanBend.LineLength"/>) and the side between them.</para>
/// </summary>
public static class RacetrackRing
{
    /// <param name="turn">The turn's line: its width, impedance and λg/4 at F, and the electrical length the turn and the
    /// lines either side of it make between the tees at the two runs' ends.</param>
    /// <param name="firstNode">The first internal plan node the ring may number; the pins come before it.</param>
    public static (List<PlanSection> Sections, int NodeCount, RingShape Shape) Build(
        IReadOnlyList<RingStop> top, IReadOnlyList<RingStop> bottom, double w0,
        (double W, double Z, double Quarter, double Span) turn, PlanCorner corner, MicrostripSubstrate s, double fHz,
        int firstNode, string part, string code)
    {
        double Run(IReadOnlyList<RingStop> run) => run.Sum(x => x is RingLine l ? l.L : w0);
        double d = (Run(bottom) - Run(top)) / 2.0;
        double onTop = Math.Max(d, 0.0), onBottom = Math.Max(-d, 0.0);
        double turnLength = turn.Span - onTop - onBottom;

        double y, radius = 0.0, side = 0.0, cornerLength = 0.0, arm = 0.0;
        double? miter = null;
        if (corner == PlanCorner.Curve)
        {
            radius = PassivePlan.OnGrid(turnLength / Math.PI - MicrostripCurve.EffectiveRadiusOffsetPerWidth * turn.W);
            if (radius < turn.W)
                throw new ParameterRefusalException(Diagnostic.Create(code, DiagnosticSeverity.Error,
                    "{part}: the turns at F = {f} Hz have {length} m of line, which leaves a half-turn radius of {radius} m, "
                  + "under the ring's own width {w} m. Lower F.",
                    ("part", part), ("f", fHz), ("length", turnLength), ("radius", radius), ("w", turn.W)));
            y = radius;
        }
        else
        {
            var bend = new PlanBend([], 0.0, 0.0, 0.0, turn.W, -90.0, PlanBend.MiterFor(corner, turn.W, s));
            arm = bend.Arm;
            miter = bend.M;
            cornerLength = bend.LineLength(s, fHz, turn.Z, turn.Quarter);
            y = PassivePlan.OnGrid((turnLength - 2.0 * cornerLength) / 2.0 + arm);
            side = 2.0 * y - 2.0 * arm;
            if (!(side > 0.0))
                throw new ParameterRefusalException(Diagnostic.Create(code, DiagnosticSeverity.Error,
                    "{part}: the turns at F = {f} Hz have {length} m of line, which leaves no straight side between the "
                  + "ring's corners ({side} m). Lower F.",
                    ("part", part), ("f", fHz), ("length", turnLength), ("side", side)));
        }

        var sections = new List<PlanSection>();
        int next = firstNode;
        // One run, left to right at height h: its stops between a line of `on` at each end. A tee's through arms take the
        // widths beside it; turned half round (the upper run) its pin 1 is its right end and its branch points up.
        (int Left, int Right, double End) Lay(IReadOnlyList<RingStop> run, double h, double on, bool up)
        {
            int left = next++, node = left;
            double x = 0.0;
            void Line(double w, double l)
            {
                int to = next++;
                sections.Add(new PlanLine([node, to], x, h, 0.0, w, l));
                node = to;
                x += l;
            }
            double WidthAt(int i) => i >= 0 && i < run.Count && run[i] is RingLine l ? l.W : turn.W;
            if (on > 0.0) Line(turn.W, on);
            for (int i = 0; i < run.Count; i++)
            {
                if (run[i] is RingLine l) { Line(l.W, l.L); continue; }
                double wl = WidthAt(i - 1), wr = WidthAt(i + 1), l3 = Math.Max(wl, wr) / 2.0 + w0;
                int to = next++, branch = ((RingTee)run[i]).Branch;
                sections.Add(up
                    ? new PlanTee(wr, wl, w0, w0 / 2.0, w0 / 2.0, l3, [to, node, branch], x + w0, h, 180.0)
                    : new PlanTee(wl, wr, w0, w0 / 2.0, w0 / 2.0, l3, [node, to, branch], x, h, 0.0));
                node = to;
                x += w0;
            }
            if (on > 0.0) Line(turn.W, on);
            return (left, node, x);
        }
        var (topLeft, topRight, xr) = Lay(top, y, onTop, up: true);
        var (bottomLeft, bottomRight, _) = Lay(bottom, -y, onBottom, up: false);

        if (corner == PlanCorner.Curve)
        {
            sections.Add(new PlanCurve(turn.W, -180.0, radius, [topRight, bottomRight], xr, radius, 0.0));
            sections.Add(new PlanCurve(turn.W, -180.0, radius, [bottomLeft, topLeft], 0.0, -radius, 180.0));
        }
        else
        {
            int r1 = next++, r2 = next++, l1 = next++, l2 = next++;
            sections.AddRange([
                new PlanBend([topRight, r1], xr, y, 0.0, turn.W, -90.0, miter),
                new PlanLine([r1, r2], xr + arm, y - arm, 270.0, turn.W, side),
                new PlanBend([r2, bottomRight], xr + arm, -y + arm, 270.0, turn.W, -90.0, miter),
                new PlanBend([bottomLeft, l1], 0.0, -y, 180.0, turn.W, -90.0, miter),
                new PlanLine([l1, l2], -arm, -y + arm, 90.0, turn.W, side),
                new PlanBend([l2, topLeft], -arm, y - arm, 90.0, turn.W, -90.0, miter),
            ]);
        }
        return (sections, next - 1, new RingShape(corner, radius, side, cornerLength, onTop, onBottom));
    }

    /// <summary>A quarter-wave line of impedance <paramref name="z"/>: MLIN's width at F and its λg/4, both on the 2 nm
    /// grid, and its εeff; a Z0 no width reaches is refused with <paramref name="code"/>.</summary>
    public static WilkinsonLine Quarter(string name, double z, double fHz, MicrostripSubstrate s, string part, string code)
    {
        double w = PassivePlan.OnGrid(BranchLinePlan.Width(z, fHz, s, part, code));
        double eeff = BranchLinePlan.Line(w, s).LineParameters(fHz).Eeff;
        return new WilkinsonLine(name, z, w, eeff, PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(eeff, Math.PI / 2.0, fHz)));
    }
}
