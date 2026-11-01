// The build — brief-schimp-1 R-si1-4/5/6, overview D3–D6.
//
//   ForeignSchematic ──► nets from the source's GEOMETRY (once, for every format) ──► TestBench + ExactPlacement
//                                                                                        │
//                                                                  NetlistSchematic.Build ──► SchematicEditModel
//
// ── THE NETS, IN TWO LAYERS ─────────────────────────────────────────────────────────────────────
//
// PHYSICAL: wires joined at their vertices; a wire end, a junction, a pin or a label lying anywhere on a wire joins it;
//   points that coincide join. Two wires merely crossing do not. These groups are what the source DRAWS as one piece.
// LOGICAL: physical groups joined by name — labels of equal text on one sheet (local) or anywhere (global, power,
//   off-page), a hidden power pin to its implicit net, ground names and ground symbols to ground. These are the nets.
//
// The readers supply geometry, never nets. A format that also DECLARES its nets has them compared against what is
// built (ImportConnectivity) — they are never used to build.
//
// ── WHAT IS DRAWN WHERE ─────────────────────────────────────────────────────────────────────────
//
// A symbol cell's symbol is translated so its first pin is the local origin, and the instance is placed with that pin
// at the source's point: its pins are NOT snapped to circuitRF's 100-mil pin grid (a source pitching pins at 50 mils would
// have two land on one point — brief-schimp-2), so every pin lands exactly where the source's did. A native part is fitted to its source pins (the rotation and on-grid origin nearest them); a pin it misses is
// re-joined by NetlistSchematic's one dog-leg, the source wire never moved (D6) — save the stretch a longer native
// body lies over, between its two pins, which is cut away so the wire ends on the pin (TrimUnderBody). A pin the source connects by touching a
// label or another pin, with no wire, is joined by a net label on it. Each physical ground group with wires keeps its
// wires and a ground symbol where the source grounded it; a ground pin with no wire gets a ground symbol on the pin.
//
// ── THE GRID (R-si1-6) ─────────────────────────────────────────────────────────────────────────
//
// circuitRF connects points by quantizing them to the schematic's GridSize. A source on a 50-mil grid puts pins at odd
// multiples of 50, which a 100 grid rounds onto each other (150 and 250 both round to 2 under banker's rounding). So the
// drawing is never snapped (ExactPlacement.SnapToGrid = false) and its GridSize is the largest divisor of 100 every
// source point sits on. See src/Design/RESOLVED.md.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Symbol;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Schematic.Import;

/// <summary>What the import is told beyond the source (R-si1-8, overview D9).</summary>
public sealed record SchematicImportOptions
{
    /// <summary>References or net names to make circuitRF Ports, in port order (overview D9). With one, the default S-parameter
    /// analysis is added.</summary>
    public IReadOnlyList<string> Ports { get; init; } = [];

    /// <summary>References the user pushed back from a native part to a symbol cell (R-si1-3).</summary>
    public IReadOnlyCollection<string> SymbolCells { get; init; } = [];

    /// <summary>The import's date, for the provenance block; null is now.</summary>
    public DateTime? Now { get; init; }
}

/// <summary>A cell holding one source symbol unit's drawing and no model (R-si1-4).</summary>
/// <param name="PinNumbers">The source's pin numbers, in the symbol's port order.</param>
/// <param name="Pins">The symbol's pins, local, in port order — first pin at the origin.</param>
public sealed record ImportSymbolCell(
    string Name, string SymbolKey, int Unit, string SourceName, CircuitRF.Design.Symbol.Symbol Symbol, string Csym,
    IReadOnlyList<string> PinNumbers, IReadOnlyList<(float X, float Y)> Pins);

/// <summary>A cell for one distinct child sheet of a hierarchical design (overview D5): a rectangle symbol whose pins are
/// the sheet's ports, and the sheet's own schematic.</summary>
public sealed record ImportSheetCell(
    string Name, string SheetName, CircuitRF.Design.Symbol.Symbol Symbol, string Csym,
    IReadOnlyList<string> Ports, IReadOnlyList<(float X, float Y)> Pins);

/// <summary>One schematic the build produced: the top one, or a child sheet's (<see cref="SheetCell"/> set).</summary>
public sealed class ImportBuiltSchematic
{
    public required string                 CellName   { get; init; }
    public          ImportSheetCell?        SheetCell  { get; init; }
    public required string                 Directory  { get; init; }
    public required SchematicEditModel      Model      { get; init; }
    public required TestBench               TestBench  { get; init; }
    public required ExactPlacement          Placement  { get; init; }

    /// <summary>Per instance, the source pin each of its ports is — null for a port the source has no pin for.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<ForeignPinRef?>> InstancePins { get; init; }

    /// <summary>Per <c>Pin</c> component's name, the hierarchical port it is (<see cref="ImportConnectivity.PortReference"/>).</summary>
    public required IReadOnlyList<string> PortNames { get; init; }

    /// <summary>The source's own nets in this schematic's scope, as sets of pins — single-pin nets and no-connect pins
    /// already left out.</summary>
    public required IReadOnlyList<IReadOnlySet<ForeignPinRef>> GeometricNets { get; init; }

    /// <summary>Pins in this scope the comparison leaves out on both sides (ground symbols, no-connects).</summary>
    public required IReadOnlySet<ForeignPinRef> Excluded { get; init; }

    /// <summary>The source's pins on ground in this scope — one net with every other scope's, which a hierarchy's check
    /// needs when it joins its cells (<see cref="ImportConnectivity.Flatten"/>).</summary>
    public IReadOnlySet<ForeignPinRef> GroundPins { get; init; } = new HashSet<ForeignPinRef>();
}

/// <summary>What <see cref="SchematicImportBuild.Build"/> made, in memory: nothing is written.</summary>
public sealed record ImportBuildResult(
    IReadOnlyList<ImportBuiltSchematic> Schematics,
    IReadOnlyList<ImportSymbolCell> SymbolCells,
    IReadOnlyList<ImportSheetCell> SheetCells,
    IReadOnlyList<ImportPartDecision> Mapping,
    IReadOnlyDictionary<string, string> Renamed,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Refusals)
{
    public bool Ok => Refusals.Count == 0;
    public ImportBuiltSchematic? Top => Schematics.Count > 0 ? Schematics[0] : null;

    /// <summary>The source references drawn as circuitRF Ports (overview D9) — parts the map made symbol cells that no
    /// cell was written for.</summary>
    public IReadOnlyList<string> PortParts { get; init; } = [];
}

public static class SchematicImportBuild
{
    private const double P = NetlistSchematic.Grid;

    /// <summary>Gap between tiled pages, in grid squares (overview D5).</summary>
    private const int PageGap = 20;

    /// <summary>
    /// Builds <paramref name="source"/> into its schematic(s) and cells, in memory (R-si1-5). <paramref name="schematicDirectory"/>
    /// is where the top schematic will be written and <paramref name="partsDirectory"/> the folder its symbol and sheet
    /// cells go in (overview D4) — both absolute, neither touched, both needed because a <c>CellRef</c> is relative.
    /// </summary>
    public static ImportBuildResult Build(ForeignSchematic source, string topCellName, string schematicDirectory,
                                          string partsDirectory, SchematicImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new SchematicImportOptions();
        var notes = new List<string>(source.Notes);
        var refusals = new List<string>();

        if (source.Sheet(source.RootSheet) is null)
            return Refuse($"The design names its top sheet '{source.RootSheet}', which it does not contain.");

        source = UniqueReferences(source, notes);
        var mapping = SchematicImportMap.Decide(source, options.SymbolCells);
        var byRef = mapping.ToDictionary(d => d.Reference, StringComparer.Ordinal);
        foreach (var d in mapping) if (d.Note is { } n) notes.Add(n);

        // ── the symbol cells and the sheet cells ─────────────────────────────────────────────────
        var cellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var symbolCells = new Dictionary<(string Key, int Unit), ImportSymbolCell>();
        foreach (var sheet in source.Sheets)
            foreach (var inst in sheet.Instances)
            {
                // A connector asked to be a Port gets its cell too: whether it CAN be one is known only once the nets are.
                if (byRef[inst.Reference].Becomes != ImportPartKind.SymbolCell || symbolCells.ContainsKey((inst.SymbolKey, inst.Unit))) continue;
                if (source.Symbol(inst.SymbolKey) is not { } symbol || symbol.Unit(inst.Unit) is not { Pins.Count: > 0 } unit)
                {
                    refusals.Add($"{inst.Reference}: its symbol '{inst.SymbolKey}' (unit {inst.Unit}) has no pins in the design.");
                    continue;
                }
                string name = Unique(SchematicImportMap.CellNameFor(symbol.DisplayName) + (symbol.UnitCount > 1 ? "_" + UnitLetter(unit.Unit) : ""), cellNames);
                if (SymbolCellOf(symbol, unit, name, notes) is { } cell) symbolCells[(inst.SymbolKey, inst.Unit)] = cell;
                else refusals.Add($"{inst.Reference}: its symbol '{symbol.DisplayName}' could not be drawn.");
            }

        var sheetCells = new Dictionary<string, ImportSheetCell>(StringComparer.Ordinal);
        if (source.Hierarchical)
            foreach (var sheet in source.Sheets)
                foreach (var si in sheet.SheetInstances)
                {
                    if (sheetCells.ContainsKey(si.SheetName)) continue;
                    if (source.Sheet(si.SheetName) is null) { refusals.Add($"Sheet '{si.Name}' shows '{si.SheetName}', which the design does not contain."); continue; }
                    if (SheetCellOf(si, Unique(SchematicImportMap.CellNameFor(si.SheetName), cellNames), notes) is { } cell) sheetCells[si.SheetName] = cell;
                    else refusals.Add($"Sheet '{si.Name}' has no ports, so it has nothing to connect to its parent.");
                }
        if (refusals.Count > 0) return Refuse();

        int grid = GridOf(source);
        if (grid < (int)P)
            notes.Add($"The source is drawn on a {grid}-mil grid, finer than circuitRF's {P:0}; the schematic keeps its points "
                    + $"exactly, on a {grid}-unit connection grid.");

        // ── the schematics ───────────────────────────────────────────────────────────────────────
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var built = new List<ImportBuiltSchematic>();
        var portParts = new List<string>();
        var scopes = new List<(IReadOnlyList<ForeignSheet> Sheets, ImportSheetCell? Cell)>();
        if (!source.Hierarchical) scopes.Add(([.. source.Pages], null));
        else
            foreach (var s in source.Pages)
                if (s.Name == source.RootSheet) scopes.Add(([s], null));
                else if (sheetCells.TryGetValue(s.Name, out var c)) scopes.Add(([s], c));
        if (source.Hierarchical)
            foreach (var unused in source.Sheets.Where(s => s.Name != source.RootSheet && !sheetCells.ContainsKey(s.Name)))
                notes.Add($"Sheet '{unused.Name}' is placed by no other sheet and was not imported.");

        foreach (var (sheets, sheetCell) in scopes)
        {
            string cellName = sheetCell?.Name ?? topCellName;
            string dir = sheetCell is null ? schematicDirectory : Path.Combine(partsDirectory, sheetCell.Name, CellFolder.SubFolderName(ViewType.Schematic));
            var scope = new Scope(source, sheets, sheetCell, byRef, symbolCells, sheetCells, grid, dir, partsDirectory, options, notes, renamed);
            if (scope.Build() is { } schematic) built.Add(schematic);
            else { refusals.AddRange(scope.Refusals); return Refuse(); }
            portParts.AddRange(scope.PortParts);
        }

        if (source.Hierarchical)
            foreach (var name in GlobalNamesAcrossSheets(source))
                notes.Add($"The global net '{name}' is used on more than one sheet; circuitRF keeps a cell's nets to that cell, so "
                        + "the sheets' copies of it are not joined.");

        // A connector drawn as a Port needs no cell: keep only the cells some drawn instance uses.
        var drawnKeys = source.Sheets.SelectMany(s => s.Instances).Where(i => !portParts.Contains(i.Reference))
                              .Select(i => (i.SymbolKey, i.Unit)).ToHashSet();
        var usedCells = symbolCells.Where(kv => drawnKeys.Contains(kv.Key)).Select(kv => kv.Value).ToList();

        return new ImportBuildResult(built, usedCells, [.. sheetCells.Values], mapping, renamed, notes, refusals)
        { PortParts = portParts };

        ImportBuildResult Refuse(string? why = null)
        {
            if (why is not null) refusals.Add(why);
            return new ImportBuildResult([], [], [], [], new Dictionary<string, string>(), notes, refusals);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  One schematic's worth: a flat design's pages together, or one sheet of a hierarchical one
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Scope(
        ForeignSchematic source, IReadOnlyList<ForeignSheet> sheets, ImportSheetCell? sheetCell,
        IReadOnlyDictionary<string, ImportPartDecision> byRef,
        IReadOnlyDictionary<(string Key, int Unit), ImportSymbolCell> symbolCells,
        IReadOnlyDictionary<string, ImportSheetCell> sheetCells, int grid, string directory, string partsDirectory,
        SchematicImportOptions options, List<string> notes, Dictionary<string, string> renamed)
    {
        public List<string> Refusals { get; } = [];
        public List<string> PortParts { get; } = [];

        private readonly UnionFind _physical = new();
        private readonly UnionFind _logical = new();
        private readonly List<Node> _nodes = [];
        private readonly HashSet<int> _wiredGroups = [];
        private readonly HashSet<int> _noConnectGroups = [];
        private readonly HashSet<int> _groundRoots = [];

        private enum NodeKind { Wire, Pin, Label, Junction, SheetPin, NoConnect }

        private sealed record Node(NodeKind Kind, int Sheet, (double X, double Y) At)
        {
            public ForeignWire?          Wire  { get; init; }
            public ForeignLabel?         Label { get; init; }
            public PlacedPin?            Pin   { get; init; }
            public (int Inst, string Port)? SheetPin { get; init; }
        }

        /// <summary>One pin of one placed unit, where it lands (page offset applied).</summary>
        private sealed record PlacedPin(int Sheet, ForeignInstance Inst, ForeignPin Pin, string Number, (double X, double Y) At);

        public ImportBuiltSchematic? Build()
        {
            var offsets = PageOffsets();

            // ── nodes ──────────────────────────────────────────────────────────────────────────
            var pinNodes = new List<int>();
            for (int s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                var off = offsets[s];
                foreach (var w in sheet.Wires.Where(w => w.Points.Count >= 1))
                    Add(new Node(NodeKind.Wire, s, Shift(w.Points[0], off)) { Wire = w with { Points = [.. w.Points.Select(p => Shift(p, off))] } });
                foreach (var j in sheet.Junctions) Add(new Node(NodeKind.Junction, s, Shift((j.X, j.Y), off)));
                foreach (var l in sheet.Labels) Add(new Node(NodeKind.Label, s, Shift((l.X, l.Y), off)) { Label = l });
                foreach (var nc in sheet.NoConnects) Add(new Node(NodeKind.NoConnect, s, Shift((nc.X, nc.Y), off)));
                foreach (var inst in sheet.Instances)
                    foreach (var pin in PinsOf(s, inst, off))
                        pinNodes.Add(Add(new Node(NodeKind.Pin, s, pin.At) { Pin = pin }));
                for (int i = 0; i < sheet.SheetInstances.Count; i++)
                    foreach (var sp in sheet.SheetInstances[i].Pins)
                        Add(new Node(NodeKind.SheetPin, s, Shift((sp.X, sp.Y), off)) { SheetPin = (i, sp.Name) });
            }

            JoinPhysically();
            JoinLogically();
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Kind == NodeKind.Wire) _wiredGroups.Add(_physical.Find(n));
                if (_nodes[n].Kind == NodeKind.NoConnect) _noConnectGroups.Add(_physical.Find(n));
                if (IsGroundMark(_nodes[n])) _groundRoots.Add(_logical.Find(n));
            }

            // ── names ──────────────────────────────────────────────────────────────────────────
            var naming = NameNets(pinNodes);
            if (naming is null) return null;
            var (netOf, quiet, groundGroups) = naming.Value;

            // ── the netlist and its drawing ─────────────────────────────────────────────────────
            var tb = new TestBench(sheetCell?.Name ?? "import");
            var exactInstances = new Dictionary<string, ExactInstance>(StringComparer.Ordinal);
            var cells = new Dictionary<string, ExactCell>(StringComparer.Ordinal);
            var joinByLabel = new HashSet<(string, int)>();
            var instancePins = new Dictionary<string, IReadOnlyList<ForeignPinRef?>>(StringComparer.Ordinal);
            var excluded = new HashSet<ForeignPinRef>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var drawnFrom = new Dictionary<string, (ForeignInstance Inst, (double X, double Y) Off, ImportPartDecision Decision)>(StringComparer.Ordinal);
            var nativePins = new Dictionary<string, (Instance Instance, List<int> Nodes)>(StringComparer.Ordinal);

            string NetOfPin(int node) => netOf[node];

            // Where a native pin must not land: on a wire, or a pin, of a net that is not its own.
            var foreign = _nodes.Select((node, n) => (Node: node, Net: netOf.TryGetValue(n, out var net) ? net : null))
                                .Where(x => x.Net is not null && x.Node.Kind is NodeKind.Wire or NodeKind.Pin).ToList();
            bool ClashesWithOtherNet((double X, double Y) at, string net) =>
                foreign.Any(x => x.Net != net && (x.Node.Wire is { } w ? OnPolyline(w.Points, at) : x.Node.At == at));
            bool Wired(int node) => GroupHasWire(_physical.Find(node));

            // Pins by unit, in declaration order.
            var units = pinNodes.GroupBy(n => (_nodes[n].Pin!.Sheet, _nodes[n].Pin!.Inst)).ToList();

            // A connector that cannot be a Port is drawn as the part it is. Decided here, before anything is drawn: decided
            // after, it was skipped as a Port to be and then refused as one, and was left out of the import altogether.
            var portRefs = PortReferences();
            foreach (var unit in units)
                if (portRefs.Contains(unit.Key.Inst.Reference) && PortRefusal(unit.ToList(), netOf) is { } why)
                {
                    notes.Add($"{unit.Key.Inst.Reference} cannot be a Port: it has {why}. It is imported as its symbol cell.");
                    portRefs.Remove(unit.Key.Inst.Reference);
                }
            var fittedBodies = new List<((double X, double Y) A, (double X, double Y) B)>();
            foreach (var unit in units)
            {
                var inst = unit.Key.Inst;
                var decision = byRef[inst.Reference];
                var nodes = unit.ToList();
                if (decision.Becomes == ImportPartKind.Ground)
                {
                    foreach (int n in nodes) excluded.Add(Ref(n));
                    continue;
                }
                if (portRefs.Contains(inst.Reference)) continue;   // drawn as a Port below

                bool multi = source.Symbol(inst.SymbolKey) is { UnitCount: > 1 };
                string wanted = inst.Reference + (multi ? UnitLetter(inst.Unit) : "");
                string name = InstanceName(wanted, usedNames);
                if (name != wanted) renamed[wanted] = name;
                var nets = nodes.Select(NetOfPin).ToList();
                drawnFrom[name] = (inst, offsets[unit.Key.Sheet], decision);

                if (decision.Becomes == ImportPartKind.Native)
                {
                    if (decision.PinOrder is { } order)
                    {
                        nodes = [.. order.Select(num => nodes.First(n => _nodes[n].Pin!.Number == num))];
                        nets = nodes.Select(NetOfPin).ToList();
                    }
                    var overrides = new List<ParameterAssignment>
                    {
                        new(decision.Parameter!, decision.Expression!, decision.Unit),
                    };
                    if (decision.Footprint is { } footprint) overrides.Add(new(ArtworkParameters.FootprintName, footprint, null));
                    var instance = new Instance(name, ComponentTypeRegistry.EngineReference(decision.Native!.Value), nets, overrides);
                    tb.Instances.Add(instance);
                    if (decision.Variable is { } v && !tb.GlobalVariables.Any(g => g.Name == v.Name))
                        tb.GlobalVariables.Add(new Variable(v.Name, v.Expression, v.Unit));
                    var fit = FitNative(instance, nodes.Select(n => _nodes[n].At).ToList(), nets, ClashesWithOtherNet, fittedBodies);
                    exactInstances[name] = fit;
                    if (NetlistSchematic.PinOffsets(instance, fit.Rotation, fit.MirrorX) is { Count: 2 } body)
                        fittedBodies.Add(((fit.X + body[0].X, fit.Y + body[0].Y), (fit.X + body[1].X, fit.Y + body[1].Y)));
                    nativePins[name] = (instance, nodes);
                }
                else
                {
                    var cell = symbolCells[(inst.SymbolKey, inst.Unit)];
                    // Port order is the symbol's: one node per pin number, in that order.
                    var ordered = cell.PinNumbers.Select(num => nodes.First(n => _nodes[n].Pin!.Number == num)).ToList();
                    nodes = ordered;
                    nets = ordered.Select(NetOfPin).ToList();
                    tb.Instances.Add(new Instance(name, cell.Name, nets));
                    cells[name] = new ExactCell(ExternalCellRef.MakeCellRef(directory, Path.Combine(partsDirectory, cell.Name)), cell.Pins);
                    var first = _nodes[ordered[0]];
                    exactInstances[name] = new ExactInstance(first.At.X, first.At.Y, Rotation(inst.Rotation), inst.Mirror);
                }

                instancePins[name] = [.. nodes.Select(n => (ForeignPinRef?)Ref(n))];
                for (int k = 0; k < nodes.Count; k++)
                    if (nets[k] != "0" && (_nodes[nodes[k]].Pin!.Pin.Hidden || !Wired(nodes[k]))) joinByLabel.Add((name, k));
            }

            // ── child sheets ─────────────────────────────────────────────────────────────────────
            for (int s = 0; s < sheets.Count; s++)
                for (int i = 0; i < sheets[s].SheetInstances.Count; i++)
                {
                    var si = sheets[s].SheetInstances[i];
                    if (!sheetCells.TryGetValue(si.SheetName, out var cell)) continue;
                    string name = InstanceName(si.Name, usedNames);
                    var nodes = cell.Ports.Select(port => _nodes.FindIndex(n => n.Kind == NodeKind.SheetPin && n.Sheet == s
                                                                               && n.SheetPin == (i, port))).ToList();
                    var nets = nodes.Select((n, k) => n >= 0 ? netOf[n] : $"{name}_{NetName(cell.Ports[k])}").ToList();
                    tb.Instances.Add(new Instance(name, cell.Name, nets));
                    cells[name] = new ExactCell(ExternalCellRef.MakeCellRef(directory, Path.Combine(partsDirectory, cell.Name)), cell.Pins);
                    var first = nodes[0] >= 0 ? _nodes[nodes[0]].At : Shift((si.X, si.Y), offsets[s]);
                    exactInstances[name] = new ExactInstance(first.X, first.Y, SymbolRotation.R0, false);
                    instancePins[name] = [.. cell.Ports.Select(p => (ForeignPinRef?)new ForeignPinRef(si.Name, p))];
                    for (int k = 0; k < nodes.Count; k++)
                        if (nodes[k] >= 0 && nets[k] != "0" && !Wired(nodes[k])) joinByLabel.Add((name, k));
                }

            // ── ports (overview D9) ───────────────────────────────────────────────────────────────
            AddPorts(tb, units.Select(u => (u.Key.Inst, u.ToList())).ToList(), netOf, exactInstances, joinByLabel, instancePins,
                     usedNames, excluded, portRefs);

            // A connector drawn as a Port is not drawn itself, so the wire and ground symbol that grounded its shield
            // would be left standing alone: a ground group that only such pins reached is not drawn.
            var usedNets = tb.Instances.SelectMany(i => i.NetBindings).ToHashSet(StringComparer.Ordinal);
            var orphanGrounds = units.Where(u => PortParts.Contains(u.Key.Inst.Reference)).SelectMany(u => u).Select(n => netOf[n])
                                     .Where(n => n.StartsWith("GND_", StringComparison.Ordinal) && !usedNets.Contains(n))
                                     .ToHashSet(StringComparer.Ordinal);
            groundGroups.RemoveAll(g => orphanGrounds.Contains(g.Net));

            // ── routes ──────────────────────────────────────────────────────────────────────────
            var routes = new Dictionary<string, List<IReadOnlyList<(double X, double Y)>>>(StringComparer.Ordinal);
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Kind != NodeKind.Wire || !netOf.TryGetValue(n, out var net) || net == "0" || orphanGrounds.Contains(net)) continue;
                if (!routes.TryGetValue(net, out var list)) routes[net] = list = [];
                list.Add(_nodes[n].Wire!.Points);
            }
            // A part with both pins on one net may sit on a wire the net runs straight through: cutting would sever it.
            foreach (var (name, native) in nativePins)
                if (native.Nodes.Select(n => netOf[n]).Distinct().Count() == native.Nodes.Count)
                    TrimUnderBody(routes, native.Instance, exactInstances[name], native.Nodes);

            var placement = new ExactPlacement
            {
                Instances = exactInstances,
                Routes = routes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IReadOnlyList<(double X, double Y)>>)kv.Value),
                SnapToGrid = false,
                KeepOverlappingGlyphs = true,
                Cells = cells,
                QuietNets = quiet,
                JoinByLabel = joinByLabel,
            };

            var result = NetlistSchematic.Build(new Library("import"), tb, directory, exact: placement);
            if (result.Schematic is not { } model)
            {
                Refusals.AddRange(result.Refusals);
                return null;
            }
            notes.AddRange(result.Notes);
            if (result.PlacementFallbacks.Count > 0)
                notes.Add($"{string.Join(", ", result.PlacementFallbacks)} could not be drawn where the source draws them "
                        + "(a pin would land on another net's pin) and were placed beside the drawing.");
            model.GridSize = grid;
            PlaceSourceLabels(model, drawnFrom, result.PlacementFallbacks);
            // The connectivity check leaves their pins out on both sides, as it does a ground symbol's.
            foreach (var name in LeaveOutOfRuns(model, drawnFrom))
                foreach (var pin in instancePins[name]) if (pin is { } p) excluded.Add(p);

            // ── what NetlistSchematic does not draw ────────────────────────────────────────────────
            AddGroundSymbols(model, groundGroups);
            var portNames = sheetCell is null ? [] : AddSheetPorts(model, netOf);
            AddFreeText(model, offsets);
            foreach (var n in _nodes.Where(n => n.Kind == NodeKind.Junction)) model.Dots.Add(new EditableDot { X = n.At.X, Y = n.At.Y });

            // ── the source's own nets, for the check ─────────────────────────────────────────────
            foreach (int n in pinNodes) if (IsNoConnected(n)) excluded.Add(Ref(n));
            var geometric = GeometricNets(pinNodes, excluded);

            return new ImportBuiltSchematic
            {
                CellName = sheetCell?.Name ?? "", SheetCell = sheetCell, Directory = directory, Model = model, TestBench = tb,
                Placement = placement, InstancePins = instancePins, PortNames = portNames, GeometricNets = geometric, Excluded = excluded,
                GroundPins = pinNodes.Where(n => IsGroundRoot(_logical.Find(n)) && !excluded.Contains(Ref(n))).Select(Ref).ToHashSet(),
            };
        }

        // ── nodes and their joins ───────────────────────────────────────────────────────────────

        private int Add(Node node)
        {
            _nodes.Add(node);
            _physical.Add(_nodes.Count - 1);
            _logical.Add(_nodes.Count - 1);
            return _nodes.Count - 1;
        }

        private IEnumerable<PlacedPin> PinsOf(int sheet, ForeignInstance inst, (double X, double Y) off)
        {
            if (source.Symbol(inst.SymbolKey)?.Unit(inst.Unit) is not { } unit) yield break;
            for (int i = 0; i < unit.Pins.Count; i++)
            {
                var p = unit.Pins[i];
                var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, Rotation(inst.Rotation), inst.Mirror);
                yield return new PlacedPin(sheet, inst, p, PinIdentity(unit, i), Shift((x, y), off));
            }
        }

        /// <summary>The physical joins: a wire's vertices; anything lying on a wire; anything at the same point.</summary>
        private void JoinPhysically()
        {
            // Segments hashed by cell, so a point is tested against the few wires near it.
            const double Cell = 500;
            var segs = new Dictionary<(int, long, long), List<(int Node, (double X, double Y) A, (double X, double Y) B)>>();
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].Wire is not { } w) continue;
                for (int i = 0; i + 1 < w.Points.Count; i++)
                {
                    var (a, b) = (w.Points[i], w.Points[i + 1]);
                    for (long cx = (long)Math.Floor((Math.Min(a.X, b.X) - 1) / Cell); cx <= (long)Math.Floor((Math.Max(a.X, b.X) + 1) / Cell); cx++)
                        for (long cy = (long)Math.Floor((Math.Min(a.Y, b.Y) - 1) / Cell); cy <= (long)Math.Floor((Math.Max(a.Y, b.Y) + 1) / Cell); cy++)
                        {
                            var key = (_nodes[n].Sheet, cx, cy);
                            if (!segs.TryGetValue(key, out var l)) segs[key] = l = [];
                            l.Add((n, a, b));
                        }
                }
            }

            var at = new Dictionary<(int, long, long), int>();
            void Touch(int node, (double X, double Y) p, bool onWires)
            {
                var key = (_nodes[node].Sheet, (long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));
                if (at.TryGetValue(key, out int other)) _physical.Union(node, other);
                else at[key] = node;
                if (!onWires) return;
                if (segs.TryGetValue((_nodes[node].Sheet, (long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell)), out var near))
                    foreach (var (wn, a, b) in near)
                        if (wn != node && SchematicGeometry.PointOnSegment(p.X, p.Y, a.X, a.Y, b.X, b.Y, 0.01)) _physical.Union(node, wn);
            }

            for (int n = 0; n < _nodes.Count; n++)
            {
                var node = _nodes[n];
                switch (node.Kind)
                {
                    case NodeKind.Wire:
                        // Every vertex is a point other things meet; only the two ENDS join another wire's interior.
                        var pts = node.Wire!.Points;
                        for (int i = 0; i < pts.Count; i++) Touch(n, pts[i], onWires: i == 0 || i == pts.Count - 1);
                        break;
                    case NodeKind.Pin when node.Pin!.Pin.Hidden && node.Pin.Pin.ImplicitNet is not null:
                        break;   // a hidden power pin joins its net by name, never by where it is drawn
                    default:
                        Touch(n, node.At, onWires: true);
                        break;
                }
            }
            for (int n = 0; n < _nodes.Count; n++) _logical.Union(n, _physical.Find(n));
        }

        /// <summary>The logical joins: names (labels, implicit nets) and ground.</summary>
        private void JoinLogically()
        {
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            void Join(int node, string key)
            {
                if (byName.TryGetValue(key, out int other)) _logical.Union(node, other);
                else byName[key] = node;
            }
            for (int n = 0; n < _nodes.Count; n++)
            {
                var node = _nodes[n];
                if (node.Label is { } l)
                {
                    if (IsGroundLabel(l)) { Join(n, GroundKey); continue; }
                    Join(n, l.Kind switch
                    {
                        ForeignLabelKind.Local or ForeignLabelKind.Hierarchical => $"L:{node.Sheet}:{l.Text}",
                        _ => $"G:{l.Text}",
                    });
                    // A hierarchical label and a local one of the same text on one sheet are one net.
                }
                else if (node.Pin is { } p)
                {
                    if (byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground) Join(n, GroundKey);
                    else if (p.Pin.ImplicitNet is { Length: > 0 } implicitNet)
                        Join(n, SchematicImportMap.IsGroundName(implicitNet) ? GroundKey : $"G:{implicitNet}");
                }
            }
        }

        private const string GroundKey = "\u0000ground";

        private static bool IsGroundLabel(ForeignLabel l) =>
            l.Kind is ForeignLabelKind.Power or ForeignLabelKind.Global && SchematicImportMap.IsGroundName(l.Text);

        /// <summary>A node that grounds its net: a ground label, a ground symbol's pin, a hidden pin whose net is ground.</summary>
        private bool IsGroundMark(Node node) =>
            node.Label is { } l && IsGroundLabel(l)
            || node.Pin is { } p && (byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground
                                     || p.Pin.ImplicitNet is { } i && SchematicImportMap.IsGroundName(i));

        private bool IsGroundRoot(int root) => _groundRoots.Contains(root);

        private bool GroupHasWire(int physicalRoot) => _wiredGroups.Contains(physicalRoot);

        private bool IsNoConnected(int pinNode) => _noConnectGroups.Contains(_physical.Find(pinNode));

        // ── names ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every node's netlist net: ground's wired physical groups each their own quiet net (grounded by a symbol drawn
        /// where the source grounds it), an unwired ground pin <c>0</c>, any other net its best label's text — power and
        /// global first, then hierarchical, off-page, local — made unique, and an unnamed one a quiet <c>N</c>k.
        /// </summary>
        private (Dictionary<int, string> NetOf, HashSet<string> Quiet, List<(string Net, List<(double X, double Y)> At)> GroundGroups)? NameNets(List<int> pinNodes)
        {
            var netOf = new Dictionary<int, string>();
            var quiet = new HashSet<string>(StringComparer.Ordinal);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0" };
            var groundGroups = new List<(string Net, List<(double X, double Y)> At)>();
            var rootName = new Dictionary<int, string>();
            var groundRoots = new Dictionary<int, bool>();

            var rootsInOrder = Enumerable.Range(0, _nodes.Count).Select(_logical.Find).Distinct().ToList();
            var members = Enumerable.Range(0, _nodes.Count).ToLookup(_logical.Find);
            // Named nets first, so a source name is never displaced by an invented one.
            foreach (int root in rootsInOrder)
            {
                if (groundRoots[root] = IsGroundRoot(root)) continue;
                var labels = members[root]
                                       .Select(n => (Text: _nodes[n].Label?.Text ?? _nodes[n].Pin?.Pin.ImplicitNet, Rank: Rank(_nodes[n])))
                                       .Where(x => !string.IsNullOrWhiteSpace(x.Text))
                                       .OrderBy(x => x.Rank).ThenBy(x => x.Text, StringComparer.Ordinal).ToList();
                if (labels.Count == 0) continue;
                rootName[root] = Unique(NetName(labels[0].Text!), taken);
            }
            int k = 0;
            foreach (int root in rootsInOrder)
            {
                if (groundRoots[root] || rootName.ContainsKey(root)) continue;
                string name;
                do name = $"N{++k}"; while (taken.Contains(name));
                taken.Add(name);
                rootName[root] = name;
                quiet.Add(name);
            }

            // Ground: one quiet net per wired physical group, grounded where the source grounds it.
            var groundNetOfGroup = new Dictionary<int, string>();
            int g = 0;
            for (int n = 0; n < _nodes.Count; n++)
            {
                int root = _logical.Find(n);
                if (!groundRoots[root]) { netOf[n] = rootName[root]; continue; }
                int group = _physical.Find(n);
                if (!GroupHasWire(group)) { netOf[n] = "0"; continue; }
                if (!groundNetOfGroup.TryGetValue(group, out var net))
                {
                    do net = $"GND_{++g}"; while (taken.Contains(net));
                    taken.Add(net);
                    quiet.Add(net);
                    groundNetOfGroup[group] = net;
                    groundGroups.Add((net, GroundPoints(group)));
                }
                netOf[n] = net;
            }
            return (netOf, quiet, groundGroups);
        }

        /// <summary>Where the source grounds a physical group: its ground labels and ground symbols' pins — else, a group
        /// grounded only by a name it shares, its first label or wire vertex.</summary>
        private List<(double X, double Y)> GroundPoints(int group)
        {
            var members = Enumerable.Range(0, _nodes.Count).Where(n => _physical.Find(n) == group).ToList();
            var points = members.Where(n => IsGroundMark(_nodes[n]) && _nodes[n].Kind != NodeKind.Pin
                                             || _nodes[n].Pin is { } p && byRef[p.Inst.Reference].Becomes == ImportPartKind.Ground)
                                .Select(n => _nodes[n].At).Distinct().ToList();
            if (points.Count == 0)
                points = [members.Where(n => _nodes[n].Kind == NodeKind.Label).Select(n => _nodes[n].At)
                                 .DefaultIfEmpty(_nodes[members.First(n => _nodes[n].Kind == NodeKind.Wire)].Wire!.Points[0]).First()];
            return points;
        }

        private static int Rank(Node n) => n.Label?.Kind switch
        {
            ForeignLabelKind.Power or ForeignLabelKind.Global => 0,
            ForeignLabelKind.Hierarchical => 1,
            ForeignLabelKind.OffPage => 2,
            ForeignLabelKind.Local => 3,
            _ => n.Pin is not null ? 4 : 5,
        };

        // ── placement ───────────────────────────────────────────────────────────────────────────

        /// <summary>A native part at the rotation, and the origin on the source's grid, that put its pins nearest the
        /// source's (overview D6): every quarter turn tried, the origin the mean of what each pin asks for, snapped either
        /// way. A fit that lands a pin on another net's wire or pin is taken only when nothing else is left — a capacitor
        /// pitched 300 mils snaps its 400-mil native symbol 50 mils up or down, and one of the two can put a pin on a wire
        /// the source only crosses.</summary>
        private ExactInstance FitNative(Instance instance, IReadOnlyList<(double X, double Y)> sourcePins, IReadOnlyList<string> nets,
                                        Func<(double X, double Y), string, bool> clashes,
                                        IReadOnlyList<((double X, double Y) A, (double X, double Y) B)> fitted)
        {
            const double Clash = 1e9;
            // A body laid over another native part already fitted — two parts the source draws closer together than
            // circuitRF's symbols are long — costs more than any snap: one grid further along, the two meet pin to pin.
            const double Overlap = 1e6;
            ExactInstance best = new(sourcePins[0].X, sourcePins[0].Y, SymbolRotation.R0, false);
            double bestCost = double.MaxValue;
            foreach (var rot in new[] { SymbolRotation.R0, SymbolRotation.R90, SymbolRotation.R180, SymbolRotation.R270 })
            {
                if (NetlistSchematic.PinOffsets(instance, rot, false) is not { } offsets || offsets.Count != sourcePins.Count) continue;
                double mx = sourcePins.Select((p, k) => p.X - offsets[k].X).Average();
                double my = sourcePins.Select((p, k) => p.Y - offsets[k].Y).Average();
                foreach (double ox in SnapsOf(mx, offsets.Count == 2 && Math.Abs(offsets[0].X - offsets[1].X) > 1e-6))
                    foreach (double oy in SnapsOf(my, offsets.Count == 2 && Math.Abs(offsets[0].Y - offsets[1].Y) > 1e-6))
                    {
                        double cost = sourcePins.Select((p, k) => Math.Abs(ox + offsets[k].X - p.X) + Math.Abs(oy + offsets[k].Y - p.Y)).Sum()
                                    + offsets.Select((o, k) => clashes((ox + o.X, oy + o.Y), nets[k]) ? Clash : 0).Sum()
                                    + (offsets.Count == 2 && OverlapsFitted((ox + offsets[0].X, oy + offsets[0].Y), (ox + offsets[1].X, oy + offsets[1].Y), fitted)
                                        ? Overlap : 0);
                        if (cost < bestCost - 1e-9) { bestCost = cost; best = new ExactInstance(ox, oy, rot, false); }
                    }
            }
            return best;
        }

        /// <summary>The nearest grid point to <paramref name="v"/> first, then the other one either side of it — and,
        /// <paramref name="alongBody"/>, one grid step either way as well, which is how a part longer than the source's
        /// steps clear of a neighbour it would otherwise overlap.</summary>
        private IEnumerable<double> SnapsOf(double v, bool alongBody = false)
        {
            double near = Snap(v);
            yield return near;
            double other = v < near ? near - grid : near + grid;
            if (Math.Abs(v - near) > 1e-9) yield return other;
            else if (alongBody) { yield return near - grid; yield return near + grid; }
        }

        /// <summary>Whether the body between <paramref name="a"/> and <paramref name="b"/> runs along one already
        /// fitted, sharing more than a point with it.</summary>
        private static bool OverlapsFitted((double X, double Y) a, (double X, double Y) b,
                                           IReadOnlyList<((double X, double Y) A, (double X, double Y) B)> fitted)
        {
            const double Eps = 1e-6;
            bool vertical = Math.Abs(a.X - b.X) < Eps, horizontal = Math.Abs(a.Y - b.Y) < Eps;
            if (vertical == horizontal) return false;
            foreach (var (fa, fb) in fitted)
            {
                if (vertical && Math.Abs(fa.X - fb.X) < Eps && Math.Abs(fa.X - a.X) < Eps)
                {
                    double lo = Math.Max(Math.Min(a.Y, b.Y), Math.Min(fa.Y, fb.Y)), hi = Math.Min(Math.Max(a.Y, b.Y), Math.Max(fa.Y, fb.Y));
                    if (hi - lo > Eps) return true;
                }
                if (horizontal && Math.Abs(fa.Y - fb.Y) < Eps && Math.Abs(fa.Y - a.Y) < Eps)
                {
                    double lo = Math.Max(Math.Min(a.X, b.X), Math.Min(fa.X, fb.X)), hi = Math.Min(Math.Max(a.X, b.X), Math.Max(fa.X, fb.X));
                    if (hi - lo > Eps) return true;
                }
            }
            return false;
        }

        private static bool OnPolyline(IReadOnlyList<(double X, double Y)> points, (double X, double Y) at)
        {
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var (a, b) = (points[i], points[i + 1]);
                if (at.X < Math.Min(a.X, b.X) - 1e-6 || at.X > Math.Max(a.X, b.X) + 1e-6
                    || at.Y < Math.Min(a.Y, b.Y) - 1e-6 || at.Y > Math.Max(a.Y, b.Y) + 1e-6) continue;
                if (Math.Abs((b.X - a.X) * (at.Y - a.Y) - (b.Y - a.Y) * (at.X - a.X)) < 1e-6) return true;
            }
            return points.Count == 1 && points[0] == at;
        }

        /// <summary>
        /// A native part longer than the source's symbol lands its body over part of a source wire: a capacitor drawn
        /// 300 mils pin to pin is fitted with its 400-mil native symbol, and the wire leaving the source's lower pin now
        /// starts inside the capacitor, with the native pin somewhere along it. The one change made to a source wire (D6):
        /// the stretch lying along the part's axis strictly between its two pins is cut away, so the wire ends on the pin
        /// again. Nothing is cut where anything else connects inside that stretch — another part's pin, a junction, a
        /// label, or a wire arriving across it — since the part's body would hide that connection either way.
        /// </summary>
        private void TrimUnderBody(Dictionary<string, List<IReadOnlyList<(double X, double Y)>>> routes, Instance instance,
                                   ExactInstance at, List<int> own)
        {
            const double Eps = 1e-6;
            if (NetlistSchematic.PinOffsets(instance, at.Rotation, at.MirrorX) is not { Count: 2 } o) return;
            var (ax, ay, bx, by) = (at.X + o[0].X, at.Y + o[0].Y, at.X + o[1].X, at.Y + o[1].Y);
            bool vertical = Math.Abs(ax - bx) < Eps;
            if (vertical == Math.Abs(ay - by) < Eps) return;
            double line = vertical ? ax : ay;
            double lo = vertical ? Math.Min(ay, by) : Math.Min(ax, bx), hi = vertical ? Math.Max(ay, by) : Math.Max(ax, bx);

            bool OnLine((double X, double Y) p) => Math.Abs((vertical ? p.X : p.Y) - line) < Eps;
            bool Inside((double X, double Y) p) => OnLine(p) && (vertical ? p.Y : p.X) is var u && u > lo + Eps && u < hi - Eps;

            for (int n = 0; n < _nodes.Count; n++)
                if (_nodes[n].Kind != NodeKind.Wire && !own.Contains(n) && Inside(_nodes[n].At)) return;
            foreach (var polyline in routes.Values.SelectMany(r => r))
                for (int i = 0; i < polyline.Count; i++)
                    if (Inside(polyline[i])
                        && (i > 0 && !OnLine(polyline[i - 1]) || i + 1 < polyline.Count && !OnLine(polyline[i + 1])))
                        return;

            foreach (var net in routes.Keys.ToList())
                routes[net] = [.. routes[net].SelectMany(Cut)];

            IEnumerable<IReadOnlyList<(double X, double Y)>> Cut(IReadOnlyList<(double X, double Y)> polyline)
            {
                (double X, double Y) Point(double u) => vertical ? (line, u) : (u, line);
                var piece = new List<(double X, double Y)> { polyline[0] };
                for (int i = 0; i + 1 < polyline.Count; i++)
                {
                    var (p, q) = (polyline[i], polyline[i + 1]);
                    double pu = vertical ? p.Y : p.X, qu = vertical ? q.Y : q.X;
                    double s0 = Math.Max(Math.Min(pu, qu), lo), s1 = Math.Min(Math.Max(pu, qu), hi);
                    if (!OnLine(p) || !OnLine(q) || s1 - s0 <= Eps) { piece.Add(q); continue; }
                    double enter = pu <= qu ? s0 : s1, leave = pu <= qu ? s1 : s0;
                    if (Math.Abs(enter - pu) > Eps) piece.Add(Point(enter));
                    if (piece.Count >= 2) yield return piece;
                    piece = [Point(leave)];
                    if (Math.Abs(leave - qu) > Eps) piece.Add(q);
                }
                if (piece.Count >= 2) yield return piece;
            }
        }

        private double Snap(double v) => Math.Round(v / grid) * grid;

        private (double X, double Y)[] PageOffsets()
        {
            var offsets = new (double X, double Y)[sheets.Count];
            double x = 0;
            for (int s = 0; s < sheets.Count; s++)
            {
                offsets[s] = (x, 0);
                x = NextPageX(x, sheets[s].Width > 0 ? sheets[s].Width : ContentRight(sheets[s]));
            }
            return offsets;
        }

        private static double ContentRight(ForeignSheet sheet) =>
            sheet.Wires.SelectMany(w => w.Points.Select(p => p.X))
                 .Concat(sheet.Instances.Select(i => i.X + 1000))
                 .Concat(sheet.Labels.Select(l => l.X)).Concat(sheet.Texts.Select(t => t.X))
                 .DefaultIfEmpty(0).Max();

        // ── ports ───────────────────────────────────────────────────────────────────────────────

        private HashSet<string> PortReferences()
        {
            var refs = sheets.SelectMany(s => s.Instances).Select(i => i.Reference).ToHashSet(StringComparer.Ordinal);
            return options.Ports.Where(refs.Contains).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Overview D9: a connector the user marked becomes a Port where it was drawn — its first pin not on ground
        /// the signal, the next its reference; a net the user marked gets a Port to ground beside the drawing. With any,
        /// the default S-parameter analysis.</summary>
        private void AddPorts(TestBench tb, List<(ForeignInstance Inst, List<int> Nodes)> units, Dictionary<int, string> netOf,
                              Dictionary<string, ExactInstance> exact, HashSet<(string, int)> joinByLabel,
                              Dictionary<string, IReadOnlyList<ForeignPinRef?>> instancePins, HashSet<string> used,
                              HashSet<ForeignPinRef> excludedPorts, HashSet<string> portRefs)
        {
            int num = 0;
            double left = _nodes.Where(n => n.Kind is NodeKind.Wire or NodeKind.Pin).Select(n => n.At.X).DefaultIfEmpty(0).Min() - 800;
            double top = _nodes.Where(n => n.Kind is NodeKind.Wire or NodeKind.Pin).Select(n => n.At.Y).DefaultIfEmpty(0).Min();
            bool IsGround(string net) => net == "0" || net.StartsWith("GND_", StringComparison.Ordinal);
            foreach (var want in options.Ports)
            {
                var unit = units.FirstOrDefault(u => u.Inst.Reference == want);
                if (unit.Inst is not null && !portRefs.Contains(want)) continue;   // refused above, drawn as its part
                string name = InstanceName($"P{num + 1}", used);
                if (unit.Inst is not null)
                {
                    var nodes = unit.Nodes;
                    int sig = nodes.FindIndex(n => !IsGround(netOf[n]));
                    var others = nodes.Where((n, i) => i != sig).ToList();
                    var otherNets = others.Select(n => netOf[n]).Where(n => !IsGround(n) && n != netOf[nodes[sig]]).Distinct().ToList();
                    string minus = otherNets.Count == 1 ? otherNets[0] : "0";
                    tb.Instances.Add(PortInstance(name, ++num, netOf[nodes[sig]], minus));
                    PortParts.Add(want);
                    var at = _nodes[nodes[sig]].At;
                    exact[name] = new ExactInstance(at.X, at.Y + 200, SymbolRotation.R0, false);
                    if (!GroupHasWire(_physical.Find(nodes[sig]))) joinByLabel.Add((name, 0));
                    if (minus != "0") joinByLabel.Add((name, 1));
                    // The reference pin is the first on the minus net; a connector's further ground pins have no
                    // terminal of their own, so the check leaves them out on both sides.
                    int minusPin = others.FirstOrDefault(n => minus == "0" ? IsGround(netOf[n]) : netOf[n] == minus, -1);
                    instancePins[name] = [Ref(nodes[sig]), minusPin >= 0 ? Ref(minusPin) : null];
                    foreach (int n in others.Where(n => n != minusPin)) excludedPorts.Add(Ref(n));
                    continue;
                }
                var target = netOf.Values.Distinct().FirstOrDefault(n => n.Equals(NetName(want), StringComparison.OrdinalIgnoreCase));
                if (target is null || IsGround(target))
                {
                    notes.Add($"No part or net named '{want}' to make a Port of.");
                    continue;
                }
                tb.Instances.Add(PortInstance(name, ++num, target, "0"));
                exact[name] = new ExactInstance(Snap(left), Snap(top + (num - 1) * 800 + 200), SymbolRotation.R0, false);
                joinByLabel.Add((name, 0));
                instancePins[name] = [null, null];
            }
            if (num > 0) tb.Analyses.Add(new SParameterAnalysis(RecognitionEmit.AnalysisName, RecognitionEmitOptions.DefaultSweep));
        }

        /// <summary>
        /// A test point (<c>TP</c>…) or any other symbol cell with one pin is drawn but left out of a run (Disable = Open):
        /// it has no model, so a run would refuse the whole design for want of one, and a part with a single pin carries
        /// no current whatever its model would be. The user can switch it back on like any disabled part.
        /// </summary>
        private List<string> LeaveOutOfRuns(SchematicEditModel model,
            Dictionary<string, (ForeignInstance Inst, (double X, double Y) Off, ImportPartDecision Decision)> drawnFrom)
        {
            var left = new List<string>();
            foreach (var c in model.Components)
            {
                if (c.InstanceName is not { } name || !drawnFrom.TryGetValue(name, out var from)
                    || from.Decision.Becomes != ImportPartKind.SymbolCell) continue;
                bool testPoint = new string([.. from.Inst.Reference.TakeWhile(char.IsLetter)]).Equals("TP", StringComparison.OrdinalIgnoreCase);
                if (!testPoint && source.Symbol(from.Inst.SymbolKey)?.Unit(from.Inst.Unit)?.Pins.Count != 1) continue;
                c.Disable = DisableState.Open;
                left.Add(name);
            }
            if (left.Count > 0)
                notes.Add($"{left.Count} test point(s) and single-pin part(s) are drawn but left out of a run (Disable = Open): "
                        + $"{string.Join(", ", left.Order(StringComparer.Ordinal))}.");
            return left;
        }

        /// <summary>Why a connector with these pins cannot be a Port (overview D9) — it needs one signal pin, and at most one
        /// other net off ground for its reference — or null when it can.</summary>
        private static string? PortRefusal(IReadOnlyList<int> nodes, Dictionary<int, string> netOf)
        {
            static bool IsGround(string net) => net == "0" || net.StartsWith("GND_", StringComparison.Ordinal);
            int off = nodes.Select(n => netOf[n]).Where(n => !IsGround(n)).Distinct().Count();
            return off == 0 ? "no pin off ground" : off > 2 ? "more than two signal nets" : null;
        }

        private static Instance PortInstance(string name, int num, string plus, string minus) =>
            new(name, ComponentTypeRegistry.EngineReference(SymbolKind.Term), [plus, minus],
                [new ParameterAssignment("Num", num.ToString(CultureInfo.InvariantCulture), null), new ParameterAssignment("Z", "50", "Ohm")]);

        // ── labels where the source shows them ──────────────────────────────────────────────────

        /// <summary>
        /// schematic-import.md §4, labels: each part's labels where the source shows its fields. The instance name goes where the source
        /// shows its <c>Reference</c>, and a native part's value — or a symbol cell's name, the nearest thing it has — where
        /// it shows its <c>Value</c>. A field the source hides is hidden, as is a native part's type, which the source does
        /// not show. Each row keeps circuitRF's own text and is placed by the end the source justifies it to, so it grows
        /// away from the part the way the source's does. A part with no fields, one drawn beside the drawing, or one whose
        /// shown fields include text run up the page keeps circuitRF's own layout: a schematic label here is always level,
        /// and a level row centred where a vertical one stood — a resistor's value written inside its body — covers the part
        /// and the field beside it.
        /// </summary>
        private void PlaceSourceLabels(SchematicEditModel model,
                                       Dictionary<string, (ForeignInstance Inst, (double X, double Y) Off, ImportPartDecision Decision)> drawnFrom,
                                       IReadOnlyList<string> placedBeside)
        {
            foreach (var c in model.Components)
            {
                if (!drawnFrom.TryGetValue(c.InstanceName, out var from) || from.Inst.Fields.Count == 0
                    || placedBeside.Contains(c.InstanceName)) continue;
                // A supply connector's fields are the connector's: its value names the part ("PWR-IN") and is usually
                // hidden. As a source it shows its name and its voltage, in circuitRF's own layout.
                if (from.Decision.Rule == SchematicImportMap.SupplyRuleName)
                {
                    c.ShowInstanceName = true;
                    c.ShowTypeLabel = false;
                    if (c.Parameters.FirstOrDefault(p => p.Name == from.Decision.Parameter) is { } volts) volts.ShowOnSchematic = true;
                    // The connector's wires leave its pins on the side its pins point to, which is where circuitRF puts a
                    // part's labels: they go on the other side, clear of the supply's own wiring.
                    var shown = c.ToRenderComponent(null, null).Labels;
                    double width = shown.Where(l => l.Length > 0).Select(l => l.Length).DefaultIfEmpty(0).Max() * NetlistSchematic.LabelCharWidth;
                    // Its pins stand off its body toward the wiring: to the right when they lie right of its origin.
                    bool wiresRight = source.Symbol(from.Inst.SymbolKey)?.Unit(from.Inst.Unit)?.Pins is { Count: > 0 } sp
                        && sp.Average(q => SchematicGeometry.LocalToWorld((float)q.X, (float)-q.Y, from.Inst.X, from.Inst.Y,
                                                                          Rotation(from.Inst.Rotation), from.Inst.Mirror).X) > from.Inst.X;
                    double dx = wiresRight ? -(SchematicComponent.LabelBaseXFor(c.Symbol) + width + 150) : 0;
                    // Level with the body: the block's middle row on the part's centre.
                    int shownRows = shown.Count(l => l.Length > 0);
                    double dy = wiresRight ? -SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount) - (shownRows - 1) * SchematicComponent.LabelWorldStep / 2
                                             + SchematicComponent.LabelWorldHeight / 2 : 0;
                    c.LabelOffsets.Clear();
                    for (int i = 0; i < shown.Count; i++) c.LabelOffsets.Add((dx, dy));
                    continue;
                }
                var fields = from.Inst.Fields;
                var reference = fields.FirstOrDefault(f => f.Name == "Reference");
                var value = fields.FirstOrDefault(f => f.Name == "Value");
                if (reference is { Hidden: false, Vertical: true } || value is { Hidden: false, Vertical: true }) continue;
                bool native = from.Decision.Becomes == ImportPartKind.Native;

                // A field the source lists but does not show, or does not list among the ones it shows, is hidden.
                c.ShowInstanceName = reference is { Hidden: false };
                var valueParam = native ? c.Parameters.FirstOrDefault(p => p.Name == from.Decision.Parameter) : null;
                if (valueParam is not null) valueParam.ShowOnSchematic = value is { Hidden: false };
                c.ShowTypeLabel = !native && value is { Hidden: false };

                var placed = new List<(int Row, ForeignField Field)>();
                if (reference is { Hidden: false }) placed.Add((1, reference));
                if (value is { Hidden: false })
                {
                    if (!native) placed.Add((0, value));
                    else if (valueParam is not null)
                        placed.Add((2 + c.LabelParameters().TakeWhile(p => !ReferenceEquals(p, valueParam)).Count(), value));
                }
                if (placed.Count == 0) continue;

                var resolution = native || !symbolCells.TryGetValue((from.Inst.SymbolKey, from.Inst.Unit), out var cell) ? null
                    : new CellSymbolResolution { State = CellSymbolState.Resolved, Symbol = cell.Symbol };
                c.LabelOffsets.Clear();
                var rendered = c.ToRenderComponent(null, resolution);
                double baseX = c.X + SchematicComponent.LabelBaseXFor(c.Symbol);
                double baseY = c.Y + SchematicComponent.LabelBaseYFor(c.Symbol, c.PortCount, rendered.GlyphBbMaxY - c.Y);

                var rowsAt = placed.OrderBy(p => p.Row).Select(p =>
                {
                    string text = p.Row < rendered.Labels.Count ? rendered.Labels[p.Row] : "";
                    double width = text.Length * NetlistSchematic.LabelCharWidth;
                    var (fx, fy) = Shift((p.Field.X, p.Field.Y), from.Off);
                    double left = p.Field.Justify switch
                    {
                        ForeignJustify.Left => fx,
                        ForeignJustify.Right => fx - width,
                        _ => fx - width / 2,
                    };
                    return (p.Row, Left: left, Width: width, Baseline: fy + SchematicComponent.LabelWorldHeight / 2);
                }).ToList();

                // circuitRF's rows read longer than the source's text ("R = 15 kOhm" for "15k"), so two fields the source
                // sets side by side can collide. Then the rows are stacked as one block at the source's place, growing
                // away from the part: upward when the source puts the fields above it.
                bool collide = rowsAt.Any(a => rowsAt.Any(b => a.Row < b.Row && a.Left < b.Left + b.Width && b.Left < a.Left + a.Width
                                                               && Math.Abs(a.Baseline - b.Baseline) < SchematicComponent.LabelWorldStep));
                if (collide)
                {
                    double left = rowsAt.Min(r => r.Left);
                    bool above = rowsAt.Average(r => r.Baseline) < (rendered.GlyphBbMinY + rendered.GlyphBbMaxY) / 2;
                    double first = above ? rowsAt.Max(r => r.Baseline) - (rowsAt.Count - 1) * SchematicComponent.LabelWorldStep
                                         : rowsAt.Min(r => r.Baseline);
                    rowsAt = [.. rowsAt.Select((r, k) => (r.Row, left, r.Width, first + k * SchematicComponent.LabelWorldStep))];
                }

                // Rows the source places nothing on stack under the last one placed, as LabelOffsetAt reads them.
                int rows = placed.Max(p => p.Row) + 1;
                var offsets = new (double DX, double DY)[rows];
                foreach (var (row, left, _, baseline) in rowsAt)
                    offsets[row] = (Math.Round(left - baseX, 3), Math.Round(baseline - (baseY + row * SchematicComponent.LabelWorldStep), 3));
                // A row with no field of its own sits where the row before it would put it.
                for (int r = 1; r < rows; r++)
                    if (!placed.Any(p => p.Row == r)) offsets[r] = offsets[r - 1];
                c.LabelOffsets.AddRange(offsets);
            }
        }

        // ── what NetlistSchematic does not draw ─────────────────────────────────────────────────

        private static void AddGroundSymbols(SchematicEditModel model, List<(string Net, List<(double X, double Y)> At)> groups)
        {
            string prefix = ComponentTypeRegistry.InstancePrefix(SymbolKind.Ground);
            int n = model.Components.Where(c => c.Symbol == SymbolKind.Ground && c.InstanceName.StartsWith(prefix, StringComparison.Ordinal))
                         .Select(c => int.TryParse(c.InstanceName.AsSpan(prefix.Length), out int i) ? i : 0).DefaultIfEmpty(0).Max();
            foreach (var (_, points) in groups)
                foreach (var (x, y) in points)
                    model.Components.Add(NetlistSchematic.NewGround(x, y, 0, 1, ++n));
        }

        /// <summary>A child sheet's hierarchical labels as <c>Pin</c>s, numbered in its symbol's port order, each with a
        /// net label of its net on the same point so it joins whatever the label touched.</summary>
        private List<string> AddSheetPorts(SchematicEditModel model, Dictionary<int, string> netOf)
        {
            var placed = new List<string>();
            for (int i = 0; i < sheetCell!.Ports.Count; i++)
            {
                string port = sheetCell.Ports[i];
                int n = _nodes.FindIndex(x => x.Label is { Kind: ForeignLabelKind.Hierarchical } l && l.Text == port);
                if (n < 0) { notes.Add($"Sheet '{sheetCell.SheetName}' has no hierarchical label for its port '{port}'."); continue; }
                var (x, y) = _nodes[n].At;
                var pin = new EditableComponent { Symbol = SymbolKind.Pin, X = x - 100, Y = y, Rotation = SymbolRotation.R0 };
                pin.Parameters.Add(new EditableParameter { Name = "Num", Expression = (i + 1).ToString(CultureInfo.InvariantCulture) });
                pin.Parameters.Add(new EditableParameter { Name = "Name", Expression = NetName(port), ShowOnSchematic = false });
                model.Components.Add(pin);
                if (netOf[n] != "0") model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = netOf[n] });
                placed.Add(NetName(port));
            }
            return placed;
        }

        private void AddFreeText(SchematicEditModel model, (double X, double Y)[] offsets)
        {
            for (int s = 0; s < sheets.Count; s++)
            {
                foreach (var t in sheets[s].Texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
                {
                    var (x, y) = Shift((t.X, t.Y), offsets[s]);
                    model.CanvasObjects.Add(new EditableText
                    {
                        Text = t.Text, X = x, Y = y, Align = t.Align ?? SymbolTextAlign.Left, VAlign = t.VAlign ?? SymbolTextVAlign.Top,
                        RotationDeg = t.Rotation,
                    });
                }
                foreach (var gr in sheets[s].Graphics)
                {
                    var pts = gr.Points.Select(p => Shift(p, offsets[s])).ToList();
                    if (gr.Closed && pts.Count > 2) pts.Add(pts[0]);
                    for (int i = 0; i + 1 < pts.Count; i++)
                        model.CanvasObjects.Add(new EditablePrimitive
                        {
                            Shape = PrimitiveShape.Line, P1X = pts[i].X, P1Y = pts[i].Y, P2X = pts[i + 1].X, P2Y = pts[i + 1].Y,
                            X = (pts[i].X + pts[i + 1].X) / 2, Y = (pts[i].Y + pts[i + 1].Y) / 2,
                        });
                }
            }
        }

        // ── the check's half ────────────────────────────────────────────────────────────────────

        private ForeignPinRef Ref(int pinNode) => new(_nodes[pinNode].Pin!.Inst.Reference, _nodes[pinNode].Pin!.Number);

        /// <summary>The source's nets as sets of pins (overview D8): each logical net's pins, plus a child sheet's
        /// hierarchical labels and a parent's sheet pins as the ports they are.</summary>
        private List<IReadOnlySet<ForeignPinRef>> GeometricNets(List<int> pinNodes, HashSet<ForeignPinRef> excluded)
        {
            var sets = new Dictionary<int, HashSet<ForeignPinRef>>();
            void Put(int node, ForeignPinRef r)
            {
                int root = _logical.Find(node);
                if (!sets.TryGetValue(root, out var set)) sets[root] = set = [];
                set.Add(r);
            }
            foreach (int n in pinNodes) if (!excluded.Contains(Ref(n))) Put(n, Ref(n));
            for (int n = 0; n < _nodes.Count; n++)
            {
                if (_nodes[n].SheetPin is { } sp && sheets[_nodes[n].Sheet].SheetInstances[sp.Inst] is var si && sheetCells.ContainsKey(si.SheetName))
                    Put(n, new ForeignPinRef(si.Name, sp.Port));
                if (sheetCell is not null && _nodes[n].Label is { Kind: ForeignLabelKind.Hierarchical } l && sheetCell.Ports.Contains(l.Text))
                    Put(n, ImportConnectivity.PortReference(NetName(l.Text)));
            }
            return [.. sets.Values.Where(s => s.Count >= 2).Select(s => (IReadOnlySet<ForeignPinRef>)s)];
        }

        private static (double X, double Y) Shift((double X, double Y) p, (double X, double Y) off) => (p.X + off.X, p.Y + off.Y);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Cells
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One source symbol unit as a symbol cell (R-si1-4), through Import Component's own symbol path
    /// (<see cref="ComponentImport.BuildSymbol"/>). Translated so its first pin is the local origin — what lets an
    /// instance put every pin on the source's point. Ports in declaration order.
    /// </summary>
    private static ImportSymbolCell? SymbolCellOf(ForeignSymbol symbol, ForeignSymbolUnit unit, string name, List<string> notes)
    {
        double ox = unit.Pins[0].X, oy = unit.Pins[0].Y;
        var drawing = new ComponentSymbolDrawing { Name = name };
        var numbers = new List<string>();
        int fallbacks = 0;
        for (int i = 0; i < unit.Pins.Count; i++)
        {
            var p = unit.Pins[i];
            string number = PinIdentity(unit, i);
            if (number != p.Number.Trim()) fallbacks++;
            numbers.Add(number);
            drawing.Pins.Add(new ComponentSymbolPin(p.Name.Length > 0 && !symbol.PinNamesHidden ? p.Name : number, number,
                                                    (int)Math.Round(p.X - ox), (int)Math.Round(p.Y - oy), NameAlign: AlignOf(p.BodySide)));
        }
        foreach (var shape in unit.Graphics) drawing.Shapes.Add(Translate(shape, -ox, -oy));
        if (fallbacks > 0)
            notes.Add($"{fallbacks} pin(s) of '{symbol.DisplayName}' have no number; each is known by its name, else its position.");

        var messages = new List<string>();
        var ports = Enumerable.Range(0, unit.Pins.Count).ToDictionary(i => i, i => i + 1);
        if (ComponentImport.BuildSymbol(drawing, ports, unit.Pins.Count, messages, snapPins: false) is not { } built) return null;
        foreach (var m in messages) notes.Add($"{name}: {m}");
        var pins = built.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY)).ToList();
        return new ImportSymbolCell(name, symbol.Key, unit.Unit, symbol.DisplayName, built, SymbolPersistence.Serialize(built), numbers, pins);
    }

    /// <summary>A child sheet's rectangle symbol (overview D5): the rectangle the parent draws, its ports where the parent
    /// draws them, the first at the local origin.</summary>
    private static ImportSheetCell? SheetCellOf(ForeignSheetInstance si, string name, List<string> notes)
    {
        if (si.Pins.Count == 0) return null;
        var ports = si.Pins.Select(p => p.Name).Distinct(StringComparer.Ordinal).ToList();
        var first = si.Pins[0];
        var drawing = new ComponentSymbolDrawing { Name = name };
        // Sheet coordinates are Y down and a drawing is Y up; the first pin is the origin.
        foreach (var port in ports)
        {
            var p = si.Pins.First(q => q.Name == port);
            drawing.Pins.Add(new ComponentSymbolPin(port, port, (int)Math.Round(p.X - first.X), (int)Math.Round(first.Y - p.Y)));
        }
        drawing.Shapes.Add(new KitSymbolRectangle(si.X - first.X, first.Y - si.Y, si.X + si.Width - first.X, first.Y - (si.Y + si.Height), false));
        var messages = new List<string>();
        if (ComponentImport.BuildSymbol(drawing, Enumerable.Range(0, ports.Count).ToDictionary(i => i, i => i + 1), ports.Count, messages, snapPins: false) is not { } built)
            return null;
        foreach (var m in messages) notes.Add($"{name}: {m}");
        var pins = built.Pins.OrderBy(p => p.PortIndex).Select(p => ((float)p.LocalX, (float)p.LocalY)).ToList();
        return new ImportSheetCell(name, si.SheetName, built, SymbolPersistence.Serialize(built), ports, pins);
    }

    private static KitTextAlign AlignOf(ForeignPinDirection body) => body switch
    {
        ForeignPinDirection.Left => KitTextAlign.Right,
        ForeignPinDirection.Up => KitTextAlign.Bottom,
        ForeignPinDirection.Down => KitTextAlign.Top,
        _ => KitTextAlign.Left,
    };

    private static KitSymbolShape Translate(KitSymbolShape shape, double dx, double dy) => shape switch
    {
        KitSymbolLine l => new KitSymbolLine(l.X1 + dx, l.Y1 + dy, l.X2 + dx, l.Y2 + dy) { Width = l.Width },
        KitSymbolRectangle r => new KitSymbolRectangle(r.X1 + dx, r.Y1 + dy, r.X2 + dx, r.Y2 + dy, r.Filled) { Width = r.Width },
        KitSymbolPath p => new KitSymbolPath([.. p.Xy.Select((v, i) => v + (i % 2 == 0 ? dx : dy))], p.Closed, p.Filled) { Width = p.Width },
        KitSymbolArc a => new KitSymbolArc(a.Cx + dx, a.Cy + dy, a.Radius, a.StartDeg, a.SweepDeg) { Width = a.Width },
        _ => shape,
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Small rules
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A pin's identity (R-si1-2, R-si1-4): its number; with none, its name; with neither, its 1-based position.</summary>
    internal static string PinIdentity(ForeignSymbolUnit unit, int index)
    {
        var p = unit.Pins[index];
        if (p.Number.Trim() is { Length: > 0 } n) return n;
        if (p.Name.Trim() is { Length: > 0 } name) return name;
        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    internal static string UnitLetter(int unit) => unit is >= 1 and <= 26 ? ((char)('A' + unit - 1)).ToString() : $"_{unit}";

    private static SymbolRotation Rotation(int degrees) => (((degrees % 360) + 360) % 360) switch
    {
        90 => SymbolRotation.R90,
        180 => SymbolRotation.R180,
        270 => SymbolRotation.R270,
        _ => SymbolRotation.R0,
    };

    /// <summary>A net name as a schematic label carries it: no whitespace.</summary>
    /// <summary>Where the page after one at <paramref name="x"/> of <paramref name="width"/> mils goes when pages are tiled
    /// left to right — the build's rule for a flat design's pages, and a reader's for the pages of one hierarchical sheet.</summary>
    internal static double NextPageX(double x, double width) => x + Math.Ceiling((width + PageGap * P) / P) * P;

    internal static string NetName(string text) =>
        new([.. text.Trim().Select(c => char.IsWhiteSpace(c) || c is '"' or ';' or '=' ? '_' : c)]);

    /// <summary>An instance name from a reference: letters, digits and underscores, unique in the schematic.</summary>
    private static string InstanceName(string reference, HashSet<string> used)
    {
        string s = new([.. reference.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_')]);
        if (s.Length == 0 || char.IsAsciiDigit(s[0])) s = "X" + s;
        return Unique(s, used);
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        string n = name;
        for (int k = 2; !taken.Add(n); k++) n = $"{name}_{k}";
        return n;
    }

    /// <summary>
    /// The connection grid every source point sits on (R-si1-6): the largest divisor of 100 mils that divides every
    /// pin, wire vertex, junction, label and sheet pin. 100 for a source on circuitRF's own grid.
    /// </summary>
    private static int GridOf(ForeignSchematic source)
    {
        int g = (int)P;
        void On(double v)
        {
            long i = (long)Math.Round(v);
            if (Math.Abs(v - i) > 1e-3) i = (long)Math.Round(v * 1000) % 1000 == 0 ? i : 1;   // off the mil: as fine as it gets
            g = (int)Gcd(g, Math.Abs(i));
        }
        foreach (var sheet in source.Sheets)
        {
            foreach (var w in sheet.Wires) foreach (var (x, y) in w.Points) { On(x); On(y); }
            foreach (var j in sheet.Junctions) { On(j.X); On(j.Y); }
            foreach (var l in sheet.Labels) { On(l.X); On(l.Y); }
            foreach (var si in sheet.SheetInstances) foreach (var p in si.Pins) { On(p.X); On(p.Y); }
            foreach (var inst in sheet.Instances)
                if (source.Symbol(inst.SymbolKey)?.Unit(inst.Unit) is { } unit)
                    foreach (var p in unit.Pins.Where(p => !p.Hidden))
                    {
                        var (x, y) = SchematicGeometry.LocalToWorld((float)p.X, (float)-p.Y, inst.X, inst.Y, Rotation(inst.Rotation), inst.Mirror);
                        On(x); On(y);
                    }
        }
        return Math.Max(1, g);

        static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return a; }
    }

    /// <summary>Two placed units of one part both claiming the same reference AND unit are two parts the source left
    /// unannotated: the second and later get a suffix, reported.</summary>
    private static ForeignSchematic UniqueReferences(ForeignSchematic source, List<string> notes)
    {
        var seen = new HashSet<(string, int)>();
        var refs = source.Sheets.SelectMany(s => s.Instances).Select(i => i.Reference).ToHashSet(StringComparer.Ordinal);
        var changed = new List<string>();
        var sheets = source.Sheets.Select(sheet => sheet with
        {
            Instances = [.. sheet.Instances.Select(i =>
            {
                if (seen.Add((i.Reference, i.Unit))) return i;
                string n = i.Reference;
                for (int k = 2; refs.Contains(n); k++) n = $"{i.Reference}_{k}";
                refs.Add(n);
                seen.Add((n, i.Unit));
                changed.Add($"{i.Reference} → {n}");
                return i with { Reference = n };
            })],
        }).ToList();
        if (changed.Count == 0) return source;
        notes.Add($"{changed.Count} part(s) share a reference with another and were renamed: {string.Join(", ", changed)}.");
        return source with { Sheets = sheets };
    }

    private static IEnumerable<string> GlobalNamesAcrossSheets(ForeignSchematic source) =>
        source.Sheets.SelectMany(s => s.Labels.Where(l => l.Kind is ForeignLabelKind.Global or ForeignLabelKind.Power
                                                          && !SchematicImportMap.IsGroundName(l.Text))
                                              .Select(l => l.Text).Distinct().Select(t => (Sheet: s.Name, Text: t)))
              .GroupBy(x => x.Text).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal);

    /// <summary>A plain union-find over node indices.</summary>
    private sealed class UnionFind
    {
        private readonly List<int> _parent = [];
        public void Add(int n) { while (_parent.Count <= n) _parent.Add(_parent.Count); }
        public int Find(int n) { while (_parent[n] != n) n = _parent[n] = _parent[_parent[n]]; return n; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[Math.Max(a, b)] = Math.Min(a, b); }
    }
}
