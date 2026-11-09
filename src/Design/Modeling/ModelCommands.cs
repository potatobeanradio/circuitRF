// The Model Composer's commands (brief-modelcomposer-13 R-mc13-1, R-mc13-2; overview D13, D15): the operations the window's
// commands and the `model` verb both call, where no single step function already was one — a new document, an import
// into an open one, a de-embedding report, a fit of one step or all of them, an explanation, and a passive cell export
// that records where it went. Each reads the .cmc from disk and writes it back, as every step's FitFile does, so the
// window and the verb produce the same bytes from the same file; the verb owns argument parsing and reporting only.
//
// Refusals are ModelingException (or the reader's CmcFormatException), each naming what it is about; cancellation throws
// OperationCanceledException and writes nothing (D13). `fit all` commits each finished step as the window's step rail
// does — a cancelled step writes nothing, the steps before it keep their results.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.Composed.Traps;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>One line of a command's report: what it is about (<see cref="Kind"/>, <see cref="Name"/>) and its text.</summary>
public sealed record ModelLine(string Kind, string Name, string Text);

/// <summary>What a fit may change before it runs, as the window's step settings would: the method, the seed, a passive
/// topology, and an Active document's base periphery. Null leaves the document's own.</summary>
public sealed record FitOptions(string? Method = null, ulong? Seed = null, string? Topology = null, BasePeriphery? Periphery = null)
{
    public static FitOptions None { get; } = new();
}

public static partial class ModelCommands
{
    /// <summary>The steps a fit can run, in the order <c>all</c> runs them.</summary>
    public static readonly IReadOnlyList<string> Steps = ["network", "thermal", "current", "traps", "charge", "noise", "passive"];

    // ── new ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <c>.cmc</c> (R-mc13-1): its kind, optionally an extrinsic network — a stock one by id, copied beside the
    /// document, or a six-pin <c>.csch</c> (a relative path is relative to the <c>.cmc</c>) — and the base periphery.
    /// An existing file is refused, never overwritten.
    /// </summary>
    public static IReadOnlyList<ModelLine> New(string cmcPath, CmcKind kind, string? network = null, BasePeriphery? periphery = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        string name = Path.GetFileName(cmcPath);
        if (File.Exists(cmcPath)) throw new ModelingException(name, "it already exists; nothing is overwritten.");
        if (kind == CmcKind.Passive && network is not null)
            throw new ModelingException("Network", "a Passive document has no six-pin extrinsic network.");
        if (kind == CmcKind.Passive && periphery is not null)
            throw new ModelingException("BasePeriphery", "a Passive document has no fingers or unit width.");

        var doc = new CmcDocument { Kind = kind, BasePeriphery = periphery };
        CmcFile.ToBytes(doc);                       // the periphery is checked before a network file is copied anywhere
        string dir = Path.GetDirectoryName(cmcPath)!;
        Directory.CreateDirectory(dir);
        if (network is { } n)
        {
            if (n.EndsWith(".csch", StringComparison.OrdinalIgnoreCase))
            {
                string full = Path.IsPathRooted(n) ? n : Path.GetFullPath(Path.Combine(dir, n));
                if (!File.Exists(full)) throw new ModelingException("Network", $"'{n}' does not exist.");
                var six = SixPinNetwork.Load(full);
                doc = CmcExtrinsic.With(doc, six.DefaultSettings(Path.GetRelativePath(dir, full).Replace('\\', '/')));
            }
            else
            {
                if (StockNetworks.All.All(s => s.Id != n))
                    throw new ModelingException("Network",
                        $"'{n}' is not a stock network or a .csch; the stock networks are {string.Join(", ", StockNetworks.All.Select(s => s.Id))}.");
                doc = StockNetworks.Choose(doc, cmcPath, n);
            }
        }
        CmcFile.Write(cmcPath, doc);

        var parts = new List<string> { kind.ToString() };
        if (CmcExtrinsic.Network(doc).Schematic is { } schematic) parts.Add($"network {schematic}");
        if (periphery is { } p) parts.Add($"N0 {p.Fingers} × {p.UnitWidth}");
        return [new ModelLine("document", name, $"{name}: {string.Join(", ", parts)}.")];
    }

    // ── import ────────────────────────────────────────────────────────────────

    /// <summary>The import format a source's extension names, or null for one no importer reads.</summary>
    public static string? FormatOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length > 3 && ext[1] == 's' && ext[^1] == 'p' && ext[2..^1].All(char.IsAsciiDigit)) return ImportSettings.Touchstone;
        return ext switch
        {
            ".csv" or ".tsv" or ".txt" => ImportSettings.Csv,
            ".npy" => ImportSettings.Npy,
            ".mat" => ImportSettings.Mat,
            ".npz" => ImportSettings.Npz,
            ".ds" => ImportSettings.Ds,
            ".spl" or ".lpcwave" => ImportSettings.Loadpull,
            _ => null,
        };
    }

    /// <summary>
    /// The sources <paramref name="inputs"/> name, relative to the <c>.cmc</c> with '/' separators: a file as itself, a folder
    /// as every file directly in it that an importer reads, in ordinal order — a Touchstone set is a folder of files. One
    /// set is one format; a mixture is refused.
    /// </summary>
    public static IReadOnlyList<string> Sources(string cmcPath, IReadOnlyList<string> inputs)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        var files = new List<string>();
        foreach (string input in inputs)
        {
            string full = Path.IsPathRooted(input) ? input : Path.GetFullPath(Path.Combine(dir, input));
            if (Directory.Exists(full))
            {
                var found = Directory.EnumerateFiles(full).Where(f => FormatOf(f) is not null).Order(StringComparer.Ordinal).ToList();
                if (found.Count == 0) throw new ModelingException(input, "the folder holds no file an importer reads.");
                files.AddRange(found);
            }
            else if (File.Exists(full)) files.Add(full);
            else throw new ModelingException(input, "it does not exist.");
        }
        if (files.Count == 0) throw new ModelingException("Sources", "no source is named.");
        var formats = files.Select(FormatOf).Distinct().ToList();
        if (formats.Count > 1)
            throw new ModelingException("Sources", $"they mix {string.Join(" and ", formats.Select(f => f ?? "unreadable files"))}; one data set is one format.");
        return [.. files.Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))];
    }

    /// <summary>
    /// The map an array source's names suggest (<see cref="DataImport.SuggestArrayMap"/>), from its arrays as listed — the
    /// window fills its empty map field with it, and <c>model import</c> uses it when no <c>--map</c> is given.
    /// <paramref name="source"/> is relative to the <c>.cmc</c>, as <see cref="Sources"/> gives it.
    /// </summary>
    public static ArrayMapSuggestion SuggestArrayMap(string cmcPath, string source, string format)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        return DataImport.SuggestArrayMap(DataImport.ListArrays(DataImport.ResolveSource(dir, source), format).Arrays);
    }

    /// <summary>The arrays an array source holds, as one sentence for a refusal: <c>vg (41), id (41 × 9), …</c>.</summary>
    public static string ArraysHeld(string cmcPath, string source, string format)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        var list = DataImport.ListArrays(DataImport.ResolveSource(dir, source), format).Arrays;
        return list.Count == 0 ? "it holds no arrays" : string.Join(", ", list.Take(16).Select(a => $"{a.Name} ({a.ShapeText})")) + (list.Count > 16 ? ", …" : "");
    }

    /// <summary>A data set name for <paramref name="inputs"/> no other set in <paramref name="doc"/> has: the first input's folder
    /// or file name, then <c>-2</c>, <c>-3</c>, ….</summary>
    public static string DefaultName(CmcDocument doc, IReadOnlyList<string> inputs)
    {
        string first = inputs.Count > 0 ? inputs[0].TrimEnd('/', '\\') : "data";
        string stem = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        if (stem.Length == 0) stem = "data";
        string name = stem;
        for (int k = 2; doc.DataSets.Any(s => s.Name == name); k++) name = $"{stem}-{k}";
        return name;
    }

    /// <summary>
    /// Adds <paramref name="set"/> to the document and imports it through the cache (R-mc3-3, R-mc3-4) — the window's
    /// importer and <c>model import</c>. The document is written only once the import has succeeded; refused or
    /// cancelled, it is as it was. One line for the set (points, bias range, frequencies), then what the open said.
    /// </summary>
    public static IReadOnlyList<ModelLine> Import(string cmcPath, CmcDataSet set, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        if (doc.DataSets.Any(s => s.Name == set.Name))
            throw new ModelingException(set.Name, "the document already has a data set of that name.");
        var next = doc with { DataSets = [.. doc.DataSets, set] };
        CmcFile.ToBytes(next);

        var lines = new List<ModelLine>();
        if (CmcStamps.FeedsFits(set))
        {
            var open = new CmcCache(cmcPath).Open(next, rc);
            lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {set.Kind}, {DataImport.Summary(open.Data[set.Name])}"));
            // The open's own line for the new set says what the line above already does; what it says of the others stays.
            lines.AddRange(open.Status.Where(s => !s.StartsWith(set.Name + ":", StringComparison.Ordinal)).Concat(open.Warnings)
                                      .Select(s => new ModelLine("note", set.Name, s)));
            lines.AddRange(DataHygiene.SetWarnings(DataHygiene.Check([(set, open.Data[set.Name])]))
                                      .Select(s => new ModelLine("note", set.Name, s)));
        }
        else
            lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {set.Kind}, {set.Sources.Count} source(s), validation only."));
        rc?.ThrowIfCancellationRequested();
        CmcFile.Write(cmcPath, next);
        return lines;
    }

    /// <summary>
    /// What <see cref="Import"/> would do with <paramref name="set"/>, writing nothing — no data set added, no cache touched;
    /// <paramref name="cmcPath"/> need not exist, and the sources resolve against its folder either way. One line naming what
    /// the first source holds (each array with its shape, or its first lines), then the line an import prints, how it read
    /// any array not in the documented layout, every warning it would add, and a count of the points hygiene flags. A set
    /// that would be refused throws exactly as <see cref="Import"/> does.
    /// </summary>
    public static IReadOnlyList<ModelLine> DryRun(string cmcPath, CmcDataSet set, RunControl? rc = null)
    {
        var doc = File.Exists(cmcPath) ? CmcFile.Read(cmcPath)
            : new CmcDocument { Kind = set.Kind == DataSetKind.PassiveSParams ? CmcKind.Passive : CmcKind.Active };
        if (doc.DataSets.Any(s => s.Name == set.Name))
            throw new ModelingException(set.Name, "the document already has a data set of that name.");
        CmcFile.ToBytes(doc with { DataSets = [.. doc.DataSets, set] });

        string dir = Path.GetDirectoryName(Path.GetFullPath(cmcPath))!;
        var lines = new List<ModelLine>();
        if (set.Sources.Count > 0)
        {
            string first = DataImport.ResolveSource(dir, set.Sources[0]);
            if (ImportSettings.IsArrayFormat(set.Import.Format))
            {
                var (arrays, skipped) = DataImport.ListArrays(first, set.Import.Format);
                lines.Add(new ModelLine("source", set.Sources[0], $"arrays in {set.Sources[0]}: " +
                    (arrays.Count == 0 ? "none" : string.Join(", ", arrays.Select(a => $"{a.Name} ({a.ShapeText}{(a.IsComplex ? ", complex" : "")})"))) + "."));
                lines.AddRange(skipped.Select(x => new ModelLine("source", x.Name, $"not read: {x.Name} — it {x.Why}")));
            }
            else if (File.Exists(first))
            {
                lines.Add(new ModelLine("source", set.Sources[0], $"{set.Sources[0]} begins:" +
                    (set.Sources.Count > 1 ? $" (1 of {set.Sources.Count} files)" : "")));
                lines.AddRange(File.ReadLines(first).Where(l => l.Trim().Length > 0).Take(4).Select(l => new ModelLine("source", set.Sources[0], "  " + l)));
            }
        }

        if (!CmcStamps.FeedsFits(set))
        {
            lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {set.Kind}, {set.Sources.Count} source(s), validation only; read when it is validated."));
        }
        else
        {
            var result = DataImport.Import(set, dir, rc);
            lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {set.Kind}, {result.Summary}"));
            lines.AddRange(result.Notes.Select(n => new ModelLine("note", set.Name, $"{set.Name}: {n}")));
            var flags = DataHygiene.Check([(set, result.Data)]);
            lines.AddRange(DataHygiene.SetWarnings(flags).Select(w => new ModelLine("note", set.Name, w)));
            var perPoint = flags.Where(f => f.Point.Length > 0).GroupBy(f => f.Kind).ToList();
            if (perPoint.Count > 0)
                lines.Add(new ModelLine("note", set.Name, $"{set.Name}: points flagged — " +
                    string.Join(", ", perPoint.Select(g => $"{g.Key} {g.Select(f => f.Point).Distinct().Count()}")) + "; the Data step's bias plane shows them."));
        }
        lines.Add(new ModelLine("dryRun", set.Name, "nothing written (dry run)."));
        return lines;
    }

    // ── deembed ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The pads, the network and every data set's intrinsic data (MC-4) — computed through the cache, so a second call
    /// solves nothing. One line for the pads, one for the network, one per data set, then the extraction's notes.
    /// </summary>
    public static IReadOnlyList<ModelLine> Deembed(string cmcPath, RunControl? rc = null)
    {
        var doc = CmcFile.Read(cmcPath);
        if (doc.Kind != CmcKind.Active) throw new ModelingException("Kind", "a Passive document has no pads or network to remove.");
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
        var lines = new List<ModelLine> { PadsLine(doc), NetworkLine(cmcPath, doc) };
        foreach (var set in doc.DataSets)
            if (intrinsic.Sets.TryGetValue(set.Name, out var ds))
                lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {IntrinsicSummary(ds)}"));
        lines.AddRange(intrinsic.Notes.Select(n => new ModelLine("note", "", n)));
        return lines;
    }

    private static ModelLine PadsLine(CmcDocument doc)
    {
        var p = CmcExtrinsic.Pads(doc);
        return new ModelLine("pads", p.Kind.ToString(), p.Kind == PadsKind.None ? "pads: none." : $"pads: {p.Kind}.");
    }

    private static ModelLine NetworkLine(string cmcPath, CmcDocument doc)
    {
        var n = CmcExtrinsic.Network(doc);
        if (n.Schematic is null) return new ModelLine("network", "", "network: none — the data is taken as intrinsic.");
        int locked = n.Variables.Count(v => v.Value.Lock), tied = n.Variables.Count(v => v.Value.Tie is not null);
        string fit = n.Result is { } r ? $", fitted ({r.Method})" : "";
        string stale = CmcStamps.StaleSteps(cmcPath, doc).Contains("Network") ? ", stale" : "";
        return new ModelLine("network", n.Schematic,
            $"network: {n.Schematic}, {n.Variables.Count} variable(s), {locked} locked, {tied} tied{fit}{stale}.");
    }

    /// <summary>One data set's intrinsic data in a line: points, the intrinsic bias ranges, the frequencies.</summary>
    public static string IntrinsicSummary(DataSet ds)
    {
        var parts = new List<string>();
        int n = ds.Cubes.TryGetValue("Vgsi", out var g) ? g.Axes[0].Values.Length : 0;
        parts.Add($"{n} point(s)");
        if (Range(ds, "Vgsi") is { } vg) parts.Add($"intrinsic Vgs {G(vg.Lo)} to {G(vg.Hi)} V");
        if (Range(ds, "Vdsi") is { } vd) parts.Add($"Vds {G(vd.Lo)} to {G(vd.Hi)} V");
        if (ds.Cubes.TryGetValue("Y", out var y)) parts.Add($"{y.Axes[1].Values.Length} frequencies");
        return string.Join(", ", parts) + ".";
    }

    private static (double Lo, double Hi)? Range(DataSet ds, string cube)
    {
        if (!ds.Cubes.TryGetValue(cube, out var c)) return null;
        var v = Enumerable.Range(0, c.Axes[0].Values.Length).Select(i => (double)c[i]).Where(double.IsFinite).ToArray();
        return v.Length == 0 ? null : (v.Min(), v.Max());
    }

    // ── fit ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The steps <c>all</c> runs on <paramref name="doc"/>, in order: a Passive document's passive step; an Active one's
    /// network (when a variable is free to fit), thermal, the currents (through the trap step when traps are modelled,
    /// which re-trains them) and the charges. With no S data, <c>all</c> holds the network at its values rather than fit
    /// it (<see cref="NoSData"/>), and the charge step writes the typed charges (MC-18).
    /// </summary>
    public static IReadOnlyList<string> AllSteps(CmcDocument doc)
    {
        if (doc.Kind == CmcKind.Passive) return ["passive"];
        var steps = new List<string>();
        var n = CmcExtrinsic.Network(doc);
        if (n.Schematic is not null && n.Variables.Values.Any(v => !v.Lock && v.Tie is null)) steps.Add("network");
        steps.Add("thermal");
        steps.Add("current");
        if (TrapFit.Modelled(doc)) steps.Add("traps");
        steps.Add("charge");
        if (NoiseFit.Settings(doc).Sources.Count > 0) steps.Add("noise");
        return steps;
    }

    /// <summary>The line <c>all</c> reports for the network when there is no S data to fit it to (R-mc18-3).</summary>
    public const string NetworkHeld = "network: held at its values — no S data to fit to";

    /// <summary>Whether <paramref name="doc"/> holds no S-parameters for the network to be fitted to (R-mc18-3).</summary>
    public static bool NoSData(CmcDocument doc) => !doc.DataSets.Any(s => s.Kind is DataSetKind.SParams or DataSetKind.PulsedSParams);

    /// <summary><paramref name="doc"/> with <paramref name="options"/> written into <paramref name="step"/>'s settings, as the
    /// window's step panel writes them. A method the step does not have is refused naming the ones it does.</summary>
    public static CmcDocument Configure(CmcDocument doc, string step, FitOptions options)
    {
        string? m = options.Method;
        switch (step)
        {
            case "network":
            {
                var n = CmcExtrinsic.Network(doc);
                var fit = n.Fit ?? NetworkFitSettings.Default;
                if (m is not null) fit = fit with { Method = Parse<NetworkFitMethod>(step, m) };
                if (options.Seed is { } seed) fit = fit with { Seed = seed };
                return m is null && options.Seed is null ? doc : CmcExtrinsic.With(doc, n with { Fit = fit });
            }
            case "current":
            {
                var s = CurrentFit.Settings(doc);
                if (m is not null) { var method = Parse<CurrentMethod>(step, m); s = s with { Id = s.Id with { Method = method }, Ig = s.Ig with { Method = method } }; }
                if (options.Seed is { } seed) s = s with { Seed = seed };
                return m is null && options.Seed is null ? doc : CurrentFit.WithSettings(doc, s);
            }
            case "charge":
            {
                var s = ChargeFit.Settings(doc);
                if (m is not null) { var method = Parse<ChargeMethod>(step, m); s = s with { Qg = s.Qg with { Method = method }, Qd = s.Qd with { Method = method } }; }
                if (options.Seed is { } seed) s = s with { Seed = seed };
                return m is null && options.Seed is null ? doc : ChargeFit.WithSettings(doc, s);
            }
            case "traps":
            {
                // A trap step's "method" is its model: None, CaptureEmission, Custom or a registered one; the reader refuses
                // a name it does not know when the document is written. Without pulsed I–V the shift coupling is the only one,
                // and it is written as the window writes it (R-mc19-2).
                if (m is null) return doc;
                var s = TrapFit.Settings(doc) with { Model = m };
                if (!TrapFit.HasPulsedIv(doc) && m != ModelTraps.None) s = s with { Coupling = ModelTraps.Shift };
                return TrapFit.WithSettings(doc, s);
            }
            case "passive":
            {
                if (m is not null) throw new ModelingException(step, "a passive fit has no method; it fits the topology the step names.");
                var s = PassiveStep.Settings(doc);
                if (options.Topology is { } t) s = s with { Topology = t };
                if (options.Seed is { } seed) s = s with { Fit = s.Fit with { Seed = seed } };
                return options.Topology is null && options.Seed is null ? doc : PassiveStep.WithSettings(doc, s);
            }
            case "thermal":
                if (m is not null) throw new ModelingException(step, "the thermal step has no method.");
                return doc;
            default:
                throw new ModelingException(step, $"it is not a step; the steps are {string.Join(", ", Steps)} and all.");
        }
    }

    /// <summary>Whether a step takes a seed — what <c>all</c> with a seed writes the seed into.</summary>
    public static bool Seeded(string step) => step is "network" or "current" or "charge" or "passive";

    /// <summary>
    /// Fits <paramref name="step"/> — one of <see cref="Steps"/>, or <c>all</c> (<see cref="AllSteps"/>) — through each step's
    /// own <c>FitFile</c>, after writing <paramref name="options"/> into the settings. A refusal or cancellation before any
    /// step finished leaves the file exactly as it was, options included.
    /// </summary>
    public static IReadOnlyList<ModelLine> Fit(string cmcPath, string step, FitOptions? options = null, RunControl? rc = null)
    {
        options ??= FitOptions.None;
        cmcPath = Path.GetFullPath(cmcPath);
        byte[] original = File.ReadAllBytes(cmcPath);
        var doc = CmcFile.Read(cmcPath);

        IReadOnlyList<string> steps;
        if (step == "all")
        {
            if (options.Method is not null) throw new ModelingException("all", "a method belongs to one step; name the step it is for.");
            steps = AllSteps(doc);
        }
        else
        {
            if (!Steps.Contains(step)) throw new ModelingException(step, $"it is not a step; the steps are {string.Join(", ", Steps)} and all.");
            if ((step == "passive") != (doc.Kind == CmcKind.Passive))
                throw new ModelingException(step, doc.Kind == CmcKind.Passive
                    ? "a Passive document has one step, passive."
                    : "an Active document has no passive step.");
            if (step == "traps" && TrapFit.Unavailable(Configure(doc, step, options)) is { } why && options.Method is not "None")
                throw new ModelingException(step, why);
            steps = [step];
        }
        if (options.Topology is not null && !steps.Contains("passive"))
            throw new ModelingException("Topology", "only the passive step fits a topology.");
        if (options.Seed is not null && step != "all" && !Seeded(step))
            throw new ModelingException(step, "the step has no seed.");

        var configured = doc;
        if (options.Periphery is { } periphery)
        {
            if (doc.Kind != CmcKind.Active) throw new ModelingException("BasePeriphery", "a Passive document has no fingers or unit width.");
            configured = doc with { BasePeriphery = periphery };
        }
        foreach (string s in steps)
            configured = Configure(configured, s, s == "all" || !Seeded(s) ? options with { Seed = null } : options);
        bool changed = !ReferenceEquals(configured, doc);

        var lines = new List<ModelLine>();
        int finished = 0;
        try
        {
            if (changed) CmcFile.Write(cmcPath, configured);
            foreach (string s in steps)
            {
                rc?.ThrowIfCancellationRequested();
                // R-mc18-3: all holds the network with no S data; a fit naming the step still refuses, as it says why.
                if (step == "all" && s == "network" && NoSData(configured))
                    lines.Add(new ModelLine("step", s, NetworkHeld));
                else
                    lines.AddRange(RunStep(cmcPath, s, rc));
                finished++;
            }
        }
        catch when (finished == 0 && changed)
        {
            AtomicWrite(cmcPath, original);
            throw;
        }
        return lines;
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static IEnumerable<ModelLine> RunStep(string cmcPath, string step, RunControl? rc)
    {
        IEnumerable<string> report = step switch
        {
            "network" => NetworkFit.FitFile(cmcPath, rc: rc) is var r
                ? [$"network: {r.Method}, cost {G(r.Cost)}.", .. r.Report.Lines, .. r.Notes] : [],
            "thermal" => ThermalFit.FitFile(cmcPath, rc).Record is var t
                ? t.Report.Count > 0 ? t.Report
                  : [t.Rth is { } rth ? $"thermal: Rth {G(rth)} °C/W." : $"thermal: no self-heating; data at {string.Join(", ", t.Temperatures.Select(G))} °C."]
                : [],
            "current" => CurrentFit.FitFile(cmcPath, rc).Record.Report,
            "traps" => TrapFit.FitFile(cmcPath, rc) is var (traps, current) ? [.. traps.Record.Report, .. current?.Record.Report ?? []] : [],
            "charge" => ChargeFit.FitFile(cmcPath, rc).Record.Report,
            "passive" => PassiveStep.FitFile(cmcPath, rc).Record.Report,
            "noise" => NoiseFit.FitFile(cmcPath, rc).Report,
            _ => throw new ModelingException(step, "it is not a step."),
        };
        var lines = report.Select(l => new ModelLine("step", step, l)).ToList();
        if (lines.Count == 0) lines.Add(new ModelLine("step", step, $"{step}: fitted."));
        return lines;
    }

    private static T Parse<T>(string step, string text) where T : struct, Enum
    {
        foreach (var v in Enum.GetValues<T>())
            if (string.Equals(v.ToString(), text, StringComparison.OrdinalIgnoreCase)) return v;
        throw new ModelingException(step, $"'{text}' is not a method of the step; it takes {string.Join(", ", Enum.GetNames<T>())}.");
    }

    // ── explain ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What the document holds and what has been done with it (R-mc13-1): its data sets and their planes, the intrinsic
    /// ranges, <c>N0</c>/<c>W0</c>, the temperature mode, each step's state (fresh, stale or absent), the network values
    /// with their identifiability, and the trap-setting levels each trap state was trained from. Runs no fit; the data
    /// and the intrinsic data come through their caches.
    /// </summary>
    public static IReadOnlyList<ModelLine> Explain(string cmcPath, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        var lines = new List<ModelLine>
        {
            new("document", Path.GetFileName(cmcPath), $"{Path.GetFileName(cmcPath)}: {doc.Kind}, {doc.DataSets.Count} data set(s)."),
        };
        if (doc.Kind == CmcKind.Active)
            lines.Add(doc.BasePeriphery is { } p
                ? new ModelLine("periphery", "", $"N0 {p.Fingers}, W0 {p.UnitWidth}.")
                : new ModelLine("periphery", "", "N0 and W0 are not set; export needs them."));

        IReadOnlyDictionary<string, NormalisedData>? data = null;
        try { data = new CmcCache(cmcPath).Open(doc, rc).Data; }
        catch (DataImportException ex) { lines.Add(new ModelLine("note", ex.SourceName, $"data: not readable — {ex.Message}")); }

        foreach (var set in doc.DataSets)
        {
            var c = set.Conditions;
            var parts = new List<string> { set.Kind.ToString() };
            if (c.ReferencePlane is { } plane) parts.Add($"{plane.ToString().ToLowerInvariant()} plane");
            if (c.AmbientC is { } t) parts.Add($"{G(t)} °C");
            if (c.Quiescent is { } q) parts.Add($"quiescent {G(q.Vgs)}, {G(q.Vds)} V");
            if (c.PrePulse is { } pp) parts.Add($"pre-pulse {G(pp.Vgs)}, {G(pp.Vds)} V");
            if (set.Exclusions.Count > 0) parts.Add($"{set.Exclusions.Count} excluded");
            if (DataImport.CapacitanceSource(set) is { } cs) parts.Add($"capacitances: {cs}");
            string summary = data is not null && data.TryGetValue(set.Name, out var nd) ? " " + DataImport.Summary(nd) : "";
            lines.Add(new ModelLine("dataSet", set.Name, $"{set.Name}: {string.Join(", ", parts)}.{summary}"));
        }

        if (doc.Kind == CmcKind.Active && data is not null)
        {
            try
            {
                var intrinsic = new IntrinsicExtraction(cmcPath).Run(doc, data, rc: rc);
                foreach (var (name, ds) in intrinsic.Sets) lines.Add(new ModelLine("intrinsic", name, $"{name}: {IntrinsicSummary(ds)}"));
            }
            catch (ModelingException ex) { lines.Add(new ModelLine("note", ex.Input, $"intrinsic: not available — {ex.Message}")); }

            var plan = ThermalFit.Plan(doc, data);
            var heating = ThermalFit.Heating(doc);
            string mode = plan.IsSingle ? $"single, {G(plan.T0)} °C" : $"range, {string.Join(", ", plan.Temperatures.Select(G))} °C";
            string self = heating.SelfHeating ? heating.Rth is { } rth ? $"; self-heating, Rth {G(rth)} °C/W" : "; self-heating, Rth not yet extracted" : "";
            lines.Add(new ModelLine("temperature", "", $"temperature: {mode}{self}."));
        }

        lines.AddRange(StepStates(cmcPath, doc));
        if (doc.Kind == CmcKind.Active)
        {
            // R-mc17-5: constant capacitances are said so, never left to be read as nonlinear charges.
            if (ChargeFit.Record(doc) is { } charge && ChargeFit.LinearProvenance(ChargeFit.Settings(doc), charge) is { } linear)
            {
                lines.Add(new ModelLine("charge", ChargeFit.StepName, $"{linear}."));
                if (charge.Typed is { IsZero: true }) lines.Add(new ModelLine("charge", ChargeFit.StepName, ChargeFit.ZeroLine));
            }
            lines.AddRange(NetworkValues(cmcPath, doc));
            if (TrapFit.Record(doc) is { } traps)
            {
                foreach (var (name, a) in traps.Strengths)
                    lines.Add(new ModelLine("traps", name, a.Error is { } se
                        ? $"{name}: {G(a.Value)} ± {G(se)} V/V, fitted from multi-bias S."
                        : $"{name}: {G(a.Value)} V/V, typed."));
                foreach (var (state, levels) in traps.Levels)
                    lines.Add(new ModelLine("traps", state,
                        $"{state}: trained from setting levels {string.Join(", ", levels.Select(G))} V" +
                        (traps.Beyond.TryGetValue(state, out int beyond) && beyond > 0 ? $"; {beyond} point(s) beyond, validation only." : ".")));
            }
        }
        return lines;
    }

    private static IEnumerable<ModelLine> StepStates(string cmcPath, CmcDocument doc)
    {
        var stale = CmcStamps.StaleSteps(cmcPath, doc);
        string State(string step, bool done) => !done ? "absent" : stale.Contains(step) ? "stale" : "fresh";
        var rows = doc.Kind == CmcKind.Passive
            ? new (string Step, bool Done)[] { (PassiveStep.StepName, PassiveStep.Record(doc) is not null) }
            :
            [
                ("Network", CmcExtrinsic.Network(doc).Result is not null),
                (ThermalFit.StepName, ThermalFit.Record(doc) is not null),
                (CurrentFit.StepName, CurrentFit.Record(doc) is not null),
                (TrapFit.StepName, TrapFit.Record(doc) is not null),
                (ChargeFit.StepName, ChargeFit.Record(doc) is not null),
            ];
        foreach (var (step, done) in rows)
            yield return new ModelLine("step", step, $"{step}: {State(step, done)}.");
    }

    private static IEnumerable<ModelLine> NetworkValues(string cmcPath, CmcDocument doc)
    {
        var n = CmcExtrinsic.Network(doc);
        if (n.Schematic is null) yield break;
        var network = SixPinNetwork.Load(DataImport.ResolveSource(Path.GetDirectoryName(cmcPath)!, n.Schematic));
        var values = network.Values(n.Values);
        var poor = (n.Fit ?? NetworkFitSettings.Default).PoorRelativeError;
        foreach (var v in network.Variables)
        {
            var s = n.Variables.GetValueOrDefault(v.Name) ?? new NetworkVariableSettings();
            double value = values.TryGetValue(v.Name, out var x) ? x : v.Value;
            string unit = v.Kind switch
            {
                NetworkElementKind.Resistance => "Ω",
                NetworkElementKind.Inductance or NetworkElementKind.Mutual => "H",
                NetworkElementKind.Capacitance => "F",
                _ => "",
            };
            string text = $"{v.Name} = {(unit.Length > 0 ? ConservationCheck.Si(value, unit) : G(value))}";
            if (s.Lock) text += ", locked";
            else if (s.Tie is { } tie) text += $", tied to {tie.To}{(tie.Factor != 1 ? $" × {G(tie.Factor)}" : "")}";
            else if (n.Result?.Variables.GetValueOrDefault(v.Name) is { } r)
            {
                double rel = value != 0 && double.IsFinite(r.StdError) ? Math.Abs(r.StdError / value) : double.PositiveInfinity;
                text += double.IsFinite(r.StdError) ? $" ± {(unit.Length > 0 ? ConservationCheck.Si(r.StdError, unit) : G(r.StdError))}" : ", error not computable";
                // R-mc5-4: the multi-start spread beside the standard error (a single start has none).
                if (r.Spread > 0) text += $", spread {(unit.Length > 0 ? ConservationCheck.Si(r.Spread, unit) : G(r.Spread))} across the starts";
                if (r.AtBound is { } bound) text += $", at its {bound.ToLowerInvariant()} bound";
                if (rel > poor) text += ", poorly determined";
            }
            else if (s.Value is null) text += ", the schematic's value";
            yield return new ModelLine("networkValue", v.Name, text + ".");
        }
        if (n.Result is { } result)
            foreach (string line in result.Report) yield return new ModelLine("network", "", line);
    }

    // ── export (passive) ──────────────────────────────────────────────────────

    /// <summary>
    /// Records where a passive model is exported — cell <paramref name="cell"/> in the workspace or library folder
    /// <paramref name="library"/>, optionally with a <c>.csym</c> — and creates the cell (R-mc12-7). Refused, the document is
    /// as it was.
    /// </summary>
    public static PassiveExportResult ExportCell(string cmcPath, string library, string cell, string? symbol = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        string dir = Path.GetDirectoryName(cmcPath)!;
        byte[] original = File.ReadAllBytes(cmcPath);
        var doc = CmcFile.Read(cmcPath);
        string Rel(string p) => Path.GetRelativePath(dir, Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(dir, p))).Replace('\\', '/');
        var s = PassiveStep.Settings(doc);
        var updated = PassiveStep.WithSettings(doc, s with { Export = new PassiveExportSettings(cell, Rel(library), symbol is null ? null : Rel(symbol)) });
        CmcFile.Write(cmcPath, updated);
        try { return PassiveStep.ExportFile(cmcPath); }
        catch
        {
            AtomicWrite(cmcPath, original);
            throw;
        }
    }

    private static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
}
