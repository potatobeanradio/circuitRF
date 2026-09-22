// A `.csch` becomes an LvsNetlist — brief-lvs-4-schematic-netlist.md, docs/design/lvs.md §5, §6.1.
//
//   SchematicEditModel ──► NetExtractor ──► CnlWriter ──► CnlReader ──► (Library, TestBench)
//                                │                                          │
//                                │ CellPorts                                ├─► Elaborator ─► values
//                                ▼                                          ▼
//                          BoundaryNets ◄──────────────────────────────  LvsNetlist
//
// ── FOUR THINGS THIS FILE DELIBERATELY DOES NOT DO ────────────────────────────────────────────
//
// It does NOT read a schematic its own way. The round trip is SchematicCircuit's, which is the
//   GUI's own Simulate; skipping it reports errors the application does not have (R-lvs4-1b).
// It does NOT restate the exclusion list. SchematicExclusions owns it and NetExtractor reads the
//   same one (R-lvs4-3b) — a component that is electrical to one and not the other produces a
//   mismatch whose cause is in NEITHER document.
// It does NOT compare the ELABORATED netlist. The elaborator flattens hierarchy and uniquifies
//   nets by instance path; LVS needs the hierarchy intact and needs to report in the user's own
//   names (R-lvs4-2a). What it takes from elaboration is resolved parameter VALUES and nothing
//   else (R-lvs4-2b).
// It does NOT write. LVS is read-only on `check`'s terms (R-aut4-6, R-lvs4-1c), so it runs on a
//   read-only tree and on a workspace another process has open.
//
// ── THE OUTPUT TYPE IS THE SAME ONE THE LAYOUT SIDE BUILDS, AND THAT IS THE POINT ─────────────
//
// R-lvs3-1a. There is no flag in LvsNetlist saying which side built it, and there must not be: a
// comparator that CAN tell will eventually treat the two differently, and the asymmetry is a bug
// nobody can see because both inputs look right and only the answer is wrong.

using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>The schematic half of the comparison: a drawing in, <see cref="LvsNetlist"/> out.</summary>
public static class SchematicRead
{
    /// <summary>
    /// Reads <paramref name="model"/> as a flat netlist of devices, terminals and nets.
    /// </summary>
    /// <param name="model">The schematic, as read.</param>
    /// <param name="cschPath">Where it was read from. A <c>CellRef</c> is relative to the folder
    /// holding it, and an SnP's <c>File</c> is relative to the WORKSPACE root — so with no path
    /// nothing resolves.</param>
    /// <param name="includeFixture">
    /// <c>--testbench</c> (R-lvs4-4c). False — the default — excludes <c>Port</c>, <c>Term</c>, the
    /// sources and the tuner family, and their nets become boundary nets; the unit of comparison is
    /// then the CELL, which is what a layout actually draws. True makes them devices, for the
    /// designer who has drawn the launches.
    /// </param>
    /// <param name="isTestBenchCell">Whether the owning <c>.ccell</c> sets <c>IsTestBench</c>, for
    /// R-lvs4-4d's info finding. Nothing else reads it.</param>
    /// <param name="overrides">
    /// <c>--set var=expr</c> (R-lvs11-2d) — globals REPLACED in the bench's own scope immediately
    /// before elaboration, so everything derived from one re-derives.
    ///
    /// <para><b>Here and not at the elaborator</b>, which is <c>cli.md</c> §5's rule in the shape it
    /// takes on this side: the scope is what an expression is evaluated in, so an override handed
    /// past it would change one value and leave every expression written in terms of it reading the
    /// old one.</para>
    /// </param>
    /// <param name="excludeInstances">
    /// Instances left out of the comparison ENTIRELY, by name — R-lvs13-4c's drifted wBond, and
    /// nothing else today.
    ///
    /// <para><b>Both sides drop it, which is the whole point.</b> A wBond whose array list has moved
    /// under it has every pin re-pointed while the drawn wiring stayed put: comparing its nets
    /// yields 2M findings that are individually true and collectively about the wrong thing, and
    /// dropping it on ONE side would merely turn that cascade into an unmatched part. One line
    /// naming the drift is the finding.</para>
    /// </param>
    public static LvsNetlist Read(
        SchematicEditModel model, string cschPath,
        bool includeFixture = false, bool isTestBenchCell = false,
        IReadOnlyList<LvsGlobalOverride>? overrides = null,
        IReadOnlySet<string>? excludeInstances = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        var notes = new List<Diagnostic>();
        string document  = Path.GetFileName(cschPath);
        string benchName = Path.GetFileNameWithoutExtension(cschPath);
        string? ownDir   = Path.GetDirectoryName(Path.GetFullPath(cschPath));

        // The base a CellRef resolves against. Set only when the caller has not — a model loaded
        // straight off disk carries none, and without it every cell instance is skipped silently,
        // which is the failure CircuitSource's own note records in the other direction.
        model.SchematicDirectory ??= ownDir;

        NetExtractor.ExtractionResult extracted;
        Library lib;
        TestBench tb;
        try
        {
            extracted = SchematicCircuit.Extract(model, benchName);
            (lib, tb) = SchematicCircuit.RoundTrip(
                SchematicCircuit.CnlTextOf(extracted, benchName),
                benchName, SchematicCircuit.ReferenceBaseOf(cschPath));
        }
        catch (Exception ex)
        {
            notes.Add(LvsDiagnostics.SchematicUnreadable(document, ex.Message));
            return LvsNetlist.Nothing(notes);
        }

        foreach (string conflict in extracted.Conflicts)
            notes.Add(LvsDiagnostics.ExtractionNote(conflict));

        // ── R-lvs4-2b/2c: the elaborator is asked for VALUES, and a failure is a refusal ───────
        //
        // Not a partial comparison. A design whose parameters do not resolve has no values to
        // compare AND its topology may depend on them — an if() in a cell parameter decides which
        // branch gets stamped. The elaborator's own sentence is carried unmodified.
        // R-lvs11-2d. Before the elaborator and after the round trip — the bench the round trip
        // produced is the scope every cell parameter and every component value is evaluated in.
        if (overrides is { Count: > 0 })
            foreach (var (name, expression) in overrides)
            {
                tb.GlobalVariables.RemoveAll(v => v.Name == name);
                tb.GlobalVariables.Add(new Variable(name, expression));
            }

        IReadOnlyDictionary<string, Resolved> resolved;
        try
        {
            // READ INSIDE THE using, not after it. An ElaboratedNetlist owns its device models, and
            // an external one owns a worker process — so the values and the terminal names are
            // copied out here rather than the components being kept and asked later.
            using var elaborated = new Elaborator(lib)
            {
                BaseDirectory = SchematicCircuit.ReferenceBaseOf(cschPath),
            }.Elaborate(tb);

            var byPath = new Dictionary<string, Resolved>(StringComparer.Ordinal);
            foreach (var ec in elaborated.Components)
                byPath.TryAdd(ec.InstancePath, new Resolved(ec.Model.TerminalNames, ValuesOf(ec)));
            resolved = byPath;
        }
        catch (Exception ex)
        {
            notes.Add(LvsDiagnostics.ElaborationFailed(document, ex.Message));
            return LvsNetlist.Nothing(notes);
        }

        // ── The devices ────────────────────────────────────────────────────────────────────────
        // R-lvs4-3b. The SAME list NetExtractor emitted against, asked again rather than assumed:
        // nothing excluded there can reach an instance here, and asking is what keeps that TRUE
        // rather than merely currently so. A VAR that reached this map would type a device.
        var components = new Dictionary<string, EditableComponent>(StringComparer.Ordinal);
        foreach (var comp in model.Components)
        {
            if (SchematicExclusions.IsExcluded(comp)) continue;
            components.TryAdd(comp.InstanceName, comp);
        }

        var nets    = new NetTable(tb, extracted.CellPorts);
        var devices = new List<LvsDevice>();
        var fixtures = new List<Instance>();

        foreach (var inst in tb.Instances)
        {
            // R-lvs13-4c. Not a device on either side; the drift is already reported.
            if (excludeInstances is not null && excludeInstances.Contains(inst.InstanceName)) continue;

            var comp = components.GetValueOrDefault(inst.InstanceName);
            var type = comp is null ? DeviceType.Unknown : DeviceTypes.OfSchematic(comp, model.SchematicDirectory);

            // R-lvs4-4c. Excluded, and REMEMBERED: their nets are what the boundary is made of.
            if (!includeFixture && type.Kind == DeviceKind.Fixture) { fixtures.Add(inst); continue; }

            int deviceIndex = devices.Count;
            var terminals = new List<LvsTerminal>(inst.NetBindings.Count + 1);
            var names = TerminalNamesOf(inst, lib, resolved);

            for (int k = 0; k < inst.NetBindings.Count; k++)
            {
                int net = nets.Of(inst.NetBindings[k]);
                terminals.Add(new LvsTerminal(k + 1, k < names.Count ? names[k] : "", net));
                nets.Attach(net, deviceIndex, terminals.Count - 1);
            }

            // R-lvs4-6c falls out of the loop above and needs nothing of its own: a wBond's 2M
            // pins arrive in array order because NetBindings IS the symbol's pin order, and their
            // names arrive from WBondModel.TerminalNames, which is generated from the array names.
            // Brief 13 is what finds their copper.
            //
            // R-lvs4-6b. The shared reference node an N-port carries under the N-or-N+1 rule is an
            // ORDINARY terminal, numbered N+1 and named REF. Dropping it would make an SnP
            // referenced to something other than ground compare as though it were grounded — which
            // is a circuit the designer did not draw, silently.
            if (inst.RefNetBinding is { Length: > 0 } refNet)
            {
                int net = nets.Of(refNet);
                terminals.Add(new LvsTerminal(inst.NetBindings.Count + 1, "REF", net));
                nets.Attach(net, deviceIndex, terminals.Count - 1);
            }

            var parameters = ParametersOf(inst, resolved);
            devices.Add(new LvsDevice(
                inst.InstanceName, comp?.InstanceName ?? inst.InstanceName, type, terminals,
                parameters,
                new LvsProvenance(document, inst.InstanceName,
                                  (long)Math.Round(comp?.X ?? 0), (long)Math.Round(comp?.Y ?? 0)))
            {
                ParameterFacts = FactsOf(comp, parameters),
            });
        }

        // ── The boundary: this cell's own ports, IN PORT ORDER ─────────────────────────────────
        var boundary = BoundaryOf(extracted.CellPorts, fixtures, nets);

        if (isTestBenchCell && !includeFixture && fixtures.Count > 0)
            notes.Add(LvsDiagnostics.TestBenchExcluded(document, fixtures.Count));

        return new LvsNetlist(devices, nets.Build(), boundary, notes);
    }

    // ── Boundary (R-lvs4-4a/b/c) ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The cell's ports, as nets, in port order.
    /// </summary>
    /// <remarks>
    /// <b>Cell ports first, and they are <c>NetExtractor.BuildCellPorts</c>' own order</b>
    /// (R-lvs4-4b) — sorted by the <c>Pin Num=</c> parameter, which is already the schematic's own
    /// answer about which port is which. Nothing here re-derives it, and nothing joins it to the
    /// layout POSITIONALLY: brief 1's terminal map is what relates the two.
    ///
    /// <para>With no <c>Pin</c> at all the schematic is a bench rather than a cell, and the
    /// boundary is what the excluded fixture was attached to (R-lvs4-4c) — a <c>Term Num=1</c> is
    /// the designer saying "port 1 is here" in the only vocabulary a bench has. Its ground return
    /// is not a port, so the first non-ground net is the one taken.</para>
    /// </remarks>
    private static IReadOnlyList<int> BoundaryOf(
        IReadOnlyList<string> cellPorts, List<Instance> fixtures, NetTable nets)
    {
        if (cellPorts.Count > 0) return [.. cellPorts.Select(nets.Of)];
        if (fixtures.Count == 0) return [];

        var ordered = fixtures
            .Select((f, order) => (Fixture: f, Num: PortNumberOf(f) ?? int.MaxValue, Order: order))
            .OrderBy(e => e.Num).ThenBy(e => e.Order);

        var boundary = new List<int>();
        foreach (var (fixture, _, _) in ordered)
        {
            string? net = fixture.NetBindings.FirstOrDefault(n => !IsGround(n));
            if (net is null) continue;
            int index = nets.Of(net);
            if (!boundary.Contains(index)) boundary.Add(index);
        }
        return boundary;
    }

    /// <summary>A fixture's declared <c>Num</c>, or null — the port number a bench states.</summary>
    private static int? PortNumberOf(Instance inst)
    {
        var num = inst.Overrides.FirstOrDefault(
            p => p.Name.Equals("Num", StringComparison.OrdinalIgnoreCase));
        return num is not null && int.TryParse(num.Expression, out int n) ? n : null;
    }

    private static bool IsGround(string net) => net == "0";

    // ── Terminals and values ─────────────────────────────────────────────────────────────────

    /// <summary>What elaboration answered about one instance, copied out before the netlist that
    /// owns the models is disposed.</summary>
    private sealed record Resolved(
        IReadOnlyList<string> TerminalNames, IReadOnlyDictionary<string, object?> Parameters);

    /// <summary>
    /// What to CALL each terminal. <b>Never what to match on</b> — R-lvs4-6a's ordering is, and a
    /// terminal's number is its position in <see cref="Instance.NetBindings"/>.
    /// </summary>
    /// <remarks>
    /// A cell instance takes its names from the cell's own <c>Ports</c>, which is
    /// <c>BuildCellPorts</c> again and therefore the same list the layout side's terminal map is
    /// keyed by. A primitive takes the model's <c>TerminalNames</c>, which is where "drain" and
    /// "gate" come from. Neither is required: an empty name is ordinary.
    /// </remarks>
    private static IReadOnlyList<string> TerminalNamesOf(
        Instance inst, Library lib, IReadOnlyDictionary<string, Resolved> resolved)
    {
        if (lib.Find(inst.Reference) is { } cell && cell.Ports.Count > 0) return cell.Ports;
        return resolved.TryGetValue(inst.InstanceName, out var r) ? r.TerminalNames : [];
    }

    /// <summary>
    /// The resolved parameter values, and <b>nothing else</b> taken from elaboration (R-lvs4-2b).
    /// </summary>
    /// <remarks>
    /// Empty for a cell INSTANCE, which the elaborator flattens away rather than resolving as a
    /// component of its own — its children carry the values, and reaching them is brief 9's
    /// hierarchy. Judging any of these is brief 10's; this only collects them.
    /// </remarks>
    private static IReadOnlyDictionary<string, object?> ParametersOf(
        Instance inst, IReadOnlyDictionary<string, Resolved> resolved)
        => resolved.TryGetValue(inst.InstanceName, out var r) ? r.Parameters : EmptyParameters;

    /// <summary>
    /// What each resolved value IS — its <see cref="UnitDimension"/>, from the component that
    /// declared the parameter (brief 10's R-lvs10-3a).
    /// </summary>
    /// <remarks>
    /// <b>The drawing is the side that always knows.</b> <c>EditableParameter.Dimension</c> is what
    /// drives the unit ComboBox beside the field, so every parameter a user can type into carries
    /// one; the artwork's side of a PCell is a snapshot of resolved numbers and says nothing about
    /// what they mean. Nothing is derived or unread here: a drawing computes nothing from its own
    /// geometry, which is exactly what makes those two flags a layout-side answer.
    /// </remarks>
    private static IReadOnlyDictionary<string, LvsParameterFact> FactsOf(
        EditableComponent? comp, IReadOnlyDictionary<string, object?> parameters)
    {
        if (comp is null || parameters.Count == 0) return EmptyFacts;

        var facts = new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);
        foreach (var p in comp.Parameters)
        {
            if (p.Dimension == UnitDimension.None) continue;
            if (!parameters.ContainsKey(p.Name)) continue;
            facts[p.Name] = new LvsParameterFact(p.Dimension);
        }
        return facts.Count > 0 ? facts : EmptyFacts;
    }

    private static readonly IReadOnlyDictionary<string, LvsParameterFact> EmptyFacts =
        new Dictionary<string, LvsParameterFact>(StringComparer.Ordinal);

    /// <summary>The kinded values as plain objects, on the same terms the layout side carries its
    /// <c>PCellOrigin</c> parameters — one dictionary shape, so brief 10 compares one way.</summary>
    private static IReadOnlyDictionary<string, object?> ValuesOf(ElaboratedComponent ec)
    {
        if (ec.Parameters.Count == 0) return EmptyParameters;

        var values = new Dictionary<string, object?>(ec.Parameters.Count, StringComparer.Ordinal);
        foreach (var (name, value) in ec.Parameters) values[name] = value.Kind switch
        {
            ValueKind.Real    => value.AsReal(),
            ValueKind.Complex => value.AsComplex(),
            ValueKind.Bool    => value.AsBool(),
            ValueKind.String  => value.AsString(),
            _                 => null,
        };
        return values;
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyParameters =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Net names in, <see cref="LvsNet"/> indices out — and the one place ground is index 0.
    /// </summary>
    /// <remarks>
    /// <b>Only an ANCHORED name becomes a label.</b> Every schematic net has a name, but most of
    /// them are auto-generated (<c>n7</c>) and mean nothing outside this one extraction. A label
    /// is what the comparison ANCHORS on, so carrying an auto-name as one would offer the
    /// comparison a name the artwork could never have and, worse, could coincidentally match.
    /// What is kept is ground, every user-placed net label (<c>TestBench.LabeledNets</c>, which is
    /// extraction's own provenance set) and every cell port name.
    /// </remarks>
    private sealed class NetTable
    {
        private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
        private readonly List<string?> _labels = [];
        private readonly List<List<(int Device, int Terminal)>> _pins = [];
        private readonly HashSet<string> _anchored;

        public NetTable(TestBench tb, IReadOnlyList<string> cellPorts)
        {
            _anchored = new HashSet<string>(tb.LabeledNets, StringComparer.Ordinal);
            foreach (string port in cellPorts) _anchored.Add(port);

            // Ground first where there is one, so "0" is index 0 whenever it exists — the same
            // small determinism the layout side has, and what makes a report's net numbers
            // readable side by side.
            foreach (var inst in tb.Instances)
            {
                if (inst.NetBindings.Any(IsGround) || inst.RefNetBinding == "0") { Of("0"); break; }
            }
        }

        public int Of(string name)
        {
            if (_byName.TryGetValue(name, out int existing)) return existing;

            _labels.Add(IsGround(name) || _anchored.Contains(name) ? name : null);
            _pins.Add([]);
            return _byName[name] = _labels.Count - 1;
        }

        public void Attach(int net, int device, int terminal) => _pins[net].Add((device, terminal));

        public IReadOnlyList<LvsNet> Build() =>
            [.. Enumerable.Range(0, _labels.Count).Select(i => new LvsNet(i, _labels[i], _pins[i]))];
    }
}
