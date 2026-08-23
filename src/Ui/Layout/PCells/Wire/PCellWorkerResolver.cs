namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>Asked for a generator nobody has registered. See <see cref="PCellRegistry"/>.</summary>
public interface IPCellGeneratorResolver
{
    /// <summary>
    /// Produce a generator for <paramref name="generatorId"/>, or null for "no opinion" — which is
    /// the ordinary answer and must not be an error: several resolvers may be registered and all but
    /// one of them will not know any given id.
    /// </summary>
    PCellGenerator? Resolve(string generatorId);

    /// <summary>Every id this resolver can currently answer for, for listing without asking by
    /// name. May start a generator to find out.</summary>
    IReadOnlyCollection<string> KnownGeneratorIds { get; }

    /// <summary>Where this resolver looked, for a message when it found nothing.</summary>
    string Describe();

    /// <summary>
    /// <paramref name="generatorId"/>'s own declared parameter defaults, or null for an id this
    /// resolver does not own. What makes a cell PLACEABLE without the caller already knowing its
    /// parameters — see <see cref="PCellWireParameterDecl.Default"/>.
    /// </summary>
    IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId) => null;

    /// <summary>
    /// Everything <paramref name="generatorId"/> declares about its parameters — kinds, defaults,
    /// labels, enumerations, bounds — or null for an id this resolver does not own.
    ///
    /// <para>A superset of <see cref="DeclaredDefaults"/>, which stays because it answers the
    /// narrower question ("what do I place this with") that every caller but the parameter editor
    /// asks, and answering it through this would make placement depend on display metadata.</para>
    /// </summary>
    IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId) => null;

    /// <summary>
    /// A string that changes whenever <paramref name="generatorId"/>'s own definition changes, folded
    /// into the key that names a generated cell. Null for an id this resolver does not own.
    ///
    /// <para>Without it, editing a generator leaves every cell it already produced on disk and in
    /// use — the exact failure the built-ins' hand-maintained version number exists to prevent, with
    /// a larger blast radius because a script is edited far more often than a built-in.</para>
    /// </summary>
    string? ContentKeyFor(string generatorId);
}

/// <summary>A kit declared under the workspace: where its manifest is, and the script that would run.</summary>
public sealed record PCellKit(string Directory, string EntryScript)
{
    /// <summary>What to call it in a prompt — the kit's own folder name.</summary>
    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));
}

/// <summary>
/// Finds generator scripts declared under a workspace and starts one the first time a design
/// actually asks for one of its cells.
///
/// <para><b>Resolvers are registered at workspace open; providers are not — so opening a workspace
/// starts no processes.</b> A workspace may reference several kits and a given design typically uses
/// cells from none of them. This is the same arrangement <c>DeviceWorkerProviderResolver</c> already
/// has, and for the same reason: the alternative is an interpreter per kit spawned on every open.</para>
/// </summary>
public sealed class PCellWorkerResolver : IPCellGeneratorResolver, IDisposable
{
    private readonly string _rootDirectory;
    private readonly Func<PCellGeneratorManifest, string, PythonInterpreter?> _findInterpreter;
    private readonly Action<string>? _report;
    private readonly Func<string, PCellTrustDecision>? _trust;
    private readonly Lock _gate = new();

    private readonly List<(string Directory, PCellGeneratorManifest Manifest)> _manifests = [];
    private readonly List<PCellWorkerProvider> _providers = [];
    private Dictionary<string, PCellWorkerProvider>? _byGeneratorId;
    private bool _disposed;

    /// <param name="rootDirectory">Workspace root — searched itself and one level down, matching the
    /// device manifest's own search shape. Deliberately shallow: the further out it goes, the less
    /// the territory has to do with the kit, and it would eventually match by accident.</param>
    /// <param name="findInterpreter">How to turn a manifest into an interpreter to run. A seam, so
    /// that real discovery (a later phase) replaces the default without touching this class.</param>
    /// <param name="report">Where a problem with a manifest goes. Null discards — a headless caller
    /// has nowhere to put it, and a resolver must not be the thing that fails an open.</param>
    /// <param name="trust">
    /// Whether this installation has agreed to run a given kit's scripts. <b>Anything other than
    /// <see cref="PCellTrustDecision.Allowed"/> means the kit is never started</b> — including
    /// <c>Unknown</c>, so a kit nobody has been asked about does not run while the asking happens.
    ///
    /// <para>Null means the CALLER HAS NO POLICY and everything declared runs. That default exists for
    /// headless callers (tests, a future CLI) which have no user to ask and no place to record an
    /// answer; the application always supplies a real policy, and
    /// <c>PCellTrustGateTests.ResetPCellGenerators_AlwaysPassesATrustPolicy</c> pins that it does, so
    /// a future construction site cannot quietly disable consent by omitting it.</para>
    /// </param>
    public PCellWorkerResolver(
        string rootDirectory,
        Func<PCellGeneratorManifest, string, PythonInterpreter?>? findInterpreter = null,
        Action<string>? report = null,
        Func<string, PCellTrustDecision>? trust = null)
    {
        _rootDirectory = rootDirectory;
        _findInterpreter = findInterpreter ?? DefaultInterpreter;
        _report = report;
        _trust = trust;
        Rescan();
    }

    /// <summary>
    /// What this workspace recorded last time, replayed rather than re-derived. Set before the first
    /// lookup; see <see cref="Recorded"/>'s own note for why replaying matters.
    /// </summary>
    /// <remarks>
    /// Probing candidates costs a process launch each, and the answer does not change between
    /// sessions — the same reasoning that made kit settings recorded rather than re-derived, where
    /// the measured difference was 0.5 ms against 199.8 ms.
    /// </remarks>
    public string? Recorded { get; set; }

    /// <summary>Raised once, with the interpreter actually chosen, so the caller can record it and
    /// say so. Not raised when discovery failed — there is nothing to record.</summary>
    public event Action<PythonInterpreter>? InterpreterChosen;

    /// <summary>
    /// Full discovery: the kit's own declaration, then this workspace's recorded choice, then PATH,
    /// the platform launcher and the usual install locations — each probed by RUNNING it, so a
    /// broken shim is rejected rather than launched.
    /// </summary>
    public PythonInterpreter? DefaultInterpreter(PCellGeneratorManifest manifest, string manifestDirectory)
    {
        // Relative resolves against the manifest, like every other path it carries — a kit shipping
        // its own environment names it relative to itself.
        string? declared = manifest.Interpreter is { Length: > 0 } d
            ? (Path.IsPathRooted(d) ? d : Path.GetFullPath(Path.Combine(manifestDirectory, d)))
            : null;

        var found = PythonInterpreterDiscovery.Find(declared, Recorded, out var rejected);

        if (found is null)
        {
            _report?.Invoke(PythonInterpreterDiscovery.DescribeFailure(rejected));
            return null;
        }

        // Reported ONCE, and only when it was not already the recorded choice: an automatically-made
        // decision should be visible the first time it is made, and noise on every open otherwise.
        if (!string.Equals(found.ToRecord(), Recorded, StringComparison.Ordinal))
        {
            _report?.Invoke($"Using Python {found.Version} for generated artwork " +
                            $"({found.HowFound}: {found.Command}).");
            InterpreterChosen?.Invoke(found);
        }

        return found;
    }

    /// <summary>
    /// Re-reads everything from disk: the manifests, and — the part authoring iteration depends on —
    /// the CONTENT HASHES. Ends whatever was already running first. Reading a manifest is cheap and
    /// starts nothing.
    ///
    /// <para><b>Clearing the content keys is what makes an edited script take effect.</b> They are
    /// computed once and cached (a hash per kit per session), so without this a script edited
    /// mid-session keeps its old key, resolves to the cell the previous version wrote, and the edit
    /// appears to do nothing. <see cref="StopProviders"/> deliberately does the opposite and KEEPS
    /// them — it is called when permission changes, where the scripts have not moved and a
    /// session-scoped fallback key must stay stable for the session.</para>
    /// </summary>
    public void Rescan()
    {
        StopProviders();

        lock (_gate)
        {
            _contentKeys.Clear();
            _manifests.Clear();

            foreach (string dir in CandidateDirectories())
            {
                var manifest = PCellGeneratorManifest.TryRead(dir, out var problem);
                if (problem is not null) _report?.Invoke(problem);
                if (manifest is not null) _manifests.Add((dir, manifest));
            }
        }
    }

    /// <summary>
    /// Ends every started interpreter and forgets what they offered, so the next lookup re-decides
    /// from scratch. Called when the trust decision changes — a kit that was refused a moment ago is
    /// now allowed, and the "we already worked out what is available" answer is stale.
    ///
    /// <para>Disposing is what actually ends the processes; clearing the map alone would start a
    /// SECOND interpreter per kit on the next lookup and leave the first running with nothing to talk
    /// to. The per-directory content keys are deliberately kept: a session-scoped fallback key must
    /// stay stable for the session, or the same cells would regenerate twice.</para>
    /// </summary>
    public void StopProviders()
    {
        List<PCellWorkerProvider> providers;
        lock (_gate)
        {
            providers = [.. _providers];
            _providers.Clear();
            _byGeneratorId = null;
            _contentKeyByGeneratorId.Clear();
        }

        foreach (var provider in providers)
            try { provider.Dispose(); } catch { /* teardown must not fail the caller */ }
    }

    /// <summary>Every kit declared under the root, from the manifest scan alone — nothing has been
    /// started to produce this. What the consent prompt names.</summary>
    public IReadOnlyList<PCellKit> Kits
    {
        get
        {
            lock (_gate)
                return [.. _manifests.Select(m => new PCellKit(m.Directory, m.Manifest.ResolveEntry(m.Directory)))];
        }
    }

    private IEnumerable<string> CandidateDirectories()
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory)) yield break;

        yield return _rootDirectory;
        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(_rootDirectory); }
        catch { yield break; }

        foreach (string child in children)
        {
            // A generated-cells folder is circuitRF's own cache, not a kit.
            if (string.Equals(Path.GetFileName(child), GeneratedCellStore.ReservedFolderName,
                              StringComparison.OrdinalIgnoreCase)) continue;

            // …and the archive's own kits folder is a container OF kits, not a kit — the one place
            // where the deliberate one-level shallowness would hide something. An unarchived
            // workspace puts each included kit at `kits/<kit>/`, two levels down; leaving that
            // unscanned would mean its generator is never found, the recipient is never asked for
            // permission, and every cell it draws stays a placeholder with nothing said. Still one
            // named folder rather than a general depth increase.
            if (string.Equals(Path.GetFileName(child), Archive.WorkspaceArchiveScanner.KitsFolder,
                              StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<string> kits;
                try { kits = Directory.EnumerateDirectories(child); }
                catch { continue; }
                foreach (string kit in kits) yield return kit;
                continue;
            }

            yield return child;
        }
    }

    /// <summary>
    /// Content key per manifest directory, computed once. A directory whose declared sources could
    /// not be hashed gets a PER-SESSION key instead, so its cells regenerate this session rather
    /// than being reused on the strength of a hash nobody could compute.
    /// </summary>
    private readonly Dictionary<string, string> _contentKeys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PCellValue>? DeclaredDefaults(string generatorId)
    {
        foreach (var provider in EnsureStarted().Values.Distinct())
            if (provider.DeclaredDefaults(generatorId) is { } defaults) return defaults;
        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PCellParameterInfo>? DeclaredParameters(string generatorId)
    {
        foreach (var provider in EnsureStarted().Values.Distinct())
            if (provider.DeclaredParameters(generatorId) is { } declared) return declared;
        return null;
    }

    public string? ContentKeyFor(string generatorId)
    {
        lock (_gate)
        {
            EnsureStartedLocked();
            return _contentKeyByGeneratorId.GetValueOrDefault(generatorId);
        }
    }

    private readonly Dictionary<string, string> _contentKeyByGeneratorId = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _kitNameByGeneratorId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which kit each generator came from, so a kit's cells can be listed under that kit's own name
    /// exactly as its schematic parts already are.
    ///
    /// <para>Starts every declared kit, for the same reason <see cref="KnownGeneratorIds"/> does: a
    /// script's own <c>describe</c> is the only source of its generator list.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> KitNameByGeneratorId
    {
        get
        {
            lock (_gate)
            {
                EnsureStartedLocked();
                return new Dictionary<string, string>(_kitNameByGeneratorId, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The interpreter's path for one kit: circuitRF's OWN Python package first, then whatever the
    /// manifest names.
    ///
    /// <para>circuitRF supplies its own package rather than making a kit say where it is — a manifest
    /// cannot know where circuitRF was installed, and an absolute path written into a shipped kit
    /// makes that kit machine-specific. It goes FIRST because the package and this build are
    /// versioned together (the wire version is a constant in both), so a kit shipping an older copy
    /// of its own must not shadow the one that matches. See <see cref="PCellPythonPackage"/>.</para>
    /// </summary>
    private static IReadOnlyList<string> PythonPathFor(string dir, PCellGeneratorManifest manifest)
    {
        var declared = manifest.ResolvePythonPath(dir);
        if (PCellPythonPackage.RootDirectory is not { } own) return declared;

        var path = new List<string>(declared.Count + 1) { own };
        path.AddRange(declared);
        return path;
    }

    private string ContentKeyOf(string manifestDirectory, PCellGeneratorManifest manifest)
    {
        if (_contentKeys.TryGetValue(manifestDirectory, out var cached)) return cached;

        string key = PCellGeneratorContentHash.Compute(manifestDirectory, manifest, out var problem);
        if (problem is not null)
        {
            _report?.Invoke(problem);
            // Unique to this resolver instance, so nothing generated by a previous session is
            // reused and nothing generated now is trusted next session. Regenerating is a cost;
            // reusing a cell built from source we cannot prove unchanged is a wrong answer.
            key = "session-" + Guid.NewGuid().ToString("N")[..12];
        }

        _contentKeys[manifestDirectory] = key;
        return key;
    }

    public IReadOnlyCollection<string> KnownGeneratorIds => EnsureStarted().Keys;

    public PCellGenerator? Resolve(string generatorId)
        => EnsureStarted().TryGetValue(generatorId, out var provider)
           && provider.TryGetGenerator(generatorId, out var generator)
            ? generator
            : null;

    public string Describe()
    {
        lock (_gate)
            return _manifests.Count == 0
                ? $"No PCell generator manifest ({PCellGeneratorManifest.FileName}) under '{_rootDirectory}'."
                : $"{_manifests.Count} PCell generator manifest(s) under '{_rootDirectory}': " +
                  string.Join(", ", _manifests.Select(m => m.Directory));
    }

    /// <summary>
    /// Starts every declared script and asks each what it offers.
    ///
    /// <para>A script that will not start, or will not describe itself, is REPORTED AND SKIPPED
    /// rather than allowed to fail the lookup: one broken kit must not make the others' cells
    /// unresolvable. The user still finds out, because the reason is reported.</para>
    /// </summary>
    private Dictionary<string, PCellWorkerProvider> EnsureStarted()
    {
        lock (_gate) return EnsureStartedLocked();
    }

    private Dictionary<string, PCellWorkerProvider> EnsureStartedLocked()
    {
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_byGeneratorId is not null) return _byGeneratorId;

            var map = new Dictionary<string, PCellWorkerProvider>(StringComparer.Ordinal);

            foreach (var (dir, manifest) in _manifests)
            {
                // CONSENT IS CHECKED BEFORE ANYTHING IS LAUNCHED, and this — not the prompt — is what
                // actually enforces it: a prompt that never appeared (headless, a dialog that failed,
                // a workspace switched away mid-question) leaves the decision Unknown, and Unknown
                // does not run. Degrade, never deny: the kit's cells draw as the existing Not Found
                // placeholder and every other kit, every built-in and the design itself are untouched.
                if (_trust is not null && _trust(dir) is var decision && decision != PCellTrustDecision.Allowed)
                {
                    _report?.Invoke(decision == PCellTrustDecision.Denied
                        ? $"The generated artwork in '{dir}' is not allowed to run on this machine, so its " +
                          "cells draw as placeholders. Settings ▸ General ▸ Generated Artwork asks again."
                        : $"The generated artwork in '{dir}' has not been allowed to run yet, so its cells " +
                          "draw as placeholders.");
                    continue;
                }

                string entry = manifest.ResolveEntry(dir);
                if (!File.Exists(entry))
                {
                    _report?.Invoke($"'{Path.Combine(dir, PCellGeneratorManifest.FileName)}' names an " +
                                    $"entry script that is not there: '{entry}'.");
                    continue;
                }

                var interpreter = _findInterpreter(manifest, dir);
                if (interpreter is null)
                {
                    // DEGRADE, NEVER DENY. Every cell this kit generates draws as the existing Not
                    // Found placeholder and the design still opens — the same rule a missing kit, a
                    // missing layout and a foreign document already follow. The user's design is
                    // their data; an interpreter is circuitRF's problem.
                    _report?.Invoke($"The PCell generators in '{dir}' have no interpreter to run in, " +
                                    "so their cells will draw as placeholders.");
                    continue;
                }

                PCellWorkerProvider provider;
                try
                {
                    provider = new PCellWorkerProvider(ProcessPCellWorkerTransport.Start(
                        interpreter.Command, entry, PythonPathFor(dir, manifest), interpreter.Arguments));

                    string contentKey = ContentKeyOf(dir, manifest);
                    string kitName = new PCellKit(dir, entry).Name;
                    foreach (string id in provider.GeneratorIds)
                        if (map.TryAdd(id, provider))
                        {
                            _contentKeyByGeneratorId[id] = contentKey;
                            // Which KIT a cell came from. Recorded here because this is the one place
                            // both facts are in hand at once — a kit's ids are only known once its
                            // script has described itself, and PCellKit deliberately carries no
                            // generator list so that listing kits (for the trust prompt) starts
                            // nothing.
                            _kitNameByGeneratorId[id] = kitName;
                        }
                        else
                            // Two kits offering one id: neither is obviously right, so the first wins
                            // and the collision is named. Silently preferring either would make which
                            // cell you get depend on directory order.
                            _report?.Invoke($"Two PCell generators are both called '{id}'; the one in " +
                                            $"'{map[id].Origin}' is used and '{provider.Origin}' is not.");
                }
                catch (PCellWireException ex)
                {
                    _report?.Invoke(ex.Message);
                    continue;
                }

                _providers.Add(provider);
            }

            return _byGeneratorId = map;
        }
    }

    public void Dispose()
    {
        List<PCellWorkerProvider> providers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            providers = [.. _providers];
            _providers.Clear();
            _byGeneratorId = null;
        }

        foreach (var provider in providers) provider.Dispose();
    }
}
