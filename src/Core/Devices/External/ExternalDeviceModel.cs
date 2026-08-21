using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// A device whose behaviour comes from an external provider rather than from circuitRF.
///
/// <para><b>Node-referenced ports.</b> A provider reports currents per NODE and derivatives per
/// node PAIR, while <see cref="ComponentModel"/> is written in terms of ports that each span a node
/// pair (port p = Nodes[2p] − Nodes[2p+1]). The two reconcile exactly when every node is made its
/// own port referenced to ground: the elaborator lays the node array out as
/// <c>[n₀, 0, n₁, 0, …]</c>, so <c>PortVoltages[k]</c> is literally the voltage of node k, I[k] is
/// the current into it, and Dg[k,l] is ∂I[k]/∂V[l]. No translation layer, and no engine change —
/// this is the same ground-referenced convention frequency-domain N-ports already use.</para>
///
/// <para><b>Passive sign convention.</b> A provider's current is positive flowing into the device,
/// which is exactly what <c>NonlinearDcEngine</c> stamps (<c>f[node] += i</c>). Nothing is negated
/// on the way through.</para>
///
/// <para><b>Internal nodes are real unknowns.</b> They get their own rows in the global matrix,
/// allocated by the elaborator like any other minted net. They are deliberately NOT eliminated
/// locally: Schur-reducing them here would be simpler and is wrong for harmonic balance, where an
/// internal node voltage carries its own harmonic content and must be a first-class unknown.</para>
///
/// <para><b>Slaved nodes cost nothing.</b> A node the descriptor reports as following another is
/// given the master's node index by the elaborator instead of a fresh one. The engine's existing
/// four-way port stamp then does the right thing on its own: the slaved node's voltage is the
/// master's, its (identically zero) current row adds nothing, and its Jacobian COLUMN lands on the
/// master's column — which is precisely the chain rule that slaving requires. No special case here
/// or in the engine.</para>
/// </summary>
public sealed class ExternalDeviceModel : ComponentModel, IDisposable
{
    private readonly IExternalDeviceInstance _instance;
    private readonly double[]                _scratch;
    private          bool                    _disposed;

    public ExternalDeviceModel(IExternalDeviceInstance instance, string providerName, string instanceLabel)
    {
        _instance    = instance;
        ProviderName = providerName;
        InstanceLabel = instanceLabel;
        _scratch     = new double[Descriptor.NodeCount];
    }

    /// <summary>
    /// What this device's nodes are, as the provider reported them — unless elaboration has since
    /// MEASURED something the provider could not say, in which case that stands in its place.
    ///
    /// <para>The one case today is a node the model writes no equation for. A provider can measure
    /// that much on its own, but not which node such a node follows; the elaborator works that out
    /// from the model's own derivatives and records it here, so every later reader — node
    /// allocation, the engine's thermal survey, a UI pin list — sees one answer rather than the
    /// provider's incomplete one plus a correction carried alongside it.</para>
    /// </summary>
    public ExternalDeviceDescriptor Descriptor => _resolved ?? _instance.Descriptor;

    private ExternalDeviceDescriptor? _resolved;

    /// <summary>
    /// Records node roles worked out after the provider spoke. Called once, by the elaborator, and
    /// never with anything the provider itself stated — it fills gaps rather than overruling.
    /// </summary>
    internal void ResolveNodes(ExternalDeviceDescriptor resolved) => _resolved = resolved;
    public string ProviderName  { get; }
    public string InstanceLabel { get; }

    /// <summary>Nodes the elaborator must mint and append after the user-named external pins.</summary>
    public int InternalNodeCount => Descriptor.InternalNodeCount;
    public int ExternalPinCount  => Descriptor.ExternalPinCount;

    /// <summary>One port per node — see the class remarks for why this is the exact mapping.</summary>
    public override int       PortCount => Descriptor.NodeCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>
    /// Descriptor-supplied labels, so branch-current cube keys read meaningfully. Falls back to the
    /// node index when a provider supplies no label.
    /// </summary>
    public override string[] TerminalNames
        => Descriptor.Nodes.OrderBy(n => n.Index)
                     .Select(n => string.IsNullOrWhiteSpace(n.Label) ? n.Index.ToString() : n.Label)
                     .ToArray();

    /// <summary>
    /// Contributes nothing. A nonlinear device has no bias-independent linear part, and this one
    /// adds no branch unknowns — its whole contribution is the small-signal admittance block that
    /// <see cref="ComponentModel.StampLinearized"/> builds from the operating point.
    ///
    /// <para>An empty override rather than an inherited throw, because the S-parameter engine makes
    /// a preliminary pass over <b>every</b> component to count branch unknowns and label them. That
    /// pass reaches nonlinear devices too, so refusing here fails the analysis before it starts.
    /// <see cref="SddModel"/> is empty for the same reason.</para>
    /// </summary>
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = Descriptor.NodeCount;
        for (int k = 0; k < n; k++) _scratch[k] = v[k];

        ExternalDeviceEvaluation r = Guarded(() => _instance.Evaluate(_scratch));

        Check(r, n);
        return new NonlinearResult(r.Current, r.Charge, r.Conductance, r.Capacitance);
    }

    /// <summary>
    /// An external evaluation is a round trip, so this is the one model in the repository for which
    /// gathering the whole set first is worth doing — see <see cref="ComponentModel.PrefersBatchEvaluate"/>.
    /// </summary>
    public override bool PrefersBatchEvaluate => true;

    /// <summary>
    /// One round trip for the whole set. <c>IExternalDeviceInstance.EvaluateBatch</c> carries a
    /// scalar-loop default, so a provider that has nothing cheaper needs no change and still gets
    /// exactly the numbers it would have returned point by point.
    /// </summary>
    public override IReadOnlyList<NonlinearResult> EvaluateBatch(double[][] portVoltages)
    {
        int n     = Descriptor.NodeCount;
        int count = portVoltages.Length;
        if (count == 0) return [];

        IReadOnlyList<ExternalDeviceEvaluation> rs = Guarded(() => _instance.EvaluateBatch(portVoltages));

        if (rs.Count != count)
            throw new ExternalDeviceException(
                $"External device '{InstanceLabel}' (provider '{ProviderName}', type " +
                $"'{Descriptor.TypeId}') was asked for {count} evaluation points and returned {rs.Count}.");

        var results = new NonlinearResult[count];
        for (int k = 0; k < count; k++)
        {
            Check(rs[k], n);
            results[k] = new NonlinearResult(rs[k].Current, rs[k].Charge, rs[k].Conductance, rs[k].Capacitance);
        }
        return results;
    }

    /// <summary>
    /// Runs one provider call, attaching this instance's label to whatever comes back.
    ///
    /// <para>The worker can only name the TYPE, and that is not enough as soon as a design holds
    /// several devices of one type — one kit's package holds five, wired differently, two of
    /// them with gate and drain shorted and a thermal node joined to nothing else. Which instance
    /// failed is the first thing anyone asks, and this is the only layer that knows.</para>
    /// </summary>
    private T Guarded<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (ExternalDeviceException ex)
        {
            throw new ExternalDeviceException($"External device '{InstanceLabel}': {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new ExternalDeviceException(
                $"External device '{InstanceLabel}' (provider '{ProviderName}', type " +
                $"'{Descriptor.TypeId}') failed during evaluation: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gives the device back to the provider that made it.
    ///
    /// <para><b>Why this has to exist, and it is not tidiness.</b> A provider's instance lives in the
    /// WORKER's memory, not ours, and a worker is a long-lived process shared by every run in the
    /// session. Every re-elaboration builds a fresh model — and a parametric sweep re-elaborates
    /// once per point, deliberately (that is how a swept variable reaches the circuit at all). So a
    /// model that never hands its instance back leaks one per point, in another process, where no
    /// garbage collector can reach it.</para>
    ///
    /// <para>Measured: a 201 × 101 DC sweep asks for 20,502 instances of a compact
    /// model whose worker holds 4,096, and the run dies part-way through with a message about the
    /// 4,097th. The memory is the smaller half of it — a compact model's instance is not small, and
    /// thousands of them are hundreds of megabytes of somebody else's process.</para>
    ///
    /// <para><b>Failure here is swallowed on purpose.</b> This runs while a netlist is being thrown
    /// away, and a worker that has already gone is not a fault to report at that moment — it is the
    /// ordinary end of a session. Reporting it would replace a completed run's result with an error
    /// about cleaning up after it.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { (_instance as IDisposable)?.Dispose(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* see remarks */ }
    }

    private void Check(in ExternalDeviceEvaluation r, int n)
    {
        if (r.Current.Length == n && r.Conductance.GetLength(0) == n && r.Conductance.GetLength(1) == n)
            return;

        throw new ExternalDeviceException(
            $"External device '{InstanceLabel}' (provider '{ProviderName}', type " +
            $"'{Descriptor.TypeId}') returned {r.Current.Length} currents and a " +
            $"{r.Conductance.GetLength(0)}×{r.Conductance.GetLength(1)} conductance matrix " +
            $"for a {n}-node device.");
    }
}
