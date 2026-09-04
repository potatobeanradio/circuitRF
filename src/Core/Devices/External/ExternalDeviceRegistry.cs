using System.Collections.Concurrent;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Finds a provider by name on demand, for a name that was never registered.
///
/// <para>This is what lets a device work without being set up. A netlist names the provider it
/// wants; a resolver goes and gets one. Nothing in Core knows how — a resolver supplies that, and
/// the registry only asks.</para>
/// </summary>
public interface IExternalProviderResolver
{
    /// <summary>
    /// Where this resolver looks, in words a user can act on. Appears in the error raised when a
    /// provider cannot be found, so it should name places rather than describe a strategy.
    /// </summary>
    string Describe { get; }

    /// <summary>
    /// Produce a provider for <paramref name="name"/>, or null if this resolver has none. Returning
    /// null is ordinary. Throwing is for a provider that exists but could not be started — that is
    /// a real failure with a real cause, and must not be mistaken for absence.
    /// </summary>
    IExternalDeviceProvider? Resolve(string name);
}

/// <summary>
/// Where external device providers are registered, keyed by the name a netlist refers to them by
/// (<c>Provider=</c> on an <c>ExtDevice</c> line).
///
/// <para>A host may register providers up front. It may instead add an
/// <see cref="IExternalProviderResolver"/> and let them be found the first time something asks —
/// which is what keeps a device working with no setup, and avoids starting worker processes for
/// kits a design never uses.</para>
/// </summary>
public static class ExternalDeviceRegistry
{
    private static readonly ConcurrentDictionary<string, IExternalDeviceProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Providers the registry produced itself, and is therefore responsible for ending.</summary>
    private static readonly ConcurrentDictionary<string, IExternalDeviceProvider> _owned =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which resolver produced each owned provider, so ONE workspace's resolver can be withdrawn
    /// without ending providers another workspace is still using (MW1 R-mw1-4). Recorded at the one
    /// point a provider is produced, rather than inferred later from the provider's name — two
    /// workspaces may reference kits of the same name.
    /// </summary>
    private static readonly ConcurrentDictionary<string, IExternalProviderResolver> _ownedBy =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<IExternalProviderResolver> _resolvers = [];
    private static readonly Lock _resolverGate = new();

    /// <summary>Guards against a resolver that asks the registry for the name it is resolving.</summary>
    [ThreadStatic] private static bool _resolving;

    public static void Register(IExternalDeviceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Name] = provider;
    }

    public static bool Unregister(string name)
    {
        _owned.TryRemove(name, out _);
        return _providers.TryRemove(name, out _);
    }

    /// <summary>
    /// Forgets every provider and resolver, ending any provider the registry started itself.
    ///
    /// <para>A provider registered by the host is left alone — the host owns what it created. One
    /// the registry resolved into being is ended here, because otherwise closing a workspace would
    /// leave worker processes running with nothing to talk to.</para>
    /// </summary>
    public static void Clear()
    {
        foreach (var provider in _owned.Values)
        {
            if (provider is IDisposable d)
            {
                try { d.Dispose(); } catch { /* teardown must not throw */ }
            }
        }

        _owned.Clear();
        _ownedBy.Clear();
        _providers.Clear();
        ClearResolvers();
    }

    /// <summary>
    /// Forgets every resolver and every provider the registry started itself, leaving providers the
    /// host registered untouched.
    ///
    /// <para>This is what a host calls when the places providers come from change — closing one
    /// workspace and opening another. The resolved providers belong to the workspace that is going
    /// away: their workers point at that workspace's kits, so keeping them would answer a later
    /// design with the wrong devices, and leaving them running would leak a process per kit.</para>
    /// </summary>
    public static void ResetResolved()
    {
        EndResolvedProviders();
        ClearResolvers();
    }

    /// <summary>
    /// Ends every provider the registry started itself, leaving the RESOLVERS in place so the next
    /// lookup produces a fresh one.
    ///
    /// <para>This is what a global policy change wants — "external workers are now allowed / no
    /// longer allowed" applies to the whole process, but withdrawing the resolvers along with the
    /// workers would leave every open workspace unable to resolve a device until it was reopened.
    /// With one window that was merely obscure; with two it silently disables the window the user was
    /// not looking at.</para>
    /// </summary>
    public static void EndResolvedProviders()
    {
        foreach (var (name, provider) in _owned.ToArray())
        {
            _owned.TryRemove(name, out _);
            _ownedBy.TryRemove(name, out _);
            _providers.TryRemove(name, out _);

            if (provider is IDisposable d)
            {
                try { d.Dispose(); } catch { /* teardown must not throw */ }
            }
        }
    }

    /// <summary>
    /// Withdraws ONE resolver and ends only the providers IT produced — what a workspace calls when
    /// its window closes or it reloads its own kits (MW1 R-mw1-4).
    ///
    /// <para><b>One resolver, not all of them.</b> A workspace registers exactly one and holds the
    /// instance, so the instance is the scope key and no workspace path has to be carried here. The
    /// process-wide <see cref="ResetResolved"/> this replaces on that path meant opening a second
    /// workspace ended the first one's workers and unregistered its resolver — its designs then
    /// reported "provider not available" for kits that were, in fact, still mounted.</para>
    /// </summary>
    public static void RemoveResolver(IExternalProviderResolver? resolver)
    {
        if (resolver is null) return;

        lock (_resolverGate) _resolvers.Remove(resolver);

        foreach (var (name, owner) in _ownedBy.ToArray())
        {
            if (!ReferenceEquals(owner, resolver)) continue;

            _ownedBy.TryRemove(name, out _);
            _owned.TryRemove(name, out var provider);
            _providers.TryRemove(name, out _);

            if (provider is IDisposable d)
            {
                try { d.Dispose(); } catch { /* teardown must not throw */ }
            }
        }
    }

    public static IReadOnlyCollection<string> ProviderNames => _providers.Keys.ToArray();

    /// <summary>Providers the registry started itself, for a host that wants to report on them.</summary>
    public static IReadOnlyCollection<KeyValuePair<string, IExternalDeviceProvider>> Resolved
        => _owned.ToArray();

    // ── resolvers ─────────────────────────────────────────────────────────────

    public static void AddResolver(IExternalProviderResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (_resolverGate) _resolvers.Add(resolver);
    }

    public static void ClearResolvers()
    {
        lock (_resolverGate) _resolvers.Clear();
    }

    public static IReadOnlyList<IExternalProviderResolver> Resolvers
    {
        get { lock (_resolverGate) return _resolvers.ToArray(); }
    }

    /// <summary>
    /// Resolvers circuitRF always offers, whatever a host has registered. Asked last.
    /// </summary>
    public static readonly IExternalProviderResolver[] BuiltInResolvers = [new VerilogAFileResolver()];

    // ── lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Look up a provider, asking the resolvers if it has not been registered. A provider found
    /// this way is kept, so the cost of producing one — starting a process, reading a kit — is paid
    /// once rather than once per device.
    /// </summary>
    /// <summary>
    /// Forgets a provider whose external resource has died, and ends what is left of it.
    ///
    /// <para>Removed from every map BEFORE it is disposed, so a caller racing this one cannot pick
    /// the dead provider back out of the registry while it is being torn down.</para>
    /// </summary>
    private static void DropDeadProvider(string name, IExternalDeviceProvider dead)
    {
        _providers.TryRemove(name, out _);
        _owned.TryRemove(name, out _);
        _ownedBy.TryRemove(name, out _);

        if (dead is IDisposable d)
        {
            try { d.Dispose(); } catch { /* it is already dead; teardown must not throw */ }
        }
    }

    public static IExternalDeviceProvider? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (_providers.TryGetValue(name, out var existing))
        {
            // A LIVE provider is the answer. A dead one is not: a provider backed by a worker
            // process stops working the moment that process exits, and this cache would otherwise
            // hand the same corpse out forever. The user saw that as "the connection failed (Pipe is
            // broken.) The worker process has exited." on a component they had only just placed —
            // a plumbing failure reported for something entirely recoverable, since the worker just
            // needs starting again.
            if (existing.IsUsable) return existing;

            // Only a provider the registry PRODUCED is replaced here, because only then does it know
            // which resolver to rebuild it from, and only then does it own the disposal. One the host
            // registered is the host's to manage; it is left exactly where it is and returned as
            // before, so nothing silently loses a provider it put there itself.
            if (!_owned.ContainsKey(name)) return existing;

            DropDeadProvider(name, existing);
            // and fall through to resolve a replacement
        }

        if (_resolving) return null;    // a resolver asked for the name it is resolving

        // The built-in resolver is ALWAYS in the chain and is deliberately last, so a host or a kit
        // can override anything it would answer. It survives ClearResolvers because that exists to
        // drop the resolvers belonging to a workspace being closed, and this one belongs to no
        // workspace: placing a compiled model file must work on a fresh install with no workspace,
        // no kit and nothing configured.
        IExternalProviderResolver[] resolvers;
        lock (_resolverGate) resolvers = [.. _resolvers, .. BuiltInResolvers];

        _resolving = true;
        try
        {
            foreach (var resolver in resolvers)
            {
                IExternalDeviceProvider? resolved = resolver.Resolve(name);
                if (resolved is null) continue;

                // Two designs opened at once can both resolve the same kit. Whichever lands first
                // wins and the loser is ended, so a duplicate worker process is not left running.
                var kept = _providers.GetOrAdd(resolved.Name, resolved);
                if (!ReferenceEquals(kept, resolved))
                {
                    if (resolved is IDisposable duplicate) { try { duplicate.Dispose(); } catch { } }
                    return kept;
                }

                _owned[resolved.Name]   = resolved;
                _ownedBy[resolved.Name] = resolver;
                return resolved;
            }
        }
        finally { _resolving = false; }

        return null;
    }

    /// <summary>
    /// Look up a provider, failing with a message that says what was searched.
    ///
    /// <para>The cases have different fixes and are worded differently: nothing registered and
    /// nowhere to look, nothing found in the places that were searched, or a name that does not
    /// match the providers present. A user reading this should be able to tell which situation they
    /// are in without opening a log.</para>
    /// </summary>
    public static IExternalDeviceProvider Require(string name)
    {
        if (Find(name) is { } provider) return provider;

        string[] searched = Resolvers.Select(r => r.Describe)
                                     .Where(d => !string.IsNullOrWhiteSpace(d))
                                     .ToArray();

        if (!_providers.IsEmpty)
            throw new ExternalDeviceException(
                $"External device provider '{name}' is not available. Registered: " +
                string.Join(", ", ProviderNames.OrderBy(n => n, StringComparer.Ordinal)) + "." +
                (searched.Length > 0 ? $" Also searched: {string.Join("; ", searched)}." : ""));

        // The usual cause is named, and named as the USUAL cause rather than as a diagnosis: this
        // layer knows only that nothing answered to the name. Saying "the library was not found" as
        // fact would be over-claiming — but leaving it out sends the user looking for a missing
        // provider registration when what is actually missing is a file on disk.
        throw new ExternalDeviceException(
            searched.Length > 0
                ? $"External device provider '{name}' is not available. Searched: " +
                  $"{string.Join("; ", searched)}. This is usually a compiled model, and the library " +
                  $"that implements it was not found — it often ships as a separate package beside " +
                  $"the kit rather than inside it. Add that package in File ▸ Manage PDKs (it needs " +
                  $"no parts of its own), or supply a '{DeviceWorkerManifest.FileName}' file " +
                  $"describing how to evaluate this kit's devices."
                : $"External device provider '{name}' is not available: no providers are registered.");
    }
}
