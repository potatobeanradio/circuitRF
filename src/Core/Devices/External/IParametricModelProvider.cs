using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// brief-sdl-6 D9 — what an instance simulated by EM asks for: its cell, the values it is simulated at, and where it sits.
/// </summary>
/// <param name="Cell">The cell folder the geometry comes from, as the netlist states it (<c>EmCell=</c>) — absolute, or
/// relative to <paramref name="BaseDirectory"/>.</param>
/// <param name="EmSetup">The <c>.cem</c> the instance or its analysis names (<c>EmSetup=</c>), or null for the one the
/// cell's layout has.</param>
/// <param name="Values">Every value of the cell's own scope at this point — its parameters and its variables for a placed
/// cell (Path B), the netlist's global variables for a schematic simulated by EM of its own layout (Path A). Base SI,
/// kinded as the expression engine resolved them.</param>
/// <param name="InstancePath">The instance's path (<c>X1</c>, <c>EM_C2</c>), for messages.</param>
/// <param name="Ports">How many terminals the instance binds: the N of the N-port the answer must be.</param>
/// <param name="BaseDirectory">What a relative <paramref name="Cell"/> or <paramref name="EmSetup"/> resolves against —
/// the elaboration's own base.</param>
public sealed record ParametricModelRequest(
    string Cell,
    string? EmSetup,
    IReadOnlyDictionary<string, Value> Values,
    string InstancePath,
    int Ports,
    string? BaseDirectory);

/// <summary>
/// What a provider answers: the Touchstone that IS this instance at these values, or the sentence saying why there is
/// none. <see cref="Deferred"/> is the counting pass's answer (SDL-6 D5): the evaluation was recorded and nothing was
/// built, so the elaborator places nothing for the instance.
/// </summary>
public sealed record ParametricModelAnswer(string? SnpPath, string? Failure, bool Deferred = false, string? Note = null)
{
    public static ParametricModelAnswer Snp(string path, string? note = null) => new(path, null, Note: note);
    public static ParametricModelAnswer Refused(string sentence) => new(null, sentence);
    public static ParametricModelAnswer Counted(string? note = null) => new(null, null, Deferred: true, Note: note);
}

/// <summary>
/// brief-sdl-6 D9 — the seam through which an instance is simulated by a model made at its own values: given the cell and
/// the resolved values, an <c>.sNp</c> path or a failure with a sentence. In the shape of
/// <see cref="IExternalProviderResolver"/>: Core declares it and calls it; each host registers the implementation (the EM
/// one lives in <c>src/Design</c>, which Core never references).
/// </summary>
public interface IParametricModelProvider
{
    /// <summary>What this provider serves, in words a user can act on — shown when nothing answers.</summary>
    string Describe { get; }

    /// <summary>The answer for <paramref name="request"/>, or null when this provider has no opinion about its cell
    /// (another provider may). Throwing is for a provider that should answer and could not start.</summary>
    ParametricModelAnswer? Resolve(ParametricModelRequest request);
}

/// <summary>
/// Where parametric model providers are registered. Static, as <see cref="ExternalDeviceRegistry"/> is, because every
/// elaboration — the run's, a sweep's per-point one, an optimizer's — must see the same providers without each caller
/// threading one through. The first provider with an opinion answers.
/// </summary>
public static class ParametricModelRegistry
{
    private static readonly List<IParametricModelProvider> _providers = [];
    private static readonly Lock _gate = new();

    /// <summary>The reserved instance parameter that switches an instance to its parametric model: <c>Model=EM</c>.</summary>
    public const string ModelParameter = "Model";

    /// <summary>The value of <see cref="ModelParameter"/> that asks for the EM model.</summary>
    public const string EmValue = "EM";

    /// <summary>The value of <see cref="ModelParameter"/> that asks for the circuit models (the default).</summary>
    public const string CircuitValue = "Circuit";

    /// <summary>The reserved instance parameter naming the cell folder the geometry comes from.</summary>
    public const string CellParameter = "EmCell";

    /// <summary>The reserved instance parameter naming the <c>.cem</c>.</summary>
    public const string SetupParameter = "EmSetup";

    /// <summary>Adds <paramref name="provider"/> once; a second registration of the same object is ignored.</summary>
    public static void Register(IParametricModelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
            if (!_providers.Contains(provider)) _providers.Add(provider);
    }

    public static bool Unregister(IParametricModelProvider provider)
    {
        lock (_gate) return _providers.Remove(provider);
    }

    /// <summary>Asks each provider in registration order; the first non-null answer wins.</summary>
    /// <exception cref="InvalidOperationException">No provider answers — said with what each one serves.</exception>
    public static ParametricModelAnswer Resolve(ParametricModelRequest request)
    {
        IParametricModelProvider[] providers;
        lock (_gate) providers = [.. _providers];
        foreach (var p in providers)
            if (p.Resolve(request) is { } answer) return answer;
        string where = providers.Length == 0
            ? "no EM model provider is registered in this host"
            : "no registered provider serves it (" + string.Join("; ", providers.Select(p => p.Describe)) + ")";
        throw new InvalidOperationException(
            $"'{request.InstancePath}' is to be simulated by EM of cell '{request.Cell}', but {where}.");
    }
}
