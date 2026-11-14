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
//
// brief-lcp-7 — the writer reads ports (EmCoSimPort, each component port's part as an EmCoSimPart), not a 3D view and its build, so
// a layout's Planar run writes the same netlist from its strips that a .c3d writes from its sheets. A Planar component port is a
// delta gap across the part's pads, the same open-pad measurement as a 3D sheet, so the wiring rule above holds for both.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>One port of the board's N-port, in the result's order.</summary>
/// <param name="Number">Its port number in the Touchstone.</param>
/// <param name="Name">The 3D view's port name, or the component port's (refdes or refdes.pin).</param>
/// <param name="Z0">Its reference impedance, as the document writes it.</param>
/// <param name="Component">The component port it is, or null for one of the 3D view's or the layout's own ports.</param>
/// <param name="DocumentNumber">brief-isc-1 D6 — the number the document gives the port: a Planar label's text, a 3D port's source
/// number. A schematic Port of that <c>Num</c> is the one it joins. Null reads <paramref name="Number"/>.</param>
public sealed record EmCoSimPort(int Number, string Name, string Z0, EmCoSimPart? Component, int? DocumentNumber = null)
{
    /// <summary>The number a schematic Port matches: <see cref="DocumentNumber"/>, else the Touchstone's.</summary>
    public int Num => DocumentNumber ?? Number;
}

/// <summary>brief-isc-1 — every port a run solved, and whether any of them is a built component port (what makes the run write
/// its <c>_cosim</c> netlist).</summary>
public sealed record EmResultPorts(IReadOnlyList<EmCoSimPort> Ports, bool ComponentPorts);

/// <summary>brief-isc-1 R-isc-2 — what closes one result port, as the co-simulation netlist and the cell made from the schematic
/// both read it: there is one decision, made by <see cref="EmCoSimNetlist.Closures"/>.</summary>
public enum EmCoSimCloseKind
{
    /// <summary>The port is a Port of the circuit: a drawn port, a 3D view's port or a connector's.</summary>
    PortLine,
    /// <summary>The part's model from the schematic, its terminals on the port's node, ground or nothing.</summary>
    Model,
    /// <summary>The part is Open in the schematic: its port stays open.</summary>
    Open,
    /// <summary>The part is Short in the schematic: its port is shorted.</summary>
    Short,
    /// <summary>No model that this wiring can read: a placeholder value across the port.</summary>
    Placeholder,
}

/// <param name="Kind">What closes the port.</param>
/// <param name="Port">The port (for a multi-pad part, its first).</param>
/// <param name="Refdes">The part, for every kind but <see cref="EmCoSimCloseKind.PortLine"/>.</param>
/// <param name="Instance">The instance the netlist places: the rewired model, a Short, or a placeholder.</param>
/// <param name="Variable">A placeholder's value.</param>
/// <param name="Comment">What the netlist says about it.</param>
/// <param name="SchematicName">The schematic component the part is, when the schematic has one.</param>
public sealed record EmCoSimClose(EmCoSimCloseKind Kind, EmCoSimPort Port, string? Refdes = null, Instance? Instance = null,
                                  Variable? Variable = null, string? Comment = null, string? SchematicName = null)
{
    /// <summary>brief-icp-5 — what a run warns about it: a cut instance's terminal left open, or its placeholder.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// brief-lcp-7 R-lcp7-1 — what the netlist reads of one component port: the part, its two pads, every pin of its footprint in pin
/// order, where its model is read from and its nets. A <c>.c3d</c>'s sheet and a layout's Planar strip both give it, so the writer
/// takes neither builder's type.
/// </summary>
/// <param name="Refdes">The part.</param>
/// <param name="Pin">The positive pad's pin.</param>
/// <param name="ReferencePin">The negative pad's pin.</param>
/// <param name="PartPins">Every pin of the part, in its footprint's pin order: a multi-pad part's model terminals map onto them.</param>
/// <param name="SchematicId">The placement's <c>SchematicId</c>: the board schematic's instance the part's model is read from.</param>
/// <param name="ClayPath">The layout the part is placed on, beside which the board's schematic is looked for.</param>
/// <param name="Nets">The nets of its two pads, positive first, where the board states them.</param>
/// <param name="Connector">A connector: its port is a Port line of the circuit, not a part across it.</param>
public sealed record EmCoSimPart(string Refdes, string? Pin, string? ReferencePin, IReadOnlyList<string> PartPins, string? SchematicId,
                                 string? ClayPath, (string? Positive, string? Negative) Nets, bool Connector)
{
    /// <summary>A <c>.c3d</c>'s component port.</summary>
    public static EmCoSimPart Of(C3dComponentPort p)
        => new(p.Refdes, p.Pin, p.ReferencePin, p.PartPins, p.SchematicId, p.ClayPath, p.Nets, p.Connector) { Cut = p.CutPart };

    /// <summary>A layout's Planar component port.</summary>
    public static EmCoSimPart Of(EmPlanarComponentPort p)
        => new(p.Refdes, p.Pin, p.ReferencePin, p.PartPins, p.SchematicId, p.ClayPath, p.Nets, p.Connector) { Cut = p.CutPart };

    /// <summary>brief-icp-5 R-icp5-1 — a cut instance's pin port: the instance, its schematic chain and every pin by name, with
    /// <see cref="Refdes"/> its path and <see cref="Pin"/> the pin. Null for a footprint part.</summary>
    public EmCoSimCut? Cut { get; init; }
}

/// <summary>The board schematic's parts: each instance by name, and what a netlist of them needs alongside.</summary>
public sealed record EmCoSimModels(IReadOnlyDictionary<string, Instance> Instances, Library? Library, string? Technology)
{
    public static readonly EmCoSimModels None = new(new Dictionary<string, Instance>(), null, null);

    /// <summary>The parts the board's schematic marks Open (not fitted): each one's port stays open, with no model across it.</summary>
    public IReadOnlySet<string> Open { get; init; } = new HashSet<string>();

    /// <summary>The parts the board's schematic marks Short: each one's port is shorted.</summary>
    public IReadOnlySet<string> Shorted { get; init; } = new HashSet<string>();

    /// <summary>The board schematic's own variables — its VARs and its cell parameters at their defaults, as its extraction
    /// states them. A part whose value is an expression (<c>C=Cshunt</c>) reads them, so the netlist carries them.</summary>
    public IReadOnlyList<Variable> Variables { get; init; } = [];

    /// <summary>brief-icp-5 R-icp5-3 — each instance's terminal names in net order, from its symbol's pin names, where the symbol
    /// names them: what a cut instance's pins are matched to.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> PinNames { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>What writing the co-simulation netlist did.</summary>
/// <param name="Text">The netlist this run makes, whether or not the file took it; null when it could not be formed.</param>
public sealed record EmCoSimResult(EmSnpSchematicOutcome Outcome, string? Path, string Sentence, string? Text = null)
{
    /// <summary>brief-icp-5 — what the netlist warns about a cut instance (a terminal left open, a placeholder).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

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

    /// <summary>brief-icp-5 R-icp5-5 — the run's cell must be drawn from the netlist rather than from the top schematic: a cut instance
    /// is nested (its model is in a sub-cell's schematic) or abuts another (its node is not a net of the schematic).</summary>
    public static bool DrawnFromNetlist(IReadOnlyList<EmCoSimPort> ports)
        => ports.Any(p => p.Component?.Cut is { } c && (c.Nested || c.SchematicChain.Count != 1 || c.Pins.Any(q => q.Class == EmPinClass.Abutting)));

    /// <summary>The port node a result port is wired at.</summary>
    public static string Node(int number) => "p" + number.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The netlist's text: the run's block on nodes p1…pN, a Port line for every 3D view port and connector port, each part from its
    /// port's node to ground, an S-parameter analysis over the setup's sweep, and with a far field a Fields analysis. Pure: no file is read.
    /// </summary>
    /// <param name="snpStored">The Touchstone's path as the netlist states it (relative to the netlist's own folder).</param>
    /// <param name="record">The run's record when it kept a far field or a field set, which makes the block EM Field Data; null makes it an SnP.</param>
    /// <param name="layoutModels">brief-icp-5 — the models for a cut instance placed on another layout than the first (a 3D view
    /// placing several); null reads <paramref name="models"/> for every part.</param>
    public static string Text(EmSetup setup, string snpStored, IReadOnlyList<EmCoSimPort> ports, EmCoSimModels models, EmCoSimRecord? record = null,
                              Func<string?, EmCoSimModels>? layoutModels = null)
    {
        var tb = new TestBench(setup.Name) { Technology = models.Technology };
        // A part's value may be an expression of the board's variables; without them the circuit names what it never defines.
        foreach (var v in models.Variables) tb.GlobalVariables.Add(v);
        var comments = new List<string>();
        int n = ports.Count;
        tb.Instances.Add(record is not null
            ? new Instance(BoardInstance, EmFieldDataRecord.TypeName, ports.Select(p => Node(p.Number)), [new ParameterAssignment("File", record.Stored)])
            : new Instance(BoardInstance, "SnP", ports.Select(p => Node(p.Number)),
                [new ParameterAssignment("NumPorts", n.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("File", snpStored)]));

        int portLine = 0;
        foreach (var c in Closures(ports, models, layoutModels))
            switch (c.Kind)
            {
                case EmCoSimCloseKind.PortLine:
                    portLine++;
                    tb.Instances.Add(new Instance("P" + portLine.ToString(CultureInfo.InvariantCulture), "Port", [Node(c.Port.Number), "0"],
                        [new ParameterAssignment("Num", portLine.ToString(CultureInfo.InvariantCulture)), new ParameterAssignment("Z", Z(c.Port.Z0), "Ohm")]));
                    break;
                case EmCoSimCloseKind.Model:
                    tb.Instances.Add(c.Instance!);
                    if (c.Comment is not null) comments.Add(c.Comment);
                    break;
                case EmCoSimCloseKind.Open:
                    comments.Add(c.Comment!);
                    break;
                case EmCoSimCloseKind.Short:
                    tb.Instances.Add(c.Instance!);
                    comments.Add(c.Comment!);
                    break;
                case EmCoSimCloseKind.Placeholder:
                    tb.GlobalVariables.Add(c.Variable!);
                    tb.Instances.Add(c.Instance!);
                    comments.Add(c.Comment!);
                    break;
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
        sb.AppendLine($"{OwnerPrefix(setup)}{n}-port with each part across its own component port (written by");
        sb.AppendLine("; circuitRF from the run). A gap port measures the board with the part's pads open, so each part stands from its");
        sb.AppendLine("; port's node to ground, never between two port nodes.");
        foreach (string c in comments) sb.AppendLine("; " + c);
        sb.AppendLine();
        bool cells = models.Library is { Cells.Count: > 0 } lib && tb.Instances.Any(i => lib.Find(i.Reference) is not null);
        sb.Append(CnlWriter.Write(tb, cells ? models.Library : null));
        return sb.ToString();
    }

    /// <summary>
    /// brief-isc-1 R-isc-2 — what closes each result port, in port order: a Port line for a drawn or connector port, and for a
    /// component port the part's model from the board's schematic across it (each part once, at its first port), Open or Short as the
    /// schematic marks it, or a placeholder. The netlist and the cell made from the schematic both read this, so the two never differ
    /// about a part.
    /// </summary>
    /// <param name="layoutModels">brief-icp-5 — the models of the layout a cut instance is placed on, by its path; null reads
    /// <paramref name="models"/>.</param>
    public static IReadOnlyList<EmCoSimClose> Closures(IReadOnlyList<EmCoSimPort> ports, EmCoSimModels models,
                                                       Func<string?, EmCoSimModels>? layoutModels = null)
    {
        var list = new List<EmCoSimClose>();
        var parts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // brief-icp-5 R-icp5-4 — a cut instance's name is made from its path, unique against every footprint part's.
        EmCoSimCuts.Names? names = null;
        foreach (var p in ports)
        {
            if (p.Component is not { } cp || cp.Connector)
            {
                list.Add(new EmCoSimClose(EmCoSimCloseKind.PortLine, p));
                continue;
            }
            if (!parts.Add(cp.Refdes)) continue;      // a multi-pad part: placed once, at its first port
            var mine = ports.Where(q => q.Component is { } c && c.Refdes.Equals(cp.Refdes, StringComparison.OrdinalIgnoreCase)).ToList();
            if (cp.Cut is not null)
            {
                names ??= new EmCoSimCuts.Names(ports.Where(q => q.Component is { Cut: null }).Select(q => q.Component!.Refdes));
                list.AddRange(EmCoSimCuts.Close(cp, mine, layoutModels?.Invoke(cp.ClayPath) ?? models, names));
                continue;
            }
            var model = models.Instances.GetValueOrDefault(cp.SchematicId ?? cp.Refdes) ?? models.Instances.GetValueOrDefault(cp.Refdes);
            if (mine.Count == 1 && model is not null && TwoTerminal(model))
            {
                list.Add(new EmCoSimClose(EmCoSimCloseKind.Model, p, cp.Refdes, Rewired(model, [Node(p.Number), "0"]), SchematicName: model.InstanceName));
                continue;
            }
            if (model is not null && mine.Count > 1 && model.NetBindings.Count == cp.PartPins.Count)
            {
                // Its terminals in pin order: a signal pin on its port's node, its ground pin (each port's reference) on ground.
                var nets = cp.PartPins.Select(pin =>
                    mine.FirstOrDefault(q => q.Component!.Pin == pin) is { } own ? Node(own.Number)
                    : mine.Any(q => q.Component!.ReferencePin == pin) ? "0"
                    : $"nc_{cp.Refdes}_{pin}").ToList();
                list.Add(new EmCoSimClose(EmCoSimCloseKind.Model, p, cp.Refdes, Rewired(model, nets), SchematicName: model.InstanceName));
                continue;
            }
            // Marked in the schematic as not fitted, or as a link: the part is no value to guess at.
            string key = cp.SchematicId ?? cp.Refdes;
            if (mine.Count == 1 && models.Open.Contains(key))
            {
                list.Add(new EmCoSimClose(EmCoSimCloseKind.Open, p, cp.Refdes,
                    Comment: $"{cp.Refdes} is Open in the board's schematic, so its port {Node(p.Number)} is left open.", SchematicName: key));
                continue;
            }
            if (mine.Count == 1 && models.Shorted.Contains(key))
            {
                list.Add(new EmCoSimClose(EmCoSimCloseKind.Short, p, cp.Refdes, new Instance(cp.Refdes, "Short", [Node(p.Number), "0"], []),
                    Comment: $"{cp.Refdes} is Short in the board's schematic, so its port {Node(p.Number)} is shorted.", SchematicName: key));
                continue;
            }
            // No model, or one this wiring cannot read: a placeholder per port, named in a comment.
            string why = model is null ? "the board's schematic gives no model for it"
                       : $"its model has {model.NetBindings.Count} terminal(s) and its footprint {cp.PartPins.Count} pad(s)";
            foreach (var q in mine)
            {
                var (kind, pkey, value, unit) = Placeholder(cp.Refdes);
                string name = mine.Count == 1 ? cp.Refdes : $"{cp.Refdes}_{q.Component!.Pin}";
                string variable = name + "_value";
                list.Add(new EmCoSimClose(EmCoSimCloseKind.Placeholder, q, cp.Refdes,
                    new Instance(name, kind, [Node(q.Number), "0"], [new ParameterAssignment(pkey, variable)]),
                    new Variable(variable, value, unit),
                    $"{name}: {why}, so {variable} is a placeholder; set it to the part's value.", model?.InstanceName));
            }
        }
        return list;
    }

    /// <summary>A part whose model is two-terminal: two nets, or an SnP of two ports (a two-port measured in series).</summary>
    private static bool TwoTerminal(Instance model) => model.NetBindings.Count == 2;

    private static Instance Rewired(Instance model, IReadOnlyList<string> nets)
        => new(model.InstanceName, model.Reference, nets, model.Overrides);

    internal static (string Kind, string Key, string Value, string Unit) Placeholder(string refdes)
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
    public static IReadOnlyList<EmCoSimPort> PortsOf(IReadOnlyList<Em3dPort> problemPorts, C3dDocument document, C3dComponentPortBuild? build)
    {
        var list = new List<EmCoSimPort>();
        foreach (var p in problemPorts.OrderBy(p => p.Number))
        {
            int source = p.SourceNumber ?? p.Number;
            var cp = build?.Ports.FirstOrDefault(c => c.Port?.Number == source);
            var own = C3dPorts.OwnerOf(document, source);
            string name = cp?.Port?.Name ?? (own is not null ? C3dPorts.Label(own) : $"port {source}");
            string z0 = cp?.Port?.Z0 ?? own?.Z0 ?? "50";
            // brief-icp-5 — a cut instance's pin port carries the instance, whose model goes back on it.
            list.Add(new EmCoSimPort(p.Number, name, z0, cp is null ? null : EmCoSimPart.Of(cp), source));
        }
        return list;
    }

    /// <summary>brief-isc-1 — a 3D problem's ports where no 3D view assembled it (a layout's <c>.cem</c> with no component ports): each
    /// by its document number, named by the problem.</summary>
    public static IReadOnlyList<EmCoSimPort> PortsOf(IReadOnlyList<Em3dPort> problemPorts)
        => [.. problemPorts.OrderBy(p => p.Number).Select(p => new EmCoSimPort(p.Number, p.Name is { Length: > 0 } n ? n : $"port {p.SourceNumber ?? p.Number}",
                                                                              Z0Text(p.Z0), null, p.SourceNumber ?? p.Number))];

    /// <summary>
    /// brief-lcp-7 R-lcp7-2 — a Planar run's ports, in the Touchstone's order: each solved port by its number, matched to the
    /// built component port of that number when there is one, its name the label's text (the layout's own port, or the part's),
    /// its reference impedance the one the run solved with.
    /// </summary>
    /// <param name="labels">The label each port came from, index-aligned with <paramref name="ports"/>.</param>
    public static IReadOnlyList<EmCoSimPort> PortsOf(IReadOnlyList<PlanarPort> ports, IReadOnlyList<LabelShape> labels, EmPlanarComponentPortBuild? build)
    {
        var list = new List<EmCoSimPort>();
        for (int i = 0; i < ports.Count; i++)
        {
            var p = ports[i];
            // brief-icp-5 — a cut instance's pin port carries the instance, whose model goes back on it.
            var cp = build?.Ports.FirstOrDefault(c => c.Ok && c.Number == p.Number);
            string name = cp?.Name ?? (i < labels.Count && labels[i].Text is { Length: > 0 } text ? text : $"P{p.Number}");
            // brief-isc-1 D6 — a drawn port's number is its label's text (SDL-5 writes the schematic Port's Num there).
            int? num = cp is null && i < labels.Count && int.TryParse(labels[i].Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
            list.Add(new EmCoSimPort(p.Number, name, Z0Text(p.Z0), cp is null ? null : EmCoSimPart.Of(cp), num));
        }
        return list;
    }

    /// <summary>A solved reference impedance as a Port line writes it: ohms, with an imaginary part when there is one.</summary>
    private static string Z0Text(Complex z)
    {
        string re = z.Real.ToString("G15", CultureInfo.InvariantCulture);
        if (z.Imaginary == 0) return re;
        string im = Math.Abs(z.Imaginary).ToString("G15", CultureInfo.InvariantCulture);
        return z.Imaginary > 0 ? $"{re}+j{im}" : $"{re}-j{im}";
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
        return ModelsForCell(cellDir);
    }

    /// <summary>brief-isc-1 D2 — the Primary schematic of <paramref name="cellDir"/>, or null when it has none.</summary>
    public static string? PrimarySchematic(string? cellDir)
    {
        if (cellDir is not { Length: > 0 } || !Directory.Exists(cellDir)) return null;
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        if (primary.ResolvedName is not { Length: > 0 } name) return null;
        string sch = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic), name);
        return File.Exists(sch) ? sch : null;
    }

    /// <summary>brief-isc-1 D2 — <see cref="ModelsFor"/> for a cell folder: its Primary schematic, extracted as the board projection reads it.</summary>
    public static EmCoSimModels ModelsForCell(string? cellDir)
    {
        if (PrimarySchematic(cellDir) is not { } sch) return EmCoSimModels.None;
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
                Variables = [.. extracted.TestBench.GlobalVariables],
                PinNames = PinNamesOf(model, sch),
            };
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException
                                       or InvalidOperationException or UnauthorizedAccessException)
        {
            return EmCoSimModels.None;
        }
    }

    /// <summary>brief-icp-5 R-icp5-3 — each cell instance's symbol pin names, in pin order, where its symbol names them.</summary>
    internal static Dictionary<string, IReadOnlyList<string>> PinNamesOf(SchematicEditModel model, string schematicPath)
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.GetDirectoryName(Path.GetFullPath(schematicPath)) ?? "";
        foreach (var c in model.Components)
        {
            if (c.InstanceName is not { Length: > 0 } name || c.CellRef is not { Length: > 0 } cellRef) continue;
            if (CellSymbolResolver.Resolve(cellRef, dir, model.HostWorkspaceRoot).Symbol is not { Pins.Count: > 0 } symbol) continue;
            var pins = symbol.Pins.OrderBy(p => p.PortIndex).Select(p => p.Name?.Trim() ?? "").ToList();
            if (pins.Any(SymbolPinNames.IsStated)) map.TryAdd(name, pins);
        }
        return map;
    }

    /// <summary>The start of the first line every netlist this setup's runs write: what <see cref="Write"/> recognises as its own.</summary>
    private static string OwnerPrefix(EmSetup setup) => $"; Co-simulation of '{setup.Name}': the board's ";

    /// <summary>
    /// Writes <see cref="FileNameFor"/> in <paramref name="workspaceDir"/> for the run's Touchstone at <paramref name="snpPath"/>.
    /// An existing file is replaced only when it is one this writer made for the same Touchstone (its first line); any other is
    /// left alone and said. Never throws.
    /// </summary>
    /// <param name="recordPath">The run's result record; null when it wrote none.</param>
    /// <param name="ports">The result's ports in its order: <see cref="PortsOf(IReadOnlyList{Em3dPort}, C3dDocument, C3dComponentPortBuild)"/>
    /// for a 3D run, <see cref="PortsOf(IReadOnlyList{PlanarPort}, IReadOnlyList{LabelShape}, EmPlanarComponentPortBuild)"/> for a Planar one.</param>
    public static EmCoSimResult Write(string workspaceDir, EmSetup setup, string snpPath, string? recordPath, IReadOnlyList<EmCoSimPort> ports)
    {
        string path = Path.Combine(workspaceDir, FileNameFor(setup));
        try
        {
            var models = ModelsFor(ports.Select(p => p.Component?.ClayPath).FirstOrDefault(c => c is not null));
            // brief-icp-5 — a cut instance's model is read from its own layout's schematic (a 3D view may place several layouts).
            var byLayout = new Dictionary<string, EmCoSimModels>(StringComparer.OrdinalIgnoreCase);
            EmCoSimModels LayoutModels(string? clay) => clay is null ? models
                : byLayout.TryGetValue(clay, out var m) ? m : byLayout[clay] = ModelsFor(clay);
            string stored = Path.GetRelativePath(workspaceDir, Path.GetFullPath(snpPath)).Replace('\\', '/');
            string text = Text(setup, stored, ports, models, RecordFor(recordPath, workspaceDir), LayoutModels);
            IReadOnlyList<string> warnings = ports.Any(p => p.Component?.Cut is not null)
                ? [.. Closures(ports, models, LayoutModels).SelectMany(c => c.Warnings).Distinct()] : [];
            if (File.Exists(path))
            {
                string old = File.ReadAllText(path);
                if (old == text)
                    return new(EmSnpSchematicOutcome.Current, path, $"{Path.GetFileName(path)} is already this run's co-simulation netlist.", text) { Warnings = warnings };
                // Ownership is the header up to the port count: a re-run with ports added or removed rewrites its own netlist
                // (designer round 39 — a 12-port run left the 10-port netlist naming a Touchstone that run superseded).
                if (!old.StartsWith(OwnerPrefix(setup), StringComparison.Ordinal))
                    return new(EmSnpSchematicOutcome.LeftAlone, path,
                        $"{Path.GetFileName(path)} exists and was not written by this setup's runs, so it was left alone.", text) { Warnings = warnings };
            }
            AtomicFile.WriteAllText(path, text);
            return new(EmSnpSchematicOutcome.Created, path,
                $"Wrote {Path.GetFileName(path)}: the board's S{ports.Count}P with {ports.Count(p => p.Component is { Connector: false })} part " +
                "port(s) closed by their models, ready to simulate.", text) { Warnings = warnings };
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
    /// <param name="replace">brief-isc-1 — write over an existing cell's schematic (Create now's Replace; a run never asks).</param>
    /// <param name="cellName">brief-isc-1 — another name for the cell (Create now's New name).</param>
    public static EmSnpSchematicResult CreateCell(string workspaceDir, EmSetup setup, string text, string snpPath, string? recordPath,
                                                  bool replace = false, string? cellName = null)
    {
        cellName ??= CellNameFor(setup);
        try
        {
            if (NameValidator.Validate(cellName) is { } bad)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: '{cellName}' cannot be a cell name. {bad}");

            string cellDir = Path.Combine(workspaceDir, cellName);
            bool exists = Directory.Exists(cellDir) || File.Exists(cellDir);
            if (exists && !replace) return ExistingCell(workspaceDir, cellDir, cellName, snpPath, recordPath);

            var (lib, tb) = new CnlReader().Read(text, cellName, workspaceDir);
            string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
            var drawn = NetlistSchematic.Build(lib, tb, schematicDir, existingCells: ExistingCells(lib, workspaceDir, schematicDir));
            if (drawn.Schematic is not { } model)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                           $"No schematic was created for this run: {string.Join(" ", drawn.Refusals)} {cellName}.cnl states the same circuit.");
            AddNotes(model, text);

            string schematic = exists ? EmSchematicWithParts.WriteOver(cellDir, cellName, model)
                                      : CellCreate.Create(workspaceDir, cellName, CellViews.Schematic, model).SchematicPath!;
            int circuitPorts = tb.Instances.Count(i => i.Reference.Equals("Port", StringComparison.OrdinalIgnoreCase));
            if (circuitPorts > 0)
                CellCreate.WriteTemplateDataDisplay(workspaceDir, schematic, null, tb.Analyses.Any(a => a is FieldsAnalysis)
                    ? PatternDisplayJson("em_cosim.npy", tb.Analyses.OfType<SParameterAnalysis>().Any(HasSweep))
                    : EmSnpSchematic.DataDisplayJson(circuitPorts, "em_cosim.npy"));
            return new(EmSnpSchematicOutcome.Created, cellName, schematic,
                       $"{(exists ? "Replaced" : "Created")} cell {cellName}: the board's result with each part on its own port, ready to simulate.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                      or FormatException or ArgumentException or CnlReadException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: {e.Message}");
        }
    }

    /// <summary>A cell of that name exists. Reading it never changes it: the only question is which sentence the run says.</summary>
    /// <summary>
    /// brief-icp-9 — the cells a co-simulation netlist defines that are cell folders of the workspace (a cut device's own cell,
    /// whose definition the netlist carries from the board's schematic): each by name, with the <c>CellRef</c> the drawing stores
    /// and its symbol's pins in port order. A cell found nowhere, or whose symbol does not have one pin per port, is left out, and
    /// the drawing refuses it as before.
    /// </summary>
    internal static Dictionary<string, ExactCell> ExistingCells(Library lib, string workspaceDir, string schematicDir)
    {
        var map = new Dictionary<string, ExactCell>(StringComparer.Ordinal);
        foreach (var cell in lib.Cells)
        {
            string dir = Path.Combine(workspaceDir, cell.Name);
            if (!File.Exists(Path.Combine(dir, ".ccell"))) continue;
            string cellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(schematicDir, dir))!;
            var symbol = CellSymbolResolver.ResolveCellDirOrRef(dir, workspaceDir).Symbol;
            if (symbol is not { Pins.Count: > 0 } || symbol.Pins.Count != cell.Ports.Count) continue;
            map[cell.Name] = new ExactCell(cellRef, [.. symbol.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY))]);
        }
        return map;
    }

    internal static EmSnpSchematicResult ExistingCell(string workspaceDir, string cellDir, string cellName, string snpPath, string? recordPath)
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

    /// <summary>The netlist's comments as notes above the drawing: what they say about a placeholder or a frequency is as true of the cell.
    /// One text, a line per comment, so the note moves and edits as one (owner request, 2026-11-08): it was a text per line.</summary>
    internal static void AddNotes(SchematicEditModel model, string text)
    {
        string[] notes = [.. text.Split('\n').Select(l => l.Trim()).TakeWhile(l => l.StartsWith(';')).Select(l => l.TrimStart(';').Trim()).Where(l => l.Length > 0)];
        if (notes.Length == 0) return;
        double left = model.Components.Min(c => c.X) - 200, top = model.Components.Min(c => c.Y) - 300;
        var note = new EditableText { Text = string.Join("\n", notes), X = left };
        note.Y = top - CircuitRF.Design.Symbol.SymbolGeometry.TextBoxSize(note.ToPrimitive()).H;   // anchored top-left, its last line just above `top`
        model.CanvasObjects.Add(note);
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
