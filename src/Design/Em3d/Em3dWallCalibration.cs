// PWL-3, second half — a wall port's calibration: the line standards, the cross-section's C′ and modes, and the de-embedding.
//
// `docs/briefs/brief-pwl-3-wall-port-model-and-palace.md` R-pwl3-7 … R-pwl3-12, on PWL-2's decisions (src/Design/Em3d/RESOLVED.md):
//
//   • ONE GROUP PER FACE. Every wall port on one face is calibrated together: the face's cross-section is extruded between two
//     PEC walls, and that standard carries all of them. A group of one is the scalar calibration, a group of N the modal one.
//   • THE STANDARD IS BUILT FROM THE SOLVED STRUCTURE, never redrawn: the end run (3 substrate heights from the face, PWL-2 Q4) is
//     sliced at three stations, every object crossing it must have the same section at all three (R-pwl3-4), and those sections
//     are extruded to the standard's length. Lines keep their gap at both ends; everything else runs wall to wall.
//   • C′ COMES FROM KERNEL A on that section (PWL-2 Q3), its mesh sized for the widest grounded metal, a group's [C] symmetrised,
//     a conducting layer as εr − jσ/(ωε₀) at each frequency. Z_c = γ/(jωC′) per mode, with γ the standards' own.
//   • THE ALGEBRA IS SHARED (PWL-1): LineCalibrator for γ and the box, LineDeembed for the block peel and modal → terminal.
//
// The standards are solved by the run (Em3dRunService) with the same solver and settings as the structure; their raw S is handed
// to Capture, and Apply turns the structure's raw S into the calibrated answer before anything is written.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Calibration;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Em3d;

/// <summary>One run's wall-port calibration: its groups, the standards they need, and the de-embedding. See the file header.</summary>
public sealed class Em3dWallCalibration
{
    /// <summary>The end run, in the line's own heights above its return (PWL-2 Q4).</summary>
    public const double EndRunHeights = 3.0;

    /// <summary>The short standard, wall to wall, in the same heights (PWL-2 Q4: 6 h or more).</summary>
    public const double ShortStandardHeights = 6.0;

    /// <summary>D6 — lines on one face nearer than this many of their heights are calibrated as one group: the planar solver's own
    /// driven-neighbour clearance (PCAL1/PCAL2 measured the coupling falling to the A-vs-B floor by 4–5.5 heights).</summary>
    public static readonly double CouplingHeights = new CircuitRF.Engine.Mom.PlanarCalibrationSettings().DrivenNeighbourClearanceHeights;

    /// <summary>How many conductors one group may hold — the planar solver's own cost gate.</summary>
    public static readonly int MaxGroupSize = new CircuitRF.Engine.Mom.PlanarCalibrationSettings().MaxCalibrationGroupSize;

    /// <summary>How far apart two modes must be in electrical length over the separation read — the planar solver's floor.</summary>
    public const double ModeSeparationFloorDegrees = 0.5;

    private const double Eps0 = 8.8541878128e-12, Mu0Eps0 = 1.0 / (LineCalibration.C0 * LineCalibration.C0);

    /// <summary>One standard: its problem and the label its run is known by.</summary>
    public sealed record Standard(string Label, Em3dProblem Problem, double LengthM);

    private sealed class Group
    {
        public required string Face;
        public required int Axis, Inward;
        public required double At, Height, Gap, Shift;
        public required List<Em3dPort> Ports;           // the structure's ports on this face, in lateral order
        public required Section Section;
        public double ShortM;
        public double[] Deltas = [];
        public int[] StandardIndex = [];                 // into the run's standards: [short, long 0, long 1, …]
        public LineCalibrator? Calibrator;
        public readonly List<string> Unusable = [];
        public double EeffLo = double.MaxValue, EeffHi = double.MinValue, ZcLo = double.MaxValue, ZcHi = double.MinValue, Residual;
    }

    private readonly List<Group> _groups = [];
    private readonly List<Standard> _standards = [];
    private readonly Dictionary<int, (double[] F, Complex[][,] S)> _raw = [];

    // R-pwl3-12 — what the last Apply measured, per frequency (in the structure's order) and per port: γ, Z_c and the group's
    // residual at that frequency. NaN where a port is not a wall port.
    private Complex[][] _gamma = [], _zc = [];
    private double[][] _residual = [];

    /// <summary>The standards the run must solve before the structure.</summary>
    public IReadOnlyList<Standard> Standards => _standards;

    private Em3dWallCalibration() { }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The plan
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The calibration of <paramref name="problem"/>'s wall ports over <paramref name="fLoHz"/>…<paramref name="fHiHz"/>, or a
    /// refusal naming what cannot be calibrated. Null calibration and null refusal when the problem has no wall port.
    /// </summary>
    public static (Em3dWallCalibration? Calibration, string? Refusal) Plan(Em3dProblem problem, double fLoHz, double fHiHz, List<string> notes)
    {
        var walls = problem.Ports.Where(p => p.Wall is not null).ToList();
        if (walls.Count == 0) return (null, null);
        var cal = new Em3dWallCalibration();
        // D6 — a face's lines group by distance: within CouplingHeights of their own heights, they are calibrated together.
        var groups = new List<List<Em3dPort>>();
        foreach (var byFace in walls.GroupBy(p => p.Wall!.Face))
        {
            var onFace = byFace.OrderBy(p => p.Wall!.LineLoM).ToList();
            var current = new List<Em3dPort> { onFace[0] };
            for (int i = 1; i < onFace.Count; i++)
            {
                var prev = current[^1].Wall!; var next = onFace[i].Wall!;
                double clearance = CouplingHeights * Math.Max(prev.HeightM, next.HeightM);
                if (next.LineLoM - current.Max(q => q.Wall!.LineHiM) < clearance) current.Add(onFace[i]);
                else { groups.Add(current); current = [onFace[i]]; }
            }
            groups.Add(current);
        }
        foreach (var ports in groups)
        {
            var w0 = ports[0].Wall!;
            var own = ports.Select(p => p.Wall!.Conductor).ToHashSet(StringComparer.Ordinal);
            var others = walls.Where(p => p.Wall!.Face == w0.Face && !ports.Contains(p)).Select(p => p.Wall!.Conductor)
                              .Where(c => !own.Contains(c)).ToHashSet(StringComparer.Ordinal);
            if (ports.Count > MaxGroupSize)
                return (null, $"Wall ports {string.Join(", ", ports.Select(p => p.Number))} on the {w0.Face} face are within " +
                              $"{CouplingHeights:0.#} line heights of one another, so they are calibrated as one group, and a group holds at most " +
                              $"{MaxGroupSize} lines in this version. Space them further apart, feed some another way, or split them across faces.");
            if (ports.Select(p => p.ReferencePlane.ShiftM).Distinct().Count() > 1)
                return (null, $"Wall ports {string.Join(", ", ports.Select(p => p.Number))} on the {w0.Face} face move their reference planes " +
                              "by different distances; lines this close are calibrated together, so they share one reference plane. Give them the same DeembedUm.");
            double h = ports.Max(p => p.Wall!.HeightM), gap = ports.Max(p => p.Wall!.GapM);
            double endRun = EndRunHeights * h;
            var section = Section.Take(problem, w0.Face, w0.Axis, w0.FaceAtM, w0.Inward, gap, endRun, ports, others)!;
            section.Label = groups.Count(x => x[0].Wall!.Face == w0.Face) > 1
                ? $"{w0.Face} ({string.Join(", ", ports.Select(p => p.Number))})" : w0.Face;
            if (section.Refusal is { } why) return (null, why);
            var g = new Group
            {
                Face = w0.Face, Axis = w0.Axis, Inward = w0.Inward, At = w0.FaceAtM, Height = h, Gap = gap,
                Shift = ports[0].ReferencePlane.ShiftM, Ports = ports, Section = section,
            };
            if (Plan(cal, problem, g, fLoHz, fHiHz, notes) is { } refusal) return (null, refusal);
            cal._groups.Add(g);
        }
        return (cal, null);
    }

    private static string? Plan(Em3dWallCalibration cal, Em3dProblem problem, Group g, double fLoHz, double fHiHz, List<string> notes)
    {
        // The medium at the band's centre, for the plan: ε_eff (or β, where a layer conducts) sizes the separations.
        double fMid = Math.Sqrt(Math.Max(fLoHz, 1) * Math.Max(fHiHz, 1));
        if (g.Section.Medium(fMid) is not { } mid) return g.Section.MediumRefusal;
        double eeff = mid.Lambda.Average() / Mu0Eps0;
        bool conducts = g.Section.Conducts;
        double BetaAt(double f) => g.Section.Medium(f)!.QuasiStaticBetaMean(f);
        g.ShortM = Math.Max(ShortStandardHeights * g.Height, 8 * g.Gap);
        g.Deltas = LineCalibration.SuggestDeltas(fLoHz, fHiHz, f => conducts
            ? LineCalibration.SeparationAt(f, BetaAt, 60.0)
            : LineCalibration.SeparationAt(f, eeff, 60.0));

        // R-pwl3-3 — modes of (nearly) one speed cannot be told apart by line standards: asked here, from the electrostatics.
        if (g.Ports.Count > 1)
            foreach (double f in new[] { fLoHz, fMid, fHiHz })
            {
                var m = g.Section.Medium(f)!;
                double w = 2 * Math.PI * f, dl = g.Deltas[LineCalibration.SelectSeparation(g.Deltas, m.QuasiStaticBetaMean(f))];
                double worst = double.PositiveInfinity;
                for (int i = 0; i < m.Lambda.Length; i++)
                    for (int j = i + 1; j < m.Lambda.Length; j++)
                        worst = Math.Min(worst, Math.Abs(w * Math.Sqrt(m.Lambda[i]) - w * Math.Sqrt(m.Lambda[j])) * dl * 180 / Math.PI);
                if (worst < ModeSeparationFloorDegrees)
                    return $"The {g.Ports.Count} lines on the {g.Face} face carry modes that travel at nearly one speed ({worst:0.###}° apart over " +
                           $"the separation read at {Ghz(f)} GHz, against a floor of {ModeSeparationFloorDegrees}°), so line standards cannot " +
                           "tell them apart (a stripline pair, or lines in a homogeneous fill). Feed them with a wave port with terminals on openEMS.";
            }

        g.StandardIndex = new int[g.Deltas.Length + 1];
        double[] lengths = [g.ShortM, .. g.Deltas.Select(d => g.ShortM + d)];
        // R-pwl3-8 — two faces with one cross-section (the two ends of a uniform line) share their standards: a standard is the
        // section extruded, and where it stands in the structure changes nothing in it.
        if (cal._groups.FirstOrDefault(o => o.Section.Signature == g.Section.Signature && o.ShortM == g.ShortM
                                            && o.Deltas.SequenceEqual(g.Deltas)) is { } same)
            g.StandardIndex = same.StandardIndex;
        else
            for (int k = 0; k < lengths.Length; k++)
            {
                g.StandardIndex[k] = cal._standards.Count;
                cal._standards.Add(new Standard($"wall {g.Section.Label} {Mm(lengths[k])} mm", g.Section.Build(problem, lengths[k]), lengths[k]));
            }
        g.Calibrator = new LineCalibrator(g.ShortM, g.Deltas,
            conducts ? BetaAt : f => LineCalibration.EstimateBeta(eeff, f), predictFromHistory: !conducts);
        var sharedWith = cal._groups.FirstOrDefault(o => o.StandardIndex == g.StandardIndex);
        notes.Add($"Wall ports on the {g.Face} face ({string.Join(", ", g.Ports.Select(p => p.Number))}): calibrated with " +
                  $"{lengths.Length} line standard(s) of {string.Join(", ", lengths.Select(Mm))} mm, wall to wall, " +
                  (sharedWith is not null
                      ? $"the {sharedWith.Section.Label} face's own, since the two have one cross-section; "
                      : $"built from the face's cross-section over its first {Mm(EndRunHeights * g.Height)} mm; ") +
                  "C′ from the cross-section's electrostatics.");
        return null;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The standards' answers, and the calibrated structure
    // ══════════════════════════════════════════════════════════════════════════════════════════

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pwl3-9 — the cache: a standard's raw S, keyed by everything that decides its solve
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The folder under a run's results root that holds solved standards.</summary>
    public const string CacheFolder = "wall-standards";

    /// <summary>
    /// The key of one standard's solve: its problem (every solid's and sheet's geometry and material, the ports, the box, the
    /// materials' values, the frequencies, the temperature) with the setup's own settings (<paramref name="settings"/>, the
    /// setup serialized) and the solver. A different answer needs a different key; the same key always the same answer.
    /// </summary>
    public static string CacheKey(Em3dProblem standard, string settings, string solver)
    {
        var t = new System.Text.StringBuilder();
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        string P(Point3 q) => $"{R(q.X)},{R(q.Y)},{R(q.Z)}";
        t.Append(solver).Append('\n').Append(settings).Append('\n');
        t.Append($"type {standard.Type} temp {R(standard.OperatingTempC)} thin {standard.ThinMetalCorrection}\n");
        var fq = standard.Frequency;
        t.Append($"f {R(fq.StartHz)} {R(fq.StopHz)} {fq.Points} {fq.Kind} {string.Join(",", (fq.ListHz ?? []).Select(R))}\n");
        foreach (var m in standard.Materials)
            t.Append($"m {m.Name} {R(m.Epsr)} {R(m.TanD)} {R(m.Mur)} {R(m.SigmaSm)} {string.Join(",", (m.EpsrTensor ?? []).Select(R))}\n");
        foreach (var x in standard.Solids)
        {
            var (a0, b0, c0, a1, b1, c1) = Em3dProblem.Bounds(x.Primitive);
            t.Append($"s {x.Name} {x.Material} {x.Role} {x.Order} {x.Primitive.GetType().Name} {R(a0)} {R(b0)} {R(c0)} {R(a1)} {R(b1)} {R(c1)}\n");
        }
        foreach (var x in standard.Sheets)
            t.Append($"h {x.Name} {x.Material} {R(x.Z)} {R(x.ThicknessM)} {x.Order} {string.Join(";", x.Outline.Select(q => $"{R(q.X)},{R(q.Y)}"))}\n");
        foreach (var p in standard.Ports)
            t.Append($"p {p.Number} {p.PositiveObject} {p.NegativeObject} {P(p.Min)} {P(p.Max)} {P(p.Direction)} {R(p.Z0.Real)},{R(p.Z0.Imaginary)}\n");
        var b = standard.Boundary;
        t.Append($"b {P(b.Min)} {P(b.Max)} {b.Faces} {b.Material}\n");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t.ToString()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>Where standard <paramref name="std"/>'s answer is kept for <paramref name="setup"/> on <paramref name="solver"/>: the
    /// one spelling the run and <c>explain</c> both use, so what explain calls cached is what the run reuses.</summary>
    public static string CachePath(string resultsRoot, Standard std, Layout.Em.EmSetup setup, Layout.Em.Em3dSolverId solver)
        => Path.Combine(resultsRoot, CacheFolder, CacheKey(std.Problem, Layout.Em.EmSetupPersistence.Serialize(setup), solver.ToString()) + ".json");

    /// <summary>A cached standard's raw S, or false when there is none (or it cannot be read).</summary>
    public static bool TryLoad(string path, out double[] frequenciesHz, out Complex[][,] s)
    {
        frequenciesHz = []; s = [];
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            frequenciesHz = [.. root.GetProperty("f").EnumerateArray().Select(e => e.GetDouble())];
            int n = root.GetProperty("n").GetInt32();
            var re = root.GetProperty("re").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var im = root.GetProperty("im").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            if (re.Length != frequenciesHz.Length * n * n || im.Length != re.Length) return false;
            s = new Complex[frequenciesHz.Length][,];
            for (int k = 0; k < frequenciesHz.Length; k++)
            {
                s[k] = new Complex[n, n];
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { int x = (k * n + i) * n + j; s[k][i, j] = new Complex(re[x], im[x]); }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                     or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Standard <paramref name="index"/>'s captured raw S written to <paramref name="path"/>. A write that fails costs only the
    /// next run its reuse, so it is not an error.</summary>
    public void Save(int index, string path)
    {
        if (!_raw.TryGetValue(index, out var r)) return;
        try
        {
            int n = r.S[0].GetLength(0);
            var re = new List<double>(); var im = new List<double>();
            foreach (var m in r.S) for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { re.Add(m[i, j].Real); im.Add(m[i, j].Imaginary); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(new { f = r.F, n, re, im }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The diagnostics cube holding the structure's raw S at its wall ports' gaps, before calibration.</summary>
    public const string RawCube = "__WallPortRawS";

    /// <summary>The raw S as a <c>[f, i, j]</c> cube, ports by number.</summary>
    public static RfCore.Data.DataCube RawCubeOf(double[] frequenciesHz, Complex[][,] s, IReadOnlyList<Em3dPort> ports)
    {
        int nf = frequenciesHz.Length, np = ports.Count;
        var v = new Complex[nf * np * np];
        for (int k = 0; k < nf; k++) for (int i = 0; i < np; i++) for (int j = 0; j < np; j++) v[(k * np + i) * np + j] = s[k][i, j];
        double[] numbers = [.. ports.Select(p => (double)p.Number)];
        return new RfCore.Data.DataCube([new RfCore.Data.Axis("f", frequenciesHz, "Hz"), new RfCore.Data.Axis("i", numbers, ""),
                                         new RfCore.Data.Axis("j", numbers, "")], v);
    }

    /// <summary>Standard <paramref name="index"/>'s raw S, as its solver wrote it.</summary>
    public void Capture(int index, double[] frequenciesHz, Complex[][,] s) => _raw[index] = (frequenciesHz, s);

    /// <summary>
    /// The structure's raw S (<paramref name="s"/>, one matrix per frequency, rows and columns in <paramref name="ports"/>'s order)
    /// calibrated at its wall ports and referred to each port's Z0; every other port passes through. Notes go to <paramref name="notes"/>.
    /// </summary>
    public Complex[][,] Apply(double[] frequenciesHz, Complex[][,] s, IReadOnlyList<Em3dPort> ports, List<string> notes)
    {
        int np = ports.Count;
        var order = Enumerable.Range(0, frequenciesHz.Length).OrderBy(k => frequenciesHz[k]).ToArray();
        var output = new Complex[frequenciesHz.Length][,];
        foreach (var g in _groups) g.Calibrator!.Restart();
        _gamma = new Complex[frequenciesHz.Length][]; _zc = new Complex[frequenciesHz.Length][]; _residual = new double[frequenciesHz.Length][];
        foreach (int k in order)
        {
            double f = frequenciesHz[k], w = 2 * Math.PI * f;
            var blocks = new List<LineBlockBox>();
            var transform = new Mat<Complex>(np, np);
            var zModal = new Complex[np];
            var gamma = new Complex[np];
            var shift = new double[np];
            var covered = new bool[np];
            var resid = new double[np];
            Array.Fill(resid, double.NaN);
            foreach (var g in _groups)
            {
                var idx = g.Ports.Select(p => IndexOf(ports, p.Number)).ToArray();
                int pick = g.Calibrator!.SelectedIndexAt(f);
                var sShort = Raw(g.StandardIndex[0], f);
                var sLong = Raw(g.StandardIndex[pick + 1], f);
                var m = g.Section.Medium(f)!;
                int n = idx.Length;
                Mat<Complex> a11, a12, a21, a22;
                Complex[] gm, zc;
                double residual;
                if (n == 1)
                {
                    var p = g.Calibrator.At(f, sShort, sLong, pick, gg => gg / (Complex.ImaginaryOne * w * m.CMode[0]));
                    a11 = One(p.Box.A11); a12 = One(p.Box.A21); a21 = One(p.Box.A21); a22 = One(p.Box.A22);
                    gm = [p.Gamma.Gamma]; zc = [p.Zc];
                    residual = p.Box.ConsistencyResidual;
                    if (!p.Gamma.Usable) g.Unusable.Add(Ghz(f));
                }
                else
                {
                    var box = g.Calibrator.ModalAt(f, sShort, sLong, pick, md => w * Math.Sqrt(m.Lambda[md]), m.Tv);
                    a11 = box.A11; a12 = box.A12; a21 = box.A21; a22 = box.A22;
                    gm = [.. box.Gamma];
                    zc = [.. Enumerable.Range(0, n).Select(md => box.Gamma[md] / (Complex.ImaginaryOne * w * m.CMode[md]))];
                    residual = box.CascadeResidual;
                    if (!LineCalibrator.Usable(box) || !LineCalibrator.Separable(box, ModeSeparationFloorDegrees)) g.Unusable.Add(Ghz(f));
                }
                blocks.Add(new LineBlockBox(idx, a11, a12, a21, a22));
                g.Residual = Math.Max(g.Residual, residual);
                for (int a = 0; a < n; a++)
                {
                    resid[idx[a]] = residual;
                    covered[idx[a]] = true;
                    zModal[idx[a]] = zc[a];
                    gamma[idx[a]] = gm[a];
                    shift[idx[a]] = g.Shift;
                    for (int b = 0; b < n; b++) transform[idx[a], idx[b]] = m.Tv[a, b];
                    double ee = Math.Pow(gm[a].Imaginary / w * LineCalibration.C0, 2);
                    g.EeffLo = Math.Min(g.EeffLo, ee); g.EeffHi = Math.Max(g.EeffHi, ee);
                    g.ZcLo = Math.Min(g.ZcLo, zc[a].Real); g.ZcHi = Math.Max(g.ZcHi, zc[a].Real);
                }
            }
            for (int i = 0; i < np; i++)
            {
                if (covered[i]) continue;
                blocks.Add(new LineBlockBox([i], One(0), One(1), One(1), One(0)));
                transform[i, i] = 1;
                zModal[i] = ports[i].Z0;
            }

            _gamma[k] = [.. Enumerable.Range(0, np).Select(i => covered[i] ? gamma[i] : new Complex(double.NaN, double.NaN))];
            _zc[k] = [.. Enumerable.Range(0, np).Select(i => covered[i] ? zModal[i] : new Complex(double.NaN, double.NaN))];
            _residual[k] = resid;
            var raw = new Mat<Complex>(np, np);
            for (int i = 0; i < np; i++) for (int j = 0; j < np; j++) raw[i, j] = s[k][i, j];
            var atZc = LineDeembed.ApplyBlocks(raw, blocks);
            // A reference plane moved into the structure: the mode's own line, peeled exactly (R-fed-2's arithmetic).
            for (int i = 0; i < np; i++)
                for (int j = 0; j < np; j++)
                    if (shift[i] != 0 || shift[j] != 0) atZc[i, j] *= Complex.Exp(gamma[i] * shift[i] + gamma[j] * shift[j]);
            var terminal = LineDeembed.ModalToTerminal(atZc, transform, zModal, [.. ports.Select(p => p.Z0)]);
            var o = new Complex[np, np];
            for (int i = 0; i < np; i++) for (int j = 0; j < np; j++) o[i, j] = terminal[i, j];
            output[k] = o;
        }

        foreach (var g in _groups)
        {
            notes.Add($"Wall ports on the {g.Section.Label} face: calibrated at the wall{(g.Shift != 0 ? $", the reference plane moved {Mm(g.Shift)} mm in" : "")}; " +
                      $"ε_eff {Fmt(g.EeffLo)}–{Fmt(g.EeffHi)}, Z_c {Fmt(g.ZcLo)}–{Fmt(g.ZcHi)} Ω" +
                      (g.Ports.Count > 1 ? " over its modes" : "") + $"; the standards agreed to {g.Residual:0.#e+0}.");
            if (g.Unusable.Count > 0)
                notes.Add($"Wall ports on the {g.Section.Label} face: at {string.Join(", ", g.Unusable)} GHz the separation read is outside the usable " +
                          "interval (20°–160°) for a mode, so the calibration there is weaker. Narrow the sweep, or add points so the band is " +
                          "covered by the standards' own separations.");
        }
        if (PassivityNote(frequenciesHz, output, ports) is { } passivity) notes.Add(passivity);
        return output;
    }

    /// <summary>R-pwl3-12 — the planar solver's passivity check on the published answer: σ_max(S) ≤ 1 + 1e-3, in a uniform real
    /// reference (the ports' own when they share one, 50 Ω otherwise). A note naming the frequencies over it, or null.</summary>
    private static string? PassivityNote(double[] frequenciesHz, Complex[][,] s, IReadOnlyList<Em3dPort> ports)
    {
        const double Tolerance = 1e-3;
        var z0 = ports.Select(p => p.Z0).ToArray();
        bool uniform = z0.All(z => z.Imaginary == 0 && z == z0[0]);
        var common = Enumerable.Repeat(new Complex(50, 0), z0.Length).ToArray();
        var over = new List<(double F, double Sigma)>();
        for (int k = 0; k < s.Length; k++)
        {
            int n = z0.Length;
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = s[k][i, j];
            double sigma;
            try { sigma = RFNetwork.Passivity(uniform ? m : RFNetwork.SToS(m, z0, common)); }
            catch (Exception) { continue; }    // a singular renormalisation says nothing about passivity
            if (sigma > 1 + Tolerance) over.Add((frequenciesHz[k], sigma));
        }
        if (over.Count == 0) return null;
        var worst = over.MaxBy(x => x.Sigma);
        return $"The calibrated answer is not passive at {over.Count} of {s.Length} frequencies (largest singular value of S " +
               $"{worst.Sigma:0.####} at {Ghz(worst.F)} GHz, over 1 + 1e-3). The standards or the structure are under-resolved there: " +
               "a finer mesh, or a quality one step up, is the usual remedy.";
    }

    /// <summary>R-pwl3-12 — per wall port and frequency, what the last <see cref="Apply"/> measured, as <c>__</c> cubes for the
    /// diagnostics group: γ (1/m), ε_eff (from β), Z_c (Ω) and the residual of the group's standards (dimensionless).</summary>
    public IReadOnlyList<(string Name, RfCore.Data.DataCube Cube)> DiagnosticCubes(double[] frequenciesHz, IReadOnlyList<Em3dPort> ports)
    {
        var cols = Enumerable.Range(0, ports.Count).Where(i => ports[i].Wall is not null).ToArray();
        if (cols.Length == 0 || _gamma.Length != frequenciesHz.Length) return [];
        int nf = frequenciesHz.Length, nc = cols.Length;
        RfCore.Data.Axis[] axes = [new("f", frequenciesHz, "Hz"), new("Port", [.. cols.Select(i => (double)ports[i].Number)], "")];
        var gamma = new Complex[nf * nc]; var zc = new Complex[nf * nc]; var eeff = new double[nf * nc]; var res = new double[nf * nc];
        for (int k = 0; k < nf; k++)
            for (int c = 0; c < nc; c++)
            {
                int x = k * nc + c, i = cols[c];
                gamma[x] = _gamma[k][i]; zc[x] = _zc[k][i]; res[x] = _residual[k][i];
                eeff[x] = Math.Pow(_gamma[k][i].Imaginary / (2 * Math.PI * frequenciesHz[k]) * LineCalibration.C0, 2);
            }
        return
        [
            ("__WallPortGamma", new RfCore.Data.DataCube(axes, gamma) { Unit = "1/m" }),
            ("__WallPortEeff", new RfCore.Data.DataCube(axes, eeff)),
            ("__WallPortZc", new RfCore.Data.DataCube(axes, zc) { Unit = "Ohm" }),
            ("__WallPortResidual", new RfCore.Data.DataCube(axes, res)),
        ];
    }

    /// <summary>R-pwl3-12 — the Touchstone header's lines for the wall groups: which ports, how many standards of what length,
    /// scalar or modal, and the C′ source. ASCII, as the header is.</summary>
    public IReadOnlyList<string> HeaderLines()
        => [.. _groups.Select(g =>
            $"circuitRF-EM 3D wall group {g.Section.Label}: ports {string.Join(", ", g.Ports.Select(p => p.Number))}, " +
            $"{(g.Ports.Count > 1 ? $"modal ({g.Ports.Count} modes)" : "scalar")} line-standard calibration with " +
            $"{g.StandardIndex.Length} standards of {string.Join(", ", new[] { g.ShortM }.Concat(g.Deltas.Select(d => g.ShortM + d)).Select(Mm))} mm, " +
            $"C' from the cross-section's electrostatics; reference plane at the wall" +
            (g.Shift != 0 ? $" moved {Mm(g.Shift)} mm in" : "") +
            (g.EeffLo <= g.EeffHi ? $"; eps_eff {Fmt(g.EeffLo)}-{Fmt(g.EeffHi)}, Z_c {Fmt(g.ZcLo)}-{Fmt(g.ZcHi)} Ohm, residual {g.Residual:0.#e+0}" : ""))];

    private Mat<Complex> Raw(int index, double f)
    {
        if (!_raw.TryGetValue(index, out var r))
            throw new InvalidOperationException($"Wall standard {index} has no answer; it is solved before the structure.");
        int k = Array.FindIndex(r.F, x => Math.Abs(x - f) <= 1e-9 * Math.Max(1, f));
        if (k < 0) throw new InvalidOperationException($"Wall standard {index} was not solved at {Ghz(f)} GHz.");
        int n = r.S[k].GetLength(0);
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = r.S[k][i, j];
        return m;
    }

    private static int IndexOf(IReadOnlyList<Em3dPort> ports, int number)
    {
        for (int i = 0; i < ports.Count; i++) if (ports[i].Number == number) return i;
        throw new InvalidOperationException($"Wall port {number} is not among the structure's ports.");
    }

    private static Mat<Complex> One(Complex v) { var m = new Mat<Complex>(1, 1); m[0, 0] = v; return m; }

    private static string Mm(double m) => (m * 1e3).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Ghz(double f) => (f / 1e9).ToString("0.###", CultureInfo.InvariantCulture);
    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The face's cross-section
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The cross-section of one face's end run: rectangles across the face (lateral × z), each a dielectric, a grounded
    /// metal or one of the group's lines, and what that makes as a standard and as a 2D electrostatic problem.</summary>
    internal sealed class Section
    {
        internal sealed record Piece(string Name, string Material, Em3dRole Role, int Order, double Lo, double Hi, double Z0, double Z1,
                                     bool IsSheet, double SheetZ, double SheetThickness, int Line);

        public string? Refusal { get; private init; }
        public string? MediumRefusal { get; private set; }
        public required string Face;
        public required int Axis, Inward;
        public required double At, Gap;
        public required List<Piece> Pieces;
        public required IReadOnlyList<Em3dMaterial> Materials;
        public required Em3dAirBox Box;
        public required IReadOnlyList<Em3dPort> Ports;
        public bool Conducts => Pieces.Any(p => p.Role == Em3dRole.Dielectric && Mat(p.Material)?.SigmaSm > 0);

        /// <summary>What makes two faces' standards the same: every piece across the face, the gap and the ports' count — where the
        /// face stands along its axis is not in it. Rounded to 0.1 nm, below any DBU.</summary>
        public string Signature => string.Join(";", Pieces.Select(p =>
            $"{p.Name}|{p.Material}|{p.Role}|{R(p.Lo)}|{R(p.Hi)}|{R(p.Z0)}|{R(p.Z1)}|{p.IsSheet}|{R(p.SheetThickness)}|{p.Line}"))
            + $"|axis{Axis}|gap{R(Gap)}|n{Ports.Count}|" + string.Join(",", Ports.Select(p => p.Z0.ToString()));

        private static string R(double m) => Math.Round(m * 1e10).ToString(CultureInfo.InvariantCulture);

        private Em3dMaterial? Mat(string name) => Materials.FirstOrDefault(m => m.Name == name);

        /// <summary>How the standards and notes name this group: its face, and its ports when the face has several groups.</summary>
        public string Label { get; set; } = "";

        /// <param name="otherLines">Other groups' lines on this face: left out of this group's section (and so its standards).</param>
        public static Section? Take(Em3dProblem problem, string face, int axis, double at, int inward, double gap, double endRun,
                                    IReadOnlyList<Em3dPort> ports, IReadOnlySet<string>? otherLines = null)
        {
            Section No(string why) => new()
            {
                Refusal = why, Face = face, Axis = axis, Inward = inward, At = at, Gap = gap, Pieces = [], Materials = [],
                Box = problem.Boundary, Ports = ports,
            };
            double tol = 1e-9;
            double[] stations = [at + inward * Math.Min(2 * gap + 1e-7, endRun), at + inward * endRun / 2, at + inward * endRun];
            double slabLo = Math.Min(at, at + inward * endRun), slabHi = Math.Max(at, at + inward * endRun);
            int lat = 1 - axis;
            var pieces = new List<Piece>();
            var lineOf = ports.Select((p, i) => (p.Wall!.Conductor, i)).ToDictionary(x => x.Conductor, x => x.i);

            List<(double Lo, double Hi)>? Uniform(string name, Func<double, List<(double, double)>> slice, out string? why)
            {
                why = null;
                var first = slice(stations[0]);
                foreach (double st in stations.Skip(1))
                {
                    var other = slice(st);
                    if (other.Count != first.Count || other.Zip(first).Any(t => Math.Abs(t.First.Item1 - t.Second.Item1) > tol ||
                                                                                 Math.Abs(t.First.Item2 - t.Second.Item2) > tol))
                    {
                        why = $"'{name}' changes across the {face} face's end run (its first {Mm(endRun)} mm), which a wall port's line " +
                              "standards reproduce as one unchanging cross-section. Extend the line straight from the face that far, " +
                              "with everything beside and under it unchanged.";
                        return null;
                    }
                }
                return first;
            }

            foreach (var s in problem.Solids)
            {
                if (otherLines?.Contains(s.Name) == true) continue;
                var (bx0, by0, bz0, bx1, by1, bz1) = Em3dProblem.Bounds(s.Primitive);
                double a0 = axis == 0 ? bx0 : by0, a1 = axis == 0 ? bx1 : by1;
                if (a1 <= slabLo + tol || a0 >= slabHi - tol) continue;          // not in the end run
                if (s.Role == Em3dRole.Air) continue;
                switch (s.Primitive)
                {
                    case Em3dBox b:
                        if (a0 > slabLo + tol || a1 < slabHi - tol)
                            return No($"'{s.Name}' ends inside the {face} face's end run (its first {Mm(endRun)} mm); the line standards " +
                                      "reproduce an unchanging cross-section there. Run it to the face, or move it back.");
                        Add(s, lat == 0 ? b.Min.X : b.Min.Y, lat == 0 ? b.Max.X : b.Max.Y, b.Min.Z, b.Max.Z);
                        break;
                    case Em3dExtrudedPolygon pr:
                        if (Uniform(s.Name, st => C3dWallPortsSlice(pr.Outline, pr.Holes, axis, st), out var why) is not { } iv) return No(why!);
                        foreach (var (lo, hi) in iv) Add(s, lo, hi, pr.ZBottom, pr.ZTop);
                        break;
                    default:
                        return No($"'{s.Name}' crosses the {face} face's end run and is not a box or an extruded outline, so its cross-section " +
                                  "cannot be reproduced in a line standard. Keep it clear of the wall ports' first " +
                                  $"{Mm(endRun)} mm.");
                }
            }
            foreach (var sh in problem.Sheets)
            {
                if (otherLines?.Contains(sh.Name) == true) continue;
                var (bx0, by0, _, bx1, by1, _) = sh.WorldBounds();
                double a0 = axis == 0 ? bx0 : by0, a1 = axis == 0 ? bx1 : by1;
                if (a1 <= slabLo + tol || a0 >= slabHi - tol) continue;
                if (sh.Frame is not null)
                    return No($"The sheet '{sh.Name}' stands upright in the {face} face's end run; the line standards reproduce horizontal metal only.");
                if (Uniform(sh.Name, st => C3dWallPortsSlice(sh.Outline, sh.Holes, axis, st), out var why) is not { } iv) return No(why!);
                foreach (var (lo, hi) in iv)
                    pieces.Add(new Piece(sh.Name, sh.Material, Em3dRole.Conductor, sh.Order, lo, hi, sh.Z, sh.Z + Math.Max(sh.ThicknessM, 1e-6),
                                         true, sh.Z, sh.ThicknessM, lineOf.GetValueOrDefault(sh.Name, -1)));
            }
            if (problem.CylinderSheets.Count > 0 || problem.LumpedElements.Count > 0 && problem.LumpedElements.Any(l => Near(l.Min, l.Max)))
                return No($"A lumped element or a cylinder sheet lies in the {face} face's end run; keep the wall ports' first {Mm(endRun)} mm clear.");
            foreach (var p in ports)
                if (!pieces.Any(q => q.Line == lineOf[p.Wall!.Conductor]))
                    return No($"Port {p.Number}'s line '{p.Wall!.Conductor}' does not run straight through the {face} face's end run.");
            return new Section
            {
                Face = face, Axis = axis, Inward = inward, At = at, Gap = gap, Pieces = pieces, Materials = problem.Materials,
                Box = problem.Boundary, Ports = ports,
            };

            void Add(Em3dSolid s, double lo, double hi, double z0, double z1)
                => pieces.Add(new Piece(s.Name, s.Material, s.Role, s.Order, lo, hi, z0, z1, false, 0, 0, lineOf.GetValueOrDefault(s.Name, -1)));
            bool Near(Point3 mn, Point3 mx) { double a0 = axis == 0 ? mn.X : mn.Y, a1 = axis == 0 ? mx.X : mx.Y; return a1 > slabLo && a0 < slabHi; }
        }

        private static List<(double, double)> C3dWallPortsSlice(IReadOnlyList<Point2> o, IReadOnlyList<IReadOnlyList<Point2>> h, int axis, double at)
            => CircuitRF.Design.ThreeD.C3dWallPorts.SliceRings(o, h, axis, at);

        /// <summary>This section as a standard of <paramref name="lengthM"/>, wall to wall: the group's ports at both ends (near
        /// 1…N, far N+1…2N in the same lateral order), both walls PEC, every other face as the structure's.</summary>
        public Em3dProblem Build(Em3dProblem structure, double lengthM)
        {
            double a0 = Math.Min(At, At + Inward * lengthM), a1 = Math.Max(At, At + Inward * lengthM);
            int lat = 1 - Axis;
            Point3 P(double along, double across, double z) => Axis == 0 ? new Point3(along, across, z) : new Point3(across, along, z);
            var solids = new List<Em3dSolid>();
            var sheets = new List<Em3dSheet>();
            int k = 0;
            foreach (var p in Pieces)
            {
                string name = Pieces.Count(q => q.Name == p.Name) > 1 ? $"{p.Name}#{k++}" : p.Name;
                double s0 = a0, s1 = a1;
                if (p.Line >= 0) { s0 = a0 + Gap; s1 = a1 - Gap; }
                if (p.IsSheet)
                {
                    var c0 = P(s0, p.Lo, 0); var c1 = P(s1, p.Hi, 0);
                    sheets.Add(new Em3dSheet(name, p.Material,
                        [new Point2(c0.X, c0.Y), new Point2(c1.X, c0.Y), new Point2(c1.X, c1.Y), new Point2(c0.X, c1.Y)], [],
                        p.SheetZ, p.SheetThickness, p.Order));
                }
                else
                {
                    var mn = P(s0, p.Lo, p.Z0); var mx = P(s1, p.Hi, p.Z1);
                    solids.Add(new Em3dSolid(name, p.Material, p.Role,
                        new Em3dBox(new Point3(Math.Min(mn.X, mx.X), Math.Min(mn.Y, mx.Y), mn.Z), new Point3(Math.Max(mn.X, mx.X), Math.Max(mn.Y, mx.Y), mx.Z)),
                        p.Order));
                }
            }
            // The ports: the structure's own gap at the near wall, mirrored at the far one.
            string far = Face[..1] + (Face.EndsWith("min", StringComparison.Ordinal) ? "max" : "min");
            var ports = new List<Em3dPort>();
            int n = Ports.Count;
            for (int i = 0; i < n; i++)
            {
                var src = Ports[i];
                ports.Add(src with { Number = i + 1, Name = $"port/{i + 1}", SourceNumber = null, SourceLabel = null });
            }
            for (int i = 0; i < n; i++)
            {
                var src = Ports[i];
                double farAt = At + Inward * lengthM;
                double m0 = Get(src.Min, Axis), m1 = Get(src.Max, Axis);
                // reflect the gap's along-axis extent about the standard's centre
                double centre = 0.5 * (At + farAt);
                double r0 = 2 * centre - m1, r1 = 2 * centre - m0;
                Point3 Along(Point3 q, double v) => Axis == 0 ? q with { X = v } : q with { Y = v };
                var dir = Axis == 0 ? new Point3(-Inward, 0, 0) : new Point3(0, -Inward, 0);
                ports.Add(src with
                {
                    Number = n + i + 1, Name = $"port/{n + i + 1}", NegativeObject = Em3dAirBox.FaceName(far), SourceNumber = null, SourceLabel = null,
                    Min = Along(src.Min, r0), Max = Along(src.Max, r1), Direction = dir,
                    ReferencePlane = new Em3dReferencePlane(Along(src.ReferencePlane.Origin, farAt), dir, 0),
                    Wall = src.Wall! with { Face = far, FaceAtM = farAt, Inward = -Inward },
                });
            }
            var b = Box;
            var min = Axis == 0 ? b.Min with { X = a0 } : b.Min with { Y = a0 };
            var max = Axis == 0 ? b.Max with { X = a1 } : b.Max with { Y = a1 };
            var faces = Axis == 0 ? b.Faces with { XMin = Em3dBoundaryKind.Pec, XMax = Em3dBoundaryKind.Pec }
                                  : b.Faces with { YMin = Em3dBoundaryKind.Pec, YMax = Em3dBoundaryKind.Pec };
            return structure with
            {
                Solids = solids, Sheets = sheets, Ports = ports,
                Boundary = new Em3dAirBox(min, max, faces) { Material = b.Material, Scan = b.Scan },
                CylinderSheets = [], LumpedElements = [], MeshRegions = [], FaceBoundaries = [], Terminals = [],
                GroundObjects = [],
            };
        }

        private static double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;

        // ── the 2D electrostatic medium ─────────────────────────────────────────────────────────

        internal sealed record ModalMedium(double[] Lambda, Mat<double> Tv, Complex[] CMode)
        {
            public double QuasiStaticBetaMean(double f)
            {
                double w = 2 * Math.PI * f, sum = 0;
                foreach (double l in Lambda) sum += w * Math.Sqrt(l);
                return sum / Lambda.Length;
            }
        }

        private readonly Dictionary<double, ModalMedium?> _media = [];
        private ModalMedium? _lossless;

        /// <summary>The group's modes, their quasi-static λ = 1/v² and each mode's C′ at <paramref name="fHz"/> (per frequency only
        /// where a layer conducts). Null with <see cref="MediumRefusal"/> set when the section is not one kernel A can solve.</summary>
        public ModalMedium? Medium(double fHz)
        {
            if (!Conducts) return _lossless ??= Solve(fHz);
            if (_media.TryGetValue(fHz, out var m)) return m;
            return _media[fHz] = Solve(fHz);
        }

        private ModalMedium? Solve(double fHz)
        {
            int lat = 1 - Axis;
            double w = 2 * Math.PI * Math.Max(fHz, 1);
            double latLo = lat == 0 ? Box.Min.X : Box.Min.Y, latHi = lat == 0 ? Box.Max.X : Box.Max.Y, tol = 1e-9;
            // The floor: a PEC floor, or grounded metal across the whole width at the bottom.
            double zGround;
            var metal = Pieces.Where(p => p.Role == Em3dRole.Conductor).ToList();
            if (Box.Faces.ZMin == Em3dBoundaryKind.Pec) zGround = Box.Min.Z;
            else if (metal.Where(p => p.Line < 0 && p.Lo <= latLo + tol && p.Hi >= latHi - tol).OrderBy(p => p.Z0).FirstOrDefault() is { } floor
                     && !metal.Any(q => q.Z0 < floor.Z0 - tol))
            { zGround = floor.Z1; metal.Remove(floor); }
            else { MediumRefusal = $"The {Face} face's cross-section has no ground plane beneath it (a PEC floor, or metal across its whole width); its C′ is taken from a layered cross-section over one."; return null; }

            // Layers: every dielectric spans the whole width, or the section is not layered.
            var layers = new List<EmDielectricRegion>();
            foreach (var d in Pieces.Where(p => p.Role == Em3dRole.Dielectric).OrderBy(p => p.Z0))
            {
                if (d.Lo > latLo + tol || d.Hi < latHi - tol)
                {
                    MediumRefusal = $"The dielectric '{d.Name}' does not span the {Face} face's whole width, so the cross-section is not a stack " +
                                    "of layers; a wall port's C′ is taken from a layered one in this version.";
                    return null;
                }
                var mat = Mat(d.Material)!;
                double tan = mat.TanD + (mat.SigmaSm > 0 ? mat.SigmaSm / (w * Eps0 * mat.Epsr) : 0);
                layers.Add(new EmDielectricRegion(d.Z0 - zGround, d.Z1 - zGround, new EmMaterial(mat.Epsr, tan)));
            }
            double top = layers.Count > 0 ? layers.Max(l => l.YTop) : 0;
            layers.Add(new EmDielectricRegion(top, Math.Max(1.0, 100 * (Box.Max.Z - zGround)), EmMaterial.Air));

            // Conductors: the group's lines first, in port order, then every other metal (grounded by the walls), the lid and the side walls.
            var lines = Enumerable.Range(0, Ports.Count).Select(i => metal.First(p => p.Line == i)).ToList();
            var conductors = new List<EmConductor>();
            EmConductor Rect(string n, double x0, double x1, double y0, double y1) => new(n, [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)], 5.8e7);
            foreach (var l in lines) conductors.Add(Rect(l.Name, l.Lo, l.Hi, l.Z0 - zGround, l.Z1 - zGround));
            foreach (var o in metal.Where(p => p.Line < 0)) conductors.Add(Rect(o.Name, o.Lo, o.Hi, o.Z0 - zGround, o.Z1 - zGround));
            double t = 1e-6, height = Box.Max.Z - zGround;
            if (Box.Faces.ZMax == Em3dBoundaryKind.Pec) conductors.Add(Rect("lid", latLo, latHi, height, height + t));
            var (sideLo, sideHi) = lat == 0 ? (Box.Faces.XMin, Box.Faces.XMax) : (Box.Faces.YMin, Box.Faces.YMax);
            if (sideLo == Em3dBoundaryKind.Pec) conductors.Add(Rect("side-lo", latLo - t, latLo, 0, height));
            if (sideHi == Em3dBoundaryKind.Pec) conductors.Add(Rect("side-hi", latHi, latHi + t, 0, height));

            // R-pwl3 C′ — the mesh sized for the widest grounded metal against the narrowest line (PWL-2 Q3).
            double narrow = lines.Min(l => l.Hi - l.Lo), widest = conductors.Skip(lines.Count).Select(c => c.Outline.Max(q => q.X) - c.Outline.Min(q => q.X)).DefaultIfEmpty(narrow).Max();
            // …and a group of lines needs a fine section on its own account: at kernel A's default six cells across, a symmetric
            // pair of thin strips reads a [C] whose two off-diagonals are a factor of three apart (PWL-2 Q3), which is a wrong
            // modal basis and so a wrong terminal answer.
            int cells = (int)Math.Clamp(Math.Ceiling(6 * widest / narrow), lines.Count > 1 ? 48 : 6, 160);
            var problem = new EmProblem(conductors, layers, new EmGroundPlane(0, 5.8e7), [], 1e-3);
            RlgcModel rlgc;
            try { rlgc = RlgcExtractor.Extract(problem, BoundaryMesher.Mesh(problem, new EmMeshSettings(MinCellsAcrossWidth: cells))); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                MediumRefusal = $"The {Face} face's cross-section could not be solved for its capacitance: {e.Message}";
                return null;
            }

            int n = lines.Count;
            var c = new Mat<Complex>(n, n); var c0 = new Mat<double>(n, n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    c[i, j] = 0.5 * (rlgc.CComplex[i, j] + rlgc.CComplex[j, i]);
                    c0[i, j] = 0.5 * (rlgc.C0[i, j] + rlgc.C0[j, i]);
                }
            var l0 = c0.Inverse();                         // [L] = μ₀ε₀[C₀]⁻¹ over the lines, everything else grounded
            var lMat = new Mat<double>(n, n);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) lMat[i, j] = Mu0Eps0 * l0[i, j];
            Mat<double> tv; double[] lambda;
            if (n == 1) { tv = new Mat<double>(1, 1); tv[0, 0] = 1; lambda = [lMat[0, 0] * c[0, 0].Real]; }
            else
            {
                var cRe = new Mat<double>(n, n);
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) cRe[i, j] = c[i, j].Real;
                tv = MulticonductorLine.VoltageModalMatrix(cRe, lMat.Inverse(), out lambda);
                // Unit columns: the calibration does not care (a column's scale cancels between the box and modal → terminal), but
                // each mode's C′, and so the Z_c the notes report, reads in ohms only in this gauge.
                for (int md = 0; md < n; md++)
                {
                    double norm = 0;
                    for (int i = 0; i < n; i++) norm += tv[i, md] * tv[i, md];
                    norm = Math.Sqrt(norm);
                    for (int i = 0; i < n; i++) tv[i, md] /= norm;
                }
            }
            var cm = new Complex[n];
            for (int md = 0; md < n; md++)
            {
                Complex sum = 0;
                for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) sum += tv[i, md] * c[i, j] * tv[j, md];
                cm[md] = sum;
            }
            return new ModalMedium(lambda, tv, cm);
        }
    }
}
