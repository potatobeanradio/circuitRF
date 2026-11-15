// brief-paa-4 R-paa4-8 — the small-signal noise of a linear functional of a block's incident waves: what a Feed-mode Phased Antenna Array
// radiates in one direction is y = Σ_k v_k·a_k, a_k the wave incident on element k, so the noise it radiates there is the noise of y.
//
// ONE TRANSPOSED SOLVE PER FUNCTIONAL, on the terminated network's own factorisation, as SParameterNoise.Waves does one per port. With
// a_k = (V_k + Z0·I_k)/(2√Re Z0) over the block's pin voltage and its branch current into the block, y = rᵀ·x for the run's unknowns x,
// and the noise current u injected into the nodes gives y = rᵀ·M⁻¹·u: hᵀ = rᵀ·M⁻¹ is the transposed solve Mᵀ·h = r, which CSparse
// answers Hermitian (Mᴴ·g = conj r, h = conj g; src/Engine/RESOLVED.md). Every noisy part of SParameterNoise.Sources contributes
// h_nodesᵀ·C·conj(h_nodes); the ports are the run's own noiseless terminations, so the noise figure's denominator is the signal's.

using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CSparse.Complex.Factorization;

namespace CircuitRF.Engine;

public static partial class SParameterEngine
{
    /// <summary>
    /// brief-paa-4 — the single-sided noise power (W/Hz) of each functional <c>y = Σ_k v_k·a_k</c> of the waves incident on block
    /// <paramref name="instancePath"/>'s pins, at <paramref name="freqHz"/>, from every noisy part with every circuit port terminated in its
    /// noiseless Z0. <paramref name="z0"/> is the pins' reference impedance; <paramref name="weights"/> one vector per functional.
    /// </summary>
    public static double[] BlockFunctionalNoise(ElaboratedNetlist netlist, double freqHz, string instancePath, Complex z0,
                                                IReadOnlyList<Complex[]> weights, AnalysisSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        settings = (settings ?? AnalysisSettings.Default).WithNoise(true);
        double[] freqs = [freqHz];
        var p = Prepare(netlist, freqs, settings);
        if (!p.AllPortsResistive)
            throw new InvalidOperationException("The noise radiated by an array needs every circuit port terminated in a resistive Z0 (Re Z0 > 0); one port's Z0 has none.");
        if (p.Noise is not { } sources) return new double[weights.Count];
        var block = netlist.Components.FirstOrDefault(c => string.Equals(c.InstancePath, instancePath, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"The circuit has no instance '{instancePath}'.");
        int[] branches = block.Model switch
        {
            PhasedArrayModel pa => pa.PortBranchIndices,
            SnpModel { Inverse: false } s => s.PortBranchIndices,
            _ => throw new InvalidOperationException($"'{instancePath}' is a {block.ComponentType}, not an S-parameter block."),
        };

        double omega = 2.0 * Math.PI * freqHz;
        var noise = sources.Select(src => src.At(omega)).ToArray();   // recorded BEFORE the assembly (SParameterNoise.Sources)
        // The block's own noise is a Norton current across its pins, part of the block as a terminal sees it: the wave incident on pin k is
        // (V_k + Z0·(I_k − i_n,k))/(2√Re Z0), I_k the noiseless part's branch current. Without the i_n term a lossy block's own noise
        // (an antenna's, received from its surroundings) would be counted as incident on it — the noise it emits, read as noise it radiates.
        int own = sources.FindIndex(src => string.Equals(src.Label, instancePath, StringComparison.Ordinal));
        var pinOf = new Dictionary<int, int>();
        for (int k = 0; k < branches.Length; k++) if (block.Nodes[k] > 0) pinOf.TryAdd(block.Nodes[k], k);
        var mna = p.Mna;
        int nonGround = mna.NodeCount;
        StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: p.DcNodeVoltages);
        StampPortConductances(mna, p.Ports, p.Ports.Count);
        ApplyRegularization(mna, netlist, nonGround, settings, applyIfNecessary: false);
        SparseLU lu;
        try { lu = mna.Factorize(nodeNamer: p.NodeNamer, branchNamer: p.BranchNamer); }
        catch (SingularMatrixException) when (p.CanRetry)
        {
            StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: p.DcNodeVoltages);
            StampPortConductances(mna, p.Ports, p.Ports.Count);
            ApplyRegularization(mna, netlist, nonGround, settings, applyIfNecessary: true);
            lu = mna.Factorize(nodeNamer: p.NodeNamer, branchNamer: p.BranchNamer);
        }

        double root = 2 * Math.Sqrt(z0.Real);
        var result = new double[weights.Count];
        var r = new Complex[mna.Size];
        var g = new Complex[mna.Size];
        for (int w = 0; w < weights.Count; w++)
        {
            Array.Clear(r);
            var v = weights[w];
            for (int k = 0; k < branches.Length; k++)
            {
                if (v[k] == Complex.Zero) continue;
                // a_k = (V_k − V_ref + Z0·I_k)/(2√Re Z0); conj, for the Hermitian solve.
                Complex c = Complex.Conjugate(v[k] / root);
                if (block.Nodes[k] > 0) r[block.Nodes[k] - 1] += c;
                if (block.ReferenceNode > 0) r[block.ReferenceNode - 1] -= c;
                r[branches[k]] += c * Complex.Conjugate(z0);
            }
            lu.SolveTranspose(r, g);
            double total = 0;
            for (int src = 0; src < noise.Length; src++)
            {
                var nc = noise[src];
                if (nc.Nodes is not { Length: > 0 } nodes) continue;
                var h = new Complex[nodes.Length];
                for (int a = 0; a < nodes.Length; a++)
                {
                    h[a] = Complex.Conjugate(g[nodes[a] - 1]);
                    if (src == own && block.ReferenceNode == 0 && pinOf.TryGetValue(nodes[a], out int k)) h[a] -= v[k] * z0 / root;
                }
                // brief-paa-7 — a Phased Antenna Array's own noise is a wave source on its own nodes, not a Norton current at its pins, so the
                // terminal current it reads is the whole of it and nothing is taken out.
                if (nc.Structured is { } st)
                {
                    total += st.Form(h, h).Real;
                    continue;
                }
                Complex sum = 0;
                for (int a = 0; a < nodes.Length; a++)
                    for (int b = 0; b < nodes.Length; b++) sum += h[a] * nc.C[a, b] * Complex.Conjugate(h[b]);
                total += sum.Real;
            }
            result[w] = total;
        }
        return result;
    }

    /// <summary>
    /// brief-paa-7 R-paa7-4 — the receive weights of circuit port <paramref name="portNumber"/>: for each node in <paramref name="nodes"/>, the wave
    /// leaving that port into its noiseless Z0 per unit current injected at the node, every circuit port terminated, at <paramref name="freqHz"/>.
    /// One transposed solve on the terminated network's factorisation, as <see cref="SParameterNoise"/>'s port rows: b_P = (√Re Z0/Z0)·V_P, and
    /// the row of M⁻¹ for V_P is conj(y) with Mᴴ·y = e_P. On a Phased Antenna Array's source nodes (1 S each) these are the weights its received
    /// waves combine with at the port, b_P = Σ T_n·c_n, so they hold whether or not the network between is reciprocal.
    /// </summary>
    public static Complex[] PortReceiveWeights(ElaboratedNetlist netlist, double freqHz, int portNumber, IReadOnlyList<int> nodes, AnalysisSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(netlist);
        settings ??= AnalysisSettings.Default;
        double[] freqs = [freqHz];
        var p = Prepare(netlist, freqs, settings);
        if (!p.AllPortsResistive)
            throw new InvalidOperationException("The receive weights need every circuit port terminated in a resistive Z0 (Re Z0 > 0); one port's Z0 has none.");
        int j = p.Ports.FindIndex(x => x.PortNum == portNumber);
        if (j < 0) throw new InvalidOperationException($"The circuit has no port number {portNumber} at its top level.");
        double omega = 2.0 * Math.PI * freqHz;
        var mna = p.Mna;
        int nonGround = mna.NodeCount;
        StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: p.DcNodeVoltages);
        StampPortConductances(mna, p.Ports, p.Ports.Count);
        ApplyRegularization(mna, netlist, nonGround, settings, applyIfNecessary: false);
        SparseLU lu;
        try { lu = mna.Factorize(nodeNamer: p.NodeNamer, branchNamer: p.BranchNamer); }
        catch (SingularMatrixException) when (p.CanRetry)
        {
            StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: p.DcNodeVoltages);
            StampPortConductances(mna, p.Ports, p.Ports.Count);
            ApplyRegularization(mna, netlist, nonGround, settings, applyIfNecessary: true);
            lu = mna.Factorize(nodeNamer: p.NodeNamer, branchNamer: p.BranchNamer);
        }
        var port = p.Ports[j];
        var u = new Complex[mna.Size];
        var y = new Complex[mna.Size];
        if (port.Node0 > 0) u[port.Node0 - 1] += 1;
        if (port.Node1 > 0) u[port.Node1 - 1] -= 1;
        lu.SolveTranspose(u, y);
        Complex d = Math.Sqrt(port.Z0.Real) / port.Z0;
        var t = new Complex[nodes.Count];
        for (int k = 0; k < nodes.Count; k++) t[k] = nodes[k] > 0 ? d * Complex.Conjugate(y[nodes[k] - 1]) : Complex.Zero;
        return t;
    }
}
