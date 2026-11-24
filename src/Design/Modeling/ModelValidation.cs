// Validation (brief-modelcomposer-11 R-mc11-4, R-mc11-5; overview D13, D16): how well the composed model predicts every
// kind of data the document holds, at the MEASUREMENT plane, through the engines a design will use.
//
//   S-parameters  every measured bias of every CW S set: the model's S at the device plane (the testbench at the data's
//                 device-plane bias, through ideal tees) with the pads re-embedded, against the data. |ΔS| per bias, and
//                 its median and 95th percentile over every entry — small entries are judged by |ΔS|, never by dB.
//   DC I–V        every point of every DC set: the model's terminal currents at the device-plane bias (a self-heating model
//                 heats itself, as the measurement did), RMS ΔId relative to the set's current range.
//   Pulsed I–V    every mapped point of every pulsed set: the channel current at the point's mapped trap state and its
//                 quiescent junction temperature (MC-10's mapping, MC-9's heating). A circuit's DC solve cannot hold a
//                 trap state, so this one is evaluated at the core, on the intrinsic data MC-4 computed.
//   Large signal  a power sweep (Pout, gain, PAE, DE, drain current against input power) and a two-tone sweep (carrier,
//                 IM3, IM5) by HB with the data's terminations; a loadpull file run through the loadpull engine over its own
//                 loads, the model interpolated to each measured drive.
//
// Every item but the pulsed one generates an ordinary .cnl testbench and runs it through the function its verb calls
// (SParameterEngine, NonlinearDcEngine, CircuitEvaluation for HB and loadpull). Results are DataSets with measured and
// model cubes on the same axes, for MC-15's overlays; the summary is one line per item. Tolerances are settings, kept in
// the .cmc (Steps.Validate). A cancelled validation reports nothing (D13): it throws.

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Design.Modeling;

/// <summary>What one validation item compares.</summary>
public enum ValidationKind { SParams, DcIv, PulsedIv, PowerSweep, TwoTone, Loadpull }

/// <summary>The tolerances an item passes within (R-mc11-4).</summary>
public sealed record ValidationTolerances
{
    /// <summary>The median and the 95th percentile of |ΔS| over every entry.</summary>
    public double SMedian { get; init; } = 0.01;
    public double S95 { get; init; } = 0.03;
    /// <summary>RMS ΔId over the set's current range.</summary>
    public double IvRelative { get; init; } = 0.02;
    /// <summary>RMS ΔPout (and ΔIM3, ΔIM5), dB.</summary>
    public double PowerDb { get; init; } = 0.5;
    /// <summary>RMS ΔPAE (or ΔDE), percentage points.</summary>
    public double EfficiencyPoints { get; init; } = 3;
    /// <summary>RMS ΔAM/PM at compression, degrees.</summary>
    public double AmPmDegrees { get; init; } = 5;
    /// <summary>The mismatch between measured and model impedances — Zin at compression, the MXP and MXE loads — as a VSWR.</summary>
    public double ImpedanceVswr { get; init; } = 1.5;

    public static ValidationTolerances Default { get; } = new();
}

/// <summary>The validation's settings: tolerances, the data sets (empty: every one), the harmonic count, and the gain
/// compression a loadpull is compared at.</summary>
public sealed record ValidationSettings
{
    public ValidationTolerances Tolerances { get; init; } = ValidationTolerances.Default;
    public IReadOnlyList<string> DataSets { get; init; } = [];
    public int MaxHarmonic { get; init; } = 5;
    /// <summary>The gain compression, dB, at which a loadpull's model and measurement are compared, load by load.</summary>
    public double Compression { get; init; } = 3;
    /// <summary>The gain <see cref="Compression"/> is measured on: <c>Gt_dB</c> (the default) or <c>Gp_dB</c> (brief LPW).</summary>
    public string CompressionReference { get; init; } = CompressionGain.Gt;
    /// <summary>Where <see cref="Compression"/> is measured from: the gain's running maximum (the default) or its
    /// small-signal value at the lowest drive.</summary>
    public CompressionType CompressionType { get; init; } = CompressionType.Gmax;

    public static ValidationSettings Default { get; } = new();

    private static readonly string[] Keys = ["Tolerances", "DataSets", "MaxHarmonic", "Compression", "CompressionReference", "CompressionType"];
    private static readonly string[] ToleranceKeys = ["SMedian", "S95", "IvRelative", "PowerDb", "EfficiencyPoints", "AmPmDegrees", "ImpedanceVswr"];

    public static ValidationSettings Parse(JsonNode node, string f)
    {
        var o = CmcExtrinsic.Obj(node, f);
        CmcExtrinsic.Only(o, f, Keys);
        var t = ValidationTolerances.Default;
        if (o["Tolerances"] is { } tn)
        {
            var to = CmcExtrinsic.Obj(tn, $"{f}.Tolerances");
            CmcExtrinsic.Only(to, $"{f}.Tolerances", ToleranceKeys);
            double P(string k, double d)
            {
                double v = CmcExtrinsic.Num(to, k, $"{f}.Tolerances.{k}") ?? d;
                return v > 0 ? v : throw new CmcFormatException($"{f}.Tolerances.{k}", $"{v} is not positive.");
            }
            t = new ValidationTolerances
            {
                SMedian = P("SMedian", t.SMedian), S95 = P("S95", t.S95), IvRelative = P("IvRelative", t.IvRelative),
                PowerDb = P("PowerDb", t.PowerDb), EfficiencyPoints = P("EfficiencyPoints", t.EfficiencyPoints),
                AmPmDegrees = P("AmPmDegrees", t.AmPmDegrees), ImpedanceVswr = P("ImpedanceVswr", t.ImpedanceVswr) is var w && w > 1 ? w
                    : throw new CmcFormatException($"{f}.Tolerances.ImpedanceVswr", $"{w} is not a VSWR above 1."),
            };
        }
        int harm = (int)(CmcExtrinsic.Num(o, "MaxHarmonic", $"{f}.MaxHarmonic") ?? 5);
        if (harm < 1) throw new CmcFormatException($"{f}.MaxHarmonic", $"{harm} is not a harmonic count.");
        double compression = CmcExtrinsic.Num(o, "Compression", $"{f}.Compression") ?? 3;
        if (!(compression > 0)) throw new CmcFormatException($"{f}.Compression", $"{compression} is not a compression in dB.");
        string reference = CompressionGain.Gt;
        if (o["CompressionReference"] is { } rn)
        {
            string r = rn.GetValueKind() == System.Text.Json.JsonValueKind.String ? rn.GetValue<string>() : "";
            reference = r is "Gt" or "Gt_dB" ? CompressionGain.Gt : r is "Gp" or "Gp_dB" ? CompressionGain.Gp
                : throw new CmcFormatException($"{f}.CompressionReference", $"'{r}' is not Gt or Gp.");
        }
        var ctype = CompressionType.Gmax;
        if (o["CompressionType"] is { } tn2)
        {
            string r = tn2.GetValueKind() == System.Text.Json.JsonValueKind.String ? tn2.GetValue<string>() : "";
            ctype = r == "Gmax" ? CompressionType.Gmax : r == "Gss" ? CompressionType.Gss
                : throw new CmcFormatException($"{f}.CompressionType", $"'{r}' is not Gmax or Gss.");
        }
        return new ValidationSettings
        {
            Tolerances = t,
            DataSets = o["DataSets"] is { } d ? CurrentFitSettings.Names(d, $"{f}.DataSets") : [],
            MaxHarmonic = harm,
            Compression = compression,
            CompressionReference = reference,
            CompressionType = ctype,
        };
    }

    public JsonNode ToJson()
    {
        var o = new JsonObject();
        var t = Tolerances;
        if (t != ValidationTolerances.Default)
            o["Tolerances"] = new JsonObject
            {
                ["SMedian"] = t.SMedian, ["S95"] = t.S95, ["IvRelative"] = t.IvRelative,
                ["PowerDb"] = t.PowerDb, ["EfficiencyPoints"] = t.EfficiencyPoints,
                ["AmPmDegrees"] = t.AmPmDegrees, ["ImpedanceVswr"] = t.ImpedanceVswr,
            };
        if (DataSets.Count > 0) o["DataSets"] = new JsonArray([.. DataSets.Select(n => (JsonNode)n)]);
        if (MaxHarmonic != 5) o["MaxHarmonic"] = MaxHarmonic;
        if (Compression != 3) o["Compression"] = Compression;
        if (CompressionReference != CompressionGain.Gt) o["CompressionReference"] = CompressionGain.Short(CompressionReference);
        if (CompressionType != CompressionType.Gmax) o["CompressionType"] = CompressionType.ToString();
        return o;
    }
}

/// <summary>One measure an item is judged by: its value, the tolerance it passes within, and the unit both are in.</summary>
public sealed record ValidationMetric(string Name, double Value, double Tolerance, string Unit);

/// <summary>
/// One item: the data set, what it compared, whether it passed, its one line, its measured-and-model DataSet and the
/// testbench it ran (empty for the pulsed item, which runs no circuit).
/// </summary>
public sealed record ValidationItem(string Set, ValidationKind Kind, bool Passed, string Line, DataSet Data, string Testbench)
{
    /// <summary>What the item ran at — bias, terminations, frequency, temperature, self-heating, solver settings — one line
    /// each, enough to reproduce it.</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];
    /// <summary>The measures it is judged by.</summary>
    public IReadOnlyList<ValidationMetric> Metrics { get; init; } = [];
    /// <summary>The points (or drive levels) the solve did not converge at, out of <see cref="Count"/>.</summary>
    public int Failed { get; init; }
    public int Count { get; init; }
}

/// <summary>A validation: the model it ran, every item, and a line for each set that has no item (and why).</summary>
public sealed record ValidationResult(string ModelPath, IReadOnlyList<ValidationItem> Items, IReadOnlyList<string> Notes)
{
    public bool Passed => Items.All(i => i.Passed);
    public IReadOnlyList<string> Lines => [.. Items.Select(i => i.Line), .. Notes];
}

public static class ModelValidation
{
    public const string StepName = "Validate";

    /// <summary>The folder, in the document's cache, that the current state is exported to when no .cmodel is named.</summary>
    public const string CacheFolder = "validate";

    // ── The document ──────────────────────────────────────────────────────────

    public static ValidationSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? ValidationSettings.Parse(s, $"Steps.{StepName}.Settings") : ValidationSettings.Default;

    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Settings"]);
        Settings(doc);
    }

    public static CmcDocument WithSettings(CmcDocument doc, ValidationSettings settings)
    {
        var steps = doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();
        steps[StepName] = new JsonObject { ["Settings"] = settings.ToJson() };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    // ── The run ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates <paramref name="cmodelPath"/>, or — when it is null — the document's current state, exported into its
    /// cache (<c>&lt;name&gt;.cmc-cache/validate/model.cmodel</c>, derived data) — what the window and the <c>model</c> verb
    /// call (R-mc11-4). Throws <see cref="OperationCanceledException"/> when cancelled: a cancelled validation reports
    /// nothing.
    /// </summary>
    public static ValidationResult RunFile(string cmcPath, string? cmodelPath = null, ValidationSettings? settings = null,
                                           RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        settings ??= Settings(doc);
        var (model, data, work) = Prepare(cmcPath, doc, cmodelPath, rc);
        return Run(cmcPath, doc, data, model, settings, work, rc);
    }

    /// <summary>
    /// What a run needs before its items: the document's data, the work folder, and the model's path — <paramref name="cmodelPath"/>,
    /// or the current state exported into the work folder when it is null.
    /// </summary>
    internal static (string Model, IReadOnlyDictionary<string, NormalisedData> Data, string Work) Prepare(
        string cmcPath, CmcDocument doc, string? cmodelPath, RunControl? rc)
    {
        // The cache opens first: a rebuild swaps its whole folder, and would take a work folder made before it along.
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        string work = Path.Combine(CmcCache.FolderFor(cmcPath), CacheFolder);
        Directory.CreateDirectory(work);
        if (cmodelPath is null)
        {
            cmodelPath = Path.Combine(work, "model.cmodel");
            var model = CmodelExport.Assemble(cmcPath, doc, work, rc: rc);
            using var buffer = new MemoryStream();
            CmodelFile.Write(model with { Symbol = null }, buffer);
            File.WriteAllBytes(cmodelPath, buffer.ToArray());
        }
        return (Path.GetFullPath(cmodelPath), data, work);
    }

    /// <summary>
    /// Validates <paramref name="cmodelPath"/> against the document's data. <paramref name="work"/> is where generated
    /// files a testbench names (a loadpull grid) are written.
    /// </summary>
    public static ValidationResult Run(string cmcPath, CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       string cmodelPath, ValidationSettings settings, string work, RunControl? rc = null)
    {
        var model = CmodelFile.Read(cmodelPath);
        var ctx = new Context(Path.GetFullPath(cmcPath), doc, data, cmodelPath, model, settings, work, rc);
        var items = new List<ValidationItem>();
        var notes = new List<string>();
        var sets = settings.DataSets.Count == 0 ? doc.DataSets
            : [.. settings.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                                ?? throw new ModelingException(StepName, $"there is no data set '{n}'."))];
        foreach (var set in sets)
        {
            rc?.ThrowIfCancellationRequested();
            try
            {
                var item = set.Kind switch
                {
                    DataSetKind.SParams => SParams(ctx, set),
                    DataSetKind.DcIv => Dc(ctx, set),
                    DataSetKind.SimulatedTables when data.TryGetValue(set.Name, out var nd) && nd.Points.Any(p => double.IsFinite(p.Id)) => Dc(ctx, set),
                    DataSetKind.PulsedIv => Pulsed(ctx, set),
                    DataSetKind.LargeSignal => set.Conditions.LargeSignal!.Measurement switch
                    {
                        LargeSignalMeasurement.PowerSweep => PowerSweep(ctx, set),
                        LargeSignalMeasurement.TwoTone => TwoTone(ctx, set),
                        _ => Loadpull(ctx, set),
                    },
                    _ => null,
                };
                if (item is null) notes.Add($"{set.Name}: {set.Kind} data is not validated.");
                else items.Add(item);
            }
            catch (ModelingException ex) { notes.Add($"{set.Name}: not validated — {ex.Message}"); }
        }
        return new ValidationResult(cmodelPath, items, notes);
    }

    /// <summary>Every item's testbench written beside the <c>.cmc</c> as <c>&lt;name&gt;.validate.&lt;set&gt;.cnl</c> —
    /// "show testbench" (R-mc11-4). Returns the paths.</summary>
    public static IReadOnlyList<string> WriteTestbenches(string cmcPath, ValidationResult result)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, stem = Path.GetFileNameWithoutExtension(cmcPath);
        var paths = new List<string>();
        foreach (var item in result.Items.Where(i => i.Testbench.Length > 0))
        {
            string path = Path.Combine(dir, $"{stem}.validate.{Safe(item.Set)}.cnl");
            File.WriteAllText(path, item.Testbench);
            paths.Add(path);
        }
        return paths;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    internal sealed record Context(string CmcPath, CmcDocument Doc, IReadOnlyDictionary<string, NormalisedData> Data,
                                   string ModelPath, ComposedModel Model, ValidationSettings Settings, string Work, RunControl? Rc)
    {
        public string Dir => Path.GetDirectoryName(CmcPath)!;
        public int N => Model.BasePeriphery.Fingers;
        public ValidationTolerances Tol => Settings.Tolerances;
        private IntrinsicResult? _intrinsic;
        public IntrinsicResult Intrinsic => _intrinsic ??= new IntrinsicExtraction(CmcPath).Run(Doc, Data, rc: Rc);
        public string Device(bool intrinsic, string g, string d) => ValidationBench.Device(ModelPath, N, intrinsic, g, d);
    }

    private static NormalisedData DataOf(Context ctx, CmcDataSet set) =>
        ctx.Data.TryGetValue(set.Name, out var nd) ? nd : throw new ModelingException(set.Name, "its data did not import.");

    /// <summary>The set's points at the device plane: the bias moved through the bias path and the pads (MC-4's own
    /// re-referencing with no network), and whether the set is stated at the intrinsic plane.</summary>
    internal static (double[] Vgs, double[] Vds, bool Intrinsic) DevicePlane(Context ctx, CmcDataSet set, NormalisedData nd)
    {
        var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
        var pads = PadDeembedding.Load(CmcExtrinsic.Pads(ctx.Doc), ctx.Dir);
        bool beforePads = plane is ReferencePlane.Instrument or ReferencePlane.Probe;
        var (vgs, vds) = IntrinsicExtraction.ReReference(set, nd, beforePads ? pads.DcDrop() : null, DcTransfer.Identity,
                                                         beforePads && pads.DcNeedsCurrents, []);
        return (vgs, vds, plane == ReferencePlane.Intrinsic);
    }

    private static double AmbientOf(CmcDataSet set, BiasPoint p) =>
        double.IsFinite(p.AmbientC) ? p.AmbientC : set.Conditions.AmbientC ?? 25.0;

    private static ParallelOptions Parallel(Context ctx) =>
        new() { CancellationToken = ctx.Rc?.Token ?? CancellationToken.None, MaxDegreeOfParallelism = Environment.ProcessorCount };

    private static (Core.Design.Library Lib, Core.Design.TestBench Tb) Read(string cnl) => new CnlReader().Read(cnl);

    // ── S-parameters ──────────────────────────────────────────────────────────

    private static ValidationItem SParams(Context ctx, CmcDataSet set)
    {
        var nd = DataOf(ctx, set);
        if (nd.S is null || nd.Ports != 2) throw new ModelingException(set.Name, "it holds no 2-port S-parameters.");
        var (vgs, vds, intrinsic) = DevicePlane(ctx, set, nd);
        double[] f = nd.FrequenciesHz!;
        var points = Enumerable.Range(0, nd.Count).Where(k => !nd.Points[k].Excluded).ToArray();
        if (points.Length == 0) throw new ModelingException(set.Name, "every bias point is excluded.");
        ctx.Rc?.BeginStage($"Validating S of {set.Name}", points.Length);

        int nf = f.Length;
        var model = new Complex[nd.Count * nf * 4];
        string device = ctx.Device(intrinsic, "g", "d");
        System.Threading.Tasks.Parallel.ForEach(points, Parallel(ctx), k =>
        {
            var p = nd.Points[k];
            var (lib, tb) = Read(ValidationBench.SParams(set.Name, device, AmbientOf(set, p), vgs[k], vds[k], f, nd.Z0));
            using var nl = new Elaborator(lib).Elaborate(tb);
            var s = SParameterEngine.Run(nl, lib, tb, null, f, HbCircuitRun.Settings())["S"];
            for (int q = 0; q < nf; q++)
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 2; j++)
                        model[(k * nf + q) * 4 + i * 2 + j] = (Complex)s[q, i, j];
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        // Back to the measurement plane: the same pads the data was de-embedded through.
        var plane = set.Conditions.ReferencePlane ?? ReferencePlane.Probe;
        var modelData = new NormalisedData { Points = nd.Points, Ports = 2, FrequenciesHz = f, S = model, Z0 = nd.Z0 };
        if (plane is ReferencePlane.Instrument or ReferencePlane.Probe)
            modelData = PadDeembedding.Load(CmcExtrinsic.Pads(ctx.Doc), ctx.Dir).Embed(modelData, set.Name, ctx.Rc);

        var all = new List<double>();
        var perBias = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        foreach (int k in points)
        {
            double ss = 0;
            for (int q = 0; q < nf * 4; q++)
            {
                double d = Complex.Abs(modelData.S![k * nf * 4 + q] - nd.S[k * nf * 4 + q]);
                all.Add(d);
                ss += d * d;
            }
            perBias[k] = Math.Sqrt(ss / (nf * 4));
        }
        all.Sort();
        double median = Quantile(all, 0.5), p95 = Quantile(all, 0.95);
        bool pass = median <= ctx.Tol.SMedian && p95 <= ctx.Tol.S95;

        var point = PointAxis(nd);
        // i and j, not two axes called "port": a Data Display slice binds to an axis by NAME, so two of one name pin together.
        Axis Port(string name) => new(name, [1, 2], "port");
        var freq = new Axis("freq", f, "Hz");
        var ds = new DataSet();
        ds.Add("S.meas", new DataCube([point, freq, Port("i"), Port("j")], nd.S));
        ds.Add("S.model", new DataCube([point, freq, Port("i"), Port("j")], modelData.S!));
        ds.Add("dS", new DataCube([point], perBias));
        ds.Add("Vgs", new DataCube([point], [.. nd.Points.Select(p => p.Vgs)]));
        ds.Add("Vds", new DataCube([point], [.. nd.Points.Select(p => p.Vds)]));
        string line = $"S {set.Name}: |ΔS| median {G(median)}, 95 % {G(p95)} {Mark(pass)}";
        int k0 = points[0];
        var ambient = points.Select(k => AmbientOf(set, nd.Points[k])).ToList();
        return new ValidationItem(set.Name, ValidationKind.SParams, pass, line, ds,
            ValidationBench.SParams(set.Name, device, AmbientOf(set, nd.Points[k0]), vgs[k0], vds[k0], f, nd.Z0))
        {
            Conditions =
            [
                Biases(points.Length, points.Select(k => vgs[k]), points.Select(k => vds[k]), intrinsic ? "intrinsic" : "device"),
                $"Frequency: {GHz(f[0])} – {GHz(f[^1])} GHz, {f.Length} point(s)",
                $"Ports: {G(nd.Z0)} Ω, bias through ideal tees (1 H, 1 F)" +
                (plane is ReferencePlane.Instrument or ReferencePlane.Probe ? $"; the model's S re-embedded in the pads to the {Plane(plane)} plane" : ""),
                Ambient(ctx, ambient),
                "Solver: linear S-parameters at the DC operating point of each bias",
            ],
            Metrics = [new("|ΔS| median", median, ctx.Tol.SMedian, ""), new("|ΔS| 95th percentile", p95, ctx.Tol.S95, "")],
            Count = points.Length,
        };
    }

    // ── DC I–V ────────────────────────────────────────────────────────────────

    private static ValidationItem Dc(Context ctx, CmcDataSet set)
    {
        var nd = DataOf(ctx, set);
        var (vgs, vds, intrinsic) = DevicePlane(ctx, set, nd);
        var points = Enumerable.Range(0, nd.Count).Where(k => !nd.Points[k].Excluded && double.IsFinite(nd.Points[k].Id)).ToArray();
        if (points.Length == 0) throw new ModelingException(set.Name, "it has no point with a drain current.");
        ctx.Rc?.BeginStage($"Validating DC of {set.Name}", points.Length);

        string device = ctx.Device(intrinsic, "g", "d");
        var id = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        var ig = Enumerable.Repeat(double.NaN, nd.Count).ToArray();
        var settings = HbCircuitRun.Settings();
        System.Threading.Tasks.Parallel.ForEach(points, Parallel(ctx), k =>
        {
            var (lib, tb) = Read(ValidationBench.Dc(set.Name, device, AmbientOf(set, nd.Points[k]), vgs[k], vds[k]));
            using var nl = new Elaborator(lib).Elaborate(tb);
            var r = NonlinearDcEngine.Run(nl, settings);
            if (r.Converged) (ig[k], id[k]) = (r.ProbeCurrents["IPG"], r.ProbeCurrents["IPD"]);
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        var (rms, rel, n) = Error(points.Select(k => (nd.Points[k].Id, id[k])));
        int failed = points.Length - n;
        bool pass = failed == 0 && rel <= ctx.Tol.IvRelative;

        var point = PointAxis(nd);
        var ds = new DataSet();
        ds.Add("Vgs", new DataCube([point], [.. nd.Points.Select(p => p.Vgs)]));
        ds.Add("Vds", new DataCube([point], [.. nd.Points.Select(p => p.Vds)]));
        ds.Add("Id.meas", new DataCube([point], [.. nd.Points.Select(p => p.Id)]));
        ds.Add("Id.model", new DataCube([point], id));
        ds.Add("Ig.meas", new DataCube([point], [.. nd.Points.Select(p => p.Ig)]));
        ds.Add("Ig.model", new DataCube([point], ig));
        ds.Add("T", new DataCube([point], [.. nd.Points.Select(p => AmbientOf(set, p))]));
        string line = $"DC {set.Name}: Id RMS {Pct(rel)} of range" + (failed > 0 ? $", {failed} point(s) did not converge" : "") + $" {Mark(pass)}";
        int k0 = points[0];
        return new ValidationItem(set.Name, ValidationKind.DcIv, pass, line, ds,
            ValidationBench.Dc(set.Name, device, AmbientOf(set, nd.Points[k0]), vgs[k0], vds[k0]))
        {
            Conditions =
            [
                Biases(points.Length, points.Select(k => vgs[k]), points.Select(k => vds[k]), intrinsic ? "intrinsic" : "device"),
                Ambient(ctx, points.Select(k => AmbientOf(set, nd.Points[k]))),
                $"Solver: DC Newton, residual tolerance {G(settings.NonlinearAbsTol)}, at most {settings.NonlinearMaxIter} iterations",
            ],
            Metrics = [new("Id RMS error", 100 * rel, 100 * ctx.Tol.IvRelative, "% of range")],
            Failed = failed, Count = points.Length,
        };
    }

    // ── Pulsed I–V ────────────────────────────────────────────────────────────

    private static ValidationItem Pulsed(Context ctx, CmcDataSet set)
    {
        ctx.Rc?.BeginStage($"Validating pulsed I–V of {set.Name}", 1);
        var report = new List<string>();
        var settings = CurrentFit.Settings(ctx.Doc) with { DataSets = [set.Name] };
        var ip = CurrentFit.Gather(ctx.Doc, ctx.Data, ctx.Intrinsic, settings, report, ThermalFit.Heating(ctx.Doc));
        var program = ComposedCoreProgram.Compile(ctx.Model, "validation");
        Span<double> s = stackalloc double[RuntimeEvaluator.MaxInputs], g = stackalloc double[RuntimeEvaluator.MaxInputs];
        int nTrap = program.Traps?.States.Count ?? 0;
        var model = new double[ip.X.Length];
        for (int k = 0; k < ip.X.Length; k++)
        {
            var x = ip.X[k];
            double t = ip.HasT ? x[2] : ip.TemperatureC;
            program.DcSeeds(x[0], x[1], t, s);
            if (ip.Trapped && nTrap > 0)
                for (int j = 0; j < nTrap; j++) s[program.TrapLane + j] = x[x.Length - nTrap + j];
            model[k] = program.Channel(s[..program.Width], g);
        }
        var (rms, rel, _) = Error(ip.Id.Select((m, k) => (m, model[k])));
        bool pass = rel <= ctx.Tol.IvRelative;
        var axis = new Axis("point", [.. Enumerable.Range(0, ip.X.Length).Select(i => (double)i)]);
        var ds = new DataSet();
        ds.Add("Vgsi", new DataCube([axis], [.. ip.X.Select(x => x[0])]));
        ds.Add("Vdsi", new DataCube([axis], [.. ip.X.Select(x => x[1])]));
        ds.Add("Id.meas", new DataCube([axis], ip.Id));
        ds.Add("Id.model", new DataCube([axis], model));
        ctx.Rc?.TickStage();
        string line = $"Pulsed {set.Name}: Id RMS {Pct(rel)} of range over {ip.X.Length} point(s) {Mark(pass)}";
        var c = set.Conditions;
        var conditions = new List<string>
        {
            Biases(ip.X.Length, ip.X.Select(x => x[0]), ip.X.Select(x => x[1]), "intrinsic"),
            "Evaluated at the model's core, not in a circuit: each point at its mapped trap state and its quiescent junction temperature",
        };
        if (c.Quiescent is { } q) conditions.Add($"Quiescent bias: Vgs {G(q.Vgs)} V, Vds {G(q.Vds)} V");
        if (c.PulseWidth is { } w) conditions.Add($"Pulse: {G(w * 1e6)} µs" + (c.Period is { } per ? $" every {G(per * 1e6)} µs" : ""));
        conditions.Add(Ambient(ctx, ip.HasT ? ip.X.Select(x => x[2]) : [ip.TemperatureC]));
        return new ValidationItem(set.Name, ValidationKind.PulsedIv, pass, line, ds, "")
        {
            Conditions = conditions,
            Metrics = [new("Id RMS error", 100 * rel, 100 * ctx.Tol.IvRelative, "% of range")],
            Count = ip.X.Length,
        };
    }

    // ── Large signal ──────────────────────────────────────────────────────────

    /// <summary>A large-signal CSV's rows: the input power axis, and each mapped quantity by name (NaN where absent).</summary>
    private static (double[] Pin, Dictionary<string, double[]> Columns) Sweep(Context ctx, CmcDataSet set)
    {
        // A logarithmic quantity is read in its own unit as written; only a current is scaled by its column's unit.
        var im = set.Import;
        var plain = set with { Import = im with { Columns = [.. im.Columns!.Select(c => CmcFile.LargeSignalUnit(c.Quantity) is null ? c : c with { Unit = null })] } };
        var rows = DataImport.CsvRows(plain, ctx.Dir, null).Select(r => r.Values).OrderBy(r => r["pin"]).ToList();
        if (rows.Count == 0) throw new ModelingException(set.Name, "it has no rows.");
        var cols = new Dictionary<string, double[]>(StringComparer.Ordinal);
        foreach (string q in CmcFile.LargeSignalQuantities)
            if (rows.Any(r => r.ContainsKey(q))) cols[q] = [.. rows.Select(r => r.TryGetValue(q, out var v) ? v : double.NaN)];
        return (cols["pin"], cols);
    }

    /// <summary>Runs a generated HB testbench through the run service; its HB result.</summary>
    private static DataSet RunHb(Context ctx, string cnl) => Evaluate(ctx, cnl, "HB", "V");

    /// <summary>Runs a generated testbench through the run service (what its verb calls); the result holding
    /// <paramref name="cube"/>.</summary>
    private static DataSet Evaluate(Context ctx, string cnl, string what, string cube)
    {
        var r = CircuitEvaluation.Evaluate(PreparedCircuit.FromText(cnl, ctx.Work, ctx.Work),
                                           new CircuitEvaluationRequest { Measurements = false, Control = ctx.Rc?.Child() });
        if (r.Status == RunStatus.Cancelled) throw new OperationCanceledException();
        if (r.Status != RunStatus.Success) throw new ModelingException(what, r.StatusMessage);
        return r.Results.FirstOrDefault(x => x.Data.Contains(cube))?.Data
               ?? throw new ModelingException(what, $"the run produced no {cube}.");
    }

    /// <summary>
    /// What the HB result says at every drive level and spectral index: the power into the load and the RF power the gate
    /// takes (W, at <paramref name="spectral"/>), and the DC currents and supply power. Pout and Pin come from the probes
    /// at the device's terminals, so a passive network around them cannot be mistaken for the device.
    /// </summary>
    internal static (double[] Pout, double[] PinDelivered, double[] Id, double[] Ig, double[] Pdc) Powers(DataSet ds, int spectral)
    {
        var v = ds["V"]; var i = ds["I"];
        int nodeAx = AxisIndex(v, "node"), branchAx = AxisIndex(i, "branch");
        int sweeps = v.Axes[0].Name is "node" ? 1 : v.Axes[0].Length;
        var pout = new double[sweeps]; var pin = new double[sweeps]; var id = new double[sweeps]; var ig = new double[sweeps]; var pdc = new double[sweeps];
        for (int k = 0; k < sweeps; k++)
        {
            Complex V(string node, int h) => Cell(v, k, nodeAx, Label(v, nodeAx, node), h);
            Complex I(string probe, int h) => Cell(i, k, branchAx, Label(i, branchAx, probe), h);
            pout[k] = 0.5 * (V("dx", spectral) * Complex.Conjugate(I("IPD", spectral))).Real;
            pin[k] = 0.5 * (V("gx", spectral) * Complex.Conjugate(I("IPG", spectral))).Real;
            id[k] = -I("IPD", 0).Real;
            ig[k] = I("IPG", 0).Real;
            pdc[k] = V("dx", 0).Real * id[k] + V("gx", 0).Real * ig[k];
        }
        return (pout, pin, id, ig, pdc);
    }

    private static int AxisIndex(DataCube c, string name)
    {
        for (int a = 0; a < c.Rank; a++) if (c.Axes[a].Name == name) return a;
        throw new ModelingException("HB", $"the result has no {name} axis.");
    }

    private static int Label(DataCube c, int axis, string label) =>
        Array.IndexOf(c.Axes[axis].Labels ?? [], label) is >= 0 and var i ? i
        : throw new ModelingException("HB", $"the result has no '{label}'.");

    /// <summary>One element of a [sweep?, named, spectral] cube.</summary>
    private static Complex Cell(DataCube c, int sweep, int namedAxis, int named, int spectral)
    {
        int nNamed = c.Axes[namedAxis].Length, nSpec = c.Axes[namedAxis + 1].Length;
        int offset = namedAxis == 0 ? 0 : sweep * nNamed * nSpec;
        return c.ComplexValues[offset + named * nSpec + spectral];
    }

    /// <summary>
    /// A loadpull file read through RfCore's <c>LoadpullFiles</c> — the call the Data Display and <c>render</c> make, so
    /// validation reads the same figures they draw — at one frequency, with the frequency it states.
    /// </summary>
    private static (DataSet Data, double? Hz) ReadLoadpull(string src, CmcDataSet set, double? hz)
    {
        DataSet meas;
        try { meas = LoadpullFiles.Read(src); }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException)
        { throw new ModelingException(set.Sources[0], $"it is not a readable loadpull file ({ex.Message})."); }
        if (!meas.Contains("GammaLoad") || !meas.Contains("PavlDbm"))
            throw new ModelingException(set.Sources[0], "it carries no load grid or drive levels.");
        return OneFrequency(meas, hz, set.Name);
    }

    /// <summary>
    /// A PowerSweep set read from a loadpull file of ONE load (a <c>.satwave</c>, a one-block <c>.cst</c>): the sweep is
    /// the load's drive-up — available power, Pout, Gt, PAE and drain efficiency as the file reads them — and the
    /// conditions it does not state come from the file: frequency, bias, source and load terminations, and the load's
    /// harmonic terminations where the waves measured them.
    /// </summary>
    private static (LargeSignalConditions C, double[] Pin, Dictionary<string, double[]> Columns) SweepFromLoadpull(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        string src = DataImport.ResolveSource(ctx.Dir, set.Sources[0]);
        if (!File.Exists(src)) throw new ModelingException(set.Sources[0], "the file does not exist.");
        var (lp, fFile) = ReadLoadpull(src, set, c.FrequencyHz);
        var gamma = lp["GammaLoad"].ComplexValues;
        if (gamma.Length != 1)
            throw new ModelingException(set.Name, $"the file holds {gamma.Length} loads; a power sweep is one load's drive-up " +
                                                  "(make it a Loadpull set to compare every load).");
        var pavl = lp["PavlDbm"].RealValues;
        int[] rows = [.. Enumerable.Range(0, pavl.Length).Where(k => double.IsFinite(pavl[k])).OrderBy(k => pavl[k])];
        if (rows.Length == 0) throw new ModelingException(set.Name, "no drive level is stated.");
        var cols = new Dictionary<string, double[]>(StringComparer.Ordinal) { ["pin"] = [.. rows.Select(k => pavl[k])] };
        foreach (var (cube, q) in new[] { ("Pout_dBm", "pout"), ("Gt_dB", "gain"), ("PAE", "pae"), ("Efficiency", "de") })
            if (lp.Contains(cube)) { var v = lp[cube].RealValues; cols[q] = [.. rows.Select(k => v[k])]; }
        if (lp.Contains("BiasILoad")) { var v = lp["BiasILoad"].RealValues; cols["id"] = [.. rows.Select(k => v[k])]; }
        if (lp.Contains("BiasISrc")) { var v = lp["BiasISrc"].RealValues; cols["ig"] = [.. rows.Select(k => v[k])]; }

        static Complex Z(Complex g) => 50 * (1 + g) / (1 - g);
        var loadHarm = c.LoadHarmonics;
        if (loadHarm.Count == 0)
        {
            var h = new List<Complex>();
            for (int n = 2; lp.Contains(LoadpullWaves.HarmonicGammaName(n)); n++)
            {
                var g = lp[LoadpullWaves.HarmonicGammaName(n)].ComplexValues[0];
                if (double.IsNaN(g.Real)) break;
                h.Add(Z(g));
            }
            loadHarm = h;
        }
        var filled = c with
        {
            FrequencyHz = c.FrequencyHz ?? fFile ?? throw new ModelingException(set.Name, "the frequency is not stated and the file does not carry one."),
            Vgs = c.Vgs ?? OperatingPoint(lp, "BiasVSrc") ?? throw new ModelingException(set.Name, "the gate bias is not stated."),
            Vds = c.Vds ?? OperatingPoint(lp, "BiasVLoad") ?? throw new ModelingException(set.Name, "the drain bias is not stated."),
            SourceZ = c.SourceZ ?? (lp.Contains("ZSource") ? lp["ZSource"].ComplexValues[0] : 50),
            LoadZ = c.LoadZ ?? (lp.Contains("ZLoad") ? lp["ZLoad"].ComplexValues[0] : Z(gamma[0])),
            LoadHarmonics = loadHarm,
        };
        return (filled, cols["pin"], cols);
    }

    private static ValidationItem PowerSweep(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        double[] pin; Dictionary<string, double[]> cols;
        if (set.Import.Format == ImportSettings.Loadpull) (c, pin, cols) = SweepFromLoadpull(ctx, set);
        else (pin, cols) = Sweep(ctx, set);
        ctx.Rc?.BeginStage($"Validating power sweep {set.Name}", 1);
        double t = set.Conditions.AmbientC ?? 25;
        string cnl = ValidationBench.PowerSweep(set.Name, ctx.Device(false, "gx", "dx"), t, c, pin, ctx.Settings.MaxHarmonic);
        var (pout, pinDel, id, ig, pdc) = Powers(RunHb(ctx, cnl), 1);
        ctx.Rc?.TickStage();

        double[] poutDbm = [.. pout.Select(Dbm)];
        double[] gain = [.. poutDbm.Select((p, k) => p - pin[k])];
        double[] pae = [.. pout.Select((p, k) => 100 * (p - pinDel[k]) / pdc[k])];
        double[] de = [.. pout.Select((p, k) => 100 * p / pdc[k])];
        var axis = new Axis("Pin", pin, "dBm");
        var ds = new DataSet();
        void Pair(string name, string q, double[] model)
        {
            if (cols.TryGetValue(q, out var meas)) ds.Add($"{name}.meas", new DataCube([axis], meas));
            ds.Add($"{name}.model", new DataCube([axis], model));
        }
        Pair("Pout", "pout", poutDbm); Pair("Gain", "gain", gain); Pair("PAE", "pae", pae); Pair("DE", "de", de);
        Pair("Id", "id", id); Pair("Ig", "ig", ig);

        var parts = new List<string>();
        var metrics = new List<ValidationMetric>();
        bool pass = true;
        if (cols.TryGetValue("pout", out var mp))
        {
            double e = Rms(mp, poutDbm);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"Pout RMS {G(e)} dB");
            metrics.Add(new("Pout RMS error", e, ctx.Tol.PowerDb, "dB"));
        }
        else if (cols.TryGetValue("gain", out var mg))
        {
            double e = Rms(mg, gain);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"gain RMS {G(e)} dB");
            metrics.Add(new("Gain RMS error", e, ctx.Tol.PowerDb, "dB"));
        }
        foreach (var (q, model, label) in new[] { ("pae", pae, "PAE"), ("de", de, "DE") })
            if (cols.TryGetValue(q, out var me))
            {
                double e = Rms(me, model);
                pass &= e <= ctx.Tol.EfficiencyPoints;
                parts.Add($"{label} RMS {G(e)} pp");
                metrics.Add(new($"{label} RMS error", e, ctx.Tol.EfficiencyPoints, "pp"));
                break;
            }
        if (parts.Count == 0) throw new ModelingException(set.Name, "it has no pout, gain, pae or de column to compare.");
        return new ValidationItem(set.Name, ValidationKind.PowerSweep, pass,
                                  $"Power {set.Name}: {string.Join(" · ", parts)} {Mark(pass)}", ds, cnl)
        {
            Conditions = LargeSignalConditionsText(ctx, c, pin, t, twoTone: false),
            Metrics = metrics,
            Failed = Unconverged(pout), Count = pin.Length,
        };
    }

    private static ValidationItem TwoTone(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        var (pin, cols) = Sweep(ctx, set);
        ctx.Rc?.BeginStage($"Validating two-tone {set.Name}", 1);
        double t = set.Conditions.AmbientC ?? 25;
        string cnl = ValidationBench.TwoTone(set.Name, ctx.Device(false, "gx", "dx"), t, c, pin, ctx.Settings.MaxHarmonic);
        var ds0 = RunHb(ctx, cnl);
        double f1 = c.FrequencyHz!.Value - c.SpacingHz!.Value / 2, f2 = c.FrequencyHz.Value + c.SpacingHz.Value / 2;
        int Mix(double hz)
        {
            var mix = ds0["V"].Axes[AxisIndex(ds0["V"], "node") + 1].Values;
            for (int m = 0; m < mix.Length; m++) if (Math.Abs(mix[m] - hz) < 1) return m;
            throw new ModelingException(set.Name, $"the spectrum does not hold {G(hz / 1e9)} GHz; raise MaxHarmonic.");
        }
        double[] carrier = [.. Powers(ds0, Mix(f1)).Pout.Select(Dbm)];
        double[] im3 = [.. Powers(ds0, Mix(2 * f1 - f2)).Pout.Select(Dbm)];
        double[] im5 = [.. Powers(ds0, Mix(3 * f1 - 2 * f2)).Pout.Select(Dbm)];
        ctx.Rc?.TickStage();

        var axis = new Axis("Pin", pin, "dBm");
        var ds = new DataSet();
        var parts = new List<string>();
        var metrics = new List<ValidationMetric>();
        bool pass = true;
        foreach (var (q, model, label) in new[] { ("pout", carrier, "Pout"), ("im3", im3, "IM3"), ("im5", im5, "IM5") })
        {
            ds.Add($"{label}.model", new DataCube([axis], model));
            if (!cols.TryGetValue(q, out var meas)) continue;
            ds.Add($"{label}.meas", new DataCube([axis], meas));
            double e = Rms(meas, model);
            pass &= e <= ctx.Tol.PowerDb;
            parts.Add($"{label} RMS {G(e)} dB");
            metrics.Add(new($"{label} RMS error", e, ctx.Tol.PowerDb, "dB"));
        }
        if (parts.Count == 0) throw new ModelingException(set.Name, "it has no pout, im3 or im5 column to compare.");
        return new ValidationItem(set.Name, ValidationKind.TwoTone, pass,
                                  $"Two-tone {set.Name}: {string.Join(" · ", parts)} {Mark(pass)}", ds, cnl)
        {
            Conditions = LargeSignalConditionsText(ctx, c, pin, t, twoTone: true),
            Metrics = metrics,
            Failed = carrier.Count(v => !double.IsFinite(v)), Count = pin.Length,
        };
    }

    private static ValidationItem Loadpull(Context ctx, CmcDataSet set)
    {
        var c = set.Conditions.LargeSignal!;
        string src = DataImport.ResolveSource(ctx.Dir, set.Sources[0]);
        if (!File.Exists(src)) throw new ModelingException(set.Sources[0], "the file does not exist.");
        var (meas, fFile) = ReadLoadpull(src, set, c.FrequencyHz);

        double f0 = c.FrequencyHz ?? fFile ?? throw new ModelingException(set.Name, "the frequency is not stated and the file does not carry one.");
        var zSource = c.SourceZ ?? (meas.Contains("ZSource") ? meas["ZSource"].ComplexValues[0] : 50);
        double vgs = c.Vgs ?? OperatingPoint(meas, "BiasVSrc") ?? throw new ModelingException(set.Name, "the gate bias is not stated.");
        double vds = c.Vds ?? OperatingPoint(meas, "BiasVLoad") ?? throw new ModelingException(set.Name, "the drain bias is not stated.");
        var gamma = meas["GammaLoad"].ComplexValues;
        var pavl = meas["PavlDbm"].RealValues;
        int nGrid = gamma.Length, nPin = pavl.Length / nGrid;
        var zl = meas.Contains("ZLoad") ? meas["ZLoad"].ComplexValues : [.. gamma.Select(g => 50 * (1 + g) / (1 - g))];
        var finite = pavl.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) throw new ModelingException(set.Name, "no drive level is stated.");
        // The model's ladder steps by the smallest gap between the measured drives, at most 200 rungs, and runs 10 dB past
        // the highest measured drive so a model that compresses later than the device still reaches compression.
        double[] levels = [.. finite.Select(v => Math.Round(v, 6)).Distinct().Order()];
        double lo = levels[0], hi = levels[^1] + 10;
        double gap = levels.Length > 1 ? levels.Zip(levels.Skip(1), (a, b) => b - a).Min() : 1;
        double step = Math.Max(Math.Max(gap, 0.1), (hi - lo) / 199);
        double comp = ctx.Settings.Compression;
        string compRef = ctx.Settings.CompressionReference;
        var compType = ctx.Settings.CompressionType;
        // The engine stops each load at `comp` dB below its gain's running maximum. Measured from the small-signal gain the
        // same depth is reached at a higher drive when the gain expands first, so the model is driven further, by the
        // largest expansion a device of this kind shows, to reach the comparison point.
        double stopAt = compType == CompressionType.Gss ? comp + 3 : comp;

        // The model load-pulled over the file's own loads, through the loadpull engine `circuitrf lp` runs, each load driven
        // up to the compression the comparison is made at: the grid is a .gam beside the generated testbench, which names it
        // by its full path so the testbench runs unchanged anywhere.
        string gam = Path.Combine(ctx.Work, $"{Safe(set.Name)}.gam");
        File.WriteAllLines(gam, ["# impedance Z0=50 re_im", .. zl.Select(z => $"{ValidationBench.R(z.Real)} {ValidationBench.R(z.Imaginary)}")]);
        string cnl = ValidationBench.Loadpull(set.Name, ctx.Device(false, "g", "d"), set.Conditions.AmbientC ?? 25, f0, vgs, vds,
                                              zSource, c.SourceHarmonics, c.LoadHarmonics, gam, lo, step, hi, ctx.Settings.MaxHarmonic, stopAt,
                                              CompressionGain.Short(compRef));
        ctx.Rc?.BeginStage($"Validating loadpull {set.Name}", 1);
        var model = Evaluate(ctx, cnl, "loadpull", "StopCode");
        ctx.Rc?.TickStage();

        // At compression, not drive by drive (owner, review 4): each load where BOTH reached P-comp dB on their own
        // drive-up, every figure the measurement carries — a file without efficiency or AM/PM simply compares fewer.
        var measLp = Canonical(meas);
        if (!measLp.Contains(compRef))
            throw new ModelingException(set.Name, $"the measurement carries no {CompressionGain.Short(compRef)}, so it cannot be compared " +
                                                  $"at {CompressionGain.Short(compRef)} compression.");
        var measAt = AtCompression(measLp, nGrid, comp, compRef, compType);
        var modelAt = AtCompression(model, nGrid, comp, compRef, compType);
        int reached = Enumerable.Range(0, nGrid).Count(g => double.IsFinite(measAt["Pout_dBm"][g]));
        if (reached == 0)
            throw new ModelingException(set.Name, $"the measurement reaches {G(comp)} dB compression at no load, so nothing is compared at " +
                                                  "compression; lower the Validate step's Compression to one the data reaches.");

        var metrics = new List<ValidationMetric>();
        var parts = new List<string>();
        bool pass = true;
        int compared = 0;
        void Rms(string key, string label, double tol, string unit)
        {
            var d = Enumerable.Range(0, nGrid).Where(g => double.IsFinite(measAt[key][g]) && double.IsFinite(modelAt[key][g]))
                              .Select(g => measAt[key][g] - modelAt[key][g]).ToList();
            if (d.Count == 0) return;
            if (key == "Pout_dBm") compared = d.Count;
            double e = Math.Sqrt(d.Average(x => x * x));
            pass &= e <= tol;
            parts.Add($"{label} RMS {G(e)} {unit}");
            metrics.Add(new($"{label} RMS error at P-{G(comp)}dB", e, tol, unit));
        }
        Rms("Pout_dBm", "Pout", ctx.Tol.PowerDb, "dB");
        Rms("Gt_dB", "Gt", ctx.Tol.PowerDb, "dB");
        Rms("PAE", "PAE", ctx.Tol.EfficiencyPoints, "pp");
        Rms("Efficiency", "DE", ctx.Tol.EfficiencyPoints, "pp");
        Rms("AMPM_deg", "AM/PM", ctx.Tol.AmPmDegrees, "°");
        var zin = Enumerable.Range(0, nGrid)
            .Where(g => double.IsFinite(measAt["Zin_real"][g]) && double.IsFinite(modelAt["Zin_real"][g]))
            .Select(g => Mismatch(new Complex(measAt["Zin_real"][g], measAt["Zin_imag"][g]), new Complex(modelAt["Zin_real"][g], modelAt["Zin_imag"][g])))
            .Where(double.IsFinite).ToList();
        if (zin.Count > 0)
        {
            double v = Vswr(Math.Sqrt(zin.Average(x => x * x)));
            pass &= v <= ctx.Tol.ImpedanceVswr;
            parts.Add($"Zin VSWR {G(v)}");
            metrics.Add(new($"Zin mismatch at P-{G(comp)}dB, VSWR", v, ctx.Tol.ImpedanceVswr, ""));
        }

        // The optimum loads, each side's own at compression, wherever the measurement has one.
        var measSurf = new LoadpullSurface(measLp);
        var modelSurf = new LoadpullSurface(model);
        var cc = ConstraintSpec.AtCompression(comp, compRef, compType);
        void Optimum(string name, string metric, string label, double tol, string unit, bool max)
        {
            var m = (max ? measSurf.MaxPower(0, cc, SurfacePlane.Gamma) : measSurf.MaxEfficiency(0, cc, SurfacePlane.Gamma))?.Interpolated;
            var s = (max ? modelSurf.MaxPower(0, cc, SurfacePlane.Gamma) : modelSurf.MaxEfficiency(0, cc, SurfacePlane.Gamma))?.Interpolated;
            if (m is not { } gm || s is not { } gs) return;
            double vm = measSurf.MetricAtCoord(0, metric, gm, cc, SurfacePlane.Gamma), vs = modelSurf.MetricAtCoord(0, metric, gs, cc, SurfacePlane.Gamma);
            double vswr = Vswr(Mismatch(50 * (1 + gm) / (1 - gm), 50 * (1 + gs) / (1 - gs)));
            pass &= vswr <= ctx.Tol.ImpedanceVswr;
            metrics.Add(new($"{name} load mismatch, VSWR", vswr, ctx.Tol.ImpedanceVswr, ""));
            string what = $"{name} ZL VSWR {G(vswr)}";
            if (double.IsFinite(vm) && double.IsFinite(vs))
            {
                double d = Math.Abs(vm - vs);
                pass &= d <= tol;
                metrics.Add(new($"{name} {label} error", d, tol, unit));
                what += $", Δ{label} {G(d)} {unit}";
            }
            parts.Add(what);
        }
        Optimum("MXP", "Pout_dBm", "Pout", ctx.Tol.PowerDb, "dB", max: true);
        Optimum("MXE", "Efficiency", "DE", ctx.Tol.EfficiencyPoints, "pp", max: false);

        var gridAx = new Axis("gridPoint", [.. Enumerable.Range(0, nGrid).Select(i => (double)i)]);
        var ds = new DataSet();
        ds.Add("GammaLoad", new DataCube([gridAx], gamma));
        ds.Add("Compression", DataCube.Scalar(comp));
        // Each side's loadpull for the plot, on its own drive levels.
        foreach (var (side, lp) in new[] { ("meas", measLp), ("model", model) })
            foreach (var (from, to) in new[] { ("PavlDbm", "PavlDbm"), ("Pout_dBm", "Pout"), ("Gt_dB", "Gt"), ("PAE", "PAE"), ("Efficiency", "Efficiency") })
                if (lp.Contains(from)) ds.Add($"{to}.{side}", lp[from]);
        foreach (var (side, at) in new[] { ("meas", measAt), ("model", modelAt) })
            foreach (var (key, v) in at) ds.Add($"{key}@compression.{side}", new DataCube([gridAx], v));

        string line = $"Loadpull {set.Name} at P-{G(comp)}dB: {string.Join(" · ", parts)} over {compared} load(s) {Mark(pass)}";
        double ambientC = set.Conditions.AmbientC ?? 25;
        var gammaAbs = gamma.Select(Complex.Abs).ToArray();
        return new ValidationItem(set.Name, ValidationKind.Loadpull, pass, line, ds, cnl)
        {
            Conditions =
            [
                $"Frequency: {GHz(f0)} GHz; bias Vgs {G(vgs)} V, Vds {G(vds)} V at the device's terminals",
                $"Source: {Terminations(zSource, c.SourceHarmonics, ctx.Settings.MaxHarmonic)}",
                $"Load: the file's {nGrid} load(s), |Γ| {G(gammaAbs.Min())} – {G(gammaAbs.Max())} (50 Ω); " +
                Terminations(null, c.LoadHarmonics, ctx.Settings.MaxHarmonic),
                $"Compared at {G(comp)} dB compression of {CompressionGain.Short(compRef)}" +
                (compType == CompressionType.Gss ? " from its small-signal value" : "") +
                $", at each of the {reached} load(s) the measurement reaches it at; " +
                "impedances as the VSWR between measured and model",
                $"Drive: the model from {G(lo)} dBm in {G(step)} dB steps to compression, or to {G(hi)} dBm",
                Ambient(ctx, [ambientC]),
                $"Solver: the loadpull engine (circuitrf lp); {ValidationBench.HbText(ctx.Settings.MaxHarmonic)}",
            ],
            Metrics = metrics,
            Failed = reached - compared, Count = reached,
        };
    }

    /// <summary>A loadpull's DataSet with the names compression is read from: Pout_dBm from the watts, and Gt_dB from Pout
    /// and the available drive where the file carries no gain.</summary>
    private static DataSet Canonical(DataSet lp)
    {
        var ds = new DataSet();
        foreach (var (name, cube) in lp.Cubes) ds.Add(name, cube);
        if (!ds.Contains("Pout_dBm"))
        {
            var w = Watts(lp);
            ds.Add("Pout_dBm", new DataCube([.. lp["PavlDbm"].Axes], [.. w.Select(Dbm)]));
        }
        if (!ds.Contains("Gt_dB"))
        {
            var p = ds["Pout_dBm"].RealValues; var a = ds["PavlDbm"].RealValues;
            ds.Add("Gt_dB", new DataCube([.. lp["PavlDbm"].Axes], [.. p.Select((v, k) => v - a[k])]));
        }
        return ds;
    }

    /// <summary>
    /// Each load's figures at <paramref name="compression"/> dB below its running Gt maximum along its own drive-up — the
    /// gain rule the loadpull engine stops on — interpolated between the two drives either side; NaN for a load that never
    /// compresses that far, or a figure the DataSet does not carry. Tickle points and points that did not converge are
    /// left out. <paramref name="reference"/> names the gain (<c>Gt_dB</c> or <c>Gp_dB</c>) and <paramref name="type"/>
    /// whether the depth is from its running maximum or from its value at the lowest drive (brief LPW).
    /// </summary>
    internal static Dictionary<string, double[]> AtCompression(DataSet lp, int nGrid, double compression,
                                                               string reference = CompressionGain.Gt,
                                                               CompressionType type = CompressionType.Gmax)
    {
        string[] keys = ["Pout_dBm", "Gt_dB", "PAE", "Efficiency", "AMPM_deg", "Zin_real", "Zin_imag"];
        var at = keys.ToDictionary(k => k, _ => Enumerable.Repeat(double.NaN, nGrid).ToArray(), StringComparer.Ordinal);
        var pavl = lp["PavlDbm"].RealValues;
        var gt = lp[CompressionGain.Normalize(reference)].RealValues;
        var conv = lp.Contains("Converged") ? lp["Converged"].RealValues : null;
        var tickle = lp.Contains("IsTickle") ? lp["IsTickle"].RealValues : null;
        int nP = pavl.Length / nGrid;
        for (int g = 0; g < nGrid; g++)
        {
            var row = Enumerable.Range(g * nP, nP)
                .Where(j => double.IsFinite(pavl[j]) && double.IsFinite(gt[j]) && (conv is null || conv[j] == 1) && (tickle is null || tickle[j] != 1))
                .OrderBy(j => pavl[j]).ToArray();
            double gmax = double.NegativeInfinity, before = double.NaN;
            for (int k = 0; k < row.Length; k++)
            {
                gmax = type == CompressionType.Gss ? gt[row[0]] : Math.Max(gmax, gt[row[k]]);
                double depth = gmax - gt[row[k]];
                if (depth >= compression && k > 0)
                {
                    double t = (compression - before) / (depth - before);
                    foreach (string key in keys)
                        if (lp.Contains(key))
                        {
                            var v = lp[key].RealValues;
                            at[key][g] = v[row[k - 1]] + t * (v[row[k]] - v[row[k - 1]]);
                        }
                    break;
                }
                before = depth;
            }
        }
        return at;
    }

    /// <summary>How far apart two impedances are, independent of any reference: |Z1 − Z2| / |Z1 + Z2*|, the reflection
    /// one sees looking into the other.</summary>
    internal static double Mismatch(Complex z1, Complex z2) => Complex.Abs(z1 - z2) / Complex.Abs(z1 + Complex.Conjugate(z2));

    internal static double Vswr(double gamma) => gamma >= 1 ? double.PositiveInfinity : (1 + gamma) / (1 - gamma);

    /// <summary>
    /// A loadpull file's DataSet at one frequency: a multi-frequency file (every cube on a leading <c>freq</c> axis) sliced
    /// at <paramref name="hz"/> — refused when it holds several and none is named, or not the one named — and the
    /// frequency the file states, if it states one.
    /// </summary>
    private static (DataSet Data, double? Hz) OneFrequency(DataSet meas, double? hz, string set)
    {
        var g = meas["GammaLoad"];
        if (g.Rank < 2 || g.Axes[0].Name != "freq")
        {
            double? f = meas.Contains("__Freq") ? meas["__Freq"].RealValues[0]
                      : meas.Contains("ZSource") ? meas["ZSource"].Axes[0].Values[0] : null;
            return (meas, f);
        }
        double[] freqs = g.Axes[0].Values;
        int fi;
        if (hz is { } want)
        {
            fi = Array.FindIndex(freqs, v => Math.Abs(v - want) <= 1e-6 * want);
            if (fi < 0) throw new ModelingException(set, $"the file holds no data at {G(want / 1e9)} GHz; it holds {string.Join(", ", freqs.Select(v => G(v / 1e9)))} GHz.");
        }
        else if (freqs.Length == 1) fi = 0;
        else throw new ModelingException(set, $"the file holds {freqs.Length} frequencies ({string.Join(", ", freqs.Select(v => G(v / 1e9)))} GHz); state FrequencyHz.");
        var one = new DataSet();
        foreach (var (name, cube) in meas.Cubes)
            one.Add(name, cube.Rank > 0 && cube.Axes[0].Name == "freq" ? cube.At("freq", fi) : cube);
        return (one, freqs[fi]);
    }

    /// <summary>Linear interpolation of <paramref name="y"/> over ascending <paramref name="x"/> at <paramref name="at"/>; within
    /// 0.05 dB of an end, that end's value; NaN beyond.</summary>
    internal static double Interpolate(double[] x, double[] y, double at)
    {
        if (x.Length == 0 || at < x[0] - 0.05 || at > x[^1] + 0.05) return double.NaN;
        if (at <= x[0]) return y[0];
        if (at >= x[^1]) return y[^1];
        int j = Array.FindIndex(x, v => v >= at);
        if (x[j] == at || x[j] == x[j - 1]) return y[j];
        double t = (at - x[j - 1]) / (x[j] - x[j - 1]);
        return y[j - 1] + t * (y[j] - y[j - 1]);
    }

    /// <summary>
    /// A loadpull of the model over <paramref name="gammas"/> (50 Ω), as the loadpull DataSet RfCore's surface and the Data
    /// Display read, under the canonical names a measured file and an enriched run carry (<c>gridPoint × pinStep</c>:
    /// Pout_W, Pout_dBm, Gt_dB, Gp_dB, Efficiency and PAE in %, Pdc_W, PavlDbm, IsTickle, the bias, Converged; GammaLoad,
    /// ZLoad, StopCode).
    ///
    /// <para><b>The loadpull engine's drive-up, one load at a time and the loads in parallel</b> (owner, review 4). Each load
    /// is driven from <paramref name="pinStart"/> by the engine `circuitrf lp` runs, and stops where its gain has fallen
    /// <paramref name="compression"/> dB or at <paramref name="pinMax"/>. A fixed ladder solved every rung to the cap and
    /// kept only those up to compression: where the gain is high the rest was deep overdrive that failed to converge at
    /// every rung, and a class at 2 GHz ran for many minutes. One engine run per load rather than one over the disc,
    /// because the engine solves its loads in turn; the region of convergence is now the loads the engine's continuation
    /// and cold retries bring to compression or the cap. <paramref name="testbench"/> is the whole disc as one loadpull,
    /// which <c>circuitrf lp</c> runs as written.</para>
    /// </summary>
    internal static DataSet LoadSweep(Context ctx, string set, double ambientC, double f0, double vgs, double vds, Complex sourceZ,
                                      IReadOnlyList<Complex> sourceHarmonics, IReadOnlyList<Complex> loadHarmonics,
                                      IReadOnlyList<Complex> gammas, double pinStart, double pinStep, double pinMax,
                                      double compression, out string testbench)
    {
        int nG = gammas.Count;
        var zl = gammas.Select(g => 50 * (1 + g) / (1 - g)).ToArray();
        string device = ctx.Device(false, "g", "d");
        string Gam(string name, IEnumerable<Complex> z)
        {
            string path = Path.Combine(ctx.Work, $"{Safe(name)}.gam");
            File.WriteAllLines(path, ["# impedance Z0=50 re_im", .. z.Select(x => $"{ValidationBench.R(x.Real)} {ValidationBench.R(x.Imaginary)}")]);
            return path;
        }
        string Bench(string gam) => ValidationBench.Loadpull(set, device, ambientC, f0, vgs, vds, sourceZ, sourceHarmonics, loadHarmonics,
                                                             gam, pinStart, pinStep, pinMax, ctx.Settings.MaxHarmonic, compression);
        testbench = Bench(Gam(set, zl));

        var runs = new DataSet?[nG];
        ctx.Rc?.BeginStage($"Load-pulling {set}", nG);
        System.Threading.Tasks.Parallel.For(0, nG, Parallel(ctx), g =>
        {
            try { runs[g] = Evaluate(ctx, Bench(Gam($"{set} {g}", [zl[g]])), "loadpull", "StopCode"); }
            catch (OperationCanceledException) { return; }
            catch (ModelingException) { }
            ctx.Rc?.TickStage();
        });
        ctx.Rc?.ThrowIfCancellationRequested();

        // Each load's ladder is as long as its own drive-up; the shorter ones are padded, not converged.
        int nP = Math.Max(1, runs.Max(r => r is not null && r.Contains("PavlDbm") ? r["PavlDbm"].Axes[^1].Length : 0));
        string[] names = ["Converged", "IsTickle", "PavlDbm", "Pout_W", "Pout_dBm", "Gt_dB", "Gp_dB", "Efficiency", "PAE", "Pdc_W",
                          "BiasVLoad", "BiasILoad", "BiasVSrc", "BiasISrc"];
        var gridAxis = new Axis("gridPoint", [.. Enumerable.Range(0, nG).Select(i => (double)i)]);
        var pinAxis = new Axis("pinStep", [.. Enumerable.Range(0, nP).Select(i => (double)i)]);
        var ds = new DataSet();
        foreach (string n in names)
        {
            bool flag = n is "Converged" or "IsTickle";
            var v = Enumerable.Repeat(flag ? 0.0 : double.NaN, nG * nP).ToArray();
            for (int g = 0; g < nG; g++)
                if (runs[g] is { } r && r.Contains(n))
                {
                    var row = r[n].RealValues;
                    Array.Copy(row, 0, v, g * nP, Math.Min(row.Length, nP));
                }
            ds.Add(n, new DataCube([gridAxis, pinAxis], v));
        }
        ds.Add("ZLoad", new DataCube([gridAxis], zl));
        ds.Add("GammaLoad", new DataCube([gridAxis], [.. gammas]));
        ds.Add("StopCode", new DataCube([gridAxis], [.. runs.Select(r => r is not null && r.Contains("StopCode") ? r["StopCode"].RealValues[0] : 3)]));
        // The frequency, on the carrier a loadpull file's reader writes: without it a surface's summary reads 0 GHz.
        ds.Add("__Freq", new DataCube([new Axis("freq", [f0], "Hz")], [f0]));
        return ds;
    }

    private static double? OperatingPoint(DataSet ds, string cube) =>
        ds.Contains(cube) && ds[cube].RealValues.FirstOrDefault(double.IsFinite) is var v && double.IsFinite(v) ? v : null;

    /// <summary>A loadpull result's output power in watts: <c>Pout_W</c> or <c>Pout</c>, or <c>Pout_dBm</c> converted.</summary>
    private static double[] Watts(DataSet ds) =>
        ds.Contains("Pout_W") ? ds["Pout_W"].RealValues
        : ds.Contains("Pout") ? ds["Pout"].RealValues
        : ds.Contains("Pout_dBm") ? [.. ds["Pout_dBm"].RealValues.Select(p => Math.Pow(10, (p - 30) / 10))]
        : throw new ModelingException("loadpull", "the result carries no output power.");

    // ── What a test ran at ────────────────────────────────────────────────────

    /// <summary>The biases a test ran at: <c>Biases: 12 point(s) at the device plane — Vgs …, Vds …</c>.</summary>
    internal static string Biases(int count, IEnumerable<double> vgs, IEnumerable<double> vds, string plane) =>
        $"Biases: {count} point(s) at the {plane} plane — Vgs {Values(vgs, "V")}, Vds {Values(vds, "V")}";

    /// <summary>
    /// The distinct values of a sweep, as a reader reproduces them: every one when there are few (<c>2, 6 V</c>), its span
    /// and count when there are many (<c>−4 … 0 V, 21 values</c>).
    /// </summary>
    internal static string Values(IEnumerable<double> values, string unit)
    {
        var v = Snapped(values).Select(x => Math.Round(x, 4) + 0.0).Distinct().Order().ToList();
        string u = unit.Length > 0 ? " " + unit : "";
        return v.Count switch
        {
            0 => "—",
            <= 8 => string.Join(", ", v.Select(G)) + u,
            _ => $"{G(v[0])} … {G(v[^1])}{u}, {v.Count} values",
        };
    }

    /// <summary>The ambient a test ran at and how the model heats itself there: Rth at that ambient, or isothermal.</summary>
    internal static string Ambient(Context ctx, IEnumerable<double> ambientC) => Ambient(ctx.Model.Thermal, ambientC);

    internal static string Ambient(ModelThermal? thermal, IEnumerable<double> ambientC)
    {
        var t = ambientC.Where(double.IsFinite).Select(x => Math.Round(x, 2)).Distinct().Order().ToList();
        string at = $"Ambient: {Values(t, "°C")}; ";
        if (thermal is null) return at + "isothermal (the model has no self-heating)";
        string rth = string.Join(", ", (t.Count > 0 ? t : [thermal.Tref]).Take(3).Select(x => $"{G(thermal.RthAt(x))} °C/W at {G(x)} °C"));
        double cth = thermal.Poles.Sum(p => p.Cth);
        return at + $"self-heating on, Rth {rth}" + (cth > 0 ? $", Cth {G(cth)} J/°C over {thermal.Poles.Count} pole(s)" : "");
    }

    internal static string Plane(ReferencePlane plane) => plane.ToString().ToLowerInvariant();

    internal static string GHz(double hz) => G(hz / 1e9);

    /// <summary>A termination as the testbench applies it: <c>Z(f0) 50 Ω; 2f0 short, 3f0 short</c>. Null
    /// <paramref name="fundamental"/> leaves it out (a loadpull's swept load).</summary>
    internal static string Terminations(Complex? fundamental, IReadOnlyList<Complex> harmonics, int maxHarm)
    {
        var parts = new List<string>();
        if (fundamental is { } z1) parts.Add($"f0 {Ohms(z1)}");
        for (int k = 2; k <= maxHarm; k++)
            parts.Add($"{k}f0 {(k - 2 < harmonics.Count ? Ohms(harmonics[k - 2]) : "short")}");
        return string.Join(", ", parts);
    }

    /// <summary>An impedance as a reader types it back: <c>50 Ω</c>, <c>12.5+j8.1 Ω</c>, <c>short</c>, <c>open</c>.</summary>
    internal static string Ohms(Complex z) =>
        Complex.Abs(z) <= 1e-3 ? "short" : Complex.Abs(z) >= 1e5 ? "open"
        : Math.Abs(z.Imaginary) <= 1e-6 * Complex.Abs(z) ? $"{G(z.Real)} Ω"
        : Math.Abs(z.Real) <= 1e-3 ? $"{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))} Ω"
        : $"{G(z.Real)}{(z.Imaginary < 0 ? "−" : "+")}j{G(Math.Abs(z.Imaginary))} Ω";

    private static IReadOnlyList<string> LargeSignalConditionsText(Context ctx, LargeSignalConditions c, double[] pin, double ambientC, bool twoTone)
    {
        int h = ctx.Settings.MaxHarmonic;
        return
        [
            (twoTone ? $"Frequency: two tones {GHz(c.FrequencyHz!.Value)} GHz ± {G(c.SpacingHz!.Value / 2e6)} MHz"
                     : $"Frequency: {GHz(c.FrequencyHz!.Value)} GHz") + $"; bias Vgs {G(c.Vgs!.Value)} V, Vds {G(c.Vds!.Value)} V at the device's terminals",
            $"Source: {Terminations(c.SourceZ ?? 50, c.SourceHarmonics, h)}",
            $"Load: {Terminations(c.LoadZ ?? 50, c.LoadHarmonics, h)}",
            $"Drive: available power {Values(pin, "dBm")}" + (twoTone ? " per tone" : ""),
            Ambient(ctx, [ambientC]),
            $"Solver: {(twoTone ? ValidationBench.TwoToneText(h) : ValidationBench.HbText(h))}",
        ];
    }

    /// <summary>The drive levels a sweep produced no output at — where HB did not converge.</summary>
    private static int Unconverged(double[] pout) => pout.Count(p => !(p > 0));

    // ── Arithmetic ────────────────────────────────────────────────────────────

    internal static double Dbm(double watts) => watts > 0 ? 10 * Math.Log10(watts * 1e3) : double.NaN;

    /// <summary>RMS of measured − model over the pairs where both are finite; RMS relative to the measured range; count.</summary>
    internal static (double Rms, double Relative, int Count) Error(IEnumerable<(double Meas, double Model)> pairs)
    {
        var list = pairs.Where(p => double.IsFinite(p.Meas) && double.IsFinite(p.Model)).ToList();
        if (list.Count == 0) return (double.NaN, double.NaN, 0);
        double rms = Math.Sqrt(list.Average(p => (p.Meas - p.Model) * (p.Meas - p.Model)));
        double range = list.Max(p => p.Meas) - list.Min(p => p.Meas);
        return (rms, range > 0 ? rms / range : rms, list.Count);
    }

    private static double Rms(double[] meas, double[] model) => Error(meas.Zip(model)).Rms;

    internal static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        double x = q * (sorted.Count - 1);
        int i = (int)Math.Floor(x);
        return i + 1 < sorted.Count ? sorted[i] + (x - i) * (sorted[i + 1] - sorted[i]) : sorted[i];
    }

    private static Axis PointAxis(NormalisedData nd) =>
        new("point", [.. Enumerable.Range(0, nd.Count).Select(i => (double)i)], labels: [.. nd.Points.Select(p => p.PointId)]);

    /// <summary>Three significant figures as a reader writes them: <c>0.848</c>, <c>25.5</c>, <c>1000</c>, <c>4e-7</c>, never
    /// <c>1E+03</c> or <c>-0</c>.</summary>
    internal static string G(double v)
    {
        if (!double.IsFinite(v)) return v.ToString(CultureInfo.InvariantCulture);
        double a = Math.Abs(v);
        if (a < 1e-15) return "0";
        if (a is >= 1e-3 and < 1e6)
        {
            double unit = Math.Pow(10, Math.Floor(Math.Log10(a)) - 2);
            return (Math.Round(v / unit) * unit + 0.0).ToString("0.######", CultureInfo.InvariantCulture);
        }
        return v.ToString("0.##e0", CultureInfo.InvariantCulture);
    }

    /// <summary><paramref name="values"/> with the ones negligible beside the largest (a solver's 1e-11 V for 0) made 0.</summary>
    internal static IEnumerable<double> Snapped(IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).ToList();
        double scale = v.Count > 0 ? v.Max(Math.Abs) : 0;
        return v.Select(x => Math.Abs(x) <= 1e-6 * scale ? 0.0 : x);
    }
    private static string Pct(double v) => (100 * v).ToString("G3", CultureInfo.InvariantCulture) + " %";
    internal static string Mark(bool pass) => pass ? "✓" : "✗";
}
