// The current constituents, Id and Ig, from DC or pulsed I–V, and the optional DC/RF dispersion
// (brief-modelcomposer-7 R-mc7-1 … R-mc7-5; overview D4, D6, D13).
//
//   1. The training points are the intrinsic points of the chosen I–V sets (MC-4 R-mc4-4), excluded points removed. One
//      quiescent point unless traps are modelled; a mix is refused naming what differs. With traps (MC-10 R-mc10-4) each
//      point is mapped to its trap state, and the enabled states are inputs of Id (never of Ig). At two or more ambient temperatures T
//      is an input (MC-9 R-mc9-1): each point at its chuck temperature, a DC point at its junction temperature
//      Tamb + Rth·P when self-heating is modelled, a point pulsed from a dissipating quiescent bias at Tamb + Rth·Pq.
//   2. The gate current is split into its two branches (GateSplit), and the channel current is the drain terminal's plus
//      the gate–drain branch, which leaves through the drain.
//   3. Each current is an ANN (MC-6 value training), a table (re-gridded through an ANN or an RBF, then MC-2's spline) or
//      an equation (a template or the user's own, its parameters fitted by the optimizer registry's algorithms).
//   4. The training domain's hull becomes each table or ANN's Domain, and G_x its extrapolation conductance (MC-2).
//   5. Optionally IdRF ([P1] eq. 6.52): derivative training on the intrinsic RF Gm and Gds, anchored to IdDC.
//   6. With self-heating or traps, an ANN Id (and a table's re-gridding network) trains by MC-6's combined mode: the I–V
//      values at each point's Tj and trap state, and the RF Gm/Gds as its derivatives with Tj and the trap states frozen at
//      each S point's DC values ([X03] §IV-C, [X10] §VI) — so one function gives both the DC and the RF output conductance
//      of [P1] eq. 6.69. With traps an equation template gains saturating trap terms, the DC I–V is a target or held out,
//      and its error is reported apart; dispersion (step 5) and drain–source exchange are refused.
//
// The result goes to the .cmc's Steps.Current; arrays to <name>.cmc-cache/current/. Cancelled, nothing is written.

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Engine;
using CircuitRF.Engine.Modeling;
using CircuitRF.Engine.Modeling.Ann;
using CircuitRF.Engine.Optimization;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Modeling;

/// <summary>A fitted current step: what the document records, the arrays its tables and networks name, and the settings
/// it was fitted with.</summary>
public sealed record CurrentFitResult(CurrentFitSettings Settings, CurrentFitRecord Record,
                                      IReadOnlyDictionary<string, DataCube> Arrays);

public static class CurrentFit
{
    public const string StepName = "Current";
    public const string CacheFolder = "current";

    private static readonly IReadOnlyList<string> Inputs = EquationFit.VoltageInputs;
    private static readonly IReadOnlyList<string> InputsWithT = [ComposedCoreProgram.Vgs, ComposedCoreProgram.Vds, ComposedCoreProgram.T];

    // ── The document ──────────────────────────────────────────────────────────

    /// <summary>The step's settings in <paramref name="doc"/>, or the defaults.</summary>
    public static CurrentFitSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is { } s ? CurrentFitSettings.Parse(s, $"Steps.{StepName}.Settings") : CurrentFitSettings.Default;

    /// <summary>The step's recorded result, or null when it has not been run.</summary>
    public static CurrentFitRecord? Record(CmcDocument doc) =>
        Step(doc)?["Result"] is { } r ? CurrentFitRecord.Parse(r, $"Steps.{StepName}.Result") : null;

    /// <summary>
    /// The last fit in one line (MC-15 R-mc15-1): <c>Id: ANN 2×8 · held-out 0.8 % · domain 412 pts</c> — the drain current's
    /// method, its held-out error (the training error when nothing was held out) and the points it saw. Null before a fit.
    /// </summary>
    public static string? Summary(CmcDocument doc)
    {
        if (Record(doc) is not { } r) return null;
        var hidden = Settings(doc).Id.Hidden;
        var parts = new List<string> { $"Id: {MethodText(r.Id.Method, hidden)}" };
        if (r.Errors.TryGetValue("Id", out var e))
        {
            parts.Add(e.HeldOutCount > 0 ? $"held-out {G(100 * e.HeldOutRelative)} %" : $"training {G(100 * e.TrainRelative)} %");
            parts.Add($"domain {e.TrainCount + e.HeldOutCount} pts");
        }
        string line = string.Join(" · ", parts);
        return UnderFit(r.Id.Method, hidden, e) is { } advice ? $"{line}. {advice}" : line;
    }

    /// <summary>The held-out error, relative to Id's range, above which a large ANN fit is said to under-fit (MC-30 D3).</summary>
    public const double UnderFitError = 0.02;

    /// <summary>The training points above which an under-fit is the network's size rather than the data's noise (MC-30 D3).
    /// The shipped examples hold 50 and 638, at 1.6 % and 1.1 %, so neither trips it.</summary>
    public const int UnderFitPoints = 2000;

    /// <summary>
    /// One sentence naming the next network size up, when an ANN Id's held-out error exceeds <see cref="UnderFitError"/> over
    /// more than <see cref="UnderFitPoints"/> training points: <c>2×8 under-fits 24,000 points: try 2×16, or a Table.</c>
    /// The defaults stay as they are — growing them would change every existing document's next fit.
    /// </summary>
    internal static string? UnderFit(string method, IReadOnlyList<int> hidden, FitErrorRecord? e)
    {
        if (method != Constituent.AnnMethod || hidden.Count == 0 || e is null
            || e.HeldOutCount == 0 || e.HeldOutRelative <= UnderFitError || e.TrainCount <= UnderFitPoints)
            return null;
        string Size(IEnumerable<int> h) { var l = h.ToList(); return l.All(w => w == l[0]) ? $"{l.Count}×{l[0]}" : string.Join("-", l); }
        return string.Create(CultureInfo.InvariantCulture,
            $"{Size(hidden)} under-fits {e.TrainCount:N0} points: try {Size(hidden.Select(w => 2 * w))}, or a Table.");
    }

    /// <summary>A fitted constituent's method as the summary line names it: an ANN with its layers (<c>ANN 2×8</c>, or each
    /// layer's width when they differ), anything else by name.</summary>
    internal static string MethodText(string method, IReadOnlyList<int> hidden) =>
        method != Constituent.AnnMethod || hidden.Count == 0 ? method
        : hidden.All(h => h == hidden[0]) ? $"ANN {hidden.Count}×{hidden[0]}"
        : $"ANN {string.Join("-", hidden)}";

    /// <summary>The block's keys checked and parsed — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Network", "Settings", "Result", CmcStamps.DataKey]);
        Settings(doc);
        Record(doc);
    }

    /// <summary>The document with <paramref name="settings"/> as the step's settings; a recorded result is kept.</summary>
    public static CmcDocument WithSettings(CmcDocument doc, CurrentFitSettings settings)
    {
        var steps = StepsCopy(doc);
        var o = steps[StepName] as JsonObject ?? new JsonObject();
        steps[StepName] = o;
        o["Settings"] = settings.ToJson();
        return doc with { Steps = steps };
    }

    /// <summary>The document with <paramref name="result"/> recorded, through the network it was computed with (R-mc7-5).</summary>
    public static CmcDocument Apply(CmcDocument doc, CurrentFitResult result)
    {
        var steps = StepsCopy(doc);
        steps[StepName] = new JsonObject
        {
            ["Network"] = NetworkFit.Digest(CmcExtrinsic.Network(doc)),
            ["Settings"] = result.Settings.ToJson(),
            ["Result"] = result.Record.ToJson(),
        };
        return doc with { Steps = steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    private static JsonObject StepsCopy(CmcDocument doc) =>
        doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();

    /// <summary>
    /// Reads the <c>.cmc</c>, fits with its settings, writes the arrays and the document — what the window and the
    /// <c>model</c> verb both call. Cancelled, nothing is written.
    /// </summary>
    public static CurrentFitResult FitFile(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var result = Run(doc, data, intrinsic, Settings(doc), rc);
        rc?.ThrowIfCancellationRequested();
        WriteArrays(cmcPath, CacheFolder, result.Record.Arrays, result.Arrays);
        CmcFile.Write(cmcPath, CmcStamps.Stamp(Apply(doc, result), StepName, CmcStamps.DataDigest(cmcPath, doc)));
        return result;
    }

    /// <summary>The recorded step and its arrays from the cache; null when there is no record, or its arrays are not in the
    /// cache (derived data: run the step again).</summary>
    public static CurrentFitResult? Load(string cmcPath, CmcDocument doc)
    {
        if (Record(doc) is not { } record) return null;
        return ReadArrays(cmcPath, CacheFolder, record.Arrays) is { } arrays ? new CurrentFitResult(Settings(doc), record, arrays) : null;
    }

    /// <summary>Whether the record's arrays are in the cache: a record whose arrays are gone is a fit that has to be run again,
    /// and the rail must not tick it. A file check only — reading it is <see cref="Load"/>'s.</summary>
    public static bool HasArrays(string cmcPath, CmcDocument doc) =>
        Record(doc) is { } record && ArraysPresent(cmcPath, CacheFolder, record.Arrays);

    internal static bool ArraysPresent(string cmcPath, string folder, string key) =>
        key.Length == 0 || File.Exists(Path.Combine(CmcCache.FolderFor(cmcPath), folder, key + ".npy"));

    /// <summary>A step's arrays from <c>&lt;name&gt;.cmc-cache/&lt;folder&gt;/&lt;key&gt;.npy</c>; empty for an empty key, null when the
    /// file is missing or unreadable (derived data: run the step again).</summary>
    internal static Dictionary<string, DataCube>? ReadArrays(string cmcPath, string folder, string key)
    {
        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        if (key.Length == 0) return arrays;
        string file = Path.Combine(CmcCache.FolderFor(cmcPath), folder, key + ".npy");
        if (!File.Exists(file)) return null;
        try
        {
            var ds = DataSetImporter.Import(file).DataSet;
            foreach (var (name, cube) in ds.Cubes) arrays[name] = cube;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
        { return null; }
        return arrays;
    }

    /// <summary>The content key of a result's arrays: what the record names and the cache file is called.</summary>
    internal static string ArraysKey(IReadOnlyDictionary<string, DataCube> arrays, out byte[] bytes)
    {
        var ds = new DataSet();
        foreach (var (name, cube) in arrays.OrderBy(kv => kv.Key, StringComparer.Ordinal)) ds.Add(name, cube);
        using var m = new MemoryStream();
        DataSetExporter.ExportNpy(ds, m);
        bytes = m.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];
    }

    /// <summary>A step's arrays written as one grouped <c>.npy</c> named by <paramref name="key"/>, by a temporary file and a
    /// rename; the folder's other entries are removed.</summary>
    internal static void WriteArrays(string cmcPath, string cacheFolder, string key, IReadOnlyDictionary<string, DataCube> arrays)
    {
        string folder = Path.Combine(CmcCache.FolderFor(cmcPath), cacheFolder);
        if (key.Length == 0) return;
        ArraysKey(arrays, out var bytes);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, key + ".npy");
        string tmp = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        foreach (string file in Directory.EnumerateFiles(folder, "*.npy"))
            if (Path.GetFileNameWithoutExtension(file) != key)
                try { File.Delete(file); } catch (IOException) { }
    }

    // ── The fit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fits the current constituents (R-mc7-1 … R-mc7-5). <paramref name="data"/> is the open document's normalised data
    /// and <paramref name="intrinsic"/> its intrinsic data (<see cref="IntrinsicExtraction"/>). Throws
    /// <see cref="ModelingException"/> for a refusal and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static CurrentFitResult Run(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                       IntrinsicResult intrinsic, CurrentFitSettings settings, RunControl? rc = null)
    {
        rc?.ThrowIfCancellationRequested();
        var report = new List<string>();
        if (TrapFit.Modelled(doc))
        {
            // R-mc10-5: the trap states carry the DC/RF difference; and they follow the forward device, which has no image.
            if (settings.Dispersion is not null)
                throw new ModelingException("Current", "dispersion and traps are not combined: with traps the DC/RF difference follows from the trap states.");
            if (settings.Symmetry == DrainSourceSymmetry.Exchange)
                throw new ModelingException("Current", "drain–source exchange is not defined for a model with traps.");
        }
        var heating = ThermalFit.Heating(doc);
        var iv = Gather(doc, data, intrinsic, settings, report, heating);
        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        var gx = new Dictionary<string, double>(StringComparer.Ordinal);
        var errors = new Dictionary<string, FitErrorRecord>(StringComparer.Ordinal);

        // Id (R-mc7-2): relative weighting with a floor.
        double idMax = iv.Id.Max(Math.Abs);
        double[] idW = [.. iv.Id.Select(y => Weight(y, settings.IdFloor * idMax))];
        AnnDerivativeData? rf = null;
        if (iv.SelfHeated && TrapFit.Settings(doc) is { Modelled: true, Shifted: true })
            // MC-19 R-mc19-3: under a shift coupling the RF/DC difference in gm and gds is what the trap step fits the strengths
            // to; trained into Id₀ it would be read as neither.
            report.Add("Id: trap-free under the shift coupling; the RF Gm and Gds fit the trap strengths, not Id.");
        else if (iv.SelfHeated || iv.Trapped)
        {
            if (settings.Id.Method != CurrentMethod.Equation) rf = RfDerivatives(doc, data, intrinsic, settings, heating, iv, report);
            else report.Add($"Id: an Equation trains on I–V values only; the RF Gm and Gds are not used.");
        }
        var idFit = FitPayloads("Id", settings.Id,
            [new("Id", iv.Id, idW, Own(0, 1))], iv, settings, report, rc, rf);
        var idConstituent = idFit.Constituent;

        // Ig: log-magnitude weighting, each branch on its own split target.
        Constituent igConstituent;
        PayloadFit? igFit = null;
        if (iv.Ig.Length == 0)
        {
            igConstituent = Constituent.GateEquations("0", "0", Inputs);
            report.Add("no gate current in the data; Ig is zero.");
        }
        else
        {
            // A table or network fits u = asinh(Ig/I0) unweighted — an equal error in u is an equal ratio in Ig above I0;
            // an equation, whose form already spans the decades, fits Ig itself weighted by 1/max(|Ig|, I0)².
            double igMax = Math.Max(iv.Ig.Max(Math.Abs), 1e-300);
            double floor = settings.IgFloor * igMax;
            bool transformed = settings.Ig.Method != CurrentMethod.Equation;
            double[] W(double[] y) => [.. y.Select(v => transformed ? 1.0 : Weight(v, floor))];
            igFit = FitPayloads("Ig", settings.Ig,
                [new("IgGs", iv.IgGs, W(iv.IgGs), Own(1, 0), transformed ? floor : null),
                 new("IgGd", iv.IgGd, W(iv.IgGd), Own(1, -1), transformed ? floor : null)],
                iv.ForGate, settings, report, rc);
            igConstituent = igFit.Constituent;
        }

        var edges = new List<string>();
        foreach (var f in igFit is null ? [idFit] : new[] { idFit, igFit })
            foreach (var p in f.Payloads)
            {
                foreach (var (k, v) in p.Arrays) arrays[k] = v;
                if (p.Gx is { } g) gx[p.Name] = g;
                if (p.Edges is { } e) edges.Add($"{p.Name} {e}");
                errors[p.Name] = p.Error;
            }

        // The report (R-mc7-5): the fitted Gm and Gds against the data's own DC derivatives.
        var conductance = Conductances(iv, idFit.Payloads[0].Eval, report);
        ValidationRecord? dcIv = null;
        if (iv.Trapped && iv.DcX.Length > 0)
        {
            // R-mc10-5: the DC I–V at its DC trap states, reported apart from the training error whether it trained or not.
            var f = idFit.Payloads[0].Eval;
            double sq = 0;
            for (int k = 0; k < iv.DcX.Length; k++) sq += Math.Pow(f(iv.DcX[k]).F - iv.DcId[k], 2);
            double rms = Math.Sqrt(sq / iv.DcX.Length), range = iv.DcId.Max() - iv.DcId.Min();
            dcIv = new ValidationRecord(iv.DcX.Length, rms, range > 0 ? rms / range : 0);
            bool held = TrapFit.Settings(doc).DcIv == DcIvUse.Validation;
            report.Add($"Id: DC I–V error {G(100 * dcIv.Relative)} % of its range over {dcIv.Count} point(s) ({(held ? "validation" : "trained on")}).");
        }
        SymmetryRecord? symmetry = settings.Symmetry == DrainSourceSymmetry.Exchange
            ? Jumps(iv, idFit.Payloads[0].Eval, igFit?.Payloads[0].Eval, igFit?.Payloads[1].Eval)
            : null;

        // DC/RF dispersion (R-mc7-4).
        Constituent? idRf = null;
        double? eq649 = null;
        (double, double)? reference = null;
        if (settings.Dispersion is { } disp)
        {
            var dsp = Dispersion(doc, intrinsic, settings, disp, iv, idFit.Payloads[0].Eval, report, rc);
            idRf = dsp.Constituent;
            foreach (var (k, v) in dsp.Payload.Arrays) arrays[k] = v;
            gx["IdRF"] = dsp.Payload.Gx!.Value;
            if (dsp.Payload.Edges is { } e) edges.Add($"IdRF {e}");
            errors["IdRF"] = dsp.Payload.Error;
            eq649 = dsp.Eq649;
            reference = dsp.Reference;
        }

        // R-mc29-4: the law, and where each current's data stops — a quiet edge is one the model continues flat.
        if (edges.Count > 0)
            report.Add($"Extrapolation (local, ten times the edge's own conductance, capped at G_x): {string.Join("; ", edges)}.");

        string key = arrays.Count > 0 ? ArraysKey(arrays, out _) : "";
        var record = new CurrentFitRecord
        {
            Id = idConstituent, Ig = igConstituent, IdRF = idRf, Gx = gx, Tau = settings.Dispersion?.Tau, Arrays = key,
            Errors = errors, Conductance = conductance, Symmetry = symmetry, Eq649 = eq649, Reference = reference,
            Traps = iv.Traps?.Block, DcIv = dcIv, Report = report,
        };
        return new CurrentFitResult(settings, record, arrays);
    }

    private static double Weight(double y, double floor)
    {
        double m = Math.Max(Math.Abs(y), floor);
        return m > 0 ? 1.0 / (m * m) : 1.0;
    }

    /// <summary>A current's own branch voltage, <c>a·vgs + b·vds</c> (MC-2's G_x term); padded with zeros over any further
    /// input by <see cref="OwnOver"/>.</summary>
    private static double[] Own(double vgs, double vds) => [vgs, vds];

    private static double[] OwnOver(double[] own, int inputs) => [.. own, .. new double[inputs - own.Length]];

    // ── Training points (R-mc7-1) ─────────────────────────────────────────────

    /// <summary>The intrinsic points one fit trains on, and the domain they span.</summary>
    internal sealed class IvPoints
    {
        public required double[][] X { get; init; }
        public required double[] Id { get; init; }
        public required double[] Ig { get; init; }
        public required double[] IgGs { get; init; }
        public required double[] IgGd { get; init; }
        /// <summary>The points that carry a gate current, by index into the gate arrays — the Ig fit's own points.</summary>
        public required IvPoints ForGate { get; init; }
        public required TrainingDomain Domain { get; init; }
        /// <summary>The lowest temperature in the data (the only one at one temperature), °C.</summary>
        public double TemperatureC { get; init; } = 25;
        /// <summary><c>vgs, vds</c>, and <c>T</c> at two or more temperatures — each row of <see cref="X"/> is one value of each.</summary>
        public IReadOnlyList<string> Inputs { get; init; } = EquationFit.VoltageInputs;
        /// <summary>Each point's ambient (chuck) temperature, °C.</summary>
        public double[] Ambient { get; init; } = [];
        /// <summary>Whether the DC points sit at their junction temperatures (self-heating modelled).</summary>
        public bool SelfHeated { get; init; }
        /// <summary>Each row of <see cref="X"/>'s data set and point index there — what the window's plots group the measured
        /// families by (MC-15). Empty for the gate's own points.</summary>
        public (string Set, int Point)[] Origin { get; init; } = [];

        /// <summary>Whether T is an input.</summary>
        public bool HasT { get; init; }

        /// <summary>The trap states the points were mapped to (MC-10), or null without traps. Then each row of
        /// <see cref="X"/> ends with the enabled states' values, and <see cref="Inputs"/> with their names.</summary>
        public TrapMapping? Traps { get; init; }

        public bool Trapped => Traps is not null;

        /// <summary>The DC points at their DC trap states (MC-10), whether they trained or were held out — what the DC I–V
        /// error is reported over; empty without traps.</summary>
        public double[][] DcX { get; init; } = [];
        public double[] DcId { get; init; } = [];

        /// <summary>A point of this fit's inputs at a bias: at <paramref name="t"/> when T is an input. Not with traps.</summary>
        public double[] At(double vgs, double vds, double t) => HasT ? [vgs, vds, t] : [vgs, vds];

        /// <summary>The middle of the temperature span, °C.</summary>
        public double MidT => Domain.Box.FirstOrDefault(b => b.Key == ComposedCoreProgram.T) is { Key: not null } b
            ? 0.5 * (b.Value.Lo + b.Value.Hi) : TemperatureC;
    }

    /// <summary>The I–V sets the current step trains on: those it names, or every DcIv, PulsedIv and SimulatedTables set.</summary>
    internal static List<CmcDataSet> ChosenIv(CmcDocument doc, CurrentFitSettings settings) =>
        settings.DataSets.Count > 0
            ? [.. settings.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                               ?? throw new ModelingException(n, "the current step names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv or DataSetKind.SimulatedTables).ToList();

    /// <summary>
    /// The training points. With traps modelled (MC-10 R-mc10-4) and <paramref name="withTraps"/>, every point is mapped to
    /// its trap state, which <c>Id</c> takes as further inputs: a pulsed point beyond its trap-setting bias under TrapState
    /// auto is left out of Id, and so are the DC points when the DC I–V is held out as validation. The gate current takes
    /// no trap input (D10), so it trains on every usable point.
    /// </summary>
    /// <param name="temperatures">The temperatures the fit saw, for a caller gathering fewer sets than it did — the window's
    /// plots gather one set at a time, and a set at one ambient would otherwise make <c>T</c> no input and leave every DC
    /// point at the ambient rather than at its junction temperature, so the model was drawn unheated against heated data.
    /// Null is the gathered sets' own.</param>
    internal static IvPoints Gather(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic,
                                    CurrentFitSettings settings, List<string> report, ThermalHeating? heating = null,
                                    bool withTraps = true, IReadOnlyList<double>? temperatures = null)
    {
        heating ??= ThermalHeating.None;
        var chosen = ChosenIv(doc, settings);
        if (chosen.Count == 0)
            throw new ModelingException("Current", "there is no I–V data set (DcIv, PulsedIv or SimulatedTables) to fit the currents to.");
        var trapSettings = withTraps ? TrapFit.Settings(doc) : TrapFitSettings.Default;
        if (trapSettings is { Modelled: true, Shifted: true })
        {
            // MC-19 R-mc19-5: under a shift coupling Id is the trap-free current at DC, so pulsed I–V beside DC trains nothing.
            trapSettings = TrapFitSettings.Default;
            var dc = chosen.Where(s => s.Kind is not (DataSetKind.PulsedIv or DataSetKind.PulsedSParams)).ToList();
            if (dc.Count > 0 && dc.Count < chosen.Count)
            {
                report.Add($"Id is trap-free under the shift coupling: {string.Join(", ", chosen.Except(dc).Select(s => s.Name))} left out.");
                chosen = dc;
            }
        }
        TrapMapping? traps = null;
        if (trapSettings.Modelled)
        {
            traps = TrapFit.Map(doc, data, intrinsic, trapSettings, chosen, "pulsed I–V");
            report.AddRange(traps.Report);
        }
        else
        {
            // No traps: one trap state per fit, and DC holds the states at their own equilibrium at every bias — the state
            // a trap-free model describes. Pulsed sets beside DC are left out, as the shift coupling leaves them, rather
            // than refused: choosing no traps is choosing the DC model.
            var dc = chosen.Where(s => s.Kind is not (DataSetKind.PulsedIv or DataSetKind.PulsedSParams)).ToList();
            if (dc.Count > 0 && dc.Count < chosen.Count)
            {
                report.Add($"No traps are modelled, so Id is trained on the DC data: {string.Join(", ", chosen.Except(dc).Select(s => s.Name))} left out.");
                chosen = dc;
            }
            TrapCondition(chosen, "Current");
        }
        var temps = temperatures ?? Temperatures(chosen, data);
        double t = temps.Count > 0 ? temps[0] : 25.0;
        bool hasT = temps.Count >= 2;
        bool selfHeating = hasT && heating.SelfHeating;
        if (selfHeating && heating.Rth is null && chosen.Any(s => s.Kind == DataSetKind.DcIv))
            throw new ModelingException("Current",
                "self-heating puts each DC point at its junction temperature, which needs Rth; run the thermal step or type Rth.");

        var x = new List<double[]>();
        var ambient = new List<double>();
        var id = new List<double>();
        var ig = new List<double?>();
        var phi = new List<double[]?>();     // a point's trap states; null when it does not train Id
        var dcX = new List<double[]>();
        var dcAt = new List<int>();          // where each DC point sits among the gathered points
        var origin = new List<(string Set, int Point)>();
        int noId = 0, reverse = 0;
        foreach (var set in chosen)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds))
                throw new ModelingException(set.Name, "it has no intrinsic data; de-embed it first.");
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, igs = ds["Ig"].RealValues,
                     ids = ds["Id"].RealValues, ex = ds["Excluded"].RealValues;
            var points = data.TryGetValue(set.Name, out var d) && d.Points.Count == vgs.Length ? d.Points : null;

            // What T means for this set (R-mc9-1): its chuck temperature; a DC point's junction temperature with
            // self-heating; a pulse from a dissipating quiescent bias starts Rth·Pq above the chuck once Rth is known.
            bool dc = set.Kind == DataSetKind.DcIv;
            double? pq = QuiescentPower(set);
            if (hasT && pq is > 0 && heating.Rth is null)
                report.Add($"{set.Name} pulses from a dissipating quiescent bias; its T is the chuck temperature until Rth is known.");
            for (int k = 0; k < vgs.Length; k++)
            {
                if (ex[k] != 0 || !double.IsFinite(vgs[k]) || !double.IsFinite(vds[k])) continue;
                if (double.IsNaN(ids[k])) { noId++; continue; }
                if (settings.Symmetry == DrainSourceSymmetry.Exchange && vds[k] < 0) { reverse++; continue; }
                double tAmb = AmbientOf(set, points?[k]);
                double tk = tAmb;
                if (hasT && dc && selfHeating)
                    tk = tAmb + heating.RthAt(tAmb) * Power(vgs[k], vds[k], double.IsNaN(igs[k]) ? 0 : igs[k], ids[k]);
                else if (hasT && pq is > 0 && heating.Rth is not null)
                    tk = tAmb + heating.RthAt(tAmb) * pq.Value;
                double[] xk = hasT ? [vgs[k], vds[k], tk] : [vgs[k], vds[k]];
                x.Add(xk);
                origin.Add((set.Name, k));
                ambient.Add(tAmb);
                id.Add(ids[k]);
                ig.Add(double.IsNaN(igs[k]) ? null : igs[k]);
                if (traps is null) continue;
                var state = traps.At(set.Name, k);
                bool dcPoint = set.Kind is not (DataSetKind.PulsedIv or DataSetKind.PulsedSParams);
                if (dcPoint && state is not null) { dcX.Add([.. xk, .. state]); dcAt.Add(x.Count - 1); }
                phi.Add(state is null || dcPoint && trapSettings.DcIv == DcIvUse.Validation ? null : [.. xk, .. state]);
            }
        }
        if (hasT && !selfHeating && chosen.Any(s => s.Kind == DataSetKind.DcIv))
            report.Add("DC points are at their ambient temperature: their self-heating is embedded statically.");
        if (noId > 0) report.Add($"{noId} point(s) without a drain current left out.");
        if (reverse > 0) report.Add($"{reverse} point(s) at vds < 0 left out: with Exchange the fit reads vds ≥ 0.");
        if (x.Count < 4) throw new ModelingException("Current", $"{x.Count} usable I–V point(s); a current fit needs at least 4.");

        // The gate split, then the channel current: the drain terminal carries Id − IgGd.
        int n = x.Count;
        var gs = new double[n];
        var gd = new double[n];
        for (int k = 0; k < n; k++)
        {
            double g = ig[k] ?? 0.0;
            gd[k] = GateDrainBranch(g, x[k][1], settings.GateSplit);
            gs[k] = g - gd[k];
        }
        double[] channel = [.. Enumerable.Range(0, n).Select(k => id[k] + gd[k])];

        var gateIdx = Enumerable.Range(0, n).Where(k => ig[k] is not null).ToArray();
        if (gateIdx.Length > 0 && gateIdx.Length < n) report.Add($"{n - gateIdx.Length} point(s) without a gate current left out of Ig.");
        var domain = Hull(x);
        if (hasT) domain = domain with { Box = [new(ComposedCoreProgram.T, (x.Min(p => p[2]), x.Max(p => p[2])))] };
        var inputs = hasT ? InputsWithT : Inputs;
        var gate = new IvPoints
        {
            X = [.. gateIdx.Select(k => x[k])], Id = [.. gateIdx.Select(k => channel[k])], Ig = [.. gateIdx.Select(k => ig[k]!.Value)],
            IgGs = [.. gateIdx.Select(k => gs[k])], IgGd = [.. gateIdx.Select(k => gd[k])],
            ForGate = null!, Domain = domain, TemperatureC = t, Inputs = inputs, HasT = hasT,
            Ambient = [.. gateIdx.Select(k => ambient[k])], SelfHeated = selfHeating,
        };
        if (traps is null)
            return new IvPoints
            {
                X = [.. x], Id = channel, Ig = gate.Ig, IgGs = gate.IgGs, IgGd = gate.IgGd, ForGate = gate,
                Domain = domain, TemperatureC = t, Inputs = inputs, HasT = hasT, Ambient = [.. ambient], SelfHeated = selfHeating,
                Origin = [.. origin],
            };

        // With traps the drain current trains on the points that have a trap state, each with the states appended; the DC
        // points' targets are their channel currents, in the order they were gathered.
        var train = Enumerable.Range(0, n).Where(k => phi[k] is not null).ToArray();
        if (train.Length < 4) throw new ModelingException("Current", $"{train.Length} I–V point(s) with a trap state; a current fit needs at least 4.");
        var xTrap = train.Select(k => phi[k]!).ToArray();
        int first = inputs.Count;
        var box = domain.Box.ToList();
        for (int j = 0; j < traps.States.Count; j++)
        {
            var column = xTrap.Select(p => p[first + j]).Concat(dcX.Select(p => p[first + j])).ToArray();
            double lo = column.Min(), hi = column.Max();
            if (!(hi > lo)) (lo, hi) = (lo - 0.5, hi + 0.5);
            box.Add(new(traps.States[j], (lo, hi)));
        }
        return new IvPoints
        {
            X = xTrap, Id = [.. train.Select(k => channel[k])], Ig = gate.Ig, IgGs = gate.IgGs, IgGd = gate.IgGd, ForGate = gate,
            Domain = Hull(xTrap) with { Box = box }, TemperatureC = t, Inputs = [.. inputs, .. traps.States], HasT = hasT,
            Ambient = [.. train.Select(k => ambient[k])], SelfHeated = selfHeating, Traps = traps, DcX = [.. dcX], DcId = [.. dcAt.Select(k => channel[k])],
            Origin = [.. train.Select(k => origin[k])],
        };
    }

    /// <summary>The share of a gate current <paramref name="ig"/> the gate–drain branch carries, by the split rule.</summary>
    internal static double GateDrainBranch(double ig, double vds, GateSplit split) => split switch
    {
        GateSplit.GateSource => 0.0,
        GateSplit.GateDrain  => ig,
        _ => (ig >= 0) == (vds >= 0) ? 0.0 : ig,
    };

    /// <summary>
    /// The isothermal channel current of the named pulsed sets (MC-9 R-mc9-3): Id over (vgs, vds, T), each point at its
    /// chuck temperature, by <paramref name="method"/> and the current step's other settings — what the Rth search inverts.
    /// </summary>
    internal static Func<double[], (double F, double Dg, double Dd)> IsothermalId(
        CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data, IntrinsicResult intrinsic, IReadOnlyList<string> sets,
        CurrentConstituentSettings method, List<string> report, RunControl? rc)
    {
        var settings = Settings(doc) with { DataSets = sets, Id = method, Dispersion = null };
        var own = new List<string>();
        var iv = Gather(doc, data, intrinsic, settings, own, ThermalHeating.None, withTraps: false);
        if (!iv.HasT) throw new ModelingException(ThermalFit.StepName, "the isothermal Id needs pulsed I–V at two or more chuck temperatures.");
        double idMax = iv.Id.Max(Math.Abs);
        double[] w = [.. iv.Id.Select(y => Weight(y, settings.IdFloor * idMax))];
        var fit = FitPayloads("Id", method, [new("Id", iv.Id, w, Own(0, 1))], iv, settings, own, rc);
        var e = fit.Payloads[0].Error;
        report.Add($"isothermal Id: held-out error {G(100 * e.HeldOutRelative)} % of its range over {e.HeldOutCount} point(s).");
        return fit.Payloads[0].Eval;
    }

    /// <summary>The intrinsic power a bias dissipates, in its terminal form <c>ig·vgs + id·vds</c> ([P1] eq. 6.67) — what
    /// the core injects into its thermal node.</summary>
    internal static double Power(double vgs, double vds, double ig, double id) => ig * vgs + id * vds;

    /// <summary>A point's ambient temperature: its own, else its set's, else 25 °C.</summary>
    internal static double AmbientOf(CmcDataSet set, BiasPoint? point) =>
        point is { AmbientC: var a } && double.IsFinite(a) ? a : set.Conditions.AmbientC ?? 25.0;

    /// <summary>The power a pulsed set's quiescent bias dissipates, when its currents are stated; null otherwise.</summary>
    internal static double? QuiescentPower(CmcDataSet set) =>
        set.Kind is DataSetKind.PulsedIv or DataSetKind.PulsedSParams && set.Conditions.Quiescent is { Id: { } id } q
            ? id * q.Vds + (q.Ig ?? 0) * q.Vgs
            : null;

    /// <summary>
    /// One temperature and one quiescent point across the chosen sets, or a refusal naming the condition that differs.
    /// Returns the temperature, °C (25 when none is stated).
    /// </summary>
    internal static double OneCondition(IReadOnlyList<CmcDataSet> sets, IReadOnlyDictionary<string, NormalisedData> data, string step)
    {
        var temps = Temperatures(sets, data);
        if (temps.Count > 1)
        {
            var at = temps.Select(t => sets.First(s => SetTemperatures(s, data).Any(x => Math.Abs(x - t) <= TemperatureTolerance)).Name).ToList();
            throw new ModelingException(step,
                $"the data differs in AmbientC ({G(temps[0])} °C in {at[0]}, {G(temps[^1])} °C in {at[^1]}); one temperature per fit here.");
        }
        TrapCondition(sets, step);
        return temps.Count > 0 ? temps[0] : 25.0;
    }

    /// <summary>Two temperatures within this are one, °C.</summary>
    internal const double TemperatureTolerance = 0.01;

    /// <summary>The distinct ambient temperatures of the sets' points (each set's own, and any a point states), ascending.</summary>
    internal static IReadOnlyList<double> Temperatures(IEnumerable<CmcDataSet> sets, IReadOnlyDictionary<string, NormalisedData> data)
    {
        var distinct = new List<double>();
        foreach (double t in sets.SelectMany(s => SetTemperatures(s, data)).Order())
            if (distinct.Count == 0 || t - distinct[^1] > TemperatureTolerance) distinct.Add(t);
        return distinct;
    }

    private static IEnumerable<double> SetTemperatures(CmcDataSet s, IReadOnlyDictionary<string, NormalisedData> data)
    {
        if (s.Conditions.AmbientC is { } a) yield return a;
        if (data.TryGetValue(s.Name, out var d))
            foreach (var p in d.Points) if (!p.Excluded && double.IsFinite(p.AmbientC)) yield return p.AmbientC;
    }

    /// <summary>One trap state across the chosen sets — not pulsed and pulsed together, nor two quiescent points or
    /// pre-pulses — or a refusal naming the condition that differs (MC-10 handles the mixes).</summary>
    internal static void TrapCondition(IReadOnlyList<CmcDataSet> sets, string step)
    {
        var pulsed = sets.Where(s => s.Kind is DataSetKind.PulsedIv or DataSetKind.PulsedSParams).ToList();
        if (pulsed.Count > 0 && pulsed.Count < sets.Count)
            throw new ModelingException(step,
                $"the data differs in Quiescent ({sets.First(s => !pulsed.Contains(s)).Name} is not pulsed, {pulsed[0].Name} is); " +
                "one trap state per fit until traps are modelled.");
        for (int k = 1; k < pulsed.Count; k++)
        {
            var a = pulsed[0].Conditions; var b = pulsed[k].Conditions;
            bool same = (a.Quiescent, b.Quiescent) switch
            {
                (null, null) => true,
                ({ } qa, { } qb) => Math.Abs(qa.Vgs - qb.Vgs) <= 1e-3 && Math.Abs(qa.Vds - qb.Vds) <= 1e-3,
                _ => false,
            };
            if (!same)
                throw new ModelingException(step,
                    $"the data differs in Quiescent ({pulsed[0].Name} and {pulsed[k].Name}); one quiescent point per fit until traps are modelled.");
            if ((a.PrePulse is null) != (b.PrePulse is null)
                || a.PrePulse is { } pa && b.PrePulse is { } pb && (Math.Abs(pa.Vgs - pb.Vgs) > 1e-3 || Math.Abs(pa.Vds - pb.Vds) > 1e-3))
                throw new ModelingException(step,
                    $"the data differs in PrePulse ({pulsed[0].Name} and {pulsed[k].Name}); one trap state per fit until traps are modelled.");
        }
    }

    /// <summary>The convex hull of the points, as a training domain (R-mc7-3); refused when it encloses no area.</summary>
    internal static TrainingDomain Hull(IReadOnlyList<double[]> x)
    {
        var hull = DataHygiene.Coverage([new NormalisedData { Points = [.. x.Select((p, k) => new BiasPoint($"{k}", p[0], p[1], 0, 0))] }]);
        double area = 0;
        for (int k = 0; k < hull.Count; k++)
            area += hull[k].Vgs * hull[(k + 1) % hull.Count].Vds - hull[(k + 1) % hull.Count].Vgs * hull[k].Vds;
        double gSpan = x.Max(p => p[0]) - x.Min(p => p[0]), dSpan = x.Max(p => p[1]) - x.Min(p => p[1]);
        if (hull.Count < 3 || !(Math.Abs(area) > 1e-9 * gSpan * dSpan) || gSpan <= 0 || dSpan <= 0)
            throw new ModelingException("Current", "the points span no area in (vgs, vds); a fit needs both voltages to vary.");
        return new TrainingDomain(hull, []);
    }

    // ── The methods (R-mc7-2) ─────────────────────────────────────────────────

    /// <summary>One payload's targets: a name (Id, IgGs, IgGd), a target and a weight per point, its own branch, and the
    /// scale of an <see cref="OutputTransform.Asinh"/> output when the payload is fitted in <c>u = asinh(Y/scale)</c>.</summary>
    private sealed record Target(string Name, double[] Y, double[] W, double[] Own, double? Transform = null)
    {
        public double[] U => Transform is { } s ? [.. Y.Select(y => Math.Asinh(y / s))] : Y;

        public RuntimeEvaluator Output(RuntimeEvaluator u) => Transform is { } s ? new AsinhOutput(u, s) : u;
    }

    /// <summary>A fitted payload: its function of a point of the fit's inputs (value and gradient by vgs and vds, extrapolated
    /// as the runtime will), arrays, G_x, error.</summary>
    private sealed record Payload(string Name, Func<double[], (double F, double Dg, double Dd)> Eval,
                                  IReadOnlyList<KeyValuePair<string, DataCube>> Arrays, double? Gx, FitErrorRecord Error,
                                  string? Edges = null);

    private sealed record PayloadFit(Constituent Constituent, IReadOnlyList<Payload> Payloads);

    /// <summary>One constituent's payloads by its method. <paramref name="rf"/> are derivative targets an ANN trains on beside
    /// the values (MC-6's combined mode) — the RF Gm/Gds of a self-heating Id.</summary>
    private static PayloadFit FitPayloads(string constituent, CurrentConstituentSettings s, IReadOnlyList<Target> targets,
                                          IvPoints iv, CurrentFitSettings settings, List<string> report, RunControl? rc,
                                          AnnDerivativeData? rf = null)
    {
        if (s.Method == CurrentMethod.Equation) return FitEquation(constituent, s, targets, iv, settings, report, rc);

        var payloads = new List<Payload>();
        foreach (var t in targets)
        {
            rc?.ThrowIfCancellationRequested();
            payloads.Add(s.Method == CurrentMethod.ANN ? FitAnn(t, s, iv, settings, report, rc, rf) : FitTable(t, s, iv, settings, report, rc, rf));
        }
        string method = s.Method == CurrentMethod.ANN ? Constituent.AnnMethod : Constituent.TableMethod;
        Constituent c = targets.Count == 1
            ? s.Method == CurrentMethod.ANN ? Constituent.Ann(targets[0].Name, iv.Inputs, iv.Domain, s.Activation)
                                            : Constituent.Table(targets[0].Name, iv.Inputs, iv.Domain, s.Smoothing)
            : Constituent.Gate(method, targets[0].Name, targets[1].Name, iv.Inputs, iv.Domain) with
              {
                  Smoothing = s.Method == CurrentMethod.Table ? s.Smoothing : null,
                  Activation = s.Method == CurrentMethod.ANN ? s.Activation : null,
              };
        if (targets[0].Transform is { } scale) c = c with { Transform = new OutputTransform(OutputTransform.Asinh, scale) };
        return new PayloadFit(c, payloads);
    }

    private static AnnTrainingSettings AnnSettings(IReadOnlyList<int> hidden, AnnActivation activation, int iterations,
                                                   CurrentFitSettings s, double heldOut) =>
        new() { Hidden = hidden, Activation = activation, MaxIterations = iterations, Restarts = s.Restarts, Seed = s.Seed,
                HeldOutFraction = heldOut };

    private static Payload FitAnn(Target t, CurrentConstituentSettings s, IvPoints iv, CurrentFitSettings settings,
                                  List<string> report, RunControl? rc, AnnDerivativeData? rf = null)
    {
        var (held, trainX, trainU, trainW) = Training(t, iv, settings);
        rc?.BeginStage($"Training {t.Name}");
        var trained = AnnTrainer.Train(new AnnTrainingProblem(iv.Inputs.Count)
                                       {
                                           Values = new AnnValueData(trainX, trainU, trainW),
                                           Derivatives = t.Transform is null ? rf : null,
                                       },
                                       AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);
        foreach (string w in trained.Warnings) report.Add($"{t.Name}: {w}");
        if (rf is not null && trained.Derivatives is { } e)
            report.Add($"{t.Name}: RF Gm/Gds at the frozen {(iv.SelfHeated && iv.Trapped ? "junction temperature and trap states" : iv.SelfHeated ? "junction temperature" : "trap states")}, RMS error {G(100 * e.TrainRelative)} % of their range.");
        var f = t.Output(trained.Network);
        var own = OwnOver(t.Own, iv.Inputs.Count);
        double gx = Gx(f, iv.X, own, t.Name, iv.Inputs);
        var eval = Guided(f, iv.Domain, iv.Inputs, gx, own);
        return new Payload(t.Name, eval, [.. trained.Arrays(t.Name)], gx, Error(eval, iv.X, t.Y, held), Edges(f, iv.X, own, gx));
    }

    /// <summary>The held-out split, and the training points' coordinates, targets (in the payload's own domain) and weights.
    /// Every method holds out the same points, so the errors compare.</summary>
    private static (bool[] Held, double[][] X, double[] U, double[] W) Training(Target t, IvPoints iv, CurrentFitSettings settings)
    {
        var held = Split(iv.X, t.Y, settings);
        var u = t.U;
        return (held, [.. iv.X.Where((_, k) => !held[k])], [.. u.Where((_, k) => !held[k])], [.. t.W.Where((_, k) => !held[k])]);
    }

    private static Payload FitTable(Target t, CurrentConstituentSettings s, IvPoints iv, CurrentFitSettings settings,
                                    List<string> report, RunControl? rc, AnnDerivativeData? rf = null)
    {
        // Points already on a complete grid are the table (MC-30 D1): no re-grid, no Regrid setting consulted. A grid with
        // holes falls through to the re-grid, and says which sites broke it.
        if (DataGrid.Detect(iv.X) is { } grid)
        {
            if (grid.Complete) return FitTableOnGrid(t, s, iv, grid, settings, report);
            report.Add(grid.HolesSentence(t.Name, iv.Inputs));
        }

        var (held, trainX, trainU, trainW) = Training(t, iv, settings);

        // The function the grid is sampled from, in the payload's own domain: a network trained on the training points
        // (held-out ones never seen), or a radial-basis interpolant through the points. The grid covers the hull's
        // bounding box, so some sites lie outside the hull: there the network is continued LINEARLY (C¹, no G_x term).
        // The G_x term grows with the square of the distance — amps a few volts out — and a spline through it rings
        // back into the cells inside the hull (measured: 7.7 % held-out error, against 1.5 % for the network itself);
        // the runtime adds G_x outside the domain on its own.
        // With T an input (MC-9) the grid gains a temperature axis, and with traps (MC-10) an axis per trap state, spline in
        // each; the radial-basis interpolant is two-dimensional, so such a table re-grids through a network — trained, for a
        // self-heating or trapped Id, on the RF conductances too (MC-6's combined mode).
        int d = iv.Inputs.Count;
        Func<double[], double> source;
        if (s.Regrid == RegridSource.ANN)
        {
            rc?.BeginStage($"Training {t.Name} for its table");
            var trained = AnnTrainer.Train(new AnnTrainingProblem(d)
                                           {
                                               Values = new AnnValueData(trainX, trainU, trainW),
                                               Derivatives = t.Transform is null ? rf : null,
                                           },
                                           AnnSettings(s.Hidden, s.Activation, s.MaxIterations, settings, 0.0), rc);
            var linear = new GuidedExtrapolation(trained.Network, iv.Domain, iv.Inputs, 0.0, null);
            source = p =>
            {
                Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
                return linear.Evaluate(p, g[..p.Length]);
            };
        }
        else if (d > 2)
            throw new ModelingException(t.Name, "a table with T or a trap state as an input re-grids through an ANN; the RBF interpolant is two-dimensional.");
        else
        {
            rc?.BeginStage($"Building the RBF interpolant for {t.Name}");
            var rbf = Regrid.RbfWithinLimit([.. trainX.Select(p => (p[0], p[1]))], trainU, rc);
            source = p => rbf(p[0], p[1]);
        }

        rc?.BeginStage($"Re-gridding {t.Name}");
        var poly = iv.Domain.Polygon;
        var siteList = new List<double[]>
        {
            Regrid.Axis(poly.Min(p => p.Vgs), poly.Max(p => p.Vgs), s.GridStep),
            Regrid.Axis(poly.Min(p => p.Vds), poly.Max(p => p.Vds), s.GridStep),
        };
        foreach (string input in iv.Inputs.Skip(2))
        {
            var (lo, hi) = iv.Domain.Box.First(b => b.Key == input).Value;
            siteList.Add(Regrid.Axis(lo, hi, input == ComposedCoreProgram.T ? s.TemperatureStep : s.TrapStep));
        }
        IReadOnlyList<double[]> sites = siteList;
        double[] values = Grid(source, sites);
        var coefficients = TensorSplineFit.Fit(sites, values, s.Smoothing);
        var arrays = ComposedArrays.Table(t.Name, iv.Inputs, sites, values, coefficients).ToList();

        if (s.GridStep / 2 > settings.SmallestSwing)
            report.Add($"{t.Name}: table step {G(s.GridStep)} V: low-level IM below ~{G(s.GridStep / 2)} V swing unreliable.");
        var f = t.Output(new TableEvaluator(sites, coefficients));
        var own = OwnOver(t.Own, d);
        double gx = Gx(f, iv.X, own, t.Name, iv.Inputs);
        var eval = Guided(f, iv.Domain, iv.Inputs, gx, own);
        return new Payload(t.Name, eval, arrays, gx, Error(eval, iv.X, t.Y, held), Edges(f, iv.X, own, gx));
    }

    /// <summary>
    /// A table on the data's own grid (MC-30 D1): the samples themselves, splined, with whole grid lines along vgs held out so
    /// the training points stay a grid and the held-out error still measures what the table does between lines.
    /// </summary>
    private static Payload FitTableOnGrid(Target t, CurrentConstituentSettings s, IvPoints iv, DataGrid grid,
                                          CurrentFitSettings settings, List<string> report)
    {
        var held = grid.HeldOutLines(settings.HeldOutFraction, out var heldLine);
        // The first axis without its held-out lines, and each kept line's new index.
        var keep = Enumerable.Range(0, heldLine.Length).Where(i => !heldLine[i]).ToArray();
        var remap = new int[heldLine.Length];
        for (int k = 0; k < keep.Length; k++) remap[keep[k]] = k;
        var sites = grid.Sites.ToArray();
        sites[0] = [.. keep.Select(i => grid.Sites[0][i])];

        var u = t.U;
        var values = new double[sites.Aggregate(1, (a, x) => a * x.Length)];
        for (int k = 0; k < iv.X.Length; k++)
        {
            if (held[k]) continue;
            int f = remap[grid.Index[k][0]];
            for (int a = 1; a < sites.Length; a++) f = f * sites[a].Length + grid.Index[k][a];
            values[f] = u[k];
        }
        var coefficients = TensorSplineFit.Fit(sites, values, s.Smoothing);
        var arrays = ComposedArrays.Table(t.Name, iv.Inputs, sites, values, coefficients).ToList();

        int heldCount = heldLine.Count(h => h);
        report.Add($"{t.Name}: Table on the data's own {grid.Shape} grid" +
                   (heldCount > 0 ? $", {heldCount} {iv.Inputs[0]} line(s) held out." : "."));
        double step = Enumerable.Range(0, Math.Min(2, sites.Length))
                                .Max(a => sites[a].Zip(sites[a].Skip(1), (p, q) => q - p).Max());
        if (step / 2 > settings.SmallestSwing)
            report.Add($"{t.Name}: table step {G(step)} V: low-level IM below ~{G(step / 2)} V swing unreliable.");

        var f2 = t.Output(new TableEvaluator(sites, coefficients));
        var own = OwnOver(t.Own, iv.Inputs.Count);
        double gx = Gx(f2, iv.X, own, t.Name, iv.Inputs);
        var eval = Guided(f2, iv.Domain, iv.Inputs, gx, own);
        return new Payload(t.Name, eval, arrays, gx, Error(eval, iv.X, t.Y, held), Edges(f2, iv.X, own, gx));
    }

    /// <summary><paramref name="f"/> at every site of the grid, row-major, the last axis fastest.</summary>
    private static double[] Grid(Func<double[], double> f, IReadOnlyList<double[]> sites)
    {
        int total = sites.Aggregate(1, (a, x) => a * x.Length);
        var values = new double[total];
        var p = new double[sites.Count];
        for (int flat = 0; flat < total; flat++)
        {
            int rest = flat;
            for (int k = sites.Count - 1; k >= 0; k--)
            {
                p[k] = sites[k][rest % sites[k].Length];
                rest /= sites[k].Length;
            }
            values[flat] = f(p);
        }
        return values;
    }

    private static PayloadFit FitEquation(string constituent, CurrentConstituentSettings s, IReadOnlyList<Target> targets,
                                          IvPoints iv, CurrentFitSettings settings, List<string> report, RunControl? rc)
    {
        bool gate = targets.Count == 2;
        string[] expressions;
        IReadOnlyList<TemplateParameter> parameters;
        if (s.Template is { } name)
        {
            var tpl = CurrentTemplates.Find(name);
            if (tpl is null || tpl.IsGate != gate)
                throw new ModelingException(constituent,
                    $"'{name}' is not a template here; the templates are {string.Join(", ", CurrentTemplates.Names(gate))}.");
            expressions = gate ? [tpl.IgGs!, tpl.IgGd!] : [tpl.Id!];
            parameters = tpl.Parameters(new TemplateData([.. iv.X.Select(p => p[0])], [.. iv.X.Select(p => p[1])],
                                                         gate ? iv.Ig : iv.Id, iv.TemperatureC));
            var coefficients = s.TemperatureCoefficients ?? (iv.HasT && tpl.Scale is { } scale ? [scale] : []);
            if (coefficients.Count > 0)
            {
                (expressions, parameters) = WithTemperatureCoefficients(constituent, expressions, parameters, coefficients, iv);
                if (s.TemperatureCoefficients is null)
                    report.Add($"{constituent}: {tpl.Scale} is linear in T, the {name} template's default for data at more than one temperature.");
            }
            if (!gate && iv.Traps is { } traps)
                (expressions, parameters) = WithTrapTerms(name, expressions[0], parameters, traps);
            if (gate)    // the two branches share one list, so each branch's own parameters are named for it
            {
                expressions = [Rename(expressions[0], "Gs", parameters), Rename(expressions[1], "Gd", parameters)];
                parameters = [.. parameters.Where(p => !p.Lock).SelectMany(p => new[] { p with { Name = p.Name + "Gs" }, p with { Name = p.Name + "Gd" } }),
                              .. parameters.Where(p => p.Lock)];
            }
        }
        else
        {
            expressions = gate
                ? [s.IgGs ?? throw new ModelingException(constituent, "an Ig equation names a template or both IgGs and IgGd."),
                   s.IgGd ?? throw new ModelingException(constituent, "an Ig equation names a template or both IgGs and IgGd.")]
                : [s.Expression ?? throw new ModelingException(constituent, "an Id equation names a template or an Expression.")];
            parameters = [];
            if (s.TemperatureCoefficients is { Count: > 0 })
                throw new ModelingException(constituent, "temperature coefficients apply to a template; write T into your own expression.");
        }
        parameters = Overrides(constituent, parameters, s.Parameters);

        var held = Split(iv.X, targets[0].Y, settings);
        var terms = targets.Select((t, i) => new EquationTerm(expressions[i],
            [.. iv.X.Where((_, k) => !held[k])], [.. t.Y.Where((_, k) => !held[k])], [.. t.W.Where((_, k) => !held[k])])).ToList();
        rc?.BeginStage($"Fitting {constituent}", 0, "evaluation(s)");
        var fit = EquationFit.Fit(terms, parameters, s.Algorithm, settings.Seed, constituent, rc, iv.Inputs);

        var payloads = targets.Select((t, i) =>
        {
            var f = EquationFit.Function(expressions[i], fit.Values, constituent, iv.Inputs);
            return new Payload(t.Name, f, [], null, Error(f, iv.X, t.Y, held));
        }).ToList();
        var pairs = fit.Values.Select(kv => (kv.Key, kv.Value)).ToArray();
        var c = gate ? Constituent.GateEquations(expressions[0], expressions[1], iv.Inputs, pairs)
                     : Constituent.Equation(expressions[0], iv.Inputs, pairs);
        return new PayloadFit(c, payloads);
    }

    /// <summary>The name of the reference temperature of a template's temperature coefficients.</summary>
    internal const string Tnom = "Tnom";

    /// <summary>
    /// A template's named parameters made linear in temperature (R-mc9-1): each <c>p</c> becomes
    /// <c>(p + p_T·(T − Tnom))</c>, <c>p_T</c> a free parameter starting at 0 and bounded by the parameter's own range
    /// over the data's temperature span, <c>Tnom</c> the data's lowest temperature, locked.
    /// </summary>
    private static (string[] Expressions, IReadOnlyList<TemplateParameter> Parameters) WithTemperatureCoefficients(
        string constituent, string[] expressions, IReadOnlyList<TemplateParameter> parameters, IReadOnlyList<string> names, IvPoints iv)
    {
        if (!iv.HasT)
            throw new ModelingException(constituent, "the data is at one temperature, so there is no temperature coefficient to fit.");
        var (lo, hi) = iv.Domain.Box.First(b => b.Key == ComposedCoreProgram.T).Value;
        double span = Math.Max(hi - lo, 1.0);
        var list = parameters.ToList();
        foreach (string name in names)
        {
            var p = list.FirstOrDefault(x => x.Name == name)
                    ?? throw new ModelingException(constituent, $"'{name}' is not a parameter of the template; its parameters are {string.Join(", ", parameters.Select(x => x.Name))}.");
            double reach = (p.Max - p.Min) / span;
            if (!(reach > 0) || !double.IsFinite(reach)) reach = Math.Max(Math.Abs(p.Start), 1e-12) / span;
            list.Add(new TemplateParameter(name + "_T", 0.0, -reach, reach));
            expressions = [.. expressions.Select(e => System.Text.RegularExpressions.Regex.Replace(
                e, $@"\b{name}\b", $"({name}+{name}_T*(T-{Tnom}))"))];
        }
        list.Add(new TemplateParameter(Tnom, iv.TemperatureC, iv.TemperatureC, iv.TemperatureC, Lock: true));
        return (expressions, list);
    }

    /// <summary>
    /// A drain-current template with the trap terms of R-mc10-5, each zero at DC by construction (<c>phi = v</c>) and each
    /// saturating, as [G19] eqs. 8, A.1, A.8 and [L26] eq. 33 find them: a threshold shift per enabled state, in the
    /// direction its capture moves it — <c>vgs → vgs − a1·tanh((vgs − phi1)/c1) − a2·tanh((phi2 − vds)/c2)</c> for
    /// gate-lag and drain-lag ([P21]'s back-gate view), and likewise <c>a3</c>, <c>a4</c> for a <c>Custom</c> model's
    /// further states — and, on the first drain-like state, a knee term on the template's knee parameter,
    /// <c>α → α·(1 + ak·tanh((phi2 − vds)/ck))</c>. The amplitudes start at zero — the trap-free fit — and the widths at 5 V.
    /// </summary>
    private static (string[] Expressions, IReadOnlyList<TemplateParameter> Parameters) WithTrapTerms(
        string template, string expression, IReadOnlyList<TemplateParameter> parameters, TrapMapping traps)
    {
        var list = parameters.ToList();
        string shift = "vgs";
        string? drainLike = null;
        for (int k = 0; k < traps.States.Count; k++)
        {
            string state = traps.States[k];
            string n = state.StartsWith("phi", StringComparison.Ordinal) ? state[3..] : (k + 1).ToString(CultureInfo.InvariantCulture);
            bool gateLike = traps.Dynamics.CaptureSign(k) < 0;
            shift += gateLike ? $"-a{n}*tanh((vgs-{state})/c{n})" : $"-a{n}*tanh(({state}-vds)/c{n})";
            list.Add(new($"a{n}", 0, -2, 2));
            list.Add(new($"c{n}", 5, 0.05, 50));
            if (!gateLike) drainLike ??= state;
        }
        expression = System.Text.RegularExpressions.Regex.Replace(expression, @"\bvgs\b", $"({shift})");
        string knee = template == CurrentTemplates.CurticeCubic ? "Gamma" : "Alpha";
        if (drainLike is not null && list.Any(p => p.Name == knee))
        {
            expression = System.Text.RegularExpressions.Regex.Replace(expression, $@"\b{knee}\b", $"({knee}*(1+ak*tanh(({drainLike}-vds)/ck)))");
            list.Add(new("ak", 0, -0.9, 0.9));
            list.Add(new("ck", 5, 0.05, 50));
        }
        return ([expression], list);
    }

    /// <summary>A template's free parameters renamed with a branch suffix in its expression.</summary>
    private static string Rename(string expression, string suffix, IReadOnlyList<TemplateParameter> parameters)
    {
        foreach (var p in parameters.Where(p => !p.Lock))
            expression = System.Text.RegularExpressions.Regex.Replace(expression, $@"\b{p.Name}\b", p.Name + suffix);
        return expression;
    }

    /// <summary>The document's per-parameter settings over the template's: a start, bounds, a lock. A parameter the template
    /// does not have is the user's own and must state all three numbers.</summary>
    internal static IReadOnlyList<TemplateParameter> Overrides(string constituent, IReadOnlyList<TemplateParameter> template,
                                                              IReadOnlyDictionary<string, EquationParameterSettings> overrides)
    {
        var list = template.ToList();
        foreach (var (name, o) in overrides)
        {
            int i = list.FindIndex(p => p.Name == name);
            if (i < 0)
            {
                if (o.Value is not { } v || (!o.Lock && (o.Min is null || o.Max is null)))
                    throw new ModelingException(constituent, $"parameter {name} needs a Value, and a Min and Max unless locked.");
                list.Add(new TemplateParameter(name, v, o.Min ?? v, o.Max ?? v, o.Lock));
                continue;
            }
            var p = list[i];
            list[i] = new TemplateParameter(name, o.Value ?? p.Start, o.Min ?? p.Min, o.Max ?? p.Max, o.Lock || p.Lock);
        }
        foreach (var p in list)
            if (!p.Lock && !(p.Min < p.Max && p.Start >= p.Min && p.Start <= p.Max))
                throw new ModelingException(constituent, $"parameter {p.Name} starts at {G(p.Start)}, outside [{G(p.Min)}, {G(p.Max)}].");
        return list;
    }

    /// <summary>The held-out points of a table or equation fit, by <see cref="HeldOutSplit"/> — spread over the domain.</summary>
    private static bool[] Split(double[][] x, double[] y, CurrentFitSettings s)
    {
        int d = x[0].Length;
        var lo = new double[d]; var hi = new double[d];
        for (int i = 0; i < d; i++) { lo[i] = x.Min(p => p[i]); hi[i] = x.Max(p => p[i]); }
        double[][] hat = [.. x.Select(p => Enumerable.Range(0, d).Select(i => Norm(p[i], lo[i], hi[i])).ToArray())];
        return HeldOutSplit.Select(hat, [.. y.Select(v => new[] { v })], s.HeldOutFraction, SplitMix64.Mix(s.Seed));
    }

    private static double Norm(double v, double lo, double hi) => hi > lo ? 2 * (v - lo) / (hi - lo) - 1 : 0;

    private static FitErrorRecord Error(Func<double[], (double F, double Dg, double Dd)> f, double[][] x, double[] y, bool[] held)
    {
        var train = new List<double>(); var hold = new List<double>();
        for (int k = 0; k < x.Length; k++) (held[k] ? hold : train).Add(f(x[k]).F - y[k]);
        static double Rms(List<double> r) => r.Count == 0 ? 0 : Math.Sqrt(r.Sum(e => e * e) / r.Count);
        double range = y.Max() - y.Min();
        double scale = range > 0 ? range : 1;
        return new FitErrorRecord(train.Count, Rms(train), Rms(train) / scale, hold.Count, Rms(hold), Rms(hold) / scale);
    }

    /// <summary>
    /// MC-2's default G_x: ten times the largest own-branch conductance over the training points, never zero. A point
    /// where that conductance is not finite (issue 67: a re-gridded, transformed gate current overflowed at forward gate)
    /// is refused here, naming the branch and the point: <c>Math.Max</c> would carry it into G_x, and the record holding it
    /// could then not be saved, since JSON has no infinity.
    /// </summary>
    internal static double Gx(RuntimeEvaluator f, IEnumerable<double[]> x, double[] own, string branch, IReadOnlyList<string> inputs)
    {
        Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
        double largest = 0;
        foreach (var p in x)
        {
            f.Evaluate(p, g[..p.Length]);
            double conductance = Math.Abs(g[0] * own[0] + g[1] * own[1]);
            if (!double.IsFinite(conductance))
                throw new ModelingException(branch,
                    $"the fitted current's conductance is {(double.IsNaN(conductance) ? "not a number" : "infinite")} at the training point "
                    + string.Join(", ", p.Select((v, i) => $"{(i < inputs.Count ? inputs[i] : $"x{i}")} = {G(v)}"))
                    + ", so no G_x can be set from it. The fit overflows there; it cannot be saved as it stands.");
            largest = Math.Max(largest, conductance);
        }
        return GuidedExtrapolation.DefaultGx(Math.Max(largest, 1e-12));
    }

    /// <summary>
    /// The local law's added conductance at the data's lowest and highest <c>vgs</c> (R-mc29-4): the largest over the
    /// points on each edge, as <c>2e-09 S at −6 V, 4.9 S at 1 V</c>.
    /// </summary>
    private static string Edges(RuntimeEvaluator f, IReadOnlyList<double[]> x, double[] own, double gx)
    {
        double lo = x.Min(p => p[0]), hi = x.Max(p => p[0]), tol = 1e-9 * Math.Max(hi - lo, 1);
        double At(double vgs)
        {
            Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
            double worst = 0;
            foreach (var p in x)
            {
                if (Math.Abs(p[0] - vgs) > tol) continue;
                f.Evaluate(p, g[..p.Length]);
                double gOwn = 0;
                for (int k = 0; k < p.Length; k++) gOwn += g[k] * own[k];
                worst = Math.Max(worst, GuidedExtrapolation.LocalConductance(gOwn, gx));
            }
            return worst;
        }
        static string V(double v) => G(v).Replace("-", "−", StringComparison.Ordinal);
        return $"{G(At(lo))} S at {V(lo)} V, {G(At(hi))} S at {V(hi)} V";
    }

    private static Func<double[], (double F, double Dg, double Dd)> Guided(RuntimeEvaluator f, TrainingDomain domain,
                                                                           IReadOnlyList<string> inputs, double gx, double[] own)
    {
        var guided = new GuidedExtrapolation(f, domain, inputs, gx, own, ExtrapolationMode.Local);
        return p =>
        {
            Span<double> g = stackalloc double[RuntimeEvaluator.MaxInputs];
            double v = guided.Evaluate(p, g[..p.Length]);
            return (v, g[0], g[1]);
        };
    }

    // ── The report (R-mc7-3, R-mc7-5) ─────────────────────────────────────────

    /// <summary>The fitted Id's Gm and Gds against the data's DC derivatives at every point — each temperature's points on
    /// their own. Not with self-heating: a self-heated DC derivative includes the heating that the isothermal Gds does not.</summary>
    private static ConductanceRecord? Conductances(IvPoints iv, Func<double[], (double F, double Dg, double Dd)> f, List<string> report)
    {
        if (iv.SelfHeated || iv.Trapped)
        {
            report.Add(iv.Trapped
                ? "Id: Gm and Gds are not compared with the DC data's own slopes, which include the trap states' response."
                : "Id: Gm and Gds are not compared with the DC data's own slopes, which include its self-heating.");
            return null;
        }
        if (iv.X.Length < 7) return null;
        var data = new (double Dx, double Dy)[iv.X.Length];
        var groups = iv.HasT
            ? Enumerable.Range(0, iv.X.Length).GroupBy(k => Math.Round(iv.Ambient[k] / TemperatureTolerance)).Select(g => g.ToArray())
            : [Enumerable.Range(0, iv.X.Length).ToArray()];
        foreach (var idx in groups)
        {
            if (idx.Length < 7) { foreach (int k in idx) data[k] = (double.NaN, double.NaN); continue; }
            var local = LocalGradients.Of([.. idx.Select(k => (iv.X[k][0], iv.X[k][1]))], [.. idx.Select(k => iv.Id[k])]);
            for (int j = 0; j < idx.Length; j++) data[idx[j]] = local[j];
        }
        double sgm = 0, sgds = 0, rgm = 0, rgds = 0;
        int n = 0;
        for (int k = 0; k < iv.X.Length; k++)
        {
            var (dg, dd) = data[k];
            if (!double.IsFinite(dg) || !double.IsFinite(dd)) continue;
            var m = f(iv.X[k]);
            sgm += (m.Dg - dg) * (m.Dg - dg); sgds += (m.Dd - dd) * (m.Dd - dd);
            rgm += dg * dg; rgds += dd * dd;
            n++;
        }
        if (n == 0) return null;
        double gm = Math.Sqrt(sgm / n), gds = Math.Sqrt(sgds / n);
        return new ConductanceRecord(gm, rgm > 0 ? gm / Math.Sqrt(rgm / n) : 0, gds, rgds > 0 ? gds / Math.Sqrt(rgds / n) : 0);
    }

    /// <summary>With Exchange, the jumps at vds = 0 over the domain's vgs: Id's value and vds-slope, and the gate–source branch.</summary>
    private static SymmetryRecord Jumps(IvPoints iv, Func<double[], (double F, double Dg, double Dd)> id,
                                        Func<double[], (double F, double Dg, double Dd)>? igGs,
                                        Func<double[], (double F, double Dg, double Dd)>? igGd)
    {
        double lo = iv.Domain.Polygon.Min(p => p.Vgs), hi = iv.Domain.Polygon.Max(p => p.Vgs);
        double value = 0, slope = 0, gate = 0;
        for (int k = 0; k <= 100; k++)
        {
            var at = iv.At(lo + (hi - lo) * k / 100.0, 0, iv.MidT);
            var m = id(at);
            value = Math.Max(value, Math.Abs(2 * m.F));
            slope = Math.Max(slope, Math.Abs(m.Dg));
            if (igGs is not null && igGd is not null) gate = Math.Max(gate, Math.Abs(igGs(at).F - igGd(at).F));
        }
        return new SymmetryRecord(value, slope, gate);
    }

    // ── DC/RF dispersion (R-mc7-4) ────────────────────────────────────────────

    private sealed record DispersionFit(Constituent Constituent, Payload Payload, double? Eq649, (double, double) Reference);

    private static DispersionFit Dispersion(CmcDocument doc, IntrinsicResult intrinsic, CurrentFitSettings settings,
                                            DispersionFitSettings disp, IvPoints iv,
                                            Func<double[], (double F, double Dg, double Dd)> idDc,
                                            List<string> report, RunControl? rc)
    {
        var sets = disp.DataSets.Count > 0
            ? [.. disp.DataSets.Select(n => doc.DataSets.FirstOrDefault(s => s.Name == n)
                                           ?? throw new ModelingException(n, "the dispersion names it, and the document has no such data set."))]
            : doc.DataSets.Where(s => s.Kind is DataSetKind.SParams or DataSetKind.PulsedSParams).ToList();

        var x = new List<double[]>();
        var targets = new List<double[]>();
        foreach (var set in sets)
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Gm"))
                throw new ModelingException(set.Name, $"the dispersion reads its RF Gm and Gds, and {IntrinsicExtraction.NoAdmittance(ds)}");
            double[] f = ds["Gm"].Axes[1].Values, band = ds["Band"].RealValues;
            double[] gm = ds["Gm"].RealValues, gds = ds["Gds"].RealValues, flagged = ds["Flagged"].RealValues;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            int nf = f.Length;
            for (int p = 0; p < vgs.Length; p++)
            {
                if (ex[p] != 0) continue;
                if (settings.Symmetry == DrainSourceSymmetry.Exchange && vds[p] < 0) continue;
                double sg = 0, sd = 0; int c = 0;
                for (int k = 0; k < nf; k++)
                {
                    if (f[k] < band[0] || f[k] > band[1] || flagged[p * nf + k] != 0) continue;
                    sg += gm[p * nf + k]; sd += gds[p * nf + k]; c++;
                }
                if (c == 0) continue;
                x.Add([vgs[p], vds[p]]);
                targets.Add([sg / c, sd / c]);
            }
        }
        if (x.Count < 4)
            throw new ModelingException("Dispersion", $"{x.Count} S-parameter point(s) with RF conductances; IdRF needs at least 4.");

        var domain = Hull(x);
        (double Vgs, double Vds) reference = disp.Reference ?? Middle(x);
        // IdRF is a function of the voltages; with T an input of IdDC it is anchored at the S data's temperature.
        double tS = sets.Select(s => s.Conditions.AmbientC).FirstOrDefault(a => a is not null) ?? iv.TemperatureC;
        double anchor = idDc(iv.At(reference.Vgs, reference.Vds, tS)).F;

        rc?.BeginStage("Training IdRF");
        var trained = AnnTrainer.Train(new AnnTrainingProblem(2)
        {
            Derivatives = new AnnDerivativeData(x, [0, 1], targets),
            Anchor = new AnnAnchor([reference.Vgs, reference.Vds], anchor),
        }, AnnSettings(disp.Hidden, AnnActivation.Tanh, CurrentConstituentSettings.DefaultMaxIterations, settings,
                       settings.HeldOutFraction), rc);
        foreach (string w in trained.Warnings) report.Add($"IdRF: {w}");
        double gx = Gx(trained.Network, x, Own(0, 1), "IdRF", Inputs);
        var e = trained.Derivatives!;
        var rfEval = Guided(trained.Network, domain, Inputs, gx, Own(0, 1));
        var payload = new Payload("IdRF", p => rfEval([p[0], p[1]]), [.. trained.Arrays("IdRF")], gx,
                                  new FitErrorRecord(e.TrainCount, e.TrainRms, e.TrainRelative, e.HeldOutCount, e.HeldOutRms, e.HeldOutRelative),
                                  Edges(trained.Network, x, Own(0, 1), gx));

        // [P1] eq. 6.49: RF conductances come from one current only if ∂Gm/∂vds = ∂Gds/∂vgs. Differencing measured
        // conductances to test that directly is too noisy on any realistic grid (it reported 86 % on exactly
        // conservative data), so the residual is the part of the RF conductances the least-squares current IdRF cannot
        // carry — zero when 6.49 holds, and what [P1] §6.4.2 says the adjoint fit trades off: RMS |∇IdRF − (Gm, Gds)|
        // relative to RMS |(Gm, Gds)| over every S point.
        double miss = 0, size = 0;
        Span<double> grad = stackalloc double[2];
        for (int k = 0; k < x.Count; k++)
        {
            trained.Network.Evaluate(x[k], grad);
            miss += Math.Pow(grad[0] - targets[k][0], 2) + Math.Pow(grad[1] - targets[k][1], 2);
            size += targets[k][0] * targets[k][0] + targets[k][1] * targets[k][1];
        }
        double? eq649 = size > 0 ? Math.Sqrt(miss / size) : null;
        if (eq649 is { } r649) report.Add($"IdRF: eq. 6.49 residual {G(100 * r649)} %.");
        return new DispersionFit(Constituent.Ann("IdRF", Inputs, domain), payload, eq649, reference);
    }

    /// <summary>
    /// The RF conductances a self-heating or trapped Id trains on (R-mc9-1, R-mc10-5): at every S point of the document's
    /// CW S-parameter sets, <c>Gm</c> and <c>Gds</c> averaged over the flatness band, as derivatives at the point's inputs
    /// with the slow states FROZEN at their DC values — <c>Tj = Tamb + Rth·P</c> from its DC currents, and each trap state at
    /// its DC state — because the S stimulus is far above the thermal and trap corners ([X10] §VI). Null when there are none.
    /// </summary>
    private static AnnDerivativeData? RfDerivatives(CmcDocument doc, IReadOnlyDictionary<string, NormalisedData> data,
                                                    IntrinsicResult intrinsic, CurrentFitSettings settings, ThermalHeating heating,
                                                    IvPoints iv, List<string> report)
    {
        if (iv.SelfHeated && heating.Rth is null) return null;
        var x = new List<double[]>();
        var targets = new List<double[]>();
        foreach (var set in doc.DataSets.Where(s => s.Kind == DataSetKind.SParams))
        {
            if (!intrinsic.Sets.TryGetValue(set.Name, out var ds) || !ds.Contains("Gm")) continue;
            double[] f = ds["Gm"].Axes[1].Values, band = ds["Band"].RealValues;
            double[] gm = ds["Gm"].RealValues, gds = ds["Gds"].RealValues, flagged = ds["Flagged"].RealValues;
            double[] vgs = ds["Vgsi"].RealValues, vds = ds["Vdsi"].RealValues, ex = ds["Excluded"].RealValues;
            double[] ig = ds["Ig"].RealValues, id = ds["Id"].RealValues;
            var points = data.TryGetValue(set.Name, out var d) && d.Points.Count == vgs.Length ? d.Points : null;
            int nf = f.Length;
            for (int p = 0; p < vgs.Length; p++)
            {
                if (ex[p] != 0 || double.IsNaN(id[p])) continue;
                if (settings.Symmetry == DrainSourceSymmetry.Exchange && vds[p] < 0) continue;
                double sg = 0, sd = 0; int c = 0;
                for (int k = 0; k < nf; k++)
                {
                    if (f[k] < band[0] || f[k] > band[1] || flagged[p * nf + k] != 0) continue;
                    sg += gm[p * nf + k]; sd += gds[p * nf + k]; c++;
                }
                if (c == 0) continue;
                double tAmb = AmbientOf(set, points?[p]);
                double tj = iv.SelfHeated ? tAmb + heating.RthAt(tAmb) * Power(vgs[p], vds[p], double.IsNaN(ig[p]) ? 0 : ig[p], id[p]) : tAmb;
                double[] xp = iv.HasT ? [vgs[p], vds[p], tj] : [vgs[p], vds[p]];
                if (iv.Traps is { } traps)
                    xp = [.. xp, .. Enumerable.Range(0, traps.States.Count).Select(k => traps.Dynamics.DcValue(k, vgs[p], vds[p], tj))];
                x.Add(xp);
                targets.Add([sg / c, sd / c]);
            }
        }
        if (x.Count == 0) return null;
        string at = iv.SelfHeated && iv.Trapped ? "junction temperatures and DC trap states"
                  : iv.SelfHeated ? "junction temperatures" : "DC trap states";
        report.Add($"Id: trained on the I–V values and on the RF Gm/Gds of {x.Count} S point(s) at their {at}.");
        return new AnnDerivativeData(x, [0, 1], targets);
    }

    /// <summary>The point nearest the middle of the points' span.</summary>
    internal static (double, double) Middle(IReadOnlyList<double[]> x)
    {
        double gLo = x.Min(p => p[0]), gHi = x.Max(p => p[0]), dLo = x.Min(p => p[1]), dHi = x.Max(p => p[1]);
        var best = x.MinBy(p => Math.Pow(Norm(p[0], gLo, gHi), 2) + Math.Pow(Norm(p[1], dLo, dHi), 2))!;
        return (best[0], best[1]);
    }

    private static string G(double v) => v.ToString("G3", CultureInfo.InvariantCulture);
}
