using System.Numerics;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core;

/// <summary>
/// HB partition: a component is entirely linear or entirely nonlinear — never both.
/// </summary>
public enum ModelKind { Linear, Nonlinear }

/// <summary>
/// Base for every component's electrical behaviour (passive and active alike).
/// "Device" is reserved for its RF meaning (an active part); ComponentModel is the type name.
/// Stamp and Evaluate bodies live in Phase 2+; the base and shape are defined here.
/// </summary>
public abstract class ComponentModel
{
    public abstract int       PortCount { get; }
    public abstract ModelKind Kind      { get; }

    /// <summary>
    /// Terminal names for each port, used to form branch-current cube keys "instancePath:terminalName".
    /// Default: 1-based numeric strings ("1", "2", …). Override in derived types for semantic names.
    /// </summary>
    public virtual string[] TerminalNames
        => Enumerable.Range(1, PortCount).Select(i => i.ToString()).ToArray();

    /// <summary>
    /// Linear contribution — the model contributes stamps; the engine owns the matrix.
    /// Called once per frequency point during analysis assembly.
    /// </summary>
    public virtual void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        => throw new NotImplementedException($"{GetType().Name}.Stamp is not implemented");

    /// <summary>
    /// Nonlinear contribution — Phase 3 (HB). Not called in Phase 1.
    /// </summary>
    public virtual NonlinearResult Evaluate(in PortVoltages v)
        => throw new NotSupportedException($"{GetType().Name} is not a nonlinear model");

    /// <summary>
    /// Frequency-domain weighting function H[w](ω).
    /// w=0 → 1 (identity/conductance), w=1 → jω (charge/capacitance).
    /// SDD overrides this for w≥2 (user-defined H[w] expressions, brief #3).
    /// Every other device inherits the built-ins and never sees w≥2.
    /// </summary>
    public virtual Complex Weight(int w, double omega) => w switch
    {
        0 => Complex.One,
        1 => new Complex(0, omega),
        _ => throw new NotSupportedException($"{GetType().Name}: H[{w}] is not defined")
    };

    /// <summary>
    /// Small-signal linear contribution of a nonlinear device, linearized at the supplied bias
    /// operating point. Stamps Y[p,q] = Σ_w H[w](ω)·∂I[p,w]/∂V_q|_bias as an N-port
    /// admittance block, using the same port→node-pair convention as NonlinearDcEngine
    /// (port p spans Nodes[2p],Nodes[2p+1]). Linear-only engines (S-parameter, future linear-AC)
    /// call this for Kind==Nonlinear devices instead of Stamp(); HB/DC never call it.
    /// Base default suits every nonlinear device (NonlinearC, SDD); override only for special cases.
    /// </summary>
    public virtual void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
    {
        var r = Evaluate(bias);
        int P = PortCount;
        for (int p = 0; p < P; p++)
        {
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;
            for (int q = 0; q < P; q++)
            {
                int qp = c.Nodes.Length > 2 * q     ? c.Nodes[2 * q]     : 0;
                int qm = c.Nodes.Length > 2 * q + 1 ? c.Nodes[2 * q + 1] : 0;
                // Y[p,q] = Σ_w H[w](ω) · ∂I[p,w]/∂V_q; w=0,1 are the fast path.
                Complex y = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
                foreach (var term in r.Terms)
                    y += Weight(term.W, omega) * term.Jac[p, q];
                if (y == Complex.Zero) continue;
                mna.AddBlockAdmittance(np, qp,  y);
                mna.AddBlockAdmittance(np, qm, -y);
                mna.AddBlockAdmittance(nm, qp, -y);
                mna.AddBlockAdmittance(nm, qm,  y);
            }
        }
    }
}

/// <summary>Port voltage vector passed to Evaluate (Phase 3+).</summary>
public readonly struct PortVoltages(double[] voltages)
{
    public double[] Voltages { get; } = voltages;
    public double this[int i] => Voltages[i];
}

/// <summary>
/// One higher-weighting-function (w≥2) bucket returned by ComponentModel.Evaluate.
/// The port current contribution is: H[w](ω) · FT{Value[p](v(t))}.
/// </summary>
public readonly struct WeightedTerm(int w, double[] value, double[,] jac)
{
    public int       W     { get; } = w;     // weighting index ≥ 2
    public double[]  Value { get; } = value; // per-port time-domain value of I[p,w]
    public double[,] Jac   { get; } = jac;   // ∂Value[p]/∂v[q]
}

/// <summary>
/// Result returned by ComponentModel.Evaluate (Phase 3+).
/// i=port currents (w=0), q=port charges (w=1), dg=di/dv, dc=dq/dv.
/// Terms carries optional higher-w (w≥2) buckets; empty for all built-in devices.
/// </summary>
public readonly struct NonlinearResult
{
    public double[]  I     { get; }
    public double[]  Q     { get; }
    public double[,] Dg    { get; }
    public double[,] Dc    { get; }
    public IReadOnlyList<WeightedTerm> Terms { get; }

    // Existing 4-arg ctor — Terms = [] (fast path, unchanged).
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = [];
    }

    // Extended 5-arg ctor — carries higher-w buckets.
    public NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc,
        IReadOnlyList<WeightedTerm>? terms)
    {
        I = i; Q = q; Dg = dg; Dc = dc; Terms = terms ?? [];
    }
}
