namespace CircuitRF.Core.Devices.External;

/// <summary>
/// One evaluation of an external device: the currents it draws at each of its nodes and the
/// derivatives of those currents with respect to every node voltage.
///
/// <para><b>Sign convention is passive: current is positive flowing INTO the device.</b> This is
/// the same convention <c>NonlinearDcEngine</c> uses when it stamps a port current
/// (<c>f[node] += i</c>), so a provider's currents are stamped with no sign flip anywhere.</para>
/// </summary>
/// <param name="Current">I[k] — current into the device at node k. Length = node count.</param>
/// <param name="Charge">Q[k] — charge at node k (the w=1 bucket). Length = node count.</param>
/// <param name="Conductance">G[k,l] = ∂I[k]/∂V[l].</param>
/// <param name="Capacitance">C[k,l] = ∂Q[k]/∂V[l].</param>
public readonly record struct ExternalDeviceEvaluation(
    double[]  Current,
    double[]  Charge,
    double[,] Conductance,
    double[,] Capacitance);

/// <summary>
/// A live instance of an external device type, created with a fixed set of parameter values.
/// Instances are created once at elaboration and evaluated many times during a solve.
/// </summary>
public interface IExternalDeviceInstance : IDisposable
{
    ExternalDeviceDescriptor Descriptor { get; }

    /// <summary>
    /// Evaluate at one node-voltage vector. <paramref name="nodeVoltages"/> has one entry per node
    /// (external pins first, then internal nodes), in descriptor order.
    /// </summary>
    ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> nodeVoltages);

    /// <summary>
    /// Evaluate a batch of node-voltage vectors in one call, returning one result per vector.
    ///
    /// <para>This is the shape that matters for performance, not a convenience wrapper: harmonic
    /// balance evaluates every device once per harmonic sample per Newton iteration, so a
    /// per-evaluation round trip to an out-of-process provider would dominate runtime. Providers
    /// that carry real transport cost must implement this as a single round trip; the default
    /// implementation below is correct but only appropriate for in-process providers.</para>
    /// </summary>
    IReadOnlyList<ExternalDeviceEvaluation> EvaluateBatch(IReadOnlyList<IReadOnlyList<double>> nodeVoltages)
    {
        var results = new ExternalDeviceEvaluation[nodeVoltages.Count];
        for (int i = 0; i < nodeVoltages.Count; i++) results[i] = Evaluate(nodeVoltages[i]);
        return results;
    }
}

/// <summary>
/// A source of external device types. An implementation may be an in-process model, or a proxy for
/// something out-of-process — circuitRF neither knows nor cares which, and learns every device type
/// it exposes at runtime through <see cref="Describe"/>.
/// </summary>
public interface IExternalDeviceProvider
{
    /// <summary>Name this provider was registered under. Opaque; rendered, never interpreted.</summary>
    string Name { get; }

    /// <summary>Every device type this provider exposes.</summary>
    IReadOnlyList<ExternalDeviceDescriptor> Describe();

    /// <summary>
    /// Create an instance of <paramref name="typeId"/> bound to <paramref name="parameters"/>.
    /// Parameter names are the ones the descriptor declared; unknown names are the provider's to
    /// reject. Throws <see cref="ExternalDeviceException"/> on any failure the user should see.
    /// </summary>
    IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters);
}

/// <summary>
/// A failure originating in an external device provider — provider unavailable, unknown type,
/// missing or unreadable model data, a rejected parameter.
///
/// <para>The message is written generically here; any provider-specific detail is interpolated
/// from strings the provider supplied at runtime, so circuitRF's own code carries no knowledge of
/// any particular provider.</para>
/// </summary>
public sealed class ExternalDeviceException(string message, Exception? inner = null)
    : Exception(message, inner);
