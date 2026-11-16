// brief-hzn-21 R-hzn21-2 — a half (or quarter) model's radiation pattern made the WHOLE structure's, the same for every solver.
//
// THE IMAGE. A symmetry plane is a wall the whole structure's field is mirrored in: with R the reflection in the plane (R r = M r + d,
// M the reflection matrix, d twice the plane's coordinate along its normal), the field obeys E(R r) = s M E(r) and
// H(R r) = s det(M) M H(r), s = +1 for a magnetic wall (E lies in it) and −1 for an electric one. The half's equivalence surface
// (the solver's absorbing faces, or openEMS's box) and its image close round the whole structure, and the image currents
// J'' = s M J, M'' = s det(M) M M_s at R r' radiate, in circuitRF's convention (e^{jωt}, N = ∮J e^{+jk r̂·r'}, NearToFarField):
//
//     F_image(r̂) = s · e^{+jk r̂·d} · M F(M r̂)
//
// Two planes (one per axis) compose: their three images each by the same rule, M the product, s the product, d the sum. M r̂ on
// the 1° sphere is a grid point (x: φ → 180° − φ, y: φ → −φ, z: θ → 180° − θ), so nothing is interpolated.
//
// THE POWER. Each image accepts what the half does, so the whole structure's accepted power is 2ⁿ times the half's, and the gain,
// directivity and efficiency the stage derives are the whole structure's. The port's reflection is the half's own: the S-parameters
// stay the half model's, referred to the impedances its ports state.
//
// THE PHASE CONVENTION is a property of the solver that formed F, through e^{+jk r̂·d}; PhaseSign says it per solver, and the
// translated-plane gate (HalfModelTests) is what fixed each one — a wrong sign shows only when the plane is off the origin.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Em3d;

internal static class Em3dSymmetry
{
    /// <summary>The sign of the image's phase factor e^{±jk r̂·d} for a solver's far field: +1 is circuitRF's own convention
    /// (NearToFarField, openEMS), and EMerge's and Palace's were measured against it (brief-hzn-21's translated gate).</summary>
    public static int PhaseSign(string solver) => +1;

    /// <summary>
    /// The whole structure's points from the half's, row-major as given, or the points unchanged when <paramref name="box"/> has no
    /// symmetry plane. Null with <paramref name="refusal"/> when the grid cannot hold a mirrored direction. <paramref name="note"/>
    /// says what was done, for the run's notes.
    /// </summary>
    public static IReadOnlyList<Em3dPatternPoint>? Mirror(IReadOnlyList<Em3dPatternPoint> points, Em3dAirBox box, int phaseSign,
                                                          out string? note, out string? refusal)
    {
        note = refusal = null;
        var planes = box.SymmetryPlanes;
        if (planes.Count == 0 || points.Count == 0) return points;
        var grid = points[0].Pattern.Grid;

        // Every non-empty subset of the planes is one image.
        var images = new List<(int[] Flip, double S, double[] D, int[] Map)>();
        for (int mask = 1; mask < 1 << planes.Count; mask++)
        {
            var flip = new int[3];
            var d = new double[3];
            double s = 1;
            for (int b = 0; b < planes.Count; b++)
            {
                if ((mask & (1 << b)) == 0) continue;
                var p = planes[b];
                double at = PlaneAt(box, p);
                flip[p.Axis] = 1;
                d[p.Axis] = 2 * at;
                s *= p.Wall == Em3dBoundaryKind.Pmc ? 1 : -1;
            }
            var map = MirrorMap(grid, flip, out string? missing);
            if (map is null)
            {
                refusal = $"The radiation pattern cannot be mirrored across the symmetry plane{(planes.Count > 1 ? "s" : "")}: {missing}";
                return null;
            }
            images.Add((flip, s, d, map));
        }

        double scale = Math.Pow(2, planes.Count);
        var mirrored = new Em3dPatternPoint[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            var pat = pt.Pattern;
            double k = 2 * Math.PI * pat.FrequencyHz / 299_792_458.0;
            int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
            var eth = new Complex[grid.DirectionCount];
            var eph = new Complex[grid.DirectionCount];
            for (int it = 0; it < nt; it++)
                for (int ip = 0; ip < np; ip++)
                {
                    int g = grid.IndexOf(it, ip);
                    var r = Direction(grid, g);
                    var f = Cartesian(grid, g, pat.ETheta[g], pat.EPhi[g]);
                    var sum = f;
                    foreach (var (flip, s, d, map) in images)
                    {
                        int m = map[g];
                        var fm = Cartesian(grid, m, pat.ETheta[m], pat.EPhi[m]);
                        double phase = phaseSign * k * (r.X * d[0] + r.Y * d[1] + r.Z * d[2]);
                        Complex e = s * Complex.FromPolarCoordinates(1, phase);
                        sum = (sum.X + e * (flip[0] == 1 ? -fm.X : fm.X),
                               sum.Y + e * (flip[1] == 1 ? -fm.Y : fm.Y),
                               sum.Z + e * (flip[2] == 1 ? -fm.Z : fm.Z));
                    }
                    (eth[g], eph[g]) = NearToFarField.ToSpherical(sum.X, sum.Y, sum.Z, grid.ThetaDeg[it], grid.PhiDeg[ip]);
                }
            mirrored[i] = new Em3dPatternPoint(FarFieldStage.Pattern(grid, eth, eph, pat.DrivenPort, pat.FrequencyHz),
                                               pt.AcceptedW * scale, pt.Reflection);
        }
        note = Note(box);
        return mirrored;
    }

    /// <summary>Marks <paramref name="data"/>'s far-field group as mirrored, when <paramref name="box"/> has symmetry planes.</summary>
    public static void Mark(RfCore.Data.DataSet data, Em3dAirBox? box)
    {
        if (box is not { SymmetryPlanes.Count: > 0 }) return;
        data.AddToGroup(PlanarFarField.Group, Em3dRadiation.SymmetryCopiesCube, RfCore.Data.DataCube.Scalar(Math.Pow(2, box.SymmetryPlanes.Count)));
    }

    /// <summary>What a mirrored run's notes say: which planes, which walls, and that S is the half model's.</summary>
    public static string Note(Em3dAirBox box)
    {
        var planes = box.SymmetryPlanes;
        string list = string.Join(" and ", planes.Select(p =>
            $"the {p.Face} face ({"xyz"[p.Axis]} = {(PlaneAt(box, p) * 1e3).ToString("G6", CultureInfo.InvariantCulture)} mm, " +
            $"{(p.Wall == Em3dBoundaryKind.Pmc ? "a magnetic wall" : "an electric wall")})"));
        return $"Symmetry: the model is {(planes.Count == 1 ? "half" : planes.Count == 2 ? "a quarter" : "an eighth")} of the structure, " +
               $"mirrored in {list}. The radiation pattern is the whole structure's, formed from the solved part's surface and its " +
               $"image{(planes.Count > 1 ? "s" : "")}, and the accepted power is {Math.Pow(2, planes.Count):0} times the solved part's, so " +
               "gain, directivity and efficiency are the whole structure's. The S-parameters are the solved part's own ports, referred " +
               "to the impedances they state: a port the plane cuts sees the field of the whole structure but carries the part's share " +
               "of its current (a magnetic wall) or voltage (an electric wall).";
    }

    /// <summary>The plane's coordinate: the air box's face along its axis, where the problem's box actually is.</summary>
    private static double PlaneAt(Em3dAirBox box, Em3dSymmetryPlane p)
    {
        bool high = p.Face.EndsWith("max", StringComparison.Ordinal);
        double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
        return p.Face.Length == 0 ? p.AtM : high ? hi[p.Axis] : lo[p.Axis];
    }

    /// <summary>For each direction g, the index of M r̂; null (with the direction) when the grid has no such point.</summary>
    private static int[]? MirrorMap(PlanarFarFieldGrid grid, int[] flip, out string? missing)
    {
        missing = null;
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count;
        var map = new int[grid.DirectionCount];
        for (int it = 0; it < nt; it++)
            for (int ip = 0; ip < np; ip++)
            {
                double th = grid.ThetaDeg[it], ph = grid.PhiDeg[ip];
                if (flip[2] == 1) th = 180 - th;
                if (flip[0] == 1) ph = 180 - ph;
                if (flip[1] == 1) ph = -ph;
                ph = ((ph % 360) + 360) % 360;
                int jt = Find(grid.ThetaDeg, th, 0), jp = Find(grid.PhiDeg, ph, 360);
                if (jt < 0 || jp < 0)
                {
                    missing = $"the pattern's grid has no direction at θ = {th.ToString("G6", CultureInfo.InvariantCulture)}°, " +
                              $"φ = {ph.ToString("G6", CultureInfo.InvariantCulture)}°, the mirror image of θ = " +
                              $"{grid.ThetaDeg[it].ToString("G6", CultureInfo.InvariantCulture)}°, φ = " +
                              $"{grid.PhiDeg[ip].ToString("G6", CultureInfo.InvariantCulture)}°" +
                              (flip[2] == 1 && grid.ThetaDeg[^1] <= 90 + 1e-9
                                  ? " — the pattern is the upper hemisphere only, which a plane normal to z cannot be mirrored into."
                                  : ".");
                    return null;
                }
                map[grid.IndexOf(it, ip)] = grid.IndexOf(jt, jp);
            }
        return map;
    }

    private static int Find(IReadOnlyList<double> values, double v, double period)
    {
        for (int i = 0; i < values.Count; i++)
        {
            double diff = Math.Abs(values[i] - v);
            if (period > 0) diff = Math.Min(diff, period - diff);
            if (diff < 1e-6) return i;
        }
        return -1;
    }

    private static Point3 Direction(PlanarFarFieldGrid grid, int g)
    {
        int it = g / grid.PhiDeg.Count, ip = g % grid.PhiDeg.Count;
        var (st, ct) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180);
        var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180);
        return new Point3(st * cp, st * sp, ct);
    }

    /// <summary>F = Fθ θ̂ + Fφ φ̂ at direction g, Cartesian.</summary>
    private static (Complex X, Complex Y, Complex Z) Cartesian(PlanarFarFieldGrid grid, int g, Complex eTheta, Complex ePhi)
    {
        int it = g / grid.PhiDeg.Count, ip = g % grid.PhiDeg.Count;
        var (st, ct) = Math.SinCos(grid.ThetaDeg[it] * Math.PI / 180);
        var (sp, cp) = Math.SinCos(grid.PhiDeg[ip] * Math.PI / 180);
        return (eTheta * ct * cp - ePhi * sp, eTheta * ct * sp + ePhi * cp, -eTheta * st);
    }
}
