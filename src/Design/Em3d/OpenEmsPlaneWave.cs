// brief-hzn-12 (follow-up) — a scattering run on openEMS: its plane-wave excitation (CSX excitation type 10, a total-field /
// scattered-field box), where that box sits, and how the run's far field becomes a radar cross-section.
//
// WHERE THE BOX GOES. openEMS injects the wave on the faces of a box: inside it the field is the total field, outside it the
// scattered field alone. Every object of the problem must be inside it (with a line to spare), and the far-field surface
// (OpenEmsFarField.Place, three cells inside the PML) outside it (with a line to spare), so the surface sees only what the model
// scatters. The box takes the grid line halfway between the two on each side; with no line between them it is refused, naming the
// face to pad.
//
// THE PHASE. openEMS refers the wave's phase to a grid line one cell outside the box's low face on each axis the wave travels
// along positively (its high face's, one cell out, where it travels negatively) — read from openEMS's own TF/SF extension, which
// circuitRF drives as a separate program. The far field is moved from that origin to the setup's phase origin by
// e^{−jk k̂·(r_setup − r_openEMS)}, and divided by the excitation's own spectrum (openEMS's 'et' file, the same DFT the ports take),
// so it is the far field of a unit wave with zero phase at the setup's origin: σ = 4π |F|², F = r·e^{jkr}·E in volts.

using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>Where the plane wave's box sits on the grid, and the point openEMS refers its phase to (metres).</summary>
public sealed record OpenEmsPlaneWaveBox(Point3 Min, Point3 Max, Point3 PhaseOrigin);

public static class OpenEmsPlaneWave
{
    /// <summary>openEMS's file of the excitation signal, written beside every run's probes.</summary>
    public const string ExcitationFile = "et";

    /// <summary>The box between the model and <paramref name="surface"/>, or why there is no room for one.</summary>
    public static (OpenEmsPlaneWaveBox? Box, string? Refusal) Place(Em3dProblem problem, FdtdGridResult grid, Nf2ffSurface surface)
    {
        var wave = problem.PlaneWave!;
        double[] lo = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        double[] hi = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        void Take(double x0, double y0, double z0, double x1, double y1, double z1)
        {
            lo[0] = Math.Min(lo[0], x0); lo[1] = Math.Min(lo[1], y0); lo[2] = Math.Min(lo[2], z0);
            hi[0] = Math.Max(hi[0], x1); hi[1] = Math.Max(hi[1], y1); hi[2] = Math.Max(hi[2], z1);
        }
        foreach (var s in problem.Solids) { var b = Em3dProblem.Bounds(s.Primitive); Take(b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1); }
        foreach (var sh in problem.Sheets) { var b = sh.WorldBounds(); Take(b.X0, b.Y0, b.Z0, b.X1, b.Y1, b.Z1); }
        if (double.IsInfinity(lo[0])) return (null, "The problem holds nothing to scatter the plane wave.");

        var axes = new[] { grid.X, grid.Y, grid.Z };
        double[] sLo = [surface.Min.X, surface.Min.Y, surface.Min.Z], sHi = [surface.Max.X, surface.Max.Y, surface.Max.Z];
        double[] k = [wave.Direction.X, wave.Direction.Y, wave.Direction.Z];
        var bLo = new double[3];
        var bHi = new double[3];
        var origin = new double[3];
        string[] faces = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        for (int a = 0; a < 3; a++)
        {
            var lines = axes[a].Lines;
            double tol = 1e-9 * Math.Max(1e-6, lines[^1] - lines[0]);
            int iSLo = Nearest(lines, sLo[a]), iSHi = Nearest(lines, sHi[a]);
            int iOLo = 0, iOHi = lines.Count - 1;
            for (int i = 0; i < lines.Count; i++) if (lines[i] <= lo[a] + tol) iOLo = i;
            for (int i = lines.Count - 1; i >= 0; i--) if (lines[i] >= hi[a] - tol) iOHi = i;
            // the box's face: a line strictly between the surface and the model's last line, with a line to spare on both sides
            int lowMost = iSLo + 1, lowLeast = iOLo - 1;
            int highLeast = iOHi + 1, highMost = iSHi - 1;
            if (lowMost > lowLeast)
                return (null, Tight(faces[2 * a], lowMost - lowLeast));
            if (highLeast > highMost)
                return (null, Tight(faces[2 * a + 1], highLeast - highMost));
            int tLo = (lowMost + lowLeast) / 2, tHi = (highLeast + highMost + 1) / 2;
            bLo[a] = lines[tLo];
            bHi[a] = lines[tHi];
            origin[a] = k[a] >= 0 ? lines[tLo - 1] : lines[tHi + 1];
        }
        return (new OpenEmsPlaneWaveBox(new Point3(bLo[0], bLo[1], bLo[2]), new Point3(bHi[0], bHi[1], bHi[2]),
                                        new Point3(origin[0], origin[1], origin[2])), null);
    }

    private static string Tight(string face, int short_)
        => $"The setup is driven by a plane wave, and on the {face} side there is no grid line between the model and the far-field " +
           $"surface for openEMS's plane-wave box ({short_} line{(short_ == 1 ? "" : "s")} short): the wave is injected on that box, " +
           $"which must enclose the model with the surface outside it. Pad the setup's AirBox on its {face} face.";

    /// <summary>The excitation property: openEMS's plane wave (type 10) on the box, E along the wave's E-field.</summary>
    public static string Excitation(Em3dPlaneWave wave, OpenEmsPlaneWaveBox box, double f0, (int R, int G, int B, int A) colour)
    {
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string P(string tag, Point3 q) => $"<{tag} X=\"{R(q.X)}\" Y=\"{R(q.Y)}\" Z=\"{R(q.Z)}\" />";
        var e = wave.EField;
        var k = wave.Direction;
        var sb = new StringBuilder();
        sb.Append($"            <Excitation ID=\"{{ID0}}\" Name=\"plane_wave\" Number=\"0\" Enabled=\"1\" Frequency=\"{R(f0)}\" Delay=\"0\" " +
                  $"Type=\"10\" Excite=\"{R(e.X)},{R(e.Y)},{R(e.Z)}\" PropDir=\"{R(k.X)},{R(k.Y)},{R(k.Z)}\">\n");
        sb.Append($"                <FillColor R=\"{colour.R}\" G=\"{colour.G}\" B=\"{colour.B}\" a=\"{colour.A}\" />\n");
        sb.Append($"                <EdgeColor R=\"{colour.R}\" G=\"{colour.G}\" B=\"{colour.B}\" a=\"{colour.A}\" />\n");
        sb.Append("                <Primitives>\n");
        sb.Append("                    <Box Priority=\"0\">\n");
        sb.Append($"                        {P("P1", box.Min)}\n");
        sb.Append($"                        {P("P2", box.Max)}\n");
        sb.Append("                    </Box>\n");
        sb.Append("                </Primitives>\n");
        sb.Append("                <Weight X=\"1\" Y=\"1\" Z=\"1\" />\n");
        sb.Append("            </Excitation>\n");
        return sb.ToString();
    }

    /// <summary>
    /// The radar cross-section of a finished run: the surface's dumps at each frequency, divided by the incident wave as it arrives at
    /// the setup's phase origin, transformed on the cross-section grid and in the monostatic direction. Null with why on failure.
    /// </summary>
    public static DataSet? Result(Em3dProblem problem, Nf2ffSurface surface, OpenEmsPlaneWaveBox box, string dir, double[] freqs,
                                  int? maxCores, RunControl? control, CancellationToken ct, out string? error)
    {
        var wave = problem.PlaneWave!;
        var et = OpenEmsRun.ReadProbe(Path.Combine(dir, ExcitationFile), out error);
        if (et is null) return null;
        var spectrum = FdtdPortTransform.Dft(et, freqs);
        var grid = FarFieldStage.Sphere(wave.ThetaStepDeg, wave.PhiStepDeg);
        var back = wave.Backward;
        double tb = Math.Acos(Math.Clamp(back.Z, -1, 1)) * 180 / Math.PI;
        double pb = (Math.Atan2(back.Y, back.X) * 180 / Math.PI + 360) % 360;
        var monoGrid = new PlanarFarFieldGrid([tb], [pb]);
        int nt = grid.ThetaDeg.Count, np = grid.PhiDeg.Count, nf = freqs.Length;
        var far = new Complex[nf * 2 * nt * np];
        var mono = new Complex[nf * 2];
        var d = new Point3(wave.PhaseOrigin.X - box.PhaseOrigin.X, wave.PhaseOrigin.Y - box.PhaseOrigin.Y, wave.PhaseOrigin.Z - box.PhaseOrigin.Z);
        double kd = wave.Direction.X * d.X + wave.Direction.Y * d.Y + wave.Direction.Z * d.Z;
        control?.BeginStage("radar cross-section (openEMS surface transform)", nf, "frequencies");
        for (int i = 0; i < nf; i++)
        {
            ct.ThrowIfCancellationRequested();
            double k0 = 2 * Math.PI * freqs[i] / 299_792_458.0;
            // the incident wave at the setup's origin: the excitation's spectrum, moved from openEMS's origin
            Complex arriving = spectrum[i] * Complex.FromPolarCoordinates(1, -k0 * kd);
            if (!(arriving.Magnitude > 0))
            {
                error = $"openEMS's excitation carries no energy at {freqs[i] / 1e9:G6} GHz, so the cross-section there has nothing to be referred to.";
                return null;
            }
            var samples = OpenEmsFarField.ReadSamples(dir, surface, freqs[i], 0.5 / arriving, out string? readError);
            if (samples is null) { error = readError; return null; }
            var p = NearToFarField.Transform(samples, freqs[i], grid, 0, surface.Floor, surface.FloorZ, maxCores, ct);
            var m = NearToFarField.Transform(samples, freqs[i], monoGrid, 0, surface.Floor, surface.FloorZ, maxCores, ct);
            // the far field of a unit wave, scaled to the setup's amplitude: σ does not depend on it, the stored far field does
            for (int t = 0; t < nt; t++)
                for (int q = 0; q < np; q++)
                {
                    int g = grid.IndexOf(t, q);
                    far[((i * 2 + 0) * nt + t) * np + q] = p.ETheta[g] * wave.E0;
                    far[((i * 2 + 1) * nt + t) * np + q] = p.EPhi[g] * wave.E0;
                }
            mono[i * 2] = m.ETheta[0] * wave.E0;
            mono[i * 2 + 1] = m.EPhi[0] * wave.E0;
            control?.TickStage();
        }
        return Em3dScatteringResult.Build(freqs, [.. grid.ThetaDeg], [.. grid.PhiDeg], far, mono, wave);
    }

    /// <summary>What the run says of the wave: its vectors, openEMS's phase origin and the move to the setup's, and σ's definition.</summary>
    public static IReadOnlyList<string> Notes(Em3dProblem problem, OpenEmsPlaneWaveBox box)
    {
        var w = problem.PlaneWave!;
        static string G(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string V(Point3 p) => $"({G(p.X)}, {G(p.Y)}, {G(p.Z)})";
        static string Mm(Point3 p) => $"({G(p.X * 1e3)}, {G(p.Y * 1e3)}, {G(p.Z * 1e3)})";
        return
        [
            $"The plane wave travels along {V(w.Direction)} with E along {V(w.EField)}, {G(w.E0)} V/m peak. openEMS refers its phase to " +
            $"{Mm(box.PhaseOrigin)} mm, a grid line outside its box; the far field is moved from there to the setup's phase origin, " +
            $"{Mm(w.PhaseOrigin)} mm, and divided by the excitation's own spectrum (its '{ExcitationFile}' file), so it is the far field of " +
            "the wave the setup states.",
            $"The radar cross-section is σ = 4π r² |E_s|² / |E_i|² as r → ∞: bistatic over θ 0–180° in {G(w.ThetaStepDeg)}° and " +
            $"φ 0–{G(360 - w.PhiStepDeg)}° in {G(w.PhiStepDeg)}° steps, and monostatic in the direction straight back along the incidence, " +
            "computed there.",
        ];
    }

    private static int Nearest(IReadOnlyList<double> lines, double v)
    {
        int best = 0;
        for (int i = 1; i < lines.Count; i++) if (Math.Abs(lines[i] - v) < Math.Abs(lines[best] - v)) best = i;
        return best;
    }
}
