// The Noise step (brief NZ-4): measured noise parameters in, the core's drain temperature Td (and gate temperature Tg, when
// the data supports both) out, by bias. The model's own network is the de-embedding: every part of it is simulated, at the
// device's temperature, and what is left of the measured noise is what Tg and Td have to explain. The one fit a device
// modeller uses; no other kind of noise extraction.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using NumFlat;
using RfCore;
using RfCore.Data;
using ComposedNoise = CircuitRF.Core.Devices.Composed.ModelNoise;

namespace CircuitRF.Design.Modeling;

/// <summary>One measured noise data file and the bias it was taken at. A table names its biases per row and needs
/// neither.</summary>
public sealed record NoiseSource(string Path, double? Vgs = null, double? Vds = null);

/// <summary>The step's settings: the measured files, and the ambient the device was measured at, °C.</summary>
public sealed record NoiseFitSettings(IReadOnlyList<NoiseSource> Sources, double AmbientC = 25.0)
{
    public static NoiseFitSettings Default { get; } = new([]);

    public JsonObject ToJson()
    {
        var a = new JsonArray();
        foreach (var s in Sources)
        {
            var o = new JsonObject { ["Path"] = s.Path };
            if (s.Vgs is { } g) o["Vgs"] = g;
            if (s.Vds is { } d) o["Vds"] = d;
            a.Add(o);
        }
        return new JsonObject { ["Sources"] = a, ["AmbientC"] = AmbientC };
    }

    public static NoiseFitSettings Parse(JsonNode node, string field)
    {
        var o = CmcExtrinsic.Obj(node, field);
        CmcExtrinsic.Only(o, field, ["Sources", "AmbientC"]);
        var list = new List<NoiseSource>();
        if (o["Sources"] is JsonArray a)
            foreach (var n in a)
            {
                var s = CmcExtrinsic.Obj(n!, $"{field}.Sources");
                CmcExtrinsic.Only(s, $"{field}.Sources", ["Path", "Vgs", "Vds"]);
                string path = s["Path"]?.GetValue<string>() ?? throw new CmcFormatException($"{field}.Sources.Path", "it is missing.");
                list.Add(new NoiseSource(path, s["Vgs"]?.GetValue<double>(), s["Vds"]?.GetValue<double>()));
            }
        return new NoiseFitSettings(list, o["AmbientC"]?.GetValue<double>() ?? 25.0);
    }
}

/// <summary>
/// One bias's fit: the temperatures, kelvin (<see cref="Tg"/> NaN when the data did not support fitting it, and the network's
/// <c>Ri</c> then stays at the device temperature), and the RMS differences between the measured and modelled noise
/// parameters over its frequencies — NFmin in dB, Γopt as a distance on 50 Ω, Rn relative.
/// </summary>
public sealed record NoiseBiasFit(double Vgs, double Vds, double Tg, double Td, int Points,
                                  double NfMinErrorDb, double GammaOptError, double RnError)
{
    /// <summary>The modelled noise parameters at each measured frequency, Γopt on 50 Ω — what the step plots against the
    /// measurement.</summary>
    public IReadOnlyList<(double Hz, TwoPortNoise Measured, TwoPortNoise Modelled)> Curves { get; init; } = [];
}

/// <summary>The step's record: one fit per bias.</summary>
public sealed record NoiseFitRecord(IReadOnlyList<NoiseBiasFit> Biases)
{
    public bool FittedTg => Biases.Count > 0 && Biases.All(b => double.IsFinite(b.Tg));

    public IReadOnlyList<string> Report => [.. Biases.Select(b => string.Create(CultureInfo.InvariantCulture,
        $"noise at Vgs {b.Vgs:G4} V, Vds {b.Vds:G4} V: {(double.IsFinite(b.Tg) ? $"Tg {b.Tg:G5} K, " : "")}Td {b.Td:G5} K; " +
        $"NFmin {b.NfMinErrorDb:G3} dB, Γopt {b.GammaOptError:G3}, Rn {100 * b.RnError:G3}% RMS over {b.Points} point(s)."))];

    public JsonObject ToJson()
    {
        var a = new JsonArray();
        foreach (var b in Biases)
        {
            var o = new JsonObject { ["Vgs"] = b.Vgs, ["Vds"] = b.Vds, ["Td"] = b.Td, ["Points"] = b.Points,
                                     ["NFminError"] = b.NfMinErrorDb, ["GammaOptError"] = b.GammaOptError, ["RnError"] = b.RnError };
            if (double.IsFinite(b.Tg)) o["Tg"] = b.Tg;
            // Each point: Hz, then NFmin dB, Re Γopt, Im Γopt and Rn Ω measured, then the same modelled.
            var curves = new JsonArray();
            foreach (var (hz, m, f) in b.Curves)
                curves.Add(new JsonArray(hz, m.NFminDb, m.GammaOpt.Real, m.GammaOpt.Imaginary, m.Rn,
                                         f.NFminDb, f.GammaOpt.Real, f.GammaOpt.Imaginary, f.Rn));
            if (curves.Count > 0) o["Curves"] = curves;
            a.Add(o);
        }
        return new JsonObject { ["Biases"] = a };
    }

    public static NoiseFitRecord Parse(JsonNode node, string field)
    {
        var o = CmcExtrinsic.Obj(node, field);
        CmcExtrinsic.Only(o, field, ["Biases"]);
        var list = new List<NoiseBiasFit>();
        if (o["Biases"] is JsonArray a)
            foreach (var n in a)
            {
                var b = CmcExtrinsic.Obj(n!, $"{field}.Biases");
                double D(string k) => b[k]?.GetValue<double>() ?? throw new CmcFormatException($"{field}.Biases.{k}", "it is missing.");
                var curves = new List<(double, TwoPortNoise, TwoPortNoise)>();
                if (b["Curves"] is JsonArray ca)
                    foreach (var pt in ca)
                    {
                        double[] v = [.. ((JsonArray)pt!).Select(x => x!.GetValue<double>())];
                        if (v.Length != 9) throw new CmcFormatException($"{field}.Biases.Curves", "a point is nine numbers.");
                        TwoPortNoise P(int i) => new(Math.Pow(10, v[i] / 10), new Complex(v[i + 1], v[i + 2]), v[i + 3], NoiseFit.ReferenceOhm);
                        curves.Add((v[0], P(1), P(5)));
                    }
                list.Add(new NoiseBiasFit(D("Vgs"), D("Vds"), b["Tg"]?.GetValue<double>() ?? double.NaN, D("Td"),
                                          (int)D("Points"), D("NFminError"), D("GammaOptError"), D("RnError")) { Curves = curves });
            }
        return new NoiseFitRecord(list);
    }
}

public static class NoiseFit
{
    /// <summary>The reference the step states Γopt on, Ω.</summary>
    public const double ReferenceOhm = 50.0;

    public const string StepName = "Noise";


    // ── The document ──────────────────────────────────────────────────────────

    public static NoiseFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? NoiseFitSettings.Parse(s, $"Steps.{StepName}.Settings") : NoiseFitSettings.Default;

    public static NoiseFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? NoiseFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Settings", "Result"]);
        Settings(doc);
        Record(doc);
    }

    public static CmcDocument WithSettings(CmcDocument doc, NoiseFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    public static CmcDocument Apply(CmcDocument doc, NoiseFitSettings settings, NoiseFitRecord record)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject { ["Settings"] = settings.ToJson(), ["Result"] = record.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>
    /// What the export writes as <c>Core.Noise</c>: null with no record. <c>Td</c> is a constant from one bias, and from
    /// several the plane <c>a + b·vgs + c·vds</c> through them by least squares; <c>Tg</c> the same, when every bias fitted
    /// it.
    /// </summary>
    /// <remarks>Noise is optional: a document with no noise data, or whose data has been removed since the fit, exports a
    /// model with no <c>Core.Noise</c> at all — a complete model, whose core is named as noiseless.</remarks>
    public static ComposedNoise? CoreNoise(CmcDocument doc) =>
        Settings(doc).Sources.Count > 0 && Record(doc) is { Biases.Count: > 0 } r ? CoreNoiseOf(r) : null;

    public static ComposedNoise CoreNoiseOf(NoiseFitRecord r)
        => new(r.FittedTg ? Value(r.Biases, b => b.Tg) : null, Value(r.Biases, b => b.Td));

    private static NoiseValue Value(IReadOnlyList<NoiseBiasFit> biases, Func<NoiseBiasFit, double> t)
    {
        if (biases.Select(b => (b.Vgs, b.Vds)).Distinct().Count() == 1) return NoiseValue.Of(biases.Average(t));
        // A plane in (vgs, vds), least squares; the degenerate directions (every bias at one vgs, say) take no slope.
        var rows = biases.Select(b => new[] { 1.0, b.Vgs, b.Vds }).ToList();
        var y = biases.Select(t).ToArray();
        var c = LeastSquares(rows, y);
        string Lit(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        string expr = $"{Lit(c[0])}+({Lit(c[1])})*vgs+({Lit(c[2])})*vds";
        return NoiseValue.Of(Constituent.Equation(expr, [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds]));
    }

    /// <summary>Minimum-norm least squares through the SVD: a direction the rows do not span takes no coefficient.</summary>
    private static double[] LeastSquares(IReadOnlyList<double[]> rows, double[] y)
    {
        int m = rows.Count, n = rows[0].Length;
        var a = new Mat<double>(m, n);
        for (int i = 0; i < m; i++) for (int j = 0; j < n; j++) a[i, j] = rows[i][j];
        var svd = a.Svd();
        var x = new double[n];
        double cut = 1e-10 * svd.S[0];
        for (int k = 0; k < Math.Min(m, n); k++)
        {
            if (!(svd.S[k] > cut)) continue;
            double uty = 0;
            for (int i = 0; i < m; i++) uty += svd.U[i, k] * y[i];
            for (int j = 0; j < n; j++) x[j] += svd.VT[k, j] * uty / svd.S[k];
        }
        return x;
    }

    // ── The data ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Measured noise parameters by bias. A Touchstone two-port with a noise block takes its bias from
    /// <paramref name="source"/>; anything else is a table, one row per line —
    /// <c>Vgs Vds Freq_Hz NFmin_dB |Γopt| ∠Γopt_deg Rn_Ω</c>, Γopt on 50 Ω, <c>#</c> or <c>!</c> starting a comment.
    /// </summary>
    public static IReadOnlyList<(double Vgs, double Vds, IReadOnlyList<(double Hz, TwoPortNoise P)> Rows)> Read(NoiseSource source, string baseDir)
    {
        string path = System.IO.Path.IsPathRooted(source.Path) ? source.Path : System.IO.Path.Combine(baseDir, source.Path);
        if (!File.Exists(path)) throw new ModelingException(source.Path, "the noise data file does not exist.");
        if (System.IO.Path.GetExtension(path).Equals(".s2p", StringComparison.OrdinalIgnoreCase))
        {
            if (source.Vgs is not { } vgs || source.Vds is not { } vds)
                throw new ModelingException(source.Path, "a Touchstone file does not say its bias; state Vgs and Vds for it.");
            var snp = TouchstoneIO.ReadFile(path);
            double r = snp.Z0.Real;
            var rows = snp.NoiseParameters.OrderBy(p => p.FrequencyHz)
                .Select(p => (p.FrequencyHz, new TwoPortNoise(Math.Pow(10, p.NFminDb / 10), p.GammaOpt, p.RnNormalized * r, r))).ToList();
            if (rows.Count == 0) throw new ModelingException(source.Path, "the file has no noise block.");
            return [(vgs, vds, rows)];
        }

        var byBias = new Dictionary<(double, double), List<(double, TwoPortNoise)>>();
        int line = 0;
        foreach (string raw in File.ReadLines(path))
        {
            line++;
            string text = raw.Split('#', '!')[0].Trim();
            if (text.Length == 0) continue;
            var f = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length != 7 || !f.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                throw new ModelingException($"{source.Path}:{line}", "a row is Vgs Vds Freq_Hz NFmin_dB |Γopt| ∠Γopt_deg Rn_Ω.");
            double[] v = [.. f.Select(x => double.Parse(x, CultureInfo.InvariantCulture))];
            var key = (v[0], v[1]);
            if (!byBias.TryGetValue(key, out var list)) byBias[key] = list = [];
            list.Add((v[2], new TwoPortNoise(Math.Pow(10, v[3] / 10), Complex.FromPolarCoordinates(v[4], v[5] * Math.PI / 180), v[6], ReferenceOhm)));
        }
        return [.. byBias.Select(kv => (kv.Key.Item1, kv.Key.Item2, (IReadOnlyList<(double, TwoPortNoise)>)[.. kv.Value.OrderBy(x => x.Item1)]))];
    }

    // ── The fit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits <c>Td</c> — and <c>Tg</c>, when the network has an <c>Ri</c> and the data separates the two — at one bias. The
    /// noise correlation of the whole device is linear in both temperatures, <c>C(Tg, Td) = C₀ + Tg·C_g + Td·C_d</c>, with
    /// <c>C₀</c> the rest of the model — the network's parasitics at the device's temperature, which is the de-embedding.
    /// Three simulations of <paramref name="model"/> give <c>C₀</c>, <c>C_g</c> and <c>C_d</c> at the measured frequencies;
    /// the temperatures are then the least-squares fit of the measured chain correlation, each entry normalised by 4kT0 and
    /// 50 Ω. A negative or ill-separated <c>Tg</c> is not reported: <c>Td</c> is refitted alone, with <c>Ri</c> at the
    /// device temperature.
    /// </summary>
    public static NoiseBiasFit FitBias(ComposedModel model, double vgs, double vds,
                                       IReadOnlyList<(double Hz, TwoPortNoise P)> measured, double ambientC, string scratchDir)
    {
        if (measured.Count == 0) throw new ModelingException(StepName, "a bias has no noise data.");
        double[] hz = [.. measured.Select(m => m.Hz)];
        bool canTg = ComposedNetworkTemplate.Build(model.Network).HasElement(ComposedNoise.GateResistorName);
        const double T1 = 1.0, Step = 1000.0;

        Mat<Complex>[] Run(NoiseValue? tg, double td, string tag) =>
            Simulate(model with { Core = model.Core with { Noise = new ComposedNoise(tg, NoiseValue.Of(td)) } },
                     vgs, vds, hz, ambientC, scratchDir, tag);

        var cm = measured.Select(m => NoiseMetrics.ChainCorrelation(m.P)).ToArray();
        var a  = Run(canTg ? NoiseValue.Of(T1) : null, T1, "a");
        var d  = Run(canTg ? NoiseValue.Of(T1) : null, T1 + Step, "d");
        var cd = Diff(d, a, Step);

        double tg = double.NaN, td;
        if (canTg)
        {
            var g  = Run(NoiseValue.Of(T1 + Step), T1, "g");
            var cg = Diff(g, a, Step);
            var c0 = a.Select((x, i) => x - new Complex(T1, 0) * cg[i] - new Complex(T1, 0) * cd[i]).ToArray();
            var (x, ok) = Solve(cm, c0, [cg, cd]);
            if (ok && x[0] > 0 && x[1] > 0) { tg = x[0]; td = x[1]; return Report(); }
        }
        // Td alone: Ri, if there is one, at the device temperature — the model with no Tg at all.
        var h   = Run(null, T1, "h");
        var c0d = h.Select((x, i) => x - new Complex(T1, 0) * cd[i]).ToArray();
        var (y, _) = Solve(cm, c0d, [cd]);
        tg = double.NaN;
        td = y[0];
        return Report();

        NoiseBiasFit Report()
        {
            // The modelled correlation at the fit, from the same runs: back to noise parameters on 50 Ω.
            var fitted = Run(double.IsFinite(tg) ? NoiseValue.Of(tg) : null, td, "fit");
            double nf = 0, gm = 0, rn = 0;
            var curves = new List<(double, TwoPortNoise, TwoPortNoise)>();
            for (int i = 0; i < hz.Length; i++)
            {
                var meas = NoiseMetrics.FromChainCorrelation(cm[i], ReferenceOhm);
                var mod  = NoiseMetrics.FromChainCorrelation(fitted[i], ReferenceOhm);
                curves.Add((hz[i], meas, mod));
                nf += Math.Pow(mod.NFminDb - meas.NFminDb, 2);
                gm += Math.Pow((mod.GammaOpt - meas.GammaOpt).Magnitude, 2);
                rn += Math.Pow((mod.Rn - meas.Rn) / meas.Rn, 2);
            }
            int n = hz.Length;
            return new NoiseBiasFit(vgs, vds, tg, td, n, Math.Sqrt(nf / n), Math.Sqrt(gm / n), Math.Sqrt(rn / n)) { Curves = curves };
        }
    }

    private static Mat<Complex>[] Diff(Mat<Complex>[] hi, Mat<Complex>[] lo, double step) =>
        [.. hi.Select((x, i) => new Complex(1 / step, 0) * (x - lo[i]))];

    /// <summary>The four real numbers of a chain correlation, each scaled to order one by 4kT0 and 50 Ω.</summary>
    private static double[] Vec(Mat<Complex> c)
    {
        double s = 4 * NoiseMetrics.KT0;
        return [c[0, 0].Real / (s * ReferenceOhm), c[1, 1].Real * ReferenceOhm / s, c[0, 1].Real / s, c[0, 1].Imaginary / s];
    }

    /// <summary>Least squares of <c>measured − c0 ≈ Σ x_k·basis_k</c> over every frequency; false when the basis does not
    /// separate the unknowns.</summary>
    private static (double[] X, bool Ok) Solve(Mat<Complex>[] measured, Mat<Complex>[] c0, Mat<Complex>[][] basis)
    {
        int k = basis.Length;
        var rows = new List<double[]>();
        var y = new List<double>();
        for (int i = 0; i < measured.Length; i++)
        {
            var r = Vec(measured[i]); var z = Vec(c0[i]);
            var b = basis.Select(bk => Vec(bk[i])).ToArray();
            for (int e = 0; e < 4; e++)
            {
                rows.Add([.. b.Select(bk => bk[e])]);
                y.Add(r[e] - z[e]);
            }
        }
        var a = new Mat<double>(rows.Count, k);
        for (int i = 0; i < rows.Count; i++) for (int j = 0; j < k; j++) a[i, j] = rows[i][j];
        var svd = a.Svd();
        bool ok = svd.S[k - 1] > 1e-8 * svd.S[0];
        return (LeastSquares(rows, [.. y]), ok);
    }

    /// <summary>
    /// The chain correlation of <paramref name="model"/> at one bias and the measured frequencies: the S-parameter noise
    /// analysis of the device between 50 Ω ports, biased through ideal tees (1 H feeds, 1 F blocks — lossless, so noiseless).
    /// </summary>
    private static Mat<Complex>[] Simulate(ComposedModel model, double vgs, double vds, double[] hz, double ambientC,
                                           string scratchDir, string tag)
    {
        Directory.CreateDirectory(scratchDir);
        string file = System.IO.Path.Combine(scratchDir, $"noise-{tag}-{Guid.NewGuid():N}.cmodel");
        CmodelFile.Write(model, file);
        string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        string cnl = $"""
            temp = {N(ambientC)}
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            C:CB1  p1 g  C=1 F
            C:CB2  p2 d  C=1 F
            L:LG  vg g  L=1 H
            L:LD  vd d  L=1 H
            Vdc:VG  vg 0  Vdc={N(vgs)}
            Vdc:VD  vd 0  Vdc={N(vds)}
            ComposedFET:X1  g d 0  File="{file}"
            """;
        try
        {
            var (lib, tb) = new CnlReader().Read(cnl);
            using var nl = new Elaborator(lib).Elaborate(tb);
            var ds = SParameterEngine.Run(nl, hz, AnalysisSettings.Default.WithNoise(true));
            var s  = ds["S"];
            var cs = ds["Cs"];
            var z0 = new[] { new Complex(50, 0), new Complex(50, 0) };
            var result = new Mat<Complex>[hz.Length];
            for (int i = 0; i < hz.Length; i++)
            {
                var sm = new Mat<Complex>(2, 2);
                var cm = new Mat<Complex>(2, 2);
                for (int p = 0; p < 2; p++)
                    for (int q = 0; q < 2; q++) { sm[p, q] = (Complex)s[i, p, q]; cm[p, q] = (Complex)cs[i, p, q]; }
                result[i] = NoiseMetrics.ChainCorrelation(NoiseMetrics.FromWaves(sm, cm, z0, ReferenceOhm));
            }
            return result;
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    /// <summary>Reads the <c>.cmc</c>, fits every source's biases with the model its records assemble, and writes the record
    /// — what the window and the <c>model</c> verb both call.</summary>
    public static NoiseFitRecord FitFile(string cmcPath, RunControl? rc = null)
    {
        cmcPath = System.IO.Path.GetFullPath(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        var settings = Settings(doc);
        if (settings.Sources.Count == 0)
            throw new ModelingException(StepName, "it has no measured noise data; add a noise-parameter file.");
        string dir = System.IO.Path.GetDirectoryName(cmcPath)!;
        string scratch = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"crf-noise-fit-{Guid.NewGuid():N}");
        try
        {
            var model = CmodelExport.Assemble(cmcPath, doc with { Steps = WithoutNoise(doc.Steps) }, scratch);
            var fits = new List<NoiseBiasFit>();
            foreach (var source in settings.Sources)
                foreach (var (vgs, vds, rows) in Read(source, dir))
                {
                    rc?.ThrowIfCancellationRequested();
                    fits.Add(FitBias(model, vgs, vds, rows, settings.AmbientC, scratch));
                }
            var record = new NoiseFitRecord(fits);
            CmcFile.Write(cmcPath, Apply(CmcFile.Read(cmcPath), settings, record));
            return record;
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } }
    }

    private static JsonNode? WithoutNoise(JsonNode? steps)
    {
        if (steps is not JsonObject s) return steps;
        var copy = (JsonObject)JsonNode.Parse(s.ToJsonString())!;
        copy.Remove(StepName);
        return copy;
    }
}
