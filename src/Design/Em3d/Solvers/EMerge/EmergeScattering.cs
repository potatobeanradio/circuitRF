// brief-hzn-12 R-hzn12-2/4 — what EMerge's driver writes of a scattering run, read into the radar cross-section result: the far
// field on the integration box over the theta/phi grid, the monostatic far field, and the wave EMerge built read back against
// the one circuitRF asked for.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Engine.Em3d;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>The incident wave as EMerge built it, read back through its own object (run.json's <c>planeWaveReadback</c>).</summary>
public sealed record EmergePlaneWaveReadback(Point3 Direction, Point3 EField, double PhaseDeg, double E0, double MonostaticThetaDeg,
                                             double MonostaticPhiDeg, int Faces);

public static class EmergeScattering
{
    public const string FarFile = "rcs_E.npy", ThetaFile = "rcs_theta.npy", PhiFile = "rcs_phi.npy", MonostaticFile = "rcs_mono_E.npy";

    /// <summary>The result, or null with why when the arrays are missing or do not fit the sweep.</summary>
    public static DataSet? Read(string dir, double[] requestedHz, Em3dPlaneWave wave, out string? error)
    {
        error = null;
        try
        {
            var (f, _) = PlainNpy.ReadReal(Path.Combine(dir, EmergeResults.FrequencyFile));
            var (far, farShape) = PlainNpy.ReadComplex(Path.Combine(dir, FarFile));
            var (theta, _) = PlainNpy.ReadReal(Path.Combine(dir, ThetaFile));
            var (phi, _) = PlainNpy.ReadReal(Path.Combine(dir, PhiFile));
            var (mono, monoShape) = PlainNpy.ReadComplex(Path.Combine(dir, MonostaticFile));
            if (f.Length != requestedHz.Length || f.Where((x, k) => Math.Abs(x - requestedHz[k]) > 1e-9 * Math.Abs(requestedHz[k])).Any())
            {
                error = $"EMerge's {EmergeResults.FrequencyFile} does not list the {requestedHz.Length} frequencies the sweep asked for.";
                return null;
            }
            int[] want = [f.Length, 2, theta.Length, phi.Length];
            if (!farShape.SequenceEqual(want) || !monoShape.SequenceEqual(new[] { f.Length, 2 }))
            {
                error = $"EMerge's far-field arrays are [{string.Join(", ", farShape)}] and [{string.Join(", ", monoShape)}], not " +
                        $"[{string.Join(", ", want)}] and [{f.Length}, 2].";
                return null;
            }
            return Em3dScatteringResult.Build((double[])requestedHz.Clone(), theta, phi, far, mono, wave);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            error = $"EMerge's radar cross-section could not be read from '{dir}' ({e.Message}).";
            return null;
        }
    }

    /// <summary>run.json's read-back of the wave, or null when it has none or cannot be read.</summary>
    public static EmergePlaneWaveReadback? ReadBack(string dir)
    {
        string path = Path.Combine(dir, EmergeResults.RunFile);
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("planeWaveReadback", out var r)) return null;
            Point3 V(string k)
            {
                var a = r.GetProperty(k);
                return new Point3(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble());
            }
            int faces = root.TryGetProperty("rcsFaces", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            return new EmergePlaneWaveReadback(V("direction"), V("eField"), r.GetProperty("phaseDeg").GetDouble(), r.GetProperty("e0").GetDouble(),
                                               r.GetProperty("monostaticThetaDeg").GetDouble(), r.GetProperty("monostaticPhiDeg").GetDouble(), faces);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What the run says of the wave and the surface: the vectors EMerge built, the monostatic direction, and the box.</summary>
    public static IReadOnlyList<string> Notes(Em3dProblem problem, EmergePlaneWaveReadback? back)
    {
        var wave = problem.PlaneWave!;
        var notes = new List<string>();
        var (theta, phi, psi) = Em3dPlaneWaves.SphAngles(wave);
        notes.Add($"The plane wave travels along {Vec(wave.Direction)} with E along {Vec(wave.EField)}, {G(wave.E0)} V/m peak, its phase zero " +
                  $"at {VecMm(wave.PhaseOrigin)} mm; EMerge was given it as θ {G(theta * 180 / Math.PI)}°, φ {G(phi * 180 / Math.PI)}°, " +
                  $"ψ {G(psi * 180 / Math.PI)}° in its spherical convention.");
        notes.Add(back is null
            ? "EMerge's run reported no read-back of the wave it built, so it was not checked against the one asked for."
            : $"EMerge built the wave travelling along {Vec(back.Direction)} with E along {Vec(back.EField)}, {G(back.E0)} V/m, phase " +
              $"{G(back.PhaseDeg)}° at the phase origin, read back through its own field: the wave asked for. The monostatic direction is " +
              $"θ {G(back.MonostaticThetaDeg)}°, φ {G(back.MonostaticPhiDeg)}°.");
        if (Em3dPlaneWaves.IntegrationBox(problem, out _) is { } box)
            notes.Add($"The far field is read on a closed box in the air from {VecMm(box.Min)} to {VecMm(box.Max)} mm, halfway between " +
                      "the model and the air box, so the absorber's own reflection is not in it" +
                      (back is { Faces: > 0 } ? $" ({back.Faces} faces of the mesh)." : "."));
        notes.Add($"The radar cross-section is σ = 4π r² |E_s|² / |E_i|² as r → ∞: bistatic over θ 0–180° in {G(wave.ThetaStepDeg)}° and " +
                  $"φ 0–{G(360 - wave.PhiStepDeg)}° in {G(wave.PhiStepDeg)}° steps, and monostatic in the direction straight back " +
                  "along the incidence, computed there.");
        return notes;
    }

    /// <summary>The monostatic cross-section at each frequency, the line a user reads first.</summary>
    public static string Headline(DataSet data)
    {
        var mono = data[$"{Em3dScatteringResult.Group}.{Em3dScatteringResult.MonostaticDbsmCube}"];
        var f = mono.Axes[0].Values;
        var db = mono.RealValues;
        var parts = f.Select((hz, k) => $"{G(db[k])} dBsm at {G(hz / 1e9)} GHz").ToList();
        return parts.Count <= 4
            ? $"Monostatic radar cross-section: {string.Join(", ", parts)}."
            : $"Monostatic radar cross-section: {parts[0]} … {parts[^1]} ({parts.Count} frequencies).";
    }

    private static string G(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Vec(Point3 p) => $"({G(p.X)}, {G(p.Y)}, {G(p.Z)})";
    private static string VecMm(Point3 p) => $"({G(p.X * 1e3)}, {G(p.Y * 1e3)}, {G(p.Z * 1e3)})";
}
