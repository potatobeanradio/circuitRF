// brief-emi-7 — EMerge's fields, its eigenmode extras and its radiation pattern: what the run asks the driver for, what the
// driver says it wrote (run.json's "fieldSteps"), and what circuitRF makes of that.
//
// THE FILES ARE PALACE'S LAYOUT (R-emi7-1). The driver writes each step as postpro/paraview/<problem>/…/Cycle<k>/data.pvtu
// with one piece, an UnstructuredGrid of Lagrange tetrahedra in exactly the form VtuReader reads for Palace; circuitRF writes
// the .pvd collections (WriteCollections), because only circuitRF knows which of EMerge's eigen solutions it reports and
// under what number (EmergeResults.FilterModes). So every field plot — clip plane, surface, the 3D view, `render --field` —
// reads an EMerge run with the reader it already had. brief-emi-11: beside each step, a boundary step of Lagrange triangles on
// the conductors' walls and the sheets (<problem>_boundary/…, J_s and Q_s), with their face groups in groups.json, as Palace's.
//
// THE SCALE IS PALACE'S (R-emi7-2). A driven step is at PalaceDrive.IncidentPowerW time-averaged incident power at the driven
// port, peak phasors: EMerge drives a lumped port with an incident wave of √(2·P·Z0) peak, P its power (1 W), so the driver
// multiplies by √(IncidentPowerW / P). A mode is normalised as Palace normalises one: ∫ε|E|² over the mesh is Lc/c₀, with Lc
// the mesh's largest extent (Palace's default characteristic length — its field unit is √Z₀/Lc, its energy unit 1 W·Lc/c₀),
// so the two solvers' mode plots of one cavity read the same peak |E|.
//
// THE EXTRAS ARE ARITHMETIC (R-emi7-3). The driver integrates Re(ε)|E|² over each material's cells (EMerge's own
// tetrahedral quadrature) and each lumped port's voltage in each mode; participation and each port's external Q are computed
// here from those, so the arithmetic is gated without Python.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

/// <summary>What an EMerge run saves of its fields: a driven run's frequencies (each a direct solve), whether the step files
/// are written (<c>SaveFieldsGHz: []</c> writes none), whether the far field is integrated at them; an eigenmode run's mode
/// normalisation.</summary>
public sealed record EmergeFieldRequest(IReadOnlyList<double> FrequenciesHz, bool Write, bool FarField, double ModeEnergyJ);

/// <summary>One step the driver reports: a driven (port, frequency) with that port's total and incident voltage at the step's
/// scale, or a mode (its index in <c>modes.npy</c>) with its energy per material and each lumped port's voltage. Pvtu is the
/// step's file relative to the run directory, null when none was written.</summary>
public sealed record EmergeFieldStep(int? Port, double FrequencyHz, int? Mode, string? Pvtu, Complex? V, Complex? VInc,
                                     IReadOnlyList<double>? Energy, IReadOnlyList<Complex?>? PortV)
{
    /// <summary>brief-emi-11 — the boundary step beside <see cref="Pvtu"/> (J_s and Q_s on the conductors and sheets), relative to
    /// the run directory; null when none was written (no volume step, or no conductor or sheet face in the mesh).</summary>
    public string? BoundaryPvtu { get; init; }
}

/// <summary>Every step of a run, the bytes the driver wrote, and (eigenmode) the ports and materials its arrays are over.</summary>
public sealed record EmergeFieldFacts(IReadOnlyList<EmergeFieldStep> Steps, long Bytes, IReadOnlyList<int> PortNumbers,
                                      IReadOnlyList<double> PortR, IReadOnlyList<string> MaterialNames)
{
    public static EmergeFieldFacts None { get; } = new([], 0, [], [], []);
}

public static class EmergeFields
{
    /// <summary>The far field's angular step, degrees: the 1° sphere every 3D pattern circuitRF publishes is on.</summary>
    public const double FarFieldStepDeg = 1;

    /// <summary>The far field's arrays, beside run.json: E_θ and E_φ [f, port, 2, θ, φ] (r·E, at each step's scale), the angles
    /// (degrees) and the frequencies.</summary>
    public const string FarFieldFile = "ff_E.npy", ThetaFile = "ff_theta.npy", PhiFile = "ff_phi.npy", FrequencyFile = "ff_freq.npy";

    /// <summary>The driver's field files' root, under the run directory: Palace's own.</summary>
    public static string ParaviewDirectory(string runDir) => Path.Combine(runDir, PalaceConfigWriter.OutputDirectory, PalaceConfigWriter.FieldDirectory);

    /// <summary>Speed of light, m/s.</summary>
    private const double C0 = 299_792_458;

    // ── the request ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <paramref name="setup"/>'s run of <paramref name="problem"/> saves (null: nothing). Driven: the setup's
    /// <c>EMerge.SaveFieldsGHz</c>, or the sweep's centre when it names none, as Palace reads its own key; a radiation pattern
    /// is integrated at the same frequencies, and at the centre alone when the setup saves no field. Eigenmode: every mode,
    /// and its energies and port voltages always (participation and Q_ext need them even with no field file written).
    /// </summary>
    public static EmergeFieldRequest? Request(EmSetup setup, Em3dProblem problem)
    {
        var saved = setup.Emerge?.SaveFieldsGHz;
        bool write = saved is not { Count: 0 };
        if (problem.Type == Em3dProblemType.Eigenmode) return new([], write, false, ModeEnergyJ(problem));
        if (problem.Type != Em3dProblemType.Driven) return null;
        bool far = setup.RadiationPattern && FarFieldRefusal(problem) is null;
        var ghz = PalaceConfigWriter.SaveFrequenciesGHz(problem.Frequency, PalaceSettings.Default with { SaveFieldsGHz = saved });
        if (ghz.Count == 0 && far) ghz = PalaceConfigWriter.SaveFrequenciesGHz(problem.Frequency, PalaceSettings.Default);
        if (ghz.Count == 0) return null;
        return new([.. ghz.Select(g => g * 1e9)], write, far, 0);
    }

    /// <summary>Palace's mode normalisation for <paramref name="problem"/>'s mesh: ∫ε|E|² = Lc/c₀, J, with Lc the air box's
    /// largest extent (Palace's default characteristic length; the box is the mesh's bounding box in both solvers).</summary>
    public static double ModeEnergyJ(Em3dProblem problem)
    {
        var b = problem.Boundary;
        double lc = Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        return lc / C0;
    }

    /// <summary>What cannot be run of a section's field list, as sentences naming the key.</summary>
    public static IEnumerable<string> Problems(CemEmerge? section)
    {
        foreach (double f in section?.SaveFieldsGHz ?? [])
            if (!(f > 0) || double.IsInfinity(f))
                yield return $"EMerge.SaveFieldsGHz holds {f.ToString(CultureInfo.InvariantCulture)}; every save frequency must be a positive number of GHz.";
    }

    /// <summary>R-emi7-4 — why EMerge computes no far field for <paramref name="problem"/>, or null when it does: its far-field
    /// integral runs over the air box's absorbing faces, and they must close round the whole problem.</summary>
    public static string? FarFieldRefusal(Em3dProblem problem)
    {
        var f = problem.Boundary.Faces;
        var kinds = new[] { ("xmin", f.XMin), ("xmax", f.XMax), ("ymin", f.YMin), ("ymax", f.YMax), ("zmin", f.ZMin), ("zmax", f.ZMax) };
        if (kinds.FirstOrDefault(k => k.Item2 != Em3dBoundaryKind.Absorbing) is { Item1: not null } wall)
            return $"No radiation pattern from EMerge: the air box's {wall.Item1} face is {wall.Item2}, and EMerge's far-field " +
                   "integral runs over absorbing faces that must enclose the whole problem — it applies no image, so a conducting " +
                   "face leaves its surface open. FDTD 3D (openEMS) closes a conducting floor by its image and produces the " +
                   "pattern; or set every AirBox face to Absorbing (drawing the ground plane, if there is one).";
        return null;
    }

    // ── what the driver wrote ───────────────────────────────────────────────────────────────

    /// <summary>run.json's field steps; <see cref="EmergeFieldFacts.None"/> when it has none or cannot be read.</summary>
    public static EmergeFieldFacts ReadSteps(string runDir)
    {
        string path = Path.Combine(runDir, EmergeResults.RunFile);
        try
        {
            if (!File.Exists(path)) return EmergeFieldFacts.None;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (!r.TryGetProperty("fieldSteps", out var steps) || steps.ValueKind != JsonValueKind.Array) return EmergeFieldFacts.None;
            var list = new List<EmergeFieldStep>();
            foreach (var s in steps.EnumerateArray())
                list.Add(new EmergeFieldStep(
                    Int(s, "port"), Num(s, "frequencyHz") ?? double.NaN, Int(s, "mode"),
                    s.TryGetProperty("pvtu", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                    Cplx(s, "v"), Cplx(s, "vinc"),
                    s.TryGetProperty("energy", out var e) && e.ValueKind == JsonValueKind.Array ? [.. e.EnumerateArray().Select(x => x.GetDouble())] : null,
                    s.TryGetProperty("portV", out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(CplxOf)] : null)
                {
                    BoundaryPvtu = s.TryGetProperty("boundaryPvtu", out var bp) && bp.ValueKind == JsonValueKind.String ? bp.GetString() : null,
                });
            long bytes = r.TryGetProperty("fieldBytes", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : 0;
            int[] ports = r.TryGetProperty("portNumbers", out var pn) && pn.ValueKind == JsonValueKind.Array ? [.. pn.EnumerateArray().Select(x => x.GetInt32())] : [];
            double[] rs = r.TryGetProperty("portR", out var pr) && pr.ValueKind == JsonValueKind.Array ? [.. pr.EnumerateArray().Select(x => x.GetDouble())] : [];
            string[] mats = r.TryGetProperty("materialNames", out var mn) && mn.ValueKind == JsonValueKind.Array ? [.. mn.EnumerateArray().Select(x => x.GetString() ?? "")] : [];
            return new(list, bytes, ports, rs, mats);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return EmergeFieldFacts.None;
        }
    }

    /// <summary>Each port's reference impedance as the run's problem.json states it, by port number (empty when unreadable).</summary>
    public static Dictionary<int, Complex> PortZ0s(string runDir)
    {
        var map = new Dictionary<int, Complex>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, EmergeScriptWriter.ProblemFile)));
            if (doc.RootElement.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                foreach (var p in ports.EnumerateArray())
                    if (Int(p, "number") is { } n && Num(p, "z0") is { } z) map[n] = z;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return map;
    }

    private static int? Int(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static Complex? Cplx(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? CplxOf(v) : null;
    private static Complex? CplxOf(JsonElement v)
        => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 ? new Complex(v[0].GetDouble(), v[1].GetDouble()) : null;

    /// <summary>
    /// The .pvd collections, in Palace's layout: one per driven port (<c>driven/excitation_&lt;k&gt;/excitation_&lt;k&gt;.pvd</c>,
    /// each step's timestep its frequency in GHz) and the eigenmode one (<c>eigenmode/eigenmode.pvd</c>, timestep the 0-based
    /// REPORTED mode). <paramref name="modeSources"/> is each reported mode's index in <c>modes.npy</c> (EmergeModes.Sources),
    /// so a solution circuitRF dropped has no step and the rest are numbered as the result's table numbers them.
    /// brief-emi-11 — the boundary steps the same way, beside them (<c>driven_boundary/…</c>, <c>eigenmode_boundary/…</c>), each
    /// with its volume step's timestep: that is how FieldRun pairs a solution with its surfaces.
    /// </summary>
    public static void WriteCollections(string runDir, EmergeFieldFacts facts, IReadOnlyList<int>? modeSources)
    {
        string root = ParaviewDirectory(runDir);
        foreach (var (problem, file) in new (string, Func<EmergeFieldStep, string?>)[] { ("driven", s => s.Pvtu), ("driven_boundary", s => s.BoundaryPvtu) })
            foreach (var port in facts.Steps.Where(s => s.Port is not null && s.Pvtu is not null && file(s) is not null).GroupBy(s => s.Port!.Value))
            {
                string dir = Path.Combine(root, problem, $"excitation_{port.Key}");
                WritePvd(Path.Combine(dir, $"excitation_{port.Key}.pvd"),
                         port.OrderBy(s => s.FrequencyHz).Select(s => ((s.FrequencyHz / 1e9).ToString("R", CultureInfo.InvariantCulture),
                                                                        Relative(dir, Path.Combine(runDir, file(s)!)))));
            }
        if (modeSources is null) return;
        foreach (var (problem, file) in new (string, Func<EmergeFieldStep, string?>)[] { ("eigenmode", s => s.Pvtu), ("eigenmode_boundary", s => s.BoundaryPvtu) })
        {
            var modes = facts.Steps.Where(s => s.Mode is not null && s.Pvtu is not null && file(s) is not null).ToDictionary(s => s.Mode!.Value);
            if (modes.Count == 0) continue;
            string dir = Path.Combine(root, problem);
            WritePvd(Path.Combine(dir, problem + ".pvd"),
                     modeSources.Select((raw, i) => (Raw: raw, I: i)).Where(m => modes.ContainsKey(m.Raw))
                                .Select(m => (m.I.ToString(CultureInfo.InvariantCulture), Relative(dir, Path.Combine(runDir, file(modes[m.Raw])!)))));
        }
    }

    private static string Relative(string from, string to) => Path.GetRelativePath(from, to).Replace('\\', '/');

    private static void WritePvd(string path, IEnumerable<(string Timestep, string File)> steps)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>\n<VTKFile type=\"Collection\" version=\"2.2\" byte_order=\"LittleEndian\">\n<Collection>\n");
        foreach (var (t, f) in steps)
            sb.Append($"<DataSet timestep=\"{t}\" group=\"\" part=\"0\" file=\"{System.Security.SecurityElement.Escape(f)}\" name=\"mesh\"/>\n");
        sb.Append("</Collection>\n</VTKFile>\n");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Cells.AtomicFile.WriteAllText(path, sb.ToString());
    }

    /// <summary>Every field file of a run, and the far field's arrays, removed before the next one writes its own: a step an
    /// earlier run left would be read as this run's.</summary>
    public static void Clear(string runDir)
    {
        string post = Path.Combine(runDir, PalaceConfigWriter.OutputDirectory);
        if (Directory.Exists(post)) Directory.Delete(post, recursive: true);
        foreach (string f in new[] { FarFieldFile, ThetaFile, PhiFile, FrequencyFile, Em3d.GmshGeoWriter.GroupsFile })
            File.Delete(Path.Combine(runDir, f));
    }

    // ── eigenmode extras ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emi7-3 — participation: each mode's (row) share of its electric energy in each material (column) — the integral of
    /// Re(ε)|E|² over that material's cells over the whole mesh's. Rows of zero energy are NaN rather than a guess.
    /// </summary>
    public static double[,] Participation(IReadOnlyList<IReadOnlyList<double>> energies, IReadOnlyList<int> columns)
    {
        var p = new double[energies.Count, columns.Count];
        for (int i = 0; i < energies.Count; i++)
        {
            double total = energies[i].Sum();
            for (int d = 0; d < columns.Count; d++) p[i, d] = total > 0 ? energies[i][columns[d]] / total : double.NaN;
        }
        return p;
    }

    /// <summary>
    /// R-emi7-3 — a lumped port's external Q in a mode: ω·W / P_port with W = ½∫Re(ε)|E|² the mode's stored energy (peak
    /// phasors) and P_port = ½|V|²/R the power its resistance takes at the mode's own voltage across it — ω·R·∫ε|E|² / |V|²,
    /// which no normalisation of the mode moves. +∞ when the port sees no voltage (it does not load the mode).
    /// </summary>
    public static double ExternalQ(double frequencyHz, double energyIntegral, Complex? v, double r)
    {
        if (v is not { } volts || !(r > 0) || !(energyIntegral > 0)) return double.NaN;
        double v2 = volts.Magnitude * volts.Magnitude;
        return v2 > 0 ? 2 * Math.PI * frequencyHz * r * energyIntegral / v2 : double.PositiveInfinity;
    }

    /// <summary>The materials a participation table has columns for: those holding energy in some mode, in problem.json order.</summary>
    public static int[] EnergyColumns(IReadOnlyList<IReadOnlyList<double>> energies, int materials)
        => [.. Enumerable.Range(0, materials).Where(m => energies.Any(e => m < e.Count && e[m] > 0))];

    // ── the radiation pattern ───────────────────────────────────────────────────────────────

    /// <summary>
    /// R-emi7-4 — the far field the driver integrated, as the "farfield" group Palace's and openEMS's runs publish, into
    /// <paramref name="data"/>: each (frequency, port) normalised to 1 V across the driven port, its accepted power at that
    /// voltage (|V_inc|² − |V − V_inc|²)/(2·R·|V|²) and its reflection (V − V_inc)/V_inc, all of the step's own direct solve
    /// (Palace's arithmetic on port-V.csv's waves, PalacePattern). Null on success; otherwise why there is no pattern.
    /// </summary>
    public static string? PublishPattern(string runDir, EmergeFieldFacts facts, IReadOnlyList<Em3dPort> ports, EmSetup setup,
                                         DataSet data, ICollection<string> notes)
    {
        Complex[] e;
        int[] shape;
        double[] theta, phi, freqs;
        try
        {
            (e, shape) = PlainNpy.ReadComplex(Path.Combine(runDir, FarFieldFile));
            (theta, _) = PlainNpy.ReadReal(Path.Combine(runDir, ThetaFile));
            (phi, _) = PlainNpy.ReadReal(Path.Combine(runDir, PhiFile));
            (freqs, _) = PlainNpy.ReadReal(Path.Combine(runDir, FrequencyFile));
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"EMerge's far field could not be read: {x.Message}";
        }
        var stated = StatedReflections(data, freqs, ports);
        var points = PatternPoints(e, shape, theta, phi, freqs, facts, ports, out var grid, out string? error, stated);
        if (points is null) return error;
        if (stated is not null) data.AddToGroup(PlanarFarField.Group, Em3dRadiation.PortsTerminatedCube, DataCube.Scalar(1.0));
        foreach (string n in Em3dRadiation.Publish(data, "EMerge (FEM)", grid, [.. ports.Select(p => p.Number)], freqs, points,
                                             setup.ReferenceInputPowerDbm, hemisphere: false,
                                             frequencies: $"A pattern at each of the {freqs.Length} frequencies the fields were saved at " +
                                                          "(EMerge.SaveFieldsGHz; the sweep's centre when it names none)"))
            notes.Add(n);
        notes.Add("EMerge's far field is its own Stratton–Chu integral over the air box's absorbing faces, which are first order: " +
                  "the pattern is only as good as the field those faces leave unreflected, so a box close to the radiator reads " +
                  "its nulls and its back radiation high. Each frequency is a direct solve of its own after the sweep.");
        return null;
    }

    /// <summary>
    /// brief-cosim-pattern-3 — each port's S_kk at each pattern frequency, [freq][port]; null when a wave port's voltage line is its own quantity, or the run's S
    /// has no such frequency. The driver reads a lumped port's voltage with a field interpolation that can miss a point on the port's edge, where EMerge's own
    /// S does not (up to 2 % of |S| on a 2 mm gap), so V = V_inc·(1 + S_kk) is the voltage of this solve as its S states it.
    /// </summary>
    private static Complex[][]? StatedReflections(DataSet data, double[] freqs, IReadOnlyList<Em3dPort> ports)
    {
        if (ports.Any(p => p.Kind != Em3dPortKind.Lumped)) return null;
        if (data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S")) is not { } group) return null;
        var cube = data.CubesIn(group)["S"];
        int n = ports.Count;
        if (cube.Rank != 3 || cube.Axes[1].Values.Length != n) return null;
        var values = cube.ComplexValues;
        var sweep = cube.Axes[0].Values;
        var stated = new Complex[freqs.Length][];
        for (int i = 0; i < freqs.Length; i++)
        {
            int row = Array.FindIndex(sweep, f => Math.Abs(f - freqs[i]) <= 1e-9 * freqs[i]);
            if (row < 0) return null;
            stated[i] = [.. Enumerable.Range(0, n).Select(k => values[(row * n + k) * n + k])];
        }
        return stated;
    }

    /// <summary>The pattern points, row-major [freq, port], from the driver's arrays; null with a sentence when they do not
    /// fit the 1° sphere or a step is missing.</summary>
    internal static List<Em3dPatternPoint>? PatternPoints(Complex[] e, int[] shape, double[] theta, double[] phi, double[] freqs,
                                                          EmergeFieldFacts facts, IReadOnlyList<Em3dPort> ports,
                                                          out PlanarFarFieldGrid grid, out string? error, Complex[][]? stated = null)
    {
        var sphere = FarFieldStage.Sphere(FarFieldStepDeg, FarFieldStepDeg);
        grid = sphere;
        error = null;
        int nf = freqs.Length, np = ports.Count, nt = grid.ThetaDeg.Count, nph = grid.PhiDeg.Count;
        if (shape is not [var a, var b, 2, var c, var d] || a != nf || b != np || c != nt || d != nph ||
            theta.Length != nt || phi.Length != nph ||
            theta.Where((t, i) => Math.Abs(t - sphere.ThetaDeg[i]) > 1e-9).Any() || phi.Where((t, i) => Math.Abs(t - sphere.PhiDeg[i]) > 1e-9).Any())
        {
            error = $"EMerge's far field is [{string.Join(", ", shape)}] where [{nf}, {np}, 2, {nt}, {nph}] on the 1° sphere was expected.";
            return null;
        }
        var points = new List<Em3dPatternPoint>(nf * np);
        for (int i = 0; i < nf; i++)
            for (int k = 0; k < np; k++)
            {
                var step = facts.Steps.FirstOrDefault(s => s.Port == ports[k].Number &&
                                                           Math.Abs(s.FrequencyHz - freqs[i]) <= 1e-9 * freqs[i]);
                if (step is not { V: { } v, VInc: { } vi } || !(v.Magnitude > 0) || !(vi.Magnitude > 0))
                {
                    error = $"EMerge reported no voltage across port {ports[k].Number} at {(freqs[i] / 1e9).ToString("G6", CultureInfo.InvariantCulture)} GHz, " +
                            "so its pattern cannot be referred to the port.";
                    return null;
                }
                if (stated is not null) v = vi * (1 + stated[i][k]);
                double r = ports[k].Z0.Real;
                var eth = new Complex[nt * nph];
                var eph = new Complex[nt * nph];
                for (int it = 0; it < nt; it++)
                    for (int ip = 0; ip < nph; ip++)
                    {
                        int at = (((i * np + k) * 2) * nt + it) * nph + ip, g = grid.IndexOf(it, ip);
                        eth[g] = e[at] / v;
                        eph[g] = e[at + nt * nph] / v;
                    }
                double reflected = (v - vi).Magnitude;
                double accepted = (vi.Magnitude * vi.Magnitude - reflected * reflected) / (2 * r * v.Magnitude * v.Magnitude);
                points.Add(new Em3dPatternPoint(FarFieldStage.Pattern(grid, eth, eph, ports[k].Number, freqs[i]), accepted, (v - vi) / vi));
            }
        return points;
    }

    /// <summary>The bytes of the field files under <paramref name="runDir"/>, or null when there are none (Palace's
    /// FieldFilesBytes, which the completion line reports).</summary>
    public static long? Bytes(string runDir) => Em3dRunService.FieldFilesBytes(Path.Combine(runDir, PalaceConfigWriter.OutputDirectory));
}
