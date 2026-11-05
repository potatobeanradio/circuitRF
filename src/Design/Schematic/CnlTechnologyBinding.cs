using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// A hand-written <c>.cnl</c>, read the way a schematic is extracted: its microstrip and via
/// instances take their substrate from the workspace technology, and its MMIC passives (AA-1) their
/// process.
///
/// <para><b>Why this exists.</b> A schematic's microstrip resolves its substrate at extraction
/// (<see cref="MicrostripSubstrateInjection"/>, <see cref="ViaSubstrateInjection"/>), so the
/// <c>.cnl</c> a schematic becomes already states <c>H</c>, <c>Er</c> and the rest. A <c>.cnl</c>
/// written by hand did not go through extraction, and before this its <c>SignalLayer="Metal1"</c> was
/// accepted, resolved against nothing, and simulated on the models' standalone default — 1.6 mm of
/// εr 4.4 — inside a GaAs workspace, with <c>check</c> reporting nothing. The only sign was a
/// validity note about W/h. That is a converged, plausible, wrong answer, the failure the whole
/// automation surface exists to prevent.</para>
///
/// <para><b>The rule is the schematic's rule, applied to whatever the line leaves unstated.</b> A
/// substrate parameter the line states is the line's. One it leaves unstated follows the
/// technology — the netlist's own <c>technology "&lt;path&gt;"</c> statement when it has one (brief-artsch-6
/// R-as6-1), else the same walk-up a schematic in that folder would use (<see cref="SchematicTechnology"/>). The layer-choice
/// parameters (<c>SignalLayer</c>, <c>GroundReference</c>, <c>FromLayer</c>, <c>ToLayer</c>,
/// <c>GroundLayer</c>) are resolution INPUTS, so they are consumed here and never reach the engine,
/// exactly as the extractor drops them. A line that already states every substrate parameter, which
/// includes everything a schematic's extraction writes, is left exactly as it is.</para>
///
/// <para><b>Naming a layer with no technology to resolve it in is a refusal, not a fallback.</b> The
/// author asked for a stackup and a default substrate is not one. A layer the technology does not
/// have falls back to its default signal layer WITH A WARNING, exactly as a schematic's does, so a
/// netlist means the same thing whichever of the two it was written as. A line that named no layer,
/// outside any technology, keeps the standalone default it always had.</para>
///
/// <para>What this decided reaches <see cref="TestBench.ReadNotes"/> and
/// <see cref="TestBench.ReadWarnings"/>, which the elaborator forwards, so <c>check</c>, a run verb
/// and the Messages pane all report it.</para>
/// </summary>
public static class CnlTechnologyBinding
{
    private static readonly string[] MicrostripSubstrate = ["H", "T", "Er", "Sigma", "TanD"];
    private static readonly string[] MicrostripLayers    = ["SignalLayer", "GroundReference"];

    /// <summary>
    /// <see cref="CnlReader.ReadFile"/> plus the binding — what every consumer that SIMULATES a
    /// <c>.cnl</c> file reads it with, so a netlist means the same thing headless and in the window.
    /// </summary>
    public static (Library Library, TestBench TestBench) ReadFile(string path, string testBenchName = "tb")
        => ReadFile(path, out _, testBenchName);

    /// <inheritdoc cref="ReadFile(string, string)"/>
    /// <param name="parts">The vendor parts its <c>SpiceModel</c> lines place, as tunables (<c>C1.Part</c>) — what a
    /// schematic's extraction hands a run in <see cref="NetExtractor.ExtractionResult.PartTunables"/> (EMB-5 R-emb5-13).</param>
    public static (Library Library, TestBench TestBench) ReadFile(
        string path, out IReadOnlyList<VendorLibraries.VendorPartTunable> parts, string testBenchName = "tb")
    {
        var (lib, tb) = CnlReader.ReadFile(path, testBenchName);
        parts = Bind(lib, tb, Path.GetDirectoryName(Path.GetFullPath(path)));
        return (lib, tb);
    }

    /// <summary>Binds every microstrip and via instance in <paramref name="tb"/> and in every cell
    /// <paramref name="lib"/> defines, resolving the technology from <paramref name="directory"/> — and first
    /// emits every <c>SpiceModel</c> line, a SPICE model or a vendor part, exactly as a schematic's SpiceModel
    /// component is emitted (<see cref="NetExtractor.ResolveNetlistSpiceModels"/>, EMB-5 R-emb5-13). Returns the
    /// placed vendor parts as tunables.</summary>
    /// <exception cref="InvalidOperationException">An instance names a layer that cannot be
    /// resolved, or a <c>SpiceModel</c> line cannot be emitted.</exception>
    public static IReadOnlyList<VendorLibraries.VendorPartTunable> Bind(Library lib, TestBench tb, string? directory)
    {
        var parts = NetExtractor.ResolveNetlistSpiceModels(lib, tb, directory);

        var cellNames = new HashSet<string>(lib.Cells.Select(c => c.Name), StringComparer.Ordinal);
        var context   = new Context(directory, cellNames, tb);

        // brief-artsch-6 R-as6-1: a netlist that NAMES its technology is bound to it, ahead of the walk-up —
        // and one that names a file that is not there is refused, whether or not any line needs it, because
        // the alternative is the workspace default's substrate under a statement that says otherwise.
        if (tb.Technology is { Length: > 0 } && context.Resolution.Error is { } error)
            throw new InvalidOperationException(error);

        BindAll(tb.Instances, context);
        foreach (var cell in lib.Cells) BindAll(cell.Instances, context);
        return parts;
    }

    private static void BindAll(List<Instance> instances, Context context)
    {
        for (int i = 0; i < instances.Count; i++)
            if (BindOne(instances[i], context) is { } bound)
                instances[i] = bound;
    }

    /// <summary>The instance with its substrate bound, or null when it is not one this applies to or
    /// already states everything.</summary>
    private static Instance? BindOne(Instance inst, Context context)
    {
        // A cell the netlist defines wins over a primitive of the same spelling, as it does in the
        // elaborator, so a user cell called "ML" is never mistaken for a microstrip line.
        if (context.CellNames.Contains(inst.Reference)) return null;
        if (!ComponentTypeRegistry.TryParseCode(inst.Reference, out var kind, out _)) return null;

        if (MmicPassiveInjection.IsMmicKind(kind)) return BindMmic(inst, kind, context);
        if (kind == SymbolKind.TestPad) return BindTestPad(inst, context);
        if (PlanarLineSubstrateInjection.IsPlanarLineKind(kind)) return BindPlanarLine(inst, kind, context);

        bool microstrip = MicrostripSubstrateInjection.IsMicrostripKind(kind);
        bool via        = ViaSubstrateInjection.IsViaKind(kind);
        if (!microstrip && !via) return null;

        string[] layerParams = microstrip ? MicrostripLayers : [.. ViaSubstrateInjection.LayerParams];
        var stated = new HashSet<string>(inst.Overrides.Select(o => o.Name), StringComparer.Ordinal);
        string? Text(string name) => inst.Overrides.LastOrDefault(o => o.Name == name)?.Expression is { } e
                                     && Unquote(e) is { Length: > 0 } t ? t : null;

        var namedLayers = layerParams.Where(p => Text(p) is not null).ToArray();
        // "H" is the barrel length on a via and the substrate height on a line: a line that states it
        // has stated its substrate, which is what an extracted .cnl always does.
        bool fullyStated = microstrip ? MicrostripSubstrate.All(stated.Contains) : stated.Contains("H");

        var kept = inst.Overrides.Where(o => !layerParams.Contains(o.Name)).ToList();
        string label = $"{inst.Reference}:{inst.InstanceName}";

        if (fullyStated)
        {
            if (namedLayers.Length == 0) return null;
            context.Tb.ReadNotes.Add(
                $"{label}: {string.Join(" and ", namedLayers)} not used — the line states its own substrate.");
            return Rebuilt(inst, kept);
        }

        var tech = context.Technology;
        IReadOnlyList<ParameterAssignment> resolved;
        var messages = new List<string>();

        if (microstrip)
        {
            resolved = MicrostripSubstrateInjection.BuildOverrides(
                tech, out var warning, Text("SignalLayer"), Text("GroundReference"));
            if (warning is not null) messages.Add(warning);
        }
        else
        {
            var editable = inst.Overrides.Select(o => new EditableParameter { Name = o.Name, Expression = Unquote(o.Expression) });
            var injection = ViaSubstrateInjection.Build(tech, kind, editable);
            resolved = injection.Span is null ? [] : injection.Overrides;
            messages.AddRange(injection.Messages);
        }

        if (resolved.Count == 0)
        {
            // Nothing resolved. Naming a layer asked for a stackup, and the default is not one.
            if (namedLayers.Length > 0)
            {
                string why = tech is null
                    ? "no technology resolves for this netlist (it is not inside a workspace, or the workspace names no default technology)"
                    : messages.FirstOrDefault() ?? "the technology cannot resolve it";
                string substrate = microstrip ? "H, T, Er, Sigma and TanD" : "H, Er and Sigma";
                throw new InvalidOperationException(
                    $"{label} names {Describe(namedLayers, Text)}, but {why}. Put the netlist in a workspace whose technology has that layer, or state {substrate} on the line.");
            }

            // A line that named nothing, with no technology: the standalone default, as it always was.
            // Inside a technology that could not resolve one, that default is worth a warning.
            if (tech is not null)
                foreach (var m in messages) context.Tb.ReadWarnings.Add($"{label}: {m}");
            return namedLayers.Length == 0 && kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);
        }

        var added = resolved.Where(r => !stated.Contains(r.Name)).ToList();
        kept.AddRange(added);

        foreach (var m in messages) context.Tb.ReadWarnings.Add($"{label}: {m}");
        if (added.Count > 0)
            context.Tb.ReadNotes.Add(
                $"{label}: substrate from the technology {context.TechnologyName} — " +
                string.Join(", ", added.Select(a => $"{a.Name}={a.Expression}")) + ".");

        return Rebuilt(inst, kept);
    }

    /// <summary>
    /// An MMIC passive (AA-1): the schematic's rule again, with no layer to name. Whatever process
    /// number the line leaves unstated follows the technology; a line stating them all is left alone;
    /// outside a technology the standalone default stands, and inside one that lacks what the part
    /// needs, the run says which piece is missing.
    /// </summary>
    private static Instance? BindMmic(Instance inst, SymbolKind kind, Context context)
    {
        var stated = new HashSet<string>(inst.Overrides.Select(o => o.Name), StringComparer.Ordinal);
        string label = $"{inst.Reference}:{inst.InstanceName}";

        // A TFR's Film is a word for this step and never reaches the numeric layer. A name no film answers is a refusal,
        // because the first film would be a different resistor with nothing on the line saying so.
        string? film = kind == SymbolKind.Tfr
            ? inst.Overrides.LastOrDefault(o => o.Name == MmicPassiveInjection.FilmParam)?.Expression is { } e
              && Unquote(e) is { Length: > 0 } named ? named : null
            : null;
        var kept = kind == SymbolKind.Tfr
            ? inst.Overrides.Where(o => o.Name != MmicPassiveInjection.FilmParam).ToList()
            : [.. inst.Overrides];

        if (MmicPassiveInjection.InjectedNames(kind).All(stated.Contains))
        {
            if (kept.Count == inst.Overrides.Count) return null;
            if (film is not null) context.Tb.ReadNotes.Add($"{label}: Film not used — the line states its own process.");
            return Rebuilt(inst, kept);
        }

        var tech = context.Technology;
        if (tech is null)
        {
            if (film is not null)
                throw new InvalidOperationException(
                    $"{label} names the film '{film}', but no technology resolves for this netlist (it is not inside a workspace, " +
                    "or the workspace names no default technology). Put the netlist in a workspace whose technology has that " +
                    $"film, or state {string.Join(", ", MmicPassiveInjection.InjectedNames(kind))} on the line.");
            return kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);
        }

        if (MmicPassiveInjection.FilmRefusal(tech, film) is { } refused)
            throw new InvalidOperationException($"{label} is refused: {refused}.");

        var (resolved, warning) = MmicPassiveInjection.Build(tech, kind, film);
        if (warning is not null) context.Tb.ReadWarnings.Add($"{label}: {warning}");
        var added = resolved.Where(r => !stated.Contains(r.Name)).ToList();
        if (added.Count == 0) return kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);

        context.Tb.ReadNotes.Add(
            $"{label}: process from the technology {context.TechnologyName} — " +
            string.Join(", ", added.Select(a => $"{a.Name}={a.Expression}")) + ".");
        return Rebuilt(inst, [.. kept, .. added]);
    }

    /// <summary>
    /// A test pad (brief-dfr26-7): the schematic's rule. A line that states <c>Hpad</c> has stated its stackup and
    /// is left alone; otherwise its layer words are consumed and what the technology resolves is appended. Outside
    /// a technology the standalone default stands, and naming a layer there is a refusal, as for a via.
    /// </summary>
    private static Instance? BindTestPad(Instance inst, Context context)
    {
        string[] inputs = [TestPadInjection.PadLayerParam, TestPadInjection.ViaFromParam, TestPadInjection.ClearLayersParam,
                           "Feed", "FeedW", "Centre", "PortNum"];
        var stated = new HashSet<string>(inst.Overrides.Select(o => o.Name), StringComparer.Ordinal);
        var kept = inst.Overrides.Where(o => !inputs.Contains(o.Name)).ToList();
        string label = $"{inst.Reference}:{inst.InstanceName}";
        if (stated.Contains("Hpad"))
            return kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);

        var tech = context.Technology;
        string? Text(string name) => inst.Overrides.LastOrDefault(o => o.Name == name)?.Expression is { } e
                                     && Unquote(e) is { Length: > 0 } t ? t : null;
        if (tech is null)
        {
            if (Text(TestPadInjection.PadLayerParam) is not null || Text(TestPadInjection.ViaFromParam) is not null)
                throw new InvalidOperationException(
                    $"{label} names a layer, but no technology resolves for this netlist (it is not inside a workspace, or"
                    + " the workspace names no default technology). Put the netlist in a workspace whose technology has that"
                    + " layer, or state Hpad, Erpad and Hvia on the line.");
            return kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);
        }

        var editable = inst.Overrides.Select(o => new EditableParameter { Name = o.Name, Expression = Unquote(o.Expression) });
        var injection = TestPadInjection.Build(tech, editable);
        foreach (var m in injection.Messages) context.Tb.ReadWarnings.Add($"{label}: {m}");
        var added = injection.Overrides.Where(r => !stated.Contains(r.Name)).ToList();
        kept.AddRange(added);
        if (added.Count > 0)
            context.Tb.ReadNotes.Add(
                $"{label}: stackup from the technology {context.TechnologyName} — " +
                string.Join(", ", added.Select(a => $"{a.Name}={a.Expression}")) + ".");
        return Rebuilt(inst, kept);
    }

    /// <summary>
    /// CPWG and SLIN (brief-artsch-1): the microstrip rule, with SLIN's one difference — inside a technology,
    /// a SLIN that cannot be given a plane on each side is refused whether or not it named its layer,
    /// because the fallback board it would otherwise simulate on is a stripline the design does not have.
    /// </summary>
    private static Instance? BindPlanarLine(Instance inst, SymbolKind kind, Context context)
    {
        var layerParams = PlanarLineSubstrateInjection.LayerParams(kind);
        var stated = new HashSet<string>(inst.Overrides.Select(o => o.Name), StringComparer.Ordinal);
        string? Text(string name) => inst.Overrides.LastOrDefault(o => o.Name == name)?.Expression is { } e
                                     && Unquote(e) is { Length: > 0 } t ? t : null;
        var namedLayers = layerParams.Where(p => Text(p) is not null).ToArray();
        var kept = inst.Overrides.Where(o => !layerParams.Contains(o.Name)).ToList();
        string label = $"{inst.Reference}:{inst.InstanceName}";

        if (PlanarLineSubstrateInjection.InjectedNames(kind).All(stated.Contains))
        {
            if (namedLayers.Length == 0) return null;
            context.Tb.ReadNotes.Add($"{label}: {string.Join(" and ", namedLayers)} not used — the line states its own substrate.");
            return Rebuilt(inst, kept);
        }

        var tech = context.Technology;
        var binding = PlanarLineSubstrateInjection.Build(tech, kind, Text("SignalLayer"),
                                                         kind == SymbolKind.Cpwg ? Text("GroundReference") : null);
        if (binding.Refusal is { } refused)
            throw new InvalidOperationException($"{label} is refused: {refused}.");

        if (binding.Overrides.Count == 0)
        {
            if (namedLayers.Length > 0)
                throw new InvalidOperationException(
                    $"{label} names {Describe(namedLayers, Text)}, but {(tech is null ? "no technology resolves for this netlist (it is not inside a workspace, or the workspace names no default technology)" : binding.Warnings.FirstOrDefault() ?? "the technology cannot resolve it")}. " +
                    $"Put the netlist in a workspace whose technology has that layer, or state {string.Join(", ", PlanarLineSubstrateInjection.InjectedNames(kind))} on the line.");
            if (tech is not null)
                foreach (var m in binding.Warnings) context.Tb.ReadWarnings.Add($"{label}: {m}");
            return kept.Count == inst.Overrides.Count ? null : Rebuilt(inst, kept);
        }

        var added = binding.Overrides.Where(r => !stated.Contains(r.Name)).ToList();
        kept.AddRange(added);
        foreach (var m in binding.Warnings) context.Tb.ReadWarnings.Add($"{label}: {m}");
        if (added.Count > 0)
            context.Tb.ReadNotes.Add(
                $"{label}: substrate from the technology {context.TechnologyName} — " +
                string.Join(", ", added.Select(a => $"{a.Name}={a.Expression}")) + ".");
        return Rebuilt(inst, kept);
    }

    private static Instance Rebuilt(Instance inst, List<ParameterAssignment> overrides)
        => new(inst.InstanceName, inst.Reference, inst.NetBindings, overrides) { RefNetBinding = inst.RefNetBinding };

    private static string Describe(string[] names, Func<string, string?> text)
        => string.Join(" and ", names.Select(n => $"{n} '{text(n)}'"));

    /// <summary>A layer name may be written quoted (<c>SignalLayer="Metal1"</c>); the quotes are the
    /// netlist's, not the name's.</summary>
    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    /// <summary>The technology is resolved once per netlist, and only when an instance needs it — the netlist's
    /// own <c>technology</c> statement first, then the workspace walk (<see cref="SchematicTechnology"/>).</summary>
    private sealed class Context(string? directory, HashSet<string> cellNames, TestBench tb)
    {
        private SchematicTechResolution? _resolution;

        public HashSet<string> CellNames { get; } = cellNames;
        public TestBench Tb { get; } = tb;

        public SchematicTechResolution Resolution => _resolution ??= SchematicTechnology.Resolve(directory, Tb.Technology);

        public Technology? Technology => Resolution.Technology;

        public string TechnologyName =>
            Resolution.Path is { } p
                ? $"'{Path.GetFileNameWithoutExtension(p)}'"
                : "of this workspace";
    }
}
