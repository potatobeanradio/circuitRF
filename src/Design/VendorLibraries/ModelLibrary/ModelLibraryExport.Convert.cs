using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>One technology-bound part Export converts to an SnP (brief vendorlib-11 R-vl11-1).</summary>
/// <param name="CellDir">The cell whose schematic places it — the exported cell or one of its sub-cells.</param>
/// <param name="Instance">Its instance name.</param>
/// <param name="Kind">Its kind, which the converted SnP keeps as its glyph.</param>
/// <param name="Pins">Its terminal count: the SnP's port count.</param>
/// <param name="Parameters">Its parameters as written, for the provenance record.</param>
/// <param name="Technology">The technology it is sampled on, by name; null when none resolves.</param>
public sealed record ExportConversion(string CellDir, string Instance, SymbolKind Kind, int Pins, string Parameters,
                                      string? Technology)
{
    /// <summary>Its type, as the netlist writes it (<c>MLIN</c>).</summary>
    public string Type => ComponentTypeRegistry.EngineReference(Kind);

    /// <summary><c>TL1 (MLIN)</c>, with the cell named when it is a sub-cell's.</summary>
    public string Describe(string topCellDir)
        => DocumentFileRefs.PathComparer.Equals(CellDir, topCellDir)
            ? $"{Instance} ({Type})"
            : $"{Instance} ({Type}, in {Path.GetFileName(CellDir)})";
}

// Brief vendorlib-11: a model never carries a technology, so every part that takes its substrate from one is replaced at
// export by an SnP sampled on the author's technology with the circuit model a run uses — the part as its own n-port,
// a 50 Ω port from each terminal to ground. Exact at every sampled frequency; what lies between samples is the SnP's
// interpolation, which the self-check measures and reports.
public static partial class ModelLibraryExport
{
    /// <summary>R-vl11-4b: one linear segment, 0 Hz to 20 GHz, 1001 points.</summary>
    public static IReadOnlyList<FrequencySpec> DefaultSweep { get; } =
        [new FrequencySpec("0", "20", 1001, SweepKind.Linear, "GHz", "GHz")];

    /// <summary>
    /// The frequencies a plan samples: every segment's points, in order, each once, with 0 Hz added when the plan does
    /// not start there (R-vl11-2b). Null with the reason when the plan cannot be sampled.
    /// </summary>
    public static double[]? PlanFrequencies(IReadOnlyList<FrequencySpec> plan, out string? refusal)
    {
        refusal = null;
        var all = new SortedSet<double>();
        foreach (var segment in plan)
        {
            double[] f;
            try { f = segment.Expand(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { refusal = $"The frequency plan cannot be read ({ex.Message})."; return null; }
            foreach (double x in f)
            {
                if (!double.IsFinite(x) || x < 0)
                { refusal = $"The frequency plan holds {x.ToString("G6", CultureInfo.InvariantCulture)} Hz; every frequency is 0 Hz or above."; return null; }
                all.Add(x);
            }
        }
        if (all.Count == 0) { refusal = "The frequency plan holds no frequency."; return null; }
        all.Add(0);
        return [.. all];
    }

    /// <summary>
    /// R-vl11-4c's line: what Export will convert, or that nothing uses the technology.
    /// </summary>
    public static string ConversionLine(ExportPlan plan)
        => plan.Conversions.Count == 0
            ? "No component uses the technology."
            : $"{plan.Conversions.Count} component{(plan.Conversions.Count == 1 ? " uses" : "s use")} the technology and will be converted: "
              + string.Join(", ", plan.Conversions.Select(c => c.Describe(plan.CellDir))) + ".";

    /// <summary>A frequency in the unit a person reads it in, as the part's Frequency range field holds it.</summary>
    public static string FormatFrequency(double hz)
    {
        if (hz == 0) return "0 Hz";
        (double scale, string unit) = hz >= 1e9 ? (1e9, "GHz") : hz >= 1e6 ? (1e6, "MHz") : hz >= 1e3 ? (1e3, "kHz") : (1, "Hz");
        return (hz / scale).ToString("G10", CultureInfo.InvariantCulture) + " " + unit;
    }

    /// <summary>A plan as the provenance record states it: each segment as the <c>sparam</c> directive spells it.</summary>
    private static string DescribePlan(IReadOnlyList<FrequencySpec> plan)
        => string.Join("; ", plan.Select(s => s.Mode == FreqSpecMode.PointCount
            ? $"start={s.StartExpr} {s.StartUnit} stop={s.StopExpr} {s.StopUnit} npts={s.NumPoints}{(s.Kind == SweepKind.Log ? " log" : "")}"
            : $"start={s.StartExpr} {s.StartUnit} stop={s.StopExpr} {s.StopUnit} step={s.StepExpr} {s.StepUnit}{(s.Kind == SweepKind.Log ? " log" : "")}"))
           + "; DC always included";

    /// <summary>R-vl11-2c: a part's parameters as written — name, expression and unit — for the provenance record.</summary>
    internal static string ParametersAsWritten(EditableComponent comp)
        => string.Join(" ", comp.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Expression))
                                           .Select(p => $"{p.Name}={p.Expression.Trim()}{(string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit.Trim())}"));

    // ── The conversion ────────────────────────────────────────────────────────────────────────────

    private const string BenchCell = "__vl11_part";

    /// <summary>What converting wrote: each part's file, model-relative, keyed by its cell and instance; and the
    /// <c>Converted</c> entries.</summary>
    private sealed record Converted(
        Dictionary<(string Cell, string Instance), string> Files, List<ModelPartConversion> Records, List<string> Warnings);

    /// <summary>
    /// R-vl11-2a: each technology-bound part sampled as its own n-port — the source cell's extraction, which already
    /// carries the substrate the seams injected, with that one instance moved into a bench cell of its own that keeps
    /// the cell's parameters, so a W written as an expression of them evaluates as it does in the cell. Written to
    /// <c>models/&lt;instance&gt;.s&lt;N&gt;p</c>. Throws <see cref="ConversionRefused"/> with the sentence to report.
    /// </summary>
    private static Converted Convert(ExportPlan plan, ExportOptions options, FileCopier files, RunControl? control)
    {
        var result = new Converted(new(), [], []);
        if (plan.Conversions.Count == 0) return result;
        double[] freqs = plan.Frequencies;
        var sweep = options.Sweep ?? DefaultSweep;
        string planText = DescribePlan(sweep);

        control?.BeginStage("Converting technology parts", plan.Conversions.Count, "part(s)");
        foreach (var group in plan.Conversions.GroupBy(c => c.CellDir, DocumentFileRefs.PathComparer))
        {
            var extracted = NetExtractor.ExtractCell(group.Key, out string? why)
                            ?? throw new ConversionRefused(why ?? $"'{Path.GetFileName(group.Key)}' could not be extracted.");
            var lib = extracted.Library;
            var cell = lib.Find(extracted.CellName)!;
            string? schPath = PrimaryPath(group.Key, ViewType.Schematic);
            string? baseDir = SchematicCircuit.ReferenceBaseOfDirectory(schPath is null ? group.Key : Path.GetDirectoryName(schPath));

            foreach (var conv in group)
            {
                if (Cancelled(control)) throw new OperationCanceledException();
                var inst = cell.Instances.FirstOrDefault(i => i.InstanceName == conv.Instance)
                           ?? throw new ConversionRefused($"{conv.Describe(plan.CellDir)} is not in the cell's extracted netlist.");

                var bench = new Cell(BenchCell);
                for (int k = 1; k <= conv.Pins; k++) bench.Ports.Add($"t{k}");
                bench.Parameters.AddRange(cell.Parameters);
                bench.Variables.AddRange(cell.Variables);
                var nets = inst.NetBindings.Select((n, k) => k < conv.Pins ? $"t{k + 1}" : n).ToList();
                bench.Instances.Add(new Instance(inst.InstanceName, inst.Reference, nets, inst.Overrides));
                lib.Cells.Add(bench);
                string text;
                try { text = CnlWriter.Write(new TestBench("model"), lib, null); }
                finally { lib.Cells.Remove(bench); }

                var s = SamplePart(text, baseDir, conv, freqs, out var said);
                // What the part's model says about itself (a junction outside its calibration, say), once per part:
                // the conversion's run is the one that samples it, so it is the one that reports.
                foreach (string w in said)
                {
                    string line = $"{conv.Describe(plan.CellDir)}: {w}";
                    if (!result.Warnings.Contains(line)) result.Warnings.Add(line);
                }
                // The part is sampled ONCE, at the cell's default parameters, and the model still publishes them: a value
                // written in terms of one would silently stop following it once placed. Said, not refused — a default that
                // is the only value anyone places is a legitimate model.
                var cellParams = cell.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var o in inst.Overrides)
                    foreach (string used in System.Text.RegularExpressions.Regex.Matches(o.Expression ?? "", @"[A-Za-z_][A-Za-z0-9_]*")
                                                  .Select(m => m.Value).Where(cellParams.Contains).Distinct())
                    {
                        string line = $"{conv.Describe(plan.CellDir)}: its {o.Name} uses the cell parameter {used}. It was converted at "
                                    + $"{used}'s default, and placing the model with another {used} does not change this part.";
                        if (!result.Warnings.Contains(line)) result.Warnings.Add(line);
                    }
                string rel = WriteSnp(files, conv, plan, freqs, s, planText);
                result.Files[(Path.TrimEndingDirectorySeparator(Path.GetFullPath(conv.CellDir)), conv.Instance)] = rel;
                result.Records.Add(new ModelPartConversion
                {
                    Instance = conv.Instance, Cell = Path.GetFileName(conv.CellDir), Type = conv.Type, File = rel,
                    Parameters = conv.Parameters, Technology = conv.Technology ?? "", Frequency = planText,
                    Version = CmodelExport.Version,
                });
                control?.TickStage();
            }
        }
        return result;
    }

    /// <summary>An n-port's S (freq-major, then row-major) as Touchstone text with <paramref name="header"/> as its
    /// comment lines, at full double precision so a check compares the sampled numbers and not their rounding.</summary>
    internal static string TouchstoneText(int n, double[] freqs, Complex[] s, IEnumerable<string> header)
    {
        var matrices = new Mat<Complex>[freqs.Length];
        for (int f = 0; f < freqs.Length; f++)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) m[i, j] = s[f * n * n + i * n + j];
            matrices[f] = m;
        }
        var snp = new SNP(freqs, matrices, MatrixType.S, MatrixFormat.RI) { FreqUnit = FrequencyUnit.Hz };
        foreach (string line in header) snp.Comments.Add(new CommentEntry(-1, line));
        using var w = new StringWriter(CultureInfo.InvariantCulture);
        TouchstoneIO.Write(snp, w, MatrixFormat.RI, writeComments: true, precision: "G17");
        return w.ToString();
    }

    /// <summary>A conversion that cannot be made, with the sentence that says why.</summary>
    private sealed class ConversionRefused(string message) : Exception(message);

    /// <summary>
    /// The part's S at every frequency, freq-major then row-major, with the 0 Hz row replaced by what circuitRF's own DC
    /// engines make of the part (R-vl11-2b): a part that states its DC shorts (<see cref="IDcShortedTerminals"/>) is the
    /// ideal junction of each shorted group, open where a terminal is in none; any other part keeps the S-parameter
    /// analysis's own 0 Hz answer, which must be finite.
    /// </summary>
    private static Complex[] SamplePart(string cnlText, string? baseDir, ExportConversion conv, double[] freqs,
                                        out IReadOnlyList<string> warnings)
    {
        int n = conv.Pins;
        var benchText = new System.Text.StringBuilder(cnlText).Append('\n');
        for (int k = 1; k <= n; k++) benchText.Append($"Port:SCP{k} __sc_n{k} 0 Num={k} Z=50 Ohm\n");
        benchText.Append(BenchCell).Append(":SCX1");
        for (int k = 1; k <= n; k++) benchText.Append($" __sc_n{k}");
        benchText.Append('\n');

        Complex[] s;
        int[] portNode = new int[n];
        List<ElaboratedComponent> parts;
        try
        {
            var (lib, tb) = new CnlReader().Read(benchText.ToString(), "convert", baseDir);
            using var nl = new Elaborator(lib).Elaborate(tb);
            s = [.. SParameterEngine.Run(nl, freqs).Cubes["S"].ComplexValues];
            warnings = [.. nl.Warnings];
            for (int k = 0; k < n; k++) portNode[k] = nl.Nodes.TryGetIndex($"__sc_n{k + 1}", out int idx) ? idx : -1;
            parts = [.. nl.Components.Where(c => c.InstancePath.StartsWith("SCX1.", StringComparison.Ordinal))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ConversionRefused)
        {
            throw new ConversionRefused($"{conv.Instance} ({conv.Type}) could not be sampled: {ex.Message}");
        }

        if (parts.Count > 0 && parts.All(p => p.Model is IDcShortedTerminals))
        {
            var junction = DcJunction(parts, portNode);
            for (int i = 0; i < n * n; i++) s[i] = junction[i];
        }
        for (int i = 0; i < s.Length; i++)
            if (!double.IsFinite(s[i].Real) || !double.IsFinite(s[i].Imaginary))
                throw new ConversionRefused(i < n * n
                    ? $"{conv.Instance}: the {conv.Type} model gives no finite S-parameters at 0 Hz, so its DC row cannot be written."
                    : $"{conv.Instance}: the {conv.Type} model gives no finite S-parameters at {FormatHz(freqs[i / (n * n)])}.");
        return s;
    }

    /// <summary>The ideal junction of each group of terminals the parts short at DC, as an n×n S row-major: a group of m
    /// ports is 2/m − δ, a group that reaches ground is −1 on its diagonal, and a terminal in no group is open.</summary>
    private static Complex[] DcJunction(IReadOnlyList<ElaboratedComponent> parts, int[] portNode)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            if (!parent.TryGetValue(x, out int p)) return parent[x] = x;
            return p == x ? x : parent[x] = Find(p);
        }
        foreach (var part in parts)
            foreach (var (a, b) in ((IDcShortedTerminals)part.Model).DcShortedTerminals)
            {
                int ra = Find(part.Nodes[a]), rb = Find(part.Nodes[b]);
                if (ra != rb) parent[ra] = rb;
            }

        int n = portNode.Length;
        var s = new Complex[n * n];
        for (int k = 0; k < n; k++)
        {
            int root = Find(portNode[k]);
            if (root == Find(0)) { s[k * n + k] = -1; continue; }
            var group = Enumerable.Range(0, n).Where(j => Find(portNode[j]) == root).ToList();
            foreach (int j in group) s[k * n + j] = 2.0 / group.Count - (j == k ? 1 : 0);
        }
        return s;
    }

    /// <summary>The SnP file, with R-vl11-2c's provenance as its header, written at full double precision so the
    /// self-check's 1e-9 compares the sampled numbers and not their rounding. Returns its model-relative path.</summary>
    private static string WriteSnp(FileCopier files, ExportConversion conv, ExportPlan plan, double[] freqs, Complex[] s,
                                   string planText)
    {
        int n = conv.Pins;
        string text = TouchstoneText(n, freqs, s,
        [
            $"circuitRF Export to Library: {conv.Instance} ({conv.Type}) converted to S-parameters",
            $"Parameters: {conv.Parameters}",
            $"Technology: {conv.Technology}",
            $"Frequency plan: {planText}",
            $"circuitRF {CmodelExport.Version}",
        ]);
        string copied = files.CopyText($"{conv.Instance}.s{n}p", text, key: "vl11|" + conv.CellDir + "|" + conv.Instance);
        return Path.GetRelativePath(files.ModelDir, copied).Replace('\\', '/');
    }
}
