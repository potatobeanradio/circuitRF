namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// Maps a generator id to its <see cref="PCellGenerator"/>. §0/guardrails of
/// brief-L5a-pcell-contract-and-microstrip.md: "no scripting host, no PDK loading, no third-party
/// PCell mechanism" — this is a small closed, built-in dispatcher (mirrors
/// <c>ComponentModelFactory</c>'s registry shape), not an extensibility point. The generator id is
/// the same string used as the microstrip <c>ComponentModel</c>'s type name in
/// <c>ComponentModelFactory</c> ("MLIN", "MBEND", "MTEE", "MCROSS"), so the artwork and electrical
/// sides of one component are keyed identically.
/// </summary>
public static class PCellRegistry
{
    private static readonly Dictionary<string, PCellGenerator> _generators =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "MLIN",   MlinPCell.Generate   },
            { "MBEND",  MBendPCell.Generate  },
            { "MTEE",   MTeePCell.Generate   },
            { "MCROSS", MCrossPCell.Generate },
            { "MTAPER", MTaperPCell.Generate },
            { "MKLOPF", MKlopfPCell.Generate },
        };

    // ── Resolvers: generators that are not built in ────────────────────────────
    //
    // The built-in dictionary above is closed and stays closed. A generator supplied from outside —
    // a script beside a kit — arrives through a RESOLVER instead, asked only when the id is not a
    // built-in and cached under that id once it answers.
    //
    // **This one seam is what makes an out-of-process cell indistinguishable from a built-in
    // everywhere above it.** The geometry cache, the content-addressed cell store, copy-on-write
    // parameter editing, the regeneration snapshots and both schematic↔layout directions all reach a
    // generator through TryGet and none of them needed changing. It is the same arrangement
    // ExternalDeviceRegistry already has for devices, and for the same reason.

    private static readonly List<Wire.IPCellGeneratorResolver> _resolvers = [];
    private static readonly Dictionary<string, PCellGenerator> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _resolverGate = new();

    /// <summary>
    /// Registers a resolver. Registration starts nothing: a resolver is asked the first time a
    /// design actually names a generator it might own.
    /// </summary>
    public static void AddResolver(Wire.IPCellGeneratorResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_resolverGate)
        {
            if (!_resolvers.Contains(resolver)) _resolvers.Add(resolver);
            _resolved.Clear(); // a new resolver may answer for an id that previously resolved to nothing
        }
    }

    /// <summary>
    /// Drops every resolver and everything they produced — called when the workspace they belong to
    /// is left, and at process exit.
    ///
    /// <para><b>Nothing here disposes a resolver</b>: this registry did not create them and does not
    /// own their processes. The owner (a workspace) disposes its own, which is what actually ends
    /// the interpreters; forgetting that leaves a generator running with nothing to talk to.</para>
    /// </summary>
    public static void ClearResolvers()
    {
        lock (_resolverGate)
        {
            _resolvers.Clear();
            _resolved.Clear();
        }
    }

    /// <summary>
    /// Forgets what the resolvers previously answered, without dropping the resolvers themselves.
    ///
    /// <para>Needed when a resolver's own answer legitimately changes mid-session — today, when a kit
    /// is granted permission to run and its generators become available where a moment ago they were
    /// not. The cached delegate would otherwise keep pointing at the pre-change answer (or at a
    /// provider that has since been disposed).</para>
    /// </summary>
    public static void InvalidateResolved()
    {
        lock (_resolverGate) _resolved.Clear();
    }

    /// <summary>Every id currently offered, built-in and resolved. May start a generator.</summary>
    public static IReadOnlyCollection<string> AllKnownGeneratorIds()
    {
        var ids = new HashSet<string>(_generators.Keys, StringComparer.OrdinalIgnoreCase);
        lock (_resolverGate)
            foreach (var resolver in _resolvers)
                try { foreach (string id in resolver.KnownGeneratorIds) ids.Add(id); }
                catch { /* a broken kit must not make the others unlistable */ }
        return ids;
    }

    /// <summary>
    /// A generator's own declared parameter defaults, or null when nothing declares any.
    ///
    /// <para><b>Only script-backed generators answer here.</b> A built-in's defaults live with the
    /// component that declares it (<c>ComponentTypeRegistry.DefaultParameters</c>, reached through
    /// <c>SchematicToLayoutGenerator.ResolveDefaultParameters</c>) and are keyed by
    /// <c>SymbolKind</c>, not by generator id — asking for them here would mean a second, reverse
    /// mapping that could disagree with the forward one.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
    {
        if (string.IsNullOrEmpty(generatorId)) return null;
        lock (_resolverGate)
            foreach (var resolver in _resolvers)
                try { if (resolver.DeclaredDefaults(generatorId) is { } d) return d; }
                catch { /* a broken kit must not make the others unusable */ }
        return null;
    }

    public static bool TryGet(string generatorId, out PCellGenerator generator)
    {
        if (_generators.TryGetValue(generatorId, out generator!)) return true;
        if (string.IsNullOrEmpty(generatorId)) return false;

        lock (_resolverGate)
        {
            if (_resolved.TryGetValue(generatorId, out generator!)) return true;

            foreach (var resolver in _resolvers)
            {
                PCellGenerator? found;
                // A resolver that throws is one broken kit; it must not make every OTHER kit's cells
                // unresolvable. Its own reporting has already said what went wrong.
                try { found = resolver.Resolve(generatorId); }
                catch (Wire.PCellWireException) { continue; }

                if (found is null) continue;
                _resolved[generatorId] = found;
                generator = found;
                return true;
            }
        }

        generator = null!;
        return false;
    }

    /// <summary>The BUILT-IN ids only. Kept as-is because the callers that use it are asking "is this
    /// one of ours" — see <see cref="AllKnownGeneratorIds"/> for the full list.</summary>
    public static IReadOnlyCollection<string> KnownGeneratorIds => _generators.Keys;

    /// <summary>
    /// Content-addressing version for a generator's OWN geometry algorithm (independent of the
    /// pcell-contract.md "PCellContractVersion" and of the parameters/technology already in the
    /// hash key) — see <see cref="GeneratedCellStore.GetOrCreate"/>'s doc comment for why this
    /// exists: without it, fixing a generator's algorithm (e.g. a coordinate-sign bug) never
    /// invalidates an already-written on-disk generated cell that was created with the OLD, buggy
    /// output, because <c>(GeneratorId, parameters, tech, layers)</c> alone is unchanged. Bump the
    /// entry for a generator whenever its geometry OUTPUT changes for some existing parameter set
    /// (not for a change that only affects NEW parameters/behavior). Defaults to 1 for any
    /// generator not listed here.
    /// </summary>
    private static readonly Dictionary<string, int> _generatorVersions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // R-L5f-5 (brief-L5-followups.md §2): branch direction flipped from +Y to -Y.
            { "MTEE", 2 },
        };

    public static int GeneratorVersion(string generatorId)
        => _generatorVersions.TryGetValue(generatorId, out var v) ? v : 1;

    /// <summary>
    /// What goes into a generated cell's content hash to represent the GENERATOR itself.
    ///
    /// <para>For a built-in that is <see cref="GeneratorVersion"/> as text, and the text is
    /// deliberately identical to what the hash carried before this existed — so every generated cell
    /// in every existing workspace keeps its name, and every already-placed instance keeps
    /// resolving. For a resolved generator it is a hash of the script's own content, because a
    /// generator that is a FILE THE USER EDITS cannot have a hand-maintained version number: the
    /// number has to be the thing itself.</para>
    ///
    /// <para>A resolver with no answer falls back to the built-in default, which is the same "1"
    /// an unversioned built-in has always contributed.</para>
    /// </summary>
    public static string GeneratorContentKey(string generatorId)
    {
        if (_generators.ContainsKey(generatorId))
            return GeneratorVersion(generatorId).ToString(System.Globalization.CultureInfo.InvariantCulture);

        lock (_resolverGate)
            foreach (var resolver in _resolvers)
            {
                string? key;
                try { key = resolver.ContentKeyFor(generatorId); }
                catch (Wire.PCellWireException) { continue; }
                if (key is { Length: > 0 }) return key;
            }

        return GeneratorVersion(generatorId).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
