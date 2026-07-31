using System.Collections.Concurrent;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Where external device providers are registered, keyed by the name a netlist refers to them by
/// (<c>Provider=</c> on an <c>ExtDevice</c> line).
///
/// <para>Registration is the host application's job — Core never constructs a provider itself, so
/// nothing here depends on how a provider is implemented or where it runs.</para>
/// </summary>
public static class ExternalDeviceRegistry
{
    private static readonly ConcurrentDictionary<string, IExternalDeviceProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IExternalDeviceProvider provider)
        => _providers[provider.Name] = provider;

    public static bool Unregister(string name) => _providers.TryRemove(name, out _);

    public static void Clear() => _providers.Clear();

    public static IReadOnlyCollection<string> ProviderNames => _providers.Keys.ToArray();

    public static IExternalDeviceProvider? Find(string name)
        => _providers.TryGetValue(name, out var p) ? p : null;

    /// <summary>
    /// Look up a provider, failing with a message that distinguishes "no providers at all" from
    /// "this one is not among the registered ones" — the two have different fixes, and this is one
    /// of the failure modes users actually hit.
    /// </summary>
    public static IExternalDeviceProvider Require(string name)
        => Find(name) ?? throw new ExternalDeviceException(
            _providers.IsEmpty
                ? $"External device provider '{name}' is not available: no providers are registered."
                : $"External device provider '{name}' is not available. Registered: " +
                  string.Join(", ", ProviderNames.OrderBy(n => n, StringComparer.Ordinal)) + ".");
}
