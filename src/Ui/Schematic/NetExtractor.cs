using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.Layout;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Turns a <see cref="SchematicEditModel"/> into a <see cref="TestBench"/> (Design model).
/// Headless, framework-free — no Avalonia, no Skia. Reuses ComputeConnectivityGeometry
/// for the geometric union (T-junctions, crossing predicate); applies same-name label union
/// on top (§2.1.6). Terminal order is the contract: NetBindings[k] = net at terminal k.
/// </summary>
public static class NetExtractor
{
    /// <param name="TestBench">The emitted Design model.</param>
    /// <param name="Conflicts">
    /// Non-fatal naming conflicts, e.g. two different label names on the same physical net.
    /// </param>
    public sealed record ExtractionResult(TestBench TestBench, IReadOnlyList<string> Conflicts)
    {
        /// <summary>
        /// Cell interface port names derived from Pin components, in ascending Num order.
        /// Differential pairs (Polarity=Plus/Minus sharing the same Num) each contribute two
        /// entries: "{base}+" then "{base}-". Single-ended Pins contribute one entry: Name or
        /// "P{Num}". Empty when no Pins are present (testbench-style schematics).
        /// These names are the values that must appear in <see cref="CircuitRF.Core.Design.Cell.Ports"/>
        /// for the elaborator's positional parentNetMap binding to work correctly.
        /// </summary>
        public IReadOnlyList<string> CellPorts { get; init; } = [];

        /// <summary>
        /// Library of Cell definitions built by recursively extracting cell-instance schematics.
        /// Empty for flat schematics (no cell instances) or when no resolver is provided.
        /// Cells are ordered leaf-first so <c>define</c>-before-use is satisfied.
        /// </summary>
        public Library Library { get; init; } = new("netlist");
    }

    public static ExtractionResult Extract(
        SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null)
    {
        var lib        = new Library("netlist");
        var imports    = new NetlistImports();
        var conflicts  = new List<string>();
        var inProgress = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labeled    = new HashSet<string>(StringComparer.Ordinal);

        var (instances, cellPorts, topVars, topMeas) = ExtractModel(model, cells, lib, imports, inProgress, conflicts, labeled);

        var tb = new TestBench(testBenchName);
        tb.Instances.AddRange(instances);
        tb.GlobalVariables.AddRange(topVars);

        // A kit netlist brings its own supporting declarations with it. They are merged rather than
        // scoped because the cells that were copied out of it reference them by bare name, and a
        // user declaration of the same name is left standing: the design the user wrote wins over
        // one a kit happened to ship.
        imports.MergeInto(tb, conflicts);
        tb.Measurements.AddRange(topMeas);
        foreach (var name in labeled)
            tb.LabeledNets.Add(name);

        // Analyses attach to the TOP testbench only (data-model §2.1 invariant).
        // All analyses are carried so ParametricSweepEngine can find its inner analysis by name.
        // Dispatch (in SchematicRunService) skips disabled analyses at run time.
        foreach (var analysis in model.Analyses)
            tb.Analyses.Add(analysis);

        return new ExtractionResult(tb, conflicts) { CellPorts = cellPorts, Library = lib };
    }

    // ── Per-model extraction pipeline (shared by top and sub-cells) ─────────

    private static (List<Instance> Instances, IReadOnlyList<string> CellPorts, List<Variable> Variables, List<Measurement> Measurements) ExtractModel(
        SchematicEditModel model,
        ICellResolver?      cells,
        Library             lib,
        NetlistImports      imports,
        HashSet<string>     inProgress,
        List<string>        conflicts,
        HashSet<string>?    labeledNetsOut = null)
    {
        double gs = model.GridSize;
        (long, long) QK(double x, double y) =>
            ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();

        // Pre-resolve cell-ref symbol geometry for this model (mirrors BuildRenderModel.ResolveAllCellRefs).
        // Null when SchematicDirectory is not set; GetEffectivePortDefs falls back to SymbolPortDefs in that case.
        var cellRefResolutions = BuildCellRefResolutions(model);

        // R-pc-8: resolved once per model (not per instance) — every microstrip instance in THIS
        // schematic frame shares its own document's workspace technology. A sub-cell schematic
        // resolves its OWN SchematicDirectory (this method runs once per recursion level), so a
        // sub-cell living in a different workspace still gets its own substrate, matching §5A.2.
        var microstripTech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(model.SchematicDirectory);

        // Detached-port synthetic keys — each detached port gets a unique key that can never
        // be produced by QK() for any finite schematic coordinate, so it will never union with
        // the geometric P-cell it overlaps.  Its root is added to AssignNetNames explicitly so
        // it receives an auto-name like "n3" rather than silently falling through to "0".
        var detachedKeys = new Dictionary<(string CompId, int PortIndex), (long, long)>();
        long detachedSeq  = 0;
        void AddDetachedKey(string compId, int portIdx)
        {
            long s  = detachedSeq++;
            var dk  = (long.MinValue + s, s);   // x ≈ -9.2e18: unreachable by real P-cells
            detachedKeys[(compId, portIdx)] = dk;
            uf.Add(dk);
        }

        // ── Layer 1: union-find over on-P connection points ─────────────────

        // Seed: all component pins using cell-ref-aware port geometry.
        // Resolved cell-refs use .csym pin positions; unresolved fall back to SymbolPortDefs.
        foreach (var comp in model.Components)
        {
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                if (comp.IsPortDetached(def.PortIndex)) { AddDetachedKey(comp.Id, def.PortIndex); continue; }
                var (px, py) = model.PortWorldOf(comp, def);
                uf.Add(QK(px, py));
            }
        }

        // Geometric connectivity (wire vertices + T-junctions + crossing dots), shared with the
        // editor's one-label-per-node rule so the two never disagree.
        AddGeometricUnions(model, QK, uf);

        // Short disable: union all non-detached terminal P-cells of shorted components.
        foreach (var comp in model.Components)
        {
            if (comp.Disable != DisableState.Short) continue;
            var defs = GetEffectivePortDefs(model, comp, cellRefResolutions);
            if (defs.Count < 2) continue;
            // Find the first non-detached port as the union anchor; skip all detached ports.
            (float LocalX, float LocalY, int PortIndex) firstNd = default;
            bool foundFirst = false;
            foreach (var d in defs)
            {
                if (!comp.IsPortDetached(d.PortIndex)) { firstNd = d; foundFirst = true; break; }
            }
            if (!foundFirst) continue;  // all ports detached — nothing to short
            var (x0, y0) = model.PortWorldOf(comp, firstNd);
            var key0 = QK(x0, y0);
            foreach (var d in defs)
            {
                if (d.PortIndex == firstNd.PortIndex) continue;
                if (comp.IsPortDetached(d.PortIndex)) continue;
                var (px, py) = model.PortWorldOf(comp, d);
                uf.Union(key0, QK(px, py));
            }
        }

        // Same-name net label union (§2.1.6): labels with identical names make one net,
        // even across physically-disjoint wires.
        var labelNetKeys = new Dictionary<EditableNetLabel, (long, long)?>();
        foreach (var lbl in model.NetLabels)
            labelNetKeys[lbl] = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);

        foreach (var grp in model.NetLabels.GroupBy(l => l.Name, StringComparer.Ordinal))
        {
            (long, long)? rep = null;
            foreach (var lbl in grp)
            {
                var k = labelNetKeys[lbl];
                if (k is null) continue;
                if (rep is null) { rep = k; continue; }
                uf.Union(rep.Value, k.Value);
                rep = k; // keep a reachable key for future unions in this group
            }
        }

        // ── Collect Pin info (before name assignment so Pin nets get port names) ───
        var pinInfos = CollectPinInfos(model, uf, QK);

        // Build pin→portName map: applied between net-label names and auto-names so that
        // the net at each Pin position is named after the port (enabling elaborator binding).
        var pinNetNameMap = BuildPinNetNameMap(pinInfos, uf);

        // ── Layer 2: assign stable net names ────────────────────────────────

        var netNames = AssignNetNames(uf, QK, gs, model, labelNetKeys, conflicts, detachedKeys.Values, pinNetNameMap, cellRefResolutions, labeledNetsOut);

        // ── Compute CellPorts + conformance warnings ─────────────────────────
        var cellPorts = BuildCellPorts(pinInfos, conflicts);

        // ── Layer 3: emit instances ──────────────────────────────────────────

        var instances = new List<Instance>();

        // Validate Term Num uniqueness before emitting.
        var termNums = new Dictionary<int, string>(); // Num → first InstanceName
        foreach (var comp in model.Components)
        {
            if (comp.Symbol is not (SymbolKind.Term or SymbolKind.TermG)) continue;
            var numParam = comp.Parameters.FirstOrDefault(p => p.Name == "Num");
            if (numParam != null && int.TryParse(numParam.Expression, out int num))
            {
                if (termNums.TryGetValue(num, out var first))
                    conflicts.Add($"Duplicate Term Num={num} on \"{comp.InstanceName}\" (first: \"{first}\")");
                else
                    termNums[num] = comp.InstanceName;
            }
        }

        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol == SymbolKind.Ground) continue;
            if (comp.Symbol == SymbolKind.Pin)    continue;
            if (comp.Symbol == SymbolKind.Var)    continue;  // VAR rows routed to Variables, not instances
            if (comp.Symbol == SymbolKind.Meas)   continue;  // MEAS rows routed to Measurements, not instances

            if (comp.CellRef is not null)
            {
                // A part whose definition is a CIRCUIT comes first: a package is a subcircuit, not a
                // device model, whatever else the kit says about it.
                var nb = TryEmitNetlistBackedCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                                          cellRefResolutions, lib, imports, conflicts,
                                                          out bool isCircuitBacked);
                if (nb is not null) { instances.Add(nb); continue; }

                // A part the kit declares as a circuit is never emitted as a device instead. Falling
                // back would answer with something the user did not place — the failure has already
                // been reported, and skipping is the honest outcome.
                if (isCircuitBacked) continue;

                // A cell backed by an external device provider is a LEAF: emit one ExtDevice
                // instance rather than descending into a schematic it deliberately does not have.
                var ext = TryEmitExternalDeviceInstance(comp, model, uf, QK, netNames, detachedKeys,
                                                        cellRefResolutions, conflicts);
                if (ext is not null) { instances.Add(ext); continue; }

                var ci = EmitCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                          cells, lib, imports, inProgress, conflicts, cellRefResolutions);
                if (ci is not null) instances.Add(ci);
                continue;
            }

            var inst = EmitInstance(comp, model, cellRefResolutions, uf, QK, netNames, detachedKeys,
                microstripTech, conflicts);
            if (inst is not null) instances.Add(inst);
        }

        // ── Collect VAR variable definitions for this frame ─────────────────
        var frameVars    = new List<Variable>();
        var varNamesSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol != SymbolKind.Var) continue;
            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var varName = p.Name.Trim();
                if (!varNamesSeen.Add(varName))
                {
                    conflicts.Add($"Variable '{varName}' defined more than once in this cell; first definition kept.");
                    continue;
                }
                string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
                frameVars.Add(new Variable(varName, p.Expression, unit));
            }
        }

        // ── Collect MEAS measurement definitions for this frame ─────────────
        var frameMeas    = new List<Measurement>();
        var measNamesSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var comp in model.Components)
        {
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            if (comp.Symbol != SymbolKind.Meas) continue;
            foreach (var p in comp.Parameters)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var measName = p.Name.Trim();
                if (!measNamesSeen.Add(measName))
                {
                    conflicts.Add($"Measurement '{measName}' defined more than once; first definition kept.");
                    continue;
                }
                string? unit = UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null;
                frameMeas.Add(new Measurement(measName, p.Expression, unit));
            }
        }

        return (instances, cellPorts, frameVars, frameMeas);
    }

    // ── Cell instance emission ───────────────────────────────────────────────

    /// <summary>
    /// The published interface of the cell an instance references — from memory for an imported
    /// kit's part, from disk for a cell folder. Null when there is none to read, which sends the
    /// caller on to the ordinary hierarchical path to report it.
    ///
    /// <para><b>A kit part needs no schematic directory.</b> Its reference is virtual rather than
    /// relative to anything, so the guard the disk path needs would wrongly refuse one on a
    /// schematic that has never been saved — which is exactly the case the virtual form exists to
    /// allow.</para>
    /// </summary>
    private static CcellFile? CcellOf(SchematicEditModel model, EditableComponent comp)
    {
        if (comp.CellRef is null) return null;
        if (PdkKitRegistry.IsKitRef(comp.CellRef)) return PdkKitRegistry.Find(comp.CellRef)?.Ccell;

        return model.SchematicDirectory is null
            ? null
            : CellSymbolResolver.ResolveCcell(comp.CellRef, model.SchematicDirectory);
    }

    /// <summary>
    /// Emits a provider-backed cell as ONE <c>ExtDevice</c> instance, or returns null when the cell
    /// is an ordinary hierarchical one (the overwhelmingly common case) so the caller falls through
    /// to <see cref="EmitCellInstance"/>.
    ///
    /// <para>Nets bind in pin order, exactly like every other component: <c>NetBindings[k]</c> is
    /// the net at pin k. The engine's own external-device mapping treats each node as its own
    /// ground-referenced port, so an UNCONNECTED pin — a thermal terminal left open, which is
    /// ordinary and correct — simply gets its own auto-named net and is never an error here.</para>
    /// </summary>
    private static Instance? TryEmitExternalDeviceInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        List<string> conflicts)
    {
        if (CcellOf(model, comp) is not { } ccell) return null;

        if (string.IsNullOrWhiteSpace(ccell.ExternalProvider)) return null;

        if (string.IsNullOrWhiteSpace(ccell.ExternalType))
        {
            conflicts.Add($"Instance '{comp.InstanceName}' (cell '{comp.CellRef}') names a device " +
                          $"provider but no device type; skipped.");
            return null;
        }

        if (RefuseUnimplementedChoice(comp, ccell) is { } refusal)
        {
            conflicts.Add(refusal);
            return null;
        }

        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                          .OrderBy(d => d.PortIndex)
                          .ToList();

        if (pinDefs.Count == 0)
        {
            conflicts.Add($"Instance '{comp.InstanceName}' (cell '{comp.CellRef}') exposes no pins; skipped.");
            return null;
        }

        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        // Provider/Type are reserved names the ExtDevice model reads itself; every OTHER parameter
        // is forwarded verbatim for the provider to match against its own declared descriptor.
        // A model library chosen on THIS instance travels in the provider name, because that is what
        // the registry keys on: two instances naming different libraries must get two providers, or
        // the second would silently be evaluated by the first's models.
        string? library = comp.Parameters
            .FirstOrDefault(p => p.Name.Equals(PdkPartInstaller.ModelLibraryParameter, StringComparison.Ordinal))
            ?.Expression.Trim();

        var overrides = new List<ParameterAssignment>
        {
            new("Provider", DeviceWorkerProviderResolver.ComposeOverride(ccell.ExternalProvider!, library), null),
            new("Type",     ccell.ExternalType!,     null),
        };

        // The kit's own infrastructure parameters, emitted verbatim and never editable.
        if (ccell.ExternalFixedParameters is { } fixedParams)
            foreach (var (k, v) in fixedParams)
                if (k is not ("Provider" or "Type"))
                    overrides.Add(new ParameterAssignment(k, AsLiteralExpression(v), null));

        // A model-selection parameter picks WHICH implementation is built; it is not a value the
        // implementation itself takes, and a provider handed one would rightly reject it as unknown.
        var variantNames = VariantParameterNames(ccell);

        foreach (var p in comp.Parameters)
        {
            if (p.Name is "Provider" or "Type") continue;          // never let a stray override shadow these
            if (p.Name == PdkPartInstaller.ModelLibraryParameter) continue;  // rides in the provider name
            if (variantNames.Contains(p.Name)) continue;           // selects an implementation, not a value
            if (string.IsNullOrWhiteSpace(p.Expression)) continue; // unset — the provider's own default stands
            var unit = UnitNormalizer.ToEngineUnit(p.Unit);
            overrides.Add(new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null));
        }

        return new Instance(comp.InstanceName, "ExtDevice", nets, overrides);
    }

    // ── Netlist-backed cells ──────────────────────────────────────────────────

    /// <summary>
    /// Cells copied out of kit netlists during one extraction, plus the declarations they depend on.
    ///
    /// <para>A netlist is read once however many instances reference it. Beyond cost, that keeps a
    /// single definition of every cell in the run — reading the same file twice would put two Cell
    /// objects with one name into the library and leave which one wins to list order.</para>
    /// </summary>
    private sealed class NetlistImports
    {
        private readonly Dictionary<string, (Library Library, TestBench TestBench)> _byPath =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<UserFunction> _functions = [];
        private readonly List<Variable>     _variables = [];
        private readonly HashSet<string>    _merged    = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads a netlist, or returns null with a reason. Never throws.</summary>
        public (Library Library, TestBench TestBench)? Load(string path, out string? problem)
        {
            problem = null;
            if (_byPath.TryGetValue(path, out var hit)) return hit;

            try
            {
                if (!File.Exists(path)) { problem = $"'{path}' does not exist"; return null; }

                var read = path.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase)
                    ? CnlReader.ReadFile(path)
                    : ReadKitNetlists(path);

                _byPath[path] = read;
                CollectDeclarations(path, read.TestBench);
                return read;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Reads a kit's own netlist — and every netlist beside it. A kit splits one library across
        /// several files: the file defining a part instantiates cells declared in another, and its
        /// process constants live in a third. Reading only the named file gives a definition whose
        /// own contents do not resolve.
        /// </summary>
        private static (Library Library, TestBench TestBench) ReadKitNetlists(string path)
        {
            var library = new Library("kit");
            var bench   = new TestBench("kit");

            string? directory = Path.GetDirectoryName(path);
            var files = new List<string> { path };
            if (directory is not null)
            {
                foreach (var sibling in Directory.EnumerateFiles(directory).OrderBy(f => f, StringComparer.Ordinal))
                    if (!sibling.Equals(path, StringComparison.OrdinalIgnoreCase) && LooksLikeNetlist(sibling))
                        files.Add(sibling);
            }

            foreach (var file in files)
            {
                KitNetlistResult read;
                try { read = KitNetlistReader.ReadFile(file); }
                catch { continue; }   // a sibling that will not read must not stop the named one

                // The named file wins on a name collision: it is the one the part was pointed at.
                foreach (var cell in read.Library.Cells)
                    if (library.Find(cell.Name) is null) library.Cells.Add(cell);

                foreach (var v in read.Variables)
                    if (!bench.GlobalVariables.Any(e => e.Name.Equals(v.Name, StringComparison.Ordinal)))
                        bench.GlobalVariables.Add(v);

                foreach (var f in read.Functions)
                    if (!bench.Functions.Any(e => e.Name.Equals(f.Name, StringComparison.Ordinal)))
                        bench.Functions.Add(f);
            }

            return (library, bench);
        }

        private static bool LooksLikeNetlist(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".net", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".inc", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".ckt", StringComparison.OrdinalIgnoreCase);
        }

        private void CollectDeclarations(string path, TestBench source)
        {
            if (!_merged.Add(path)) return;
            _functions.AddRange(source.Functions);
            _variables.AddRange(source.GlobalVariables);
        }

        /// <summary>
        /// Adds the imported declarations to the testbench. A name the design already uses is kept —
        /// the user's own declaration wins over one a kit happened to ship — and the collision is
        /// reported rather than resolved silently, because the two may well mean different things.
        /// </summary>
        public void MergeInto(TestBench tb, List<string> conflicts)
        {
            foreach (var f in _functions)
            {
                if (tb.Functions.Any(e => e.Name.Equals(f.Name, StringComparison.Ordinal))) continue;
                tb.Functions.Add(f);
            }

            foreach (var v in _variables)
            {
                if (tb.GlobalVariables.Any(e => e.Name.Equals(v.Name, StringComparison.Ordinal)))
                {
                    conflicts.Add($"Variable '{v.Name}' is declared both in this design and in an " +
                                  $"imported kit netlist; the design's own definition is used.");
                    continue;
                }
                tb.GlobalVariables.Add(v);
            }
        }
    }

    /// <summary>
    /// Rewrites every kit-native device type inside a kit's own cells into an ordinary
    /// <c>ExtDevice</c> carrying that kit's provider name.
    ///
    /// <para><b>Which references are rewritten, and why that classification is the whole trick.</b> A
    /// kit's cell instantiates three kinds of thing: circuitRF primitives (<c>R</c>, <c>SnP</c>,
    /// <c>Chain</c>), other cells the same kit defines, and its own compiled device models. The first
    /// two are recognisable — so whatever is left is the third, and the kit already told us which
    /// provider evaluates it. Nothing here knows any type name.</para>
    ///
    /// <para>A genuine mistake in the kit (a misspelled type, a definition in a file that failed to
    /// read) also lands here, and that is the better outcome: the provider is the authority on what
    /// it serves, so it refuses by name — which says more than "cell not found" ever could.</para>
    ///
    /// <para>Idempotent, because the netlist read is cached and the same <see cref="Cell"/> objects
    /// are shared by every instance of the part.</para>
    /// </summary>
    private static void BindNativeDeviceTypes(Library kitLibrary, string provider, EditableComponent comp)
    {
        // The per-instance model-library override travels in the provider NAME, because that is what
        // the registry keys on — the same rule the leaf provider-backed path follows.
        string? modelLibrary = comp.Parameters
            .FirstOrDefault(p => p.Name.Equals(PdkPartInstaller.ModelLibraryParameter, StringComparison.Ordinal))
            ?.Expression.Trim();
        string providerRef = DeviceWorkerProviderResolver.ComposeOverride(provider, modelLibrary);

        foreach (var cell in kitLibrary.Cells)
        {
            for (int i = 0; i < cell.Instances.Count; i++)
            {
                var inst = cell.Instances[i];

                if (inst.Reference.Equals(ExtDeviceReference, StringComparison.OrdinalIgnoreCase)) continue;
                if (CircuitRF.Core.Devices.ComponentModelFactory.IsPrimitive(inst.Reference)) continue;
                if (kitLibrary.Find(inst.Reference) is not null)        continue;

                var overrides = new List<ParameterAssignment>(inst.Overrides.Count + 2)
                {
                    new("Provider", providerRef,     null),
                    new("Type",     inst.Reference,  null),
                };
                // Every other parameter is the kit's own and is forwarded verbatim, for the provider
                // to match against the names its descriptor declares.
                overrides.AddRange(inst.Overrides.Where(o => o.Name is not ("Provider" or "Type")));

                cell.Instances[i] = new Instance(
                    inst.InstanceName, ExtDeviceReference, inst.NetBindings, overrides)
                {
                    RefNetBinding = inst.RefNetBinding,
                };
            }
        }
    }

    /// <summary>Emitted reference for a device an external provider evaluates.</summary>
    private const string ExtDeviceReference = "ExtDevice";

    /// <summary>
    /// Emits a cell instance whose definition comes from a netlist the kit supplies, rather than
    /// from a schematic. Returns null when this component is not one — every other kind of cell
    /// falls through to the paths below untouched.
    ///
    /// <para>The point of the whole path: a packaged part is a subcircuit, so once its definition is
    /// in the library everything downstream — elaboration, nets, sweeps, results — treats it exactly
    /// like a cell the user drew.</para>
    /// </summary>
    private static Instance? TryEmitNetlistBackedCellInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        Library lib,
        NetlistImports imports,
        List<string> conflicts,
        out bool isCircuitBacked)
    {
        isCircuitBacked = false;
        if (CcellOf(model, comp) is not { } ccell) return null;

        if (string.IsNullOrWhiteSpace(ccell.ExternalNetlistPath) ||
            string.IsNullOrWhiteSpace(ccell.ExternalNetlistCell)) return null;

        // From here on this component is ours whatever happens: every exit reports, and none of them
        // hands the component back to be built as something else.
        isCircuitBacked = true;

        if (RefuseUnimplementedChoice(comp, ccell) is { } refusal)
        {
            conflicts.Add(refusal);
            return null;
        }

        var read = imports.Load(ccell.ExternalNetlistPath!, out string? problem);
        if (read is null)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the netlist defining this part could " +
                          $"not be read ({problem}). The kit may have moved.");
            return null;
        }

        string wanted = SubstituteParameters(ccell.ExternalNetlistCell!, comp, ccell);
        var definition = read.Value.Library.Find(wanted);
        if (definition is null)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': '{wanted}' is not defined in the kit's " +
                          $"netlist, so this part cannot be built.");
            return null;
        }

        // A kit netlist names its own compiled device types natively — `KITLIB_DEVICE_v1:FET1 …` —
        // and those are neither circuitRF primitives nor cells the kit defines. They are what the
        // kit's worker evaluates: a packaged part is built from the netlist, and the devices INSIDE
        // it still resolve to the provider. Bind them before the cells are copied, so what lands in
        // the library is already expressed in terms every downstream layer understands.
        if (!string.IsNullOrWhiteSpace(ccell.ExternalProvider))
            BindNativeDeviceTypes(read.Value.Library, ccell.ExternalProvider!, comp);

        // Copy the whole file's cells, not just the one named: the definition instantiates others
        // beside it, and walking the dependency graph would re-derive what reading the file already
        // told us. Names already present win — a design's own cell is never replaced by a kit's.
        foreach (var cell in read.Value.Library.Cells)
            if (lib.Find(cell.Name) is null) lib.Cells.Add(cell);

        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                          .OrderBy(d => d.PortIndex)
                          .ToList();

        // The symbol and the subcircuit come from the same kit, so their terminal counts agreeing is
        // the whole basis for binding pin k to port k. Disagreement means one of them is not the
        // part it claims to be, and guessing an alignment would wire the design wrong in silence.
        if (definition.Ports.Count != pinDefs.Count)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the symbol exposes {pinDefs.Count} " +
                          $"pin(s) but '{wanted}' defines {definition.Ports.Count}; skipped.");
            return null;
        }

        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        var overrides = new List<ParameterAssignment>();

        // The kit's own infrastructure parameters, then the user's — a design value beats a default.
        if (ccell.ExternalFixedParameters is { } fixedParams)
            foreach (var (k, v) in fixedParams)
                overrides.Add(new ParameterAssignment(k, AsLiteralExpression(v), null));

        var variantNames = VariantParameterNames(ccell);
        foreach (var p in comp.Parameters)
        {
            if (variantNames.Contains(p.Name)) continue;           // picked the definition, not a value
            if (string.IsNullOrWhiteSpace(p.Expression)) continue; // unset — the cell's own default stands
            var unit = UnitNormalizer.ToEngineUnit(p.Unit);
            overrides.RemoveAll(o => o.Name == p.Name);
            overrides.Add(new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null));
        }

        // Only what the definition actually declares: a subcircuit handed a parameter it never
        // named is an error in the elaborator, and the kit's own interface is the authority on which
        // of a part's parameters reach the circuit and which only pick which circuit it is.
        var declared = new HashSet<string>(definition.Parameters.Select(x => x.Name), StringComparer.Ordinal);
        overrides.RemoveAll(o => !declared.Contains(o.Name));

        return new Instance(comp.InstanceName, definition.Name, nets, overrides);
    }

    /// <summary>Replaces every <c>{Name}</c> with that parameter's value on this instance, falling
    /// back to the cell's declared default. An unknown name is left as written, so it shows up in
    /// the "not defined in the kit's netlist" message rather than vanishing into a wrong lookup.</summary>
    private static string SubstituteParameters(string pattern, EditableComponent comp, CcellFile ccell)
        => System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+)\}", m =>
        {
            string name = m.Groups[1].Value;

            var onInstance = comp.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
            if (onInstance is not null && !string.IsNullOrWhiteSpace(onInstance.Expression))
                return onInstance.Expression.Trim();

            var declared = ccell.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
            return declared is not null && declared.DefaultExpression.Length > 0
                ? declared.DefaultExpression.Trim()
                : m.Value;
        });

    /// <summary>Parameters whose value picks WHICH definition is built rather than supplying a value
    /// to one. Never forwarded: the thing they select has already been selected by the time an
    /// instance is emitted.</summary>
    private static HashSet<string> VariantParameterNames(CcellFile ccell)
        => new(ccell.Parameters.Where(c => c.Choices is { Count: > 0 }).Select(c => c.Name),
               StringComparer.Ordinal);

    /// <summary>
    /// The refusal message for an instance set to a choice circuitRF has no implementation for, or
    /// null. Substituting a supported choice is not an option — the answer would be for a different
    /// model than the one asked for.
    /// </summary>
    private static string? RefuseUnimplementedChoice(EditableComponent comp, CcellFile ccell)
    {
        foreach (var declared in ccell.Parameters)
        {
            if (declared.UnsupportedChoices is not { Count: > 0 } bad) continue;

            string chosen = (comp.Parameters
                                 .FirstOrDefault(p => p.Name.Equals(declared.Name, StringComparison.Ordinal))
                                 ?.Expression ?? declared.DefaultExpression).Trim();

            if (bad.Contains(chosen, StringComparer.Ordinal))
                return $"Instance '{comp.InstanceName}': {declared.Name} = {chosen} is not " +
                       $"implemented in circuitRF. Choose another value for {declared.Name} " +
                       $"(available: {string.Join(", ", declared.Choices ?? [])}).";
        }
        return null;
    }

    private static Instance? EmitCellInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        ICellResolver? cells,
        Library lib,
        NetlistImports imports,
        HashSet<string> inProgress,
        List<string> conflicts,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        if (cells is null) return null;   // flat caller / no resolver — skip silently (back-compat)

        var res = cells.Resolve(comp, model);
        if (res is null)
        {
            conflicts.Add($"Cell instance '{comp.InstanceName}' (cell '{comp.CellRef}') has no " +
                          $"primary schematic; skipped.");
            return null;
        }

        var cellName = res.CellName;

        // Cycle guard: a cell currently being extracted up the stack instantiates itself.
        if (inProgress.Contains(cellName))
        {
            conflicts.Add($"Cell '{cellName}' instantiates itself (cycle); " +
                          $"instance '{comp.InstanceName}' skipped.");
            return null;
        }

        // Build the cell once (dedupe by name); children are added before parents → leaf-first lib.
        if (lib.Find(cellName) is null)
        {
            inProgress.Add(cellName);
            var (subInstances, subPorts, subVars, subMeas) = ExtractModel(res.Schematic, cells, lib, imports, inProgress, conflicts);
            if (subMeas.Count > 0)
                conflicts.Add($"Cell '{cellName}': MEAS components are ignored inside a cell; measurements attach to the top testbench only.");
            var cell = new Cell(cellName);
            cell.Ports.AddRange(subPorts);
            cell.Instances.AddRange(subInstances);
            cell.Variables.AddRange(subVars);
            foreach (var p in res.Parameters) cell.Parameters.Add(p);
            lib.Cells.Add(cell);
            inProgress.Remove(cellName);
        }

        var cellDef = lib.Find(cellName)!;

        // Port geometry: use resolved .csym pins when available; fall back to SymbolPortDefs
        // for unresolvable cell-refs (SchematicDirectory not set or symbol not found).
        // Sorted by PortIndex so NetBindings[k] aligns with Cell.Ports[k].
        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                          .OrderBy(d => d.PortIndex)
                          .ToList();

        // BINDING CONTRACT GUARD: resolved pin count must equal the cell's interface-pin count.
        if (cellDef.Ports.Count != pinDefs.Count)
        {
            conflicts.Add($"Cell '{cellName}' instance '{comp.InstanceName}': symbol exposes " +
                          $"{pinDefs.Count} port(s) but the cell defines {cellDef.Ports.Count} " +
                          $"interface pin(s); skipped.");
            return null;
        }

        // NetBindings = parent net at each pin, in PortIndex order (== Cell.Ports order).
        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        var overrides = comp.Parameters
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();

        return new Instance(comp.InstanceName, cellName, nets, overrides);
    }

    // ── Shared geometric union helper ────────────────────────────────────────────

    /// <summary>
    /// Adds the GEOMETRIC connectivity unions to <paramref name="uf"/>: wire-vertex chains, T-junction
    /// auto-dots, and user-dot 4-way crossings — all from ComputeConnectivityGeometry, the single source
    /// of connectivity truth. Shared by ExtractModel and FindNodeLabel so the editor's one-label-per-node
    /// rule matches extraction. Does not seed component pins, shorts, or label unions; callers add those.
    /// </summary>
    private static void AddGeometricUnions(
        SchematicEditModel model, Func<double, double, (long, long)> QK, UnionFind uf)
    {
        // Wire vertices; consecutive vertices of one wire = one net.
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            if (pts.Count == 0) continue;
            var first = QK(pts[0].X, pts[0].Y);
            uf.Add(first);
            for (int i = 1; i < pts.Count; i++)
            {
                var next = QK(pts[i].X, pts[i].Y);
                uf.Add(next);
                uf.Union(first, next);
                first = next;
            }
        }

        var cg = model.ComputeConnectivityGeometry();

        // T-junction unions: an auto-dot key (a wire endpoint on another wire's interior) unions with it.
        foreach (var autoDotKey in cg.AutoDotKeys)
        {
            double wx = autoDotKey.Item1 * model.GridSize;
            double wy = autoDotKey.Item2 * model.GridSize;
            foreach (var wire in model.Wires)
            {
                var pts = wire.Points;
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    if (SchematicGeometry.PointOnSegmentInterior(wx, wy,
                            pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                            SchematicEditModel.ConnectTolerance))
                    {
                        uf.Union(autoDotKey, QK(pts[i].X, pts[i].Y));
                        break;
                    }
                }
            }
        }

        // User-dot crossing unions: a dot-gated 4-way crossing connects the wires through it.
        foreach (var dot in model.Dots)
        {
            if (!cg.IsCrossingAtDot(dot.X, dot.Y)) continue;
            (long, long)? firstKey = null;
            foreach (var wire in model.Wires)
            {
                var pts = wire.Points;
                bool onInterior = false;
                for (int i = 0; i < pts.Count - 1 && !onInterior; i++)
                    if (SchematicGeometry.PointOnSegmentInterior(dot.X, dot.Y,
                            pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                            SchematicEditModel.ConnectTolerance))
                        onInterior = true;
                if (!onInterior) continue;
                var wireKey = QK(pts[0].X, pts[0].Y);
                if (firstKey is null) firstKey = wireKey;
                else uf.Union(firstKey.Value, wireKey);
            }
        }
    }

    /// <summary>
    /// Groups the model's net labels by physical node (wires connected via shared vertices, T-junctions,
    /// or dot crossings — the same connectivity as extraction). Returns ONLY nodes carrying more than one
    /// label — the merge set the editor must collapse. Each group is in NetLabels (creation) order, so
    /// element [0] is the label to keep.
    /// </summary>
    public static List<List<EditableNetLabel>> LabelsSharingNode(SchematicEditModel model)
    {
        var result = new List<List<EditableNetLabel>>();
        if (model.NetLabels.Count < 2) return result;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        var byRoot = new Dictionary<(long, long), List<EditableNetLabel>>();
        foreach (var lbl in model.NetLabels)
        {
            var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);
            if (k is null || !uf.Contains(k.Value)) continue;
            var root = uf.Find(k.Value);
            if (!byRoot.TryGetValue(root, out var list)) byRoot[root] = list = [];
            list.Add(lbl);
        }

        foreach (var g in byRoot.Values)
            if (g.Count > 1) result.Add(g);
        return result;
    }

    /// <summary>
    /// Returns a net label already present on the same electrical node as wire <paramref name="wireId"/>
    /// (connected via shared vertices, T-junctions, or dot crossings), excluding the label whose Id is
    /// <paramref name="exceptId"/>; null if the node carries no other label. Uses the same connectivity
    /// as extraction. The editor uses this to keep at most one label per node.
    /// </summary>
    public static EditableNetLabel? FindNodeLabel(
        SchematicEditModel model, string wireId, string? exceptId = null)
    {
        var target = model.FindWire(wireId);
        if (target is null || target.Points.Count == 0) return null;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        var targetKey = QK(target.Points[0].X, target.Points[0].Y);
        if (!uf.Contains(targetKey)) return null;
        var targetRoot = uf.Find(targetKey);

        foreach (var lbl in model.NetLabels)
        {
            if (exceptId is not null && lbl.Id == exceptId) continue;
            var k = FindLabelNetKey(uf, QK, gs, model, lbl.X, lbl.Y);
            if (k is null || !uf.Contains(k.Value)) continue;
            if (uf.Find(k.Value) == targetRoot) return lbl;
        }
        return null;
    }

    /// <summary>
    /// Returns every wire id on the same electrical node(s) as <paramref name="seedWireIds"/>: the full
    /// set of wires connected via shared vertices, T-junctions, and dot crossings (the same geometric
    /// connectivity as extraction). Seed ids that exist in the model are always included. A wire is one
    /// node end-to-end (AddGeometricUnions chains a wire's vertices into a single root), so this returns
    /// the connected-wire set for the touched net. Used by the crossing rubber-band to grab a whole net
    /// from a single touched wire.
    /// </summary>
    public static HashSet<string> ConnectedWireIds(SchematicEditModel model, IEnumerable<string> seedWireIds)
    {
        var result = new HashSet<string>();
        foreach (var id in seedWireIds)
            if (model.FindWire(id) is not null) result.Add(id);
        if (result.Count == 0 || model.Wires.Count == 0) return result;

        double gs = model.GridSize;
        (long, long) QK(double x, double y) => ((long)Math.Round(x / gs), (long)Math.Round(y / gs));

        var uf = new UnionFind();
        AddGeometricUnions(model, QK, uf);

        // A wire's first-vertex root identifies its net node; wires sharing a node share the root.
        var seedRoots = new HashSet<(long, long)>();
        foreach (var id in result)
        {
            var w = model.FindWire(id);
            if (w is null || w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k)) seedRoots.Add(uf.Find(k));
        }
        if (seedRoots.Count == 0) return result;

        foreach (var w in model.Wires)
        {
            if (w.Points.Count == 0) continue;
            var k = QK(w.Points[0].X, w.Points[0].Y);
            if (uf.Contains(k) && seedRoots.Contains(uf.Find(k)))
                result.Add(w.Id);
        }
        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Net name for port <paramref name="pi"/> of <paramref name="comp"/> at world (px, py).
    /// Detached ports use their synthetic key so they never share a net with their P-cell.
    /// Shared by EmitInstance (primitives) and EmitCellInstance.
    /// </summary>
    private static string NetForPort(
        EditableComponent comp, int pi, double px, double py,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys)
    {
        if (comp.IsPortDetached(pi) && detachedKeys.TryGetValue((comp.Id, pi), out var dk))
            return NetAtKey(uf, netNames, dk);
        return NetAt(uf, QK, netNames, px, py);
    }

    /// <summary>
    /// Find the union-find key for a net label at (lx, ly).
    /// First checks exact coincidence (label placed on a wire vertex or pin);
    /// then scans wire segments with gs/2 tolerance (label placed mid-span).
    /// Returns null if the label is not on any wire or pin.
    /// </summary>
    private static (long, long)? FindLabelNetKey(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        double gs,
        SchematicEditModel model,
        double lx, double ly)
    {
        var lKey = QK(lx, ly);
        if (uf.Contains(lKey)) return lKey;

        // Label not on a vertex — scan wire segments.
        double tol = gs / 2.0;
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                if (SchematicGeometry.PointOnSegment(
                        lx, ly, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                    return QK(pts[i].X, pts[i].Y);
            }
        }
        return null;
    }

    /// <summary>
    /// Assigns a stable name to each union-find root.
    /// Priority: ground → "0"; net-label name; auto-stable n1/n2/…
    /// </summary>
    private static Dictionary<(long, long), string> AssignNetNames(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        double gs,
        SchematicEditModel model,
        Dictionary<EditableNetLabel, (long, long)?> labelNetKeys,
        List<string> conflicts,
        IEnumerable<(long, long)>? extraRoots = null,
        IReadOnlyDictionary<(long, long), string>? pinNetNameMap = null,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null,
        HashSet<string>? labeledNetNames = null)
    {
        var rootToName = new Dictionary<(long, long), string>();

        // Ground components → net "0".
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.Ground) continue;
            var (px, py) = comp.GetPortWorldCoord(0);
            var k = QK(px, py);
            if (!uf.Contains(k)) continue;
            rootToName[uf.Find(k)] = "0";
        }

        // Net labels — applied after ground so "0" always wins a ground-label conflict.
        foreach (var lbl in model.NetLabels)
        {
            var k = labelNetKeys[lbl];
            if (k is null || !uf.Contains(k.Value)) continue;
            var root = uf.Find(k.Value);
            if (rootToName.TryGetValue(root, out var existing))
            {
                if (existing != "0" && existing != lbl.Name)
                    conflicts.Add(
                        $"Net conflict: labels '{existing}' and '{lbl.Name}' on same net");
            }
            else
                rootToName[root] = lbl.Name;
        }

        // Pin port names — a Pin OWNS its net's name (beats a coincident label); ground still wins.
        if (pinNetNameMap is not null)
        {
            foreach (var (key, portName) in pinNetNameMap)
            {
                if (!uf.Contains(key)) continue;
                var root = uf.Find(key);
                if (rootToName.TryGetValue(root, out var existing) && existing == "0")
                {
                    // Pin sits on the ground net — interface can't bind to the parent. Warn, keep "0".
                    conflicts.Add($"Pin net '{portName}' is tied to ground inside the cell; " +
                                  $"its interface will not connect to the parent.");
                    continue;
                }
                rootToName[root] = portName;   // override any label/auto name
            }
        }

        // Auto-names: deterministic stable order — component list order, then PortIndex order.
        var seen = new HashSet<(long, long)>();
        var orderedRoots = new List<(long, long)>();

        foreach (var comp in model.Components)
        {
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                var k = QK(px, py);
                if (!uf.Contains(k)) continue;
                var root = uf.Find(k);
                if (!rootToName.ContainsKey(root) && seen.Add(root))
                    orderedRoots.Add(root);
            }
        }
        foreach (var wire in model.Wires)
        {
            if (wire.Points.Count == 0) continue;
            var k = QK(wire.Points[0].X, wire.Points[0].Y);
            if (!uf.Contains(k)) continue;
            var root = uf.Find(k);
            if (!rootToName.ContainsKey(root) && seen.Add(root))
                orderedRoots.Add(root);
        }

        // Detached-port unique keys: their roots never appear in the component/wire P-cell scan
        // above, so add them explicitly to ensure they receive auto-names (not fall through to "0").
        if (extraRoots is not null)
        {
            foreach (var dk in extraRoots)
            {
                if (!uf.Contains(dk)) continue;
                var root = uf.Find(dk);
                if (!rootToName.ContainsKey(root) && seen.Add(root))
                    orderedRoots.Add(root);
            }
        }

        int idx = 1;
        foreach (var root in orderedRoots)
            rootToName[root] = $"n{idx++}";

        // Collect net names whose final assigned name still comes from a user-placed label.
        // A Pin-overridden label (rootToName[root] != lbl.Name) is excluded; ground "0" is also excluded.
        if (labeledNetNames is not null)
        {
            foreach (var lbl in model.NetLabels)
            {
                var k = labelNetKeys[lbl];
                if (k is null || !uf.Contains(k.Value)) continue;
                var root = uf.Find(k.Value);
                if (rootToName.TryGetValue(root, out var finalName) && finalName == lbl.Name)
                    labeledNetNames.Add(finalName);
            }
        }

        return rootToName;
    }

    /// <summary>
    /// Returns the name of the net at world position (x, y), or "0" if the point is
    /// not in the union-find (unconnected pin → treat as ground for safety).
    /// </summary>
    private static string NetAt(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> names,
        double x, double y)
    {
        var k = QK(x, y);
        if (!uf.Contains(k)) return "0";
        var root = uf.Find(k);
        return names.TryGetValue(root, out var n) ? n : "0";
    }

    /// <summary>Net name for a specific union-find key, or "0" if the key is absent.</summary>
    private static string NetAtKey(UnionFind uf, Dictionary<(long, long), string> names, (long, long) key)
    {
        if (!uf.Contains(key)) return "0";
        var root = uf.Find(key);
        return names.TryGetValue(root, out var n) ? n : "0";
    }

    private static Instance? EmitInstance(
        EditableComponent comp,
        SchematicEditModel model,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        Technology? microstripTech = null,
        List<string>? warningsOut = null)
    {
        var reference = ComponentTypeRegistry.EngineReference(comp.Symbol, comp.PortCount);
        // ToneSource with indexed V[1]/Freq[1] format (NumFreqs present) → use V_nTone factory.
        if (comp.Symbol == SymbolKind.ToneSource &&
            comp.Parameters.Any(p => p.Name == "NumFreqs"))
            reference = "V_nTone";

        // SnP: filter UI-only params and separate the optional reference-node pin.
        if (comp.Symbol == SymbolKind.Snp)
        {
            bool hasRefNode = comp.Parameters
                .FirstOrDefault(p => p.Name == "RefNode")?.Expression
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
            int numPorts = comp.PortCount;

            var overrides = comp.Parameters
                .Where(p => p.Name is not "RefNode" and not "PinConfig" and not "Pitch")
                .Select(p =>
                {
                    var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                    return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
                })
                .ToList();

            var snpNets = new List<string>();
            var portDefs = GetEffectivePortDefs(model, comp, cellRefResolutions);
            foreach (var def in portDefs)
            {
                if (hasRefNode && def.PortIndex == numPorts) continue; // ref pin handled separately
                var (px, py) = model.PortWorldOf(comp, def);
                snpNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }

            string? refNetBinding = null;
            if (hasRefNode)
            {
                for (int di = 0; di < portDefs.Count; di++)
                {
                    if (portDefs[di].PortIndex == numPorts)
                    {
                        var (px, py) = model.PortWorldOf(comp, portDefs[di]);
                        refNetBinding = NetForPort(comp, numPorts, px, py, uf, QK, netNames, detachedKeys);
                        break;
                    }
                }
            }

            return new Instance(comp.InstanceName, reference, snpNets, overrides)
                { RefNetBinding = refNetBinding };
        }

        // wBond: 2M+1 terminals, taken from the symbol GENERATED by the referenced .wBond design.
        //
        // R-wbb2-2 — the terminal ORDER is the contract, and it is stated here rather than left to
        // the general loop below: WBondModel reads Nodes[2k]/Nodes[2k+1] as array k's input and
        // output and Nodes[2M] as REF, so the nets must arrive in pin-number order. Sorting is what
        // makes that true rather than coincidental; a transposition here produces a circuit that
        // solves and reports the wrong array's inductance on the wrong net.
        //
        // §5 question 2 — REF *does* appear in NetBindings, and the model ignores the last one.
        // WBondModel.PortCount is 2M+1 and its TerminalNames end in "REF", so the elaborator binds
        // 2M+1 nets; the stamp then uses 2M of them. The alternative (omit REF and make PortCount
        // mean something other than the symbol's pin count) would put two different meanings on one
        // number, and both sides would have to remember which. WB20 makes REF a DECLARATION — the
        // user has to be able to SAY which net is the reference plane — and a declaration nobody can
        // wire is not one.
        if (comp.Symbol == SymbolKind.WBond)
        {
            var wbDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                             .OrderBy(d => d.PortIndex)
                             .ToList();

            if (wbDefs.Count == 0)
            {
                string named = comp.Parameters.FirstOrDefault(p => p.Name == "File")?.Expression ?? "";
                warningsOut?.Add(string.IsNullOrWhiteSpace(named)
                    ? $"wBond '{comp.InstanceName}' names no design file, so it has no pins; skipped."
                    : $"wBond '{comp.InstanceName}': the design '{named}' could not be read, or declares "
                      + "no wire arrays, so it has no pins; skipped.");
                return null;
            }

            var wbNets = new List<string>(wbDefs.Count);
            foreach (var def in wbDefs)
            {
                var (px, py) = model.PortWorldOf(comp, def);
                wbNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }

            var wbOverrides = comp.Parameters
                // `Arrays` is circuitRF's own record of the array list this instance was wired
                // against (§5 question 3) — editor bookkeeping, never an engine parameter. Same
                // rule CvData and ShowBias already follow.
                .Where(p => p.Name != "Arrays")
                // A blank value means "use the design's own", and it must be DROPPED rather than
                // emitted: `Temp=` with nothing after it is the empty-parameter-value trap already
                // recorded in src/Core/CLAUDE.md, where the .cnl reader glues the next token on as
                // the value and silently eats the parameters after it.
                .Where(p => !string.IsNullOrWhiteSpace(p.Expression))
                .Select(p =>
                {
                    var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                    return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
                })
                .ToList();

            return new Instance(comp.InstanceName, reference, wbNets, wbOverrides);
        }

        var overrides2 = comp.Parameters
            // CvData is editor-only: the raw C-V table persists in .csch for re-editing, but it is
            // not an engine parameter (the NonlinearC model reads only C0..Cn). Keep it out of the
            // netlist/elaboration the same way the SnP branch drops RefNode/PinConfig/Pitch.
            // ShowBias is the Tuner family's display-only bias-branch toggle (loadpull.md §1.1) — it
            // drives the glyph only and must NEVER reach the engine, so it is dropped here too. The
            // extracted Instance is therefore identical whether ShowBias is true or false.
            // SignalLayer/GroundReference (brief-technology-editor-units-and-layers.md R-tec-6) are
            // resolution INPUTS consumed entirely below, within this same method — mirroring how
            // H/T/Er/Sigma/TanD are resolution OUTPUTS emitted only from here (R-pc-8's "never
            // declared cell parameters" convention, applied symmetrically): they never reach the
            // engine as raw overrides, so no .cnl quoting/Elaborator string-param handling is needed
            // for them at all.
            .Where(p => p.Name is not "CvData" and not "ShowBias" and not "SignalLayer" and not "GroundReference")
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();

        // Tuner family: 1 symbol pin (DUT-facing) but the engine TunerModel needs 2 declared nets.
        // All three tiles emit Reference "Tuner" with the SAME net ordering (loadpull.md §1, §9):
        // Nodes[0] = pin (DUT-facing), Nodes[1] = "0" (reference, hard-coded ground; exposing it as a
        // second pin is DEFERRED). The SourceTuner's internal RF-drive node is minted by the engine
        // (Elaborator → __tuner_<inst>_outer), so all three tiles are net-identical here.
        if (comp.Symbol is SymbolKind.Tuner or SymbolKind.LoadTuner or SymbolKind.SourceTuner)
        {
            var def = GetEffectivePortDefs(model, comp, cellRefResolutions)[0];
            var (px, py) = model.PortWorldOf(comp, def);
            string pinNet = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
            var tunerNets = new List<string> { pinNet, "0" };   // [Nodes0 = DUT-facing, Nodes1 = ground]
            return new Instance(comp.InstanceName, reference, tunerNets, overrides2);
        }

        // TermG: 1 symbol pin (Term's own port-1 identity) but the engine "Port" model needs 2
        // declared nets — R-hk-6: reuses Term's model, node 2 permanently tied to ground ("0"),
        // never a second, parallel port model. Electrically identical to Term with port 2 wired
        // to GND.
        if (comp.Symbol is SymbolKind.TermG)
        {
            var def = GetEffectivePortDefs(model, comp, cellRefResolutions)[0];
            var (px, py) = model.PortWorldOf(comp, def);
            string pinNet = NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys);
            var termgNets = new List<string> { pinNet, "0" };
            return new Instance(comp.InstanceName, reference, termgNets, overrides2);
        }

        // R-pc-8: microstrip components get their substrate injected as extra parameter overrides,
        // resolved from the schematic's own workspace technology — the "one parameter list" the
        // user sees (W/L/Angle/...) is untouched; H/T/Er/Sigma/TanD are never declared cell
        // parameters (see MicrostripSubstrateInjection's own doc comment).
        if (MicrostripSubstrateInjection.IsMicrostripKind(comp.Symbol))
        {
            // R-tec-6/8: an empty SignalLayer/GroundReference means "follow the technology" — pass
            // null (never an empty string) so SubstrateResolver's own null-means-default convention
            // applies unchanged; only a genuinely non-empty override reaches it.
            string? signalOverride = NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "SignalLayer")?.Expression);
            string? groundOverride = NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "GroundReference")?.Expression);

            var substrateOverrides = MicrostripSubstrateInjection.BuildOverrides(
                microstripTech, out var warning, signalOverride, groundOverride);
            overrides2.AddRange(substrateOverrides);
            if (warning is not null)
                warningsOut?.Add($"{comp.InstanceName}: {warning}");
        }

        // All built-in primitives: emit terminals in PortIndex order.
        var nets2 = new List<string>();
        foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets2.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }
        return new Instance(comp.InstanceName, reference, nets2, overrides2);
    }

    // ── Cell-ref-aware port geometry helpers ─────────────────────────────────

    /// <summary>
    /// Builds the per-model cell-ref resolution map (mirrors BuildRenderModel.ResolveAllCellRefs).
    /// Returns null when <paramref name="model"/>.SchematicDirectory is not set.
    /// </summary>
    private static Dictionary<string, CellSymbolResolution>? BuildCellRefResolutions(
        SchematicEditModel model)
    {
        Dictionary<string, CellSymbolResolution>? result = null;
        foreach (var comp in model.Components)
        {
            if (comp.ExternalSymbolRef is not { } symRef) continue;
            // Mirrors BuildRenderModel.ResolveAllCellRefs: a CellRef needs the schematic's own
            // directory; a wBond reference carries its own resolution rule and does not.
            if (model.SchematicDirectory is null && !WBondSymbolProvider.IsWBondRef(symRef)) continue;
            result ??= new Dictionary<string, CellSymbolResolution>(StringComparer.Ordinal);
            result[comp.Id] = CellSymbolResolver.Resolve(symRef, model.SchematicDirectory);
        }
        return result;
    }

    /// <summary>
    /// Cell-ref-aware port definitions for a component in PortIndex order.
    /// Uses resolved .csym pins when available (SchematicDirectory set + symbol found);
    /// falls back to built-in SymbolPortDefs for unresolvable cell-refs so existing
    /// schematics without SchematicDirectory set continue to extract correctly.
    /// For built-in (non-cell-ref) components, PortDefsOf already returns SymbolPortDefs.
    /// </summary>
    private static IReadOnlyList<(float LocalX, float LocalY, int PortIndex)> GetEffectivePortDefs(
        SchematicEditModel model, EditableComponent comp,
        Dictionary<string, CellSymbolResolution>? resolutions)
    {
        var defs = model.PortDefsOf(comp, resolutions);
        if (defs.Count > 0) return defs;

        // A wBond has NO built-in geometry to fall back to (SymbolPortDefs returns empty for it),
        // and inventing two terminals here would emit a two-net instance for a component the
        // elaborator expects 2M+1 nets from — a different circuit that still parses. The empty
        // result is what the caller turns into a named refusal.
        if (comp.Symbol == SymbolKind.WBond) return [];

        // Fallback for unresolvable cell-refs: use built-in placeholder geometry.
        // This keeps pre-fix behavior for models without SchematicDirectory set.
        var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
        var result   = new (float LocalX, float LocalY, int PortIndex)[portDefs.Length];
        for (int i = 0; i < portDefs.Length; i++)
            result[i] = (portDefs[i].LocalX, portDefs[i].LocalY, i);
        return result;
    }

    /// <summary>R-tec-8: an empty/whitespace-only stored value means "follow the technology" —
    /// normalizes it to null so <c>SubstrateResolver</c>'s own null-means-default convention applies
    /// unchanged, rather than treating an empty string as a (trivially failing) named override.</summary>
    private static string? NonEmptyOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── Pin helpers ──────────────────────────────────────────────────────────

    private readonly record struct PinInfo(int Num, string Name, string Polarity, (long, long) Key);

    /// <summary>
    /// Collects all valid Pin instances from the schematic: those with a positive Num and a
    /// connected (non-detached) port that is in the union-find.
    /// </summary>
    private static List<PinInfo> CollectPinInfos(
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK)
    {
        var infos = new List<PinInfo>();
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.Pin) continue;
            if (comp.IsPortDetached(0)) continue;

            var numParam  = comp.Parameters.FirstOrDefault(p => p.Name == "Num");
            var nameParam = comp.Parameters.FirstOrDefault(p => p.Name == "Name");
            var polParam  = comp.Parameters.FirstOrDefault(p => p.Name == "Polarity");

            if (!int.TryParse(numParam?.Expression ?? "0", out int num) || num <= 0) continue;

            var (px, py) = comp.GetPortWorldCoord(0);
            var k = QK(px, py);
            if (!uf.Contains(k)) continue;

            infos.Add(new PinInfo(
                num,
                nameParam?.Expression?.Trim() ?? "",
                polParam?.Expression?.Trim() ?? "",
                k));
        }
        return infos;
    }

    /// <summary>
    /// Builds a map from each Pin's union-find key to its port name string.
    /// Single-ended Pin: port name = Name (if non-empty) else "P{Num}".
    /// Differential pair (Polarity=Plus and Polarity=Minus, same Num): "{base}+" / "{base}-".
    /// </summary>
    private static IReadOnlyDictionary<(long, long), string> BuildPinNetNameMap(
        List<PinInfo> pinInfos,
        UnionFind uf)
    {
        var map = new Dictionary<(long, long), string>();
        foreach (var pin in pinInfos)
        {
            var root = uf.Find(pin.Key);
            if (map.ContainsKey(root)) continue; // first one wins for net-name priority

            string baseName = pin.Name.Length > 0 ? pin.Name : $"P{pin.Num}";
            string portName = pin.Polarity.Equals("Plus",  StringComparison.OrdinalIgnoreCase) ? baseName + "+" :
                              pin.Polarity.Equals("Minus", StringComparison.OrdinalIgnoreCase) ? baseName + "-" :
                              baseName;

            map[root] = portName;
        }
        return map;
    }

    /// <summary>
    /// Builds the ordered CellPorts list from PinInfos. Sorts by Num; differential pairs
    /// (Polarity Plus/Minus sharing a Num) contribute "{base}+" then "{base}-" in that order.
    /// Adds conformance conflict messages for duplicate non-differential Nums and Num gaps.
    /// </summary>
    private static IReadOnlyList<string> BuildCellPorts(List<PinInfo> pinInfos, List<string> conflicts)
    {
        if (pinInfos.Count == 0) return [];

        // Group by Num.
        var byNum = pinInfos
            .GroupBy(p => p.Num)
            .OrderBy(g => g.Key)
            .ToList();

        var ports = new List<string>();

        foreach (var grp in byNum)
        {
            int num = grp.Key;
            var items = grp.ToList();

            var plusItem  = items.FirstOrDefault(p => p.Polarity.Equals("Plus",  StringComparison.OrdinalIgnoreCase));
            var minusItem = items.FirstOrDefault(p => p.Polarity.Equals("Minus", StringComparison.OrdinalIgnoreCase));

            bool isDiff = plusItem.Key != default && minusItem.Key != default;

            if (isDiff)
            {
                string baseName = plusItem.Name.Length > 0 ? plusItem.Name :
                                  minusItem.Name.Length > 0 ? minusItem.Name :
                                  $"P{num}";
                // Strip trailing + or - from the Name if the user set it that way.
                if (baseName.EndsWith('+') || baseName.EndsWith('-'))
                    baseName = baseName[..^1];
                ports.Add(baseName + "+");
                ports.Add(baseName + "-");

                // Extra items beyond the pair = conflict.
                int extras = items.Count - 2;
                if (extras > 0)
                    conflicts.Add($"Pin Num={num} has {items.Count} Pins; differential pair expects exactly 2.");
            }
            else
            {
                if (items.Count > 1)
                    conflicts.Add($"Duplicate Pin Num={num} ({items.Count} Pins share this number).");

                var first = items[0];
                string portName = first.Name.Length > 0 ? first.Name : $"P{num}";
                ports.Add(portName);
            }
        }

        // Check for gaps in the Num sequence (1, 2, 3, …).
        var nums = byNum.Select(g => g.Key).ToHashSet();
        int maxNum = byNum[^1].Key;
        for (int i = 1; i < maxNum; i++)
        {
            if (!nums.Contains(i))
                conflicts.Add($"Pin Num={i} is missing (gap in sequence 1..{maxNum}).");
        }

        return ports;
    }

    // ── Union-Find ───────────────────────────────────────────────────────────────

    private sealed class UnionFind
    {
        private readonly Dictionary<(long, long), (long, long)> _parent = new();

        public void Add((long, long) key)
        {
            if (!_parent.ContainsKey(key)) _parent[key] = key;
        }

        public bool Contains((long, long) key) => _parent.ContainsKey(key);

        public (long, long) Find((long, long) x)
        {
            while (true)
            {
                var p = _parent[x];
                if (p == x) return x;
                var gp = _parent[p];
                _parent[x] = gp; // path compression (halving)
                x = p;
            }
        }

        public void Union((long, long) a, (long, long) b)
        {
            Add(a); Add(b);
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }

    /// <summary>
    /// Turns a kit's fixed-parameter value into something the expression engine will accept.
    ///
    /// <para><b>These values are literal TEXT, not expressions.</b> A kit writes
    /// <c>DataPath="Kit_..._Data"</c> in its own netlist; the reader strips the quotes because the
    /// text is what a worker wants. Emitted verbatim into a <see cref="ParameterAssignment"/> it is
    /// then EVALUATED, and a bare word is a variable name — so a perfectly ordinary folder name
    /// fails elaboration with "Unresolved name '...' in scope 'global'". Quoting restores the
    /// author's meaning.</para>
    ///
    /// <para>A value that already parses as a number is left alone: it is a numeric literal and
    /// quoting it would make it a string. So is one that is already quoted, or that contains a quote
    /// (nothing here can escape one safely, and reporting a wrong value beats inventing a syntax).</para>
    /// </summary>
    internal static string AsLiteralExpression(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') return value;
        if (trimmed.Contains('"')) return value;
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out _)) return value;

        return $"\"{trimmed}\"";
    }
}
