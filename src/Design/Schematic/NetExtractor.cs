using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;
using CircuitRF.WBond;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
using CircuitRF.Design.Layout.Em.ParametricEm;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// Turns a <see cref="SchematicEditModel"/> into a <see cref="TestBench"/> (Design model).
/// Headless, framework-free — no Avalonia, no Skia. Reuses ComputeConnectivityGeometry
/// for the geometric union (T-junctions, crossing predicate); applies same-name label union
/// on top (§2.1.6). Terminal order is the contract: NetBindings[k] = net at terminal k.
/// </summary>
public static class NetExtractor
{
    /// <summary>
    /// The tiles that show N pins for a model declaring N ground-referenced PORTS, so extraction
    /// emits 2N nets by appending <c>"0"</c> after each pin's own net.
    ///
    /// <para>One set rather than one branch per kind: the rule is a single rule, and repeating it
    /// eleven times is eleven chances for the eleventh to be subtly different. A kind belongs here
    /// when its GLYPH hides the return path, not when its model happens to be N-port —
    /// <c>MixerD</c>, which brings all six of its nets out as pins, is deliberately not a member.</para>
    /// </summary>
    internal static readonly HashSet<SymbolKind> GroundReferencedPortBlocks =
    [
        SymbolKind.Mixer,
        // The system blocks (brief-sys-1). Every one of them is ground-referenced by construction:
        // a system block diagram draws a signal path, and its return is the ground plane.
        SymbolKind.Balun, SymbolKind.Circulator, SymbolKind.Switch, SymbolKind.SwitchD,
        SymbolKind.Amp, SymbolKind.Coupler, SymbolKind.Hybrid90, SymbolKind.Hybrid180,
        SymbolKind.Filter, SymbolKind.Atten, SymbolKind.Duplexer, SymbolKind.Delay,
        SymbolKind.Combiner,
    ];

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

        /// <summary>
        /// Each schematic-backed cell's library name — the name the <c>.cnl</c> spells its instances'
        /// type with — mapped to the identity its resolver gave it (<see cref="CellResolution.Key"/>:
        /// the absolute cell folder, for the disk and workspace resolvers). A cell the library holds
        /// that is absent here came from a kit's netlist rather than a schematic.
        /// </summary>
        public IReadOnlyDictionary<string, string> CellKeys { get; init; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Every cell a LOOSE schematic's instance was matched to in the workspace it is run from, or
        /// could have been (<see cref="ExternalCellRef.FindInHostWorkspace"/>). Kept apart from
        /// <see cref="Conflicts"/> because each one says which cell the run USED, and that has to be
        /// said wherever the design runs, by surfaces that do not report extraction conflicts at all.
        /// </summary>
        public IReadOnlyList<HostCellChoice> HostCellChoices { get; init; } = [];

        /// <summary>
        /// One sentence per placed vendor part number whose stated frequency range the analyses leave
        /// (brief vendorlib-3 R-vl3-3, <see cref="VendorLibraries.VendorPartRange"/>). Kept apart from
        /// <see cref="Conflicts"/> for the reason <see cref="HostCellChoices"/> is: it is said wherever
        /// the design RUNS — Messages in the GUI, stderr from the CLI — and it is a warning about the
        /// answer, not a defect in the drawing.
        /// </summary>
        public IReadOnlyList<string> PartRangeWarnings { get; init; } = [];

        /// <summary>
        /// The placed vendor parts that could not be read — a library the workspace does not name, a
        /// library folder that is missing, a part or file the library no longer holds (brief vendorlib-3
        /// R-vl3-2c). Each is ALSO among <see cref="Conflicts"/>, which the GUI posts; this list is what a
        /// headless run says on stderr, so a skipped part is never silent there either.
        /// </summary>
        public IReadOnlyList<string> PartRefusals { get; init; } = [];

        /// <summary>
        /// Every placed cell the netlist LEAVES OUT — one with no schematic or model, one whose symbol's pins do not
        /// match its cell's Pins, one that instantiates itself — each as the conflict that says why. Each is ALSO among
        /// <see cref="Conflicts"/>. A run refuses while any is here (<see cref="RunRefusal"/>): the circuit it would
        /// simulate is not the one drawn, and an amplifier left out reads as an open with nothing else to say so.
        /// </summary>
        public IReadOnlyList<string> DroppedInstances { get; init; } = [];

        /// <summary>
        /// Every device inside a kit cell this circuit REACHES that names a <c>.model</c> card nothing can build — no
        /// compiled model implementing or standing in for it, and no built-in — each as the conflict that says why and
        /// what to declare. Each is ALSO among <see cref="Conflicts"/>. A run refuses while any is here: left as the kit
        /// wrote it, the device fails elaboration as a cell "not found", which names the card and nothing else.
        /// </summary>
        public IReadOnlyList<string> UnbuiltDevices { get; init; } = [];

        /// <summary>Why a run must not go ahead with this extraction, or null. <see cref="DroppedInstances"/>,
        /// then <see cref="UnbuiltDevices"/>.</summary>
        public string? RunRefusal => RefusalFor(DroppedInstances)
            ?? (UnbuiltDevices.Count == 0 ? null
                : "Not simulated: a device inside a placed kit part cannot be built. " + string.Join(" ", UnbuiltDevices));

        /// <summary><see cref="RunRefusal"/> for a list of dropped instances.</summary>
        public static string? RefusalFor(IReadOnlyList<string> dropped) => dropped.Count == 0 ? null
            : $"Not simulated: {dropped.Count} placed cell(s) would be left out of the circuit, so the result would not "
            + "be the circuit drawn. Give each a schematic, an S-parameter file or a model, or set its Disable to Open. "
            + string.Join(" ", dropped);

        /// <summary>
        /// Every placed vendor part as a tunable — its key, and the candidate parts its <c>Part</c> entry chooses
        /// among (brief vendorlib-4 R-vl4-1/2). Kept here because the netlist cannot say it: a <c>.cnl</c> holds the
        /// cell an instance binds to, never the library part it came from. When the tuning setup tunes or
        /// optimizes a part, its candidates' cells are in <see cref="Library"/> too.
        /// </summary>
        public IReadOnlyList<VendorLibraries.VendorPartTunable> PartTunables { get; init; } = [];
    }

    /// <summary>One <see cref="ExtractionResult.HostCellChoices"/> entry. <see cref="IsWarning"/>
    /// for two candidate cells (the stored path's and the host's, or two in the host); an
    /// information line when the host's one cell was used because the stored path named nothing.</summary>
    public sealed record HostCellChoice(string InstanceName, string Text, bool IsWarning);

    /// <param name="cornerVariables">
    /// Process constants the selected corners bind, already resolved by the caller (see
    /// <see cref="WorkspaceCorners.BindingsFor"/>).
    ///
    /// <para>Resolved OUTSIDE, deliberately: which corners a design is set to is workspace knowledge
    /// (which kits are referenced, and where they are on this machine), and extraction has no business
    /// holding any of it. What arrives here is just a list of variables.</para>
    ///
    /// <para>They are merged on the same terms as a kit netlist's own declarations — the design's own
    /// VAR of the same name wins, and the collision is reported. A corner constant is a kit's
    /// statement about its process; a variable the user wrote is a statement about their design, and
    /// the design is the thing being simulated.</para>
    /// </param>
    /// <param name="cornerBinder">
    /// What a set of corner selections binds — the GUI passes <c>WorkspaceCorners.BindingsFor</c> over the
    /// workspace's axes, the very function that resolved <paramref name="cornerVariables"/>. It resolves
    /// the kit axis selections of the tuning setup's named corners (docs/design/yield.md, overview D10),
    /// each overlaid on the design's own selections, into the values the <c>.cnl</c> corner line binds.
    /// Null where no workspace knowledge is at hand: a corner's kit selections are then reported, not
    /// applied.
    /// </param>
    /// <param name="cornerSections">
    /// The selected corner sections as read (<c>WorkspaceCorners.Bind</c>). A section that includes a VARIANT of a
    /// part's model library — a kit's statistical or mismatch section — supplies that variant's subcircuits and
    /// cards in place of the part library's own, which is what makes the kit's mismatch draws reach the run
    /// (docs/design/spice-models.md §8.11). A section that includes the part's own library changes nothing.
    /// </param>
    public static ExtractionResult Extract(
        SchematicEditModel model, string testBenchName = "tb", ICellResolver? cells = null,
        IReadOnlyList<Variable>? cornerVariables = null,
        Func<IReadOnlyDictionary<string, string>, List<string>, IReadOnlyList<Variable>>? cornerBinder = null,
        IReadOnlyList<PdkCornerSection>? cornerSections = null)
    {
        var lib        = new Library("netlist");
        var imports    = new NetlistImports(cornerSections);
        var conflicts  = new List<string>();
        var scope      = new CellScope { RunBase = SchematicCircuit.ReferenceBaseOfDirectory(model.SchematicDirectory, model.HostWorkspaceRoot) };
        var labeled    = new HashSet<string>(StringComparer.Ordinal);

        var (instances, cellPorts, topVars, topMeas) = ExtractModel(model, cells, lib, imports, scope, conflicts, labeled);

        var tb = new TestBench(testBenchName);
        // The schematic's own technology, as the netlist's `technology` statement (R-as6-1) — RELATIVE to the
        // base the text is read back against (SchematicCircuit.ReferenceBaseOf: the workspace root, else the
        // schematic's folder), which is where Simulate writes it and what every other relative reference in it
        // resolves against. Absent without a TechRef, so the text is unchanged.
        tb.Technology = SchematicTechnology.NetlistRef(model);
        tb.Instances.AddRange(instances);
        tb.GlobalVariables.AddRange(OwnCellParameterDefaults(model, topVars));
        tb.GlobalVariables.AddRange(topVars);

        // A kit netlist brings its own supporting declarations with it. They are merged rather than
        // scoped because the cells that were copied out of it reference them by bare name, and a
        // user declaration of the same name is left standing: the design the user wrote wins over
        // one a kit happened to ship.
        imports.MergeInto(tb, conflicts);

        if (cornerVariables is { Count: > 0 })
        {
            foreach (var v in cornerVariables)
            {
                var existing = tb.GlobalVariables
                    .FirstOrDefault(e => e.Name.Equals(v.Name, StringComparison.Ordinal));

                if (existing is not null)
                {
                    // Only when the two actually DISAGREE. Most of what is already in scope here
                    // came out of the kit's own netlists a moment ago, and a kit routinely states a
                    // shared switch both at the top of a model library and inside every corner
                    // section — so the common case is the design and the corner saying the same
                    // thing, which is agreement rather than a collision to report on every run.
                    if (!string.Equals(existing.Expression?.Trim(), v.Expression?.Trim(), StringComparison.Ordinal))
                        conflicts.Add($"Corner constant '{v.Name}' is also declared in this design " +
                                      $"as '{existing.Expression}' rather than '{v.Expression}'; the " +
                                      $"one declared in this design is used.");
                    continue;
                }
                tb.GlobalVariables.Add(v);
            }
        }

        tb.Measurements.AddRange(topMeas);
        foreach (var name in labeled)
            tb.LabeledNets.Add(name);

        // Analyses attach to the TOP testbench only (data-model §2.1 invariant).
        // All analyses are carried so ParametricSweepEngine can find its inner analysis by name.
        // Dispatch (in SchematicRunService) skips disabled analyses at run time.
        foreach (var analysis in model.Analyses)
            tb.Analyses.Add(analysis);

        // The tuning block rides along unchanged, so the `.cnl` says what the `.csch` says (overview D5).
        // A copy: the netlist is handed to runs that must not reach back into the open document.
        if (model.Tuning is { IsEmpty: false } tuning)
        {
            tb.Tuning = tuning.Clone();
            ResolveCorners(tb.Tuning, model.CornerSelections, cornerBinder, conflicts);
        }

        return new ExtractionResult(tb, conflicts)
        {
            CellPorts = cellPorts, Library = lib, CellKeys = scope.LibraryNameToKey(),
            HostCellChoices = scope.HostCellChoices,
            PartRangeWarnings = VendorLibraries.VendorPartRange.Warnings(tb, imports.VendorParts),
            PartRefusals = imports.VendorRefusals,
            DroppedInstances = imports.DroppedInstances,
            UnbuiltDevices = ReachedUnbuilt(tb, lib, imports.UnbuiltCards),
            PartTunables = PartTunablesOf(model, tb.Tuning, imports, lib, conflicts),
        };
    }

    /// <summary>
    /// The sentences of the unbuilt card devices (<see cref="NetlistImports.UnbuiltCards"/>) the circuit actually
    /// reaches. A kit file's cells are all copied, and most of them are never instantiated: those say their
    /// conflict and refuse nothing.
    /// </summary>
    private static List<string> ReachedUnbuilt(TestBench tb, Library lib, IReadOnlyDictionary<string, string> unbuilt)
    {
        var said = new List<string>();
        if (unbuilt.Count == 0) return said;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<IEnumerable<Instance>>([tb.Instances]);
        while (todo.TryPop(out var instances))
            foreach (var inst in instances)
            {
                if (lib.Find(inst.Reference) is { } cell) { if (seen.Add(cell.Name)) todo.Push(cell.Instances); }
                else if (unbuilt.TryGetValue(inst.Reference, out string? why) && !said.Contains(why)) said.Add(why);
            }
        return said;
    }

    /// <summary>
    /// Every placed vendor part as a tunable (brief vendorlib-4 R-vl4-1/2). Its candidates come from the index
    /// alone; when the tuning setup TUNES or OPTIMIZES the part, each candidate's circuit is read from its own
    /// byte block and emitted as a cell under the part's own name, exactly as the placed part was (R-vl4-2a) —
    /// so an evaluation rebinds the instance to another cell and the elaborator sees an ordinary netlist. A
    /// candidate that does not read is left out and reported, one line per entry naming the parts. There is no cap
    /// on the count (issue #32): a candidate is one subcircuit of a few elements, read once per extraction, and the
    /// search moves from an exhaustive grid to coordinate descent on its own when the list is long.
    /// </summary>
    private static IReadOnlyList<VendorLibraries.VendorPartTunable> PartTunablesOf(
        SchematicEditModel top, TuningSetup? setup, NetlistImports imports, Library lib, List<string> conflicts)
    {
        var result = new List<VendorLibraries.VendorPartTunable>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placed in imports.PlacedVendorParts)
        {
            string? cell = ReferenceEquals(placed.Model, top) ? null : imports.ModelCellNames.GetValueOrDefault(placed.Model);
            if (cell is null && !ReferenceEquals(placed.Model, top)) continue;
            string key = (cell is null ? "" : cell + ":") + placed.Instance + "." + TunableKey.PartParameter;
            if (!seen.Add(key)) continue;

            var entry    = setup?.Variables.FirstOrDefault(e => e.Key == key);
            var selected = VendorLibraries.VendorPartCandidates.Select(entry, placed.Part, out int removed, out string selection);
            bool emit    = entry is { } e0 && (e0.Tune || e0.Opt);

            var candidates = new List<VendorLibraries.VendorPartCandidate>();
            var unreadable = new List<string>();
            foreach (var e in selected)
            {
                if (!emit) { candidates.Add(VendorLibraries.VendorPartCandidates.Candidate(e, "")); continue; }
                if (e.Part.Equals(placed.Part.Entry.Part, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(VendorLibraries.VendorPartCandidates.Candidate(e, placed.Cell));
                    continue;
                }
                string? emitted = EmitCandidate(e, placed, lib, imports, out string? why);
                if (emitted is null) unreadable.Add($"{e.Part} ({why})");
                else candidates.Add(VendorLibraries.VendorPartCandidates.Candidate(e, emitted));
            }
            if (unreadable.Count > 0)
                conflicts.Add($"{key}: {unreadable.Count} candidate part{(unreadable.Count == 1 ? "" : "s")} could not be " +
                              $"read and {(unreadable.Count == 1 ? "was" : "were")} left out: {string.Join(", ", unreadable)}.");

            // The part's VALUE drawn in a Monte Carlo trial (brief vendorlib-5 R-vl5-3c): its cell, and every emitted
            // candidate's, gets the scale parameter an evaluation binds to drawn ÷ nominal. Only then — every other
            // netlist stays byte-identical.
            string? letter = placed.Part.Entry.Header.Quantity switch
            {
                VendorLibraries.VendorPartQuantity.Capacitance => "C",
                VendorLibraries.VendorPartQuantity.Inductance  => "L",
                _ => null,
            };
            string valueKey = (cell is null ? "" : cell + ":") + placed.Instance + "." + letter;
            var valueEntry  = letter is null ? null : setup?.Variables.FirstOrDefault(v => v.Key == valueKey);
            var noTolerance = new List<string>();
            if (valueEntry is { IsStatistical: true })
            {
                // Each candidate carries its OWN tolerance under tol=part (R-vl5-3d): one whose tolerance is not known
                // is left out and said, as an unreadable one is.
                if (emit && valueEntry.Spread?.TolIsPart == true)
                {
                    foreach (var c in candidates.ToList())
                        if (c.Entry.Tolerance is null
                            && !c.Part.Equals(placed.Part.Entry.Part, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Remove(c);
                            noTolerance.Add($"{c.Part} ({c.Entry.ToleranceNote ?? "no tolerance"})");
                        }
                    if (noTolerance.Count > 0)
                        conflicts.Add($"{key}: {noTolerance.Count} candidate part{(noTolerance.Count == 1 ? "" : "s")} with no " +
                                      $"known tolerance {(noTolerance.Count == 1 ? "was" : "were")} left out, since {valueKey} draws " +
                                      $"with tol=part: {string.Join(", ", noTolerance)}.");
                }
                SpiceModelNetlist.ScaleQuantity(lib, placed.Cell, letter!);
                foreach (var c in candidates.Where(c => c.Cell.Length > 0))
                    SpiceModelNetlist.ScaleQuantity(lib, c.Cell, letter!);
            }

            result.Add(new VendorLibraries.VendorPartTunable(
                key, cell, placed.Instance, placed.Part.LibraryName, placed.Part.Entry, placed.Cell, placed.Glyph,
                selection, candidates, selected.Count, removed, unreadable, emit) { NoTolerance = noTolerance });
        }
        return result;
    }

    /// <summary>One candidate's circuit, read from its own block and emitted as the placed part's was. The cell
    /// name, or null with why.</summary>
    private static string? EmitCandidate(VendorLibraries.VendorLibraryEntry e, PlacedVendorPart placed, Library lib,
                                         NetlistImports imports, out string? why)
    {
        var src  = placed.Part;
        var part = new VendorLibraries.VendorPartSource.Resolved(src.WorkspaceRoot, src.LibraryName, src.LibraryRoot,
            System.IO.Path.GetFullPath(System.IO.Path.Combine(src.LibraryRoot, CircuitRF.Core.RefPath.ToNative(e.File))), e);
        var peeked = VendorLibraries.VendorPartSource.Read(part);
        if (peeked.Error is { } error) { why = error; return null; }
        if (SpiceModelPeek.Select(peeked, e.Part) is not { } definition) { why = "its block defines no such subcircuit"; return null; }
        var emission = SpiceModelNetlist.Build(definition, peeked.Scan, part.FilePath, lib, imports.SpiceCells);
        if (emission.Refusal is { } refusal) { why = refusal; return null; }
        if (emission.CellName is null) { why = "it is not a subcircuit"; return null; }
        if (emission.Ports.Count != placed.Ports) { why = $"it has {emission.Ports.Count} ports"; return null; }
        why = null;
        return emission.CellName;
    }

    /// <summary>
    /// A named corner's kit axis selections, resolved into the values its <c>.cnl</c> line binds (yield
    /// overview D10): the corner's selections overlaid on the design's own, bound by the same function
    /// Simulate binds the design's selections with, so a corner reads exactly as the design would read
    /// with those selections made. A <c>.cnl</c> corner therefore never names a kit file. A value the
    /// corner states itself wins over a kit binding of the same name.
    ///
    /// <para>A selection that does not resolve is reported in that function's own words, once per
    /// corner — and only what the corner's own selections add, since the design's selections are
    /// reported where they are applied.</para>
    /// </summary>
    private static void ResolveCorners(
        TuningSetup setup, IReadOnlyDictionary<string, string> designSelections,
        Func<IReadOnlyDictionary<string, string>, List<string>, IReadOnlyList<Variable>>? binder,
        List<string> conflicts)
    {
        List<string>? baseline = null;
        foreach (var corner in setup.Corners)
        {
            if (corner.AxisSelections is not { Count: > 0 } selections) continue;
            corner.AxisSelections = null;

            if (binder is null)
            {
                conflicts.Add($"Corner '{corner.Name}' selects kit corners ({string.Join(", ", selections.Values)}), " +
                              "and no workspace kit corners are available here. They were NOT applied.");
                continue;
            }

            if (baseline is null) binder(designSelections, baseline = []);

            var effective = new Dictionary<string, string>(designSelections, StringComparer.Ordinal);
            foreach (var (axis, section) in selections) effective[axis] = section;

            var problems = new List<string>();
            var bound    = binder(effective, problems);
            foreach (var p in problems.Where(p => !baseline.Contains(p)))
                conflicts.Add($"Corner '{corner.Name}': {p}");

            var values = new OrderedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in bound)
                if (!corner.Values.ContainsKey(v.Name))
                    values[v.Name] = string.IsNullOrEmpty(v.Unit) ? v.Expression : $"{v.Expression} {v.Unit}";
            foreach (var (k, v) in corner.Values) values[k] = v;
            corner.Values = values;
        }
    }

    /// <summary>
    /// A cell's own schematic extracted as the TOP binds the cell's declared parameters at their defaults — the
    /// values an instance placed with no overrides would give it. Without this, a parameterised cell's schematic did
    /// not elaborate on its own (<c>Unresolved name 'dB'</c>), so <c>check</c> reported an error and LVS refused a
    /// cell that is correct, while the same cell simulated fine as an instance. As a sub-cell its parameters are bound
    /// by the instance (<see cref="ParameterDeclaration"/>), which this does not touch.
    ///
    /// <para>Placed AHEAD of the schematic's own VARs, which may be written in terms of them, and skipped for any
    /// name a VAR already defines: what the user wrote in the drawing wins over the interface's default. Read from
    /// disk, as <see cref="DiskCellResolver"/> reads a sub-cell's; an unreadable <c>.ccell</c> binds nothing, as it
    /// declares nothing there either.</para>
    /// </summary>
    private static IEnumerable<Variable> OwnCellParameterDefaults(SchematicEditModel model, IReadOnlyList<Variable> topVars)
    {
        if (model.SchematicDirectory is not { Length: > 0 } schematicDir) return [];
        string cellDir = Path.GetDirectoryName(Path.GetFullPath(schematicDir)) ?? "";
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)
            || !string.Equals(Path.GetFullPath(CellFolder.SubFolderPath(cellDir, ViewType.Schematic)),
                              Path.GetFullPath(schematicDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            return [];

        List<CcellParameter> declared;
        try { declared = CellPersistence.LoadFromFile(ccellPath).Parameters; }
        catch { return []; }

        return declared
            .Where(p => p.Name.Length > 0 && p.DefaultExpression.Length > 0
                        && !topVars.Any(v => v.Name.Equals(p.Name, StringComparison.Ordinal)))
            .Select(p => new Variable(p.Name, p.DefaultExpression,
                                      UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null))
            .ToList();
    }

    // ── Per-model extraction pipeline (shared by top and sub-cells) ─────────

    private static (List<Instance> Instances, IReadOnlyList<string> CellPorts, List<Variable> Variables, List<Measurement> Measurements) ExtractModel(
        SchematicEditModel model,
        ICellResolver?      cells,
        Library             lib,
        NetlistImports      imports,
        CellScope           scope,
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
        //
        // brief-artsch-6 R-as6-1: the schematic's own TechRef FIRST, then the workspace default — one resolver
        // (SchematicTechnology) for every injection below. A TechRef that does not resolve is said, and the
        // lines are left on no technology rather than quietly moved onto the workspace default's substrate.
        var techResolution = SchematicTechnology.Resolve(model);
        if (techResolution.Error is { } techError) conflicts.Add(techError);
        var microstripTech = techResolution.Technology;

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

        // A VProbe attaches the way a NET LABEL does, not the way a device pin does. A device pin
        // unions only where a wire actually has a vertex — right for a device, since a pin sitting
        // mid-span would silently tap a run the user drew straight past it — and wrong for a probe,
        // whose entire gesture is being dropped onto the middle of a wire. So it gets the label's
        // own segment scan. Placed after the geometric unions so the run it lands on is already one
        // class; before naming, so the probe's net is the run's net.
        foreach (var comp in model.Components)
        {
            if (comp.Symbol != SymbolKind.VProbe) continue;
            if (comp.Disable is DisableState.Open or DisableState.Short) continue;
            var vdefs = GetEffectivePortDefs(model, comp, cellRefResolutions);
            if (vdefs.Count == 0 || comp.IsPortDetached(vdefs[0].PortIndex)) continue;
            var (vpx, vpy) = model.PortWorldOf(comp, vdefs[0]);
            if (FindWireSpanKey(model, QK, gs, vpx, vpy) is { } wireKey)
                uf.Union(QK(vpx, vpy), wireKey);
        }

        // Same-name net label union (§2.1.6): labels with identical names make one net,
        // even across physically-disjoint wires.
        var labelNetKeys = new Dictionary<EditableNetLabel, (long, long)?>();
        foreach (var lbl in model.NetLabels)
            labelNetKeys[lbl] = FindLabelNetKey(uf, QK, gs, model, lbl);

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
            // R-lvs4-3b. The list is SchematicExclusions' and not this loop's: LVS reads the same
            // one, and a component that is electrical to one of the two and not the other produces
            // a mismatch whose cause is in neither document.
            if (SchematicExclusions.IsExcluded(comp)) continue;

            // A VProbe touching nothing would otherwise be emitted against its own auto-named net —
            // a row in the results that reads a plausible number for a point in the circuit the user
            // never actually probed. Say so and emit nothing instead.
            if (comp.Symbol == SymbolKind.VProbe &&
                !IsVProbeConnected(model, comp, uf, QK, cellRefResolutions))
            {
                conflicts.Add(
                    $"VProbe {comp.InstanceName} is not connected and will not report any results.");
                continue;
            }

            // A SpiceModel is not a cell reference and not a primitive: it is a file, and what it
            // emits — a device or a subcircuit — is a property of that file. Checked before both,
            // because it carries neither a CellRef nor an engine component of its own.
            if (comp.Symbol == SymbolKind.SpiceModel)
            {
                var sm = EmitSpiceModelInstance(comp, model, uf, QK, netNames, detachedKeys,
                                                cellRefResolutions, lib, imports, conflicts);
                if (sm is not null) instances.Add(sm);
                continue;
            }

            // A placed library MODEL (brief vendorlib-9 R-vl9-3b) runs its .cnl, read once per distinct part per
            // run; it is never descended into as a schematic and never falls through to the paths below.
            if (comp.CellRef is not null && VendorLibraries.ModelLibrary.VendorModelRefs.IsRef(comp.CellRef))
            {
                int before = conflicts.Count;
                var vm = EmitVendorModelInstance(comp, model, uf, QK, netNames, detachedKeys, cellRefResolutions,
                                                 lib, imports, scope, conflicts);
                if (vm is not null) instances.Add(vm);
                else NoteDropped(comp, before, conflicts, imports);
                continue;
            }

            if (comp.CellRef is not null)
            {
                NoteHostWorkspaceCell(model, comp, scope.HostCellChoices);
                int said = conflicts.Count;

                // A part whose definition is a CIRCUIT comes first: a package is a subcircuit, not a
                // device model, whatever else the kit says about it.
                var nb = TryEmitNetlistBackedCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                                          cellRefResolutions, lib, imports, conflicts,
                                                          out bool isCircuitBacked);
                if (nb is not null) { instances.Add(nb); continue; }

                // A part the kit declares as a circuit is never emitted as a device instead. Falling
                // back would answer with something the user did not place — the failure has already
                // been reported, and skipping is the honest outcome.
                if (isCircuitBacked) { NoteDropped(comp, said, conflicts, imports); continue; }

                // A cell backed by an external device provider is a LEAF: emit one ExtDevice
                // instance rather than descending into a schematic it deliberately does not have.
                var ext = TryEmitExternalDeviceInstance(comp, model, uf, QK, netNames, detachedKeys,
                                                        cellRefResolutions, conflicts);
                if (ext is not null) { instances.Add(ext); continue; }

                var ci = EmitCellInstance(comp, model, uf, QK, netNames, detachedKeys,
                                          cells, lib, imports, scope, conflicts, cellRefResolutions);
                if (ci is not null) instances.Add(ci);
                else if (cells is not null) NoteDropped(comp, said, conflicts, imports);
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
                string  expr = p.Expression;
                // A VAR row whose unit column is empty may still carry the unit INLINE — "2 GHz" —
                // because that is the spelling a .cnl uses and the one the schematic renders the
                // row back as ("RFfreq = 2 GHz"). The expression parser reads a unit only when it
                // is GLUED to a number (`2GHz`), so leaving a spaced one there makes the whole variable VANISH: Elaborator skips
                // a global it cannot resolve and does it silently, after which every reader of that
                // variable falls back or fails somewhere else entirely. Lift it into the unit
                // column's meaning instead. The row's own unit column still wins when it is set.
                if (unit is null)
                    (expr, unit) = LiftInlineUnit(expr);
                frameVars.Add(new Variable(varName, expr, unit));
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

    /// <summary>
    /// Lifts a unit written inline in a VAR row's EXPRESSION ("2 GHz") into the unit the row would
    /// have carried had it been typed in the unit column. Returns the expression unchanged with a
    /// null unit whenever there is nothing to lift.
    ///
    /// <para>VAR rows only. A MEAS row with the same mistake is not silent — <c>MeasurementEvaluator</c>
    /// collects the failure and names the measurement and the text — so there is nothing hidden there
    /// to fix, and a measurement expression is far likelier to end in a letter token legitimately.</para>
    ///
    /// <para><b>The rule itself lives in <see cref="Units.LiftInlineUnit"/> since AUT-8 R-aut8-6</b>,
    /// because the <c>.cnl</c> reader applies the same one and the two had drifted: this path lifted
    /// identity units (V, A, W, dBm) and the reader's did not, so <c>VDS = 48 V</c> meant a variable
    /// in a schematic and a parse error in a netlist. See that method for the parse-verification the
    /// wide unit table depends on.</para>
    /// </summary>
    internal static (string Expression, string? Unit) LiftInlineUnit(string expression)
        => Units.LiftInlineUnit(expression);

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
    /// <summary>
    /// The workspace whose kits this extraction resolves against (MW1 R-mw1-5) — the one the
    /// schematic itself belongs to, found by the same ancestor-<c>.cws</c> walk-up technology uses.
    /// Null for a scratch schematic that has never been saved, which is an ordinary state here.
    /// </summary>
    private static string? WorkspaceOf(SchematicEditModel model)
        => WorkspaceRootFinder.WorkspaceDirOf(model.SchematicDirectory) ?? model.HostWorkspaceRoot;

    /// <summary>
    /// Says which cell a LOOSE schematic's instance runs with whenever the workspace it is run from
    /// was asked (<see cref="ExternalCellRef.FindInHostWorkspace"/>). Silent for a schematic inside a
    /// workspace, with no host, and when the stored path and the host name the same cell.
    ///
    /// <para><b>Two cells, one name: the stored path wins, and the run says so.</b> The schematic's
    /// own path is what the file says; the host's cell of the same name is a different cell, and a
    /// result computed with one of them looks exactly like a result computed with the other. Two
    /// host cells of that name and no path that resolves is an instance with NO cell, and the note
    /// names both rather than leaving only "no primary schematic". When the stored path names
    /// nothing and the host has the one cell, that cell is used and the run says where it came
    /// from — information, because nothing else was on offer.</para>
    /// </summary>
    private static void NoteHostWorkspaceCell(SchematicEditModel model, EditableComponent comp, List<HostCellChoice> choices)
    {
        if (ExternalCellRef.FindInHostWorkspace(comp.CellRef, model.SchematicDirectory, model.HostWorkspaceRoot)
                is not { } hosted)
            return;

        string name   = Path.GetFileName(comp.CellRef!.Replace('\\', '/').TrimEnd('/'));
        string host   = Path.GetFileName(WorkspaceRootFinder.Normalize(model.HostWorkspaceRoot));
        string? stored = ExternalCellRef.ResolveCellDir(comp.CellRef, model.SchematicDirectory, out _, out bool exists);

        if (exists && stored is not null)
        {
            var others = hosted.Candidates
                .Where(c => !string.Equals(Path.GetFullPath(c), Path.GetFullPath(stored), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count == 0) return;
            choices.Add(new HostCellChoice(comp.InstanceName,
                $"Cell instance '{comp.InstanceName}': found two cells named '{name}' — " +
                $"{stored} (the schematic's stored path) and {string.Join(", ", others)} " +
                $"(workspace '{host}'). Used {stored}.", IsWarning: true));
            return;
        }

        choices.Add(hosted.CellDir is { } used
            ? new HostCellChoice(comp.InstanceName,
                $"Cell instance '{comp.InstanceName}': '{comp.CellRef}' is not at the schematic's stored path; " +
                $"used {used} from workspace '{host}'.", IsWarning: false)
            : new HostCellChoice(comp.InstanceName,
                $"Cell instance '{comp.InstanceName}': '{comp.CellRef}' is not at the schematic's " +
                $"stored path, and workspace '{host}' has {hosted.Candidates.Count} cells named " +
                $"'{name}' ({string.Join(", ", hosted.Candidates)}). Neither was used.", IsWarning: true));
    }

    private static CcellFile? CcellOf(SchematicEditModel model, EditableComponent comp)
    {
        if (comp.CellRef is null) return null;
        if (PdkKitRegistry.IsKitRef(comp.CellRef))
            return PdkKitRegistry.Find(comp.CellRef, WorkspaceOf(model))?.Ccell;

        return model.SchematicDirectory is null
            ? null
            : CellSymbolResolver.ResolveCcell(comp.CellRef, model.SchematicDirectory, model.HostWorkspaceRoot);
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

        SayIfTheKitHasNoModelForThisPart(comp, ccell, WorkspaceOf(model), conflicts);

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

    /// <summary>
    /// Says so when a kit whose devices are compiled Verilog-A offers this part no model at all.
    ///
    /// <para><b>Why the failure needs explaining rather than merely happening.</b> On a kit of this
    /// shape a part's device type is never the part's own name — it is a module named by a
    /// <c>.model</c> card inside the kit's netlist, resolved by <see cref="RouteToCompiledModel"/>.
    /// A part with no netlist definition therefore reaches the provider under its own id, which no
    /// artefact declares, and the failure at Run reads <i>"provider does not expose a device type
    /// 'inductor'. Available: auxmodel"</i> — where <c>auxmodel</c> is simply whichever artefact the
    /// manifest names by default and has nothing to do with the question. Measured:
    /// its inductors are layout PCells with no simulation model anywhere in the netlists, so this is
    /// an ordinary state for a user to be in and a terrible thing to have to work out from that
    /// message.</para>
    ///
    /// <para><b>Reported, and the instance still emitted.</b> Skipping it would leave a run that
    /// produces numbers for a circuit missing a component the user placed, which is the one outcome
    /// worse than failing — so the run still stops at elaboration, and this is what says why.</para>
    /// </summary>
    private static void SayIfTheKitHasNoModelForThisPart(
        EditableComponent comp, CcellFile ccell, string? workspaceRoot, List<string> conflicts)
    {
        var models = PdkKitRegistry.OsdiModelsOf(workspaceRoot, ccell.ExternalProvider);
        if (models.Count == 0) return;   // not a compiled-Verilog-A kit: this rule says nothing

        var declared = models.SelectMany(m => m.TypeIds).ToList();
        if (declared.Contains(ccell.ExternalType!, StringComparer.OrdinalIgnoreCase)) return;

        conflicts.Add(
            $"Instance '{comp.InstanceName}' is '{ccell.ExternalType}' from kit " +
            $"'{ccell.ExternalProvider}'. That kit supplies no circuit for this part and none of the " +
            $"compiled models built for it implements a '{ccell.ExternalType}', so there is nothing " +
            $"to simulate it with. Compiled models available: {string.Join(", ", declared)}. A part " +
            $"like this is usually one the kit draws but does not model — replace it with an ideal " +
            $"component, or with a network you extracted yourself.");
    }

    // ── Netlist-backed cells ──────────────────────────────────────────────────

    /// <summary>
    /// Cells copied out of kit netlists during one extraction, plus the declarations they depend on.
    ///
    /// <para>A netlist is read once however many instances reference it. Beyond cost, that keeps a
    /// single definition of every cell in the run — reading the same file twice would put two Cell
    /// objects with one name into the library and leave which one wins to list order.</para>
    /// </summary>
    /// <summary>One vendor part as it was placed: the drawing, the instance, the part, its glyph, the cell it
    /// was emitted under and its port count.</summary>
    private sealed record PlacedVendorPart(
        SchematicEditModel Model, string Instance, VendorLibraries.VendorPartSource.Resolved Part,
        string Glyph, string Cell, int Ports);

    private sealed class NetlistImports
    {
        /// <summary>
        /// One netlist, read.
        ///
        /// <para><b><see cref="ModelCards"/> is why this is a record rather than the pair it used to
        /// be.</b> A card is the parameter block of whatever device implements its type, and a device
        /// instance in this dialect names the CARD, not the model — so without the cards in hand the
        /// only thing an extraction has to go on is a name that identifies nothing. Empty for every
        /// format but the SPICE dialect, which is the only one that has them.</para>
        /// </summary>
        public sealed record Netlist(
            Library                        Library,
            TestBench                      TestBench,
            IReadOnlyList<SpiceModelCard>  ModelCards);

        private readonly Dictionary<string, Netlist> _byPath =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly IReadOnlyList<PdkCornerSection> _cornerSections;

        public NetlistImports(IReadOnlyList<PdkCornerSection>? cornerSections = null)
            => _cornerSections = cornerSections ?? [];

        /// <summary>
        /// Cell names this extraction has minted from a SPICE file, and which file each came from.
        ///
        /// <para>Lives here because this object is the one thing already threaded through the whole
        /// recursive extraction, and the claim has to be extraction-wide: two SpiceModel instances
        /// pointed at the SAME file must share one set of cells (a subcircuit named twice is one
        /// definition placed twice), while two pointed at DIFFERENT files that happen to define the
        /// same name must be reported rather than silently bound to whichever was read first.</para>
        /// </summary>
        public Dictionary<string, string> SpiceCells { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every vendor library part this extraction emitted, in emission order — what the
        /// frequency-range warning is judged over once the analyses are known.</summary>
        public List<VendorLibraries.VendorLibraryEntry> VendorParts { get; } = [];

        /// <summary>The vendor parts this extraction refused, each as the conflict it added.</summary>
        public List<string> VendorRefusals { get; } = [];

        /// <summary>Each <c>.model</c> card a kit cell's device names and nothing could build, to the conflict
        /// that said why (<see cref="BindNativeDeviceTypes"/>, <see cref="ExtractionResult.UnbuiltDevices"/>).</summary>
        public Dictionary<string, string> UnbuiltCards { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each placed library model's emitted top cell, or why it could not be, keyed on its model folder —
        /// so two instances of one part read its <c>.cnl</c> once (brief vendorlib-9 R-vl9-3b).</summary>
        public Dictionary<string, (string? Top, string? Problem)> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every cell instance this extraction LEFT OUT, at any level, each as the conflict that says why.
        /// <see cref="ExtractionResult.DroppedInstances"/>.</summary>
        public List<string> DroppedInstances { get; } = [];

        /// <summary>Every vendor part this extraction placed, with the drawing it is in — what
        /// <see cref="PartTunablesOf"/> turns into tunables once the whole hierarchy is known.</summary>
        public List<PlacedVendorPart> PlacedVendorParts { get; } = [];

        /// <summary>The library name each sub-cell DRAWING was extracted under, so a part placed inside one is
        /// keyed <c>Cell:Instance.Part</c> as the catalog keys every other value of that cell.</summary>
        public Dictionary<SchematicEditModel, string> ModelCellNames { get; } = new(ReferenceEqualityComparer.Instance);
        private readonly List<UserFunction> _functions = [];
        private readonly List<Variable>     _variables = [];
        private readonly HashSet<string>    _merged    = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads a netlist, or returns null with a reason. Never throws.</summary>
        public Netlist? Load(string path, out string? problem)
        {
            problem = null;
            if (_byPath.TryGetValue(path, out var hit)) return hit;

            try
            {
                if (!File.Exists(path)) { problem = $"'{path}' does not exist"; return null; }

                Netlist read;
                if (path.EndsWith(".cnl", StringComparison.OrdinalIgnoreCase))
                {
                    var (library, bench) = CnlTechnologyBinding.ReadFile(path);
                    read = new Netlist(library, bench, []);
                }
                else read = LooksLikeSpice(path) ? WithCornerVariants(path, ReadSpiceNetlist(path)) : ReadKitNetlists(path);

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

        /// <summary>The part library read from <paramref name="path"/>, with any selected corner section's variant
        /// definitions in place of its own (<see cref="KitCornerVariants.Apply"/>).</summary>
        private Netlist WithCornerVariants(string path, Netlist read)
        {
            if (_cornerSections.Count == 0) return read;
            var cards = KitCornerVariants.Apply(path, read.Library, read.ModelCards, _cornerSections);
            return ReferenceEquals(cards, read.ModelCards) ? read : read with { ModelCards = cards };
        }

        /// <summary>
        /// Whether this file is in the SPICE dialect rather than the record-based vendor format.
        ///
        /// <para>Decided from CONTENT, never the extension: both formats use <c>.lib</c>/<c>.sp</c>
        /// freely, and handing one to the other's reader produces cells that are silently wrong
        /// rather than a failure. A <c>.subckt</c> or <c>.model</c> at the start of a line is a marker
        /// only this dialect has — the second because a kit's file of nothing but cards is what a
        /// primitive element's part points at (brief-gdf-1b §4).</para>
        /// </summary>
        private static bool LooksLikeSpice(string path)
        {
            try
            {
                if (new FileInfo(path).Length > 32L * 1024 * 1024) return false;
                foreach (var line in File.ReadLines(path))
                {
                    var head = line.TrimStart();
                    if (head.StartsWith(".subckt", StringComparison.OrdinalIgnoreCase) ||
                        head.StartsWith(".model",  StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Reads a SPICE-dialect netlist into the same shape the other readers return.
        ///
        /// <para>The reader follows this dialect's own <c>.include</c>, so the model cards and shared
        /// libraries a subcircuit depends on come with it — there is no sibling-file sweep to do here,
        /// unlike the record format, which states no inclusions.</para>
        ///
        /// <para><b>The file's own top-level bindings become testbench globals, and are then OUTRANKED
        /// by whatever is already there.</b> That ordering is the whole point: a kit's corner file
        /// binds its process constants at top level too, so a corner the user selected must win over
        /// the nominal value the model library happens to declare beside it. `CollectDeclarations`
        /// keeps the first definition of a name, and the corner's is merged first.</para>
        /// </summary>
        /// <para><b>The model cards are carried out, not dropped.</b> This dialect instantiates a
        /// compiled device by naming a <c>.model</c> card — <c>Nhv_nmos d g s b
        /// hv_nmos_card</c> — so the instance's reference is the CARD's name, and the card is
        /// the only thing that says which model implements it and with what parameters. Reading them
        /// and then throwing them away leaves an instance pointing at a name nothing can resolve.</para>
        private static Netlist ReadSpiceNetlist(string path)
        {
            var read  = SpiceNetlistReader.ReadFile(path);
            var bench = new TestBench("kit");

            foreach (var v in read.Variables)     bench.GlobalVariables.Add(v);
            foreach (var f in read.Functions)     bench.Functions.Add(f);

            return new Netlist(read.Library, bench, read.ModelCards);
        }

        /// <summary>
        /// Reads a kit's own netlist — and every netlist beside it. A kit splits one library across
        /// several files: the file defining a part instantiates cells declared in another, and its
        /// process constants live in a third. Reading only the named file gives a definition whose
        /// own contents do not resolve.
        /// </summary>
        private static Netlist ReadKitNetlists(string path)
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

            return new Netlist(library, bench, []);
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
                                  $"imported kit netlist; the one declared in this design is used.");
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
    /// <para><b>A reference that names a <c>.model</c> CARD is a fourth kind, and it is the one this
    /// classification cannot see on its own.</b> In the SPICE dialect a compiled device is written
    /// <c>Nhv_nmos d g s b hv_nmos_card</c> — the last word is a card, not a model, and no
    /// provider has ever heard of it. The card is what says which MODULE implements it
    /// (<c>mdla_va</c>) and with what parameters, so a card-backed instance is routed to the compiled
    /// artefact declaring that module instead. See <see cref="RouteToCompiledModel"/>.</para>
    ///
    /// <para>Idempotent, because the netlist read is cached and the same <see cref="Cell"/> objects
    /// are shared by every instance of the part.</para>
    /// </summary>
    private static void BindNativeDeviceTypes(
        Library kitLibrary, string provider, EditableComponent comp,
        IReadOnlyList<SpiceModelCard> modelCards, IReadOnlyList<OsdiModel> osdiModels,
        IReadOnlyDictionary<string, string> vaSources, Dictionary<string, string> unbuilt, List<string> conflicts)
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

                var card = modelCards.FirstOrDefault(
                    c => c.Name.Equals(inst.Reference, StringComparison.OrdinalIgnoreCase));

                int said = conflicts.Count;
                var routed = card is null
                    ? AsExtDevice(inst, BindByTypeName(inst, providerRef))
                    : RouteCardDevice(inst, card, provider, modelLibrary, osdiModels, vaSources, conflicts);

                // Reported, and left as written. The sentence is kept by card the first time it is said
                // (a repeat is not said again), for a run that reaches this device to refuse with it.
                if (routed is null)
                {
                    if (card is not null && conflicts.Count > said) unbuilt.TryAdd(card.Name, conflicts[^1]);
                    continue;
                }
                cell.Instances[i] = routed;
            }
        }
    }

    private static Instance? AsExtDevice(Instance inst, List<ParameterAssignment>? overrides)
        => overrides is null ? null
         : new Instance(inst.InstanceName, ExtDeviceReference, inst.NetBindings, overrides)
           {
               RefNetBinding = inst.RefNetBinding,
           };

    /// <summary>
    /// A device naming a <c>.model</c> card, in the order brief-gdf-1b §3 (D4) sets: a COMPILED model
    /// implementing the card's type first, because the kit or the user compiled it and that is the
    /// kit's own statement of the model — the module the card names, else one the kit's settings
    /// declare as the stand-in for the built-in the card names (<see cref="DeviceWorkerStandIn"/>);
    /// then circuitRF's own physics, when the card translates onto a built-in; then a refusal carrying
    /// the translation's own sentence and the stand-in that would answer it.
    ///
    /// <para><b>Why the built-in is second and not absent.</b> A diode, bipolar or sheet-resistor card
    /// is a device the kit's own simulator builds in; the kit ships no source for it, so sending the
    /// user to compile one sends them looking for a file that does not exist. The compile advice is
    /// given only for a type the kit SHIPS A SOURCE for (brief-gdf-6 R-gdf6-3) — the import read which
    /// modules its <c>.va</c> files declare — and a type with no compiled model, no source and no
    /// built-in is said to be exactly that.</para>
    /// </summary>
    private static Instance? RouteCardDevice(
        Instance inst, SpiceModelCard card, string provider, string? modelLibrary,
        IReadOnlyList<OsdiModel> osdiModels, IReadOnlyDictionary<string, string> vaSources,
        List<string> conflicts)
    {
        if (OsdiModelDiscovery.ImplementorOf(osdiModels, card.ModelType) is { } named)
            return RouteToCompiledModel(inst, card, provider, modelLibrary, named, null, conflicts);

        if (StandInFor(osdiModels, card) is ({ } implementor, { } standIn))
            return RouteToCompiledModel(inst, card, provider, modelLibrary, implementor, standIn, conflicts);

        if (!SpiceModelCardTranslation.IsSupportedType(card.ModelType))
        {
            conflicts.Add(vaSources.TryGetValue(card.ModelType, out string? source)
                ? $"'{inst.InstanceName}' is a '{card.ModelType}' model (from the card " +
                  $"'{card.Name}'), and no compiled model implementing it was found for kit " +
                  $"'{provider}'. The kit ships its Verilog-A source, '{source}'; compile it with a " +
                  $"Verilog-A compiler, then add the folder holding the resulting '.osdi' file in " +
                  $"File ▸ Manage Libraries."
                : $"'{inst.InstanceName}' is a '{card.ModelType}' model (from the card " +
                  $"'{card.Name}'): there is no compiled model, no source in the kit and no built-in " +
                  $"for '{card.ModelType}' in kit '{provider}', so nothing can simulate it. If it is a " +
                  $"model the kit's own simulator builds in, declare a compiled one in the kit's " +
                  $"settings to stand in for it: {StandInHint(card)}.");
            return null;
        }

        var translation = SpiceModelCardTranslation.Translate(card);
        string? refusal = translation.Binding is null
            ? translation.Refusal
            : BuiltInRefusal(inst, translation);
        if (refusal is not null)
        {
            // Once, however many placed parts reach this kit cell: the cell is shared, so the same
            // instance is met again for every part built from the same library.
            string said = $"'{inst.InstanceName}' names the card '{card.Name}': {refusal} No compiled " +
                          $"model implements or stands in for '{card.ModelType}' in kit '{provider}' either; " +
                          $"to run it on one, declare it in the kit's settings: {StandInHint(card)}.";
            if (!conflicts.Contains(said)) conflicts.Add(said);
            return null;
        }

        return BuiltInFromCard(inst, translation, conflicts);
    }

    /// <summary>
    /// Why a card that DOES translate still cannot stand in for this instance, or null. Each is a
    /// way the built-in would simulate a different circuit from the one the kit wrote.
    /// </summary>
    private static string? BuiltInRefusal(Instance inst, ModelCardTranslation translation)
    {
        var binding = translation.Binding!;

        if (ModelCardCellBuilder.SymbolFor(binding.EngineReference) is not { } kind)
            return $"circuitRF implements it as '{binding.EngineReference}', which has no fixed terminal list to bind.";

        int terminals = SymbolPortDefs.For(kind).Length;
        if (inst.NetBindings.Count != terminals)
            return $"the instance binds {inst.NetBindings.Count} net(s), and circuitRF's " +
                   $"'{binding.EngineReference}' has {terminals} terminal(s). Nothing is dropped or tied " +
                   "elsewhere to make the counts agree — that would be a different circuit.";

        var (rd, rs) = SpiceModelCardTranslation.MesfetLeadResistance(translation.Card);
        if (binding.EngineReference.Contains("FET_", StringComparison.Ordinal) && (rd is not null || rs is not null))
            return "it states a lead resistance, which circuitRF's MESFET has no parameter for.";

        // Null is a family nobody has listed the DC-moving parameters of: whatever the card leaves
        // behind might change the operating point, and that is not knowable here, so the built-in does
        // not stand in for it (brief-gdf-1b R-gdf1b-3, "refused rather than approximated").
        if (SpiceModelCardTranslation.DcAffectingUnmapped(translation) is not { } dc)
            return $"circuitRF's built-in '{binding.EngineReference}' has no record of which of this card's " +
                   "parameters change the DC operating point, so what it leaves behind cannot be shown to be " +
                   "harmless; it is refused rather than approximated.";
        if (dc.Count > 0)
            return $"it states {string.Join(", ", dc)}, which circuitRF's built-in " +
                   $"'{binding.EngineReference}' does not carry and which change the DC operating point, " +
                   "so it is refused rather than approximated.";

        var sidewall = SidewallStated(binding, translation.Card);
        foreach (var o in inst.Overrides)
        {
            if (InstanceWordFor(binding, o.Name) is not null) continue;
            if (IsDroppedGeometry(binding, o.Name))
            {
                if (sidewall.Count == 0) continue;
                return $"the instance states a junction perimeter '{o.Name}' and the card a sidewall term " +
                       $"({string.Join(", ", sidewall)}), which circuitRF's diode has no home for.";
            }
            return $"the instance states '{o.Name}', which circuitRF's '{binding.EngineReference}' " +
                   "has nowhere to put.";
        }

        return null;
    }

    /// <summary>
    /// The instance built as circuitRF's own component: the card's parameters, then the instance's
    /// own words over them — the card is the model, the line is this device's geometry.
    ///
    /// <para><b>What the card states and the built-in does not carry is reported ONCE PER CARD</b>,
    /// not per instance: it is a property of the card, and a kit cell placing it ten times would
    /// otherwise say the same thing ten times.</para>
    /// </summary>
    private static Instance BuiltInFromCard(Instance inst, ModelCardTranslation translation, List<string> conflicts)
    {
        var binding   = translation.Binding!;
        var overrides = new List<ParameterAssignment>(binding.Parameters.Count + inst.Overrides.Count);

        // No unit: a card arrives from the reader already in base SI.
        foreach (var p in binding.Parameters) overrides.Add(new ParameterAssignment(p.Name, p.Expression));

        var dropped = new List<string>();
        foreach (var o in inst.Overrides)
        {
            if (InstanceWordFor(binding, o.Name) is not { } name) { dropped.Add(o.Name); continue; }
            overrides.RemoveAll(x => x.Name == name);
            overrides.Add(new ParameterAssignment(name, o.Expression, o.Unit));
        }

        var said = new List<string>();
        if (binding.Unmapped.Count > 0)
            said.Add("not carried: " + string.Join(", ", binding.Unmapped) + ".");
        said.AddRange(binding.Notes);

        string report = $"The card '{translation.Card.Name}' runs on circuitRF's built-in " +
                        $"'{binding.EngineReference}'" + (said.Count > 0 ? "; " + string.Join(" ", said) : ".");
        if (!conflicts.Contains(report)) conflicts.Add(report);

        // Keyed on the card and the word, never the instance, for the same reason as the line above.
        foreach (string word in dropped.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string note = $"Instances of the card '{translation.Card.Name}' state '{word}' (a junction perimeter), " +
                          "which circuitRF's diode has no sidewall term for; dropped.";
            if (!conflicts.Contains(note)) conflicts.Add(note);
        }

        return new Instance(inst.InstanceName, binding.EngineReference, inst.NetBindings, overrides)
        {
            RefNetBinding = inst.RefNetBinding,
        };
    }

    /// <summary>
    /// The built-in's spelling of a word on the instance line, or null. The dialect is
    /// case-insensitive and circuitRF's parameters compare ordinally, so <c>area</c> must become
    /// <c>Area</c>; the multiplier stays lower-case <c>m</c>, which is a different key from a
    /// diode's grading coefficient <c>M</c>.
    /// </summary>
    private static string? InstanceWordFor(ModelCardBinding binding, string word)
    {
        if (word.Equals("m", StringComparison.Ordinal))               return "m";
        if (word.Equals("temp",  StringComparison.OrdinalIgnoreCase)) return "Temp";
        if (word.Equals("dtemp", StringComparison.OrdinalIgnoreCase)) return "Dtemp";
        if (word.Equals("area",  StringComparison.OrdinalIgnoreCase) &&
            binding.EngineReference is "Diode" or "BJT_NPN" or "BJT_PNP")    return "Area";
        return null;
    }

    /// <summary>
    /// A junction perimeter: a diode's sidewall geometry, inert — and so dropped — only while the card
    /// states no sidewall term (<see cref="SidewallStated"/>). With one stated it is the perimeter half
    /// of the junction, and dropping it would lose that half.
    /// </summary>
    private static bool IsDroppedGeometry(ModelCardBinding binding, string word)
        => binding.EngineReference == "Diode" && word.Equals("pj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The sidewall terms a diode card states other than at "not modelled" zero, by the card's own
    /// spelling. The perimeter capacitance is not a DC parameter, so the DC gate passes it, but it is
    /// exactly what an instance's perimeter scales.
    /// </summary>
    private static List<string> SidewallStated(ModelCardBinding binding, SpiceModelCard card)
    {
        var stated = new List<string>();
        if (binding.EngineReference != "Diode") return stated;
        foreach (var (name, value) in card.Parameters)
        {
            if (!SidewallWords.Contains(name)) continue;
            if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v) && v == 0.0)
                continue;
            stated.Add(name);
        }
        return stated;
    }

    private static readonly HashSet<string> SidewallWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CJSW", "CJP", "JSW", "ISW", "JTUNSW",
    };

    /// <summary>
    /// The original rule: the instance's reference IS the device type, and the kit's own provider
    /// serves it. This is the record-format kit's shape, where a netlist names a compiled device
    /// family directly.
    /// </summary>
    private static List<ParameterAssignment> BindByTypeName(Instance inst, string providerRef)
    {
        var overrides = new List<ParameterAssignment>(inst.Overrides.Count + 2)
        {
            new("Provider", providerRef,     null),
            new("Type",     inst.Reference,  null),
        };
        // Every other parameter is the kit's own and is forwarded verbatim, for the provider to
        // match against the names its descriptor declares.
        overrides.AddRange(inst.Overrides.Where(o => o.Name is not ("Provider" or "Type")));
        return overrides;
    }

    /// <summary>
    /// Routes a device that references a <c>.model</c> card to the compiled artefact implementing the
    /// card's type, or standing in for it, as an <c>ExtDevice</c>; or reports why it cannot and returns
    /// null.
    ///
    /// <para><b>Three separate facts have to travel, and each is taken from the one place that knows
    /// it.</b> WHICH FILE comes from the artefact that declares the card's module — carried in the
    /// provider name, which is what the registry keys on, so two models in one design get two workers
    /// rather than the second being silently evaluated by the first's. WHICH MODULE is the artefact's
    /// own spelling of it, never the card's, because the descriptor is matched ordinally downstream.
    /// And the PARAMETERS are the card's, under the instance's — the card is the model's
    /// parameterisation and the instance line is the geometry, so an instance value beats a card
    /// value and the card fills in everything the instance did not state.</para>
    ///
    /// <para><b>The names are respelled the way the artefact declares them</b>, which is not tidying:
    /// the dialect is case-insensitive and a kit writes <c>level</c>, while the module declares
    /// <c>LEVEL</c> and the worker matches with <c>strcmp</c>. Measured directly — <c>level</c> is
    /// refused by name. Only names the module declares a match for are respelled, so a genuine typo is
    /// still refused rather than being turned into something the model happens to accept.</para>
    ///
    /// <para><b>A model-library chosen on the instance still wins</b>, because that is a user saying
    /// "evaluate this one against that file" and it can only mean the file it names.</para>
    ///
    /// <para><b>A stand-in's card was written for a built-in, not for the module</b>, so its names
    /// and its terminal order are the built-in's. The declaration's maps translate both; a card name
    /// left that the module does not declare is refused by name here, all of them at once, rather
    /// than by the worker one Run at a time.</para>
    /// </summary>
    private static Instance? RouteToCompiledModel(
        Instance inst, SpiceModelCard card, string provider, string? modelLibrary,
        OsdiImplementor implementor, DeviceWorkerStandIn? standIn, List<string> conflicts)
    {
        var declared = implementor.Parameters;

        // BLANK IS NOT UNSET HERE. `ModelLibrary` is declared on every kit part and defaults to an
        // empty string, so a null-coalesce keeps the empty string and composes the BARE kit name —
        // the artefact is then never named, the worker loads whatever the manifest's default argument
        // happens to be, and every device reports that the provider does not expose its type while
        // listing a model from some other file.
        string library = string.IsNullOrWhiteSpace(modelLibrary) ? implementor.FilePath : modelLibrary!;

        var overrides = new List<ParameterAssignment>(card.Parameters.Count + inst.Overrides.Count + 2)
        {
            new("Provider", DeviceWorkerProviderResolver.ComposeOverride(provider, library), null),
            new("Type", implementor.TypeId, null),
        };

        var placed  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();

        void Put(string name, string expression, string? unit, bool fromCard)
        {
            // A stand-in's map first: the card is written for the built-in, so its names are the
            // built-in's. A level that picked the stand-in picked the model and is not a value of it.
            // What the card still states that the module does not declare is refused below, never
            // dropped — the stand-in would otherwise compute a different model in silence.
            if (standIn is not null)
            {
                if (standIn.Parameters.TryGetValue(name, out string? to)) { if (to is null) return; name = to; }
                else if (fromCard && standIn.Level is not null && name.Equals("level", StringComparison.OrdinalIgnoreCase)) return;

                if (fromCard && !declared.Contains(name, StringComparer.OrdinalIgnoreCase)) { unknown.Add(name); return; }
            }

            string spelling = OsdiModelDiscovery.AlignParameterCase(declared, name);

            // Never let a kit's own parameter shadow the two selectors above — the device would then
            // be created from a name the netlist did not choose. Aligned first, so a model parameter
            // that merely differs in CASE from a selector (a MOS model's `TYPE` is its channel
            // polarity) keeps its own spelling and is forwarded rather than mistaken for one.
            if (spelling is "Provider" or "Type") return;

            if (placed.TryGetValue(spelling, out int at)) overrides[at] = new(spelling, expression, unit);
            else { placed[spelling] = overrides.Count; overrides.Add(new(spelling, expression, unit)); }
        }

        foreach (var (name, value) in card.Parameters) Put(name, value, null, fromCard: true);
        foreach (var ov in inst.Overrides)             Put(ov.Name, ov.Expression, ov.Unit, fromCard: false);

        if (unknown.Count > 0)
        {
            string said = $"The card '{card.Name}' runs on '{implementor.TypeId}' as its declared stand-in, " +
                          $"and states {string.Join(", ", unknown)}, which that module does not declare. Map " +
                          "each to the module's name for it, or to null to drop it, in the stand-in's \"parameters\".";
            if (!conflicts.Contains(said)) conflicts.Add(said);
            return null;
        }

        // A stand-in's terminal order: module terminal k takes the element line's node Terminals[k].
        var nets = inst.NetBindings;
        if (standIn is { Terminals.Count: > 0 })
        {
            if (standIn.Terminals.Any(k => k < 1 || k > inst.NetBindings.Count))
            {
                string said = $"'{inst.InstanceName}' writes {inst.NetBindings.Count} node(s), and the stand-in for " +
                              $"the card '{card.Name}' maps its terminals from nodes " +
                              $"[{string.Join(", ", standIn.Terminals)}], which the line does not all have.";
                if (!conflicts.Contains(said)) conflicts.Add(said);
                return null;
            }
            nets = [.. standIn.Terminals.Select(k => inst.NetBindings[k - 1])];
        }

        return new Instance(inst.InstanceName, ExtDeviceReference, nets, overrides) { RefNetBinding = inst.RefNetBinding };
    }

    /// <summary>
    /// The artefact and declaration standing in for the built-in this card names, or null. A
    /// declaration stating the card's level beats one for every level of its type.
    /// </summary>
    private static (OsdiImplementor, DeviceWorkerStandIn)? StandInFor(IReadOnlyList<OsdiModel> models, SpiceModelCard card)
    {
        string? level = card.Parameters.FirstOrDefault(kv => kv.Key.Equals("level", StringComparison.OrdinalIgnoreCase)).Value;

        foreach (bool byLevel in new[] { true, false })
            foreach (var model in models)
                foreach (var s in model.StandIns)
                    if (s.Model.Equals(card.ModelType.Trim(), StringComparison.OrdinalIgnoreCase) &&
                        (s.Level is not null) == byLevel && (s.Level is null || SameNumber(s.Level, level)) &&
                        OsdiModelDiscovery.ImplementorOf([model], s.Module) is { } implementor)
                        return (implementor, s);
        return null;

        static bool SameNumber(string a, string? b)
            => b is not null && (a.Equals(b.Trim(), StringComparison.OrdinalIgnoreCase) ||
               double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x) &&
               double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double y) &&
               x == y);
    }

    /// <summary>The stand-in declaration that would route this card, for a refusal to quote.</summary>
    private static string StandInHint(SpiceModelCard card)
    {
        string? level = card.Parameters.FirstOrDefault(kv => kv.Key.Equals("level", StringComparison.OrdinalIgnoreCase)).Value;
        return $"\"standIns\": [{{ \"model\": \"{card.ModelType.Trim()}\"" +
               (level is null ? "" : $", \"level\": \"{level}\"") +
               ", \"file\": \"<compiled .osdi>\", \"module\": \"<its module>\" }]";
    }

    /// <summary>
    /// Emits a <see cref="SymbolKind.SpiceModel"/> — a <c>.model</c> card or a <c>.subckt</c>
    /// definition, run straight from the file it names.
    ///
    /// <para><b>Every failure is reported and none of them falls back.</b> A missing file, an
    /// unreadable one, a definition circuitRF has no model for: each ends in a conflict naming the
    /// instance and skips it. Emitting something else instead — the generic two-port the symbol
    /// happens to be drawn as, say — would answer with a circuit the user never placed.</para>
    ///
    /// <para><b>Pin k binds to port k, and the counts must agree.</b> Both come from the same peek
    /// of the same file, so a disagreement means the symbol on screen is not the definition being
    /// run, and guessing an alignment would wire the design wrong in silence — the same contract
    /// the kit-netlist path states for the same reason.</para>
    /// </summary>
    private static Instance? EmitSpiceModelInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        Library lib,
        NetlistImports imports,
        List<string> conflicts)
        => EmitSpiceModel(comp.InstanceName, comp.Parameters, model, lib, imports, conflicts, (definitionName, ports) =>
        {
            var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                              .OrderBy(d => d.PortIndex)
                              .ToList();

            if (pinDefs.Count != ports)
            {
                conflicts.Add($"Instance '{comp.InstanceName}': the symbol exposes {pinDefs.Count} " +
                              $"pin(s) but '{definitionName}' has {ports}. The symbol is " +
                              "generated from the same file, so this means it was drawn against an " +
                              "older version of it — reopen the schematic; skipped.");
                return null;
            }

            var nets = new List<string>(pinDefs.Count);
            foreach (var def in pinDefs)
            {
                var (px, py) = model.PortWorldOf(comp, def);
                nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }
            return nets;
        });

    /// <summary>
    /// The SpiceModel emission itself — file or vendor part, the definition, its cells, its overrides — for one
    /// instance whose nets <paramref name="netsFor"/> supplies once the definition's port count is known (null when
    /// they cannot be, having said why). A drawn component's nets come from its pins; a <c>.cnl</c> line's from the
    /// line (EMB-5 R-emb5-13, <see cref="ResolveNetlistSpiceModels"/>), so the two are one path.
    /// </summary>
    private static Instance? EmitSpiceModel(
        string instanceName,
        IReadOnlyList<EditableParameter> parameters,
        SchematicEditModel model,
        Library lib,
        NetlistImports imports,
        List<string> conflicts,
        Func<string, int, List<string>?> netsFor)
    {
        string file = (parameters
            .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression ?? "").Trim();

        if (file.Length == 0)
        {
            conflicts.Add($"Instance '{instanceName}': no SPICE model file is set, so there is " +
                          "nothing to simulate. Open its parameters and choose one.");
            return null;
        }

        string wanted = (parameters
            .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.NameParameter)?.Expression ?? "").Trim();

        // A vendor library part reads ONE block — its own — out of a file holding thousands, through
        // the index, rather than parsing the whole file (brief vendorlib-3 R-vl3-2). What follows the
        // read is the file path's own: the same Select, the same Build, the same claimed cells.
        string? path;
        SpiceModelFile peeked;
        VendorLibraries.VendorPartSource.Resolved? vendorPart = null;
        if (VendorLibraries.VendorLibraryRefs.IsVendorRef(file))
        {
            if (VendorLibraries.VendorPartSource.Resolve(file, wanted, model.SchematicDirectory, out string? vendorRefusal)
                is not { } part)
            {
                conflicts.Add($"Instance '{instanceName}': {vendorRefusal}");
                imports.VendorRefusals.Add(conflicts[^1]);
                return null;
            }
            path   = part.FilePath;
            peeked = VendorLibraries.VendorPartSource.Read(part);
            if (peeked.Error is null) imports.VendorParts.Add(part.Entry);
            vendorPart = part;
        }
        else
        {
            path = SpiceModelSymbolProvider.ResolvePath(file, model.SchematicDirectory, out string? unresolved);
            if (path is null && unresolved is not null)
            {
                conflicts.Add($"Instance '{instanceName}': {unresolved}");
                return null;
            }
            if (path is null)
            {
                conflicts.Add($"Instance '{instanceName}': '{file}' is a relative path and this " +
                              "schematic has not been saved, so there is no directory to resolve it " +
                              "against. Save the schematic, or point it at an absolute path.");
                return null;
            }

            string section = (parameters
                .FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.SectionParameter)?.Expression ?? "").Trim();

            peeked = SpiceModelPeek.Read(path, section);
        }

        if (peeked.Error is { } error)
        {
            conflicts.Add($"Instance '{instanceName}': {error}");
            return null;
        }

        var definition = SpiceModelPeek.Select(peeked, wanted);
        if (definition is null)
        {
            conflicts.Add($"Instance '{instanceName}': '{wanted}' is not defined in " +
                          $"{System.IO.Path.GetFileName(path)}. It defines: " +
                          string.Join(", ", peeked.Definitions.Select(d => d.Name)) + ".");
            return null;
        }

        var emission = SpiceModelNetlist.Build(definition, peeked.Scan, path, lib, imports.SpiceCells);
        if (emission.Refusal is { } refusal)
        {
            conflicts.Add($"Instance '{instanceName}' ({definition.Name}): {refusal}");
            return null;
        }

        if (netsFor(definition.Name, emission.Ports.Count) is not { } nets) return null;

        var overrides = new List<ParameterAssignment>(emission.Overrides);

        // The instance's own rows, which for a .subckt are the definition's declared parameters as
        // the dialog populated them. Only what the definition actually declares is forwarded: a
        // subcircuit handed a parameter it never named is an error in the elaborator.
        var declared = emission.CellName is not null && lib.Find(emission.CellName) is { } cellDef
            ? new HashSet<string>(cellDef.Parameters.Select(x => x.Name), StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var p in parameters)
        {
            if (SpiceModelSymbolProvider.IsPanelParameter(p.Name)) continue;
            if (string.IsNullOrWhiteSpace(p.Name))           continue;
            if (string.IsNullOrWhiteSpace(p.Expression))     continue;   // unset — the file's default stands
            if (declared is not null && !declared.Contains(p.Name)) continue;

            var unit = UnitNormalizer.ToEngineUnit(p.Unit);
            overrides.RemoveAll(o => o.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            overrides.Add(new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null));
        }

        // Everything the translation decided that a user could want to see. Reported once per
        // DEFINITION rather than once per instance: a card's dropped parameters are a property of
        // the card, and ten instances of one part would otherwise post the same line ten times.
        foreach (var note in emission.Notes)
            if (!conflicts.Contains(note)) conflicts.Add(note);

        if (vendorPart is not null && emission.CellName is not null)
            imports.PlacedVendorParts.Add(new PlacedVendorPart(model, instanceName, vendorPart,
                (parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.GlyphParameter)?.Expression ?? "").Trim(),
                emission.CellName, emission.Ports.Count));

        return new Instance(instanceName,
                            emission.CellName ?? emission.PrimitiveReference!,
                            nets, overrides);
    }

    /// <summary>The <c>.cnl</c> type token that places a SPICE model or a vendor part — the schematic's
    /// <see cref="SymbolKind.SpiceModel"/> component, written as a netlist line (EMB-5 R-emb5-13).</summary>
    public const string NetlistSpiceModelToken = "SpiceModel";

    /// <summary>
    /// A <c>.cnl</c>'s <c>SpiceModel:C1 p5 0 File="vendorlib://…" Name=…</c> lines (EMB-5 R-emb5-13), each
    /// resolved by <see cref="EmitSpiceModel"/> — the function a drawn SpiceModel component is emitted with, the
    /// same vendor-library lookup through the workspace's <c>.cws</c> above <paramref name="directory"/>, the same
    /// block read, the same cells — and the line replaced by the instance of what it emitted, so the netlist
    /// elaborates as the schematic placing the part does. A line naming a <c>File=</c> path rather than a
    /// library reference is resolved against <paramref name="directory"/> as a schematic's is.
    ///
    /// <para>Returns the top level's placed vendor parts as tunables (<c>C1.Part</c>), built by the schematic's
    /// own <see cref="PartTunablesOf"/>; a part placed inside a cell the netlist defines is emitted but not offered,
    /// since a <c>.cnl</c> cell has no drawing for its key to name. A line that cannot be emitted is a refusal
    /// carrying the schematic's sentence, never a silent skip; what the translation noted reaches
    /// <see cref="TestBench.ReadNotes"/>.</para>
    /// </summary>
    public static IReadOnlyList<VendorLibraries.VendorPartTunable> ResolveNetlistSpiceModels(Library lib, TestBench tb, string? directory)
    {
        bool Placed(Instance i) => i.Reference.Equals(NetlistSpiceModelToken, StringComparison.OrdinalIgnoreCase)
                                   && lib.Find(i.Reference) is null;
        var cells = lib.Cells.ToList();
        if (!tb.Instances.Any(Placed) && !cells.Any(c => c.Instances.Any(Placed))) return [];

        var top     = new SchematicEditModel { SchematicDirectory = directory };
        var imports = new NetlistImports();
        var notes   = new List<string>();

        void Resolve(List<Instance> list, SchematicEditModel model)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                if (!Placed(inst)) continue;
                // The line's parameters as the component's rows: the panel's own (File, Name, Section, Glyph) are
                // text, so a quoted value is its contents; every other row is an expression, as it is in a drawing.
                var rows = inst.Overrides.Select(o => new EditableParameter
                {
                    Name       = o.Name,
                    Expression = SpiceModelSymbolProvider.IsPanelParameter(o.Name) ? Unquoted(o.Expression) : o.Expression,
                    Unit       = o.Unit ?? "",
                }).ToList();
                var said = new List<string>();
                var emitted = EmitSpiceModel(inst.InstanceName, rows, model, lib, imports, said, (definition, ports) =>
                {
                    if (inst.NetBindings.Count == ports) return [.. inst.NetBindings];
                    said.Add($"Instance '{inst.InstanceName}': the line binds {inst.NetBindings.Count} net(s) but " +
                             $"'{definition}' has {ports} port(s).");
                    return null;
                });
                if (emitted is null)
                    throw new InvalidOperationException(said.Count > 0
                        ? string.Join(" ", said)
                        : $"Instance '{inst.InstanceName}': its SPICE model could not be emitted.");
                list[i] = emitted;
                notes.AddRange(said);
            }
        }

        Resolve(tb.Instances, top);
        foreach (var cell in cells) Resolve(cell.Instances, new SchematicEditModel { SchematicDirectory = directory });

        var parts = PartTunablesOf(top, tb.Tuning, imports, lib, notes);
        foreach (var n in notes.Distinct(StringComparer.Ordinal)) tb.ReadNotes.Add(n);
        return parts;

        static string Unquoted(string e)
        {
            string t = e.Trim();
            return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
        }
    }

    /// <summary>
    /// A placed library model (brief vendorlib-9 R-vl9-3b): its <c>.cnl</c> read ONCE per distinct model folder per
    /// extraction, its file references made absolute against the model folder (brief vendorlib-8), its cells renamed
    /// to names nothing else in the run holds, and the instance emitted with its parameter overrides. A library the
    /// workspace does not name, a missing library folder, a part it no longer holds and an unreadable <c>.cnl</c> are
    /// each a refusal naming the part and the library (R-vl9-3c) — never a silent skip. The <c>.csch</c> beside it
    /// is never read (D1).
    /// </summary>
    private static Instance? EmitVendorModelInstance(
        EditableComponent comp,
        SchematicEditModel model,
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        Dictionary<(long, long), string> netNames,
        Dictionary<(string CompId, int PortIndex), (long, long)> detachedKeys,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions,
        Library lib,
        NetlistImports imports,
        CellScope scope,
        List<string> conflicts)
    {
        string? Refuse(string why)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': {why}");
            imports.VendorRefusals.Add(conflicts[^1]);
            return null;
        }

        if (VendorLibraries.ModelLibrary.VendorModelRefs.Resolve(comp.CellRef, model.SchematicDirectory,
                model.HostWorkspaceRoot, out string? refusal) is not { } r)
        { Refuse(refusal!); return null; }

        if (!imports.Models.TryGetValue(r.ModelDir, out var emitted))
            imports.Models[r.ModelDir] = emitted = EmitModelCells(r, lib, scope);
        if (emitted.Problem is { } problem) { Refuse(problem); return null; }

        var top = lib.Find(emitted.Top!)!;
        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions).OrderBy(d => d.PortIndex).ToList();
        if (pinDefs.Count != top.Ports.Count)
        {
            Refuse($"the symbol exposes {pinDefs.Count} pin(s) but the model '{r.Entry.Part}' in the vendor library " +
                   $"'{r.Library}' has {top.Ports.Count} ({string.Join(" ", top.Ports)}).");
            return null;
        }
        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        // The instance's rows, the glyph aside: only what the model declares, in the model's own spelling, and a
        // blank row leaves the model's default standing.
        var declared = top.Parameters.Select(p => p.Name).ToList();
        var overrides = new List<ParameterAssignment>();
        foreach (var p in comp.Parameters)
        {
            if (p.Name == VendorLibraries.ModelLibrary.VendorModelRefs.GlyphParameter) continue;
            // The Corner row (brief vendorlib-13 R-vl13-2d): a selector, never a parameter of the model. It rides the
            // instance as written and the elaborator applies it, so an explicit value on the same instance still wins.
            if (p.Name == CellCorner.SelectorParameter)
            {
                string chosen = (p.Expression ?? "").Trim();
                if (chosen.Length == 0 || chosen.Equals(CellCorner.Nominal, StringComparison.OrdinalIgnoreCase)) continue;
                if (!top.Corners.Any(c => c.Name.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
                {
                    Refuse($"the corner '{chosen}' is not one the model '{r.Entry.Part}' in the vendor library '{r.Library}' " +
                           $"declares; it offers {string.Join(", ", new[] { CellCorner.Nominal }.Concat(top.Corners.Select(c => c.Name)))}.");
                    return null;
                }
                overrides.Add(new ParameterAssignment(CellCorner.SelectorParameter, chosen));
                continue;
            }
            if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Expression)) continue;
            // Exact spelling first; a case-blind match only when it is the one candidate (Vg beside VG is no match for vg).
            var loose = declared.Where(d => d.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            string? name = declared.FirstOrDefault(d => d == p.Name) ?? (loose.Count == 1 ? loose[0] : null);
            if (name is null) continue;
            var unit = UnitNormalizer.ToEngineUnit(p.Unit);
            // A row that repeats the model's own default is not an override (brief vendorlib-13): written as one it
            // would beat the instance's corner, and a distribution in the default would be re-evaluated in the
            // PARENT's scope — a process draw shared by every instance instead of one per instance.
            var decl = top.Parameters.First(d => d.Name == name);
            if (p.Expression.Trim() == decl.DefaultExpression.Trim()
                && (unit.Length > 0 ? unit : null) == (string.IsNullOrEmpty(decl.Unit) ? null : UnitNormalizer.ToEngineUnit(decl.Unit)))
                continue;
            overrides.Add(new ParameterAssignment(name, p.Expression, unit.Length > 0 ? unit : null));
        }

        imports.VendorParts.Add(r.Entry);
        return new Instance(comp.InstanceName, top.Name, nets, overrides);
    }

    /// <summary>One model folder's cells, added to the run's library under fresh names — or why they could not be.</summary>
    private static (string? Top, string? Problem) EmitModelCells(
        VendorLibraries.ModelLibrary.VendorModelRefs.Resolved r, Library lib, CellScope scope)
    {
        string part = r.Entry.Part;
        var read = VendorLibraries.ModelLibrary.ModelNetlist.Read(r.ModelDir, out string? problem);
        if (read is null)
            return (null, $"the model '{part}' in the vendor library '{r.Library}' cannot run: its netlist {problem}.");
        string topName = r.Entry.Model!.Cell;
        if (read.Find(topName) is null)
            return (null, $"the model '{part}' in the vendor library '{r.Library}' cannot run: its netlist defines no " +
                          $"cell '{topName}'.");

        // Every cell under a name the run does not already hold, chosen before any is added so a call inside one
        // model cell to another is rewritten consistently.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in read.Cells)
            names[cell.Name] = scope.Fresh(Identifier(cell.Name == topName ? part : part + "__" + cell.Name), lib);

        foreach (var cell in read.Cells)
        {
            var copy = new Cell(names[cell.Name]);
            copy.Ports.AddRange(cell.Ports);
            copy.Parameters.AddRange(cell.Parameters);
            copy.Variables.AddRange(cell.Variables);
            // The model's corners travel with it, and its top cell is marked with the part number so a bench corner's
            // part: axis and the held parts of a statistics line find this part's instances in the run's netlist
            // (brief vendorlib-13 R-vl13-2e, R-vl13-1c).
            copy.Corners.AddRange(cell.Corners);
            if (cell.Name == topName) copy.Part = part;
            foreach (var inst in cell.Instances)
            {
                var overrides = inst.Overrides.Select(o =>
                    FileReferences.Rebase(inst.Reference, o.Name, o.Expression, r.ModelDir) is { } rebased
                        ? new ParameterAssignment(o.Name, rebased, o.Unit) : o).ToList();
                copy.Instances.Add(new Instance(inst.InstanceName,
                    names.GetValueOrDefault(inst.Reference, inst.Reference), inst.NetBindings, overrides)
                    { RefNetBinding = inst.RefNetBinding });
            }
            lib.Cells.Add(copy);
        }
        return (names[topName], null);

        static string Identifier(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name) sb.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_');
            if (sb.Length == 0 || char.IsAsciiDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
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

        Cell? definition;
        if (ccell.ExternalElement is { Length: > 0 } element)
        {
            definition = ElementDefinition(comp, ccell, element, read,
                                           GetEffectivePortDefs(model, comp, cellRefResolutions).Count(),
                                           WorkspaceOf(model), imports.UnbuiltCards, conflicts);
            if (definition is null) return null;
            if (lib.Find(definition.Name) is null) lib.Cells.Add(definition);
        }
        else
        {
            string wanted = SubstituteParameters(ccell.ExternalNetlistCell!, comp, ccell);
            definition = read.Library.Find(wanted);
            if (definition is null)
            {
                conflicts.Add($"Instance '{comp.InstanceName}': '{wanted}' is not defined in the kit's " +
                              $"netlist, so this part cannot be built.");
                return null;
            }
        }

        // A kit netlist names its own compiled device types natively — `KITLIB_DEVICE_v1:FET1 …` —
        // and those are neither circuitRF primitives nor cells the kit defines. They are what the
        // kit's worker evaluates: a packaged part is built from the netlist, and the devices INSIDE
        // it still resolve to the provider. Bind them before the cells are copied, so what lands in
        // the library is already expressed in terms every downstream layer understands.
        if (!string.IsNullOrWhiteSpace(ccell.ExternalProvider))
            BindNativeDeviceTypes(read.Library, ccell.ExternalProvider!, comp,
                                  read.ModelCards,
                                  PdkKitRegistry.OsdiModelsOf(WorkspaceOf(model), ccell.ExternalProvider),
                                  PdkKitRegistry.VerilogASourcesOf(WorkspaceOf(model), ccell.ExternalProvider),
                                  imports.UnbuiltCards, conflicts);

        // Copy the whole file's cells, not just the one named: the definition instantiates others
        // beside it, and walking the dependency graph would re-derive what reading the file already
        // told us. Names already present win — a design's own cell is never replaced by a kit's.
        foreach (var cell in read.Library.Cells)
            if (lib.Find(cell.Name) is null) lib.Cells.Add(cell);

        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                          .OrderBy(d => d.PortIndex)
                          .ToList();

        // The symbol and the subcircuit come from the same kit, so their terminal counts agreeing is
        // the whole basis for binding pin k to port k. Disagreement means one of them is not the
        // part it claims to be, and guessing an alignment would wire the design wrong in silence.
        //
        // The one stated exception: terminals the kit's netlist line writes AFTER the drawn pins, by
        // naming a parameter whose value is the net (a body connection, body=sub!). They count only
        // when they make up the difference exactly.
        var extras = ccell.ExternalExtraTerminals ?? [];
        if (definition.Ports.Count != pinDefs.Count &&
            definition.Ports.Count != pinDefs.Count + extras.Count)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the symbol exposes {pinDefs.Count} " +
                          $"pin(s) but '{definition.Name}' defines {definition.Ports.Count}; skipped.");
            return null;
        }

        var nets = new List<string>(definition.Ports.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
        }

        // Each extra terminal is wired to the net its parameter NAMES — the instance's own value, else
        // the kit's default — in the schematic the part is placed in; a net label of that name ties it.
        if (nets.Count < definition.Ports.Count)
            foreach (string terminal in extras)
            {
                string? net = comp.Parameters.FirstOrDefault(p => p.Name.Equals(terminal, StringComparison.Ordinal))
                                  ?.Expression.Trim();
                if (string.IsNullOrEmpty(net) && ccell.ExternalFixedParameters?.TryGetValue(terminal, out var d) == true)
                    net = d.Trim();
                if (string.IsNullOrEmpty(net))
                {
                    conflicts.Add($"Instance '{comp.InstanceName}': its '{terminal}' terminal names no net; skipped.");
                    return null;
                }
                string named = EditableNetLabel.NormalizeName(net);
                nets.Add(named);

                // A net nothing drawn here names is a terminal left floating — the kit's own convention
                // for such a name (a global net) is not circuitRF's, so say so rather than simulate an
                // open body as if it were tied.
                if (named != "0" && !named.Equals("gnd", StringComparison.OrdinalIgnoreCase) &&
                    !netNames.Values.Contains(named, StringComparer.Ordinal))
                {
                    string note = $"Instance '{comp.InstanceName}': its '{terminal}' terminal is wired to the net " +
                                  $"'{named}', which nothing in this schematic names, so that terminal connects to " +
                                  $"nothing else. Place a net label '{named}' where it belongs.";
                    if (!conflicts.Contains(note)) conflicts.Add(note);
                }
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

        // …in the DEFINITION's spelling. The kit's netlist dialect is case-insensitive and circuitRF
        // compares names ordinally, so a symbol stating W, L and MF against a subcircuit declaring w,
        // l and mf had every value it stated dropped by the filter below, silently — the part then
        // simulated at the subcircuit's own defaults (brief-gdf-1). Renamed only when the definition
        // declares no exact match and exactly one by case, the reader's own rule
        // (SpicePassiveModelBinding.AlignSubcircuitParameterCase): a definition declaring both M and
        // m means two different parameters, and nothing is invented.
        for (int k = 0; k < overrides.Count; k++)
        {
            var o = overrides[k];
            if (declared.Contains(o.Name)) continue;
            var byCase = declared.Where(d => d.Equals(o.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCase.Count == 1 && !overrides.Any(x => x.Name == byCase[0]))
                overrides[k] = new ParameterAssignment(byCase[0], o.Expression, o.Unit);
        }
        overrides.RemoveAll(o => !declared.Contains(o.Name));

        return new Instance(comp.InstanceName, definition.Name, nets, overrides);
    }

    /// <summary>
    /// The one-element cell a kit symbol's PRIMITIVE netlist line describes (brief-gdf-1b §4): the
    /// element its template names, on the card its format calls, with the format's own words — so a
    /// diode drawn as <c>@name @pinlist @model area='@w * @l'</c> becomes exactly that line, inside a
    /// cell whose parameters are the symbol's template. The line is READ by the SPICE reader rather
    /// than assembled here, because the words are in that dialect (quoted expressions, its suffixes,
    /// its multiplier) and the reader is the one place that already knows it.
    ///
    /// <para>Then it takes the same road every card-backed device inside a kit cell takes: a resistor
    /// or capacitor card through the passive binding, anything else through
    /// <see cref="BindNativeDeviceTypes"/> — so a primitive and the same element inside a subcircuit
    /// can never be built two different ways.</para>
    /// </summary>
    private static Cell? ElementDefinition(
        EditableComponent comp, CcellFile ccell, string element, NetlistImports.Netlist read,
        int pins, string? workspaceRoot, Dictionary<string, string> unbuilt, List<string> conflicts)
    {
        string cardName = ccell.ExternalNetlistCell!;

        if (element == "M")
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the kit writes this part as a MOS element on the " +
                          $"card '{cardName}'. MOS compact models are outside what circuitRF implements, and the " +
                          "kit ships no source to compile one from, so it cannot be simulated.");
            return null;
        }

        var card = read.ModelCards.FirstOrDefault(c => c.Name.Equals(cardName, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the kit writes this part as a '{element}' element on " +
                          $"the card '{cardName}', which its model file does not define as read. A card inside " +
                          "a corner section is read only when that section is the corner selected.");
            return null;
        }

        var ports = Enumerable.Range(1, pins).Select(k => $"p{k}").ToList();

        var textual = ccell.ExternalFixedParameters?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
        var header  = ccell.Parameters
            .Where(p => !textual.Contains(p.Name) && !string.IsNullOrWhiteSpace(p.DefaultExpression))
            .Select(p => $"{p.Name}={Quoted(p.DefaultExpression.Trim())}");

        string words = System.Text.RegularExpressions.Regex.Replace(
            ccell.ExternalElementArguments ?? "", @"@(\w+)", "$1");

        // The name carries EVERYTHING the cell is built from, hashed: the caller keeps the first cell of
        // a name it meets, so a name made of the part id alone let a second kit's part of the same id —
        // or the same part bound to another model library — run on the first one's card, silently.
        string modelLibrary = comp.Parameters
            .FirstOrDefault(p => p.Name.Equals(PdkPartInstaller.ModelLibraryParameter, StringComparison.Ordinal))
            ?.Expression.Trim() ?? "";
        string body = $" {string.Join(" ", ports)} {string.Join(" ", header)}\n" +
                      $"{element}1 {string.Join(" ", ports)} {card.Name} {words}\n";
        string identity = string.Join("\n", ccell.ExternalProvider ?? "", ccell.ExternalNetlistPath ?? "",
                                      modelLibrary, body,
                                      string.Join(",", card.Parameters.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                                                                      .Select(kv => $"{kv.Key}={kv.Value}")));
        string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(identity)))[..10].ToLowerInvariant();
        string name = "kitelem_" + new string([.. (ccell.ExternalType ?? cardName)
                          .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_')]) + "_" + digest;
        string text = $".subckt {name}{body}.ends {name}\n";

        var built = SpiceNetlistReader.Read(text, fileLabel: name);
        var cell  = built.Library.Find(name);
        if (cell is null || cell.Instances.Count != 1 || built.IncompleteCells.Count > 0)
        {
            conflicts.Add($"Instance '{comp.InstanceName}': the kit's netlist line for this part could not be " +
                          $"read ({string.Join("; ", built.Notes.Select(n => n.Message))}).");
            return null;
        }

        SpicePassiveModelBinding.Bind(built.Library, read.ModelCards,
                                      note => conflicts.Add($"Instance '{comp.InstanceName}': {note}"),
                                      _ => { });
        if (!string.IsNullOrWhiteSpace(ccell.ExternalProvider))
            BindNativeDeviceTypes(built.Library, ccell.ExternalProvider!, comp, read.ModelCards,
                                  PdkKitRegistry.OsdiModelsOf(workspaceRoot, ccell.ExternalProvider),
                                  PdkKitRegistry.VerilogASourcesOf(workspaceRoot, ccell.ExternalProvider),
                                  unbuilt, conflicts);
        return cell;

        static string Quoted(string v) => v.Any(char.IsWhiteSpace) ? $"'{v}'" : v;
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

    /// <summary>
    /// Cell identity for the duration of one extraction: which cells are currently being built (the
    /// cycle guard), and what each distinct cell is CALLED in the elaborated library.
    ///
    /// <para><b>Keyed by the cell's absolute folder, never by its leaf name.</b> Two workspaces that
    /// reference each other (MW2) routinely both hold a cell called <c>Amp</c>, and both defects a
    /// name-keyed scope produced were silent: <c>Library.Find</c> handed the second <c>Amp</c> the
    /// FIRST one's contents, so a design elaborated with a circuit nobody drew; and the cycle guard
    /// reported the pair as a cell instantiating itself and skipped the instance outright. This is
    /// the same class as the last-path-segment defect MW2 fixed in
    /// <c>CellUsageScanner.RewriteCellReferences</c>, arriving in the elaborator instead.</para>
    ///
    /// <para>The library itself stays name-keyed — <c>Instance</c> names its cell by string, and
    /// that is the netlist's own contract, which <c>CnlWriter</c> also writes — so a second distinct
    /// cell whose leaf name is taken is given a suffixed one. <b>That is not MW3's R-mw3-9</b>, which
    /// forbids auto-suffixing a CELL FOLDER on a cross-workspace copy: nothing here creates a file,
    /// renames anything on disk, or appears in the Project Tree. It is an internal netlist key, and
    /// it is deliberately NOT reported as a conflict — a conflict blocks Match and the CLI, and being
    /// handed a correctly-distinguished cell is not a problem the user has to solve.</para>
    ///
    /// <para>The suffix is <c>_2</c> rather than something a folder name could never hold (<c>:</c>
    /// is barred by <c>NameValidator</c> and would be collision-proof) because the elaborated name
    /// reaches <c>CnlWriter</c>, and a tokenized text format is the wrong place to introduce a
    /// character no reader was written for. Uniqueness comes from the loop instead.</para>
    /// </summary>
    private sealed class CellScope
    {
        /// <summary>The base the run resolves a relative file reference against: the extraction root's
        /// <see cref="SchematicCircuit.ReferenceBaseOfDirectory"/>. A cell whose own owning root differs
        /// has its file references rebased (<see cref="RebaseFileReferences"/>).</summary>
        public string? RunBase { get; init; }

        /// <summary>Cell KEYS on the path from the extraction root to here.</summary>
        public readonly HashSet<string> InProgress = new(StringComparer.OrdinalIgnoreCase);

        /// <summary><see cref="ExtractionResult.HostCellChoices"/>, gathered across every level.</summary>
        public readonly List<HostCellChoice> HostCellChoices = [];

        private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        /// <summary>
        /// The library name for the cell at <paramref name="key"/> — its own leaf name where that is
        /// free, else the first free <c>name_2</c>, <c>name_3</c>… Stable within one extraction, and
        /// deterministic across runs of the same design, because it is assigned in the order the
        /// components are visited and that order is the file's own.
        ///
        /// <para><c>_taken</c> is not redundant with <c>lib.Find</c>: a cell is registered here when
        /// its name is CHOSEN and added to the library only when its recursive build FINISHES, so
        /// during that window the library cannot yet answer for it.</para>
        /// </summary>
        public string NameFor(string key, string leafName, Library lib)
        {
            if (_names.TryGetValue(key, out var known)) return known;

            string name = leafName;
            for (int n = 2; lib.Find(name) is not null || _taken.Contains(name); n++)
                name = $"{leafName}_{n}";

            _names[key] = name;
            _taken.Add(name);
            return name;
        }

        /// <summary>A library name no cell of this extraction holds or has chosen — <paramref name="leafName"/>, else
        /// the first free <c>_2</c>, <c>_3</c>… — reserved at once. For cells that come from a library model's netlist,
        /// which have no folder key of their own (brief vendorlib-9 R-vl9-3b).</summary>
        public string Fresh(string leafName, Library lib)
        {
            string name = leafName;
            for (int n = 2; lib.Find(name) is not null || _taken.Contains(name); n++)
                name = $"{leafName}_{n}";
            _taken.Add(name);
            return name;
        }

        /// <summary>Library name → cell key, for every cell named during this extraction.</summary>
        public Dictionary<string, string> LibraryNameToKey()
            => _names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
    }

    /// <summary>A placed cell left out of the netlist, recorded with the sentence that says why
    /// (<see cref="ExtractionResult.DroppedInstances"/>).</summary>
    private static void NoteDropped(EditableComponent comp, int conflictsBefore, List<string> conflicts, NetlistImports imports)
        => imports.DroppedInstances.Add(conflicts.Count > conflictsBefore
            ? conflicts[^1]
            : $"Instance '{comp.InstanceName}' (cell '{comp.CellRef}') could not be extracted; skipped.");

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
        CellScope scope,
        List<string> conflicts,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions = null)
    {
        if (cells is null) return null;   // flat caller / no resolver — skip silently (back-compat)

        var res = cells.Resolve(comp, model);
        if (res is null)
        {
            // brief-sdl-10 D1/D2 — a 3D-parametric cell with no schematic is a part all the same: its 3D view's ports are its
            // pins, its parameters its interface, and with no circuit model it is simulated by EM whatever it states.
            if (EmitThreeDOnlyInstance(comp, model, lib, scope, conflicts, cellRefResolutions,
                    (c, k, x, y) => NetForPort(c, k, x, y, uf, QK, netNames, detachedKeys)) is { } emOnly) return emOnly;
            conflicts.Add($"Cell instance '{comp.InstanceName}' (cell '{comp.CellRef}') has no " +
                          $"primary schematic; skipped.");
            return null;
        }

        // Identity is the cell FOLDER; the leaf name is only what it is CALLED. See CellScope.
        var cellKey  = res.Key;
        var cellName = scope.NameFor(cellKey, res.CellName, lib);
        imports.ModelCellNames.TryAdd(res.Schematic, cellName);

        // Cycle guard: a cell currently being extracted up the stack instantiates itself. Asked of
        // the KEY, so A/Amp instancing B/Amp is not mistaken for a self-instantiation — and so a
        // genuine A/Amp -> B/Buf -> A/Amp loop across two workspaces still is one.
        if (scope.InProgress.Contains(cellKey))
        {
            conflicts.Add($"Cell '{cellName}' instantiates itself (cycle); " +
                          $"instance '{comp.InstanceName}' skipped.");
            return null;
        }

        // Build the cell once (dedupe by key); children are added before parents → leaf-first lib.
        if (lib.Find(cellName) is null) BuildCellDefinition(res, cellKey, cellName, cells, lib, imports, scope, conflicts);

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
            // Say what to do about it: a symbol pin connects to the cell's Pin of the SAME number, so the
            // cell needs a Pin for every pin its symbol draws — a ground pin included (field report: a
            // three-pin amplifier symbol over a two-Pin S-parameter cell ran as an open).
            conflicts.Add($"Cell '{cellName}' instance '{comp.InstanceName}': its symbol has " +
                          $"{pinDefs.Count} pin(s) but the cell's schematic places {cellDef.Ports.Count} Pin(s). " +
                          $"Each symbol pin connects to the Pin whose Num is its number, so the cell needs " +
                          $"a Pin for every one, Num 1 to {pinDefs.Count}; skipped.");
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
            // brief-sdl-6 D7: a row left blank is no override — the cell's own value stands. Every blank row is dropped, not
            // only one naming a current cell VAR: the Parameter dialog tops an instance up with blank rows (a VAR per cell
            // VAR, an EmSetup to pick), a VAR renamed in the cell leaves its old blank row behind, and a blank value written
            // to a .cnl as `Key=` makes the reader take the NEXT parameter as its value, losing that one.
            .Where(p => !string.IsNullOrWhiteSpace(p.Expression))
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();

        // brief-sdl-6 R-sdl6-4: an instance of an EM-capable cell names the cell's folder (EmCell=), which is what lets a
        // run simulate it by EM — chosen on the instance (Model=EM) or by an analysis's model=em. An instance set to EM
        // whose cell is not capable is said, and runs on its circuit model rather than failing on a key the cell lacks.
        if (EmCellOf(res) is { } emCell)
            overrides.Add(new ParameterAssignment(ParametricModelRegistry.CellParameter,
                                                  "\"" + RelativeToRunBase(emCell, scope.RunBase) + "\""));
        else if (overrides.RemoveAll(IsEmKey) > 0
                 && comp.Parameters.Any(p => p.Name == ParametricModelRegistry.ModelParameter
                                             && p.Expression.Trim().Equals(ParametricModelRegistry.EmValue, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add($"'{comp.InstanceName}' is set to Model = EM, but cell '{cellName}' cannot be simulated by EM: " +
                          $"{EmCapabilityRefusal(res)} It runs on its circuit model.");

        return new Instance(comp.InstanceName, cellName, nets, overrides);
    }

    /// <summary>
    /// brief-sdl-10 D1/D2 — the instance of a 3D-parametric cell that has no schematic: a cell of its ports (the 3D view's,
    /// or the <c>.ccell</c>'s <c>NumPorts</c>) and its declared parameters, with no contents; the instance names the cell's
    /// folder (<c>EmCell=</c>) and is set to EM, because there is no circuit model to run it on. Null when the cell is not one.
    /// </summary>
    private static Instance? EmitThreeDOnlyInstance(EditableComponent comp, SchematicEditModel model, Library lib, CellScope scope,
                                                    List<string> conflicts, Dictionary<string, CellSymbolResolution>? cellRefResolutions,
                                                    Func<EditableComponent, int, double, double, string> netFor)
    {
        if (comp.CellRef is not { Length: > 0 } cellRef || PdkKitRegistry.IsKitRef(cellRef)) return null;
        string? dir;
        try { dir = ExternalCellRef.ResolveCellDir(cellRef, model.SchematicDirectory); }
        catch (Exception) { return null; }
        if (dir is null || !Directory.Exists(dir)) return null;
        if (EmCapability.Resolve(dir, null, null, out _, out bool several) is not { ThreeDOnly: true } target)
        {
            if (!several) return null;
            target = EmCapability.SetupsFor(dir).FirstOrDefault() is { } first ? EmCapability.Resolve(dir, first, null, out _) : null;
            if (target is not { ThreeDOnly: true }) return null;
        }

        int ports;
        try
        {
            string ccell = Path.Combine(dir, CellFolder.CcellFileName);
            int declared = File.Exists(ccell) ? CellPersistence.LoadFromFile(ccell).NumPorts : 0;
            ports = declared > 0 ? declared : CircuitRF.Design.ThreeD.C3dPersistence.LoadFromFile(target.C3dPath!).Ports.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or InvalidDataException or ArgumentException or InvalidOperationException) { return null; }

        string key = Path.GetFullPath(dir);
        string cellName = scope.NameFor(key, Path.GetFileName(Path.TrimEndingDirectorySeparator(key)), lib);
        if (lib.Find(cellName) is null)
        {
            var cell = new Cell(cellName);
            for (int k = 1; k <= ports; k++) cell.Ports.Add("p" + k.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var p in DiskCellResolver.ParametersOf(dir)) cell.Parameters.Add(p);
            lib.Cells.Add(cell);
        }
        var cellDef = lib.Find(cellName)!;

        var pinDefs = GetEffectivePortDefs(model, comp, cellRefResolutions).OrderBy(d => d.PortIndex).ToList();
        if (pinDefs.Count != cellDef.Ports.Count)
        {
            conflicts.Add($"Cell '{cellName}' instance '{comp.InstanceName}': its symbol has {pinDefs.Count} pin(s) but its 3D view " +
                          $"has {cellDef.Ports.Count} port(s); skipped.");
            return null;
        }
        var nets = new List<string>(pinDefs.Count);
        foreach (var def in pinDefs)
        {
            var (px, py) = model.PortWorldOf(comp, def);
            nets.Add(netFor(comp, def.PortIndex, px, py));
        }

        var overrides = comp.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Expression) && p.Name is not (ParametricModelRegistry.ModelParameter
                        or ParametricModelRegistry.SetupParameter or ParametricModelRegistry.CellParameter))
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
            })
            .ToList();
        if (comp.Parameters.Any(p => p.Name == ParametricModelRegistry.ModelParameter
                                     && p.Expression.Trim().Equals(ParametricModelRegistry.CircuitValue, StringComparison.OrdinalIgnoreCase)))
            conflicts.Add($"'{comp.InstanceName}' is set to Model = Circuit, but cell '{cellName}' has no circuit model (no schematic): " +
                          "it is simulated by EM.");
        overrides.Add(new ParameterAssignment(ParametricModelRegistry.CellParameter, "\"" + RelativeToRunBase(dir, scope.RunBase) + "\""));
        overrides.Add(new ParameterAssignment(ParametricModelRegistry.ModelParameter, ParametricModelRegistry.EmValue));
        if (comp.Parameters.FirstOrDefault(p => p.Name == ParametricModelRegistry.SetupParameter) is { Expression.Length: > 0 } setup)
            overrides.Add(new ParameterAssignment(ParametricModelRegistry.SetupParameter, setup.Expression));
        return new Instance(comp.InstanceName, cellName, nets, overrides);
    }

    private static bool IsEmKey(ParameterAssignment p) =>
        p.Name == ParametricModelRegistry.ModelParameter || p.Name == ParametricModelRegistry.SetupParameter;

    /// <summary>The folder of <paramref name="res"/>'s cell when it is EM-capable (brief-sdl-6 §1), else null.</summary>
    private static string? EmCellOf(CellResolution res) =>
        res.CellKey is { } dir && Directory.Exists(dir) && EmCapability.IsCapable(dir) ? dir : null;

    private static string EmCapabilityRefusal(CellResolution res)
    {
        if (res.CellKey is not { } dir || !Directory.Exists(dir)) return "it is not a cell folder on disk.";
        EmCapability.Resolve(dir, null, null, out var why);
        return why ?? "";
    }

    /// <summary>A cell folder as a netlist states it: relative to the run's base when inside it (so the netlist moves with
    /// the workspace), else absolute — the rule every other file reference in the netlist follows.</summary>
    private static string RelativeToRunBase(string dir, string? runBase)
    {
        if (runBase is not { Length: > 0 }) return dir.Replace('\\', '/');
        string rel = Path.GetRelativePath(runBase, dir);
        return (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? dir : rel).Replace('\\', '/');
    }

    /// <summary>
    /// brief-sdl-6 D8: an EM-capable cell drawn with Ports and no Pins — the schematic a designer simulates by EM of its
    /// own layout — takes its Ports as its interface, in Num order, so EM ports, symbol pins and the parent's connections
    /// agree by number with no step from the user. The Ports themselves leave the cell: placed in a parent, it is the
    /// parent that drives it.
    /// </summary>
    private static (List<Instance> Instances, List<string> Ports) PortsAsPins(List<Instance> instances)
    {
        var ports = instances
            .Where(i => (i.Reference.Equals("Term", StringComparison.OrdinalIgnoreCase) || i.Reference.Equals("Port", StringComparison.OrdinalIgnoreCase))
                        && i.NetBindings.Count > 0)
            .Select(i => (Inst: i, Num: int.TryParse(i.Overrides.FirstOrDefault(o => o.Name == "Num")?.Expression?.Trim(),
                                                     System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                                                     out int n) ? n : int.MaxValue))
            .OrderBy(t => t.Num)
            .ToList();
        var kept = instances.Where(i => !ports.Any(t => ReferenceEquals(t.Inst, i))).ToList();
        return (kept, ports.Select(t => t.Inst.NetBindings[0]).ToList());
    }

    /// <summary>The one build of a cell's definition from its schematic — what an instance of it descends into, and
    /// what <see cref="ExtractCell"/> extracts a cell as on its own.</summary>
    private static void BuildCellDefinition(CellResolution res, string cellKey, string cellName, ICellResolver? cells,
                                            Library lib, NetlistImports imports, CellScope scope, List<string> conflicts)
    {
        scope.InProgress.Add(cellKey);
        var (subInstances, subPorts, subVars, subMeas) = ExtractModel(res.Schematic, cells, lib, imports, scope, conflicts);
        if (subPorts.Count == 0 && EmCellOf(res) is not null)
            (subInstances, subPorts) = PortsAsPins(subInstances);
        if (subMeas.Count > 0)
            conflicts.Add($"Cell '{cellName}': MEAS components are ignored inside a cell; measurements attach to the top testbench only.");
        var cell = new Cell(cellName);
        cell.Ports.AddRange(subPorts);
        cell.Instances.AddRange(RebaseFileReferences(subInstances, res.Schematic.SchematicDirectory, scope.RunBase));
        cell.Variables.AddRange(subVars);
        foreach (var p in res.Parameters) cell.Parameters.Add(p);
        if (res.Corners is { } corners) cell.Corners.AddRange(corners);   // brief vendorlib-13 R-vl13-2b
        lib.Cells.Add(cell);
        scope.InProgress.Remove(cellKey);
    }

    /// <summary>
    /// A cell extracted as DEFINE BLOCKS only (brief vendorlib-9 R-vl9-3a): the cell itself, named after its folder,
    /// and every sub-cell and translated SPICE definition it places, leaf-first — exactly the definitions an instance
    /// of the cell would add to a run's library, through the same build. Relative file references stay relative to
    /// the cell's own owning root, which is the base the run is made against here. Null with the reason when the
    /// cell has no primary schematic that reads.
    /// </summary>
    public static (Library Library, string CellName, IReadOnlyList<string> Conflicts)? ExtractCell(
        string cellDir, out string? refusal, ICellResolver? cells = null)
    {
        refusal = null;
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        PrimaryResolution primary;
        try { primary = CellFolder.ResolvePrimary(dir, ViewType.Schematic); }
        catch (Exception ex) { refusal = ex.Message; return null; }
        if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent) || primary.ResolvedName is null)
        {
            refusal = $"'{Path.GetFileName(dir)}' has no primary schematic.";
            return null;
        }
        string csch = Path.Combine(CellFolder.SubFolderPath(dir, ViewType.Schematic), primary.ResolvedName);

        SchematicEditModel schematic;
        try { schematic = SchematicPersistence.LoadFromFile(csch).model; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            refusal = $"{csch} could not be read ({ex.Message}).";
            return null;
        }
        schematic.SchematicDirectory = Path.GetDirectoryName(csch);

        string cellName = Path.GetFileName(dir);
        var res = new CellResolution(cellName, schematic, DiskCellResolver.ParametersOf(dir), dir, CellCorners.Of(dir));
        var lib = new Library("cell");
        var conflicts = new List<string>();
        var scope = new CellScope { RunBase = SchematicCircuit.ReferenceBaseOfDirectory(schematic.SchematicDirectory) };
        string name = scope.NameFor(res.Key, cellName, lib);
        BuildCellDefinition(res, res.Key, name, cells ?? DiskCellResolver.Instance, lib, new NetlistImports(), scope, conflicts);
        return (lib, name, conflicts);
    }

    /// <summary>
    /// A cell's instances with each relative file reference made absolute against the cell's OWN
    /// owning root, when that differs from the run's base (brief vendorlib-8 R-vl8-2c). A cell from a
    /// referenced workspace stores its SnP relative to its workspace root, and a model folder's
    /// schematic relative to the model folder; the elaborator resolves every relative reference
    /// against the one base the run started in, so without this both would read the consuming
    /// workspace's file of the same name, or none.
    ///
    /// <para><b>The elaborator is unchanged</b>: an absolute path passes through
    /// <c>ResolveFileReference</c> untouched, so a <c>.cnl</c> written from such a design runs on its
    /// own. Which parameters are files is <see cref="FileReferences"/>'s answer, the one list the
    /// elaborator's resolution sites are checked against. A cell in the run's own root is returned
    /// as it is, so an ordinary design's netlist keeps its portable relative paths.</para>
    /// </summary>
    private static IEnumerable<Instance> RebaseFileReferences(IEnumerable<Instance> instances, string? cellDirectory, string? runBase)
    {
        string? owning = OwningRoot.Of(cellDirectory);
        if (owning is null || OwningRoot.Same(owning, runBase)) return instances;

        return instances.Select(inst =>
        {
            bool changed = false;
            var overrides = inst.Overrides.Select(o =>
            {
                if (FileReferences.Rebase(inst.Reference, o.Name, o.Expression, owning) is not { } rebased) return o;
                changed = true;
                return new ParameterAssignment(o.Name, rebased, o.Unit);
            }).ToList();
            return changed
                ? new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, overrides) { RefNetBinding = inst.RefNetBinding }
                : inst;
        }).ToList();
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
            var k = FindLabelNetKey(uf, QK, gs, model, lbl);
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
            var k = FindLabelNetKey(uf, QK, gs, model, lbl);
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
    /// <summary>The union-find key of the node <paramref name="lbl"/> names. An anchored label names its OWNER WIRE's
    /// node wherever it is drawn: the anchor is the source of truth for its position, and a label drawn a little off
    /// its wire — dragged clear of a part, or left there by an edit — used to name whatever its drawn point landed on,
    /// or nothing (brief-dfr22-2 R-dfr22-2-4, R-dfr22-2-5). An unanchored label, or one whose wire is gone, names the
    /// node under its point.</summary>
    private static (long, long)? FindLabelNetKey(
        UnionFind uf,
        Func<double, double, (long, long)> QK,
        double gs,
        SchematicEditModel model,
        EditableNetLabel lbl)
    {
        if (lbl.IsAnchored && model.FindWire(lbl.OwnerWireId) is { Points.Count: >= 2 } owner
            && QK(owner.Points[0].X, owner.Points[0].Y) is var ownerKey && uf.Contains(ownerKey))
            return ownerKey;
        double lx = lbl.X, ly = lbl.Y;
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
    /// The union-find key of the wire run passing through (x, y), or null when no wire does — the
    /// segment-scan half of <see cref="FindLabelNetKey"/>, without its "already a key" shortcut.
    ///
    /// <para>The shortcut is what makes it a separate function rather than a call. Every component
    /// pin is seeded into the union-find before this runs, so a probe asking about its OWN pin
    /// position would be answered with its own pin — which is exactly the question it is trying to
    /// avoid asking.</para>
    /// </summary>
    private static (long, long)? FindWireSpanKey(
        SchematicEditModel model, Func<double, double, (long, long)> QK,
        double gs, double x, double y)
    {
        double tol = gs / 2.0;
        foreach (var wire in model.Wires)
        {
            var pts = wire.Points;
            for (int i = 0; i < pts.Count - 1; i++)
                if (SchematicGeometry.PointOnSegment(
                        x, y, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y, tol))
                    return QK(pts[i].X, pts[i].Y);
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="probe"/>'s single terminal reaches anything at all — a wire, or
    /// another component's pin.
    ///
    /// <para>Asked of the union-find CLASS rather than of geometry, so every way two things can
    /// share a net counts the same: a shared vertex, a T-junction, a dotted crossing, a same-name
    /// label pair on two disjoint runs. What it deliberately does not count is another VProbe — two
    /// probes touching each other and nothing else are still reading nothing, and reporting one of
    /// them as connected would be worse than reporting neither.</para>
    /// </summary>
    /// <summary>
    /// True when a pin touches anything: a wire in its net, another component's pin, or a net label placed on it. A
    /// pin detached from the schematic is wired to nothing.
    /// </summary>
    private static bool IsPinWired(
        SchematicEditModel model, EditableComponent comp, (float LocalX, float LocalY, int PortIndex) def,
        UnionFind uf, Func<double, double, (long, long)> QK,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions)
    {
        if (comp.IsPortDetached(def.PortIndex)) return false;
        var (px, py) = model.PortWorldOf(comp, def);
        var key = QK(px, py);
        if (!uf.Contains(key)) return false;
        var root = uf.Find(key);
        bool InNet((long, long) k) => uf.Contains(k) && uf.Find(k) == root;

        if (model.Wires.Any(w => w.Points.Any(pt => InNet(QK(pt.X, pt.Y))))) return true;
        if (model.NetLabels.Any(l => QK(l.X, l.Y) == key)) return true;
        foreach (var other in model.Components)
        {
            if (other.Disable is DisableState.Open) continue;
            foreach (var d in GetEffectivePortDefs(model, other, cellRefResolutions))
            {
                if (ReferenceEquals(other, comp) && d.PortIndex == def.PortIndex) continue;
                if (other.IsPortDetached(d.PortIndex)) continue;
                var (ox, oy) = model.PortWorldOf(other, d);
                if (InNet(QK(ox, oy))) return true;
            }
        }
        return false;
    }

    private static bool IsVProbeConnected(
        SchematicEditModel model, EditableComponent probe,
        UnionFind uf, Func<double, double, (long, long)> QK,
        Dictionary<string, CellSymbolResolution>? cellRefResolutions)
    {
        var defs = GetEffectivePortDefs(model, probe, cellRefResolutions);
        if (defs.Count == 0 || probe.IsPortDetached(defs[0].PortIndex)) return false;

        var (px, py) = model.PortWorldOf(probe, defs[0]);
        var key = QK(px, py);
        if (!uf.Contains(key)) return false;
        var root = uf.Find(key);

        foreach (var wire in model.Wires)
            foreach (var pt in wire.Points)
            {
                var wk = QK(pt.X, pt.Y);
                if (uf.Contains(wk) && uf.Find(wk) == root) return true;
            }

        foreach (var other in model.Components)
        {
            if (ReferenceEquals(other, probe))     continue;
            if (other.Symbol == SymbolKind.VProbe) continue;
            if (other.Disable is DisableState.Open) continue;
            foreach (var d in GetEffectivePortDefs(model, other, cellRefResolutions))
            {
                if (other.IsPortDetached(d.PortIndex)) continue;
                var (ox, oy) = model.PortWorldOf(other, d);
                var ok = QK(ox, oy);
                if (uf.Contains(ok) && uf.Find(ok) == root) return true;
            }
        }
        return false;
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

        // VProbe names — the same statement a net label makes, one priority step BELOW a label.
        //
        // A probe on an UNNAMED net becomes that net's name outright, rather than a second name for
        // it: naming it `n7` as well would put two rows in every result for one point in the
        // circuit, and the auto-name is not a name anybody chose (owner, 2026-09-06). A probe on a
        // net that already carries a user's own label leaves the label standing and is emitted as an
        // instance, which the elaborator turns into an ALIAS — both names, one node.
        //
        // A name already spoken for is refused rather than reused: two roots carrying one name merge
        // into ONE node at elaboration, which is a probe changing the circuit — the one thing it may
        // never do.
        {
            var takenNames = new HashSet<string>(rootToName.Values, StringComparer.Ordinal);
            if (pinNetNameMap is not null)
                foreach (var portName in pinNetNameMap.Values) takenNames.Add(portName);

            foreach (var comp in model.Components)
            {
                if (comp.Symbol != SymbolKind.VProbe) continue;
                if (comp.Disable is DisableState.Open or DisableState.Short) continue;

                var vdefs = GetEffectivePortDefs(model, comp, cellRefResolutions);
                if (vdefs.Count == 0 || comp.IsPortDetached(vdefs[0].PortIndex)) continue;
                var (vpx, vpy) = model.PortWorldOf(comp, vdefs[0]);
                var vkey = QK(vpx, vpy);
                if (!uf.Contains(vkey)) continue;

                var vroot = uf.Find(vkey);
                if (rootToName.ContainsKey(vroot)) continue;   // ground, or a label the user gave

                // A probe touching nothing must not name its OWN floating pin: the emission loop
                // drops it and reports it, and a net name left behind here would put it in the
                // picker with nothing behind it.
                if (!IsVProbeConnected(model, comp, uf, QK, cellRefResolutions)) continue;

                var name = comp.InstanceName;
                if (string.IsNullOrWhiteSpace(name) || !takenNames.Add(name))
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        conflicts.Add($"VProbe {name} has the same name as a net in this design; " +
                                      $"its own net keeps its automatic name.");
                    continue;
                }
                rootToName[vroot] = name;
                labeledNetNames?.Add(name);
            }
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

        // Skip an auto-name a user name has already taken. Two roots sharing one name merge into ONE
        // node at elaboration — silently, and as a different circuit — so `n2` must not be handed out
        // when something on the drawing is already called `n2`.
        var assigned = new HashSet<string>(rootToName.Values, StringComparer.Ordinal);
        int idx = 1;
        foreach (var root in orderedRoots)
        {
            string auto;
            do { auto = $"n{idx++}"; } while (!assigned.Add(auto));
            rootToName[root] = auto;
        }

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
        // A tone source with the indexed V[1]/I[1]/Freq[1] format (NumFreqs present) is the
        // MULTI-tone factory spelling — V_nTone for the voltage source, I_nTone for the current one.
        if (comp.Symbol is SymbolKind.ToneSource or SymbolKind.CurrentToneSource &&
            comp.Parameters.Any(p => p.Name == "NumFreqs"))
            reference = comp.Symbol == SymbolKind.ToneSource ? "V_nTone" : "I_nTone";

        // SnP: filter UI-only params and separate the optional reference-node pin.
        if (comp.IsSnpBox)
        {
            bool hasRefNode = comp.Parameters
                .FirstOrDefault(p => p.Name == "RefNode")?.Expression
                .Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
            int numPorts = comp.PortCount;

            var overrides = comp.Parameters
                .Where(p => p.Name is not "RefNode" and not "PinConfig" and not "Pitch" and not SnpGlyph.SymbolParameter)
                // An EM Field Data block's port count is its record's: the drawn pin count is not part of its line.
                .Where(p => comp.Symbol != SymbolKind.EmFieldData || p.Name != "NumPorts")
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
        // §5 question 2 — when REF is present it *does* appear in NetBindings, and the model ignores
        // the last one. `WBondModel.PortCount` is then 2M+1 and its `TerminalNames` end in "REF", so
        // the elaborator binds 2M+1 nets and the stamp uses 2M of them. The alternative (bind 2M and
        // make PortCount mean something other than the symbol's pin count) would put two different
        // meanings on one number, and both sides would have to remember which.
        //
        // **The pin is optional and OFF by default since 2026-08-16** (owner), matching SnP's
        // `RefNode`. Both sides read the same `RefPin` instance parameter — the symbol generator to
        // decide whether to draw the pin, and `ComponentModelFactory` to decide the model's port
        // count — so the two cannot disagree. That is why `RefPin`, alone among the wBond artwork
        // parameters, is forwarded to the engine rather than filtered out below.
        if (comp.Symbol == SymbolKind.WBond)
        {
            var wbDefs = GetEffectivePortDefs(model, comp, cellRefResolutions)
                             .OrderBy(d => d.PortIndex)
                             .ToList();

            if (wbDefs.Count == 0)
            {
                string payload = comp.Parameters
                    .FirstOrDefault(p => p.Name == WBondEmbedding.DesignParameter)?.Expression ?? "";
                warningsOut?.Add(string.IsNullOrWhiteSpace(payload)
                    ? $"wBond '{comp.InstanceName}' carries no wirebond design, so it has no pins; "
                      + "skipped. Use File ▸ Import ▸ Wirebond Wires… to give it one."
                    : $"wBond '{comp.InstanceName}': its embedded design could not be read, or declares "
                      + "no wire arrays, so it has no pins; skipped.");
                return null;
            }

            var wbNets = new List<string>(wbDefs.Count);
            foreach (var def in wbDefs)
            {
                var (px, py) = model.PortWorldOf(comp, def);
                wbNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }

            // wbond.md §9.7, revised 2026-10-05 — the netlist carries the SCHEMATIC's own wires, always,
            // exactly as every other component simulates what the schematic says. Until then a `Linked`
            // instance named its `.wBond` here instead, so a layout edit that added an array produced a
            // model with more terminals than the symbol had pins, and the run was refused. The layout
            // file is now only COMPARED, and a difference is a warning the instance can switch off.
            if (warningsOut is not null && WBondPlacement.WarnsIfUnsynced(comp)
                && WBondSync.Check(comp, model.SchematicDirectory).Warning(comp.InstanceName) is { } unsynced)
                warningsOut.Add(unsynced);

            var wbOverrides = comp.Parameters
                // `SymbolPitch` is artwork, deciding how far apart the symbol's port rows sit and
                // nothing else. Same rule CvData and ShowBias already follow.
                //
                // `Arrays` is circuitRF's own record of the array list this instance was wired against
                // (§5 question 3) — editor bookkeeping, and the payload cannot drift against itself.
                //
                // `File` and `WarnUnsynced` are the layout comparison above, which the engine has no use
                // for; `Source` is the retired WB45 switch an older document may still carry.
                //
                // `RefPin` is deliberately NOT in this list. It looks like artwork and is not: it
                // decides whether the component has 2M or 2M+1 terminals, so the engine has to be
                // told, or the model's port count disagrees with the net list the extractor just
                // built from the symbol.
                .Where(p => p.Name is not ("Arrays" or "SymbolPitch" or "File"
                                           or WBondPlacement.LegacySourceParameter
                                           or WBondPlacement.WarnUnsyncedParameter))
                // A blank value means "use the design's own", and it must be DROPPED rather than
                // emitted: `Temp=` with nothing after it is the empty-parameter-value trap already
                // recorded in src/Core/CLAUDE.md, where the .cnl reader glues the next token on as
                // the value and silently eats the parameters after it.
                .Where(p => !string.IsNullOrWhiteSpace(p.Expression))
                .Select(p =>
                {
                    // The material library is a PATH: relative to the schematic in the document, absolute
                    // in the netlist, because the netlist is a generated intermediate written wherever the run writes it. Quoted, as a
                    // metal's NAME is when it has a space in it ("Gold-tin solder (80/20)"): a .cnl is
                    // whitespace-delimited, and an unquoted tail would be read as a unit and refuse the line.
                    if (p.Name == WBondPlacement.MaterialLibraryParameter)
                        return new ParameterAssignment(p.Name,
                            Quoted(WBondPlacement.ResolveMaterialLibrary(comp, model.SchematicDirectory) ?? p.Expression), null);
                    if (WBondPlacement.IsMaterialParameter(p.Name))
                        return new ParameterAssignment(p.Name, Quoted(p.Expression.Trim()), null);

                    var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                    return new ParameterAssignment(p.Name, p.Expression, unit.Length > 0 ? unit : null);
                })
                .ToList();

            static string Quoted(string v) =>
                v.Length >= 2 && v[0] == '"' && v[^1] == '"' || !v.AsSpan().ContainsAny(' ', '\t') ? v : $"\"{v}\"";

            return new Instance(comp.InstanceName, reference, wbNets, wbOverrides);
        }

        // A composed FET (brief-modelcomposer-1): pins 1/2/3 are g d s BY NUMBER, whatever symbol draws
        // them. `Symbol` is artwork and never reaches the engine; a blank `N` means the model's own
        // base finger count and is dropped rather than written as `N=` (the empty-value trap).
        if (comp.Symbol == SymbolKind.ComposedFET)
        {
            // Pin 4 is the thermal node t (MC-9 R-mc9-2). Unwired it is no connection: the line keeps three nets, so a
            // model that does not self-heat places with the standard glyph's thermal pin left open.
            var cfNets = new List<string>();
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions).OrderBy(d => d.PortIndex))
            {
                if (def.PortIndex == 3 && !IsPinWired(model, comp, def, uf, QK, cellRefResolutions)) continue;
                if (def.PortIndex > 3) continue;
                var (px, py) = model.PortWorldOf(comp, def);
                cfNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }

            string? Param(string name) => comp.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;
            if (warningsOut is not null
                && ComposedFetSymbolProvider.Diagnose(comp.InstanceName, Param(ComposedFetSymbolProvider.FileParameter),
                                                      Param(ComposedFetSymbolProvider.SymbolParameter),
                                                      model.SchematicDirectory) is { } symbolWarning)
                warningsOut.Add(symbolWarning);

            // A switch at its default is left out (brief-modelcomposer-21 R-mc21-6), so a schematic that never touched one
            // extracts exactly as before the switches existed.
            var cfOverrides = comp.Parameters
                .Where(p => p.Name != ComposedFetSymbolProvider.SymbolParameter && !string.IsNullOrWhiteSpace(p.Expression))
                .Where(p => !ComposedSwitches.IsSwitch(p.Name) || !ComposedSwitches.StatesDefault(p.Name, p.Expression))
                .Select(p => p.Name == ComposedFetSymbolProvider.FileParameter
                    ? new ParameterAssignment(p.Name, QuotedPath(p.Expression), null)
                    : new ParameterAssignment(p.Name, p.Expression,
                          UnitNormalizer.ToEngineUnit(p.Unit) is { Length: > 0 } u ? u : null))
                .ToList();

            static string QuotedPath(string v)
            {
                string t = v.Trim();
                return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t : $"\"{t}\"";
            }

            return new Instance(comp.InstanceName, reference, cfNets, cfOverrides);
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
            // CLIN's Eo is optional and placed blank (blank = "same as E"). It is DROPPED rather than
            // emitted: `Eo=` with nothing after it is the empty-parameter-value trap, where the .cnl
            // reader takes the next token as the value.
            .Where(p => !(comp.Symbol == SymbolKind.CLine && string.IsNullOrWhiteSpace(p.Expression)))
            // The amplifier's, the mixer's and the FETs' noise keys (briefs NZ-3, NZ-4) are placed blank (blank = noiseless) and dropped for the
            // same reason; a blank row is the absent key, so a design that states no noise extracts exactly as before.
            .Where(p => !(comp.Symbol is SymbolKind.Amp or SymbolKind.Mixer or SymbolKind.MixerD
                          or SymbolKind.FetCurtice or SymbolKind.FetCurticeCubic or SymbolKind.FetStatz
                          or SymbolKind.FetMaterka or SymbolKind.FetAngelov
                          && string.IsNullOrWhiteSpace(p.Expression)))
            // NFType qualifies an NF; with no NF it says nothing, and is dropped so a placed mixer's default adds no key.
            .Where(p => !(comp.Symbol is SymbolKind.Mixer or SymbolKind.MixerD && p.Name == "NFType"
                          && string.IsNullOrWhiteSpace(comp.Parameters.FirstOrDefault(q => q.Name == "NF")?.Expression)))
            // A source's phase-noise mask (brief NZ-7) is placed blank and dropped the same way.
            .Where(p => !(p.Name is "PhaseNoise" or "PhaseNoiseFloor" && string.IsNullOrWhiteSpace(p.Expression)))
            .Select(p =>
            {
                var unit = UnitNormalizer.ToEngineUnit(p.Unit);
                // The mask is a list with spaces and commas in it; unquoted, its words would read as nets.
                string expr = p.Name == "PhaseNoise" && !p.Expression.TrimStart().StartsWith('"')
                    ? "\"" + p.Expression.Trim() + "\"" : p.Expression;
                return new ParameterAssignment(p.Name, expr, unit.Length > 0 ? unit : null);
            })
            .ToList();

        // VerilogA (brief-vendorlib-7): `Symbol` and `Terminals` say how the instance is DRAWN and never reach the
        // model. The nets need nothing special — a FET glyph's pins carry the model terminal they bind to as their
        // PortIndex, so the generic PortIndex-order emission below lists them in the model's own order.
        if (comp.Symbol == SymbolKind.VerilogA)
        {
            string? Param(string name) => comp.Parameters.FirstOrDefault(p => p.Name == name)?.Expression;
            overrides2.RemoveAll(o => o.Name is VerilogASymbolProvider.SymbolParameter
                                             or VerilogASymbolProvider.TerminalsParameter);
            if (warningsOut is not null)
                warningsOut.AddRange(VerilogASymbolProvider.Diagnose(
                    comp.InstanceName, comp.PortCount, Param(VerilogASymbolProvider.TerminalsParameter),
                    Param(VerilogASymbolProvider.SymbolParameter), model.SchematicDirectory));
        }

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

        // GROUND-REFERENCED N-PORT BLOCKS: N symbol pins, but the model declares N PORTS — i.e. 2N
        // nets in ± pair order — with every port's − hard-wired to ground ("0"). Exactly what TermG
        // does with Term's port 2 and Tuner does with its reference: a packaging convenience over
        // the SAME engine component, never a parallel model.
        //
        // A SET rather than a dozen copies of one branch, because the rule is one rule. MixerD is
        // deliberately absent: it emits all six nets from its own pins and falls through to the
        // ordinary path below, which is the whole difference between the two mixer tiles.
        if (GroundReferencedPortBlocks.Contains(comp.Symbol))
        {
            var portNets = new List<string>();
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                portNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
                portNets.Add("0");
            }
            return new Instance(comp.InstanceName, reference, portNets, overrides2);
        }

        // The vias (brief-via-component.md): the layer choices and any dimension left empty are
        // resolution INPUTS, like a microstrip's SignalLayer — never engine parameters, and an empty
        // value must not reach the .cnl at all (CnlWriter would write "Key=" and the reader would glue
        // the next token onto it). What the stackup resolves is appended in their place.
        if (ViaSubstrateInjection.IsViaKind(comp.Symbol))
        {
            overrides2.RemoveAll(o => ViaSubstrateInjection.LayerParams.Contains(o.Name)
                                      || string.IsNullOrWhiteSpace(o.Expression));
            for (int i = 0; i < overrides2.Count; i++)
                if (overrides2[i].Name == "IncludeC")
                    overrides2[i] = new ParameterAssignment("IncludeC", ViaSubstrateInjection.FlagExpression(overrides2[i].Expression));
            var via = ViaSubstrateInjection.Build(microstripTech, comp.Symbol, comp.Parameters);
            overrides2.AddRange(via.Overrides);
            foreach (var m in via.Messages) warningsOut?.Add($"{comp.InstanceName}: {m}");

            var viaNets = new List<string>();
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                viaNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }
            // A via to ground draws one pin and binds two nets, the far end being the reference.
            if (comp.Symbol == SymbolKind.ViaGnd) viaNets.Add("0");
            return new Instance(comp.InstanceName, reference, viaNets, overrides2);
        }

        // The test pad (brief-dfr26-7): D, Drill and ClearD reach the engine as typed; every other row is a resolution
        // or artwork input, consumed here, and what the stackup resolves for it is appended in their place.
        if (TestPadInjection.IsTestPadKind(comp.Symbol))
        {
            overrides2.RemoveAll(o => !TestPadInjection.EngineParams.Contains(o.Name) || string.IsNullOrWhiteSpace(o.Expression));
            var pad = TestPadInjection.Build(microstripTech, comp.Parameters);
            overrides2.AddRange(pad.Overrides);
            foreach (var m in pad.Messages) warningsOut?.Add($"{comp.InstanceName}: {m}");

            var padNets = new List<string>();
            foreach (var def in GetEffectivePortDefs(model, comp, cellRefResolutions))
            {
                var (px, py) = model.PortWorldOf(comp, def);
                padNets.Add(NetForPort(comp, def.PortIndex, px, py, uf, QK, netNames, detachedKeys));
            }
            return new Instance(comp.InstanceName, reference, padNets, overrides2);
        }

        // The broadside-coupled pairs (BCLIN, MBCLIN): the layer choices and every cross-section row left
        // empty are resolution INPUTS, exactly as a via's are — never engine parameters, and an empty
        // value must not reach the .cnl. What the stackup resolves for them is appended in their place.
        if (BroadsideSubstrateInjection.IsBroadsideKind(comp.Symbol))
        {
            overrides2.RemoveAll(o => BroadsideSubstrateInjection.LayerParams.Contains(o.Name)
                                      || string.IsNullOrWhiteSpace(o.Expression));
            var pair = BroadsideSubstrateInjection.Build(microstripTech, comp.Symbol, comp.Parameters);
            overrides2.AddRange(pair.Overrides);
            foreach (var m in pair.Messages) warningsOut?.Add($"{comp.InstanceName}: {m}");
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
            // A Roughness the instance states itself is a deliberate per-part value and wins over the
            // technology's.
            bool ownRoughness = comp.Parameters.Any(p => p.Name == MicrostripSubstrateInjection.RoughnessParameter
                                                         && !string.IsNullOrWhiteSpace(p.Expression));
            overrides2.AddRange(ownRoughness
                ? substrateOverrides.Where(o => o.Name != MicrostripSubstrateInjection.RoughnessParameter)
                : substrateOverrides);
            if (warning is not null)
                warningsOut?.Add($"{comp.InstanceName}: {warning}");
        }

        // brief-artsch-1: CPWG and SLIN, by PlanarLineSubstrateInjection's rule. A SLIN the technology cannot
        // give two planes is a refusal: it is reported here, and the line keeps its SignalLayer so the run's
        // own binding (CnlTechnologyBinding) refuses it again rather than simulating it on the fallback board.
        if (PlanarLineSubstrateInjection.IsPlanarLineKind(comp.Symbol))
        {
            string? signalOverride = NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "SignalLayer")?.Expression);
            string? groundOverride = comp.Symbol == SymbolKind.Cpwg
                ? NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == "GroundReference")?.Expression)
                : null;
            var binding = PlanarLineSubstrateInjection.Build(microstripTech, comp.Symbol, signalOverride, groundOverride);
            overrides2.AddRange(binding.Overrides);
            foreach (var w in binding.Warnings) warningsOut?.Add($"{comp.InstanceName}: {w}");
            if (binding.Refusal is { } refusal)
            {
                warningsOut?.Add($"Instance '{comp.InstanceName}' (SLIN) is refused: {refusal}.");
                if (signalOverride is not null)
                    overrides2.Add(new ParameterAssignment("SignalLayer", $"\"{signalOverride}\""));
            }
        }

        // AA-1: the MMIC passives take their process numbers from the same technology, by the same
        // resolution their artwork draws with (MmicPassiveInjection).
        if (MmicPassiveInjection.IsMmicKind(comp.Symbol))
        {
            // A TFR's Film is consumed here. One naming no film stays on the line, so the run's own binding refuses it too
            // (CnlTechnologyBinding), as a SLIN's SignalLayer does, rather than simulating another film's value.
            string? film = comp.Symbol == SymbolKind.Tfr
                ? NonEmptyOrNull(comp.Parameters.FirstOrDefault(p => p.Name == MmicPassiveInjection.FilmParam)?.Expression)
                : null;
            overrides2.RemoveAll(o => comp.Symbol == SymbolKind.Tfr && o.Name == MmicPassiveInjection.FilmParam);
            if (film is not null && (microstripTech is null || MmicPassiveInjection.FilmRefusal(microstripTech, film) is not null))
            {
                if (MmicPassiveInjection.FilmRefusal(microstripTech, film) is { } refused)
                    warningsOut?.Add($"Instance '{comp.InstanceName}' (TFR) is refused: {refused}.");
                overrides2.Add(new ParameterAssignment(MmicPassiveInjection.FilmParam, $"\"{film}\""));
            }
            else
            {
                var (mmicOverrides, mmicWarning) = MmicPassiveInjection.Build(microstripTech, comp.Symbol, film);
                // brief-icp-9 — a parameter the part states is not stated twice: the netlist's binding (CnlTechnologyBinding)
                // already leaves a stated one alone, and a part drawn from a netlist (a _cosim cell) states them all, so
                // appending the technology's copy wrote each one twice on the extracted line.
                overrides2.AddRange(mmicOverrides.Where(m => !overrides2.Any(o => o.Name.Equals(m.Name, StringComparison.Ordinal))));
                if (mmicWarning is not null)
                    warningsOut?.Add($"{comp.InstanceName}: {mmicWarning}");
            }
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
            // directory; a VIRTUAL reference carries its own resolution rule and does not. The
            // resolver owns that list — extracting an unsaved schematic must see exactly the same
            // pins the canvas drew, or a kit part would extract with the wrong terminal count.
            if (model.SchematicDirectory is null && !CellSymbolResolver.NeedsNoBaseDirectory(symRef)) continue;
            result ??= new Dictionary<string, CellSymbolResolution>(StringComparer.Ordinal);
            result[comp.Id] = CellSymbolResolver.Resolve(symRef, model.SchematicDirectory, model.HostWorkspaceRoot);
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
