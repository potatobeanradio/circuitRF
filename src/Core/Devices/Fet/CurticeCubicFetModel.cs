namespace CircuitRF.Core.Devices.Fet;

/// <summary>
/// Curtice–Ettenberg cubic FET. A cubic in an effective gate voltage rather than a square, which
/// is what lets it follow real transconductance compression — the reason it is preferred over the
/// quadratic law for intermodulation work.
///
/// <code>
///   V1 = Vgs·(1 + Beta·(Vds0 − Vds))
///   Id = (A0 + A1·V1 + A2·V1² + A3·V1³)·tanh(Gamma·Vds)
/// </code>
///
/// <para><c>Vds0</c> is the drain bias at which the cubic coefficients were extracted, and
/// <c>Beta</c> here is the gate-voltage shift with drain bias — NOT the quadratic model's
/// transconductance parameter of the same name. Same spelling, different quantity, which is exactly
/// why each model in this family carries its own parameter set.</para>
/// </summary>
public sealed class CurticeCubicFetModel(
    double a0 = 0.1, double a1 = 0.05, double a2 = 0.0, double a3 = 0.0,
    double gamma = 2.0, double beta = 0.0, double vds0 = 5.0,
    double cgs = 0.0, double cgd = 0.0,
    double gateSaturationCurrent = 0.0, double gateEmissionCoefficient = 1.0,
    int capModel = 1, double vbi = 1.0, double mGrading = 0.5, double fc = 0.5,
    double tempC = FetModelBase.NominalTemperatureC,
    double tnomC = FetModelBase.NominalTemperatureC,
    double xti = 0.0, double eg = 1.16,
    double gammatc = 0.0)
    : FetModelBase(cgs, cgd, gateSaturationCurrent, gateEmissionCoefficient,
                   capModel, vbi, mGrading, fc, tempC, tnomC, xti, eg)
{
    private readonly double _gamma = ScaleLinear(gamma, gammatc, DeltaT(tempC, tnomC));

    protected override (double Id, double Gm, double Gds) DrainCurrent(double vgs, double vds)
    {
        double k   = 1.0 + beta * (vds0 - vds);
        double v1  = vgs * k;
        double p   = a0 + v1 * (a1 + v1 * (a2 + v1 * a3));            // Horner
        double dp  = a1 + v1 * (2.0 * a2 + v1 * 3.0 * a3);            // dP/dV1

        double th    = System.Math.Tanh(_gamma * vds);
        double sech2 = 1.0 - th * th;

        double id  = p * th;
        double gm  = dp * k * th;                                     // dV1/dVgs = k
        double gds = dp * (-beta * vgs) * th + p * _gamma * sech2;     // dV1/dVds = -Beta·Vgs
        return (id, gm, gds);
    }
}
