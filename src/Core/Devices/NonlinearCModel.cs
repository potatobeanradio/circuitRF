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
    private double CapAt(double v)
    {
        double acc = 0.0;
        for (int k = _c.Length - 1; k >= 0; k--) acc = acc * v + _c[k];
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
        double vd  = v[0];
        double cap = CapAt(vd);
        return new NonlinearResult(
            i:  [0.0],
            q:  [ChargeAt(vd)],
            dg: new double[1, 1] { { 0.0 } },
            dc: new double[1, 1] { { cap } });
    }
}
