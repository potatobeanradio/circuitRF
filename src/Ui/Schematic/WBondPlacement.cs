using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Placing a <c>.wBond</c> design into a schematic, and keeping a placed one honest about the design
/// it was wired against (wbond.md §9.2 route 2, brief-wbond-wbb2 M2/M3).
///
/// <para>Framework-free (no Avalonia): every decision that can be wrong lives here rather than in a
/// menu handler, so it is testable without a window.</para>
/// </summary>
public static class WBondPlacement
{
    /// <summary>The parameter that records the array list an instance's wiring was drawn against.</summary>
    public const string ArraysParameter = "Arrays";

    /// <summary>The parameter that CARRIES the design (<see cref="WBondEmbedding"/>).</summary>
    public const string DesignParameter = WBondEmbedding.DesignParameter;

    /// <summary>The parameter that declares where this instance's wires come from (WB45).</summary>
    public const string SourceParameter = "Source";

    /// <summary>The parameter naming the linked <c>.wBond</c>, relative to the schematic.</summary>
    public const string FileParameter = "File";

    // ── WB45: Carried or Linked ───────────────────────────────────────────────

    /// <summary>
    /// Which of a placed wBond's two wire sources the next Run simulates (<c>wbond.md</c> §9.7/WB45).
    ///
    /// <para><b>Carried, not Embedded.</b> §9.1 already spends <i>embedded</i> and <i>referenced</i> on
    /// a different axis — whether a <c>.wBond</c> file embeds the layout artwork it was drawn over, or
    /// references cells by path. That axis is about what is inside the file; this one is about where a
    /// placed component's WIRES come from. The two are independent, and reusing the words made them
    /// indistinguishable. <i>Carried</i> is §5.0's own verb.</para>
    /// </summary>
    public enum WireSource
    {
        /// <summary>
        /// Today's behaviour and still the portable one: the wires travel inside the schematic, so
        /// there is no path to break and nothing to resolve. §5.0/WB17b governs this case.
        /// </summary>
        Carried,

        /// <summary>
        /// The netlist names the cell's <c>.wBond</c> by a path relative to the schematic. ONE copy of
        /// the wires, so staleness becomes unrepresentable rather than reported — which is what §9.5's
        /// layout-driven flow wants. The cost is a "Not Found" state and the drift check of §3.2.
        /// </summary>
        Linked,
    }

    /// <summary>
    /// A placed instance's declared wire source. <b>Carried is the default for anything that does not
    /// say</b> — every schematic written before WB45, and every instance that has never been through
    /// Update Layout from Schematic.
    /// </summary>
    public static WireSource SourceOf(EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(comp);

        string value = comp.Parameters.FirstOrDefault(p => p.Name == SourceParameter)?.Expression ?? "";
        return value.Equals(nameof(WireSource.Linked), StringComparison.OrdinalIgnoreCase)
            ? WireSource.Linked
            : WireSource.Carried;
    }

    /// <summary>The stored link path — relative to the schematic — or null when there is none.</summary>
    public static string? LinkedPathOf(EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(comp);

        string value = comp.Parameters.FirstOrDefault(p => p.Name == FileParameter)?.Expression ?? "";
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// The linked <c>.wBond</c>'s absolute path, resolved against the SCHEMATIC's own directory.
    ///
    /// <para><b>Relative to the schematic, exactly as §4 of <c>workspace-and-project-tree.md</c>
    /// resolves a cell reference</b> — an absolute path stored in a document breaks on every other
    /// machine, and a workspace-relative one breaks when the cell folder is moved as a unit. A cell's
    /// own wires live at <c>../layout/&lt;cell&gt;.wBond</c> from its schematic, which travels.</para>
    ///
    /// <para>Null when nothing is linked, or when there is no schematic directory to resolve against —
    /// a scratch schematic that has never been saved. An already-absolute stored value is honoured, so
    /// a hand-edited one still works.</para>
    /// </summary>
    public static string? ResolveLinkedPath(EditableComponent comp, string? schematicDirectory)
    {
        if (LinkedPathOf(comp) is not { } stored) return null;
        if (Path.IsPathRooted(stored)) return stored;
        if (string.IsNullOrEmpty(schematicDirectory)) return null;

        // Tolerate a Windows-authored separator so a schematic ports across operating systems — the
        // same allowance the elaborator's own SnP path resolution makes.
        return Path.GetFullPath(Path.Combine(schematicDirectory, stored.Replace('\\', '/')));
    }

    /// <summary>
    /// Points an instance at a <c>.wBond</c> and flips it to <see cref="WireSource.Linked"/>
    /// (WB45a) — the ONE place that transition happens, and it is called from a command the user can
    /// see (<c>WBondCellSeeding</c>).
    ///
    /// <para>The path is stored RELATIVE to the schematic when one is known, and absolute otherwise;
    /// see <see cref="ResolveLinkedPath"/> for why.</para>
    /// </summary>
    /// <returns>The stored (usually relative) path.</returns>
    public static string LinkTo(EditableComponent comp, string absolutePath, string? schematicDirectory)
    {
        ArgumentNullException.ThrowIfNull(comp);

        string stored = absolutePath;
        if (!string.IsNullOrEmpty(schematicDirectory))
        {
            // Always forward slashes in the stored value: it is read on whatever machine opens the
            // document next, and Path.Combine accepts '/' on every platform circuitRF runs on.
            stored = Path.GetRelativePath(schematicDirectory, absolutePath).Replace('\\', '/');
        }

        SetParameter(comp, SourceParameter, nameof(WireSource.Linked));
        SetParameter(comp, FileParameter, stored);
        return stored;
    }

    // ── Building the component ────────────────────────────────────────────────

    /// <summary>What went wrong, phrased for the user, or null when a component was built.</summary>
    public sealed record BuildResult(EditableComponent? Component, string? Error);

    /// <summary>
    /// Builds a placeable wBond component carrying the design read from <paramref name="absolutePath"/>.
    ///
    /// <para><b>The wires are EMBEDDED, not referenced</b> — <see cref="WBondEmbedding.Encode"/>, which
    /// deliberately drops any layout artwork the file carried. A schematic component has nowhere to
    /// put artwork, and a reference to a file that mostly holds artwork is what produced the
    /// "Not Found" placeholder on every freshly-placed wBond. Artwork travels by
    /// <c>AddWBondAsCell</c>, which makes it a real layout view.</para>
    ///
    /// <para><b>M1 — a design with no arrays is refused BY NAME, not placed with no pins.</b>
    /// <c>WBondSymbolGenerator.Build</c> returns null for such a design, so the component would have
    /// nothing to wire and nothing to stamp; a silent placeholder in the middle of a schematic is a
    /// worse answer than saying which file and why.</para>
    /// </summary>
    public static BuildResult TryBuild(string absolutePath, string? workspaceRootDir, string instanceName)
    {
        _ = workspaceRootDir;   // nothing is stored as a path any more; kept so callers need no change

        if (string.IsNullOrWhiteSpace(absolutePath))
            return new BuildResult(null, "No wirebond design was named.");

        if (!File.Exists(absolutePath))
            return new BuildResult(null, $"Wirebond design not found: \"{absolutePath}\".");

        WBondDesign design;
        try
        {
            design = WBondIo.ReadFile(absolutePath);
        }
        catch (Exception ex)
        {
            return new BuildResult(null,
                $"\"{Path.GetFileName(absolutePath)}\" could not be read as a wirebond design: {ex.Message}");
        }

        if (design.Arrays.Count == 0)
            return new BuildResult(null,
                $"\"{Path.GetFileName(absolutePath)}\" declares no wire arrays, so it has no pins and " +
                "cannot be placed. Group its wires into at least one array first.");

        return new BuildResult(BuildCarrying(design, instanceName), null);
    }

    /// <summary>
    /// Builds a wBond component carrying <paramref name="design"/>. With no design supplied it gets
    /// the shipped one-array, one-wire default — <b>at the user's own Wire z-height</b> (Settings ▸
    /// Wirebonds), which is why the payload is written here rather than left as the registry's
    /// cached <c>DefaultPayload</c> string: that one is computed once per process and cannot know a
    /// preference. At the shipped 4 mil the two are byte-identical.
    /// </summary>
    public static EditableComponent BuildCarrying(WBondDesign? design, string instanceName)
    {
        var comp = new EditableComponent
        {
            InstanceName = instanceName,
            Symbol       = SymbolKind.WBond,
        };

        var info = ComponentTypeRegistry.Get(SymbolKind.WBond);
        comp.ShowTypeLabel    = info.DefaultShowTypeLabel;
        comp.ShowInstanceName = info.DefaultShowInstanceName;

        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });

        ApplyDesign(comp, design ?? WBondEmbedding.DefaultDesign(WBondDefaults.FootZNm));
        return comp;
    }

    /// <summary>
    /// Writes a design onto an existing component — the one place an import lands.
    ///
    /// <para><c>Arrays</c> is updated in step, so the recorded identity always describes the payload
    /// that is actually there. Whether the arrays MOVED is the caller's question, asked BEFORE this
    /// runs (<see cref="DriftBetween"/>).</para>
    /// </summary>
    public static void ApplyDesign(EditableComponent comp, WBondDesign design)
    {
        ArgumentNullException.ThrowIfNull(comp);
        ArgumentNullException.ThrowIfNull(design);

        SetParameter(comp, DesignParameter, WBondEmbedding.Encode(design));
        SetParameter(comp, ArraysParameter, WBondSymbolProvider.ArraysKeyOf(design));

        // The design's own capacitance flag becomes this instance's parameter — the relationship the
        // wBond editor's toolbar toggle depends on. The toggle belongs to the EDITOR's readout and the
        // parameter to a PLACED component; they are two different things, and this one moment of
        // inheritance is the whole connection between them. A document placed twice can then be given
        // two different settings without either changing the other.
        SetParameter(comp, "IncludeCapacitance", design.IncludeCapacitance ? "true" : "false");
    }

    private static void SetParameter(EditableComponent comp, string name, string value)
    {
        var p = comp.Parameters.FirstOrDefault(q => q.Name == name);
        if (p is not null) p.Expression = value;
        else comp.Parameters.Add(new EditableParameter { Name = name, Expression = value });
    }

    /// <summary>
    /// A free spot to drop a component into: clear of everything already placed, snapped to the
    /// connection grid. Deliberately crude — a placement gesture that starts from a menu has no
    /// cursor to follow, and landing on top of an existing component would be worse than landing
    /// somewhere the user has to drag it from.
    /// </summary>
    public static (double X, double Y) SuggestPlacementPoint(SchematicEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Components.Count == 0) return (0, 0);

        double maxX = model.Components.Max(c => c.X);
        double minY = model.Components.Min(c => c.Y);
        return (model.SnapToGrid(maxX + 800), model.SnapToGrid(minY));
    }

    // ── §5.5.1/WB44: reading the controlling parameters off a placed instance ─

    /// <summary>
    /// What a placed instance's controlling parameters say, reduced to the units
    /// <see cref="ControllingParameters"/> works in.
    /// </summary>
    /// <param name="Overrides">Lengths in metres and material names, ready to apply.</param>
    /// <param name="Unbakeable">
    /// Parameters that are EXPRESSIONS rather than numbers, phrased for reporting. A <c>VAR</c>
    /// reference is the whole point of these being sweepable, and it is exactly why it has no single
    /// value to draw — so it is named rather than having a number invented for it.
    /// </param>
    public sealed record ControllingParameterRead(
        WBondOverrides Overrides, IReadOnlyList<string> Unbakeable);

    /// <summary>
    /// Reads the controlling parameters (<c>wbond.md</c> §5.5.1/WB44) off a placed component, for the
    /// two surfaces that need the EFFECTIVE geometry rather than the drawn geometry: Update Layout
    /// from Schematic, and the parameter panel's own one-line summary.
    ///
    /// <para><b>This is not the run path.</b> A Run goes through the elaborator, which resolves every
    /// expression against the design's real variable scope; this sees only the instance's own text and
    /// so handles literals. Both reduce to the same <see cref="WBondOverrides"/>, and the geometry
    /// beneath them is one implementation, which is what keeps a written <c>.wBond</c> and the next
    /// netlist from disagreeing about what "30 mil" is.</para>
    /// </summary>
    public static ControllingParameterRead ReadControllingParameters(EditableComponent comp)
    {
        ArgumentNullException.ThrowIfNull(comp);

        var overrides = new WBondOverrides();
        var unbakeable = new List<string>();

        foreach (var p in comp.Parameters)
        {
            bool isLength = MatchesControlling(p.Name, "LoopHeight") || MatchesControlling(p.Name, "Diameter");
            bool isName   = MatchesControlling(p.Name, "Material");

            if (!isLength && !isName) continue;
            if (string.IsNullOrWhiteSpace(p.Expression)) continue;   // unset — as drawn

            if (isName) { overrides.SetName(p.Name, p.Expression); continue; }

            if (TryLiteralMetres(p, out double metres)) overrides.SetLength(p.Name, metres);
            else unbakeable.Add($"{p.Name} = '{p.Expression}'");
        }

        return new ControllingParameterRead(overrides, unbakeable);
    }

    /// <summary>
    /// Writes the layout's own measured loop height, diameter and material back into the controlling
    /// parameters that are SET — the schematic half of <b>Update Schematic from Layout</b>.
    ///
    /// <h3>The reported bug (owner, 2026-08-17)</h3>
    /// <para><i>"I changed the loop height in layout using the Array Inductance double-click on loop
    /// height. Then I did an Update Schematic from Layout, but the loop height was not updated in the
    /// schematic."</i></para>
    ///
    /// <para>The reconcile brought the GEOMETRY back into the payload and left the override alone, so
    /// the dialog went on showing the old number — <b>and the next Run applied that old number back
    /// over the geometry that had just been imported</b>, silently undoing the command. That is §2.0's
    /// "schematic wins" behaving as specified, arriving immediately after a command whose entire
    /// purpose was to make the schematic match the layout.</para>
    ///
    /// <h3>Only what is already SET, and only when it is a literal</h3>
    /// <list type="bullet">
    ///   <item><b>An unset parameter stays unset.</b> Blank means "as drawn", and the payload now
    ///     carries what was drawn — writing a number here would invent an override the user never
    ///     asked for, on every array, on every reconcile.</item>
    ///   <item><b>An expression is never overwritten.</b> <c>LoopHeight_G1 = loopH</c> is the handle a
    ///     sweep turns; replacing it with a literal would silently retire the sweep. It is reported
    ///     with the measured value instead, so the user can decide.</item>
    ///   <item><b>Wires that disagree are reported, not averaged.</b> Individually dragged wires can
    ///     leave an array with no single loop height, and inventing one would state something about
    ///     the layout that is not true of it.</item>
    /// </list>
    /// </summary>
    public static void WriteBackControllingParameters(
        List<EditableParameter> parameters, WBondDesign fromLayout, List<string> messages)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(fromLayout);
        ArgumentNullException.ThrowIfNull(messages);

        foreach (var array in fromLayout.Arrays)
            WriteBackScope(parameters, array.Name, array.Wires, messages);

        // The unsuffixed forms govern every array at once, so they can only be written back when the
        // whole design agrees on one value.
        WriteBackScope(parameters, null, [.. fromLayout.AllWires()], messages);
    }

    private static void WriteBackScope(
        List<EditableParameter> parameters, string? arrayName, IReadOnlyList<Wire> wires,
        List<string> messages)
    {
        if (wires.Count == 0) return;

        string scope = arrayName is null ? "" : "_" + arrayName;
        string where = arrayName is null ? "the design" : $"array '{arrayName}'";

        WriteBackLength(parameters, "LoopHeight" + scope, where,
            wires.Select(w => w.LoopHeightNm), messages);

        WriteBackLength(parameters, "Diameter" + scope, where,
            wires.Select(w => w.DiameterNm), messages);

        var param = parameters.FirstOrDefault(p => p.Name == "Material" + scope);
        if (param is not null && !string.IsNullOrWhiteSpace(param.Expression))
        {
            var metals = wires.Select(w => w.Material).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (metals.Count > 1)
                messages.Add(
                    $"{param.Name} was left at '{param.Expression}': the wires in {where} no longer " +
                    $"share one metal ({string.Join(", ", metals)}).");
            else if (!metals[0].Equals(param.Expression.Trim(), StringComparison.OrdinalIgnoreCase))
                param.Expression = metals[0];
        }
    }

    private static void WriteBackLength(
        List<EditableParameter> parameters, string name, string where,
        IEnumerable<long> valuesNm, List<string> messages)
    {
        var param = parameters.FirstOrDefault(p => p.Name == name);
        if (param is null || string.IsNullOrWhiteSpace(param.Expression)) return;   // unset stays unset

        var distinct = valuesNm.Distinct().ToList();

        // A parameter that is an EXPRESSION is the handle a sweep turns — reported, never replaced.
        if (!double.TryParse(param.Expression.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            messages.Add(
                $"{name} is an expression ('{param.Expression}'), so the layout's own value was not " +
                $"written over it. {where} now measures " +
                (distinct.Count == 1 ? FormatIn(distinct[0], param.Unit) : "more than one value") + ".");
            return;
        }

        if (distinct.Count > 1)
        {
            messages.Add(
                $"{name} was left at '{param.Expression}': the wires in {where} no longer share one " +
                $"value ({string.Join(", ", distinct.Select(v => FormatIn(v, param.Unit)))}).");
            return;
        }

        string written = FormatValueIn(distinct[0], param.Unit);
        if (!written.Equals(param.Expression.Trim(), StringComparison.Ordinal)) param.Expression = written;
    }

    /// <summary>
    /// A DBU length expressed in a parameter's OWN unit, so the dialog goes on showing "15" in mil
    /// rather than 0.000381 in metres. A blank unit means the value is in metres, matching the engine's
    /// own convention for a wBond length.
    /// </summary>
    private static string FormatValueIn(long nm, string? unit)
    {
        double metres = WBondUnits.ToMetres(nm);
        string engine = UnitNormalizer.ToEngineUnit(unit ?? "");

        double scale = engine.Length == 0 || engine == "None" ? 1.0 : Units.Scale(engine) ?? 1.0;
        return (metres / scale).ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static string FormatIn(long nm, string? unit)
    {
        string engine = UnitNormalizer.ToEngineUnit(unit ?? "");
        string suffix = engine.Length == 0 || engine == "None" ? " m" : " " + engine;
        return FormatValueIn(nm, unit) + suffix;
    }

    /// <summary>Matches <c>&lt;parameter&gt;</c> and <c>&lt;parameter&gt;_&lt;array&gt;</c>.</summary>
    private static bool MatchesControlling(string name, string parameter) =>
        name.Equals(parameter, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(parameter + "_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A plain numeric literal in the row's own unit, reduced to metres — the same reduction the
    /// expression engine performs on this parameter on its way to the elaborator.
    ///
    /// <para>A blank unit means the value is already in metres, matching the engine's own convention
    /// for a wBond length (<c>LoopHeight=0.000762</c> in a hand-authored <c>.cnl</c>). An unrecognised
    /// unit is a refusal to guess rather than a silent fallback to 1.0 — that is the phantom-scale trap
    /// recorded in <c>src/Core/CLAUDE.md</c>.</para>
    /// </summary>
    private static bool TryLiteralMetres(EditableParameter p, out double metres)
    {
        metres = 0.0;

        if (!double.TryParse(p.Expression.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                             out double value))
            return false;

        string unit = UnitNormalizer.ToEngineUnit(p.Unit ?? "");
        if (unit.Length == 0 || unit == "None") { metres = value; return true; }

        if (Units.Scale(unit) is not { } scale) return false;

        metres = value * scale;
        return true;
    }

    // ── Parameter ORDER, which is label order on the symbol ───────────────────

    /// <summary>
    /// The controlling parameters a per-array row can write, in the order the panel shows them.
    /// </summary>
    private static readonly string[] PerArrayControls = ["LoopHeight", "Diameter", "Material"];

    /// <summary>
    /// Puts a wBond's parameters into a canonical order — <b>which is the order they are RENDERED on
    /// the symbol.</b>
    ///
    /// <h3>The reported bug (owner, 2026-08-17)</h3>
    /// <para><i>"When I create G1, G2, G3 arrays in the Component Parameters dialog, the symbol
    /// rendering lists them as LoopHeight_G2, LoopHeight_G1, LoopHeight_G3."</i></para>
    ///
    /// <para>Labels are built by walking <c>Parameters</c> in list order
    /// (<c>EditableComponent.BuildRenderModel</c>), and a per-array override is APPENDED when its box
    /// is first committed — so the on-symbol order was the order the user's focus happened to visit the
    /// boxes in. Nothing was wrong with it; nothing made it right either. Sorting the list itself rather
    /// than sorting at render time is what makes the dialog, the symbol, the <c>.csch</c> and the
    /// netlist all agree, instead of one of them being re-sorted on the way out.</para>
    ///
    /// <para>Order: everything the registry declares, in registry order; then one group per array in
    /// ARRAY order — which is pin order, and is what the dialog lists — each group being loop height,
    /// diameter, material; then anything else, keeping its relative position. Only the last group can
    /// contain something this code has never heard of, and leaving those alone is what keeps this a
    /// sort rather than a filter.</para>
    /// </summary>
    public static List<EditableParameter> InCanonicalOrder(IEnumerable<EditableParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var list = parameters.ToList();
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int next = 0;

        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.WBond, 0))
            rank[dp.Name] = next++;

        // The array list is read from the `Arrays` RECORD rather than by decoding the payload: it is
        // the same string the symbol's own pin order is generated from (WBondSymbolProvider.RefFor),
        // so the two cannot disagree, and it costs no base64 decode on a path that runs per edit.
        foreach (string array in ArrayNamesOf(list))
            foreach (string control in PerArrayControls)
                rank[$"{control}_{array}"] = next++;

        return [.. list
            .Select((p, index) => (p, key: rank.TryGetValue(p.Name, out int r) ? r : int.MaxValue, index))
            .OrderBy(t => t.key)
            .ThenBy(t => t.index)      // stable within a rank, so unranked entries keep their order
            .Select(t => t.p)];
    }

    /// <summary>The array names an instance records, in order. Empty when it records none.</summary>
    private static string[] ArrayNamesOf(IEnumerable<EditableParameter> parameters) =>
        (parameters.FirstOrDefault(p => p.Name == ArraysParameter)?.Expression ?? "")
            .Split('|', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Renames an array's controlling parameters with the array, and <b>drops the ones whose array no
    /// longer exists</b>.
    ///
    /// <para>Both halves are silent-wrong-answer guards rather than tidiness. A rename that left
    /// <c>LoopHeight_G2</c> behind would leave an override that no longer reaches anything — it stops
    /// applying, with the value still sitting in the dialog and still drawn on the symbol. A removed
    /// array that left its override behind would draw a label for a pin pair that is not on the symbol
    /// any more.</para>
    /// </summary>
    /// <param name="arrayNames">The array names that exist AFTER the edit.</param>
    /// <param name="renamedFrom">The array's previous name, when this edit was a rename.</param>
    /// <param name="renamedTo">Its new name.</param>
    public static List<EditableParameter> ReconcilePerArrayParameters(
        IEnumerable<EditableParameter> parameters, IReadOnlyCollection<string> arrayNames,
        string? renamedFrom = null, string? renamedTo = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(arrayNames);

        var live = new HashSet<string>(arrayNames, StringComparer.OrdinalIgnoreCase);
        var kept = new List<EditableParameter>();

        foreach (var p in parameters)
        {
            var (control, array) = SplitPerArray(p.Name);
            if (control is null || array is null)
            {
                kept.Add(p);
                continue;
            }

            if (renamedFrom is not null && renamedTo is not null
                && array.Equals(renamedFrom, StringComparison.OrdinalIgnoreCase))
            {
                p.Name = $"{control}_{renamedTo}";
                kept.Add(p);
                continue;
            }

            if (live.Contains(array)) kept.Add(p);
        }

        return kept;
    }

    /// <summary>Splits <c>LoopHeight_G1</c> into its control and its array, or (null, null).</summary>
    private static (string? Control, string? Array) SplitPerArray(string name)
    {
        foreach (string control in PerArrayControls)
        {
            if (name.Length > control.Length + 1
                && name.StartsWith(control + "_", StringComparison.OrdinalIgnoreCase))
                return (control, name[(control.Length + 1)..]);
        }
        return (null, null);
    }

    // ── §5 question 3: the array list an instance was wired against ───────────

    /// <summary>
    /// One placed wBond whose referenced design's array list has changed since the instance was
    /// placed (or since the user last acknowledged a change).
    /// </summary>
    /// <param name="InstanceName">The placed component, so the user can find it.</param>
    /// <param name="Source">What the new arrays came from, for the message — usually a file name.</param>
    /// <param name="Recorded">The array list the wiring was drawn against.</param>
    /// <param name="Current">The array list the incoming design declares.</param>
    public sealed record ArrayDrift(string InstanceName, string Source, string Recorded, string Current)
    {
        /// <summary>
        /// True when the arrays are the same set in a different order — the case that silently
        /// re-points wiring, because every pin keeps its position while its NAME moves to a
        /// different row.
        /// </summary>
        public bool IsReorder =>
            Recorded.Split('|').OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(Current.Split('|').OrderBy(s => s, StringComparer.Ordinal));

        /// <summary>The message, naming the remedy rather than only the problem.</summary>
        public string Message =>
            IsReorder
                ? $"wBond '{InstanceName}': the arrays in \"{Source}\" are REORDERED relative to what "
                  + $"this instance was wired against ({Recorded} → {Current}). Every pin keeps its "
                  + "position while its name moves, so the wires now connect to different arrays. "
                  + "Check the wiring."
                : $"wBond '{InstanceName}': the array list changed on import from \"{Source}\" "
                  + $"({Recorded} → {Current}), so its pins have moved. Check the wiring.";
    }

    /// <summary>
    /// Whether an incoming design's arrays differ from what a placed instance was wired against.
    ///
    /// <para><b>This is the whole of M2's silent-failure guard, and reporting is the answer rather
    /// than repair.</b> A wBond's pin ORDER is its array order — that is R-wbb2-2's contract with
    /// <c>WBondModel</c>'s stamp — so a reorder genuinely moves pins and there is no re-mapping that
    /// keeps existing wires correct without moving the artwork the user drew. What must not happen
    /// is that it happens quietly.</para>
    ///
    /// <para>Now that the design travels INSIDE the component, this is an import-time question rather
    /// than a load-time one: nothing external can change under a placed instance, so the only moment
    /// its pins can move is the moment new wires are imported over them. Call it BEFORE
    /// <see cref="ApplyDesign"/>.</para>
    ///
    /// <para>An instance with no recorded list (hand-authored, or placed before this existed) yields
    /// null: nothing is known about what it was wired against, and a warning that cannot be acted on
    /// is noise.</para>
    /// </summary>
    public static ArrayDrift? DriftBetween(EditableComponent comp, WBondDesign incoming, string source)
    {
        ArgumentNullException.ThrowIfNull(comp);
        ArgumentNullException.ThrowIfNull(incoming);

        string recorded = comp.Parameters.FirstOrDefault(p => p.Name == ArraysParameter)?.Expression ?? "";
        if (string.IsNullOrWhiteSpace(recorded)) return null;

        string current = WBondSymbolProvider.ArraysKeyOf(incoming);
        if (string.Equals(current, recorded, StringComparison.Ordinal)) return null;

        return new ArrayDrift(comp.InstanceName, source, recorded, current);
    }
}
