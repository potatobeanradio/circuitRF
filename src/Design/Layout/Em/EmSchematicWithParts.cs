// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-isc-1 — Create SnP schematic with "Include pre-existing schematic components": the cell made from the Primary schematic of the
// cell the EM document belongs to. The sheet is the user's: kept components stay where they are with every parameter, the copper the
// run stands for is removed, and the run's SnP block is joined by net labels to the schematic's Ports (by Num) and to each part on a
// component port (one pin to its SnP pin, the other to ground, exactly as the co-simulation netlist closes it: EmCoSimNetlist.Closures
// is the one decision both read).

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Results;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em;

public static class EmSchematicWithParts
{
    /// <summary>What is appended to the result's name to name the cell.</summary>
    public const string CellSuffix = "_sch";

    /// <summary>The SnP instance's name in the new cell (made unique against the schematic's own names).</summary>
    public const string SnpInstanceName = "EM1";

    /// <summary>The length of the wire from a pin to its net label or ground.</summary>
    private const double Stub = 200;

    /// <summary>The cell: the result's name, as the <c>_sim</c> cell's, plus <see cref="CellSuffix"/>.</summary>
    public static string CellNameFor(EmSetup setup)
    {
        string sim = EmSnpSchematic.CellNameFor(setup);
        return sim[..^EmSnpSchematic.CellSuffix.Length] + CellSuffix;
    }

    /// <summary>
    /// Makes <see cref="CellNameFor"/> in <paramref name="workspaceDir"/> from the Primary schematic of <paramref name="cellDir"/>, or
    /// says what an existing cell of that name does. Null, with <paramref name="fallback"/> saying why, when there is no Primary
    /// schematic to start from: the caller makes the ordinary cell instead. Never throws.
    /// </summary>
    /// <param name="snpPath">The run's Touchstone. When it does not exist yet (Create now, before any run), the block still names it,
    /// <paramref name="ports"/> give the port count, and a warning says so.</param>
    /// <param name="ports">Every port of the result, in its order; null numbers them 1…N from the Touchstone.</param>
    /// <param name="faces">The side each port faces, by port number, for the block's pin arrangement.</param>
    /// <param name="checkLvs">Compare the cell's layout against its schematic first (D9).</param>
    /// <param name="replace">Write over an existing cell of that name (Create now's Replace; a run never asks for it).</param>
    /// <param name="cellName">Another name for the cell (Create now's New name); null takes <see cref="CellNameFor"/>.</param>
    public static EmSnpSchematicResult? Create(
        string workspaceDir, EmSetup setup, string? cellDir, string snpPath, IReadOnlyList<EmCoSimPort>? ports,
        IReadOnlyDictionary<int, EmPortFace> faces, out string? fallback,
        bool checkLvs = true, bool replace = false, string? cellName = null)
    {
        fallback = null;
        cellName ??= CellNameFor(setup);
        string? primary = EmCoSimNetlist.PrimarySchematic(cellDir);
        if (primary is null)
        {
            fallback = cellDir is null
                ? "Include pre-existing schematic components found no cell around this EM document, so the ordinary schematic was made."
                : $"Include pre-existing schematic components found no Primary schematic in cell {Path.GetFileName(cellDir)}, so the ordinary schematic was made.";
            return null;
        }
        try
        {
            if (NameValidator.Validate(cellName) is { } bad)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: '{cellName}' cannot be a cell name. {bad}");
            string newCellDir = Path.Combine(workspaceDir, cellName);
            bool exists = Directory.Exists(newCellDir) || File.Exists(newCellDir);
            if (exists && !replace) return EmCoSimNetlist.ExistingCell(workspaceDir, newCellDir, cellName, snpPath, null);

            var warnings = new List<string>();
            bool haveSnp = File.Exists(snpPath);
            int n;
            Func<int, Complex>? z0 = null;
            if (haveSnp)
            {
                var snp = RfCore.TouchstoneIO.ReadFile(snpPath, readComments: false);
                n = snp.Ports;
                z0 = k => snp.Z0PerPort is { } per && k - 1 < per.Length ? per[k - 1] : snp.Z0;
                if (ports is not null && ports.Count != n)
                {
                    warnings.Add($"The run's ports ({ports.Count}) and its Touchstone's ({n}) do not agree, so the SnP pins are numbered 1 to {n} " +
                                 "and no part is placed on a component port.");
                    ports = null;
                }
            }
            else
            {
                if (ports is not { Count: > 0 })
                    return new(EmSnpSchematicOutcome.NotCreated, cellName, null,
                               $"No schematic was created: there is no Touchstone at {snpPath} yet, and without one the setup's ports could not be counted.");
                n = ports.Count;
                warnings.Add($"No Touchstone exists yet at {SnpPathPolicy.ToStored(Path.GetFullPath(snpPath), workspaceDir)}: the SnP block names it, " +
                             "and reads it once the setup has been run.");
            }
            if (n < 1)
                return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: {Path.GetFileName(snpPath)} holds no ports.");
            ports ??= [.. Enumerable.Range(1, n).Select(k => new EmCoSimPort(k, $"P{k}", z0 is null ? "50" : Z0Text(z0(k)), null, k))];

            if (checkLvs && cellDir is not null) warnings.InsertRange(0, LvsWarnings(cellDir));

            var (model, _, _) = SchematicPersistence.LoadFromFile(primary);
            var models = EmCoSimNetlist.ModelsForCell(cellDir);
            string stored = SnpPathPolicy.ToStored(Path.GetFullPath(snpPath), workspaceDir);
            var built = Build(model, setup, stored, ports, models, faces, warnings);

            string schematicDir = CellFolder.SubFolderPath(newCellDir, ViewType.Schematic);
            model.TechRef = SchematicTechnology.Rebase(model.TechRef, Path.GetDirectoryName(primary), schematicDir);
            string schematic = exists ? WriteOver(newCellDir, cellName, model)
                                      : CellCreate.Create(workspaceDir, cellName, CellViews.Schematic, model).SchematicPath!;

            WriteDisplay(workspaceDir, primary, schematic, model, built.PortCount, warnings);
            if (built.HasPins) CopySymbol(cellDir, newCellDir, cellName, warnings);

            string sentence = $"{(exists ? "Replaced" : "Created")} cell {cellName} from {Path.GetFileName(primary)}: an S{n}P of " +
                              $"{(haveSnp ? "this run" : "the setup's results")}, {built.Joined} port(s) joined, {built.Parts} part(s) on component ports" +
                              (warnings.Count > 0 ? $", {warnings.Count} warning(s)." : ".") +
                              (built.PortCount == 0 && built.HasPins
                                  ? $" Its Pins are the board's ports: place it where {Path.GetFileName(cellDir)} is placed."
                                  : warnings.Count > 0 ? "" : " Ready to simulate.");
            return new EmSnpSchematicResult(EmSnpSchematicOutcome.Created, cellName, schematic, sentence) { Warnings = warnings };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                      or FormatException or ArgumentException or InvalidOperationException)
        {
            return new(EmSnpSchematicOutcome.NotCreated, cellName, null, $"No schematic was created for this run: {e.Message}");
        }
    }

    /// <summary>
    /// Create now's Replace: <paramref name="model"/> written over the cell's Primary schematic (or as a new one named after the cell
    /// when it has none). The cell's other views, its Data Display and its results are left as they are. Returns the schematic's path.
    /// </summary>
    internal static string WriteOver(string cellDir, string cellName, SchematicEditModel model)
    {
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        var resolved = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
        string schematic = resolved.ResolvedName is { Length: > 0 } name
            ? Path.Combine(schematicDir, name)
            : Path.Combine(schematicDir, cellName + CellFolder.ViewExtension(ViewType.Schematic));
        Directory.CreateDirectory(schematicDir);
        SchematicPersistence.SaveToFile(schematic, model, cellName);
        return schematic;
    }

    /// <summary>D9 — what the layout comparison found wrong, one warning each; nothing when the cell has no layout or the
    /// comparison could not run.</summary>
    private static IEnumerable<string> LvsWarnings(string cellDir)
    {
        var list = new List<string>();
        try
        {
            var r = LvsRun.Run(cellDir);
            // Every error, and every device warning: parts are matched to component ports by name, so a part the two sides name
            // differently (which LVS calls a warning, comparing by topology) is exactly the difference that wires a part wrong.
            var errors = r.Findings.Where(f => !f.Waived && (f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Error
                || f.Severity == CircuitRF.Diagnostics.DiagnosticSeverity.Warning && f.Id.StartsWith("lvs.device.", StringComparison.Ordinal))).ToList();
            foreach (var f in errors.Take(8)) list.Add($"LVS: {f.Diagnostic.Render()}");
            if (errors.Count > 8) list.Add($"LVS found {errors.Count - 8} more difference(s); run Layout ▸ LVS to see them all.");
            if (errors.Count > 0)
                list.Insert(0, $"The layout and the schematic differ in {errors.Count} place(s), so a part or port may be wired to the wrong SnP pin.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException
                                      or ArgumentException or InvalidOperationException) { }
        return list;
    }

    /// <param name="PortCount">The cell's Ports (what an S-parameter analysis drives).</param>
    /// <param name="Joined">The schematic's Ports and Pins joined to an EM port.</param>
    /// <param name="Parts">The parts placed on component ports.</param>
    /// <param name="HasPins">The cell has Pins: it is placed in a test bench, by its symbol.</param>
    private sealed record Built(int PortCount, int Joined, int Parts, bool HasPins);

    /// <summary>A pin: its component, its index and where it is.</summary>
    private sealed record PinRef(EditableComponent Comp, int Index, double X, double Y);

    /// <summary>
    /// Turns <paramref name="model"/> (a copy of the Primary schematic) into the cell's schematic in place: D4 to D8.
    /// </summary>
    private static Built Build(SchematicEditModel model, EmSetup setup, string storedFile, IReadOnlyList<EmCoSimPort> ports,
                               EmCoSimModels models, IReadOnlyDictionary<int, EmPortFace> faces, List<string> warnings)
    {
        string? schematicDir = model.SchematicDirectory;
        var byName = new Dictionary<string, EditableComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in model.Components)
            if (c.InstanceName is { Length: > 0 } nm) byName.TryAdd(nm, c);

        var closures = EmCoSimNetlist.Closures(ports, models);
        var removed = new HashSet<EditableComponent>(ReferenceEqualityComparer.Instance);
        // Each re-tied pin and what it goes to: an SnP node, "0" (ground) or "" (left open).
        var retied = new Dictionary<(EditableComponent, int), string>();
        var added = new List<(EditableComponent Comp, string[] Targets)>();
        var classified = new HashSet<EditableComponent>(ReferenceEqualityComparer.Instance);
        int parts = 0;

        // ── The parts on component ports (D5's component-port row): the netlist's own decision ──
        var pendingPorts = new List<EmCoSimPort>();
        var connectors = new List<EmCoSimPort>();
        foreach (var cl in closures)
        {
            if (cl.Kind == EmCoSimCloseKind.PortLine)
            {
                if (cl.Port.Component is { Connector: true }) connectors.Add(cl.Port);
                else pendingPorts.Add(cl.Port);
                continue;
            }
            var comp = cl.SchematicName is { } sn ? byName.GetValueOrDefault(sn) : null;
            switch (cl.Kind)
            {
                case EmCoSimCloseKind.Model when comp is not null:
                    for (int i = 0; i < cl.Instance!.NetBindings.Count; i++)
                        retied[(comp, i)] = cl.Instance.NetBindings[i] is var nb && nb.StartsWith("nc_", StringComparison.Ordinal) ? "" : nb;
                    classified.Add(comp);
                    parts++;
                    break;
                case EmCoSimCloseKind.Open or EmCoSimCloseKind.Short when comp is not null:
                    retied[(comp, 0)] = EmCoSimNetlist.Node(cl.Port.Number);
                    retied[(comp, 1)] = "0";
                    classified.Add(comp);
                    parts++;
                    break;
                case EmCoSimCloseKind.Placeholder:
                    // A schematic part the wiring cannot read stays where it is, its pins open; the placeholder stands across the port.
                    if (comp is not null) classified.Add(comp);
                    warnings.Add(cl.Comment!);
                    added.Add((PlaceholderPart(cl, model), [cl.Instance!.NetBindings[0], "0"]));
                    parts++;
                    break;
                default:
                    warnings.Add($"{cl.Refdes} is on a component port but the schematic has no component of that name, so its port is left open.");
                    break;
            }
        }

        // ── The schematic's Ports, by Num (D6) ──
        // A Port, or a cell's own Pin: a cell made to be placed in a test bench marks the board's ports with Pins, numbered the
        // same way, and its EM ports are those Pins'.
        var schematicPorts = model.Components.Where(IsPortOrPin).ToList();
        var matched = new Dictionary<EditableComponent, EmCoSimPort>(ReferenceEqualityComparer.Instance);
        var usedNums = new HashSet<int>();
        foreach (var sp in schematicPorts)
        {
            classified.Add(sp);
            if (!int.TryParse(PortPlacement.NumberText(sp), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
            {
                warnings.Add($"{Noun(sp)} {sp.InstanceName} states its number as '{PortPlacement.NumberText(sp)}', so no EM port could be matched to it; it is left as it was.");
                continue;
            }
            usedNums.Add(num);
            if (pendingPorts.FirstOrDefault(p => p.Num == num) is { } em)
            {
                matched[sp] = em;
                pendingPorts.Remove(em);
                retied[(sp, 0)] = EmCoSimNetlist.Node(em.Number);
            }
        }

        // A connector's component port is the Port on the connector's net: the connector itself is bridged, as the netlist bridges it.
        foreach (var cp in connectors)
        {
            var part = cp.Component!;
            var comp = byName.GetValueOrDefault(part.SchematicId ?? part.Refdes) ?? byName.GetValueOrDefault(part.Refdes);
            EditableComponent? port = null;
            if (comp is not null)
            {
                removed.Add(comp);
                classified.Add(comp);
                var nets = models.Instances.GetValueOrDefault(comp.InstanceName)?.NetBindings ?? [];
                port = schematicPorts.FirstOrDefault(sp => !matched.ContainsKey(sp)
                    && models.Instances.GetValueOrDefault(sp.InstanceName) is { NetBindings.Count: > 0 } pi && nets.Contains(pi.NetBindings[0]));
            }
            if (port is not null)
            {
                matched[port] = cp;
                retied[(port, 0)] = EmCoSimNetlist.Node(cp.Number);
            }
            else pendingPorts.Add(cp);
        }

        foreach (var sp in schematicPorts.Where(sp => !matched.ContainsKey(sp) && int.TryParse(PortPlacement.NumberText(sp), out _)))
            warnings.Add($"{Noun(sp)} {sp.InstanceName} (Num {PortPlacement.NumberText(sp)}) matches no port of the EM result, so it is left open.");

        // ── Everything else, by what it is to the EM result ──
        var footprintNoPort = new List<EditableComponent>();
        var offBoard = new List<EditableComponent>();
        foreach (var c in model.Components)
        {
            if (classified.Contains(c) || !SchematicLayoutPlanner.IsPhysical(c) || c.InstanceName.Length == 0) continue;
            switch (EmPartKinds.Of(c, schematicDir))
            {
                case EmPartKind.Copper:   removed.Add(c); break;
                case EmPartKind.Part:     footprintNoPort.Add(c); break;
                case EmPartKind.NoLayout: offBoard.Add(c); break;
            }
        }

        // ── Connectivity of the sheet as drawn ──
        var pins = new List<PinRef>();
        foreach (var c in model.Components)
            foreach (var d in model.PortDefsOf(c).OrderBy(d => d.PortIndex))
            {
                if (c.IsPortDetached(d.PortIndex)) continue;
                var (x, y) = SchematicGeometry.LocalToWorld(d.LocalX, d.LocalY, c.X, c.Y, c.Rotation, c.MirrorX);
                pins.Add(new PinRef(c, d.PortIndex, x, y));
            }
        var net = new Connectivity(model, pins);

        bool IsGroundSymbol(EditableComponent c) => c.Symbol == SymbolKind.Ground;
        bool Kept(PinRef p) => !removed.Contains(p.Comp) && !IsGroundSymbol(p.Comp) && !retied.ContainsKey((p.Comp, p.Index));

        // The SnP node each class carries: a matched Port's.
        var classNode = new Dictionary<int, string>();
        foreach (var (sp, em) in matched)
            if (pins.FirstOrDefault(p => ReferenceEquals(p.Comp, sp) && p.Index == 0) is { } pp)
                classNode.TryAdd(net.ClassOf(pp), EmCoSimNetlist.Node(em.Number));

        var deleted = new HashSet<int>();
        foreach (int cls in net.Classes)
        {
            var members = net.PinsIn(cls).ToList();
            bool ground = members.Any(p => IsGroundSymbol(p.Comp)) || net.LabelNamesIn(cls).Any(IsGroundName);
            bool touched = members.Any(p => removed.Contains(p.Comp) || retied.ContainsKey((p.Comp, p.Index)));
            // A ground stays when a kept pin is on it, or a part pin that is to go to ground anyway: the user's own ground symbol
            // then serves, rather than a new one on a stub.
            if (ground ? !members.Any(p => Kept(p) || retied.GetValueOrDefault((p.Comp, p.Index)) == "0") : touched) deleted.Add(cls);
        }

        // A pin re-tied to something other than ground cannot stay on a ground the sheet keeps.
        foreach (var ((comp, idx), target) in retied)
        {
            if (pins.FirstOrDefault(p => ReferenceEquals(p.Comp, comp) && p.Index == idx) is not { } pin) continue;
            int cls = net.ClassOf(pin);
            if (deleted.Contains(cls)) continue;
            if (target == "0") retied[(comp, idx)] = "=";      // already on ground: nothing to add
            else
                warnings.Add($"{comp.InstanceName}'s pin {idx + 1} is drawn on ground together with other parts, so it could not be joined to its SnP pin; redraw it.");
        }

        // Kept pins on a removed net: joined to the SnP node the net carries, or left open and said.
        var open = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var openFootprint = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pins)
        {
            if (!Kept(p) || !deleted.Contains(net.ClassOf(p))) continue;
            if (classNode.TryGetValue(net.ClassOf(p), out string? node)) { retied[(p.Comp, p.Index)] = node; continue; }
            if (IsPortOrPin(p.Comp)) continue;          // said above
            if (footprintNoPort.Contains(p.Comp)) openFootprint.Add(p.Comp.InstanceName);
            else open.Add(p.Comp.InstanceName);
        }
        foreach (string name in openFootprint)
            warnings.Add(model.Components.FirstOrDefault(c => c.InstanceName == name) is { Footprint: { Length: > 0 } }
                ? $"{name} has a footprint but no component port, so the EM result has no port at its pads and its pins are left open."
                // brief-icp-9 — a device cell (a kit device, or a cell whose schematic is a model) has pins, not pads.
                : $"{name} is a device with no component port, so the EM result has no port at its pins and they are left open.");
        foreach (string name in open)
            warnings.Add($"{name} is not on the board and was joined only to copper the EM result now stands for, so those pins are left open.");

        // ── Remove ──
        var deadWires = new HashSet<string>(net.WiresIn(deleted));
        var deadVertices = model.Wires.Where(w => deadWires.Contains(w.Id)).SelectMany(w => w.Points).Select(Key).ToHashSet();
        var liveVertices = model.Wires.Where(w => !deadWires.Contains(w.Id)).SelectMany(w => w.Points).Select(Key).ToHashSet();
        var deadLabels = net.LabelsIn(deleted).ToHashSet(ReferenceEqualityComparer.Instance);
        model.NetLabels.RemoveAll(l => deadLabels.Contains(l) || l.IsAnchored && deadWires.Contains(l.OwnerWireId));
        model.Wires.RemoveAll(w => deadWires.Contains(w.Id));
        model.Dots.RemoveAll(d => deadVertices.Contains(Key((d.X, d.Y))) && !liveVertices.Contains(Key((d.X, d.Y))));
        var groundsGone = pins.Where(p => IsGroundSymbol(p.Comp) && deleted.Contains(net.ClassOf(p))).Select(p => p.Comp).ToHashSet(ReferenceEqualityComparer.Instance);
        var removedBox = Bounds(removed);
        model.Components.RemoveAll(c => removed.Contains(c) || groundsGone.Contains(c));
        Separate(model, retied, warnings);

        // ── The SnP block (D8) ──
        var names = new HashSet<string>(model.Components.Select(c => c.InstanceName), StringComparer.OrdinalIgnoreCase);
        string snpName = Unique(SnpInstanceName, names);
        var config = EmSnpSchematic.PinConfigFor(ports.Count, k => faces.TryGetValue(ports[k - 1].Number, out var f) ? f : EmSnpSchematic.FallbackFace(k));
        var block = EmSnpSchematic.SnpComponent(storedFile, ports.Count, config, 0, 0, snpName);
        var local = model.PortDefsOf(block).OrderBy(d => d.PortIndex).ToList();
        double minX = local.Min(d => (double)d.LocalX) - Stub - 300, maxX = local.Max(d => (double)d.LocalX) + Stub + 300;
        double minY = Math.Min(local.Min(d => (double)d.LocalY), -100) - Stub - 200, maxY = Math.Max(local.Max(d => (double)d.LocalY), 100) + Stub + 200;
        var (bx, by) = Place(model, removedBox, (minX, minY, maxX, maxY));
        block.X = bx; block.Y = by;
        // What a new wire must not touch, taken before the block joins the sheet (its own pins are where its stubs start).
        var sheet = new Sheet(model);
        model.Components.Add(block);
        sheet.AddBlock(model, block);

        // The label names: p<k>, or em_p<k> when the schematic already names a net so.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in model.NetLabels) taken.Add(l.Name);
        foreach (var inst in models.Instances.Values) foreach (var nb in inst.NetBindings) taken.Add(nb);
        bool prefix = ports.Any(p => taken.Contains(EmCoSimNetlist.Node(p.Number)));
        string Label(string node) => prefix ? "em_" + node : node;

        for (int i = 0; i < ports.Count && i < local.Count; i++)
        {
            var (px, py) = SchematicGeometry.LocalToWorld(local[i].LocalX, local[i].LocalY, bx, by, SymbolRotation.R0, false);
            var (end, dir) = AddStub(model, sheet, px, py, Outward(local[i].LocalX, local[i].LocalY));
            if (pendingPorts.Contains(ports[i]))
            {
                int num = NextNum(usedNums, ports[i].Num);
                model.Components.Add(NewPort(model, end, dir, num, ports[i].Z0, names));
                warnings.Add($"EM port {ports[i].Number} ({ports[i].Name}) matches no Port of the schematic, so Port {num} was added for it.");
            }
            AddLabel(model, end, Label(EmCoSimNetlist.Node(ports[i].Number)));
        }

        // ── Re-tied pins: a stub and the SnP node's label, or a ground ──
        foreach (var ((comp, idx), target) in retied)
        {
            if (target is "" or "=" || removed.Contains(comp)) continue;
            // Where the pin is now: Separate may have moved its part.
            if (PinsOf(model, comp).FirstOrDefault(p => p.Index == idx) is not { } pin) continue;
            Tie(model, sheet, pin.X, pin.Y, Outward(pin.X - comp.X, pin.Y - comp.Y), target, Label);
        }

        // ── Placeholders, in a row under the block ──
        double rowY = by + maxY + 600, rowX = bx + minX;
        foreach (var (comp, targets) in added)
        {
            comp.InstanceName = Unique(comp.InstanceName, names);
            comp.X = model.SnapToGrid(rowX); comp.Y = model.SnapToGrid(rowY);
            rowX += 800;
            model.Components.Add(comp);
            sheet.AddBody(comp);
            var defs = model.PortDefsOf(comp).OrderBy(d => d.PortIndex).ToList();
            for (int k = 0; k < defs.Count && k < targets.Length; k++)
            {
                var (x, y) = SchematicGeometry.LocalToWorld(defs[k].LocalX, defs[k].LocalY, comp.X, comp.Y, comp.Rotation, comp.MirrorX);
                Tie(model, sheet, x, y, Outward(x - comp.X, y - comp.Y), targets[k], Label);
            }
        }

        // ── D4: analyses, results, provenance ──
        foreach (var a in model.Analyses.Where(a => a.Model == AnalysisModel.Em)) a.Model = AnalysisModel.Circuit;
        // Only a cell with a Port can drive one: a cell whose board ports are its Pins is placed in a test bench, as its original is.
        if (!model.Analyses.OfType<SParameterAnalysis>().Any() && model.Components.Any(PortPlacement.IsPort))
        {
            var used = new HashSet<string>(model.Analyses.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            model.Analyses.Add(new SParameterAnalysis(Unique(EmSnpSchematic.AnalysisName, used), setup.FrequencyPlan));
        }
        model.ResultsFileName = null;
        model.ArtworkSource = null;
        model.ImageSource = null;
        model.SchematicImportSource = null;
        return new Built(model.Components.Count(PortPlacement.IsPort), matched.Count, parts,
                         model.Components.Any(c => c.Symbol == SymbolKind.Pin));
    }

    private static bool IsPortOrPin(EditableComponent c) => PortPlacement.IsPort(c) || c.Symbol == SymbolKind.Pin;

    /// <summary>A component's attached pins where it stands now.</summary>
    private static IEnumerable<PinRef> PinsOf(SchematicEditModel model, EditableComponent c)
    {
        foreach (var d in model.PortDefsOf(c).OrderBy(d => d.PortIndex))
        {
            if (c.IsPortDetached(d.PortIndex)) continue;
            var (x, y) = SchematicGeometry.LocalToWorld(d.LocalX, d.LocalY, c.X, c.Y, c.Rotation, c.MirrorX);
            yield return new PinRef(c, d.PortIndex, x, y);
        }
    }

    /// <summary>
    /// Pins drawn touching (two parts placed end to end, with no wire between them) are one net however each is re-tied: a stub
    /// from that point joins both, so two parts on two component ports came out on one SnP node. Where pins at one point are
    /// going to different places, a part whose every pin is re-tied anyway is moved clear — away from the point first, then to
    /// either side — onto a spot where none of its pins lands on anything. A part that cannot move, or finds no spot, is named.
    /// </summary>
    private static void Separate(SchematicEditModel model, Dictionary<(EditableComponent, int), string> retied, List<string> warnings)
    {
        // Where a pin goes: its re-tied node, ground, a pin of its own when left open, or the net it is drawn on.
        string Goes(PinRef p) => retied.TryGetValue((p.Comp, p.Index), out var t)
            ? t switch { "" => $"open:{p.Comp.Id}:{p.Index}", "=" => "0", _ => t }
            : "drawn";
        bool Movable(EditableComponent c) => c.Symbol != SymbolKind.Ground
            && PinsOf(model, c).All(p => retied.TryGetValue((p.Comp, p.Index), out var t) && t != "=");

        var moved = new HashSet<EditableComponent>(ReferenceEqualityComparer.Instance);
        var given = new HashSet<(long, long)>();
        double step = model.GridSize > 0 ? model.GridSize : 100;
        while (true)
        {
            var clash = model.Components.Where(c => c.Symbol != SymbolKind.Ground).SelectMany(c => PinsOf(model, c))
                .GroupBy(p => Key((p.X, p.Y)))
                .FirstOrDefault(g => !given.Contains(g.Key) && g.Select(p => p.Comp).Distinct(ReferenceEqualityComparer.Instance).Count() > 1
                                     && g.Select(Goes).Distinct(StringComparer.Ordinal).Count() > 1);
            if (clash is null) return;
            var members = clash.ToList();
            // The part that stays: one that cannot move when there is one, else the first.
            var anchor = members.FirstOrDefault(p => !Movable(p.Comp)) ?? members[0];
            bool clear = true;
            foreach (var p in members)
            {
                if (ReferenceEquals(p.Comp, anchor.Comp) || Goes(p) == Goes(anchor)) continue;
                if (moved.Contains(p.Comp) || !Movable(p.Comp) || !Move(model, p, step, retied)) { clear = false; continue; }
                moved.Add(p.Comp);
            }
            if (!clear)
            {
                given.Add(clash.Key);
                warnings.Add($"{string.Join(" and ", members.Select(p => p.Comp.InstanceName).Distinct())} are drawn pin to pin but go to different " +
                             "SnP pins, and could not be moved apart, so they are joined; redraw them apart.");
            }
        }
    }

    /// <summary>Moves <paramref name="pin"/>'s part off its point, onto the first spot where its pins land on nothing.</summary>
    private static bool Move(SchematicEditModel model, PinRef pin, double step, Dictionary<(EditableComponent, int), string> retied)
    {
        var c = pin.Comp;
        var sheet = new Sheet(model, c);
        var away = Outward(c.X - pin.X, c.Y - pin.Y);
        (int, int)[] dirs = [away, (away.DY, away.DX), (-away.DY, -away.DX)];
        foreach (int n in new[] { 2, 4, 6, 8 })
            foreach (var (dx, dy) in dirs)
            {
                double x = c.X + dx * n * step, y = c.Y + dy * n * step;
                if (!sheet.Fits(model, c, x, y)) continue;
                c.X = x; c.Y = y;
                return true;
            }
        return false;
    }

    private static string Noun(EditableComponent c) => c.Symbol == SymbolKind.Pin ? "Pin" : "Port";

    /// <summary>A cell whose ports are Pins is placed by its symbol: the original cell's, copied, so the new cell can stand where the
    /// original is placed, pin for pin. Nothing when the original has none or the new cell already has one.</summary>
    private static void CopySymbol(string? sourceCellDir, string newCellDir, string cellName, List<string> warnings)
    {
        if (sourceCellDir is null) return;
        var source = CellFolder.ResolvePrimary(sourceCellDir, ViewType.Symbol);
        if (source.ResolvedName is not { Length: > 0 } name) return;
        string from = Path.Combine(CellFolder.SubFolderPath(sourceCellDir, ViewType.Symbol), name);
        string dir = CellFolder.SubFolderPath(newCellDir, ViewType.Symbol);
        if (!File.Exists(from) || Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*" + CellFolder.ViewExtension(ViewType.Symbol)).Any()) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(from, Path.Combine(dir, cellName + CellFolder.ViewExtension(ViewType.Symbol)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"The original cell's symbol was not copied, so the new cell cannot be placed until it has one: {e.Message}");
        }
    }

    // ── Pieces ──────────────────────────────────────────────────────────────────────────────

    private static bool IsGroundName(string name) => name is "0" or "GND" or "gnd";

    private static (long, long) Key((double X, double Y) p) => ((long)Math.Round(p.X * 2), (long)Math.Round(p.Y * 2));

    private static string Unique(string name, HashSet<string> names)
    {
        string n = name;
        for (int i = 2; !names.Add(n); i++) n = $"{name}_{i}";
        return n;
    }

    private static int NextNum(HashSet<int> used, int wanted)
    {
        int n = used.Contains(wanted) ? 1 : wanted;
        while (!used.Add(n)) n++;
        return n;
    }

    /// <summary>The way a pin points, from its component's origin: the larger of its offsets, up when it has none.</summary>
    private static (int DX, int DY) Outward(double dx, double dy)
        => Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9 ? (0, -1)
         : Math.Abs(dx) >= Math.Abs(dy) ? (Math.Sign(dx), 0) : (0, Math.Sign(dy));

    /// <summary>
    /// A wire from a pin to where its label or ground goes: the pin's own direction first, then the others, then longer, until it
    /// touches nothing on the sheet — no pin, wire, label, other new wire or component body. A wire's end on another wire IS a
    /// connection, so a stub that merely looked tidy could short an SnP node to ground. With nowhere clear the label sits on the pin
    /// itself (end = the pin), which joins only that pin.
    /// </summary>
    private static ((double X, double Y) End, (int DX, int DY) Dir) AddStub(SchematicEditModel model, Sheet sheet, double x, double y, (int DX, int DY) preferred)
    {
        (int, int)[] dirs = [preferred, (preferred.DY, preferred.DX), (-preferred.DY, -preferred.DX), (-preferred.DX, -preferred.DY)];
        var tries = new[] { 200, 300, 400 }.Select(l => (preferred, l))
            .Concat(new[] { 200, 300, 400, 600, 800, 1200 }.SelectMany(l => dirs.Select(d => (d, l))));
        foreach (var (dir, length) in tries)
        {
            if (!sheet.Clear(x, y, dir, length)) continue;
            var end = (x + dir.Item1 * length, y + dir.Item2 * length);
            var wire = new EditableWire();
            wire.Points.Add((x, y));
            wire.Points.Add(end);
            model.Wires.Add(wire);
            sheet.Take(x, y, dir, length);
            return (end, dir);
        }
        sheet.Reserve(x, y);
        return ((x, y), preferred);
    }

    private static void AddLabel(SchematicEditModel model, (double X, double Y) at, string name)
    {
        var label = new EditableNetLabel { Name = name, X = at.X, Y = at.Y };
        // On its stub when it has one; on the pin itself when there was nowhere clear to draw one.
        if (model.Wires.LastOrDefault() is { } wire && wire.Points.Count == 2 && wire.Points[1] == at) label.AnchorToWire(wire, at.X, at.Y);
        model.NetLabels.Add(label);
    }

    /// <summary>A pin's stub, ending on <paramref name="target"/>'s label, or on a ground symbol for "0".</summary>
    private static void Tie(SchematicEditModel model, Sheet sheet, double x, double y, (int DX, int DY) dir, string target, Func<string, string> label)
    {
        var (end, used) = AddStub(model, sheet, x, y, dir);
        if (target == "0")
        {
            var names = new HashSet<string>(model.Components.Select(c => c.InstanceName), StringComparer.OrdinalIgnoreCase);
            // As a ground is placed by hand: named, its labels hidden.
            var ground = new EditableComponent
            {
                Symbol = SymbolKind.Ground, InstanceName = Enumerable.Range(1, names.Count + 1).Select(k => $"GND{k}").First(n => !names.Contains(n)),
                X = end.X, Y = end.Y, Rotation = Facing(used, 0, 1), ShowTypeLabel = false, ShowInstanceName = false,
            };
            model.Components.Add(ground);
            sheet.Reserve(end.X + used.DX * 100, end.Y + used.DY * 100);
        }
        else AddLabel(model, end, label(target));
    }

    /// <summary>
    /// What a new wire must not touch: every pin, every wire's vertices and segments, every label, every component's body, and
    /// each new wire as it is drawn. Positions are on the connection grid, so a wire is tested at each grid step along it.
    /// </summary>
    private sealed class Sheet
    {
        private readonly HashSet<(long, long)> _points = [];
        private readonly List<((double X, double Y) A, (double X, double Y) B)> _segments = [];
        private readonly List<(double MinX, double MinY, double MaxX, double MaxY)> _bodies = [];
        private readonly double _step;

        /// <param name="except">A component to leave out (one about to be moved).</param>
        public Sheet(SchematicEditModel model, EditableComponent? except = null)
        {
            _step = model.GridSize > 0 ? model.GridSize : 100;
            foreach (var c in model.Components)
            {
                if (ReferenceEquals(c, except)) continue;
                foreach (var d in model.PortDefsOf(c))
                    _points.Add(Key(SchematicGeometry.LocalToWorld(d.LocalX, d.LocalY, c.X, c.Y, c.Rotation, c.MirrorX)));
                AddBody(c);
            }
            foreach (var w in model.Wires)
            {
                foreach (var p in w.Points) _points.Add(Key(p));
                for (int i = 0; i + 1 < w.Points.Count; i++) _segments.Add((w.Points[i], w.Points[i + 1]));
            }
            foreach (var l in model.NetLabels) Reserve(l.X, l.Y);
        }

        /// <summary>The middle of a component's glyph, which no wire may cross (its pins sit on or outside this box).</summary>
        public void AddBody(EditableComponent c)
        {
            if (c.Symbol is SymbolKind.Ground || c.IsSnpBox) return;
            _bodies.Add((c.X - 150, c.Y - 150, c.X + 150, c.Y + 150));
        }

        public void Reserve(double x, double y) => _points.Add(Key((x, y)));

        /// <summary>Whether <paramref name="c"/> stood at (<paramref name="x"/>, <paramref name="y"/>) would touch nothing: no
        /// pin of it on a pin, wire or label, its body clear of every other body, and nothing's pin inside its body.</summary>
        public bool Fits(SchematicEditModel model, EditableComponent c, double x, double y)
        {
            foreach (var d in model.PortDefsOf(c))
            {
                var (px, py) = SchematicGeometry.LocalToWorld(d.LocalX, d.LocalY, x, y, c.Rotation, c.MirrorX);
                if (_points.Contains(Key((px, py))) || OnSegment(px, py)) return false;
            }
            var box = (MinX: x - 150, MinY: y - 150, MaxX: x + 150, MaxY: y + 150);
            if (_bodies.Any(b => b.MinX < box.MaxX && b.MaxX > box.MinX && b.MinY < box.MaxY && b.MaxY > box.MinY)) return false;
            return !_segments.Any(s => Crosses(s.A, s.B, box));
        }

        /// <summary>Whether an axis-aligned segment passes through the inside of a box.</summary>
        private static bool Crosses((double X, double Y) a, (double X, double Y) b, (double MinX, double MinY, double MaxX, double MaxY) box)
            => Math.Max(a.X, b.X) > box.MinX && Math.Min(a.X, b.X) < box.MaxX && Math.Max(a.Y, b.Y) > box.MinY && Math.Min(a.Y, b.Y) < box.MaxY;

        /// <summary>The SnP block: its pins, and the box its pins outline (which its own stubs leave outward).</summary>
        public void AddBlock(SchematicEditModel model, EditableComponent block)
        {
            var at = model.PortDefsOf(block).Select(d => SchematicGeometry.LocalToWorld(d.LocalX, d.LocalY, block.X, block.Y, block.Rotation, block.MirrorX)).ToList();
            foreach (var p in at) _points.Add(Key(p));
            double minX = Math.Min(at.Min(p => p.X), block.X - 100), maxX = Math.Max(at.Max(p => p.X), block.X + 100);
            double minY = Math.Min(at.Min(p => p.Y), block.Y - 100), maxY = Math.Max(at.Max(p => p.Y), block.Y + 100);
            _bodies.Add((minX, minY, maxX, maxY));
        }

        public bool Clear(double x, double y, (int DX, int DY) dir, int length)
        {
            for (double t = _step; t <= length + 1e-9; t += _step)
            {
                double px = x + dir.DX * t, py = y + dir.DY * t;
                if (_points.Contains(Key((px, py))) || OnSegment(px, py) || _bodies.Any(b => px > b.MinX && px < b.MaxX && py > b.MinY && py < b.MaxY))
                    return false;
            }
            // The label at the end reads to the right of it: keep that clear of another label.
            double ex = x + dir.DX * length, ey = y + dir.DY * length;
            return !_points.Contains(Key((ex + _step, ey))) && !_points.Contains(Key((ex - _step, ey)));
        }

        public void Take(double x, double y, (int DX, int DY) dir, int length)
        {
            for (double t = 0; t <= length + 1e-9; t += _step) _points.Add(Key((x + dir.DX * t, y + dir.DY * t)));
            _segments.Add(((x, y), (x + dir.DX * length, y + dir.DY * length)));
            Reserve(x + dir.DX * length + _step, y + dir.DY * length);
        }

        private bool OnSegment(double x, double y)
        {
            foreach (var (a, b) in _segments)
            {
                double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X), minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
                if (x < minX - 0.5 || x > maxX + 0.5 || y < minY - 0.5 || y > maxY + 0.5) continue;
                double cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
                if (Math.Abs(cross) < 0.5 * Math.Max(1, Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y))) return true;
            }
            return false;
        }
    }

    /// <summary>The rotation that turns a glyph's local direction (<paramref name="lx"/>, <paramref name="ly"/>) to <paramref name="dir"/>.</summary>
    private static SymbolRotation Facing((int DX, int DY) dir, float lx, float ly)
    {
        foreach (var r in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 })
        {
            var (x, y) = SchematicGeometry.LocalToWorld(lx, ly, 0, 0, r, false);
            if (Math.Sign(Math.Round(x)) == dir.DX && Math.Sign(Math.Round(y)) == dir.DY) return r;
        }
        return SymbolRotation.R0;
    }

    /// <summary>A Port (a TermG) for an EM port the schematic has none for, its pin on the stub's end and its body beyond.</summary>
    private static EditableComponent NewPort(SchematicEditModel model, (double X, double Y) pin, (int DX, int DY) dir, int num, string z0, HashSet<string> names)
    {
        var rot = Facing(dir, 0, 1);
        var (ox, oy) = SchematicGeometry.LocalToWorld(0f, -200f, 0, 0, rot, false);
        var term = new EditableComponent { Symbol = SymbolKind.TermG, InstanceName = Unique($"Term{num}", names), X = pin.X - ox, Y = pin.Y - oy, Rotation = rot };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.TermG, 0))
        {
            var p = EmSnpSchematic.Param(dp, dp.Name == "Num" ? num.ToString(CultureInfo.InvariantCulture) : dp.Expression);
            if (dp.Name == "Z" && z0.Trim().Length > 0) p.Expression = z0.Trim();
            term.Parameters.Add(p);
        }
        return term;
    }

    /// <summary>A placeholder part for a component port the schematic gives no model the wiring can read.</summary>
    private static EditableComponent PlaceholderPart(EmCoSimClose cl, SchematicEditModel model)
    {
        var (kind, key, _, _) = EmCoSimNetlist.Placeholder(cl.Refdes ?? "");
        var symbol = kind switch { "C" => SymbolKind.Capacitor, "L" => SymbolKind.Inductor, _ => SymbolKind.Resistor };
        var comp = new EditableComponent { Symbol = symbol, InstanceName = cl.Instance!.InstanceName };
        var v = cl.Variable!;
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(symbol, 2))
        {
            var p = EmSnpSchematic.Param(dp, dp.Expression);
            if (dp.Name == key) { p.Expression = v.Expression; p.Unit = v.Unit ?? p.Unit; }
            comp.Parameters.Add(p);
        }
        return comp;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY)? Bounds(IEnumerable<EditableComponent> comps)
    {
        var list = comps.ToList();
        if (list.Count == 0) return null;
        var boxes = list.Select(c => c.GetBoundingBox()).ToList();
        return (boxes.Min(b => b.MinX), boxes.Min(b => b.MinY), boxes.Max(b => b.MaxX), boxes.Max(b => b.MaxY));
    }

    /// <summary>D8 — where the block goes: centred on the removed copper, moved down until it covers no component; right of the
    /// sheet when nothing was removed or nothing near is clear.</summary>
    private static (double X, double Y) Place(SchematicEditModel model, (double MinX, double MinY, double MaxX, double MaxY)? copper,
                                              (double MinX, double MinY, double MaxX, double MaxY) extent)
    {
        // A component's glyph and the labels drawn beside and below it, roughly: the glyph's box alone let a stub land on a part's value.
        var boxes = model.Components.Select(c => c.GetBoundingBox()).Select(b => (MinX: b.MinX - 100, MinY: b.MinY - 100, MaxX: b.MaxX + 500, MaxY: b.MaxY + 400)).ToList();
        bool Clear(double x, double y) => !boxes.Any(b => b.MinX < x + extent.MaxX && b.MaxX > x + extent.MinX && b.MinY < y + extent.MaxY && b.MaxY > y + extent.MinY);
        if (copper is { } c)
        {
            double cx = model.SnapToGrid((c.MinX + c.MaxX) / 2), cy = model.SnapToGrid((c.MinY + c.MaxY) / 2);
            for (int i = 0; i < 40; i++)
            {
                double y = cy + i * model.GridSize * 2;
                if (Clear(cx, y)) return (cx, y);
            }
        }
        if (boxes.Count == 0) return (0, 0);
        double rx = model.SnapToGrid(boxes.Max(b => b.MaxX) - extent.MinX + 400);
        double ry = model.SnapToGrid((boxes.Min(b => b.MinY) + boxes.Max(b => b.MaxY)) / 2);
        return (rx, ry);
    }

    private static string Z0Text(Complex z)
    {
        string re = z.Real.ToString("G15", CultureInfo.InvariantCulture);
        if (z.Imaginary == 0) return re;
        string im = Math.Abs(z.Imaginary).ToString("G15", CultureInfo.InvariantCulture);
        return z.Imaginary > 0 ? $"{re}+j{im}" : $"{re}-j{im}";
    }

    /// <summary>D10 — the Primary schematic's own Data Display, repointed to this cell's results; without one, the S-parameter
    /// template when the cell's only analysis is an S-parameter one.</summary>
    private static void WriteDisplay(string workspaceDir, string primary, string schematic, SchematicEditModel model, int portCount, List<string> warnings)
    {
        string authored = ResultsWriter.AuthoredDisplayPath(workspaceDir, ResultsWriter.SchematicKey(primary));
        try
        {
            if (File.Exists(authored))
                CellCreate.WriteTemplateDataDisplay(workspaceDir, schematic, null, File.ReadAllText(authored));
            else if (portCount > 0 && model.Analyses.Count == 1 && model.Analyses[0] is SParameterAnalysis)
                CellCreate.WriteTemplateDataDisplay(workspaceDir, schematic, null, EmSnpSchematic.DataDisplayJson(portCount, "em_sch.npy"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            warnings.Add($"The schematic's Data Display was not copied: {e.Message}");
        }
    }

    // ── Connectivity ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sheet's nets as drawn, by geometry: wires joined as extraction joins them (<see cref="NetExtractor.ConnectedWireIds"/>),
    /// a pin on a wire's vertex, pins on one point, and net labels of one name. What is kept and what is removed is decided per
    /// class here; the extraction's own names are not needed for it.
    /// </summary>
    private sealed class Connectivity
    {
        private readonly int[] _parent;
        private readonly List<PinRef> _pins;
        private readonly int[] _pinItem;
        private readonly Dictionary<string, int> _wireItem = new(StringComparer.Ordinal);
        private readonly List<(EditableNetLabel Label, int Item)> _labels = [];

        public Connectivity(SchematicEditModel model, List<PinRef> pins)
        {
            _pins = pins;
            int count = 0;
            var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var w in model.Wires)
            {
                if (groupOf.ContainsKey(w.Id)) continue;
                int g = count++;
                foreach (string id in NetExtractor.ConnectedWireIds(model, [w.Id])) groupOf[id] = g;
            }
            foreach (var (id, g) in groupOf) _wireItem[id] = g;
            _pinItem = new int[pins.Count];
            for (int i = 0; i < pins.Count; i++) _pinItem[i] = count++;
            var labelNames = model.NetLabels.Select(l => l.Name).Distinct(StringComparer.Ordinal).ToList();
            var nameItem = labelNames.ToDictionary(n => n, _ => count++, StringComparer.Ordinal);
            _parent = Enumerable.Range(0, count).ToArray();

            var vertexGroup = new Dictionary<(long, long), int>();
            foreach (var w in model.Wires)
                foreach (var p in w.Points) vertexGroup.TryAdd(Key(p), _wireItem[w.Id]);
            var pointPin = new Dictionary<(long, long), int>();
            for (int i = 0; i < pins.Count; i++)
            {
                var k = Key((pins[i].X, pins[i].Y));
                if (vertexGroup.TryGetValue(k, out int g)) Union(_pinItem[i], g);
                if (pointPin.TryGetValue(k, out int other)) Union(_pinItem[i], other); else pointPin[k] = _pinItem[i];
            }
            foreach (var l in model.NetLabels)
            {
                int item = nameItem[l.Name];
                if (l.IsAnchored && _wireItem.TryGetValue(l.OwnerWireId, out int g)) Union(item, g);
                else if (vertexGroup.TryGetValue(Key((l.X, l.Y)), out int g2)) Union(item, g2);
                else if (pointPin.TryGetValue(Key((l.X, l.Y)), out int pin)) Union(item, pin);
                _labels.Add((l, item));
            }
        }

        private int Find(int x) { while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]]; return x; }
        private void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[a] = b; }

        public int ClassOf(PinRef p) => Find(_pinItem[_pins.IndexOf(p)]);

        public IEnumerable<int> Classes => _pinItem.Select(Find).Concat(_wireItem.Values.Select(Find)).Distinct();

        public IEnumerable<PinRef> PinsIn(int cls) => _pins.Where((_, i) => Find(_pinItem[i]) == cls);

        public IEnumerable<string> LabelNamesIn(int cls) => _labels.Where(l => Find(l.Item) == cls).Select(l => l.Label.Name);

        public IEnumerable<EditableNetLabel> LabelsIn(HashSet<int> classes) => _labels.Where(l => classes.Contains(Find(l.Item))).Select(l => l.Label);

        public IEnumerable<string> WiresIn(HashSet<int> classes) => _wireItem.Where(w => classes.Contains(Find(w.Value))).Select(w => w.Key);
    }
}
