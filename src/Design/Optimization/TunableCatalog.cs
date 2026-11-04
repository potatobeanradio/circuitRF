using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Optimization;

/// <summary>What a tunable is: a component's parameter, a VAR row, a sub-circuit instance's own
/// declared cell parameter, a placed vendor part's choice of part (brief vendorlib-4), or one S-parameter entry's
/// dB or phase offset on an SnP block (brief-sens-2).</summary>
public enum TunableKind { Parameter, Variable, CellParameter, Part, PartValue, SnpEntry }

/// <summary>
/// An SnP entry tunable's place in its block (brief-sens-2 R-sens2-4): <c>SdB_3_1</c> is (3, 1, dB).
/// </summary>
/// <param name="Instance">The instance as a key's owner prefix spells it — <c>SNP3</c>, <c>DUT:SNP3</c>.</param>
/// <param name="I">The row port, 1-based.</param>
/// <param name="J">The column port, 1-based.</param>
/// <param name="Ports">The block's port count.</param>
/// <param name="Tied">One offset moves S(i,j) and S(j,i) together.</param>
/// <param name="TieReason">Why it is tied or not: <c>auto: worst |S(i,j) − S(j,i)| = 0.0021</c>, <c>stated</c>.</param>
public sealed record SnpEntryInfo(string Instance, int I, int J, RfCore.SnpOffsetKind Kind, int Ports, bool Tied, string TieReason)
{
    /// <summary><c>SNP3 · S31 dB</c> — <see cref="TunableKey.SnpEntryLabel"/>.</summary>
    public string Label => TunableKey.SnpEntryLabel(Instance, I, J, Kind, Ports);
}

/// <summary>One SnP instance as the catalog found it (brief-sens-2): what <c>explain --tunables</c> lists.</summary>
/// <param name="Instance">The owner prefix (<c>SNP3</c>, <c>DUT:SNP3</c>).</param>
/// <param name="Location"><c>top</c> or <c>DUT · ×2</c>.</param>
/// <param name="Ports">The port count; 0 when the file did not load and NumPorts is not stated.</param>
/// <param name="Tied">Null when the file did not load.</param>
/// <param name="TieReason">Why it is tied or untied.</param>
/// <param name="Entries">The keys offered.</param>
/// <param name="LoadError">Why the file did not load; null when it did.</param>
/// <param name="Reciprocity">The measured worst |S(i,j) − S(j,i)|; null when the file did not load.</param>
public sealed record SnpInstanceInfo(string Instance, string Location, string? Cell, int Ports, bool? Tied, string TieReason,
                                     int Entries, string? LoadError, RfCore.SnpReciprocity? Reciprocity, RfCore.SnpReciprocal Mode);

/// <summary>
/// One value of a design that can be tuned (overview D1/D2).
/// </summary>
/// <param name="Key">The identity (overview D3).</param>
/// <param name="Location"><c>top</c>, or <c>DUT · ×2</c> — the cell it lives in and how many
/// instances of that cell share it.</param>
/// <param name="Cell">The cell as the <c>.cnl</c> spells its instance type; null at the top.</param>
/// <param name="InstanceCount">How many instances of <paramref name="Cell"/> the whole hierarchy holds
/// (1 at the top). Tuning a value inside a cell moves it in all of them.</param>
/// <param name="Owner">The instance, or the variable's own name.</param>
/// <param name="Parameter">The parameter's name; null for a variable.</param>
/// <param name="ValueText">The value as the design holds it (<c>47 pF</c>).</param>
/// <param name="Value">The number in <paramref name="Unit"/> (47).</param>
/// <param name="Unit">The engine spelling of its unit, "" for none.</param>
/// <param name="IsInteger">The parameter only takes whole numbers (a finger count, a device multiplier).</param>
/// <param name="IsDefault">A cell parameter the instance does not override — the value is the cell's
/// declared default, and Push adds an override rather than editing one.</param>
/// <param name="ReadOnlyReason">Why Push cannot write it; null when it can. Tuning and optimizing it
/// still work.</param>
/// <param name="DisabledReason">Why it is offered but cannot be moved right now (a parametric sweep
/// sweeps it); null when it can.</param>
/// <param name="DefaultMin">The range a first activation gives it (overview D4), in its own unit.</param>
/// <param name="DefaultMax">Upper end of that range.</param>
/// <param name="RangeGuessed">The value is zero, so the default range is a guess.</param>
/// <param name="Part">Which part of a complex value this is (overview D18); null for a real value.
/// <paramref name="Value"/>, <paramref name="Unit"/> and <paramref name="ValueText"/> are then the
/// part's — a phase is in <c>deg</c> — and the whole value is in the four parameters after it.</param>
/// <param name="WholeKey">The key of the whole complex value (<c>ZL</c> for <c>mag(ZL)</c>).</param>
/// <param name="Whole">The whole value as the design holds it, in <paramref name="WholeUnit"/>.</param>
/// <param name="WholeUnit">The whole value's unit, "" for none.</param>
/// <param name="Form">How the design writes it, which is how Push writes it back.</param>
/// <param name="VendorPart">A placed vendor part's <c>Part</c> tunable (<see cref="TunableKind.Part"/>): the part
/// and its candidates. <paramref name="ValueText"/> is then the part number, <paramref name="Value"/> its stated
/// value in SI and <paramref name="Unit"/> that value's unit; the bounds are the candidates' value span.</param>
/// <param name="PartEntry">A part's value tunable (brief vendorlib-5 R-vl5-3a): the library entry the value and its
/// tolerance are the part's — the placed part, or at a moved design the candidate the part is bound to.</param>
/// <param name="ValueOf">A part's value tunable: the placed part it is the value of. Deliberately not
/// <paramref name="VendorPart"/>, which marks the part's CHOICE everywhere it is read.</param>
/// <param name="SnpEntry">An SnP block's S-parameter entry offset (<see cref="TunableKind.SnpEntry"/>): the entry, the
/// block's port count and its tie state with its reason.</param>
public sealed record Tunable(
    string      Key,
    string      Location,
    string?     Cell,
    int         InstanceCount,
    string      Owner,
    string?     Parameter,
    TunableKind Kind,
    string      ValueText,
    double      Value,
    string      Unit,
    bool        IsInteger,
    bool        IsDefault,
    string?     ReadOnlyReason,
    string?     DisabledReason,
    string      DefaultMin,
    string      DefaultMax,
    bool        RangeGuessed,
    ComplexPart? Part      = null,
    string?     WholeKey   = null,
    Complex     Whole      = default,
    string      WholeUnit  = "",
    ComplexForm Form       = ComplexForm.Rect,
    CircuitRF.Design.VendorLibraries.VendorPartTunable? VendorPart = null,
    CircuitRF.Design.VendorLibraries.VendorLibraryEntry? PartEntry = null,
    CircuitRF.Design.VendorLibraries.VendorPartTunable? ValueOf = null,
    SnpEntryInfo? SnpEntry = null)
{
    /// <summary>What a table calls it: an SnP entry's <see cref="SnpEntryInfo.Label"/>, any other tunable its key.</summary>
    public string Label => SnpEntry?.Label ?? Key;

    /// <summary>A placed vendor part's choice of part, not a number.</summary>
    public bool IsVendorPart => VendorPart is not null;

    /// <summary>A placed vendor part's C or L value — statistical only (brief vendorlib-5 R-vl5-3a).</summary>
    public bool IsPartValue => Kind == TunableKind.PartValue;

    /// <summary>The part's own tolerance, for <c>tol=part</c>; null when this is not a part's value or it is not known.</summary>
    public CircuitRF.Design.VendorLibraries.VendorPartTolerance? PartTolerance => IsPartValue ? PartEntry?.Tolerance : null;

    /// <summary>Why the part's own tolerance is not known, for <c>check</c>'s error; null when it is.</summary>
    public string? PartToleranceNote => IsPartValue && PartEntry?.Tolerance is null
        ? PartEntry?.ToleranceNote ?? "its header states no tolerance"
        : null;

    /// <summary>The key a value is written under — in a preset, a session request, a Push: the whole
    /// value's for a part, since a schematic holds a complex value whole.</summary>
    public string ValueKey => WholeKey ?? Key;

    /// <summary>A resistance, inductance or capacitance (or a magnitude in one of those units): a
    /// value nothing automatic — Widen, Re-centre — may move a range below zero for. A minimum typed
    /// below zero is still taken; that is how a range opts in to negative values.</summary>
    public bool IsNonNegative => Part is null or ComplexPart.Mag && Units.BaseUnit(Unit) is "Ohm" or "H" or "F";
}

/// <summary>
/// Every tunable of a schematic, at any depth, plus the keys its tuning setup names that resolve to
/// nothing (overview D3).
///
/// <para><b>It walks the netlist the design extracts to, not the drawing.</b> The hierarchy is
/// descended once, by <see cref="NetExtractor"/> with the caller's resolver — the one Simulate uses —
/// so the cell names in the keys are spelled exactly as the <c>.cnl</c> spells the instance types,
/// a cell two workspaces both call <c>Amp</c> gets the same <c>Amp_2</c> here as in the netlist, and
/// there is no second resolver to disagree with the first.</para>
/// </summary>
public sealed class TunableCatalog
{
    /// <summary>Parameters that only take whole numbers. Case matters: <c>m</c> is the device
    /// multiplier, <c>M</c> a diode's grading coefficient.</summary>
    private static readonly HashSet<string> IntegerParameters = new(StringComparer.Ordinal)
        { "m", "Nf", "NumFingers", "Fingers" };

    /// <summary>Numbers that are identities, not values: a port's number, a block's port count.</summary>
    private static readonly HashSet<string> IdentityParameters = new(StringComparer.Ordinal)
        { "Num", "NumPorts", "NumFreqs" };

    private readonly Dictionary<string, Tunable> _byKey;
    private readonly Dictionary<string, string>  _notOffered;
    private readonly Dictionary<string, List<Tunable>> _parts;
    private IReadOnlyDictionary<string, SchematicEditModel> _drawings =
        new Dictionary<string, SchematicEditModel>(StringComparer.Ordinal);

    private TunableCatalog(List<Tunable> tunables, Dictionary<string, string> notOffered)
    {
        Tunables       = tunables;
        _byKey         = tunables.GroupBy(t => t.Key, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        _parts         = tunables.Where(t => t.WholeKey is not null)
                                 .GroupBy(t => t.WholeKey!, StringComparer.Ordinal)
                                 .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _notOffered    = notOffered;
    }

    /// <summary>Every tunable, top level first, then each cell in the netlist's own order.</summary>
    public IReadOnlyList<Tunable> Tunables { get; }

    /// <summary>Every SnP instance the design reaches, with its port count and tie state (brief-sens-2).</summary>
    public IReadOnlyList<SnpInstanceInfo> SnpInstances { get; private set; } = [];

    /// <summary>The placed vendor parts the catalog was built with — what a run prepared from the same bench is
    /// handed (<see cref="Circuit.PreparedCircuit.FromBench"/>) so it offers the same <c>Instance.Part</c> keys.</summary>
    public IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable> PartTunables { get; private set; } = [];

    /// <summary>Keys the design's tuning setup names (entries and preset values) that resolve to
    /// nothing at all — a warning, never an error (overview D3).</summary>
    public IReadOnlyList<string> UnresolvedKeys { get; private set; } = [];

    /// <summary>The tunable with this key, or null. A complex value's own key finds nothing — its parts
    /// are the tunables (<see cref="PartsOf"/>).</summary>
    public Tunable? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>The four parts of the complex value <paramref name="wholeKey"/> — real, imaginary,
    /// magnitude, phase — or empty when it is not a complex value the catalog offers.</summary>
    public IReadOnlyList<Tunable> PartsOf(string wholeKey)
        => _parts.TryGetValue(wholeKey, out var parts) ? parts : [];

    /// <summary>
    /// What a key a value is written under names: the tunable itself, or for a complex value's own key
    /// its real part, which carries everything about the whole (overview D18). Null when neither.
    /// </summary>
    public Tunable? FindValue(string key) => Find(key) ?? PartsOf(key).FirstOrDefault();

    /// <summary>Why a key that DOES name a value of the design is not offered (it is an expression,
    /// a string, a port number, a complex value named whole, a part of a value that is not complex);
    /// null when it is offered or names nothing.</summary>
    public string? WhyNotOffered(string key)
    {
        if (_notOffered.TryGetValue(key, out var why)) return why;
        if (_byKey.ContainsKey(key) || !TunableKey.TryParse(key, out var k) || k.Part is null) return null;
        string whole = k.Whole.ToString();
        if (_byKey.TryGetValue(whole, out var real) && real.Part is null) return "its value is not a complex number";
        return _notOffered.GetValueOrDefault(whole);
    }

    /// <summary>
    /// The drawing each scope's values live in: <c>""</c> for the tuned schematic itself, otherwise the
    /// cell as the keys spell it. These are the very models the resolver handed out — in the GUI the
    /// open sessions' own — so Push writes into the drawing that is on screen. Empty for a catalog of
    /// a netlist, which has no drawing.
    /// </summary>
    public IReadOnlyDictionary<string, SchematicEditModel> Drawings => _drawings;

    /// <summary>The cell a drawing is, as the keys spell it — <c>""</c> for the tuned schematic, null for
    /// a drawing this design does not reach.</summary>
    public string? CellOf(SchematicEditModel drawing)
    {
        foreach (var (cell, model) in _drawings)
            if (ReferenceEquals(model, drawing)) return cell;
        return null;
    }

    /// <summary>
    /// The keys of a parameter row as drawn in <paramref name="drawing"/> — a VAR row is its variable,
    /// anything else <c>instance.parameter</c>; a complex value's four parts, real first (overview D18)
    /// — or empty when the catalog offers nothing of it. The one mapping the Inspector, the canvas and
    /// Push share, so the three cannot disagree on what a row is.
    /// </summary>
    public IReadOnlyList<string> KeysFor(SchematicEditModel drawing, EditableComponent component, EditableParameter parameter)
    {
        if (CellOf(drawing) is not { } cell || string.IsNullOrWhiteSpace(parameter.Name)) return [];
        string prefix = cell.Length == 0 ? "" : cell + ":";
        string key = component.Symbol == SymbolKind.Var
            ? prefix + parameter.Name.Trim()
            : $"{prefix}{component.InstanceName}.{parameter.Name}";
        if (_byKey.ContainsKey(key)) return [key];
        return [.. PartsOf(key).Select(t => t.Key)];
    }

    /// <summary>
    /// The catalog of a schematic. <paramref name="cells"/> is the resolver Simulate uses — the
    /// workspace's in the GUI, <see cref="DiskCellResolver.Instance"/> headless.
    /// </summary>
    /// <param name="workspaceRoot">The workspace the schematic belongs to. A cell outside it is
    /// read-only (Push would write another workspace). Null skips that test.</param>
    /// <param name="referenceBase">What a relative SnP <c>File</c> resolves against — the base Simulate elaborates with
    /// (<c>SchematicCircuit.ReferenceBaseOf</c>); null takes <paramref name="workspaceRoot"/>.</param>
    public static TunableCatalog Discover(SchematicEditModel schematic, ICellResolver cells, string? workspaceRoot = null,
                                          string? referenceBase = null)
    {
        var recording = new RecordingResolver(cells);
        var extracted = NetExtractor.Extract(schematic, "tb", recording);

        // Only the schematic's own VAR rows are top-level variables. The extracted globals also hold a
        // cell's own parameter defaults, a kit's process constants and the selected corners — none of
        // which is a row anyone could push a value into.
        var varRows = schematic.Components
            .Where(c => c.Symbol == SymbolKind.Var && c.Disable is not (DisableState.Open or DisableState.Short))
            .SelectMany(c => c.Parameters)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name.Trim())
            .ToHashSet(StringComparer.Ordinal);

        // Extraction ADDS parameters a drawing never stated — a microstrip's substrate comes from the
        // workspace technology. Those are not values of this design and Push has no row to write them
        // into, so an instance parameter is offered only when the component in the drawing holds it.
        var drawings = new Dictionary<string, SchematicEditModel>(StringComparer.Ordinal) { [""] = schematic };
        foreach (var (libName, key) in extracted.CellKeys)
            if (recording.Seen.TryGetValue(key, out var model)) drawings[libName] = model;

        bool Stored(string? cell, string instance, string parameter)
            => !drawings.TryGetValue(cell ?? "", out var m)          // a kit cell: no drawing to ask
            || m.Components.Any(c => c.InstanceName == instance && c.Parameters.Any(p => p.Name == parameter));

        // A disabled component is not in the netlist; one excluded from it (a probe, a MEAS block) has
        // nothing to tune. Instances the extraction emitted are exactly the rest.
        var catalog = FromNetlist(extracted.TestBench, extracted.Library, varRows, extracted.CellKeys, workspaceRoot, Stored,
                                  extracted.PartTunables, referenceBase ?? workspaceRoot);
        catalog._drawings = drawings;
        return catalog;
    }

    /// <summary>Passes every resolution through, remembering each cell's drawing by its key.</summary>
    private sealed class RecordingResolver(ICellResolver inner) : ICellResolver
    {
        public Dictionary<string, SchematicEditModel> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CellResolution? Resolve(EditableComponent cellInstance, SchematicEditModel containingModel)
        {
            var r = inner.Resolve(cellInstance, containingModel);
            if (r is not null) Seen.TryAdd(r.Key, r.Schematic);
            return r;
        }
    }

    /// <summary>
    /// The catalog of a netlist — what <see cref="Discover"/> computes after extraction, and what
    /// <c>check</c> asks of a hand-written <c>.cnl</c>.
    /// </summary>
    /// <param name="variableNames">The top-level names that are VAR rows. Null treats every global as
    /// one, which is right for a <c>.cnl</c>, whose globals are its variables.</param>
    /// <param name="cellKeys">Library name → cell folder (<see cref="NetExtractor.ExtractionResult.CellKeys"/>).
    /// Null when unknown — then nothing is reported read-only.</param>
    /// <param name="stored">Whether the drawing of a scope (null cell = the top) holds a parameter on an
    /// instance. Null when there is no drawing — a <c>.cnl</c> holds every value it states.</param>
    /// <param name="parts">The placed vendor parts (<see cref="NetExtractor.ExtractionResult.PartTunables"/>,
    /// <see cref="Circuit.PreparedCircuit.PartTunables"/>): each is offered as ONE tunable, <c>Instance.Part</c>,
    /// and never as the hidden parameters it was placed with (brief vendorlib-4 R-vl4-1a). Null for a netlist,
    /// which cannot place one.</param>
    /// <param name="referenceBase">What a relative SnP <c>File</c> resolves against, as elaboration resolves it
    /// (<see cref="Elaborator.ResolveFileReference"/>); a <c>.cnl</c>'s reader has already made a quoted one absolute.</param>
    public static TunableCatalog FromNetlist(
        TestBench tb, Library lib, IReadOnlyCollection<string>? variableNames = null,
        IReadOnlyDictionary<string, string>? cellKeys = null, string? workspaceRoot = null,
        Func<string?, string, string, bool>? stored = null,
        IReadOnlyList<CircuitRF.Design.VendorLibraries.VendorPartTunable>? parts = null,
        string? referenceBase = null)
    {
        var snpInstances = new List<SnpInstanceInfo>();
        var partsByKey = (parts ?? []).GroupBy(p => p.Key, StringComparer.Ordinal)
                                      .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var tunables   = new List<Tunable>();
        var notOffered = new Dictionary<string, string>(StringComparer.Ordinal);
        var counts     = InstanceCounts(tb, lib);

        var swept = tb.Analyses.OfType<ParametricSweepAnalysis>()
            .Where(a => a.Enabled)
            .GroupBy(a => a.SweepVarName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        var topVars = tb.GlobalVariables.Where(v => variableNames is null || variableNames.Contains(v.Name));
        Scope(null, 1, null, tb.Instances, topVars);

        // A vendor part's own circuit is the library's statement of the part, not a value of this design: the part
        // offers ONE tunable, its choice of part (brief vendorlib-4 R-vl4-1a), and its internals none.
        var partCells = new HashSet<string>(
            (parts ?? []).SelectMany(p => p.Candidates.Select(c => c.Cell).Append(p.PlacedCell)).Where(c => c.Length > 0),
            StringComparer.Ordinal);

        // Cells in the library's own (leaf-first) order; one the hierarchy never reaches is not part
        // of this design, however much a kit's netlist brought along.
        foreach (var cell in lib.Cells)
        {
            if (!counts.TryGetValue(cell.Name, out int n) || partCells.Contains(cell.Name)) continue;
            Scope(cell.Name, n, ReadOnlyReason(cell.Name, cellKeys, workspaceRoot), cell.Instances, cell.Variables);
        }

        var catalog    = new TunableCatalog(tunables, notOffered) { PartTunables = parts ?? [], SnpInstances = snpInstances };
        var unresolved = new List<string>();
        if (tb.Tuning is { } setup)
        {
            // A preset holds a complex value whole, under the value's own key (overview D18).
            var named = setup.Variables.Select(v => v.Key)
                .Concat(setup.Presets.SelectMany(p => p.Values.Keys));
            foreach (var key in named.Distinct(StringComparer.Ordinal))
                if (catalog.FindValue(key) is null && catalog.WhyNotOffered(key) is null)
                    unresolved.Add(key);
        }
        catalog.UnresolvedKeys = unresolved;
        return catalog;

        void Scope(string? cell, int count, string? readOnly, IEnumerable<Instance> instances, IEnumerable<Variable> vars)
        {
            string prefix   = cell is null ? "" : cell + ":";
            string location = cell is null ? "top" : $"{cell} · ×{count}";

            foreach (var inst in instances)
            {
                if (partsByKey.TryGetValue($"{prefix}{inst.InstanceName}.{TunableKey.PartParameter}", out var part))
                {
                    tunables.Add(PartTunable(part, location, cell, count, readOnly));
                    // Its C or L value, drawn in a Monte Carlo trial and never tuned (brief vendorlib-5 R-vl5-3a).
                    if (PartValueTunable(part, prefix, location, cell, count, notOffered) is { } value) tunables.Add(value);
                    continue;
                }
                if (lib.Find(inst.Reference) is { } def)
                {
                    // A sub-circuit instance: its cell's DECLARED parameters, per instance, whether
                    // this instance overrides them or inherits the default (overview D2). A selected corner's values
                    // are this instance's defaults (brief vendorlib-13), so the nominal a tune starts from is the corner's.
                    Cell cornered = def;
                    try { cornered = CircuitRF.Core.Design.CellCorner.Apply(def, inst.Overrides, inst.InstanceName).Cell; }
                    catch (InvalidOperationException) { }   // a corner the cell lacks: the run refuses it and names it
                    foreach (var decl in cornered.Parameters)
                    {
                        var ov = inst.Overrides.FirstOrDefault(o => o.Name == decl.Name);
                        var (expr, unit) = ov is null ? (decl.DefaultExpression, decl.Unit) : (ov.Expression, ov.Unit);
                        Consider($"{prefix}{inst.InstanceName}.{decl.Name}", inst.InstanceName, decl.Name,
                                 TunableKind.CellParameter, expr, unit, ov is null);
                    }
                    continue;
                }

                bool isSnp = inst.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase);
                if (isSnp) SnpEntries(inst, prefix, location, cell, count, readOnly);

                foreach (var ov in inst.Overrides)
                {
                    // An SnP's entry offsets are offered by SnpEntries, every one of them, stated or not.
                    if (isSnp && RfCore.SnpPerturbation.TryParseName(ov.Name, out _, out _, out _)) continue;
                    if (stored?.Invoke(cell, inst.InstanceName, ov.Name) == false)
                    {
                        notOffered[$"{prefix}{inst.InstanceName}.{ov.Name}"] =
                            "the netlist supplies it (a workspace technology's substrate, say); the drawing does not hold it";
                        continue;
                    }
                    Consider($"{prefix}{inst.InstanceName}.{ov.Name}", inst.InstanceName, ov.Name,
                             TunableKind.Parameter, ov.Expression, ov.Unit, isDefault: false);
                }
            }

            foreach (var v in vars)
                Consider(prefix + v.Name, v.Name, null, TunableKind.Variable, v.Expression, v.Unit, isDefault: false);

            // brief-sens-2 R-sens2-4: an SnP block's S-parameter entries, per its tie state — the lower triangle when
            // tied, all N² when untied — each a dB and a phase offset at 0 unless the instance states one.
            void SnpEntries(Instance inst, string prefix, string location, string? cell, int count, string? readOnly)
            {
                string owner = prefix + inst.InstanceName;
                string? Stated(string name) => inst.Overrides.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } o
                    ? Unquote(o.Expression) : null;
                int ports = int.TryParse(Stated("NumPorts"), System.Globalization.NumberStyles.Integer,
                                         System.Globalization.CultureInfo.InvariantCulture, out int np) ? np : 0;
                var mode = RfCore.SnpPerturbation.ParseReciprocal(Stated(RfCore.SnpPerturbation.ReciprocalParameter)) ?? RfCore.SnpReciprocal.Auto;

                string? loadError = null;
                RfCore.SNP? snp = null;
                string file = Elaborator.ResolveFileReference(Stated("File") ?? "", referenceBase);
                if (string.IsNullOrWhiteSpace(file)) loadError = "no Touchstone file is specified";
                else if (!File.Exists(file)) loadError = $"its Touchstone file was not found: '{file}'";
                else
                {
                    try { snp = RfCore.TouchstoneCache.Get(file); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
                    { loadError = $"its Touchstone file could not be read ({ex.Message})"; }
                }
                if (snp is not null && ports == 0) ports = snp.Matrices.Length > 0 ? snp.Matrices[0].RowCount : 0;

                if (snp is null)
                {
                    // Every key the block would offer is named, with the load's own sentence.
                    for (int i = 1; i <= ports; i++)
                        for (int j = 1; j <= ports; j++)
                            foreach (var kind in new[] { RfCore.SnpOffsetKind.Db, RfCore.SnpOffsetKind.Deg })
                                notOffered[$"{owner}.{RfCore.SnpPerturbation.ParameterName(kind, i, j)}"] = $"{owner}: {loadError}";
                    snpInstances.Add(new SnpInstanceInfo(owner, location, cell, ports, null, "", 0, loadError, null, mode));
                    return;
                }

                var (tied, rec) = RfCore.SnpPerturbation.ResolveTie(snp, mode);
                string reason = mode switch
                {
                    RfCore.SnpReciprocal.Tied   => "stated",
                    RfCore.SnpReciprocal.Untied => "stated",
                    _ => $"auto: worst |S(i,j) − S(j,i)| = {rec.Worst.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)}",
                };
                int entries = 0;
                for (int i = 1; i <= ports; i++)
                    for (int j = 1; j <= ports; j++)
                    {
                        if (tied && i < j) continue;
                        foreach (var kind in new[] { RfCore.SnpOffsetKind.Db, RfCore.SnpOffsetKind.Deg })
                        {
                            string name = RfCore.SnpPerturbation.ParameterName(kind, i, j);
                            string unit = kind == RfCore.SnpOffsetKind.Db ? "dB" : "deg";
                            double span = kind == RfCore.SnpOffsetKind.Db ? 1 : 10;
                            var ov = inst.Overrides.FirstOrDefault(o => o.Name == name);
                            double value = 0;
                            if (ov is not null && TunableValue.InUnit(TunableValue.Text(ov.Expression, ov.Unit), unit) is { } v) value = v;
                            else if (ov is not null)
                            {
                                notOffered[$"{owner}.{name}"] = $"its value '{TunableValue.Text(ov.Expression, ov.Unit)}' is not a plain number — tune the variable it reads instead";
                                continue;
                            }
                            tunables.Add(new Tunable(
                                $"{owner}.{name}", location, cell, count, inst.InstanceName, name, TunableKind.SnpEntry,
                                TunableValue.Format(value, unit, "G6"), value, unit, false, ov is null, readOnly, null,
                                TunableValue.Format(value - span, unit, "G6"), TunableValue.Format(value + span, unit, "G6"), false,
                                SnpEntry: new SnpEntryInfo(owner, i, j, kind, ports, tied, reason)));
                            entries++;
                        }
                    }
                snpInstances.Add(new SnpInstanceInfo(owner, location, cell, ports, tied, reason, entries, null, rec, mode));
            }

            void Consider(string key, string owner, string? parameter, TunableKind kind,
                          string expression, string? unit, bool isDefault)
            {
                string name = parameter ?? owner;
                if (name.StartsWith("__", StringComparison.Ordinal)) return;   // circuitRF plumbing, not a value

                string text = TunableValue.Text(expression, unit);
                if (parameter is not null && IdentityParameters.Contains(parameter))
                {
                    notOffered[key] = $"{parameter} is a number that identifies, not a value to tune";
                    return;
                }
                string? disabled = cell is null && kind == TunableKind.Variable && swept.TryGetValue(owner, out var sweep)
                    ? $"swept by {sweep}" : null;

                if (!TunableValue.TryParse(text, out double number, out string displayUnit, out _))
                {
                    if (ComplexValue.TryParse(text, out var z, out string zUnit, out var form))
                    {
                        // Never offered whole: each part is a tunable of its own (overview D18).
                        notOffered[key] = $"its value is complex — tune {TunableKey.PartKey(key, ComplexPart.Real)}, "
                            + $"{TunableKey.PartKey(key, ComplexPart.Imag)}, {TunableKey.PartKey(key, ComplexPart.Mag)} "
                            + $"or {TunableKey.PartKey(key, ComplexPart.Phase)}";
                        foreach (var part in Enum.GetValues<ComplexPart>())
                        {
                            double v = ComplexValue.Get(z, part);
                            string u = part == ComplexPart.Phase ? "deg" : zUnit;
                            var (lo, hi, guess) = TunableValue.DefaultPartRange(part, v, u);
                            tunables.Add(new Tunable(
                                TunableKey.PartKey(key, part), location, cell, count, owner, parameter, kind,
                                TunableValue.Format(v, u, "G6"), v, u, false, isDefault, readOnly, disabled,
                                lo, hi, guess, part, key, z, zUnit, form));
                        }
                        return;
                    }
                    // A distribution is an expression (tuning D1) — and the one a user is most likely to ask about,
                    // since its nominal is a number: it varies in a Monte Carlo trial, and only a plain value tunes.
                    notOffered[key] = Evaluator.ContainsStatisticalCall(expression)
                        ? $"its value '{text}' is a distribution, which varies in a Monte Carlo trial and is not tuned — " +
                          "write its nominal as a plain value with a tolerance (dist=) to tune and vary it"
                        : $"its value '{text}' is not a plain number — tune the variable it reads instead";
                    return;
                }

                var (min, max, guessed) = TunableValue.DefaultRange(number, displayUnit);

                tunables.Add(new Tunable(
                    key, location, cell, count, owner, parameter, kind, text, number, displayUnit,
                    parameter is not null && IntegerParameters.Contains(parameter), isDefault,
                    readOnly, disabled, min, max, guessed));
            }
        }
    }

    /// <summary>A placed vendor part's one tunable: the part number as its value text, the stated value as its
    /// number, the candidates' value span as its default range.</summary>
    private static Tunable PartTunable(CircuitRF.Design.VendorLibraries.VendorPartTunable part, string location,
                                       string? cell, int count, string? readOnly)
    {
        string unit = part.Unit;
        var values = part.Candidates.Select(c => c.Value).OfType<double>().ToList();
        string min = values.Count > 0 ? TunableValue.Format(values.Min(), unit, "G6") : "";
        string max = values.Count > 0 ? TunableValue.Format(values.Max(), unit, "G6") : "";
        return new Tunable(part.Key, location, cell, count, part.Instance, TunableKey.PartParameter, TunableKind.Part,
                           part.PlacedPart, part.Placed.Header.Value ?? 0, unit, false, false, readOnly, null,
                           min, max, false, VendorPart: part);
    }

    /// <summary>
    /// A placed vendor part's value as a statistical tunable (brief vendorlib-5 R-vl5-3a): <c>C1.C</c> for a
    /// capacitance, <c>C1.L</c> for an inductance — the spelling an ideal component uses — with the stated value as
    /// its nominal. Null for a part of another quantity, or one whose header states no value.
    /// </summary>
    private static Tunable? PartValueTunable(CircuitRF.Design.VendorLibraries.VendorPartTunable part, string prefix,
                                             string location, string? cell, int count, Dictionary<string, string> notOffered)
    {
        string? letter = part.Quantity switch
        {
            CircuitRF.Design.VendorLibraries.VendorPartQuantity.Capacitance => "C",
            CircuitRF.Design.VendorLibraries.VendorPartQuantity.Inductance  => "L",
            _ => null,
        };
        if (letter is null) return null;
        string key = $"{prefix}{part.Instance}.{letter}";
        if (part.Placed.Header.Value is not { } value || value <= 0)
        {
            notOffered[key] = $"{part.PlacedPart}'s header states no value";
            return null;
        }
        // In pF / nH — the units a part's value and its tolerance letters are written in — not base SI.
        string unit = PartValueUnit(letter);
        double inUnit = value / (Units.Scale(unit) ?? 1);
        var (min, max, guessed) = TunableValue.DefaultRange(inUnit, unit);
        return new Tunable(key, location, cell, count, part.Instance, letter, TunableKind.PartValue,
                           TunableValue.Format(inUnit, unit, "G6"), inUnit, unit, false, false,
                           "a vendor part's value is its part's; change the part to change it", null,
                           min, max, guessed, PartEntry: part.Placed, ValueOf: part);
    }

    /// <summary>The unit a part's value tunable is in: <c>pF</c> for a capacitance, <c>nH</c> for an inductance.</summary>
    public static string PartValueUnit(string letter) => letter == "L" ? "nH" : "pF";

    /// <summary>How many instances of each cell the flattened hierarchy holds. Cells no instance
    /// reaches are absent.</summary>
    private static Dictionary<string, int> InstanceCounts(TestBench tb, Library lib)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Walk(tb.Instances, 1, 0);
        return counts;

        void Walk(IEnumerable<Instance> instances, int multiplicity, int depth)
        {
            if (depth > 64) return;   // extraction refuses a cycle; this only guards a hand-written one
            foreach (var inst in instances)
            {
                if (lib.Find(inst.Reference) is not { } cell) continue;
                counts[cell.Name] = counts.GetValueOrDefault(cell.Name) + multiplicity;
                Walk(cell.Instances, multiplicity, depth + 1);
            }
        }
    }

    /// <summary>Why Push cannot write into a cell (overview D2), or null when it can.</summary>
    private static string? ReadOnlyReason(string cell, IReadOnlyDictionary<string, string>? cellKeys, string? workspaceRoot)
    {
        if (cellKeys is null) return null;
        if (!cellKeys.TryGetValue(cell, out var folder))
            return "it is a kit or library cell, defined by a netlist rather than a schematic";

        if (workspaceRoot is { Length: > 0 } && Path.IsPathFullyQualified(folder))
        {
            string root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(folder).StartsWith(root, StringComparison.Ordinal))
                return "it belongs to another workspace";
        }

        string schematicDir = CellFolder.SubFolderPath(folder, ViewType.Schematic);
        if (Directory.Exists(schematicDir)
            && Directory.EnumerateFiles(schematicDir, "*.csch")
                        .Any(f => File.GetAttributes(f).HasFlag(FileAttributes.ReadOnly)))
            return "its schematic file is read-only";

        return null;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }
}
