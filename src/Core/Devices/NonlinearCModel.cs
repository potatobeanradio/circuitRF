using System.Collections.Generic;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Core.Devices;

/// <summary>
/// 1-D polynomial nonlinear capacitor: C(V) = Σ Cₖ·Vᵏ, with charge Q(V) = ∫₀ⱽ C(u)du =
/// Σ Cₖ·V^(k+1)/(k+1). Capacitance depends only on its own terminal voltage (V = V(n+) − V(n−)).
/// One differential port (PortCount = 1, two nets). Nonlinear: contributes nothing at DC (open),
/// drives the charge balance in HB, and linearizes to jω·C(V_bias) in the linear engines
/// (via ComponentModel.StampLinearized). See docs/design/nonlinear-in-linear-engines.md §4.
/// </summary>
public sealed class NonlinearCModel : ComponentModel
{
    private readonly double[] _c;   // [C0, C1, …, Cn]; lowest power first. Never empty.

    public NonlinearCModel(double[] coefficients)
        => _c = coefficients is { Length: > 0 } ? coefficients : [0.0];

    public override int       PortCount => 1;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    /// <summary>Small-signal capacitance C(V) = Σ Cₖ·Vᵏ (Horner).</summary>
    private double CapAt(double v) => CapacitanceAt(_c, v);

    /// <summary>
    /// The same Horner, exposed for a caller (harmonicaRF's readout strip, R7D §3.3) that needs the
    /// linearized value of an arbitrary coefficient list without constructing a whole model instance.
    /// </summary>
    public static double CapacitanceAt(IReadOnlyList<double> coefficients, double v)
    {
        double acc = 0.0;
        for (int k = coefficients.Count - 1; k >= 0; k--) acc = acc * v + coefficients[k];
        return acc;
    }

    /// <summary>Charge Q(V) = Σ Cₖ·V^(k+1)/(k+1), Q(0)=0 (Horner on the integrated coefficients).</summary>
    private double ChargeAt(double v)
    {
        // Q(V) = V · Σ_{k} (Cₖ/(k+1)) · Vᵏ  → Horner over bₖ = Cₖ/(k+1), then × V.
        double acc = 0.0;
        for (int k = _c.Length - 1; k >= 0; k--) acc = acc * v + _c[k] / (k + 1);
        return acc * v;
    }

    // Pure capacitor: no DC/conduction contribution. Linear engines call StampLinearized instead.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega) { }

    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        var i = new double[1];
        var q = new double[1];
        var dg = new double[1, 1];
        var dc = new double[1, 1];
        EvaluateInto(v, i, q, dg, dc);
        return new NonlinearResult(i, q, dg, dc);
    }

    /// <inheritdoc/>
    /// <remarks>HB-P4 M4 — see <see cref="ComponentModel.EvaluateInto"/>.</remarks>
    public override bool PrefersGridEvaluate => !NonlinearEvalDiagnostics.DisableGridEvaluate;

    /// <inheritdoc/>
    protected override bool HasEvaluateInto => true;

    /// <inheritdoc/>
    protected override void EvaluateInto(in PortVoltages v, double[] i, double[] q, double[,] dg, double[,] dc)
    {
        double vd = v[0];
        i[0] = 0.0;
        q[0] = ChargeAt(vd);
        dg[0, 0] = 0.0;
        dc[0, 0] = CapAt(vd);
    }
}
