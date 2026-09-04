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
/// A model's own operating-point variables over a set of evaluation points.
///
/// <para><paramref name="Names"/> is what the provider can actually hand back — which is not the
/// same as what the descriptor declares. A string-valued op-var has nowhere to land in a
/// single-kind numeric cube, and a quantity the provider will not read out is an omission rather
/// than a zero, so the names travel with the values instead of the caller being asked to line a
/// fixed-length array up against the declaration and guess which slot went missing.</para>
///
/// <para><paramref name="Values"/> has one row per evaluation point, each row in
/// <paramref name="Names"/> order. The names are stated once because they cannot change between
/// points of one call.</para>
/// </summary>
public sealed record ExternalOperatingPoint(
    IReadOnlyList<string>   Names,
    IReadOnlyList<double[]> Values);

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

    /// <summary>
    /// The model's own operating-point variables <b>as they stand</b> — the values it computed for
    /// whichever bias it last evaluated — or null when this provider offers none.
    ///
    /// <para><b>It performs no evaluation, and that is the contract, not an implementation
    /// detail.</b> A read-back is a value the model wrote during a load; only the CALLER knows
    /// which of the many biases it has asked about is the converged one. A read that evaluated on
    /// the caller's behalf would hide that question rather than answer it, and a read positioned
    /// one call too early returns a perfectly plausible number for the previous point.</para>
    ///
    /// <para>Keyed by the model's own spelling. Names are opaque: rendered, never interpreted.</para>
    /// </summary>
    IReadOnlyDictionary<string, double>? ReadOperatingPoint() => null;

    /// <summary>
    /// Evaluate every supplied point and capture the operating-point variables <b>at each one</b>,
    /// in a single round trip — or null when this provider offers none.
    ///
    /// <para><b>This exists because <see cref="ReadOperatingPoint"/> cannot serve harmonic
    /// balance.</b> HB hands over a whole time grid in one call, and afterwards the instance holds
    /// only the last sample; recovering the rest one read at a time would be one round trip per
    /// sample, which is precisely the cost <see cref="EvaluateBatch"/> exists to avoid. So the
    /// values are captured inside the provider's own per-point loop, at the point they describe.</para>
    ///
    /// <para>Like <see cref="EvaluateBatch"/>, this leaves the instance evaluated at the LAST
    /// supplied point.</para>
    /// </summary>
    ExternalOperatingPoint? EvaluateOperatingPoint(IReadOnlyList<IReadOnlyList<double>> nodeVoltages) => null;
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
