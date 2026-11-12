// brief-hzn-12 — a scattering run: a driven 3D problem driven by an incident plane wave instead of its ports. What a setup with a
// plane wave is refused for, which solvers run one, EMerge's angles for the wave, and the closed surface its far field is read on.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class Em3dPlaneWaves
{
    /// <summary>
    /// Why a setup's plane wave cannot be solved, or null when it can: the wave itself (a zero vector, an E-field not at right angles to
    /// the direction of travel, an amplitude that is not positive), and ports beside it. <paramref name="ports"/> is how many the
    /// document and the setup's component ports would build.
    /// </summary>
    public static string? SetupRefusal(EmSetup setup, int ports, string? setupName = null)
    {
        if (!setup.IsScattering3D) return null;
        string who = setupName is { Length: > 0 } n ? $"Setup '{n}'" : "The setup";
        if (setup.PlaneWave!.Resolve(out string? why) is null) return $"{who}'s plane wave cannot be solved: {why}.";
        if (ports > 0)
            return $"{who} is driven by a plane wave and the 3D view has {ports} port(s); circuitRF runs a plane wave with no port beside " +
                   "it in this version. Remove the ports from the problem (turn each one's Model off), or drive the setup by its ports.";
        return null;
    }

    /// <summary>Why a scattering problem's air box cannot hold it, or null: every face must absorb, since the scattered field has
    /// nowhere else to go and the incident wave enters through them.</summary>
    public static string? BoxRefusal(Em3dAirBox box)
    {
        var f = box.Faces;
        var closed = new[] { ("xmin", f.XMin), ("xmax", f.XMax), ("ymin", f.YMin), ("ymax", f.YMax), ("zmin", f.ZMin), ("zmax", f.ZMax) }
            .Where(x => x.Item2 != Em3dBoundaryKind.Absorbing).Select(x => $"{x.Item1} ({x.Item2})").ToList();
        return closed.Count == 0 ? null
            : $"The setup is driven by a plane wave, and the air box's {string.Join(", ", closed)} face{(closed.Count == 1 ? " is" : "s are")} " +
              "not absorbing. A plane wave enters through the absorbing faces and its scattered field leaves through them, so every " +
              "face of a scattering run's box absorbs. Set each face's Boundary to Absorbing in the setup's AirBox.";
    }

    /// <summary>Why <paramref name="choice"/> cannot run a plane wave (a solver it names has no plane-wave source: Palace), or null when
    /// every solver it names can.</summary>
    public static string? SolverCannot(Em3dSolverChoice choice)
        => Em3dSolvers.Of(choice).FirstOrDefault(d => !d.Capabilities.PlaneWave) is { } no ? SolverRefusal(no.DisplayName) : null;

    /// <summary>The refusal a solver that does not run a plane wave gives: why, and the solvers that do.</summary>
    public static string SolverRefusal(string solver)
    {
        var able = Em3dSolvers.With(c => c.PlaneWave);
        string why = solver == "Palace"
            ? " Palace has no incident-field source: its one plane-wave excitation is a Floquet port on a periodic unit cell, and its " +
              "surface-current source is a uniform current, which cannot carry a wave's phase across the model."
            : "";
        return $"The setup is driven by a plane wave, which {Em3dSolvers.Names(able)} run{(able.Count == 1 ? "s" : "")} and {solver} " +
               $"does not.{why} Set the setup's solver to {able[0].DisplayName}.";
    }

    /// <summary>
    /// The wave in EMerge's spherical convention ('SPH'): k̂ = (sinθ cosφ, sinθ sinφ, cosθ) and Ê = cosψ θ̂ + sinψ φ̂, in radians.
    /// Along ±z θ̂ has no unique direction, so φ is chosen there to put Ê along θ̂ (ψ = 0).
    /// </summary>
    public static (double Theta, double Phi, double Psi) SphAngles(Em3dPlaneWave wave)
    {
        var k = wave.Direction;
        var e = wave.EField;
        double theta = Math.Acos(Math.Clamp(k.Z, -1, 1));
        double sinT = Math.Sin(theta);
        double phi;
        if (sinT < 1e-12)
        {
            // θ̂ = ±(cosφ, sinφ, 0): + at θ = 0, − at θ = π
            double sign = k.Z > 0 ? 1 : -1;
            phi = Math.Atan2(sign * e.Y, sign * e.X);
            return (theta, phi, 0);
        }
        phi = Math.Atan2(k.Y, k.X);
        var thetaHat = new Point3(Math.Cos(theta) * Math.Cos(phi), Math.Cos(theta) * Math.Sin(phi), -sinT);
        var phiHat = new Point3(-Math.Sin(phi), Math.Cos(phi), 0);
        double psi = Math.Atan2(Dot(e, phiHat), Dot(e, thetaHat));
        return (theta, phi, psi);
    }

    /// <summary>k̂ and Ê of the SPH angles: the inverse of <see cref="SphAngles"/>, the formula EMerge's own background field uses.</summary>
    public static (Point3 K, Point3 E) FromSphAngles(double theta, double phi, double psi)
    {
        var k = new Point3(Math.Sin(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Sin(phi), Math.Cos(theta));
        var e = new Point3(Math.Cos(theta) * Math.Cos(phi) * Math.Cos(psi) - Math.Sin(phi) * Math.Sin(psi),
                           Math.Cos(theta) * Math.Sin(phi) * Math.Cos(psi) + Math.Cos(phi) * Math.Sin(psi),
                           -Math.Sin(theta) * Math.Cos(psi));
        return (k, e);
    }

    /// <summary>
    /// The closed surface a scattering run's far field is integrated over: a box in the air, halfway between what the problem holds
    /// and its air box on every side. It is away from the absorber, whose own reflection it would otherwise read (measured on a
    /// sphere: up to 1.8 dB on the back-scattered value with the absorbing faces as the surface, 0.2 dB with this box). Null with why
    /// when no such box fits: something reaches the air box.
    /// </summary>
    public static (Point3 Min, Point3 Max)? IntegrationBox(Em3dProblem problem, out string? why)
    {
        why = null;
        var lo = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var hi = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        void Take(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            lo[0] = Math.Min(lo[0], x0); lo[1] = Math.Min(lo[1], y0); lo[2] = Math.Min(lo[2], z0);
            hi[0] = Math.Max(hi[0], x1); hi[1] = Math.Max(hi[1], y1); hi[2] = Math.Max(hi[2], z1);
        }
        foreach (var s in problem.Solids)
            if (Em3dProblem.Bounds(s.Primitive) is var (x0, y0, z0, x1, y1, z1)) Take(x0, y0, z0, x1, y1, z1);
        foreach (var sh in problem.Sheets)
            if (sh.WorldBounds() is var (x0, y0, z0, x1, y1, z1)) Take(x0, y0, z0, x1, y1, z1);
        if (double.IsInfinity(lo[0]))
        {
            why = "the problem holds nothing to scatter the wave";
            return null;
        }
        var b = problem.Boundary;
        double[] bmin = [b.Min.X, b.Min.Y, b.Min.Z], bmax = [b.Max.X, b.Max.Y, b.Max.Z];
        double size = Math.Max(bmax[0] - bmin[0], Math.Max(bmax[1] - bmin[1], bmax[2] - bmin[2]));
        var min = new double[3];
        var max = new double[3];
        string[] axes = ["x", "y", "z"];
        for (int a = 0; a < 3; a++)
        {
            double gapLo = lo[a] - bmin[a], gapHi = bmax[a] - hi[a];
            if (gapLo <= 1e-6 * size || gapHi <= 1e-6 * size)
            {
                why = $"the model reaches the air box's {(gapLo <= 1e-6 * size ? axes[a] + "min" : axes[a] + "max")} face, so no closed surface " +
                      "in the air can enclose it; pad the air box on that side";
                return null;
            }
            min[a] = lo[a] - gapLo / 2;
            max[a] = hi[a] + gapHi / 2;
        }
        return (new Point3(min[0], min[1], min[2]), new Point3(max[0], max[1], max[2]));
    }

    /// <summary>The far-field grid's steps, degrees: the setup's own when it states them, else 2° each (Hertzon's own default sphere).</summary>
    public const double DefaultStepDeg = 2;

    internal static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
