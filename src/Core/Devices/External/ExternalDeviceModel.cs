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
public sealed class ExternalDeviceModel : ComponentModel
{
    private readonly IExternalDeviceInstance _instance;
    private readonly double[]                _scratch;

    public ExternalDeviceModel(IExternalDeviceInstance instance, string providerName, string instanceLabel)
    {
        _instance    = instance;
        ProviderName = providerName;
        InstanceLabel = instanceLabel;
        _scratch     = new double[Descriptor.NodeCount];
    }

    public ExternalDeviceDescriptor Descriptor => _instance.Descriptor;
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

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = Descriptor.NodeCount;
        for (int k = 0; k < n; k++) _scratch[k] = v[k];

        ExternalDeviceEvaluation r;
        try
        {
            r = _instance.Evaluate(_scratch);
        }
        catch (ExternalDeviceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExternalDeviceException(
                $"External device '{InstanceLabel}' (provider '{ProviderName}', type " +
                $"'{Descriptor.TypeId}') failed during evaluation: {ex.Message}", ex);
        }

        if (r.Current.Length != n || r.Conductance.GetLength(0) != n || r.Conductance.GetLength(1) != n)
            throw new ExternalDeviceException(
                $"External device '{InstanceLabel}' (provider '{ProviderName}', type " +
                $"'{Descriptor.TypeId}') returned {r.Current.Length} currents and a " +
                $"{r.Conductance.GetLength(0)}×{r.Conductance.GetLength(1)} conductance matrix " +
                $"for a {n}-node device.");

        return new NonlinearResult(r.Current, r.Charge, r.Conductance, r.Capacitance);
    }
}
