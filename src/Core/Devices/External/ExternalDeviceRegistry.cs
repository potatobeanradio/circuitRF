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
        foreach (var (name, provider) in _owned.ToArray())
        {
            _owned.TryRemove(name, out _);
            _providers.TryRemove(name, out _);

            if (provider is IDisposable d)
            {
                try { d.Dispose(); } catch { /* teardown must not throw */ }
            }
        }

        ClearResolvers();
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

    // ── lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Look up a provider, asking the resolvers if it has not been registered. A provider found
    /// this way is kept, so the cost of producing one — starting a process, reading a kit — is paid
    /// once rather than once per device.
    /// </summary>
    public static IExternalDeviceProvider? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_providers.TryGetValue(name, out var existing)) return existing;

        if (_resolving) return null;    // a resolver asked for the name it is resolving

        IExternalProviderResolver[] resolvers;
        lock (_resolverGate) resolvers = _resolvers.ToArray();
        if (resolvers.Length == 0) return null;

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

                _owned[resolved.Name] = resolved;
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

        throw new ExternalDeviceException(
            searched.Length > 0
                ? $"External device provider '{name}' is not available. Searched: " +
                  $"{string.Join("; ", searched)}. A kit supplies this by including a " +
                  $"'{DeviceWorkerManifest.FileName}' file describing how to evaluate its devices."
                : $"External device provider '{name}' is not available: no providers are registered.");
    }
}
