// brief-emi-5 R-emi5-4 — what EMerge's run writes, read back, and the files circuitRF makes of it.
//
// The driver writes PLAIN arrays (freq.npy, S.npy, Z0.npy; modes.npy, Q.npy) and run.json. circuitRF builds the S DataSet,
// the Touchstone with the run's provenance and the grouped .npy from them, through the writers Palace's run uses, so the files
// land where EmRunService.ResolveSnpPath says. EMerge's own Touchstone writer is never used: one owner for port
// normalisation and provenance.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Em3d;

/// <summary>What the driver's <c>run.json</c> says of a run. Every field may be missing (an older driver, an EMerge that no
/// longer logs a line the driver reads); a missing one is left out of every sentence, never guessed.</summary>
public sealed record EmergeRunFacts(string? Version, string? SparseSolver, long? Tetrahedra, long? Unknowns, int? Samples,
                                    bool Adaptive, double? Error, bool? Converged, int? Modes, double? MeshSeconds,
                                    double? SolveSeconds, IReadOnlyList<string> Warnings)
{
    public static EmergeRunFacts None { get; } = new(null, null, null, null, null, false, null, null, null, null, null, []);

    /// <summary>brief-emi-9 — where the run factorised, as the driver read it back from EMerge's own log (the solver it
    /// called), never as asked; null when the run did not say.</summary>
    public EmergeDevice? Device { get; init; }

    /// <summary>brief-emw-3 — the adaptive sweep the run began on the GPU and stopped once it stalled, before solving it again
    /// on the CPU: how many frequencies it had solved, its best estimated error and where; null when it did not.</summary>
    public EmergeGpuStall? GpuStalled { get; init; }

    /// <summary>brief-emw-2 R-emw2-3a — each wave port's face asked for two modes at the sweep's top; empty when the run has no
    /// wave port or did not say.</summary>
    public IReadOnlyList<EmergeSecondMode> SecondModes { get; init; } = [];
}

/// <summary>brief-emw-3 — a stalled GPU sweep: its frequencies, its best estimated error, and the frequency count it reached it at.
/// <paramref name="Unusable"/>: it was stopped because a solve could not be trusted, not for a stall. <paramref name="Residual"/>:
/// that solve's relative residual, when the driver's check on it is what stopped the sweep (brief EMG-1); null when EMerge's GPU
/// solver flagged its own factorisation.</summary>
public sealed record EmergeGpuStall(int Samples, double? BestError, int BestAt, bool Unusable = false, double? Residual = null);

/// <summary>brief-emw-2 R-emw2-3a — one wave port's second-mode check: the modes EMerge kept at <see cref="FrequencyHz"/>
/// (it drops evanescent ones, so a second one kept propagates), each one's β, rad/m, the face's own mode first; or why the
/// check could not be made.</summary>
public sealed record EmergeSecondMode(int Port, double FrequencyHz, IReadOnlyList<Complex> Betas, string? Error)
{
    /// <summary>brief-emw-3 — how many of the kept modes are the face's own: its terminals' count on a terminal group, else 1.</summary>
    public int Own { get; init; } = 1;

    /// <summary>A kept solution with β below this fraction of k₀ is the 2D solve's null space, not a mode: on a finely meshed
    /// face EMerge returned one at β/k₀ 5e-5, ten times below any box mode's cutoff (src/Design/RESOLVED.md, EMW-2).</summary>
    public const double NullBetaFraction = 1e-3;

    /// <summary>The first kept mode past the face's own whose β is not the null space's; null when there is none.</summary>
    public Complex? Second => Error is null
        ? Betas.Skip(Own).Cast<Complex?>().FirstOrDefault(b => b!.Value.Real > NullBetaFraction * 2 * Math.PI * FrequencyHz / 299792458.0)
        : null;

    public bool Propagating => Second is not null;
}

/// <summary>A driven run's arrays: the requested frequencies, S as [f][i, j] over the ports in ascending number, each port's
/// reference impedance as EMerge states it [f][port] (brief-emw-2: a wave port's is its mode's Z_PV at that frequency), and
/// each port's propagation constant α + jβ [f][port] (0 for a lumped port).</summary>
public sealed record EmergeSweep(double[] FrequenciesHz, Complex[][,] S, Complex[][] Z0, Complex[][] Gamma);

/// <summary>R-emi5-4b — an eigenmode run's modes after filtering: what is kept, how many were dropped, and below what.
/// brief-emi-7 — <see cref="Sources"/> is each kept mode's index in <c>modes.npy</c>, which the driver's field steps are by.</summary>
public sealed record EmergeModes(IReadOnlyList<Em3dMode> Kept, int Dropped, double FloorHz, IReadOnlyList<int> Sources);

public static class EmergeResults
{
    /// <summary>The diagnostics group an EMerge <c>.npy</c> carries beside S.</summary>
    public const string Group = "emerge";

    /// <summary>
    /// The frequencies at which EMerge's S is exactly −I: every port a short and nothing passing between ports. A zero field
    /// solution reads that way, and it is what any solve that failed without raising leaves behind — a run on the designer's
    /// board wrote S = −I at all ten frequencies and was reported solved (designer round 28); brief-dfr28-3 traced the same
    /// result to one degenerate tetrahedron, which the driver now refuses at the first solve, so this is the last line of
    /// defence and names no cause. No structure with ports reads −I to 1e-12 at every entry, so the run is refused rather
    /// than written.
    /// </summary>
    public static int[] NoFieldFrequencies(EmergeSweep sweep, double tolerance = 1e-12)
    {
        var none = new List<int>();
        for (int k = 0; k < sweep.S.Length; k++)
        {
            var m = sweep.S[k];
            int n = m.GetLength(0);
            bool minusI = true;
            for (int i = 0; i < n && minusI; i++)
                for (int j = 0; j < n && minusI; j++)
                    minusI = (m[i, j] - (i == j ? -1.0 : 0.0)).Magnitude <= tolerance;
            if (minusI) none.Add(k);
        }
        return [.. none];
    }

    public const string FrequencyFile = "freq.npy", SFile = "S.npy", Z0File = "Z0.npy", GammaFile = "gamma.npy", ModesFile = "modes.npy",
                        QFile = "Q.npy";
    public const string RunFile = "run.json";

    /// <summary>EMerge's stdout and stderr, in the run directory.</summary>
    public const string LogFile = "emerge.log";

    /// <summary>
    /// R-emi5-4b — how far below the search start a solution may sit and still be reported. EMerge's eigensolver looks
    /// either side of the frequency it is given, where Palace reports only modes above it; and it returns null-space
    /// solutions near 0 Hz (the overview's probe saw three at ~0.05 GHz with Q ≈ 0.2). A mode a few per cent under the start
    /// is still the one a user set the start below, so the floor is the start less this fraction.
    /// </summary>
    public const double ModeFloorMargin = 0.05;

    // ── run.json ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The run's facts; <see cref="EmergeRunFacts.None"/> when <c>run.json</c> is missing or unreadable.</summary>
    public static EmergeRunFacts ReadFacts(string runDir)
    {
        string path = Path.Combine(runDir, RunFile);
        try
        {
            if (!File.Exists(path)) return EmergeRunFacts.None;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? Long(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            double? Num(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
            bool? Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            var warnings = r.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? [.. w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                : new List<string>();
            var second = new List<EmergeSecondMode>();
            if (r.TryGetProperty("secondModes", out var sm) && sm.ValueKind == JsonValueKind.Array)
                foreach (var m in sm.EnumerateArray())
                {
                    if (!m.TryGetProperty("port", out var port) || !m.TryGetProperty("frequencyHz", out var f)) continue;
                    var betas = m.TryGetProperty("betas", out var b) && b.ValueKind == JsonValueKind.Array
                        ? [.. b.EnumerateArray().Select(x => new Complex(x[0].GetDouble(), x[1].GetDouble()))]
                        : new List<Complex>();
                    string? error = m.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    int own = m.TryGetProperty("own", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 1;
                    second.Add(new(port.GetInt32(), f.GetDouble(), betas, error) { Own = own });
                }
            return new(Str("emerge"), Str("sparseSolver"), Long("tets"), Long("unknowns"), (int?)Long("samples"), Bool("adaptive") ?? false,
                       Num("error"), Bool("converged"), (int?)Long("modes"), Num("meshSeconds"), Num("solveSeconds"), warnings)
                   {
                       Device = Str("device") is "gpu" ? EmergeDevice.Gpu : Str("device") is "cpu" ? EmergeDevice.Cpu : null, SecondModes = second,
                       GpuStalled = r.TryGetProperty("gpuStalled", out var g) && g.ValueKind == JsonValueKind.Object
                           ? new EmergeGpuStall(g.TryGetProperty("samples", out var gs) && gs.ValueKind == JsonValueKind.Number ? gs.GetInt32() : 0,
                                                g.TryGetProperty("bestError", out var ge) && ge.ValueKind == JsonValueKind.Number ? ge.GetDouble() : null,
                                                g.TryGetProperty("bestAt", out var ga) && ga.ValueKind == JsonValueKind.Number ? ga.GetInt32() : 0,
                                                g.TryGetProperty("unusable", out var gu) && gu.ValueKind == JsonValueKind.True,
                                                g.TryGetProperty("residual", out var gr) && gr.ValueKind == JsonValueKind.Number ? gr.GetDouble() : null)
                           : null,
                   };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return EmergeRunFacts.None;
        }
    }

    // ── driven ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The driven arrays, checked against each other and against <paramref name="ports"/>; null with a sentence when
    /// one is missing or they disagree.</summary>
    public static EmergeSweep? ReadDriven(string runDir, int ports, out string? error)
    {
        error = null;
        try
        {
            var (f, fShape) = PlainNpy.ReadReal(Path.Combine(runDir, FrequencyFile));
            var (s, sShape) = PlainNpy.ReadComplex(Path.Combine(runDir, SFile));
            var (z, zShape) = PlainNpy.ReadComplex(Path.Combine(runDir, Z0File));
            var (g, gShape) = PlainNpy.ReadComplex(Path.Combine(runDir, GammaFile));
            int nf = f.Length;
            // brief-emw-2 — Z0 and gamma are [f, ports] (driver version 5); any other shape is refused, never reshaped
            if (fShape.Length != 1 || sShape is not [var a, var b, var c] || a != nf || b != ports || c != ports ||
                zShape is not [var zf, var zp] || zf != nf || zp != ports || gShape is not [var gf, var gp] || gf != nf || gp != ports)
            {
                error = $"EMerge's arrays do not agree: {FrequencyFile} [{string.Join(", ", fShape)}], {SFile} [{string.Join(", ", sShape)}], " +
                        $"{Z0File} [{string.Join(", ", zShape)}] and {GammaFile} [{string.Join(", ", gShape)}], where [{nf}], " +
                        $"[{nf}, {ports}, {ports}], [{nf}, {ports}] and [{nf}, {ports}] were expected ({nf} frequencies, {ports} ports).";
                return null;
            }
            var mats = new Complex[nf][,];
            for (int k = 0; k < nf; k++)
            {
                mats[k] = new Complex[ports, ports];
                for (int i = 0; i < ports; i++)
                    for (int j = 0; j < ports; j++)
                        mats[k][i, j] = s[(k * ports + i) * ports + j];
            }
            if (mats.Any(m => m.Cast<Complex>().Any(v => !double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary))))
            {
                error = $"EMerge's {SFile} holds a value that is not a number.";
                return null;
            }
            Complex[][] Rows(Complex[] v) => [.. Enumerable.Range(0, nf).Select(k => v[(k * ports)..((k + 1) * ports)])];
            return new EmergeSweep(f, mats, Rows(z), Rows(g));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"EMerge's results could not be read: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// The sweep referred to each port's stated Z0. brief-emw-2 R-emw2-4: first each moved reference plane is shifted, in the
    /// mode's own normalisation (<see cref="WavePortReferences.Shift"/>, with the port's γ), then every port whose stated
    /// reference differs from its Z0 is renormalised per frequency (<see cref="WavePortReferences.Renormalise"/>, the whole
    /// matrix, never an element at a time) — the order Palace's run has. EMerge refers a lumped port's S to that port's own
    /// Z0, which the lowering set to the port's, so a lumped-only run is normally the sweep unchanged; a wave port's S is
    /// referred to its mode's Z_PV and is always renormalised.
    /// </summary>
    public static EmergeSweep AtPortReferences(EmergeSweep sweep, IReadOnlyList<Em3dPort> ports, out bool renormalised)
    {
        var target = ports.Select(p => p.Z0).ToArray();
        var s = sweep.S;
        if (ports.Any(p => p.Kind == Em3dPortKind.Wave && p.ReferencePlane.ShiftM != 0))
            s = WavePortReferences.Shift(s, ports, sweep.Gamma);
        renormalised = sweep.Z0.Any(row => row.Where((z, i) => (z - target[i]).Magnitude > 1e-9 * Math.Max(1, target[i].Magnitude)).Any());
        if (renormalised) s = WavePortReferences.Renormalise(s, ports, sweep.Z0);
        return sweep with { S = s, Z0 = [.. sweep.Z0.Select(_ => (Complex[])target.Clone())] };
    }

    /// <summary>S and Z0 in the house convention, the port map, and what EMerge reported, in the <see cref="Group"/> group.</summary>
    public static DataSet BuildDataSet(EmergeSweep sweep, IReadOnlyList<Em3dPort> ports, EmergeRunFacts facts)
    {
        int nf = sweep.FrequenciesHz.Length, np = ports.Count;
        var mats = new Mat<Complex>[nf];
        for (int k = 0; k < nf; k++)
        {
            var m = new Mat<Complex>(np, np);
            for (int i = 0; i < np; i++)
                for (int j = 0; j < np; j++)
                    m[i, j] = sweep.S[k][i, j];
            mats[k] = m;
        }
        var z0 = ports.Select(p => p.Z0).ToArray();
        var ds = DataSetBuilder.FromSnp(new SNP(sweep.FrequenciesHz, mats, MatrixType.S, MatrixFormat.RI, z0[0]));
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0));
        Em3dPortMap.AddTo(ds, Group, ports);
        AddFacts(ds, facts);
        return ds;
    }

    private static void AddFacts(DataSet ds, EmergeRunFacts facts)
    {
        void Scalar(string name, double? v)
        {
            if (v is { } x) ds.AddToGroup(Group, name, DataCube.Scalar(x));
        }
        Scalar("Tetrahedra", facts.Tetrahedra);
        Scalar("DegreesOfFreedom", facts.Unknowns);
        Scalar("SweepSamples", facts.Samples);
        Scalar("SweepError", facts.Error);
    }

    /// <summary>
    /// The Touchstone, through the exporter and the provenance stamp Palace's run writes with (R-em3d7-5d). Null when written;
    /// otherwise why not. <paramref name="when"/> is the write stamp, the one line two runs of one setup differ by besides the
    /// cost line.
    /// </summary>
    public static string? WriteSnp(DataSet data, string snpBase, EmSetup setup, Em3dProblem problem, EmergeLowering lowering,
                                   EmergeSettings settings, EmergeRunFacts facts, string solverVersion, TimeSpan wall, long? peakBytes,
                                   DateTimeOffset when, IReadOnlyList<string>? dcLines = null)
    {
        string? group = data.Groups.FirstOrDefault(g => data.CubesIn(g).ContainsKey("S"));
        if (group is null) return "the solved DataSet carries no S cube";

        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var portText = new StringBuilder();
        foreach (var p in problem.Ports)
            portText.Append($"{p.Number}:{p.PositiveObject}:{p.NegativeObject}:{R(p.Min.X)},{R(p.Min.Y)},{R(p.Min.Z)}:" +
                            $"{R(p.Max.X)},{R(p.Max.Y)},{R(p.Max.Z)}:{R(p.Z0.Real)},{R(p.Z0.Imaginary)}|");
        string settingsText = string.Join("|", R(settings.MaxElementWavelengths), R(settings.EdgeRefinement), settings.ElementOrder,
            settings.AdaptiveSweep, R(settings.Tolerance), settings.MaxSamples, settings.AdaptiveMesh);

        // Which numbered port is which named port, first and in the spelling other tools read (Port[N] = name).
        var lines = new List<string>(Em3dPortMap.NameLines(problem.Ports));
        foreach (var p in problem.Ports.OrderBy(p => p.Number))
            lines.Add($"{EmProvenanceStamp.PortPrefixNumbered}{p.Number}: '{Ascii(p.Name)}' from '{Ascii(p.NegativeObject)}' " +
                      $"to '{Ascii(p.PositiveObject)}', lumped, {R(p.Z0.Real)} Ohm");
        lines.AddRange(Em3dPortMap.Lines(problem.Ports));
        lines.Add($"circuitRF-EM 3D mesh: {N(facts.Tetrahedra)} tetrahedra, element order {settings.ElementOrder}" +
                  (problem.MeshFrequencyHz is { } meshedAt ? $", sized at {(meshedAt / 1e9).ToString("R", System.Globalization.CultureInfo.InvariantCulture)} GHz" : ""));
        lines.Add($"circuitRF-EM 3D operating temperature: {R(problem.OperatingTempC)} C");
        lines.Add(Em3dRunService.RunProvenancePrefix + string.Join("; ",
            $"preset {setup.Emerge?.Quality ?? PalaceQuality.Standard}",
            $"{N(facts.Tetrahedra)} tetrahedra",
            $"{N(facts.Unknowns)} unknowns",
            facts.Adaptive
                ? $"adaptive sweep, {N(facts.Samples)} frequency sample(s), estimated error {(facts.Error is { } e ? e.ToString("G3", CultureInfo.InvariantCulture) : "unreported")}"
                : $"{N(facts.Samples)} frequency sample(s)"));
        lines.Add(Em3dRunService.CostProvenancePrefix + $"wall {wall.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s; EMerge peak memory " +
                  (peakBytes is { } m ? Ascii(MachineMemory.Format(m)) : "unreported"));
        lines.AddRange(dcLines ?? []);                        // brief-emdc-2 R-emdc2-4

        double z0 = problem.Ports.OrderBy(p => p.Number).First().Z0.Real;
        var opts = new TouchstoneExportOptions(
            Z0Ohms:         z0 > 0 ? z0 : 50,
            Digits:         10,
            DigitFormat:    'g',
            MatrixFormat:   MatrixFormat.RI,
            HeaderComments: EmSnpProvenance.BuildHeader3D(
                "EMerge",
                EmSnpProvenance.HashText(lowering.ProblemJson!),
                EmSnpProvenance.HashText(settingsText),
                EmSnpProvenance.HashText(portText.ToString()),
                "EMerge " + solverVersion,
                lines,
                setup.Name is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(snpBase),
                setup.SolveRegion is { } r ? $"{setup.LayoutRef} (solve region {Ascii(r.Describe())})" : setup.LayoutRef,
                when));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snpBase))!);
        var result = TouchstoneExporter.Export(
            data, group, opts,
            pinnedIndexByAxis:    new Dictionary<string, int>(),
            allSweepFiles:        false,
            baseFilePathNoSuffix: snpBase);
        return result.Status == TouchstoneExportStatus.Ok ? null : $"Touchstone export returned {result.Status}.";
    }

    // ── eigenmode ────────────────────────────────────────────────────────────────────────────

    /// <summary>The eigenmode arrays: each solution's complex frequency (Hz) and its Q. Null with a sentence when unreadable.</summary>
    /// <summary>brief-emi-7 — the modes a finished eigenmode run reports, read back from its directory as the run filtered them
    /// (modes.npy at problem.json's search start): the field picker labels its steps with them. Null when either is missing.</summary>
    public static EmergeModes? ReportedModes(string runDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, EmergeScriptWriter.ProblemFile)));
            if (!doc.RootElement.TryGetProperty("eigenmode", out var eig) || !eig.TryGetProperty("targetHz", out var t)) return null;
            return ReadModes(runDir, out _) is { } m ? FilterModes(m.Frequencies, m.Q, t.GetDouble()) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    public static (Complex[] Frequencies, double[] Q)? ReadModes(string runDir, out string? error)
    {
        error = null;
        try
        {
            var (f, fShape) = PlainNpy.ReadComplex(Path.Combine(runDir, ModesFile));
            var (q, qShape) = PlainNpy.ReadReal(Path.Combine(runDir, QFile));
            if (fShape.Length != 1 || qShape.Length != 1 || f.Length != q.Length)
            {
                error = $"EMerge's {ModesFile} [{string.Join(", ", fShape)}] and {QFile} [{string.Join(", ", qShape)}] do not agree.";
                return null;
            }
            return (f, q);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"EMerge's modes could not be read: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// R-emi5-4b — the modes worth reporting: every solution whose frequency (the real part) is at or above the search start
    /// less <see cref="ModeFloorMargin"/>, in ascending frequency, numbered from 1. What is below is dropped and counted.
    /// </summary>
    public static EmergeModes FilterModes(IReadOnlyList<Complex> frequencies, IReadOnlyList<double> q, double searchStartHz)
    {
        double floor = searchStartHz * (1 - ModeFloorMargin);
        var kept = frequencies.Select((f, i) => (F: f.Real, Q: q[i], I: i))
                              .Where(m => double.IsFinite(m.F) && m.F >= floor)
                              .OrderBy(m => m.F).ToList();
        return new EmergeModes([.. kept.Select((m, i) => new Em3dMode(i + 1, m.F, m.Q))], frequencies.Count - kept.Count, floor,
                               [.. kept.Select(m => m.I)]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static string N(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "unreported";
    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "unreported";

    private static string Ascii(string s)
        => new(s.Replace("µm", "um").Replace("–", "-").Replace("×", "x").Select(ch => ch < 128 ? ch : '?').ToArray());
}

/// <summary>
/// A plain NumPy array, as the driver's <c>np.save</c> writes it: version 1 or 2, C order, little-endian <c>&lt;f8</c> or
/// <c>&lt;c16</c>. circuitRF's own <c>.npy</c> reader reads its structured format, not this.
/// </summary>
internal static class PlainNpy
{
    public static (double[] Values, int[] Shape) ReadReal(string path)
    {
        var (descr, shape, data) = Read(path);
        if (descr != "<f8") throw new InvalidDataException($"'{Path.GetFileName(path)}' holds {descr}, not 64-bit reals.");
        var v = new double[data.Length / 8];
        Buffer.BlockCopy(data, 0, v, 0, v.Length * 8);
        return (v, shape);
    }

    public static (Complex[] Values, int[] Shape) ReadComplex(string path)
    {
        var (descr, shape, data) = Read(path);
        if (descr != "<c16") throw new InvalidDataException($"'{Path.GetFileName(path)}' holds {descr}, not 128-bit complex values.");
        var v = new Complex[data.Length / 16];
        for (int k = 0; k < v.Length; k++)
            v[k] = new Complex(BitConverter.ToDouble(data, 16 * k), BitConverter.ToDouble(data, 16 * k + 8));
        return (v, shape);
    }

    private static (string Descr, int[] Shape, byte[] Data) Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 10 || b[0] != 0x93 || Encoding.ASCII.GetString(b, 1, 5) != "NUMPY")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a NumPy array.");
        int headerLen = b[6] == 1 ? BitConverter.ToUInt16(b, 8) : BitConverter.ToInt32(b, 8);
        int start = (b[6] == 1 ? 10 : 12) + headerLen;
        string header = Encoding.ASCII.GetString(b, start - headerLen, headerLen);
        if (!header.Contains("'fortran_order': False", StringComparison.Ordinal))
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is in Fortran order.");
        string descr = Regex.Match(header, @"'descr': '([^']+)'").Groups[1].Value;
        int[] shape = [.. Regex.Match(header, @"'shape': \(([^)]*)\)").Groups[1].Value
                              .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
        long count = shape.Aggregate(1L, (a, n) => a * n);
        int size = descr == "<c16" ? 16 : 8;
        if (b.Length - start != count * size)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' holds {b.Length - start} bytes of data where its shape needs {count * size}.");
        return (descr, shape, b[start..]);
    }
}
