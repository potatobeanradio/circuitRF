// brief-emb-1 R-emb1-7 — the co-simulation netlist a finished driven run with component ports writes: the board's N-port with
// every part's model across its own port.
//
// WRITTEN, NOT AUTHORED. Once the ports exist the connection rule is mechanical, and it is the step most likely to be done
// wrong by hand: a GAP port measures the board with the part's pads left open, so the part goes from that port's node to
// GROUND — never between two port nodes, which would put it in series with the board's own port reference. A two-port part
// file measured in series is wired the same way, port 1 on the node and port 2 on ground, so its series impedance stands
// across the gap. A part with more pads (an amplifier) connects its model's terminals in pin order: each signal pin to its
// own port's node, its ground pin to ground. The board's connector ports and the 3D view's own ports are the circuit's Port
// lines. Each part's model is the board schematic's own instance (value, SnP file, vendor part); a part the schematic gives
// no model is a placeholder value, said in a comment and by `check`.
//
// brief-cosim-pattern-6 — the board is an EM Field Data block on the run's record when the run kept a far field or a field set, and
// an SnP otherwise. With a setup's Create schematic on, the same text is drawn as a <setup>_cosim cell someone can edit, by
// NetlistSchematic, the drawing `netlist --to-schematic` uses. An existing cell is never written to.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One port of the board's N-port, in the result's order.</summary>
/// <param name="Number">Its port number in the Touchstone.</param>
/// <param name="Name">The 3D view's port name, or the component port's (refdes or refdes.pin).</param>
/// <param name="Z0">Its reference impedance, as the document writes it.</param>
/// <param name="Component">The component port it is, or null for one of the 3D view's own ports.</param>
public sealed record EmCoSimPort(int Number, string Name, string Z0, C3dComponentPort? Component);

/// <summary>The board schematic's parts: each instance by name, and what a netlist of them needs alongside.</summary>
public sealed record EmCoSimModels(IReadOnlyDictionary<string, Instance> Instances, Library? Library, string? Technology)
{
    public static readonly EmCoSimModels None = new(new Dictionary<string, Instance>(), null, null);

    /// <summary>The parts the board's schematic marks Open (not fitted): each one's port stays open, with no model across it.</summary>
    public IReadOnlySet<string> Open { get; init; } = new HashSet<string>();

    /// <summary>The parts the board's schematic marks Short: each one's port is shorted.</summary>
    public IReadOnlySet<string> Shorted { get; init; } = new HashSet<string>();
}

/// <summary>What writing the co-simulation netlist did.</summary>
/// <param name="Text">The netlist this run makes, whether or not the file took it; null when it could not be formed.</param>
public sealed record EmCoSimResult(EmSnpSchematicOutcome Outcome, string? Path, string Sentence, string? Text = null);

/// <summary>A run's record, as the netlist states it: the run kept a far field or a field set.</summary>
/// <param name="Stored">The record's path as the netlist states it (relative to the netlist's own folder).</param>
/// <param name="SweepHz">The frequencies of the run's sweep.</param>
/// <param name="FarFieldHz">The frequencies its far field holds; empty when it kept only a field set.</param>
public sealed record EmCoSimRecord(string Stored, IReadOnlyList<double> SweepHz, IReadOnlyList<double> FarFieldHz)
{
    /// <summary>The far-field frequency nearest the middle of the sweep.</summary>
    public double NearestSweepCentreHz()
    {
        double centre = SweepHz.Count > 0 ? (SweepHz.Min() + SweepHz.Max()) / 2 : (FarFieldHz.Min() + FarFieldHz.Max()) / 2;
        return FarFieldHz.MinBy(f => Math.Abs(f - centre));
    }
}

public static class EmCoSimNetlist
{
    /// <summary>What is appended to the setup's result key to name the netlist.</summary>
    public const string Suffix = "_cosim";

    /// <summary>The board's block's instance name.</summary>
    public const string BoardInstance = "BOARD";

    /// <summary>The Fields analysis's name.</summary>
    public const string FieldsAnalysisName = "F1";

    /// <summary>The cell the netlist is drawn as: the Touchstone's own stem, as the <c>_sim</c> cell's, plus <see cref="Suffix"/>.</summary>
    public static string CellNameFor(EmSetup setup)
    {
        string sim = EmSnpSchematic.CellNameFor(setup);
        return sim[..^EmSnpSchematic.CellSuffix.Length] + Suffix;
    }

    /// <summary>The netlist's file name: <see cref="CellNameFor"/> as a <c>.cnl</c>.</summary>
    public static string FileNameFor(EmSetup setup) => CellNameFor(setup) + ".cnl";

    /// <summary>The port node a result port is wired at.</summary>
    public static string Node(int number) => "p" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The netlist's text: the run's block on nodes p1…pN, a Port line for every 3D view port and connector port, each part from its
    /// port's node to ground, an S-parameter analysis over the setup's sweep, and with a far field a Fields analysis. Pure: no file is read.
    /// </summary>
    /// <param name="snpStored">The Touchstone's path as the netlist states it (relative to the netlist's own folder).</param>
    /// <param name="record">The run's record when it kept a far field or a field set, which makes the block EM Field Data; null makes it an SnP.</param>
    public static string Text(EmSetup setup, string snpStored, IReadOnlyList<EmCoSimPort> ports, EmCoSimModels models, EmCoSimRecord? record = null)
    {
        var tb = new TestBench(setup.Name) { Technology = models.Technology };
        var comments = new List<string>();
        int n = ports.Count;
        tb.Instances.Add(record is not null
            ? new Instance(BoardInstance, EmFieldDataRecord.TypeName, ports.Select(p => Node(p.Number)), [new ParameterAssignment("File", record.Stored)])
            : new Instance(BoardInstance, "SnP", ports.Select(p => Node(p.Number)),
                [new ParameterAssignment("NumPorts", n.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("File", snpStored)]));

        int portLine = 0;
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ports)
        {
            if (p.Component is not { } cp || cp.Connector)
            {
                portLine++;
                tb.Instances.Add(new Instance("P" + portLine.ToString(CultureInfo.InvariantCulture), "Port", [Node(p.Number), "0"],
                    [new ParameterAssignment("Num", portLine.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("Z", Z(p.Z0), "Ohm")]));
                continue;
            }
            if (!parts.Add(cp.Refdes)) continue;      // a multi-pad part: placed once, at its first port
            var mine = ports.Where(q => q.Component is { } c && c.Refdes.Equals(cp.Refdes, StringComparison.OrdinalIgnoreCase)).ToList();
            var model = models.Instances.GetValueOrDefault(cp.SchematicId ?? cp.Refdes) ?? models.Instances.GetValueOrDefault(cp.Refdes);
            if (mine.Count == 1 && model is not null && TwoTerminal(model))
            {
                tb.Instances.Add(Rewired(model, [Node(p.Number), "0"]));
                continue;
            }
            if (model is not null && mine.Count > 1 && model.NetBindings.Count == cp.PartPins.Count)
            {
                // Its terminals in pin order: a signal pin on its port's node, its ground pin (each port's reference) on ground.
                var nets = cp.PartPins.Select(pin =>
                    mine.FirstOrDefault(q => q.Component!.Pin == pin) is { } own ? Node(own.Number)
                    : mine.Any(q => q.Component!.ReferencePin == pin) ? "0"
                    : $"nc_{cp.Refdes}_{pin}").ToList();
                tb.Instances.Add(Rewired(model, nets));
                continue;
            }
            // Marked in the schematic as not fitted, or as a link: the part is no value to guess at.
            if (mine.Count == 1 && models.Open.Contains(cp.SchematicId ?? cp.Refdes))
            {
                comments.Add($"{cp.Refdes} is Open in the board's schematic, so its port {Node(p.Number)} is left open.");
                continue;
            }
            if (mine.Count == 1 && models.Shorted.Contains(cp.SchematicId ?? cp.Refdes))
            {
                tb.Instances.Add(new Instance(cp.Refdes, "Short", [Node(p.Number), "0"], []));
                comments.Add($"{cp.Refdes} is Short in the board's schematic, so its port {Node(p.Number)} is shorted.");
                continue;
            }
            // No model, or one this wiring cannot read: a placeholder per port, named in a comment.
            string why = model is null ? "the board's schematic gives no model for it"
                       : $"its model has {model.NetBindings.Count} terminal(s) and its footprint {cp.PartPins.Count} pad(s)";
            foreach (var q in mine)
            {
                var (kind, key, value, unit) = Placeholder(cp.Refdes);
                string name = mine.Count == 1 ? cp.Refdes : $"{cp.Refdes}_{q.Component!.Pin}";
                string variable = name + "_value";
                tb.GlobalVariables.Add(new Variable(variable, value, unit));
                tb.Instances.Add(new Instance(name, kind, [Node(q.Number), "0"], [new ParameterAssignment(key, variable)]));
                comments.Add($"{name}: {why}, so {variable} is a placeholder; set it to the part's value.");
            }
        }
        tb.Analyses.Add(new SParameterAnalysis(EmSnpSchematic.AnalysisName, setup.FrequencyPlan));
        if (record is { FarFieldHz.Count: > 0 })
        {
            if (portLine > 0)
            {
                double hz = record.NearestSweepCentreHz();
                var (value, unit) = InUnit(hz);
                tb.Analyses.Add(new FieldsAnalysis(FieldsAnalysisName) { Block = BoardInstance, FreqExpr = value, FreqUnit = unit, DriveExpr = "1" });
                comments.Add($"{FieldsAnalysisName} is at {MaterialDispersion.Hz(hz)}, the stored frequency nearest the sweep's centre. The result holds a pattern at " +
                             $"{string.Join(", ", record.FarFieldHz.Select(MaterialDispersion.Hz))}, and only there; freq=all takes every one.");
            }
            else comments.Add("There is no Fields analysis: no port in this circuit can drive it, so give one a Port line first.");
        }
        comments.Insert(0, $"{BoardInstance}'s ports: {string.Join(", ", ports.Select(p => $"{Node(p.Number)} is {p.Name}"))}.");

        var sb = new StringBuilder();
        sb.AppendLine($"; Co-simulation of '{setup.Name}': the board's {n}-port with each part across its own component port (written by");
        sb.AppendLine("; circuitRF from the run). A gap port measures the board with the part's pads open, so each part stands from its");
        sb.AppendLine("; port's node to ground, never between two port nodes.");
        foreach (string c in comments) sb.AppendLine("; " + c);
        sb.AppendLine();
        bool cells = models.Library is { Cells.Count: > 0 } lib && tb.Instances.Any(i => lib.Find(i.Reference) is not null);
        sb.Append(CnlWriter.Write(tb, cells ? models.Library : null));
        return sb.ToString();
    }

    /// <summary>A part whose model is two-terminal: two nets, or an SnP of two ports (a two-port measured in series).</summary>
    private static bool TwoTerminal(Instance model) => model.NetBindings.Count == 2;

    private static Instance Rewired(Instance model, IReadOnlyList<string> nets)
        => new(model.InstanceName, model.Reference, nets, model.Overrides);

    private static (string Kind, string Key, string Value, string Unit) Placeholder(string refdes)
        => char.ToUpperInvariant(refdes.FirstOrDefault()) switch
        {
            'C' => ("C", "C", "1", "pF"),
            'L' => ("L", "L", "1", "nH"),
            'R' => ("R", "R", "50", "Ohm"),
            _   => ("R", "R", "1e9", "Ohm"),
        };

    private static string Z(string z0) => z0.Trim().Length > 0 ? z0.Trim() : "50";

    private static (string Value, string Unit) InUnit(double hz)
    {
        string unit = hz >= 1e9 ? "GHz" : hz >= 1e6 ? "MHz" : hz >= 1e3 ? "kHz" : "Hz";
        return ((hz / FreqUnit.Multiplier(unit)).ToString("G15", CultureInfo.InvariantCulture), unit);
    }

    // ── Writing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The result's ports, from the problem the run solved: each problem port by its document number (a renumbered one by its
    /// source number), matched to the component port of that number when there is one.
    /// </summary>
    public static IReadOnlyList<EmCoSimPort> PortsOf(IReadOnlyList<Em3dPort> problemPorts, C3dDocument document, C3dComponentPortBuild build)
    {
        var list = new List<EmCoSimPort>();
        foreach (var p in problemPorts.OrderBy(p => p.Number))
        {
            int source = p.SourceNumber ?? p.Number;
            var cp = build.Ports.FirstOrDefault(c => c.Port?.Number == source);
            var own = C3dPorts.OwnerOf(document, source);
            string name = cp?.Port?.Name ?? (own is not null ? C3dPorts.Label(own) : $"port {source}");
            string z0 = cp?.Port?.Z0 ?? own?.Z0 ?? "50";
            list.Add(new EmCoSimPort(p.Number, name, z0, cp));
        }
        return list;
    }

    /// <summary>
    /// The board schematic beside <paramref name="clayPath"/>, extracted as the board projection reads it: every instance by
    /// name. <see cref="EmCoSimModels.None"/> when there is none or it does not extract.
    /// </summary>
    public static EmCoSimModels ModelsFor(string? clayPath)
    {
        if (clayPath is not { Length: > 0 }) return EmCoSimModels.None;
        string layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath)) ?? "";
        string cellDir = Path.GetDirectoryName(layoutDir) ?? layoutDir;
        if (!Directory.Exists(cellDir)) return EmCoSimModels.None;
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.ResolvedName is not { Length: > 0 } name) return EmCoSimModels.None;
        string sch = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        try
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(sch);
            var extracted = NetExtractor.Extract(model, "tb", DiskCellResolver.Instance);
            if (extracted.Conflicts.Count > 0) return EmCoSimModels.None;
            var byName = new Dictionary<string, Instance>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in extracted.TestBench.Instances)
                if (inst.InstanceName is { Length: > 0 } n) byName.TryAdd(n, inst);
            HashSet<string> Marked(DisableState state) =>
                new(model.Components.Where(c => c.Disable == state && c.InstanceName is { Length: > 0 }).Select(c => c.InstanceName!), StringComparer.OrdinalIgnoreCase);
            return new EmCoSimModels(byName, extracted.Library, extracted.TestBench.Technology)
            {
                Open = Marked(DisableState.Open),
                Shorted = Marked(DisableState.Short),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return EmCoSimModels.None;
        }
    }

    /// <summary>
    /// Writes <see cref="FileNameFor"/> in <paramref name="workspaceDir"/> for the run's Touchstone at <paramref name="snpPath"/>.
    /// An existing file is replaced only when it is one this writer made for the same Touchstone (its first line); any other is
    /// left alone and said. Never throws.
    /// </summary>
    /// <param name="recordPath">The run's result record; null when it wrote none.</param>
    public static EmCoSimResult Write(string workspaceDir, EmSetup setup, string snpPath, string? recordPath, IReadOnlyList<Em3dPort> problemPorts,
                                      C3dDocument document, C3dComponentPortBuild build)
    {
        string path = Path.Combine(workspaceDir, FileNameFor(setup));
        try
        {
            var ports = PortsOf(problemPorts, document, build);
            var models = ModelsFor(build.Ports.Select(p => p.ClayPath).FirstOrDefault(c => c is not null));
            string stored = Path.GetRelativePath(workspaceDir, Path.GetFullPath(snpPath)).Replace('\\', '/');
            string text = Text(setup, stored, ports, models, RecordFor(recordPath, workspaceDir));
            if (File.Exists(path))
            {
                string old = File.ReadAllText(path);
                if (old == text)
                    return new(EmSnpSchematicOutcome.Current, path, $"{Path.GetFileName(path)} is already this run's co-simulation netlist.", text);
                if (!old.StartsWith(text[..text.IndexOf('\n')], StringComparison.Ordinal))
                    return new(EmSnpSchematicOutcome.LeftAlone, path,
                        $"{Path.GetFileName(path)} exists and was not written by this setup's runs, so it was left alone.", text);
            }
            AtomicFile.WriteAllText(path, text);
            return new(EmSnpSchematicOutcome.Created, path,
                $"Wrote {Path.GetFileName(path)}: the board's S{ports.Count}P with {build.Ports.Count(p => p.Ok && !p.Connector)} part " +
                "port(s) closed by their models, ready to simulate.", text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, null, $"No co-simulation netlist was written: {e.Message}");
        }
    }

    /// <summary>R-cp6-1 — the record as the netlist states it, when the run kept a far field or a field set; null otherwise.</summary>
    public static EmCoSimRecord? RecordFor(string? recordPath, string netlistDir)
    {
        if (recordPath is null || EmResultRecord.Load(recordPath).Record is not { } record) return null;
        if (record.Frequencies.FarFieldHz.Count == 0 && record.Frequencies.FieldSetHz.Count == 0) return null;
        return new EmCoSimRecord(Path.GetRelativePath(netlistDir, Path.GetFullPath(recordPath)).Replace('\\', '/'),
                                 record.Frequencies.SweepHz, record.Frequencies.FarFieldHz);
    }

    // ── The cell ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-cp6-2 — <see cref="CellNameFor"/> in <paramref name="workspaceDir"/>, drawn from the netlist's text by
    /// <see cref="NetlistSchematic.Build"/>, with the text's comments as notes on the sheet and a Data Display. An existing cell is
    /// never written to, whatever it holds. Never throws: a failure is a <see cref="EmSnpSchematicOutcome.NotCreated"/> carrying the reason.
    /// </summary>
    /// <param name="snpPath">The run's Touchstone, what an existing cell's block may read.</param>
    /// <param name="recordPath">The run's result record, what an existing cell's block may read; null when it wrote none.</param>
    public static EmSnpSchematicResult CreateCell(string workspaceDir, EmSetup setup, string text, string snpPath, string? recordPath)
    {
        string cellName = CellNameFor(setup);
        try
        {
            if (NameValidator.Validate(cellName) is { } bad)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: '{cellName}' cannot be a cell name. {bad}");

            string cellDir = Path.Combine(workspaceDir, cellName);
            if (Directory.Exists(cellDir) || File.Exists(cellDir)) return ExistingCell(workspaceDir, cellDir, cellName, snpPath, recordPath);

            var (lib, tb) = new CnlReader().Read(text, cellName, workspaceDir);
            var drawn = NetlistSchematic.Build(lib, tb, CellFolder.SubFolderPath(cellDir, ViewType.Schematic));
            if (drawn.Schematic is not { } model)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                           $"No schematic was created for this run: {string.Join(" ", drawn.Refusals)} {cellName}.cnl states the same circuit.");
            AddNotes(model, text);

            string schematic = CellCreate.Create(workspaceDir, cellName, CellViews.Schematic, model).SchematicPath!;
            int circuitPorts = tb.Instances.Count(i => i.Reference.Equals("Port", StringComparison.OrdinalIgnoreCase));
            if (circuitPorts > 0)
                CellCreate.WriteTemplateDataDisplay(workspaceDir, schematic, null, tb.Analyses.Any(a => a is FieldsAnalysis)
                    ? PatternDisplayJson("em_cosim.npy", tb.Analyses.OfType<SParameterAnalysis>().Any(HasSweep))
                    : EmSnpSchematic.DataDisplayJson(circuitPorts, "em_cosim.npy"));
            return new(EmSnpSchematicOutcome.Created, cellName, schematic,
                       $"Created cell {cellName}: the board's result with each part on its own port, ready to simulate.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                      or FormatException or ArgumentException or CnlReadException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: {e.Message}");
        }
    }

    /// <summary>A cell of that name exists. Reading it never changes it: the only question is which sentence the run says.</summary>
    private static EmSnpSchematicResult ExistingCell(string workspaceDir, string cellDir, string cellName, string snpPath, string? recordPath)
    {
        string? schematic = null;
        if (Directory.Exists(cellDir) && CellFolder.ResolvePrimary(cellDir, ViewType.Schematic) is { ResolvedName: { Length: > 0 } name })
            schematic = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        if (schematic is null || !File.Exists(schematic)) return Left(null);

        var (model, _, _) = SchematicPersistence.LoadFromFile(schematic);
        string[] targets = recordPath is null ? [Full(snpPath)] : [Full(snpPath), Full(recordPath)];
        foreach (var c in model.Components.Where(c => c.Symbol is SymbolKind.Snp or SymbolKind.EmFieldData))
            if (SnpPathPolicy.Resolve(c.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression.Trim('"'), workspaceDir, Path.GetDirectoryName(schematic)) is { } read
                && targets.Contains(Full(read), StringComparer.OrdinalIgnoreCase))
                return new(EmSnpSchematicOutcome.Current, cellName, schematic, $"Cell {cellName} already reads this result, so it was left as it is.");
        return Left(schematic);

        EmSnpSchematicResult Left(string? path) => new(EmSnpSchematicOutcome.LeftAlone, cellName, path,
            $"A cell named {cellName} already exists and does not read this result, so it was left alone and no schematic was created.");
        static string Full(string p) => Path.GetFullPath(p).Replace('\\', '/');
    }

    /// <summary>The netlist's comments as notes above the drawing: what they say about a placeholder or a frequency is as true of the cell.</summary>
    private static void AddNotes(SchematicEditModel model, string text)
    {
        string[] notes = [.. text.Split('\n').Select(l => l.Trim()).TakeWhile(l => l.StartsWith(';')).Select(l => l.TrimStart(';').Trim()).Where(l => l.Length > 0)];
        double left = model.Components.Min(c => c.X) - 200, top = model.Components.Min(c => c.Y) - 300;
        const double Pitch = 90;
        for (int i = 0; i < notes.Length; i++)
            model.CanvasObjects.Add(new EditableText { Text = notes[i], X = left, Y = top - (notes.Length - i) * Pitch });
    }

    /// <summary>The S-parameter analysis covers a band: a run of one frequency has no curve to draw, and its S11 is in the table.</summary>
    private static bool HasSweep(SParameterAnalysis a) => a.Sweeps.Count > 1 || a.Sweeps.Any(s => s.StartExpr != s.StopExpr);

    /// <summary>
    /// R-cp6-3 — the display a cell with a far field opens: S11, polar cuts of the pattern through both planes, and a table of S11 and the
    /// gains. <paramref name="source"/> is a placeholder that <see cref="CellCreate.WriteTemplateDataDisplay"/> repoints to the file the
    /// schematic's run writes.
    /// </summary>
    /// <param name="sweep">False for a run of one frequency, which gets no S11 curve.</param>
    public static string PatternDisplayJson(string source, bool sweep = true)
    {
        string f = FieldsAnalysisName;
        var plots = new JsonArray();
        double tableTop = 440;
        if (sweep)
        {
            var match = Plot("Input match", 0, 0, 520, 320, "Rect", EmSnpSchematic.Trace(source, 1, 1, 0, EmSnpSchematic.AnalysisName + ".S"));
            match["CustomYLabel"] = "dB";
            match["CustomYLabelOn"] = true;
            plots.Add(match);
            tableTop = 340;
        }
        double cutLeft = sweep ? 540 : 0;
        plots.Add(Cut(cutLeft, 0));
        plots.Add(Cut(cutLeft + 430, 90));
        // The S11 column is the Fields analysis's own, so it is the value at the pattern's frequency.
        var gain = Plot(null, 0, tableTop, sweep ? 520 : 850, 100, "Table",
            CubeTrace(source, $"{f}.farfield.RealizedGainDbi", "None", "Realized gain (dBi)", FreqX(), Port()));
        var rows = (JsonArray)gain["Traces"]!;
        rows.Insert(0, CubeTrace(source, $"{f}.S", "dB20", "S11 (dB)", FreqX(),
            EmSnpSchematic.Slice("i", "PinToIndex", 0), EmSnpSchematic.Slice("j", "PinToIndex", 0)));
        rows.Add(CubeTrace(source, $"{f}.farfield.GainDbi", "None", "Gain (dBi)", FreqX(), Port()));
        rows.Add(CubeTrace(source, $"{f}.farfield.DirectivityDbi", "None", "Directivity (dBi)", FreqX(), Port()));
        foreach (var row in rows) ((JsonObject)row!)["ColumnWidth"] = 150;
        plots.Add(gain);
        var root = new JsonObject
        {
            ["FormatVersion"] = 2,
            ["SelectedDataSource"] = source,
            ["SourceAliases"] = new JsonObject(),
            ["Tabs"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Co-simulation", ["ZoomLevel"] = 1, ["ViewOffsetX"] = 0, ["ViewOffsetY"] = 0, ["Plots"] = plots,
            }),
            ["ActiveTabIndex"] = 0,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        static JsonObject FreqX() => EmSnpSchematic.Slice("freq", "KeepAsX", 0);
        static JsonObject Port() => EmSnpSchematic.Slice("port", "PinToIndex", 0);

        // One cut through a pole, at a φ on the pattern's 1° grid. A board's long axis decides which of the two shows the null, so both are drawn.
        JsonObject Cut(double left, int phiDeg)
        {
            var cut = Plot($"phi = {phiDeg}", left, 0, 420, 420, "Polar", CubeTrace(source, $"{f}.farfield.U", "dB10", "dB(U)",
                EmSnpSchematic.Slice("freq", "PinToIndex", 0), EmSnpSchematic.Slice("theta", "KeepAsX", 0),
                EmSnpSchematic.Slice("phi", "PinToIndex", phiDeg), Port()));
            cut["PolarRadial"] = "Db";
            // U here is per volt at the drive, which nobody reads: the shape is the point, and the outer ring is the plot's own peak.
            cut["PolarDbUnit"] = "dB re peak";
            ((JsonObject)((JsonArray)cut["Traces"]!)[0]!)["PatternWholePlane"] = true;
            return cut;
        }

        // A table has no title, so a title is given to everything else.
        static JsonObject Plot(string? title, double left, double top, double width, double height, string type, JsonNode trace) => new()
        {
            ["Left"] = left, ["Top"] = top, ["Width"] = width, ["Height"] = height, ["PlotType"] = type, ["FreqUnit"] = "GHz",
            ["CustomTitle"] = title ?? "", ["CustomTitleOn"] = title is not null, ["Traces"] = new JsonArray(trace),
        };

        static JsonNode CubeTrace(string source, string cube, string transform, string label, params JsonObject[] slices) => new JsonObject
        {
            ["SourcePath"] = source, ["CubeName"] = cube, ["CubeSlice"] = new JsonArray(slices), ["CubeTransform"] = transform,
            ["Label"] = label, ["Properties"] = EmSnpSchematic.TraceProperties(0),
        };
    }
}
