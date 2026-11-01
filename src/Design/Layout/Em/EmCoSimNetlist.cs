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

using System.Globalization;
using System.Text;
using CircuitRF.Core.Design;
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
}

/// <summary>What writing the co-simulation netlist did.</summary>
public sealed record EmCoSimResult(EmSnpSchematicOutcome Outcome, string? Path, string Sentence);

public static class EmCoSimNetlist
{
    /// <summary>What is appended to the setup's result key to name the netlist.</summary>
    public const string Suffix = "_cosim";

    /// <summary>The board's block's instance name.</summary>
    public const string BoardInstance = "BOARD";

    /// <summary>The netlist's file name: the Touchstone's own stem, as the <c>_sim</c> cell's, plus <see cref="Suffix"/>.</summary>
    public static string FileNameFor(EmSetup setup)
    {
        string sim = EmSnpSchematic.CellNameFor(setup);
        return sim[..^EmSnpSchematic.CellSuffix.Length] + Suffix + ".cnl";
    }

    /// <summary>The port node a result port is wired at.</summary>
    public static string Node(int number) => "p" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The netlist's text: the SnP of the run's Touchstone on nodes p1…pN, a Port line for every 3D view port and connector port,
    /// each part from its port's node to ground, and an S-parameter analysis over the setup's sweep. Pure: no file is read.
    /// </summary>
    /// <param name="snpStored">The Touchstone's path as the netlist states it (relative to the netlist's own folder).</param>
    public static string Text(EmSetup setup, string snpStored, IReadOnlyList<EmCoSimPort> ports, EmCoSimModels models)
    {
        var tb = new TestBench(setup.Name) { Technology = models.Technology };
        var comments = new List<string>();
        int n = ports.Count;
        tb.Instances.Add(new Instance(BoardInstance, "SnP", ports.Select(p => Node(p.Number)),
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
        tb.Analyses.Add(new SParameterAnalysis("SP1", setup.FrequencyPlan));

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
            return new EmCoSimModels(byName, extracted.Library, extracted.TestBench.Technology);
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
    public static EmCoSimResult Write(string workspaceDir, EmSetup setup, string snpPath, IReadOnlyList<Em3dPort> problemPorts,
                                      C3dDocument document, C3dComponentPortBuild build)
    {
        string path = Path.Combine(workspaceDir, FileNameFor(setup));
        try
        {
            var ports = PortsOf(problemPorts, document, build);
            var models = ModelsFor(build.Ports.Select(p => p.ClayPath).FirstOrDefault(c => c is not null));
            string stored = Path.GetRelativePath(workspaceDir, Path.GetFullPath(snpPath)).Replace('\\', '/');
            string text = Text(setup, stored, ports, models);
            if (File.Exists(path))
            {
                string old = File.ReadAllText(path);
                if (old == text)
                    return new(EmSnpSchematicOutcome.Current, path, $"{Path.GetFileName(path)} is already this run's co-simulation netlist.");
                if (!old.StartsWith(text[..text.IndexOf('\n')], StringComparison.Ordinal))
                    return new(EmSnpSchematicOutcome.LeftAlone, path,
                        $"{Path.GetFileName(path)} exists and was not written by this setup's runs, so it was left alone.");
            }
            AtomicFile.WriteAllText(path, text);
            return new(EmSnpSchematicOutcome.Created, path,
                $"Wrote {Path.GetFileName(path)}: the board's S{ports.Count}P with {build.Ports.Count(p => p.Ok && !p.Connector)} part " +
                "port(s) closed by their models, ready to simulate.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, null, $"No co-simulation netlist was written: {e.Message}");
        }
    }
}
