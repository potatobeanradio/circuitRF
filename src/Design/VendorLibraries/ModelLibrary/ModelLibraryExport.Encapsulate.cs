// Export to Library's Encapsulated netlist (brief vendorlib-19): every linear element of the cell sampled into ONE N-port
// S-parameter block over the dialog's frequency plan, with the devices placed against its ports. It needs no compiler.
// What ships is the network's behaviour at its ports; its topology, element values and parts list stay with the maker.
//
// Reused, never copied: the flattening is ProtectPartition's (with the engine's ModelKind as the rule — ProtectClassify),
// the plan and its 0 Hz rule are brief 11's, the wrapper is drawn as Compile netlist's is (FinishWrapper), and the proof
// is ProtectCheck.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

public static partial class ModelLibraryExport
{
    /// <summary><see cref="ModelPartManifest.Protection"/> of an encapsulated model.</summary>
    public const string ProtectionEncapsulated = "encapsulated";

    /// <summary>The wrapper's instance of the block.</summary>
    internal const string BlockInstance = "ENC1";

    /// <summary>R-vl19-1d's note.</summary>
    public const string NothingLinearLine = "Nothing in the cell is linear: the model is its devices.";

    /// <summary>The harmonic count a harmonic-balance run uses when it states none.</summary>
    private const int DefaultHarmonics = 7;

    /// <summary>What Encapsulated netlist adds to the plan.</summary>
    /// <param name="PortNets">The block's ports, as flattened nets: the cell's pins it touches in pin order, then the
    /// nets where it meets an outside element (D3). Empty when there is no block.</param>
    /// <param name="Frozen">The cell parameters that reach the block, frozen at their defaults (D5).</param>
    internal sealed record EncapsulatePlanned(ProtectPlan Partition, string CellName, string? BaseDirectory,
                                              IReadOnlyList<string> PortNets, IReadOnlyList<string> Frozen,
                                              IReadOnlyList<ProtectBias> Bias, string PlanText, double[] Freqs,
                                              IReadOnlyList<string> Lines, string BlockLine, string HarmonicLine)
    {
        public bool HasBlock => PortNets.Count > 0;
    }

    // ── Plan's half ──────────────────────────────────────────────────────────────────────────────

    private static EncapsulatePlanned? PlanEncapsulate(string dir, ExportOptions options, IReadOnlyList<string> pins, double[] freqs,
                                                       List<string> refusals)
    {
        string cellName = Path.GetFileName(dir);
        string? text = ModelNetlist.FromCell(dir, out string? why);
        if (text is null) { refusals.Add(why ?? "The cell could not be extracted."); return null; }
        string? srcBase = SchematicCircuit.ReferenceBaseOfDirectory(Path.GetDirectoryName(PrimaryPath(dir, ViewType.Schematic) ?? dir));
        var partition = ProtectPartition.Partition(text, cellName, srcBase, ProtectRule.Encapsulate);
        refusals.AddRange(partition.Refusals);

        // D3: pins the block touches, in pin order, then the joining nets. A block that reaches nothing outside itself
        // changes nothing at the pins; it is no block.
        var insideNets = partition.Inside.SelectMany(e => e.Nets).ToHashSet(StringComparer.Ordinal);
        var ports = partition.Pins.Where(insideNets.Contains).Concat(partition.Joins).ToList();
        if (partition.Inside.Count > 0 && ports.Count == 0)
            refusals.Add($"The linear elements of the cell ({string.Join(", ", partition.Inside.Select(e => e.Path))}) touch no pin and no device, " +
                         "so they change nothing at the pins. Remove them, or wire them to the cell.");

        var frozen = FrozenParameters(partition, refusals);
        foreach (var corner in partition.Top.Cell.Corners)
            foreach (var b in corner.Bindings.Where(b => frozen.Contains(b.Name)))
                refusals.Add($"{b.Name} has corner values and reaches the S-parameter block, which is sampled at one value. " +
                             "Make it reach only a device, or use Compile netlist.");

        var bias = new List<ProtectBias>();
        foreach (string b in options.CheckBias)
        {
            if (ProtectBias.Parse(b, pins, out string? bad) is { } parsed) bias.Add(parsed);
            else refusals.Add($"The bias point '{b}' cannot be read: {bad}");
        }
        if (options.CheckBias.Count == 0) bias.AddRange(ProtectBias.Defaults(pins));

        string planText = DescribePlan(options.Sweep ?? DefaultSweep);
        string blockLine = "", harmonicLine = "";
        if (ports.Count > 0 && freqs.Length > 0)
        {
            blockLine = $"The S-parameter block is sampled over this plan: {ports.Count} port{(ports.Count == 1 ? "" : "s")} × " +
                        $"{freqs.Length.ToString("N0", CultureInfo.InvariantCulture)} points, about {FormatBytes(BlockBytes(ports.Count, freqs.Length))}.";
            double top = freqs[^1];
            harmonicLine = top > 0
                ? $"Harmonic balance at the default {DefaultHarmonics} harmonics stays inside the plan up to a " +
                  $"{FormatFrequency(RoundDown(top / DefaultHarmonics))} fundamental. Above {FormatFrequency(top)} the block holds its last point and warns."
                : "";
        }
        else if (ports.Count == 0) blockLine = NothingLinearLine;

        var lines = new List<string>();
        if (ports.Count == 0) lines.Add(NothingLinearLine);
        else
        {
            lines.Add($"One S-parameter block of {ports.Count} port{(ports.Count == 1 ? "" : "s")}: " +
                      string.Join(", ", partition.Inside.Select(e => $"{e.Path} ({e.Type})")) + ".");
            for (int k = 0; k < ports.Count; k++) lines.Add($"Port {k + 1}: {DescribePort(partition, ports[k])}.");
        }
        lines.Add(partition.Outside.Count == 0 ? "Outside: nothing."
                  : $"Outside, as instances: {string.Join(", ", partition.Outside.Select(e => $"{e.Path} ({e.Type})"))}.");
        if (frozen.Count > 0)
            lines.Add($"Frozen at {(frozen.Count == 1 ? "its default" : "their defaults")}, because {(frozen.Count == 1 ? "it reaches" : "they reach")} the block: " +
                      string.Join(", ", frozen.Select(n => DescribeDefault(partition.Top.Cell, n))) +
                      $". The part does not have {(frozen.Count == 1 ? "it" : "them")}.");
        if (!options.CompileModels && ReadableDevices(partition) is { Count: > 0 } readable)
            lines.Add($"Readable in the library: {string.Join(", ", readable)}. Tick Compile Verilog-A models to ship {(readable.Count == 1 ? "its build" : "their builds")} instead.");

        return new EncapsulatePlanned(partition, cellName, srcBase, ports, frozen, bias, planText, freqs, lines, blockLine, harmonicLine);
    }

    /// <summary>Four significant figures, rounded down so the fundamental stated is one that stays inside the plan.</summary>
    private static double RoundDown(double hz)
    {
        if (hz <= 0) return hz;
        double step = Math.Pow(10, Math.Floor(Math.Log10(hz)) - 3);
        return Math.Floor(hz / step) * step;
    }

    /// <summary>A port as the summary names it: the cell pin, or the outside terminals the net joins.</summary>
    private static string DescribePort(ProtectPlan p, string net)
    {
        if (p.Pins.Contains(net, StringComparer.Ordinal)) return $"pin {net}";
        var at = new List<string>();
        foreach (var e in p.Outside)
            for (int k = 0; k < e.Nets.Count; k++)
                if (e.Nets[k] == net)
                {
                    string[] names = p.TerminalNames.TryGetValue(e.Path, out var own) ? own : SymbolPinNames(e.Type, e.Nets.Count);
                    at.Add(k < names.Length && names[k].Length > 0 ? $"{e.Path} ({names[k]})" : $"{e.Path} (terminal {k + 1})");
                }
        return "joins " + string.Join(", ", at);
    }

    /// <summary>The pin names of the symbol a netlist type is drawn with — the one whose engine reference IS the type, as
    /// the netlist-to-schematic path finds it — for a device that builds several components (a composed FET's core sits
    /// behind its own parasitics, not on its nets).</summary>
    private static string[] SymbolPinNames(string type, int pins)
    {
        foreach (var kind in Enum.GetValues<SymbolKind>())
        {
            string reference;
            try { reference = ComponentTypeRegistry.EngineReference(kind); } catch { continue; }
            if (!reference.Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
            try { return [.. SymbolPortDefs.For(kind, pins).Select(d => d.Name)]; } catch { return []; }
        }
        return [];
    }

    private static string DescribeDefault(Cell top, string name)
        => top.Parameters.FirstOrDefault(p => p.Name == name) is { } p
            ? $"{name} = {p.DefaultExpression}{(string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit)}"
            : name;

    /// <summary>D6: the outside devices whose source ships readable — a ComposedFET's <c>.cmodel</c>, a Verilog-A
    /// source — by instance.</summary>
    private static List<string> ReadableDevices(ProtectPlan p)
    {
        var list = new List<string>();
        foreach (var e in p.Outside)
        {
            string? file = e.Instance.Overrides.FirstOrDefault(o => o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))?.Expression.Trim().Trim('"');
            if (ProtectPartition.IsComposedFet(e.Type)) list.Add($"{e.Path} (its .cmodel)");
            else if (e.Type.Equals("VerilogA", StringComparison.OrdinalIgnoreCase) && file is { Length: > 0 }
                     && !CircuitRF.Core.Devices.External.OsdiBuilds.IsCompiled(file))
                list.Add($"{e.Path} (its Verilog-A source)");
        }
        return list;
    }

    /// <summary>
    /// D5: every top-level parameter an inside element's value reaches — through sub-cell overrides, defaults and
    /// variables — in declaration order. A distribution on the way is a refusal, since the block is sampled once.
    /// </summary>
    private static List<string> FrozenParameters(ProtectPlan p, List<string> refusals)
    {
        var top = p.Top.Cell;
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();

        void Reach(ProtectLevel level, string expr)
        {
            foreach (string name in Refs(expr)) Resolve(level, name);
        }
        void Spread(string name, string expr)
        {
            if (Evaluator.ContainsStatisticalCall(expr))
                refusals.Add($"{name} has a spread and reaches the S-parameter block, which is sampled at one value. " +
                             "Make it reach only a device, or use Compile netlist.");
        }
        void Resolve(ProtectLevel level, string name)
        {
            if (!seen.Add((level.Path, name))) return;
            if (!level.IsTop)
            {
                if (level.PlacedBy!.Overrides.FirstOrDefault(o => o.Name == name) is { } ov) { Reach(level.Parent!, ov.Expression); return; }
                if (level.Cell.Parameters.FirstOrDefault(x => x.Name == name) is { } sp) { Spread($"{level.Path}.{name}", sp.DefaultExpression); Reach(level, sp.DefaultExpression); return; }
                if (level.Cell.Variables.FirstOrDefault(x => x.Name == name) is { } sv) { Spread($"{level.Path}.{name}", sv.Expression); Reach(level, sv.Expression); return; }
                Resolve(level.Parent!, name);
                return;
            }
            if (top.Parameters.FirstOrDefault(x => x.Name == name) is { } tp)
            {
                reached.Add(name);
                Spread(name, tp.DefaultExpression);
                Reach(level, tp.DefaultExpression);
            }
            else if (top.Variables.FirstOrDefault(x => x.Name == name) is { } tv) { Spread(name, tv.Expression); Reach(level, tv.Expression); }
        }

        foreach (var e in p.Inside)
            foreach (var ov in e.Instance.Overrides)
            {
                if (Evaluator.ContainsStatisticalCall(ov.Expression))
                    refusals.Add($"{e.Path}'s {ov.Name}={ov.Expression} has a spread and is inside the S-parameter block, which is sampled at one value. " +
                                 "Make it a cell parameter that reaches only a device, or use Compile netlist.");
                Reach(e.Level, ov.Expression);
            }
        return [.. top.Parameters.Select(x => x.Name).Where(reached.Contains)];
    }

    /// <summary>The names an expression reads; none for one that is not an expression (a quoted file path).</summary>
    private static IEnumerable<string> Refs(string expr)
    {
        string t = expr.Trim();
        if (t.Length == 0 || t[0] == '"') return [];
        try { return AstWalker.CollectRefs(Parser.Parse(t)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }

    /// <summary>The block's Touchstone size, estimated as the writer spells it: two numbers of about 21 characters per
    /// entry, N² entries a line per frequency.</summary>
    internal static long BlockBytes(int ports, int points) => (long)points * (ports * (long)ports * 2 * 21 + 26) + 400;

    private static string FormatBytes(long bytes)
        => bytes >= 1_000_000 ? $"{(bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture)} MB"
         : bytes >= 1_000 ? $"{(bytes / 1e3).ToString("0", CultureInfo.InvariantCulture)} kB"
         : $"{bytes} bytes";

    // ── Export's half ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-vl19-1b/1c, after the cell is copied into <paramref name="tmp"/>: the block sampled from the SOURCE cell and
    /// written to <c>models/&lt;Part&gt;.s&lt;N&gt;p</c>; the copy rewritten as the wrapper — the block on its port nets,
    /// each outside element as the copy wrote it, the cell's pins — with every frozen parameter taken off the part. The
    /// copy is partitioned with the source's sides, so an element that came out differently is a refusal.
    /// </summary>
    private static void BuildEncapsulated(EncapsulatePlanned enc, ExportPlan plan, string tmp, string copiedCnl, RunControl? control)
    {
        string part = plan.PartNumber;
        var sides = enc.Partition.Inside.Select(e => (e.Path, ProtectSide.Inside))
                       .Concat(enc.Partition.Outside.Select(e => (e.Path, ProtectSide.Outside)))
                       .ToDictionary(x => x.Path, x => x.Item2, StringComparer.Ordinal);
        var copy = ProtectPartition.Partition(copiedCnl, part, baseDirectory: null, ProtectRule.Encapsulate, sides);
        if (copy.Refusals.Count > 0) throw new ProtectRefusedException(copy.Refusals);
        if (!copy.Outside.Select(e => e.Path).SequenceEqual(enc.Partition.Outside.Select(e => e.Path), StringComparer.Ordinal))
            throw new ProtectRefusedException(["The copied cell does not partition as the cell did; nothing was written."]);

        string? snp = null;
        if (enc.HasBlock)
        {
            control?.BeginStage("Sampling the S-parameter block");
            var s = SampleBlock(enc, control);
            string models = Path.Combine(tmp, ModelFolder.ModelsFolder);
            Directory.CreateDirectory(models);
            snp = Path.Combine(models, $"{part}.s{enc.PortNets.Count}p");
            // D8: the part number, the plan and the version — no element name, no net name, no time.
            AtomicFile.WriteAllText(snp, TouchstoneText(enc.PortNets.Count, enc.Freqs, s,
            [
                $"circuitRF Export to Library: {part}, encapsulated netlist",
                $"Frequency plan: {enc.PlanText}",
                $"circuitRF {CmodelExport.Version}",
            ]));
        }

        // D3: pins keep their names; every other net is neutral — the joins first, in port order.
        var nets = new Dictionary<string, string>(StringComparer.Ordinal) { ["0"] = "0" };
        foreach (string p in copy.Pins) nets[p] = p;
        int next = 1;
        string Net(string flat)
        {
            if (nets.TryGetValue(flat, out string? n)) return n;
            string name;
            do name = $"n{next++}"; while (nets.ContainsValue(name));
            return nets[flat] = name;
        }
        foreach (string p in enc.PortNets) Net(p);
        var names = new HashSet<string>(StringComparer.Ordinal) { BlockInstance };
        string Name(string path)
        {
            string name = path.Replace('.', '_');
            for (int k = 2; !names.Add(name); k++) name = $"{path.Replace('.', '_')}_{k}";
            return name;
        }

        var tb = new TestBench(part);
        tb.GlobalVariables.AddRange(WrapperVariables(copy, enc.Frozen));
        if (snp is not null)
            tb.Instances.Add(new Instance(BlockInstance, "SnP", enc.PortNets.Select(Net),
            [
                new("File", "\"" + Path.GetRelativePath(tmp, snp).Replace('\\', '/') + "\""),
                new("NumPorts", enc.PortNets.Count.ToString(CultureInfo.InvariantCulture)),
            ]));
        foreach (var e in copy.Outside)
        {
            // A composed FET's line leaves its thermal pin out when nothing is wired to it, and its glyph draws four pins:
            // the wrapper wires it to a net of its own, which touches nothing else — the internal node it is unwired.
            var pinNets = e.Nets.Select(Net).ToList();
            if (ProtectPartition.IsComposedFet(e.Type) && e.Nets.Count == 3) pinNets.Add(Net(e.Path + ".__thermal"));
            tb.Instances.Add(new Instance(Name(e.Path), e.Instance.Reference, pinNets, e.Instance.Overrides));
        }
        if (tb.Instances.Count == 0) throw new ProtectRefusedException(["The cell holds nothing to export."]);

        FinishWrapper(tb, copy, part, tmp, f => snp is not null && DocumentFileRefs.PathComparer.Equals(Path.GetFullPath(f), Path.GetFullPath(snp)));

        // D5: a frozen parameter is not the part's. A device it also reached reads it as a variable (WrapperVariables).
        string ccellPath = Path.Combine(tmp, CellFolder.CcellFileName);
        if (enc.Frozen.Count > 0 && File.Exists(ccellPath))
        {
            var ccell = CellPersistence.LoadFromFile(ccellPath);
            ccell.Parameters.RemoveAll(p => enc.Frozen.Contains(p.Name, StringComparer.Ordinal));
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
    }

    /// <summary>
    /// The variables the wrapper's outside elements read, and only those: each top-level variable they reach, and each
    /// frozen parameter they reach, as a variable holding its default — so a device a frozen parameter also reached gets
    /// the frozen value (D5), and no value that only the block used is written anywhere (D8).
    /// </summary>
    private static List<Variable> WrapperVariables(ProtectPlan copy, IReadOnlyList<string> frozen)
    {
        var top = copy.Top.Cell;
        var want = new HashSet<string>(StringComparer.Ordinal);
        void Reach(string expr)
        {
            foreach (string name in Refs(expr))
            {
                if (!want.Add(name)) continue;
                if (top.Variables.FirstOrDefault(v => v.Name == name) is { } v) Reach(v.Expression);
                else if (frozen.Contains(name) && top.Parameters.FirstOrDefault(p => p.Name == name) is { } p) Reach(p.DefaultExpression);
                else want.Remove(name);
            }
        }
        foreach (var e in copy.Outside.Where(e => e.Level.IsTop))
            foreach (var ov in e.Instance.Overrides) Reach(ov.Expression);

        var list = new List<Variable>();
        foreach (var p in top.Parameters.Where(p => want.Contains(p.Name) && frozen.Contains(p.Name)))
            list.Add(new Variable(p.Name, p.DefaultExpression, p.Unit));
        list.AddRange(top.Variables.Where(v => want.Contains(v.Name)));
        return list;
    }

    /// <summary>
    /// The block's S, freq-major then row-major, on the plan: the cell elaborated once, its inside components put in a
    /// network of their own with a 50 Ω port from each port net to ground. Above 0 Hz the S-parameter engine samples it;
    /// the 0 Hz row is the network's DC SOLUTION, solved by the DC engine the proof and every run use — each port driven
    /// in turn through 50 Ω by a 2 V source, the others terminated in 50 Ω, so S = 2·V/E − δ. That is not the
    /// S-parameter engine's own 0 Hz answer, which takes a line's resonance clamp for its DC.
    /// </summary>
    private static Complex[] SampleBlock(EncapsulatePlanned enc, RunControl? control)
    {
        int n = enc.PortNets.Count;
        var pins = enc.Partition.Pins;
        double[] freqs = enc.Freqs;
        try
        {
            string text = ModelNetlistText(enc);
            using var nl = ProtectClassify.Elaborate(text, enc.CellName, pins.Count, enc.BaseDirectory);
            var inside = enc.Partition.Inside.SelectMany(e => ProtectClassify.ComponentsOf(nl, e.Path)).ToList();
            int[] portNode = [.. enc.PortNets.Select(net => NodeOf(nl, pins, net))];

            var s = new Complex[freqs.Length * n * n];
            double[] rf = [.. freqs.Where(f => f > 0)];
            if (rf.Length > 0)
            {
                var net = Network(nl, inside, portNode, drive: -1);
                var sRf = SParameterEngine.Run(net, rf, control: control).Cubes["S"].ComplexValues;
                int offset = (freqs.Length - rf.Length) * n * n;
                for (int i = 0; i < sRf.Length; i++) s[offset + i] = sRf[i];
            }
            if (freqs[0] == 0)
                for (int k = 0; k < n; k++)
                {
                    control?.Token.ThrowIfCancellationRequested();
                    var net = Network(nl, inside, portNode, drive: k);
                    var dc = NonlinearDcEngine.Run(net);
                    if (!dc.Converged) throw new ConversionRefused($"The S-parameter block's DC solve with port {k + 1} driven did not converge.");
                    for (int j = 0; j < n; j++)
                    {
                        double v = portNode[j] > 0 && portNode[j] - 1 < dc.NodeVoltages.Length ? dc.NodeVoltages[portNode[j] - 1] : 0;
                        s[j * n + k] = 2 * v / DcDrive - (j == k ? 1 : 0);
                    }
                }
            for (int i = 0; i < s.Length; i++)
                if (!double.IsFinite(s[i].Real) || !double.IsFinite(s[i].Imaginary))
                    throw new ConversionRefused($"The S-parameter block has no finite S-parameters at {FormatHz(freqs[i / (n * n)])}.");
            return s;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ConversionRefused)
        {
            throw new ConversionRefused($"The S-parameter block could not be sampled: {ex.Message}");
        }
    }

    private const double DcDrive = 2.0, PortZ0 = 50.0;

    /// <summary>The source cell's netlist text, as the plan partitioned it.</summary>
    private static string ModelNetlistText(EncapsulatePlanned enc)
        => enc.Partition.SourceText ?? throw new ConversionRefused("The cell's netlist is not at hand to sample the block from.");

    private static int NodeOf(ElaboratedNetlist nl, IReadOnlyList<string> pins, string net)
        => ProtectClassify.NodeOf(nl, pins, net) is var idx and >= 0 ? idx
         : throw new ConversionRefused($"The block's port net {net} is not in the elaborated cell.");

    /// <summary>
    /// The inside components in a netlist of their own, node numbers kept: ports from each port node to ground (for the
    /// S-parameter engine, <paramref name="drive"/> &lt; 0), or 50 Ω terminations with port <paramref name="drive"/>
    /// fed by <see cref="DcDrive"/> volts through its 50 Ω (for the DC engine). A node only outside elements touched is
    /// tied to ground through 1 Ω, so it is no floating unknown; nothing inside reaches it, so it changes nothing.
    /// </summary>
    private static ElaboratedNetlist Network(ElaboratedNetlist from, List<ElaboratedComponent> inside, int[] portNode, int drive)
    {
        var net = new ElaboratedNetlist();
        for (int i = 1; i < from.Nodes.Count; i++) net.Nodes.GetOrAssign(from.Nodes.NameOf(i));
        foreach (var c in inside) net.AddComponent(c);
        var used = inside.SelectMany(c => c.Nodes).Concat(portNode).ToHashSet();
        for (int i = 1; i < from.Nodes.Count; i++)
            if (!used.Contains(i)) net.AddComponent(Resistor($"__enc_tie{i}", i, 0, 1.0));
        for (int k = 0; k < portNode.Length; k++)
        {
            if (drive < 0)
            {
                var ps = new Dictionary<string, Value> { ["Num"] = new(k + 1), ["Z"] = new(PortZ0) };
                net.AddComponent(new ElaboratedComponent("Port", $"__enc_P{k + 1}", [portNode[k], 0], ps, new PortModel()));
            }
            else if (k != drive) net.AddComponent(Resistor($"__enc_R{k + 1}", portNode[k], 0, PortZ0));
            else
            {
                int src = net.Nodes.GetOrAssign("__enc_src");
                net.AddComponent(new ElaboratedComponent("Vdc", "__enc_V", [src, 0],
                    new Dictionary<string, Value> { ["Vdc"] = new(DcDrive) }, new VdcModel()));
                net.AddComponent(Resistor($"__enc_R{k + 1}", src, portNode[k], PortZ0));
            }
        }
        return net;
    }

    private static ElaboratedComponent Resistor(string path, int a, int b, double ohms)
        => new("R", path, [a, b], new Dictionary<string, Value> { ["R"] = new(ohms) }, new ResistorModel());

    /// <summary>
    /// D7 (§4): the original cell and the wrapper with its block, through the same engine, at the proof's bias points:
    /// the DC terminal currents, and the Y-parameters at three frequencies taken ON sampled points, where the block is
    /// exact by construction — at the one ambient it was sampled at. Between samples it is its interpolation: the worst
    /// |S| difference halfway between samples, in dB, is reported as information.
    /// </summary>
    private static ProtectCheckResult ProveEncapsulated(ExportPlan plan, EncapsulatePlanned enc, string modelDir, ModelPartManifest manifest,
                                                        out double? betweenDb)
    {
        betweenDb = null;
        string srcText = enc.Partition.SourceText!;
        string wrapperText = File.ReadAllText(ModelNetlist.PathOf(modelDir));
        double[] rf = [.. enc.Freqs.Where(f => f > 0)];
        double[] at = rf.Length == 0 ? [] : [.. new[] { rf[0], rf[rf.Length / 2], rf[^1] }.Distinct()];
        var proof = ProtectCheck.Run(srcText, enc.CellName, enc.BaseDirectory, wrapperText, manifest.Cell, modelDir, manifest.Pins,
                                     enc.Bias, at, thermal: false, subject: "encapsulated");
        if (!proof.Passed || rf.Length < 2) return proof;
        try
        {
            double[] mids = [.. rf.Zip(rf.Skip(1), (x, y) => (x + y) / 2)];
            var a = BenchS(srcText, enc.CellName, enc.BaseDirectory, manifest.Pins.Count, mids);
            var b = BenchS(wrapperText, manifest.Cell, modelDir, manifest.Pins.Count, mids);
            double db = 0;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                double ma = a[i].Magnitude, mb = b[i].Magnitude;
                if (Math.Max(ma, mb) < 0.01) continue;
                db = ma == 0 || mb == 0 ? double.PositiveInfinity : Math.Max(db, Math.Abs(20 * Math.Log10(mb / ma)));
            }
            betweenDb = db;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        return proof;
    }

    /// <summary>The finished export's line: the block, the proof, what lies between samples.</summary>
    private static string EncapsulatedMessage(EncapsulatePlanned enc, ExportPlan plan, ProtectCheckResult proof, double? betweenDb)
        => (enc.HasBlock
               ? $"Encapsulated: one {enc.PortNets.Count}-port S-parameter block, {enc.Freqs.Length} points"
                 + (betweenDb is { } db ? $"; between samples, within {db.ToString("G2", CultureInfo.InvariantCulture)} dB." : ".")
               : "Encapsulated: " + NothingLinearLine)
           + " " + proof.Summary;
}
