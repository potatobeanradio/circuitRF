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
    /// Linear contribution — Phase 2. The model contributes stamps;
    /// the engine owns the matrix. Not called in Phase 1.
    /// mna is MnaSystem (Phase 2); c is ElaboratedComponent.
    /// </summary>
    public virtual void Stamp(object mna, object c, double omega)
        => throw new NotImplementedException($"{GetType().Name}.Stamp is not implemented (Phase 2+)");

    /// <summary>
    /// Nonlinear contribution — Phase 3 (HB). Not called in Phase 1.
    /// </summary>
    public virtual NonlinearResult Evaluate(in PortVoltages v)
        => throw new NotSupportedException($"{GetType().Name} is not a nonlinear model");
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
