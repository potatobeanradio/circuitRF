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
    /// Small-signal linear contribution of a nonlinear device, linearized at the supplied bias
    /// operating point. Stamps Y[p,q] = Dg[p,q] + jω·Dc[p,q] (from Evaluate(bias)) as an N-port
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
                var y = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
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
/// Result returned by ComponentModel.Evaluate (Phase 3+).
/// i=port currents, q=port charges, dg=di/dv, dc=dq/dv.
/// </summary>
public readonly struct NonlinearResult(double[] i, double[] q, double[,] dg, double[,] dc)
{
    public double[]  I  { get; } = i;
    public double[]  Q  { get; } = q;
    public double[,] Dg { get; } = dg;
    public double[,] Dc { get; } = dc;
}
