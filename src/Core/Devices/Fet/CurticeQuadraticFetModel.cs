namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Curtice quadratic FET. The original large-signal MESFET law and still the usual first choice:
/// four parameters, everything analytic.
///
/// <code>
///   Id = Beta·(Vgs − Vto)²·(1 + Lambda·Vds)·tanh(Alpha·Vds)      Vgs &gt; Vto
///      = 0                                                        otherwise
/// </code>
///
/// <para><c>tanh(Alpha·Vds)</c> supplies the knee, <c>Lambda</c> the output slope in saturation.</para>
/// </summary>
public sealed class CurticeQuadraticFetModel(
    double vto = -2.0, double beta = 0.02, double lambda = 0.0, double alpha = 2.0,
    double cgs = 0.0, double cgd = 0.0,
    double gateSaturationCurrent = 0.0, double gateEmissionCoefficient = 1.0,
    int capModel = 1, double vbi = 1.0, double mGrading = 0.5, double fc = 0.5,
    double tempC = FetModelBase.NominalTemperatureC,
    double tnomC = FetModelBase.NominalTemperatureC,
    double xti = 0.0, double eg = 1.16,
    double betatc = 0.0, double alphatc = 0.0, double vtotc = 0.0,
    FetModelBase.Channel channel = FetModelBase.Channel.N)
    : FetModelBase(cgs, cgd, gateSaturationCurrent, gateEmissionCoefficient,
                   capModel, vbi, mGrading, fc, tempC, tnomC, xti, eg, channel)
{
    // Temperature-scaled working parameters. Scaled ONCE here rather than per evaluation: they are
    // constant for the device's lifetime and DrainCurrent is on the Newton inner loop.
    // Negated for a p-channel device: a card states Vto in its OWN channel's convention
    // (positive for a p-channel depletion MESFET) and the law below is written in the n-channel one.
    private readonly double _vto   = (double)(int)channel * ShiftPerDegree(vto, vtotc, DeltaT(tempC, tnomC));
    private readonly double _beta  = ScalePercentPerDegree(beta, betatc, DeltaT(tempC, tnomC));
    private readonly double _alpha = ScalePercentPerDegree(alpha, alphatc, DeltaT(tempC, tnomC));

    protected override (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double vg = vgs - _vto;
        // Below pinch-off the device is off AND its derivatives are zero. Returning a small
        // conductance instead would be a fudge; the engine's own gmin already keeps the node alive.
        if (vg <= 0.0) return (0.0, 0.0, 0.0);

        double th = System.Math.Tanh(_alpha * vds);
        double sech2 = 1.0 - th * th;
        double lam = 1.0 + lambda * vds;

        double id  = _beta * vg * vg * lam * th;
        double gm  = 2.0 * _beta * vg * lam * th;
        double gds = _beta * vg * vg * (lambda * th + lam * _alpha * sech2);
        return (id, gm, gds);
    }
}
