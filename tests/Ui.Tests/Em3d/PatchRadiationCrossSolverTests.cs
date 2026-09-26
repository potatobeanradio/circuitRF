// brief-em3d-31 §5 — one patch antenna from the shipped examples, planar against FDTD 3D: broadside directivity and
// the E- and H-plane beamwidths. Two solvers that share no algebra — kernel B's closed-form spectral transform of
// rooftop currents over an infinite grounded slab, and openEMS's time-domain fields transformed off a box by
// circuitRF's own surface integral — asked the same question.
//
// THE SAME QUESTION means the same physics. The planar kernel's ground and substrate are laterally infinite; the
// example as shipped draws a 40 × 40 mm ground plane, which the 3D generator builds as finite metal. So the gated
// comparison removes the drawn ground: the generator then makes the ground-reference layer the air box's PEC
// floor and runs the substrate into the absorber — the planar model, in 3D. The as-shipped board is run too and
// only RECORDED: the difference between the two 3D runs is the finite ground's effect, which is a property of the
// board rather than of either solver.

using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em3d;

public sealed class PatchRadiationCrossSolverTests(ITestOutputHelper output) : IDisposable
{
    private const double FGHz = 5.85;
    private readonly string _root = Directory.CreateTempSubdirectory("crf-em3d31x-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from))
            if (Path.GetFileName(d) != "results") Copy(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private (DataSet Data, IReadOnlyList<string> Notes) Run(string ws, Action<EmSetup> edit, string name)
    {
        string cem = Path.Combine(ws, "patch", "em", "patch-5p8GHz.cem");
        var setup = EmSetupPersistence.LoadFromFile(cem);
        edit(setup);
        var res = EmSetupResolver.Resolve(cem, setup.LayoutRef, Path.Combine(ws, ".cws"), new TechnologyCache());
        Assert.NotNull(res.Source);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = EmRunService.Run(setup, res.Source!, Path.Combine(ws, "results-" + name));
        Assert.True(result.Status == EmRunStatus.Ok, result.Error);
        output.WriteLine($"── {name}: {watch.Elapsed.TotalSeconds:F0} s");
        foreach (string n in (result.Notes ?? []).Where(n => n.Contains("air box", StringComparison.OrdinalIgnoreCase) ||
                                                            n.Contains("Radiation pattern", StringComparison.Ordinal) ||
                                                            n.Contains("crosses", StringComparison.Ordinal)))
            output.WriteLine("   " + n);
        return (result.Data!, result.Notes ?? []);
    }

    /// <summary>D, and the −3 dB width of the cut through broadside at <paramref name="phiDeg"/> (the φ and φ+180°
    /// halves), read off the published U cube at <see cref="FGHz"/> — the same arithmetic for every solver.</summary>
    private static (double D, double Bw0, double Bw90) Read(DataSet ds)
    {
        var g = ds.CubesIn(PlanarFarField.Group);
        var u = g["U"];
        double[] f = u.Axes[0].Values, th = u.Axes[1].Values, ph = u.Axes[2].Values;
        int fi = Array.FindIndex(f, x => Math.Abs(x - FGHz * 1e9) < 1e3);
        Assert.True(fi >= 0, $"no pattern at {FGHz} GHz");
        var vals = u.RealValues;
        double U(int t, int p) => vals[((fi * th.Length + t) * ph.Length + p) * u.Axes[3].Values.Length];
        int nt90 = Array.FindLastIndex(th, x => x <= 90 + 1e-9);
        double Width(double phiDeg)
        {
            int pf = Array.FindIndex(ph, x => Math.Abs(x - phiDeg) < 1e-6), pb = Array.FindIndex(ph, x => Math.Abs(x - (phiDeg + 180) % 360) < 1e-6);
            var prof = new List<(double T, double V)>();
            for (int t = nt90; t > 0; t--) prof.Add((-th[t], U(t, pb)));
            for (int t = 0; t <= nt90; t++) prof.Add((th[t], U(t, pf)));
            int top = prof.IndexOf(prof.MaxBy(x => x.V));
            double half = prof[top].V / 2;
            double Cross(int dir)
            {
                for (int i = top; i + dir >= 0 && i + dir < prof.Count; i += dir)
                    if (prof[i + dir].V < half)
                        return prof[i].T + (prof[i + dir].T - prof[i].T) * (prof[i].V - half) / (prof[i].V - prof[i + dir].V);
                return double.NaN;
            }
            return Cross(+1) - Cross(-1);
        }
        var d = g["DirectivityDbi"];
        return (d.RealValues[fi * d.Axes[1].Values.Length], Width(0), Width(90));
    }

    /// <summary>U(θ)/U(peak) in dB along the cut at <paramref name="phiDeg"/>, at the θ values given (negative θ on
    /// the φ + 180° half).</summary>
    private static double[] Cut(DataSet ds, double phiDeg, double[] thetas)
    {
        var u = ds.CubesIn(PlanarFarField.Group)["U"];
        double[] f = u.Axes[0].Values, th = u.Axes[1].Values, ph = u.Axes[2].Values;
        int fi = Array.FindIndex(f, x => Math.Abs(x - FGHz * 1e9) < 1e3);
        var vals = u.RealValues;
        double U(int t, int p) => vals[((fi * th.Length + t) * ph.Length + p) * u.Axes[3].Values.Length];
        int pf = Array.FindIndex(ph, x => Math.Abs(x - phiDeg) < 1e-6), pb = Array.FindIndex(ph, x => Math.Abs(x - (phiDeg + 180) % 360) < 1e-6);
        double peak = 0;
        for (int t = 0; t < th.Length && th[t] <= 90; t++) peak = Math.Max(peak, Math.Max(U(t, pf), U(t, pb)));
        return [.. thetas.Select(q => 10 * Math.Log10(U(Array.FindIndex(th, x => Math.Abs(x - Math.Abs(q)) < 1e-6), q < 0 ? pb : pf) / peak))];
    }

    private static double At(DataSet ds, string cube)
    {
        var c = ds.CubesIn(PlanarFarField.Group)[cube];
        int fi = Array.FindIndex(c.Axes[0].Values, x => Math.Abs(x - FGHz * 1e9) < 1e3);
        return c.RealValues[fi * c.Axes[1].Values.Length];
    }

    /// <summary>
    /// <b>The measurement, and what it gates.</b> At 5.85 GHz (brief-em3d-31, 2026-09-26): planar D 6.697 dBi,
    /// H-plane 78.3°, E-plane 146.1°; FDTD 3D over the PEC floor D 6.323 dBi, H-plane 78.0°, and NO half-power point
    /// in the E-plane within ±90°. The two gaps have one cause, and it is testable: over an unbounded substrate the
    /// planar kernel books the guided (surface) wave separately as <c>PowerSurfaceWave</c>, while the FDTD surface
    /// transform — whose side faces the substrate runs through — counts it as radiation, near the horizon and mostly
    /// along the E-plane. So:
    /// <list type="bullet">
    /// <item>the H-plane beamwidths agree within 3°;</item>
    /// <item>FDTD's D equals planar's D with planar's own surface-wave power added to its radiated power,
    ///   D − 10·log₁₀(1 + P_sw/P_rad), within 0.2 dB;</item>
    /// <item>the E-plane agrees in SHAPE within ±60° of broadside (0.5 dB), where the guided wave's contribution is
    ///   small; its −3 dB width is not compared, because the FDTD model has none to measure.</item>
    /// </list>
    /// The as-shipped board (a drawn 40 × 40 mm ground) measured D 7.188 dBi, H-plane 77.6°, E-plane 82.6°: the
    /// finite plane's own effect, which the planar kernel's beamwidth note predicts ("nearer 40°" either side).
    /// It costs 5.5 minutes, so it runs only with CRF_RECORD_FINITE_GROUND=1.
    /// </summary>
    [OpenEmsFact]
    [Trait("Category", "Benchmark")]
    public void ThePatch_PlanarAgainstFdtd3D_BroadsideDirectivityAndPlaneBeamwidthsAgree()
    {
        string example = Path.Combine(PalaceBackendTests.RepoRoot(), "testdata", "antenna");
        string ws = Path.Combine(_root, "ws");
        Copy(example, ws);

        // Planar, one point, the pattern on (the example's own .cem otherwise).
        var planar = Run(ws, s =>
        {
            string g = FGHz.ToString(System.Globalization.CultureInfo.InvariantCulture);
            s.Frequency = new CircuitRF.Core.Design.FrequencySpec(g, g, 1, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
            s.ResonanceSearch = false;
            s.AdaptiveSampling = false;
        }, "planar");

        void Fdtd(EmSetup s)
        {
            s.Solver3D = Em3dSolver.OpenEms;
            s.Frequency = new CircuitRF.Core.Design.FrequencySpec("5.35", "6.35", 3, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
        }
        if (Environment.GetEnvironmentVariable("CRF_RECORD_FINITE_GROUND") == "1")
        {
            var (dS, h0S, e90S) = Read(Run(ws, Fdtd, "fdtd-shipped").Data);
            output.WriteLine($"FDTD 3D, 40 mm ground    {dS,7:F3}   {h0S,9:F1}°   {e90S,9:F1}°   (recorded only)");
        }

        // The planar model's physics: the ground undrawn, so it is the PEC floor and the substrate is unbounded.
        string clay = Path.Combine(ws, "patch", "layout", "patch.clay");
        var doc = JsonNode.Parse(File.ReadAllText(clay))!;
        var shapes = doc["Shapes"]!.AsArray();
        shapes.Remove(shapes.Single(x => x!["Layer"]!["Layer"]!.GetValue<int>() == 2));
        File.WriteAllText(clay, doc.ToJsonString());
        var floor = Run(ws, Fdtd, "fdtd-floor");
        Assert.Contains(floor.Notes, n => n.Contains("PEC plane", StringComparison.Ordinal));

        var (dP, h0P, e90P) = Read(planar.Data);
        var (dF, h0F, e90F) = Read(floor.Data);
        double sw = At(planar.Data, "PowerSurfaceWave") / At(planar.Data, "PowerRadiated");
        double dPsw = dP - 10 * Math.Log10(1 + sw);
        output.WriteLine($"at {FGHz} GHz            D (dBi)   H-plane φ=0   E-plane φ=90");
        output.WriteLine($"planar (infinite)        {dP,7:F3}   {h0P,9:F1}°   {e90P,9:F1}°   P_sw/P_rad = {sw:P2} → D with it {dPsw:F3} dBi");
        output.WriteLine($"FDTD 3D, PEC floor       {dF,7:F3}   {h0F,9:F1}°   {e90F,9:F1}°");
        double[] thetas = [-60, -45, -30, 0, 30, 45, 60];
        var eP = Cut(planar.Data, 90, thetas);
        var eF = Cut(floor.Data, 90, thetas);
        output.WriteLine("E-plane U/U_peak (dB) at θ = " + string.Join(", ", thetas) + ": planar " +
                         string.Join(" ", eP.Select(v => v.ToString("F2"))) + " | FDTD " + string.Join(" ", eF.Select(v => v.ToString("F2"))));

        Assert.True(Math.Abs(h0F - h0P) <= 3, $"H-plane {h0P:F1}° vs {h0F:F1}°");
        Assert.True(Math.Abs(dF - dPsw) <= 0.2, $"D FDTD {dF:F3} vs planar-with-surface-wave {dPsw:F3} dBi");
        for (int i = 0; i < thetas.Length; i++)
            Assert.True(Math.Abs(eF[i] - eP[i]) <= 0.5, $"E-plane at θ = {thetas[i]}°: {eP[i]:F2} vs {eF[i]:F2} dB");
    }
}
